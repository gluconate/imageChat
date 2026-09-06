using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using ImageChat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ImageChat.Tests;

public class ApiTests
{
    [Fact]
    public async Task ChatRoundtripSupportsTextImageFollowupPersistenceAndDeletion()
    {
        await using var factory = new AppFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var create = await http.PostAsJsonAsync("/api/chats", new { });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var chat = (await create.Content.ReadFromJsonAsync<Chat>())!;

        using var first = Form("A green chair");
        var firstResponse = await http.PostAsync($"/api/chats/{chat.Id}/messages", first);
        firstResponse.EnsureSuccessStatusCode();
        var generated = (await firstResponse.Content.ReadFromJsonAsync<Chat>())!;
        Assert.Equal(2, generated.Messages.Count);
        Assert.Null(factory.Generator.Inputs[0].Image);
        var imageUrl = generated.Messages[1].ImageUrl!;
        Assert.Equal(CoreTests.Png, await http.GetByteArrayAsync(imageUrl));
        var download = await http.GetAsync(imageUrl + "?download=true");
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);

        using var followup = Form("Make it blue");
        (await http.PostAsync($"/api/chats/{chat.Id}/messages", followup)).EnsureSuccessStatusCode();
        Assert.Equal(CoreTests.Png, factory.Generator.Inputs[1].Image!.Bytes);
        Assert.Contains("A green chair", factory.Generator.Inputs[1].Prompt);
        Assert.Contains("Make it blue", factory.Generator.Inputs[1].Prompt);

        using var imageOnly = Form("");
        imageOnly.Add(new ByteArrayContent(CoreTests.Png), "image", "image.png");
        (await http.PostAsync($"/api/chats/{chat.Id}/messages", imageOnly)).EnsureSuccessStatusCode();
        Assert.Contains(InputRules.ImageOnlyPrompt, factory.Generator.Inputs[2].Prompt);

