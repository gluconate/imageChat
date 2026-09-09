using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageChat;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static ImageChat.Tests.FluxTestFixture;

namespace ImageChat.Tests;

public class FluxSelectionTests
{
    [Fact]
    public void CatalogExposesStableFluxIdDefaultsAndExactSubFourMegapixelCapabilities()
    {
        var defaults = new FoundryOptions().Flux;
        Assert.Equal("", defaults.Endpoint);
        Assert.Equal("FLUX.2-pro", defaults.Deployment);
        Assert.Equal("preview", defaults.ApiVersion);
        Assert.Equal("ApiKey", defaults.AuthMode);
        Assert.Equal("", defaults.ApiKey);
        Assert.Equal("https://cognitiveservices.azure.com/.default", defaults.TokenScope);
        Assert.Equal(300, defaults.TimeoutSeconds);
        Assert.Equal("flux.2-pro", ModelCatalog.Flux);

        var options = Options();
        var catalog = new ModelCatalog(options);
        var flux = catalog.Resolve(ModelCatalog.Flux);
        Assert.Equal("flux.2-pro", flux.Id);
        Assert.Equal("FLUX.2-pro", flux.Label);
        Assert.NotSame(options, flux.Connection);
        Assert.Equal("bfl", flux.Connection.ApiStyle);
        Assert.Equal(options.Flux.Endpoint, flux.Connection.Endpoint);
        Assert.Equal(options.Flux.Deployment, flux.Connection.Deployment);
        Assert.Equal(options.Flux.ApiVersion, flux.Connection.ApiVersion);
        Assert.Equal(options.Flux.AuthMode, flux.Connection.AuthMode);
        Assert.Equal(options.Flux.ApiKey, flux.Connection.ApiKey);
        Assert.Equal(options.Flux.TokenScope, flux.Connection.TokenScope);
        Assert.Equal(options.Flux.TimeoutSeconds, flux.Connection.TimeoutSeconds);
        Assert.Null(flux.ConfigurationError);
        Assert.Equal(Sizes, flux.Sizes.ToArray());
        Assert.Equal(Sizes, flux.EditSizes.ToArray());
        Assert.Equal(new[] { "default" }, flux.Qualities.ToArray());
        foreach (var size in Sizes)
        {
            var dimensions = size.Split('x').Select(int.Parse).ToArray();
            Assert.InRange((long)dimensions[0] * dimensions[1], 1, 3_999_999);
            flux.Validate(size, "default", editing: false);
            flux.Validate(size, "default", editing: true);
        }
        Assert.Equal(FluxUri, flux.Connection.RequestUri(false).AbsoluteUri);
        Assert.Equal(flux.Connection.RequestUri(false), flux.Connection.RequestUri(true));
        options.Flux.ApiVersion = "fixture preview+1";
        Assert.EndsWith("?api-version=fixture%20preview%2B1", catalog.Resolve(ModelCatalog.Flux).Connection.RequestUri(true).AbsoluteUri);
        Assert.Equal(ModelCatalog.Gpt, catalog.Resolve(null).Id);
        Assert.Same(options, catalog.Resolve(null).Connection);
        Assert.Equal(ModelIds, catalog.Statuses().Select(status => status.Id).ToArray());
        Assert.All(catalog.Statuses(), status => Assert.True(status.Configured));
        // bfl is internal to the allowlisted FLUX profile, not another root GPT API style.
        options.ApiStyle = "bfl";
        Assert.NotNull(options.ConfigurationError());
        Assert.Null(catalog.Resolve(ModelCatalog.Flux).ConfigurationError);
    }

