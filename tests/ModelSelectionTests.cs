using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageChat;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ImageChat.Tests;

public class ModelSelectionTests
{
    private const string GptDeployment = "fixture-gpt-deployment";
    private const string MaiDeployment = "fixture-mai-deployment";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void CatalogDefaultsToGptAndAdvertisesExactModelSpecificCapabilities()
    {
        var options = new FoundryOptions();
        ConfigureBoth(options);
        var catalog = new ModelCatalog(options);
        var gpt = catalog.Resolve(null);
        Assert.Equal(ModelCatalog.Gpt, gpt.Id);
        Assert.Same(options, gpt.Connection);
        Assert.Equal(GptDeployment, gpt.Connection.Deployment);
        Assert.Equal(new[] { "1024x1024", "1536x1024", "1024x1536", "1536x1536", "3840x2160" }, gpt.Sizes.ToArray());
        Assert.Equal(gpt.Sizes.ToArray(), gpt.EditSizes.ToArray());
        Assert.Equal(new[] { "low", "medium", "high" }, gpt.Qualities.ToArray());

        var mai = catalog.Resolve(ModelCatalog.Mai);
        Assert.NotSame(options, mai.Connection);
        Assert.Equal(MaiDeployment, mai.Connection.Deployment);
        Assert.Equal("mai", mai.Connection.ApiStyle);
        Assert.Equal(new[] { "1024x1024", "768x1024", "1024x768", "768x1365", "1365x768" }, mai.Sizes.ToArray());
        Assert.Equal(new[] { "auto" }, mai.EditSizes.ToArray());
        Assert.Equal(new[] { "default" }, mai.Qualities.ToArray());
        foreach (var size in mai.Sizes)
        {
            var dimensions = size.Split('x').Select(int.Parse).ToArray();
            Assert.InRange(dimensions[0], 768, 1365);
            Assert.InRange(dimensions[1], 768, 1365);
            Assert.InRange((long)dimensions[0] * dimensions[1], 1L, 1048576L);
            mai.Validate(size, "default", editing: false);
        }
        mai.Validate("auto", "default", editing: true);
        var statuses = catalog.Statuses();
        Assert.Equal(new[] { ModelCatalog.Gpt, ModelCatalog.Mai, ModelCatalog.Flux }, statuses.Select(status => status.Id).ToArray());
        Assert.All(statuses, status =>
        {
            Assert.Equal(status.Id != ModelCatalog.Flux, status.Configured); // This fixture configures only GPT and MAI.
            Assert.True(status.SupportsEditing);
            Assert.False(string.IsNullOrWhiteSpace(status.Label));
            var selected = catalog.Resolve(status.Id);
            Assert.Equal(selected.Connection.Deployment, status.Deployment);
            Assert.Equal(selected.Sizes.ToArray(), status.Sizes.ToArray());
            Assert.Equal(selected.EditSizes.ToArray(), status.EditSizes.ToArray());
            Assert.Equal(selected.Qualities.ToArray(), status.Qualities.ToArray());
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("GPT-IMAGE-2")]
    [InlineData("mai-image-2.5-flash ")]
    [InlineData(MaiDeployment)]
    [InlineData("https://untrusted.test/mai/v1/images/generations")]
    public void CatalogRejectsAnythingExceptStableAllowlistedIds(string model)
    {
        var exception = Assert.Throws<AppException>(() => new ModelCatalog(new FoundryOptions()).Resolve(model));
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("Choose a supported image model.", exception.Message);
    }

    [Theory]
    [InlineData("3840x2160", "default", false)]
    [InlineData("1536x1536", "default", false)]
    [InlineData("512x1024", "default", false)]
    [InlineData("1024x1025", "default", false)]
    [InlineData("auto", "default", false)]
    [InlineData("1024x1024", "default", true)]
    [InlineData("1024x1024", "high", false)]
    public void CatalogRejectsUnsupportedMaiDimensionsQualityAndExplicitEditSize(string size, string quality, bool editing)
    {
        var mai = new ModelCatalog(new FoundryOptions()).Resolve(ModelCatalog.Mai);
        Assert.Equal(400, Assert.Throws<AppException>(() => mai.Validate(size, quality, editing)).StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StatusReportsIndependentReadinessAndNeverDisclosesConnectionSecrets(bool gptReady, bool maiReady)
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        var secrets = new[]
        {
            factory.ModelOptions.Endpoint, factory.ModelOptions.ApiKey, factory.ModelOptions.TokenScope,
            factory.ModelOptions.Mai.Endpoint, factory.ModelOptions.Mai.ApiKey, factory.ModelOptions.Mai.TokenScope
        };
        if (!gptReady) factory.ModelOptions.Endpoint = "";
        if (!maiReady) factory.ModelOptions.Mai.ApiKey = "";
        using var http = await Authenticated(factory);
        using var response = await http.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        foreach (var secret in secrets) Assert.DoesNotContain(secret, text);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal(ModelCatalog.Gpt, root.GetProperty("defaultModel").GetString());
        // The legacy top-level flag describes GPT, not aggregate readiness.
        Assert.Equal(gptReady, root.GetProperty("configured").GetBoolean());
        Assert.Equal(GptDeployment, root.GetProperty("deployment").GetString());
        Assert.Equal("ApiKey", root.GetProperty("authMode").GetString());
        AssertSafeStatusProperties(root);
        var models = root.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(3, models.Length);
        Assert.False(Assert.Single(models, model => model.GetProperty("id").GetString() == ModelCatalog.Flux)
            .GetProperty("configured").GetBoolean());
        foreach (var (id, ready, deployment) in new[]
                 { (ModelCatalog.Gpt, gptReady, GptDeployment), (ModelCatalog.Mai, maiReady, MaiDeployment) })
        {
            var model = Assert.Single(models, model => model.GetProperty("id").GetString() == id);
            Assert.Equal(ready, model.GetProperty("configured").GetBoolean());
            Assert.Equal(deployment, model.GetProperty("deployment").GetString());
            Assert.True(model.GetProperty("supportsEditing").GetBoolean());
            Assert.NotEmpty(model.GetProperty("sizes").EnumerateArray());
            Assert.NotEmpty(model.GetProperty("editSizes").EnumerateArray());
            Assert.NotEmpty(model.GetProperty("qualities").EnumerateArray());
            AssertSafeStatusProperties(model);
        }
        Assert.Empty(factory.Generator.Inputs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ModelCatalog.Gpt)]
    public async Task AbsentOrExplicitGptModelKeepsLegacyGenerationBehavior(string? model)
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("A green chair", model, "1024x1024", "medium");
        var result = await Send(http, chat.Id, form);
        var input = Assert.Single(factory.Generator.Inputs);
        Assert.Equal(new GenerationInput("A green chair", "1024x1024", "medium", null), input);
        AssertTurn(result.Messages, ModelCatalog.Gpt, GptDeployment, "1024x1024", "medium");
        Assert.Equal(CoreTests.Png, await http.GetByteArrayAsync(result.Messages[1].ImageUrl!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public async Task MaiOnlyConfigurationGeneratesWithOmittedOrDefaultQuality(string? quality)
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        factory.ModelOptions.Endpoint = "";
        factory.ModelOptions.ApiKey = "";
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("A MAI landscape", ModelCatalog.Mai, "1365x768", quality);
        var result = await Send(http, chat.Id, form);
        var input = Assert.Single(factory.Generator.Inputs);
        Assert.Equal(new GenerationInput("A MAI landscape", "1365x768", "default", null, ModelCatalog.Mai), input);
        AssertTurn(result.Messages, ModelCatalog.Mai, MaiDeployment, "1365x768", "default");
        var item = Assert.Single((await Report(http)).Items);
        Assert.Equal(ModelCatalog.Mai, item.Model);
        Assert.Equal(MaiDeployment, item.Deployment);
        Assert.Equal("generation", item.Operation);
        Assert.Equal(1L, item.ImageCount);
    }

    [Fact]
    public async Task UnconfiguredMaiReturns503WithoutFallingBackToConfiguredGpt()
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        factory.ModelOptions.Mai.ApiKey = "";
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("Do not route to GPT", ModelCatalog.Mai, "1024x1024", null);
        using var response = await http.PostAsync($"/api/chats/{chat.Id}/messages", form);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Foundry:Mai:ApiKey", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Generator.Inputs);
        Assert.Empty((await http.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages);
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(1, 0, 0, 0, 0, 1, 0, 0), report.Summary);
        var item = Assert.Single(report.Items);
        Assert.Equal(ModelCatalog.Mai, item.Model);
        Assert.Equal(MaiDeployment, item.Deployment);
        Assert.Equal(503, item.HttpStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(MaiDeployment)]
    [InlineData("https://untrusted.test/images")]
    public async Task InvalidModelReturns400BeforeConfigurationOrGeneratorAndCreatesNoTurn(string model)
    {
        await using var factory = new ApiTests.AppFactory();
        // Both connections are unavailable: resolution must still return 400, not 503.
        factory.ModelOptions.Endpoint = "";
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("Invalid selection", model, "1024x1024", "medium");
        using var response = await http.PostAsync($"/api/chats/{chat.Id}/messages", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Generator.Inputs);
        Assert.Empty((await http.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages);
        var item = Assert.Single((await Report(http)).Items);
        Assert.Null(item.Model);
        Assert.Null(item.Deployment);
        Assert.Equal("rejected", item.Status);
        Assert.Equal(0L, item.ImageCount);
    }

    [Fact]
    public async Task GptToMaiFollowupReusesPreviousImageAndHistoryWithAutoEditSize()
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var first = Form("A green chair", ModelCatalog.Gpt, "1024x1024", "high");
        var generated = await Send(http, chat.Id, first);
        using var followup = Form("Make it blue", ModelCatalog.Mai, "auto", null);
        var result = await Send(http, chat.Id, followup);
        var inputs = factory.Generator.Inputs;
        Assert.Equal(2, inputs.Length);
        Assert.Equal(ModelCatalog.Gpt, inputs[0].Model);
        Assert.Null(inputs[0].Image);
        Assert.Equal(ModelCatalog.Mai, inputs[1].Model);
        Assert.Equal("auto", inputs[1].Size);
        Assert.Equal("default", inputs[1].Quality);
        Assert.Equal(CoreTests.Png, inputs[1].Image!.Bytes);
        Assert.Equal("image/png", inputs[1].Image!.ContentType);
        Assert.Equal(InputRules.BuildPrompt(generated, "Make it blue"), inputs[1].Prompt);
        AssertTurn(result.Messages.Take(2), ModelCatalog.Gpt, GptDeployment, "1024x1024", "high");
        AssertTurn(result.Messages.Skip(2), ModelCatalog.Mai, MaiDeployment, "auto", "default");
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(2, 1, 1, 0, 2, 0, 0, 0), report.Summary);
        Assert.Equal("generation", Assert.Single(report.Items, item => item.Model == ModelCatalog.Gpt).Operation);
        Assert.Equal("edit", Assert.Single(report.Items, item => item.Model == ModelCatalog.Mai).Operation);
    }

    [Fact]
    public async Task MaiImageOnlyUploadUsesDefaultPromptAndAutoEditWithoutQualityField()
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("  ", ModelCatalog.Mai, "auto", null);
        form.Add(new ByteArrayContent(CoreTests.Png), "image", "fixture.png");
        var result = await Send(http, chat.Id, form);
        var input = Assert.Single(factory.Generator.Inputs);
        Assert.Equal(ModelCatalog.Mai, input.Model);
        Assert.Equal(InputRules.ImageOnlyPrompt, input.Prompt);
        Assert.Equal("auto", input.Size);
        Assert.Equal("default", input.Quality);
        Assert.Equal(CoreTests.Png, input.Image!.Bytes);
        AssertTurn(result.Messages, ModelCatalog.Mai, MaiDeployment, "auto", "default");
        Assert.Equal(InputRules.ImageOnlyPrompt, result.Messages[0].Text);
        foreach (var message in result.Messages)
            Assert.Equal(CoreTests.Png, await http.GetByteArrayAsync(message.ImageUrl!));
        Assert.Equal(new TelemetrySummary(1, 0, 1, 0, 1, 0, 0, 0), (await Report(http)).Summary);
    }

    [Theory]
    [InlineData(ModelCatalog.Gpt, ModelCatalog.Mai, "1024x1024", "high", false)]
    [InlineData(ModelCatalog.Gpt, ModelCatalog.Mai, "1024x1024", "", false)]
    [InlineData(ModelCatalog.Gpt, ModelCatalog.Mai, "1536x1024", "default", false)]
    [InlineData(ModelCatalog.Gpt, ModelCatalog.Mai, "3840x2160", "default", false)]
    [InlineData(ModelCatalog.Gpt, ModelCatalog.Mai, "1024x1024", "default", true)]
    [InlineData(ModelCatalog.Gpt, ModelCatalog.Mai, "auto", "default", false)]
    [InlineData(ModelCatalog.Mai, ModelCatalog.Gpt, "768x1024", "medium", false)]
    [InlineData(ModelCatalog.Mai, ModelCatalog.Gpt, "1024x1024", "default", false)]
    public async Task SwitchingModelsRejectsStaleSizeOrQualityWithoutAnotherProviderCall(
        string firstModel, string nextModel, string size, string quality, bool usePrevious)
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var first = Form("Original image", firstModel, "1024x1024", firstModel == ModelCatalog.Gpt ? "medium" : "default");
        var original = await Send(http, chat.Id, first);
        using var invalid = Form("Do not generate", nextModel, size, quality, usePrevious);
        using var response = await http.PostAsync($"/api/chats/{chat.Id}/messages", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(factory.Generator.Inputs);
        var stored = (await http.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!;
        Assert.Equal(original.Messages.ToArray(), stored.Messages.ToArray());
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(2, 1, 0, 0, 1, 0, 1, 0), report.Summary);
        var rejected = Assert.Single(report.Items, item => item.Status == "rejected");
        Assert.Equal(nextModel, rejected.Model);
        Assert.Equal(nextModel == ModelCatalog.Gpt ? GptDeployment : MaiDeployment, rejected.Deployment);
        Assert.Equal(400, rejected.HttpStatus);
        Assert.Equal(0L, rejected.ImageCount);
    }

    [Fact]
    public async Task BothTurnRowsAndAdminTelemetryRetainModelDeploymentAfterReopenAndChatDeletion()
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var gpt = Form("GPT output", ModelCatalog.Gpt, "1024x1024", "medium");
        await Send(http, chat.Id, gpt);
        using var mai = Form("MAI output", ModelCatalog.Mai, "768x1024", "default", usePrevious: false);
        var saved = await Send(http, chat.Id, mai);
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(2, 2, 0, 0, 2, 0, 0, 0), report.Summary);
        foreach (var (model, deployment) in new[] { (ModelCatalog.Gpt, GptDeployment), (ModelCatalog.Mai, MaiDeployment) })
        {
            var item = Assert.Single(report.Items, item => item.Model == model);
            Assert.Equal(deployment, item.Deployment);
            Assert.Equal("testadmin", item.Username);
            Assert.Equal("completed", item.Status);
            Assert.Equal(200, item.HttpStatus);
            Assert.Equal(1L, item.ImageCount);
        }
        var reopened = new ChatStore(factory.DataDirectory);
        var stored = reopened.Get(chat.Id);
        Assert.Equal(saved.Messages.ToArray(), stored.Messages.ToArray());
        AssertTurn(stored.Messages.Take(2), ModelCatalog.Gpt, GptDeployment, "1024x1024", "medium");
        AssertTurn(stored.Messages.Skip(2), ModelCatalog.Mai, MaiDeployment, "768x1024", "default");
        foreach (var output in stored.Messages.Where(message => message.Role == "assistant"))
            Assert.Equal(CoreTests.Png, reopened.GetImage(output.Id)!.Bytes);
        var reopenedReport = reopened.Report(new TelemetryFilter(null, null, null));
        Assert.Equal(report.Summary, reopenedReport.Summary);
        Assert.Equal(report.Items.ToArray(), reopenedReport.Items.ToArray());
        using var deletion = await http.DeleteAsync($"/api/chats/{chat.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        Assert.Equal(report.Items.ToArray(), (await Report(http)).Items.ToArray());
        Assert.Equal(report.Summary, new ChatStore(factory.DataDirectory).Report(new TelemetryFilter(null, null, null)).Summary);
    }

    [Fact]
    public async Task DifferentChatsRunGptAndMaiTogetherAndFinishInReverseWithoutContamination()
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var gptChat = await CreateChat(http);
        var maiChat = await CreateChat(http);
        var completeGpt = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeMai = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maiBytes = CoreTests.Png.Concat(new byte[] { 42 }).ToArray();
        factory.Generator.Handler = input => input.Model == ModelCatalog.Gpt ? completeGpt.Task : completeMai.Task;
        using var gptForm = Form("Only GPT context", ModelCatalog.Gpt, "1536x1024", "high");
        using var maiForm = Form("Only MAI context", ModelCatalog.Mai, "1024x768", null);
        var gptRequest = http.PostAsync($"/api/chats/{gptChat.Id}/messages", gptForm);
        var maiRequest = http.PostAsync($"/api/chats/{maiChat.Id}/messages", maiForm);
        try
        {
            await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.False(gptRequest.IsCompleted);
            Assert.False(maiRequest.IsCompleted);
            var inputs = factory.Generator.Inputs;
            Assert.Equal(2, inputs.Length);
            Assert.Equal(new GenerationInput("Only GPT context", "1536x1024", "high", null, ModelCatalog.Gpt),
                Assert.Single(inputs, input => input.Model == ModelCatalog.Gpt));
            Assert.Equal(new GenerationInput("Only MAI context", "1024x768", "default", null, ModelCatalog.Mai),
                Assert.Single(inputs, input => input.Model == ModelCatalog.Mai));
            var pending = await Report(http);
            Assert.Equal(2L, pending.Summary.Pending);
            Assert.Equal(GptDeployment, Assert.Single(pending.Items, item => item.Model == ModelCatalog.Gpt).Deployment);
            Assert.Equal(MaiDeployment, Assert.Single(pending.Items, item => item.Model == ModelCatalog.Mai).Deployment);

            completeMai.SetResult(new ImageData(maiBytes, "image/png"));
            using var maiResponse = await maiRequest.WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.OK, maiResponse.StatusCode);
            var maiResult = (await maiResponse.Content.ReadFromJsonAsync<Chat>())!;
            Assert.Equal(maiChat.Id, maiResult.Id);
            AssertTurn(maiResult.Messages, ModelCatalog.Mai, MaiDeployment, "1024x768", "default");
            Assert.Equal(maiBytes, await http.GetByteArrayAsync(maiResult.Messages[1].ImageUrl!));
            Assert.False(gptRequest.IsCompleted);
            Assert.Equal("pending", (await http.GetFromJsonAsync<Chat>($"/api/chats/{gptChat.Id}"))!.Messages[1].Status);
            Assert.Equal(new TelemetrySummary(2, 1, 0, 0, 1, 0, 0, 1), (await Report(http)).Summary);

            completeGpt.SetResult(new ImageData(CoreTests.Png, "image/png"));
            using var gptResponse = await gptRequest.WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.OK, gptResponse.StatusCode);
            var gptResult = (await gptResponse.Content.ReadFromJsonAsync<Chat>())!;
            Assert.Equal(gptChat.Id, gptResult.Id);
            AssertTurn(gptResult.Messages, ModelCatalog.Gpt, GptDeployment, "1536x1024", "high");
            Assert.Equal(CoreTests.Png, await http.GetByteArrayAsync(gptResult.Messages[1].ImageUrl!));
            Assert.Equal("Only GPT context", gptResult.Messages[0].Text);
            Assert.Equal("Only MAI context", maiResult.Messages[0].Text);
            Assert.Equal(new TelemetrySummary(2, 2, 0, 0, 2, 0, 0, 0), (await Report(http)).Summary);
        }
        finally
        {
            completeGpt.TrySetResult(new ImageData(CoreTests.Png, "image/png"));
            completeMai.TrySetResult(new ImageData(maiBytes, "image/png"));
            var responses = await Task.WhenAll(gptRequest, maiRequest).WaitAsync(Deadline);
            foreach (var response in responses) response.Dispose();
        }
    }

    [Theory]
    [InlineData(ModelCatalog.Gpt)]
    [InlineData(ModelCatalog.Mai)]
    public async Task ModelSelectionPreservesAuthenticationOwnershipAndMutationHeaderProtections(string model)
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        using var owner = await Authenticated(factory, "testuser");
        using var other = await Authenticated(factory);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        anonymous.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var chat = await CreateChat(owner);
        using var form = Form("Private image", model, "1024x1024", model == ModelCatalog.Gpt ? "medium" : "default");
        var saved = await Send(owner, chat.Id, form);
        var imageUrl = saved.Messages[1].ImageUrl!;
        foreach (var (client, denied) in new[] { (other, HttpStatusCode.NotFound), (anonymous, HttpStatusCode.Unauthorized) })
        {
            foreach (var path in new[] { $"/api/chats/{chat.Id}", imageUrl, imageUrl + "?download=true" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(denied, response.StatusCode);
            }
            using var forbiddenForm = Form("Foreign edit", model, model == ModelCatalog.Mai ? "auto" : "1024x1024",
                model == ModelCatalog.Gpt ? "medium" : "default");
            using var post = await client.PostAsync($"/api/chats/{chat.Id}/messages", forbiddenForm);
            using var delete = await client.DeleteAsync($"/api/chats/{chat.Id}");
            Assert.Equal(denied, post.StatusCode);
            Assert.Equal(denied, delete.StatusCode);
        }
        Assert.Empty((await other.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!);
        using var anonymousStatus = await anonymous.GetAsync("/api/status");
        using var forbiddenTelemetry = await owner.GetAsync("/api/admin/telemetry");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousStatus.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenTelemetry.StatusCode);
        owner.DefaultRequestHeaders.Remove("X-ImageChat-Request");
        using var noHeader = Form("No mutation header", model, "1024x1024", "default");
        using var missingHeader = await owner.PostAsync($"/api/chats/{chat.Id}/messages", noHeader);
        Assert.Equal(HttpStatusCode.Forbidden, missingHeader.StatusCode);
        Assert.Single(factory.Generator.Inputs);
        Assert.Equal(saved.Messages.ToArray(), (await owner.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages.ToArray());
        Assert.Equal(CoreTests.Png, await owner.GetByteArrayAsync(imageUrl));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdditiveMigrationPreservesOldImagesTotalsAndNullAttribution(bool hadTelemetry)
    {
        await using var factory = new ApiTests.AppFactory();
        ConfigureBoth(factory.ModelOptions);
        Directory.CreateDirectory(factory.DataDirectory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(factory.DataDirectory, "imagechat.db"), Pooling = false
        }.ToString();
        // This database is entirely synthetic and owned/cleaned up by AppFactory.
        using (var db = new SqliteConnection(connectionString))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE chats(id TEXT PRIMARY KEY,title TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
                CREATE TABLE messages(id TEXT PRIMARY KEY,chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                    role TEXT NOT NULL,text TEXT NOT NULL,created_at TEXT NOT NULL,size TEXT NOT NULL,quality TEXT NOT NULL,
                    status TEXT NOT NULL,error TEXT,image BLOB,content_type TEXT);
                INSERT INTO chats VALUES('old-chat','Old fixture',$time,$time);
                INSERT INTO messages VALUES('old-input','old-chat','user','Old fixture',$time,'1024x1024','medium','completed',NULL,$png,'image/png');
                INSERT INTO messages VALUES('old-output','old-chat','assistant','Old output',$time,'1024x1024','medium','completed',NULL,$png,'image/png');
                """;
            command.Parameters.AddWithValue("$time", "2025-01-02T03:04:05.0000000+00:00");
            command.Parameters.Add("$png", SqliteType.Blob).Value = CoreTests.Png;
            command.ExecuteNonQuery();
            if (hadTelemetry)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    CREATE TABLE image_requests(id TEXT PRIMARY KEY,message_id TEXT UNIQUE,user_id TEXT,username TEXT,ip TEXT,
                        requested_ms INTEGER NOT NULL,completed_ms INTEGER,operation TEXT NOT NULL,status TEXT NOT NULL,
                        image_count INTEGER NOT NULL DEFAULT 0,http_status INTEGER);
                    INSERT INTO image_requests VALUES('old-request','old-output',NULL,NULL,'203.0.113.7',1735787045000,
                        1735787045000,'generation','completed',1,200);
                    CREATE TABLE app_migrations(name TEXT PRIMARY KEY);
                    INSERT INTO app_migrations VALUES('telemetry-v1');
                    """;
                command.ExecuteNonQuery();
            }
        }
        var migrated = new ChatStore(factory.DataDirectory);
        var oldChat = migrated.Get("old-chat");
        Assert.Equal("Old fixture", oldChat.Title);
        Assert.Equal(2, oldChat.Messages.Count);
        Assert.Equal(new[] { "Old fixture", "Old output" }, oldChat.Messages.Select(message => message.Text).ToArray());
        Assert.All(oldChat.Messages, message =>
        {
            Assert.Null(message.Model);
            Assert.Null(message.Deployment);
            Assert.Equal("completed", message.Status);
            Assert.Equal("1024x1024", message.Size);
            Assert.Equal("medium", message.Quality);
            Assert.Equal(CoreTests.Png, migrated.GetImage(message.Id)!.Bytes);
        });
        var baseline = migrated.Report(new TelemetryFilter(null, null, null));
        Assert.Equal(new TelemetrySummary(1, hadTelemetry ? 1 : 0, 0, hadTelemetry ? 0 : 1, 1, 0, 0, 0), baseline.Summary);
        var historical = Assert.Single(baseline.Items);
        Assert.Null(historical.Model);
        Assert.Null(historical.Deployment);
        Assert.Equal(hadTelemetry ? "completed" : "legacy", historical.Status);
        Assert.Equal(hadTelemetry ? "generation" : "unknown", historical.Operation);
        Assert.Equal(hadTelemetry ? "203.0.113.7" : null, historical.Ip);
        using var http = await Authenticated(factory);
        Assert.Equal(baseline.Items.ToArray(), (await Report(http)).Items.ToArray());
        var viaApi = (await http.GetFromJsonAsync<Chat>("/api/chats/old-chat"))!;
        Assert.Equal(oldChat.Messages.ToArray(), viaApi.Messages.ToArray());
        using var followup = Form("New MAI edit", ModelCatalog.Mai, "auto", null);
        var updated = await Send(http, "old-chat", followup);
        Assert.Equal(oldChat.Messages.ToArray(), updated.Messages.Take(2).ToArray());
        AssertTurn(updated.Messages.Skip(2), ModelCatalog.Mai, MaiDeployment, "auto", "default");
        Assert.Equal(CoreTests.Png, Assert.Single(factory.Generator.Inputs).Image!.Bytes);
        var reopened = new ChatStore(factory.DataDirectory);
        var report = reopened.Report(new TelemetryFilter(null, null, null));
        Assert.Equal(new TelemetrySummary(2, hadTelemetry ? 1 : 0, 1, hadTelemetry ? 0 : 1, 2, 0, 0, 0), report.Summary);
        Assert.Equal(historical, Assert.Single(report.Items, item => item.Id == historical.Id));
        Assert.Equal(updated.Messages.ToArray(), reopened.Get("old-chat").Messages.ToArray());
        foreach (var table in new[] { "messages", "image_requests" })
        {
            using var db = new SqliteConnection(connectionString);
            db.Open();
            using var columns = db.CreateCommand();
            columns.CommandText = $"PRAGMA table_info({table})";
            using var reader = columns.ExecuteReader();
            var added = new List<string>();
            while (reader.Read())
                if (reader.GetString(1) is "model" or "deployment")
                {
                    added.Add(reader.GetString(1));
                    Assert.Equal("TEXT", reader.GetString(2));
                    Assert.Equal(0L, reader.GetInt64(3)); // Nullable; no fabricated default/backfill.
                    Assert.True(reader.IsDBNull(4));
                }
            Assert.Equal(new[] { "model", "deployment" }, added.ToArray());
        }
    }

    private static void ConfigureBoth(FoundryOptions options)
    {
        options.Endpoint = "https://gpt-connection-secret.test";
        options.Deployment = GptDeployment;
        options.ApiKey = "gpt-only-fixture-key";
        options.TokenScope = "https://gpt-scope-secret.test/.default";
        options.Mai = new MaiOptions
        {
            Endpoint = "https://mai-connection-secret.test", Deployment = MaiDeployment,
            ApiKey = "mai-only-fixture-key", TokenScope = "https://mai-scope-secret.test/.default"
        };
    }

    private static void AssertSafeStatusProperties(JsonElement value)
    {
        foreach (var forbidden in new[] { "endpoint", "apiKey", "tokenScope", "connection", "connectionString", "mai" })
            Assert.DoesNotContain(value.EnumerateObject(), property =>
                string.Equals(property.Name, forbidden, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertTurn(IEnumerable<ChatMessage> messages, string model, string deployment, string size, string quality)
    {
        var turn = messages.ToArray();
        Assert.Equal(new[] { "user", "assistant" }, turn.Select(message => message.Role).ToArray());
        Assert.All(turn, message =>
        {
            Assert.Equal(model, message.Model);
            Assert.Equal(deployment, message.Deployment);
            Assert.Equal(size, message.Size);
            Assert.Equal(quality, message.Quality);
            Assert.Equal("completed", message.Status);
            Assert.Null(message.Error);
        });
        Assert.NotNull(turn[1].ImageUrl);
    }

    private static MultipartFormDataContent Form(string prompt, string? model, string size, string? quality, bool usePrevious = true)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent(size), "size" },
            { new StringContent(usePrevious ? "true" : "false"), "usePreviousImage" }
        };
        if (model != null) form.Add(new StringContent(model), "model");
        if (quality != null) form.Add(new StringContent(quality), "quality");
        return form;
    }

    private static async Task<HttpClient> Authenticated(ApiTests.AppFactory factory, string username = "testadmin")
    {
        var client = await factory.CreateAuthenticatedClient(username);
        client.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        return client;
    }

    private static async Task<Chat> CreateChat(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/chats", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Chat>())!;
    }

    private static async Task<Chat> Send(HttpClient client, string chatId, MultipartFormDataContent form)
    {
        using var response = await client.PostAsync($"/api/chats/{chatId}/messages", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Chat>())!;
    }

    private static async Task<TelemetryReport> Report(HttpClient client) =>
        (await client.GetFromJsonAsync<TelemetryReport>("/api/admin/telemetry"))!;
}