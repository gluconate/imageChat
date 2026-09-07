using Azure;
using Azure.Identity;
using ImageChat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace ImageChat.Tests;

public sealed class CloudFactAttribute : FactAttribute
{
    public CloudFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("IMAGECHAT_AZURITE") != "1")
            Skip = "Set IMAGECHAT_AZURITE=1 and run Azurite for Azure storage integration tests.";
    }
}

public sealed class CloudStorageTests : IDisposable
{
    private readonly AzureStorage storage;
    private const string Password = "Cloud-test-password-123!";

    public CloudStorageTests()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["Storage:ResourceSuffix"] = Guid.NewGuid().ToString("N")
        }).Build();
        storage = new AzureStorage(configuration, new DefaultAzureCredential());
        storage.Initialize();
    }

    private UserStore Accounts() => new(new AzureAccountRepository(storage));

    [CloudFact]
    public async Task ApiAcceptsQueuedWorkWithoutCallingModelInWebProcess()
    {
        await using var local = new ApiTests.AppFactory();
        Accounts().Bootstrap("cloudadmin", Password);
        using var factory = local.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatStore>();
            services.AddSingleton<IChatStore>(new AzureChatStore(storage));
            services.RemoveAll<UserStore>();
            services.AddSingleton(Accounts());
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        (await client.PostAsJsonAsync("/api/auth/login", new { username = "cloudadmin", password = Password })).EnsureSuccessStatusCode();
        var created = await client.PostAsync("/api/chats", null);
        var chat = (await created.Content.ReadFromJsonAsync<Chat>())!;
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Draw an image"), "prompt" },
            { new StringContent("1024x1024"), "size" },
            { new StringContent("low"), "quality" }
        };
        var response = await client.PostAsync($"/api/chats/{chat.Id}/messages", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(local.Generator.Inputs);
        var pending = (await response.Content.ReadFromJsonAsync<Chat>())!;
        Assert.Equal("pending", pending.Messages.Last().Status);
        await new AzureChatStore(storage).ProcessOneAsync(new CountingGenerator(), default);
        var completed = (await client.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!;
        Assert.Equal("completed", completed.Messages.Last().Status);
    }

    [CloudFact]
    public void ChatOwnershipBusyStateAndTelemetrySurviveNewInstances()
    {
        var first = new AzureChatStore(storage);
        var second = new AzureChatStore(storage);
        var chat = first.Create("owner");
        var request = first.StartRequest("owner", "user", "127.0.0.1");
        var messageId = first.BeginTurn(chat, "An image", "1024x1024", "low", null, request, "generation");
        first.EndRequest(request, 202);
        Assert.True(Assert.Single(second.List("owner")).IsGenerating);
        Assert.Equal(409, Assert.Throws<AppException>(() => second.BeginTurn(chat, "Another", "1024x1024", "low", null)).StatusCode);
        Assert.Equal(409, Assert.Throws<AppException>(() => second.Delete(chat.Id, "owner")).StatusCode);
        Assert.Equal(404, Assert.Throws<AppException>(() => second.Get(chat.Id, "another-owner")).StatusCode);
        Assert.Equal(1, first.Report(new(null, null, null)).Summary.Pending);
        var image = new ImageData([137, 80, 78, 71], "image/png");
        second.FinishTurn(chat.Id, messageId, image, null);
        first.FinishTurn(chat.Id, messageId, image, null);
        Assert.Equal(image.Bytes, second.GetImage(messageId, "owner")!.Bytes);
        Assert.Equal(404, Assert.Throws<AppException>(() => first.GetImage(messageId, "another-owner")).StatusCode);
        Assert.Equal(1, first.Report(new(null, null, null)).Summary.GeneratedImages);
        first.Delete(chat.Id, "owner");
        Assert.Empty(second.List("owner"));
        Assert.Null(second.GetImage(messageId, "owner"));
        Assert.Equal(1, second.Report(new(null, null, null)).Summary.TotalImages);
    }

    [CloudFact]
    public async Task ConcurrentAccountCreationPreservesUsernameUniqueness()
    {
        Accounts().Bootstrap("administrator", Password);
        async Task<bool> Create() => await Task.Run(() =>
        {
            try { Accounts().Create("shared", Password, "user"); return true; }
            catch (AppException exception) when (exception.StatusCode == 409) { return false; }
        });
        var results = await Task.WhenAll(Create(), Create());
        Assert.Single(results, result => result);
        Assert.Equal(2, Accounts().List().Count);
        Assert.NotNull(Accounts().Authenticate("shared", Password));
    }

    [CloudFact]
    public async Task ConcurrentDeletesCannotRemoveBothAdministrators()
    {
        var first = Accounts().Bootstrap("firstadmin", Password);
        var second = Accounts().Create("secondadmin", Password, "admin");
        async Task<bool> Delete(string id) => await Task.Run(() =>
        {
            try { Accounts().Delete(id); return true; }
            catch (AppException exception) when (exception.StatusCode == 409) { return false; }
        });
        var results = await Task.WhenAll(Delete(first.Id), Delete(second.Id));
        Assert.Single(results, result => result);
        Assert.Single(Accounts().List(), user => user.Enabled && user.Role == "admin");
    }

    [CloudFact]
    public void MissingAccountControlFailsClosed()
    {
        var accounts = Accounts();
        accounts.Bootstrap("administrator", Password);
        storage.Table.DeleteEntity("accounts", "version", ETag.All);
        Assert.Equal(503, Assert.Throws<AppException>(() => accounts.HasUsers).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => Accounts()).StatusCode);
    }

    [CloudFact]
    public async Task QueuedModelSelectionSurvivesWorkerAndTelemetry()
    {
        var store = new AzureChatStore(storage);
        var chat = store.Create("owner");
        var request = store.StartRequest("owner", "user", null);
        store.SetRequestModel(request, ModelCatalog.Mai, "mai-deployment");
        var pending = store.QueueTurn(chat, "Draw", "1024x1024", "default", null, request,
            new("Draw", "1024x1024", "default", null, ModelCatalog.Mai), "mai-deployment");
        Assert.All(pending.Messages, message => Assert.Equal(ModelCatalog.Mai, message.Model));
        var generator = new CountingGenerator();
        await new AzureChatStore(storage).ProcessOneAsync(generator, default);
        Assert.Equal(ModelCatalog.Mai, generator.LastInput!.Model);
        Assert.All(store.Get(chat.Id).Messages, message => Assert.Equal("mai-deployment", message.Deployment));
        var item = Assert.Single(store.Report(new(null, null, null)).Items);
        Assert.Equal(ModelCatalog.Mai, item.Model);
        Assert.Equal("mai-deployment", item.Deployment);
        Assert.Equal("completed", item.Status);
    }

    [CloudFact]
    public async Task DuplicateQueueDeliveryDoesNotGenerateOrCountTwice()
    {
        var store = new AzureChatStore(storage);
        var chat = store.Create("owner");
        var request = store.StartRequest("owner", "user", null);
        var pending = store.QueueTurn(chat, "Draw", "1024x1024", "low", null, request, new("Draw", "1024x1024", "low", null));
        var id = pending.Messages.Last().Id;
        storage.Queue.SendMessage(id);
        var generator = new CountingGenerator();
        Assert.True(await store.ProcessOneAsync(generator, default));
        Assert.True(await store.ProcessOneAsync(generator, default));
        Assert.Equal(1, generator.Calls);
        Assert.Equal("completed", store.Get(chat.Id).Messages.Last().Status);
        Assert.Equal(1, store.Report(new(null, null, null)).Summary.TotalImages);
    }

    [CloudFact]
    public async Task DispatcherRecoversLostQueueDelivery()
    {
        var store = new AzureChatStore(storage);
        var chat = store.Create("owner");
        var pending = store.QueueTurn(chat, "Draw", "1024x1024", "low", null, null, new("Draw", "1024x1024", "low", null));
        storage.Queue.ClearMessages();
        var job = storage.Find("j" + pending.Messages.Last().Id)!;
        job.Remove("PublishedAt");
        storage.Table.UpdateEntity(job, job.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
        store.DispatchPendingJobs();
        Assert.True(await store.ProcessOneAsync(new CountingGenerator(), default));
        Assert.Equal("completed", store.Get(chat.Id).Messages.Last().Status);
    }

    [CloudFact]
    public async Task InterruptedRunningJobNeverRepeatsBillableCall()
    {
        var store = new AzureChatStore(storage);
        var pending = store.QueueTurn(store.Create("owner"), "Draw", "1024x1024", "low", null, null, new("Draw", "1024x1024", "low", null));
        var job = storage.Find("j" + pending.Messages.Last().Id)!;
        job["State"] = "running";
        job["StartedAt"] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        storage.Table.UpdateEntity(job, job.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
        var generator = new CountingGenerator();
        await store.ProcessOneAsync(generator, default);
        Assert.Equal(0, generator.Calls);
        Assert.Equal("failed", store.Get(pending.Id).Messages.Last().Status);
    }

    [CloudFact]
    public async Task StagedImageIsRecoveredAfterWorkerInterruption()
    {
        var store = new AzureChatStore(storage);
        var pending = store.QueueTurn(store.Create("owner"), "Draw", "1024x1024", "low", null, null, new("Draw", "1024x1024", "low", null));
        var id = pending.Messages.Last().Id;
        var job = storage.Find("j" + id)!;
        job["State"] = "running";
        job["StartedAt"] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        storage.Table.UpdateEntity(job, job.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
        store.UploadImage(id, new([137, 80, 78, 71], "image/png"));
        var generator = new CountingGenerator();
        await store.ProcessOneAsync(generator, default);
        Assert.Equal(0, generator.Calls);
        Assert.Equal("completed", store.Get(pending.Id).Messages.Last().Status);
    }

    [CloudFact]
    public void RateLimitIsSharedBetweenInstances()
    {
        new AzureChatStore(storage).CheckRateLimit("generation", 1);
        Assert.Equal(429, Assert.Throws<AppException>(() => new AzureChatStore(storage).CheckRateLimit("generation", 1)).StatusCode);
    }

    private sealed class CountingGenerator : IImageGenerator
    {
        public int Calls { get; private set; }
        public GenerationInput? LastInput { get; private set; }
        public Task<ImageData> GenerateAsync(GenerationInput input, CancellationToken cancellationToken)
        {
            Calls++;
            LastInput = input;
            return Task.FromResult(new ImageData([137, 80, 78, 71], "image/png"));
        }
    }

    public void Dispose()
    {
        storage.Table.Delete();
        storage.Images.DeleteIfExists();
        storage.Work.DeleteIfExists();
        storage.Keys.DeleteIfExists();
        storage.Queue.DeleteIfExists();
    }
}