    [Theory]
    [InlineData("Endpoint", "")]
    [InlineData("Endpoint", "http://flux-fixture.test")]
    [InlineData("Endpoint", "https://user:password@flux-fixture.test")]
    [InlineData("Endpoint", "https://flux-fixture.test/api/projects/project")]
    [InlineData("Endpoint", "https://flux-fixture.test/providers/blackforestlabs/v1/flux-2-pro")]
    [InlineData("Endpoint", "https://flux-fixture.test?query=value")]
    [InlineData("Endpoint", "https://flux-fixture.test#fragment")]
    [InlineData("Deployment", " ")]
    [InlineData("ApiVersion", " ")]
    [InlineData("AuthMode", "Bearer")]
    [InlineData("ApiKey", " ")]
    public void InvalidFluxConfigurationDoesNotChangeOtherProfilesOrInheritTheirValues(string field, string value)
    {
        var options = Options();
        switch (field)
        {
            case "Endpoint": options.Flux.Endpoint = value; break;
            case "Deployment": options.Flux.Deployment = value; break;
            case "ApiVersion": options.Flux.ApiVersion = value; break;
            case "AuthMode": options.Flux.AuthMode = value; break;
            case "ApiKey": options.Flux.ApiKey = value; break;
            default: throw new InvalidOperationException("Unknown fixture field.");
        }
        var catalog = new ModelCatalog(options);
        var flux = catalog.Resolve(ModelCatalog.Flux);
        Assert.Contains("Foundry:Flux:" + field, flux.ConfigurationError!);
        Assert.False(flux.Status().Configured);
        Assert.True(catalog.Resolve(ModelCatalog.Gpt).Status().Configured);
        Assert.True(catalog.Resolve(ModelCatalog.Mai).Status().Configured);
        Assert.Equal(3, catalog.Statuses().Length);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PublicStatusHasThreeIndependentProfilesAndNeverDisclosesKeysEndpointsOrScopes(
        bool gptReady, bool maiReady, bool fluxReady)
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        var options = factory.ModelOptions;
        var secrets = new[] { options.Endpoint.TrimEnd('/'), options.ApiKey, options.TokenScope,
            options.Mai.Endpoint.TrimEnd('/'), options.Mai.ApiKey, options.Mai.TokenScope,
            options.Flux.Endpoint.TrimEnd('/'), options.Flux.ApiKey, options.Flux.TokenScope };
        if (!gptReady) options.Endpoint = "";
        if (!maiReady) options.Mai.Endpoint = "";
        if (!fluxReady) options.Flux.ApiKey = "";
        using var http = await Authenticated(factory);
        using var response = await http.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        foreach (var secret in secrets) Assert.DoesNotContain(secret, text);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        AssertFields(root, "configured", "deployment", "authMode", "message", "defaultModel", "models", "chatModel");
        Assert.Equal(ModelCatalog.Gpt, root.GetProperty("defaultModel").GetString());
        Assert.Equal(gptReady, root.GetProperty("configured").GetBoolean());
        Assert.Equal(Deployment(ModelCatalog.Gpt), root.GetProperty("deployment").GetString());
        var statuses = root.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(ModelIds, statuses.Select(status => status.GetProperty("id").GetString()).ToArray());
        foreach (var (id, ready) in new[] { (ModelCatalog.Gpt, gptReady), (ModelCatalog.Mai, maiReady), (ModelCatalog.Flux, fluxReady) })
        {
            var status = Assert.Single(statuses, status => status.GetProperty("id").GetString() == id);
            AssertFields(status, "id", "label", "configured", "deployment", "authMode", "message",
                "supportsEditing", "sizes", "editSizes", "qualities");
            Assert.Equal(ready, status.GetProperty("configured").GetBoolean());
            Assert.Equal(Deployment(id), status.GetProperty("deployment").GetString());
            Assert.Equal("ApiKey", status.GetProperty("authMode").GetString());
            Assert.True(status.GetProperty("supportsEditing").GetBoolean());
            if (id == ModelCatalog.Flux)
            {
                Assert.Equal("FLUX.2-pro", status.GetProperty("label").GetString());
                Assert.Equal(Sizes, status.GetProperty("sizes").EnumerateArray().Select(value => value.GetString()).ToArray());
                Assert.Equal(Sizes, status.GetProperty("editSizes").EnumerateArray().Select(value => value.GetString()).ToArray());
                Assert.Equal(new[] { "default" }, status.GetProperty("qualities").EnumerateArray().Select(value => value.GetString()).ToArray());
            }
        }
        Assert.Empty(factory.Generator.Inputs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public async Task FluxOnlyGenerationNormalizesOmittedQualityAndPersistsBothMessagesAndTelemetry(string? quality)
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        factory.ModelOptions.Endpoint = factory.ModelOptions.ApiKey = "";
        factory.ModelOptions.Mai.Endpoint = factory.ModelOptions.Mai.ApiKey = "";
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("A FLUX landscape", ModelCatalog.Flux, "1920x1920", quality);
        var saved = await Send(http, chat.Id, form);
        Assert.Equal(new GenerationInput("A FLUX landscape", "1920x1920", "default", null, ModelCatalog.Flux),
            Assert.Single(factory.Generator.Inputs));
        AssertTurn(saved.Messages, ModelCatalog.Flux, "1920x1920");
        Assert.Equal(CoreTests.Png, await http.GetByteArrayAsync(saved.Messages[1].ImageUrl!));
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(1, 1, 0, 0, 1, 0, 0, 0), report.Summary);
        AssertRequest(Assert.Single(report.Items), ModelCatalog.Flux, "generation");
        var reopened = new ChatStore(factory.DataDirectory);
        Assert.Equal(saved.Messages.ToArray(), reopened.Get(chat.Id).Messages.ToArray());
        Assert.Equal(CoreTests.Png, reopened.GetImage(saved.Messages[1].Id)!.Bytes);
        Assert.Equal(report.Items.ToArray(), reopened.Report(new TelemetryFilter(null, null, null)).Items.ToArray());
        using var deletion = await http.DeleteAsync($"/api/chats/{chat.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        Assert.Equal(report.Items.ToArray(), (await Report(http)).Items.ToArray());
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    public async Task ImageOnlyUploadUsesFluxDefaultPromptAndExplicitEditSize(string contentType)
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        var reference = Reference(contentType);
        using var form = Form("  ", ModelCatalog.Flux, "1536x1024");
        form.Add(new ByteArrayContent(reference.Bytes), "image", contentType == "image/png" ? "fixture.png" : "fixture.jpg");
        var saved = await Send(http, chat.Id, form);
        var input = Assert.Single(factory.Generator.Inputs);
        Assert.Equal(ModelCatalog.Flux, input.Model);
        Assert.Equal(InputRules.ImageOnlyPrompt, input.Prompt);
        Assert.Equal("1536x1024", input.Size);
        Assert.Equal("default", input.Quality);
        Assert.Equal(reference.Bytes, input.Image!.Bytes);
        Assert.Equal(contentType, input.Image.ContentType);
        AssertTurn(saved.Messages, ModelCatalog.Flux, "1536x1024");
        Assert.Equal(InputRules.ImageOnlyPrompt, saved.Messages[0].Text);
        Assert.Equal(reference.Bytes, await http.GetByteArrayAsync(saved.Messages[0].ImageUrl!));
        Assert.Equal(CoreTests.Png, await http.GetByteArrayAsync(saved.Messages[1].ImageUrl!));
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(1, 0, 1, 0, 1, 0, 0, 0), report.Summary);
        AssertRequest(Assert.Single(report.Items), ModelCatalog.Flux, "edit");
    }