        // A new repository instance sees all data; no browser memory is needed.
        var reopened = new ChatStore(factory.DataDirectory);
        Assert.Equal(6, reopened.Get(chat.Id).Messages.Count);
        Assert.Equal(CoreTests.Png, reopened.GetImage(generated.Messages[1].Id)!.Bytes);
        Assert.Single(await http.GetFromJsonAsync<List<ChatSummary>>("/api/chats") ?? []);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/chats/{chat.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(imageUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/chats/{chat.Id}")).StatusCode);
    }

    [Fact]
    public async Task FailedGenerationIsPersistedAndCanBeRetried()
    {
        await using var factory = new AppFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var chat = (await (await http.PostAsJsonAsync("/api/chats", new { })).Content.ReadFromJsonAsync<Chat>())!;
        factory.Generator.Fail = true;
        using var form = Form("A scene");
        Assert.Equal(HttpStatusCode.BadGateway, (await http.PostAsync($"/api/chats/{chat.Id}/messages", form)).StatusCode);
        var failed = (await http.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!;
        Assert.Equal("failed", failed.Messages[1].Status);
        Assert.Equal("Safe failure", failed.Messages[1].Error);
        factory.Generator.Fail = false;
        using var retry = Form("Another scene", false);
        (await http.PostAsync($"/api/chats/{chat.Id}/messages", retry)).EnsureSuccessStatusCode();
        Assert.Null(factory.Generator.Inputs[1].Image);
        Assert.DoesNotContain("A scene", factory.Generator.Inputs[1].Prompt);
    }

    [Fact]
    public async Task MissingHeaderAndInvalidUploadAreRejectedWithoutModelCalls()
    {
        await using var factory = new AppFactory();
        using var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/chats", new { })).StatusCode);
        http.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var chat = (await (await http.PostAsJsonAsync("/api/chats", new { })).Content.ReadFromJsonAsync<Chat>())!;
        using var invalid = Form("test");
        invalid.Add(new StringContent("not an image"), "image", "fake.png");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"/api/chats/{chat.Id}/messages", invalid)).StatusCode);
        Assert.Empty(factory.Generator.Inputs);
        Assert.Empty((await http.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages);
        var response = await http.GetAsync("/api/status");
        Assert.DoesNotContain("not-a-real-key", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task DifferentChatsGenerateConcurrentlyAndFinishIndependently()
    {
        await using var factory = new AppFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var a = (await (await http.PostAsJsonAsync("/api/chats", new { })).Content.ReadFromJsonAsync<Chat>())!;
        var b = (await (await http.PostAsJsonAsync("/api/chats", new { })).Content.ReadFromJsonAsync<Chat>())!;
        var completeA = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeB = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Generator.Handler = input => input.Prompt == "Scene A" ? completeA.Task : completeB.Task;
        using var formA = Form("Scene A");
        using var formB = Form("Scene B");
        var requestA = http.PostAsync($"/api/chats/{a.Id}/messages", formA);
        var requestB = http.PostAsync($"/api/chats/{b.Id}/messages", formB);
        try
        {
            // Both must enter the model before either model response is released.
            var first = await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var second = await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(first.Prompt, second.Prompt);
            Assert.False(requestA.IsCompleted);
            Assert.False(requestB.IsCompleted);
            var summaries = (await http.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!;
            Assert.All(summaries, summary => Assert.True(summary.IsGenerating));
            Assert.All(summaries, summary => Assert.Equal(2, summary.MessageCount));

            // Complete B first, while A remains pending. No cross-chat image/history contamination.
            completeB.SetResult(new(CoreTests.Png, "image/png"));
            var responseB = await requestB.WaitAsync(TimeSpan.FromSeconds(10));
            responseB.EnsureSuccessStatusCode();
            var resultB = (await responseB.Content.ReadFromJsonAsync<Chat>())!;
            Assert.Equal(b.Id, resultB.Id);
            Assert.Equal("Scene B", resultB.Messages[0].Text);
            Assert.Equal("completed", resultB.Messages[1].Status);
            Assert.Equal("pending", (await http.GetFromJsonAsync<Chat>($"/api/chats/{a.Id}"))!.Messages[1].Status);
            Assert.False(requestA.IsCompleted);
            summaries = (await http.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!;
            Assert.True(summaries.Single(s => s.Id == a.Id).IsGenerating);
            Assert.False(summaries.Single(s => s.Id == b.Id).IsGenerating);

            completeA.SetResult(new(CoreTests.Png, "image/png"));
            (await requestA.WaitAsync(TimeSpan.FromSeconds(10))).EnsureSuccessStatusCode();
            Assert.All((await http.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!, summary => Assert.False(summary.IsGenerating));
        }
        finally
        {
            completeA.TrySetResult(new(CoreTests.Png, "image/png"));
            completeB.TrySetResult(new(CoreTests.Png, "image/png"));
            await Task.WhenAll(requestA, requestB).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task BusyChatRejectsDuplicateAndDeletionButAllowsOtherChatOperations()
    {
        await using var factory = new AppFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var busy = (await (await http.PostAsJsonAsync("/api/chats", new { })).Content.ReadFromJsonAsync<Chat>())!;
        var finish = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Generator.Handler = _ => finish.Task;
        using var original = Form("Keep this turn");
        var pending = http.PostAsync($"/api/chats/{busy.Id}/messages", original);
        try
        {
            await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            using var duplicate = Form("Do not append this");
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/chats/{busy.Id}/messages", duplicate)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await http.DeleteAsync($"/api/chats/{busy.Id}")).StatusCode);
            Assert.Single(factory.Generator.Inputs);
            Assert.Equal(2, (await http.GetFromJsonAsync<Chat>($"/api/chats/{busy.Id}"))!.Messages.Count);

            var other = (await (await http.PostAsJsonAsync("/api/chats", new { })).Content.ReadFromJsonAsync<Chat>())!;
            Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/chats/{other.Id}")).StatusCode);
            Assert.False(pending.IsCompleted);
            finish.SetResult(new(CoreTests.Png, "image/png"));
            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).EnsureSuccessStatusCode();

            factory.Generator.Handler = null;
            using var followup = Form("Now continue");
            (await http.PostAsync($"/api/chats/{busy.Id}/messages", followup)).EnsureSuccessStatusCode();
            Assert.Equal(4, (await http.GetFromJsonAsync<Chat>($"/api/chats/{busy.Id}"))!.Messages.Count);
            Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/chats/{busy.Id}")).StatusCode);
        }
        finally
        {
            finish.TrySetResult(new(CoreTests.Png, "image/png"));
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledGenerationReleasesItsChatWithoutBlockingAnother(bool cancelled)
    {
        await using var factory = new AppFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var a = (await (await http.PostAsJsonAsync("/api/chats", new { })).Content.ReadFromJsonAsync<Chat>())!;
        var b = (await (await http.PostAsJsonAsync("/api/chats", new { })).Content.ReadFromJsonAsync<Chat>())!;
        var finishA = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishB = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Generator.Handler = input => input.Prompt == "A" ? finishA.Task : finishB.Task;
        using var formA = Form("A");
        using var formB = Form("B");
        var requestA = http.PostAsync($"/api/chats/{a.Id}/messages", formA);
        var requestB = http.PostAsync($"/api/chats/{b.Id}/messages", formB);
        try
        {
            await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (cancelled) finishA.SetCanceled();
            else finishA.SetException(new AppException(502, "Safe concurrent failure"));
            Assert.Equal(cancelled ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway,
                (await requestA.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
            Assert.Equal("failed", (await http.GetFromJsonAsync<Chat>($"/api/chats/{a.Id}"))!.Messages[1].Status);
            Assert.False(requestB.IsCompleted);

            factory.Generator.Handler = _ => Task.FromResult(new ImageData(CoreTests.Png, "image/png"));
            using var retry = Form("Retry A");
            (await http.PostAsync($"/api/chats/{a.Id}/messages", retry)).EnsureSuccessStatusCode();
            Assert.False(requestB.IsCompleted);
        }
        finally
        {
            finishA.TrySetResult(new(CoreTests.Png, "image/png"));
            finishB.TrySetResult(new(CoreTests.Png, "image/png"));
            await Task.WhenAll(requestA, requestB).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public static MultipartFormDataContent Form(string prompt, bool usePrevious = true)
    {
        var form = new MultipartFormDataContent();
        foreach (var pair in new Dictionary<string, string> { ["prompt"] = prompt, ["size"] = "1024x1024", ["quality"] = "medium", ["usePreviousImage"] = usePrevious ? "true" : "false" })
            form.Add(new StringContent(pair.Value), pair.Key);
        return form;
    }

    private sealed class AppFactory : WebApplicationFactory<Program>
    {
        public string DataDirectory { get; } = Path.Combine(FindRepo(), ".cache", "test-data", Guid.NewGuid().ToString("N"));
        public FakeGenerator Generator { get; } = new();
        private static string FindRepo()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "ImageChat.csproj"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(FindRepo());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Foundry:Endpoint"] = "https://example.test", ["Foundry:ApiKey"] = "not-a-real-key",
                ["Foundry:AuthMode"] = "ApiKey", ["Storage:Directory"] = DataDirectory
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<FoundryOptions>();
                services.AddSingleton(new FoundryOptions { Endpoint = "https://example.test", ApiKey = "not-a-real-key" });
                services.RemoveAll<ChatStore>();
                services.AddSingleton(_ => new ChatStore(DataDirectory));
                services.RemoveAll<IImageGenerator>();
                services.AddSingleton<IImageGenerator>(Generator);
            });
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true);
        }
    }
    private sealed class FakeGenerator : IImageGenerator
    {
        private readonly ConcurrentQueue<GenerationInput> inputs = new();
        public GenerationInput[] Inputs => inputs.ToArray();
        public Channel<GenerationInput> Started { get; } = Channel.CreateUnbounded<GenerationInput>();
        public Func<GenerationInput, Task<ImageData>>? Handler { get; set; }
        public bool Fail { get; set; }
        public Task<ImageData> GenerateAsync(GenerationInput input, CancellationToken cancellationToken)
        {
            inputs.Enqueue(input);
            var handler = Handler;
            Started.Writer.TryWrite(input);
            if (Fail) throw new AppException(502, "Safe failure");
            return handler == null ? Task.FromResult(new ImageData(CoreTests.Png, "image/png")) : handler(input);
        }
    }
}