    [Theory]
    [InlineData(ModelCatalog.Gpt, ModelCatalog.Flux)]
    [InlineData(ModelCatalog.Mai, ModelCatalog.Flux)]
    [InlineData(ModelCatalog.Flux, ModelCatalog.Gpt)]
    [InlineData(ModelCatalog.Flux, ModelCatalog.Mai)]
    [InlineData(ModelCatalog.Flux, ModelCatalog.Flux)]
    public async Task FollowupUsesSelectedModelAndPreviousImageWithoutRewritingEarlierProvenance(string firstModel, string nextModel)
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var first = Form("A green chair", firstModel, "1024x1024", Quality(firstModel));
        var generated = await Send(http, chat.Id, first);
        var editSize = nextModel == ModelCatalog.Mai ? "auto" : "1536x1024";
        using var followup = Form("Make it blue", nextModel, editSize, nextModel == ModelCatalog.Gpt ? "medium" : null);
        var saved = await Send(http, chat.Id, followup);
        var inputs = factory.Generator.Inputs;
        Assert.Equal(2, inputs.Length);
        Assert.Equal(firstModel, inputs[0].Model);
        Assert.Null(inputs[0].Image);
        Assert.Equal(nextModel, inputs[1].Model);
        Assert.Equal(editSize, inputs[1].Size);
        Assert.Equal(Quality(nextModel), inputs[1].Quality);
        Assert.Equal(InputRules.BuildPrompt(generated, "Make it blue"), inputs[1].Prompt);
        Assert.Equal(CoreTests.Png, inputs[1].Image!.Bytes);
        Assert.Equal("image/png", inputs[1].Image!.ContentType);
        Assert.Equal(generated.Messages.ToArray(), saved.Messages.Take(2).ToArray());
        AssertTurn(saved.Messages.Take(2), firstModel, "1024x1024");
        AssertTurn(saved.Messages.Skip(2), nextModel, editSize);
        var reopened = new ChatStore(factory.DataDirectory);
        Assert.Equal(saved.Messages.ToArray(), reopened.Get(chat.Id).Messages.ToArray());
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(2, 1, 1, 0, 2, 0, 0, 0), report.Summary);
        AssertRequest(Assert.Single(report.Items, item => item.Operation == "generation"), firstModel, "generation");
        AssertRequest(Assert.Single(report.Items, item => item.Operation == "edit"), nextModel, "edit");
        Assert.Equal(report.Items.ToArray(), reopened.Report(new TelemetryFilter(null, null, null)).Items.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfiguredFluxReturns503WithoutCallingConfiguredGptOrMai(bool missingKey)
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        if (missingKey) factory.ModelOptions.Flux.ApiKey = "";
        else factory.ModelOptions.Flux.Endpoint = "";
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("No fallback", ModelCatalog.Flux, "1024x1024");
        using var response = await http.PostAsync($"/api/chats/{chat.Id}/messages", form);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Foundry:Flux:" + (missingKey ? "ApiKey" : "Endpoint"), await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Generator.Inputs);
        Assert.Empty((await http.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages);
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(1, 0, 0, 0, 0, 1, 0, 0), report.Summary);
        var item = Assert.Single(report.Items);
        Assert.Equal(ModelCatalog.Flux, item.Model);
        Assert.Equal(FluxDeployment, item.Deployment);
        Assert.Equal(503, item.HttpStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("flux2.pro")]
    [InlineData("FLUX.2-pro")]
    [InlineData("flux.2-pro ")]
    [InlineData(FluxDeployment)]
    [InlineData("https://untrusted.test/providers/blackforestlabs/v1/flux-2-pro")]
    public async Task UnknownOrEmptyModelRejects400BeforeConfigurationAndDoesNotCreateATurn(string model)
    {
        Assert.Equal(400, Assert.Throws<AppException>(() => new ModelCatalog(Options()).Resolve(model)).StatusCode);
        await using var factory = new ApiTests.AppFactory();
        factory.ModelOptions.Endpoint = ""; // All three profiles unconfigured: still 400, never a fallback or 503.
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("Unknown selection", model, "1024x1024");
        using var response = await http.PostAsync($"/api/chats/{chat.Id}/messages", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Choose a supported image model.", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Generator.Inputs);
        Assert.Empty((await http.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages);
        var item = Assert.Single((await Report(http)).Items);
        Assert.Null(item.Model);
        Assert.Null(item.Deployment);
        Assert.Equal("rejected", item.Status);
        Assert.Equal(400, item.HttpStatus);
        Assert.Equal(0L, item.ImageCount);
    }

    [Theory]
    [InlineData("3840x2160", "default", false)]
    [InlineData("3840x2160", "default", true)]
    [InlineData("2048x2048", "default", false)]
    [InlineData("auto", "default", true)]
    [InlineData("1024x1024", "high", false)]
    [InlineData("1024x1024", "medium", true)]
    [InlineData("1024x1024", "", false)]
    public async Task StaleGptQualityOrUnsupportedSizeRejectsBeforeGeneratorAndPersistsNoTurn(string size, string quality, bool editing)
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("Rejected options", ModelCatalog.Flux, size, quality);
        if (editing) form.Add(new ByteArrayContent(CoreTests.Png), "image", "fixture.png");
        using var response = await http.PostAsync($"/api/chats/{chat.Id}/messages", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Generator.Inputs);
        Assert.Empty((await http.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages);
        var report = await Report(http);
        Assert.Equal(new TelemetrySummary(1, 0, 0, 0, 0, 0, 1, 0), report.Summary);
        var item = Assert.Single(report.Items);
        Assert.Equal(ModelCatalog.Flux, item.Model);
        Assert.Equal(FluxDeployment, item.Deployment);
        Assert.Equal(400, item.HttpStatus);
        Assert.Equal(0L, item.ImageCount);
    }

    [Theory]
    [InlineData(429, "private-marker upstream body", 429)]
    [InlineData(200, "{\"data\":[{\"url\":\"https://private-marker.test/image?sig=secret\"}]}", 502)]
    [InlineData(202, "{\"status\":\"Pending\",\"polling_url\":\"https://private-marker.test/poll\"}", 502)]
    [InlineData(200, "not-json private-marker", 502)]
    public async Task ClientFailuresStaySafeInApiAndPersistedHistoryAndNeverCountAnOutput(int upstream, string body, int expected)
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        var credential = new StubCredential();
        var handler = new StubHandler((request, _) =>
        {
            AssertFluxRequest(request, factory.ModelOptions.Flux.ApiKey);
            var response = new HttpResponseMessage((HttpStatusCode)upstream) { Content = new StringContent(body) };
            response.Headers.Add("Operation-Location", "https://private-marker.test/poll");
            return Task.FromResult(response);
        });
        using var transport = new HttpClient(handler);
        var client = new FoundryClient(transport, factory.ModelOptions, credential);
        // Exercise the real parser through the fake generator, never the production HTTP registration.
        factory.Generator.Handler = input => client.GenerateAsync(input, default);
        using var http = await Authenticated(factory);
        var chat = await CreateChat(http);
        using var form = Form("Private fixture prompt", ModelCatalog.Flux, "1024x1024");
        using var response = await http.PostAsync($"/api/chats/{chat.Id}/messages", form);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        AssertSafeError(await response.Content.ReadAsStringAsync(), factory.ModelOptions);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(credential.Scopes);
        Assert.Equal(ModelCatalog.Flux, Assert.Single(factory.Generator.Inputs).Model);
        var reopened = new ChatStore(factory.DataDirectory);
        var saved = reopened.Get(chat.Id);
        Assert.Equal(2, saved.Messages.Count);
        Assert.All(saved.Messages, message =>
        {
            Assert.Equal(ModelCatalog.Flux, message.Model);
            Assert.Equal(FluxDeployment, message.Deployment);
        });
        var failed = saved.Messages[1];
        Assert.Equal("failed", failed.Status);
        Assert.Null(failed.ImageUrl);
        Assert.Null(reopened.GetImage(failed.Id));
        AssertSafeError(JsonSerializer.Serialize(failed), factory.ModelOptions);
        var report = await Report(http);
        var item = Assert.Single(report.Items);
        Assert.Equal(ModelCatalog.Flux, item.Model);
        Assert.Equal(FluxDeployment, item.Deployment);
        Assert.Equal(expected, item.HttpStatus);
        Assert.Equal(0L, item.ImageCount);
        Assert.Equal(0L, report.Summary.Pending);
        AssertSafeError(JsonSerializer.Serialize(report), factory.ModelOptions);
    }

    [Fact]
    public async Task ThreeModelsInSeparateChatsFinishInReverseWithIndependentMessagesOutputsAndTelemetry()
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        using var http = await Authenticated(factory);
        var chats = new Dictionary<string, Chat>();
        foreach (var id in ModelIds) chats[id] = await CreateChat(http);
        var gates = ModelIds.ToDictionary(id => id, _ => new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously));
        var images = ModelIds.Select((id, index) => (id, image: new ImageData(CoreTests.Png.Concat(new[] { (byte)(50 + index) }).ToArray(), "image/png")))
            .ToDictionary(pair => pair.id, pair => pair.image);
        factory.Generator.Handler = input => gates[input.Model].Task;
        var forms = ModelIds.ToDictionary(id => id, id => Form("Only " + id, id, "1024x1024", Quality(id)));
        var requests = ModelIds.ToDictionary(id => id, id => http.PostAsync($"/api/chats/{chats[id].Id}/messages", forms[id]));
        try
        {
            foreach (var _ in ModelIds) await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.All(requests.Values, request => Assert.False(request.IsCompleted));
            Assert.Equal(3, factory.Generator.Inputs.Length);
            foreach (var id in ModelIds)
                Assert.Equal(new GenerationInput("Only " + id, "1024x1024", Quality(id), null, id),
                    Assert.Single(factory.Generator.Inputs, input => input.Model == id));
            var pendingReport = await Report(http);
            Assert.Equal(3L, pendingReport.Summary.Pending);
            foreach (var id in ModelIds)
                Assert.Equal(Deployment(id), Assert.Single(pendingReport.Items, item => item.Model == id).Deployment);
            var completed = 0;
            foreach (var id in ModelIds.Reverse())
            {
                gates[id].SetResult(images[id]);
                using var response = await requests[id].WaitAsync(Deadline);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var saved = (await response.Content.ReadFromJsonAsync<Chat>())!;
                Assert.Equal(chats[id].Id, saved.Id);
                Assert.Equal("Only " + id, saved.Messages[0].Text);
                AssertTurn(saved.Messages, id, "1024x1024");
                Assert.Equal(images[id].Bytes, await http.GetByteArrayAsync(saved.Messages[1].ImageUrl!));
                foreach (var pending in ModelIds.Where(other => !gates[other].Task.IsCompleted))
                {
                    Assert.False(requests[pending].IsCompleted);
                    Assert.Equal("pending", (await http.GetFromJsonAsync<Chat>($"/api/chats/{chats[pending].Id}"))!.Messages[1].Status);
                }
                completed++;
                Assert.Equal(new TelemetrySummary(3, completed, 0, 0, completed, 0, 0, 3 - completed), (await Report(http)).Summary);
            }
            var reopened = new ChatStore(factory.DataDirectory);
            var report = reopened.Report(new TelemetryFilter(null, null, null));
            foreach (var id in ModelIds)
            {
                AssertTurn(reopened.Get(chats[id].Id).Messages, id, "1024x1024");
                AssertRequest(Assert.Single(report.Items, item => item.Model == id), id, "generation");
            }
        }
        finally
        {
            foreach (var id in ModelIds) gates[id].TrySetResult(images[id]);
            var responses = await Task.WhenAll(requests.Values).WaitAsync(Deadline);
            foreach (var response in responses) response.Dispose();
            foreach (var form in forms.Values) form.Dispose();
        }
    }

    [Fact]
    public async Task FluxSelectionDoesNotBypassAuthenticationOwnershipOrMutationHeaderProtection()
    {
        await using var factory = new ApiTests.AppFactory();
        Configure(factory.ModelOptions);
        using var owner = await Authenticated(factory, "testuser");
        using var other = await Authenticated(factory);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        anonymous.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var chat = await CreateChat(owner);
        using var form = Form("Private FLUX image", ModelCatalog.Flux, "1024x1024");
        var saved = await Send(owner, chat.Id, form);
        foreach (var (http, expected) in new[] { (other, HttpStatusCode.NotFound), (anonymous, HttpStatusCode.Unauthorized) })
        {
            foreach (var path in new[] { $"/api/chats/{chat.Id}", saved.Messages[1].ImageUrl!, saved.Messages[1].ImageUrl + "?download=true" })
            {
                using var denied = await http.GetAsync(path);
                Assert.Equal(expected, denied.StatusCode);
            }
            using var edit = Form("Foreign edit", ModelCatalog.Flux, "1024x1024");
            using var deniedEdit = await http.PostAsync($"/api/chats/{chat.Id}/messages", edit);
            using var deniedDelete = await http.DeleteAsync($"/api/chats/{chat.Id}");
            Assert.Equal(expected, deniedEdit.StatusCode);
            Assert.Equal(expected, deniedDelete.StatusCode);
        }
        using var status = await anonymous.GetAsync("/api/status");
        using var telemetry = await owner.GetAsync("/api/admin/telemetry");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, telemetry.StatusCode);
        owner.DefaultRequestHeaders.Remove("X-ImageChat-Request");
        using var noHeader = Form("Missing header", ModelCatalog.Flux, "1024x1024");
        using var rejected = await owner.PostAsync($"/api/chats/{chat.Id}/messages", noHeader);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Single(factory.Generator.Inputs);
        Assert.Equal(saved.Messages.ToArray(), (await owner.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages.ToArray());
    }

    private static void AssertTurn(IEnumerable<ChatMessage> messages, string model, string size)
    {
        var turn = messages.ToArray();
        Assert.Equal(new[] { "user", "assistant" }, turn.Select(message => message.Role).ToArray());
        Assert.All(turn, message =>
        {
            Assert.Equal(model, message.Model);
            Assert.Equal(Deployment(model), message.Deployment);
            Assert.Equal(size, message.Size);
            Assert.Equal(Quality(model), message.Quality);
            Assert.Equal("completed", message.Status);
            Assert.Null(message.Error);
        });
        Assert.NotNull(turn[1].ImageUrl);
    }

    private static void AssertRequest(TelemetryItem item, string model, string operation)
    {
        Assert.Equal(model, item.Model);
        Assert.Equal(Deployment(model), item.Deployment);
        Assert.Equal(operation, item.Operation);
        Assert.Equal("testadmin", item.Username);
        Assert.Equal("completed", item.Status);
        Assert.Equal(200, item.HttpStatus);
        Assert.Equal(1L, item.ImageCount);
    }

    private static MultipartFormDataContent Form(string prompt, string model, string size, string? quality = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" }, { new StringContent(model), "model" },
            { new StringContent(size), "size" }, { new StringContent("true"), "usePreviousImage" }
        };
        if (quality != null) form.Add(new StringContent(quality), "quality");
        return form;
    }

    private static async Task<HttpClient> Authenticated(ApiTests.AppFactory factory, string username = "testadmin")
    {
        var http = await factory.CreateAuthenticatedClient(username);
        http.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        return http;
    }

    private static async Task<Chat> CreateChat(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync("/api/chats", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Chat>())!;
    }

    private static async Task<Chat> Send(HttpClient http, string chatId, MultipartFormDataContent form)
    {
        using var response = await http.PostAsync($"/api/chats/{chatId}/messages", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Chat>())!;
    }

    private static async Task<TelemetryReport> Report(HttpClient http) =>
        (await http.GetFromJsonAsync<TelemetryReport>("/api/admin/telemetry"))!;
}