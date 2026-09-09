using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using ImageChat;
using Xunit;

namespace ImageChat.Tests;

public sealed class ResponsesChatClientTests
{
    private const string PrivateMarker = "private-fixture-secret";
    private const int ProviderLimit = 2 * 1024 * 1024;
    private const int EventLimit = 256 * 1024;
    private static readonly TextChatInput Input = new([new("user", "A fixture prompt")]);
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void DefaultsMatchThePublicContract()
    {
        var options = new ChatModelOptions();
        Assert.Equal("gpt-5.6-sol", ChatModelOptions.ModelId);
        Assert.Equal(ChatModelOptions.ModelId, options.Deployment);
        Assert.Equal("https://embedding-0483-resource.services.ai.azure.com/openai/v1/responses", options.Endpoint);
        Assert.Equal(options.Endpoint, options.RequestUri().AbsoluteUri);
        Assert.Equal("ApiKey", options.AuthMode);
        Assert.Equal("", options.ApiKey);
        Assert.Equal("https://cognitiveservices.azure.com/.default", options.TokenScope);
        Assert.Equal(180, options.TimeoutSeconds);
        Assert.Equal(2048, options.MaxOutputTokens);
        Assert.True(options.Streaming);
        Assert.Equal(64000, options.MaxPromptCharacters);
        Assert.Equal(256000, options.MaxContextCharacters);
        Assert.Equal(200, options.MaxContextMessages);
        Assert.Equal(8, options.MaxContextImages);
        Assert.Equal(32 * 1024 * 1024, options.MaxContextImageBytes);
        Assert.True(options.SupportsImages);
        Assert.Null(new ChatInputMessage("user", "Compatible positional constructor").Images);
        Assert.Equal(12000, ResponsesChatClient.MaxResponseCharacters);
        Assert.NotNull(options.ConfigurationError());
    }

    [Theory]
    [InlineData("https://provider.test")]
    [InlineData("https://provider.test/")]
    [InlineData("https://provider.test/openai/v1/responses")]
    [InlineData("HTTPS://provider.test/openai/v1/responses")]
    public void OnlyHttpsRootOrExactResponsesPathIsAccepted(string endpoint)
    {
        var options = Options();
        options.Endpoint = endpoint;
        Assert.Null(options.ConfigurationError());
        Assert.Equal("https://provider.test/openai/v1/responses", options.RequestUri().AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("provider.test")]
    [InlineData("http://provider.test")]
    [InlineData("https://private-fixture-secret@provider.test/")]
    [InlineData("https://@provider.test/")]
    [InlineData("https://provider.test/?private-fixture-secret")]
    [InlineData("https://provider.test/#private-fixture-secret")]
    [InlineData("https://provider.test/?")]
    [InlineData("https://provider.test/#")]
    [InlineData("https://provider.test/openai/v1")]
    [InlineData("https://provider.test/openai/v1/responses/")]
    [InlineData("https://provider.test/OpenAI/v1/responses")]
    [InlineData("https://provider.test/anything")]
    [InlineData("https://provider.test/x/../openai/v1/responses")]
    [InlineData("https://provider.test/%6fpenai/v1/responses")]
    [InlineData("https://provider.test//openai/v1/responses")]
    [InlineData("https://provider.test\\openai\\v1\\responses")]
    [InlineData(" https://provider.test/")]
    [InlineData("https://provider.test/\r\n")]
    public void InvalidEndpointsFailWithoutEchoingConfiguration(string endpoint)
    {
        var options = Options();
        options.Endpoint = endpoint;
        Assert.NotNull(options.ConfigurationError());
        Assert.DoesNotContain(PrivateMarker, options.ConfigurationError()!);
        var error = Assert.Throws<AppException>(() => options.RequestUri());
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
    }

    [Theory]
    [InlineData("ApiKey", "")]
    [InlineData("ApiKey", "private-fixture-secret\r\n")]
    [InlineData("AuthMode", "private-fixture-secret")]
    [InlineData("Deployment", " ")]
    [InlineData("Deployment", "private-fixture-secret\n")]
    [InlineData("TokenScope", " ")]
    [InlineData("TimeoutSeconds", "29")]
    [InlineData("TimeoutSeconds", "211")]
    [InlineData("MaxOutputTokens", "0")]
    [InlineData("MaxOutputTokens", "-1")]
    [InlineData("MaxPromptCharacters", "0")]
    [InlineData("MaxPromptCharacters", "256001")]
    [InlineData("MaxContextCharacters", "63999")]
    [InlineData("MaxContextCharacters", "1000001")]
    [InlineData("MaxContextMessages", "1")]
    [InlineData("MaxContextMessages", "1001")]
    [InlineData("MaxContextImages", "0")]
    [InlineData("MaxContextImages", "17")]
    [InlineData("MaxContextImageBytes", "20971519")]
    [InlineData("MaxContextImageBytes", "67108865")]
    public async Task InvalidConfigurationDoesNotSendOrRequestCredentials(string property, string value)
    {
        var options = Options();
        var member = typeof(ChatModelOptions).GetProperty(property)!;
        member.SetValue(options, member.PropertyType == typeof(int) ? int.Parse(value) : value);
        if (property == "TokenScope") options.AuthMode = "DefaultAzureCredential";
        var handler = Handler();
        var credential = new StubCredential();
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, options, credential)));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, credential.Calls);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(210)]
    public void TimeoutBoundaryValuesAreValid(int seconds)
    {
        var options = Options();
        options.TimeoutSeconds = seconds;
        Assert.Null(options.ConfigurationError());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactPayloadAndApiKeyAuthenticationAreRequestLocal(bool streaming)
    {
        var options = Options(streaming);
        options.Deployment = "operator-selected-deployment";
        options.MaxOutputTokens = 3072;
        var input = new TextChatInput([new("user", "Any prompt: <>& 🌍\ntext"), new("assistant", "Previous answer"), new("user", "Continue")]);
        var credential = new StubCredential();
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://provider.test/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal(PrivateMarker, Assert.Single(request.Headers.GetValues("api-key")));
            Assert.Null(request.Headers.Authorization);
            Assert.True(token.CanBeCanceled);
            Assert.NotEqual(cancellation.Token, token);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var root = json.RootElement;
            AssertFields(root, "model", "input", "stream", "store", "max_output_tokens");
            Assert.Equal(options.Deployment, root.GetProperty("model").GetString());
            Assert.Equal(streaming, root.GetProperty("stream").GetBoolean());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal(3072, root.GetProperty("max_output_tokens").GetInt32());
            var messages = root.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal(input.Messages.Count, messages.Length);
            for (var i = 0; i < messages.Length; i++)
            {
                AssertFields(messages[i], "role", "content");
                Assert.Equal(input.Messages[i].Role, messages[i].GetProperty("role").GetString());
                Assert.Equal(input.Messages[i].Text, messages[i].GetProperty("content").GetString());
            }
            return Response(Final("Answer"), "application/json", 1);
        });
        using var http = Http(handler);
        http.DefaultRequestHeaders.Add("X-Fixture", "unchanged");
        var headersBefore = http.DefaultRequestHeaders.ToString();
        var result = await Collect(new ResponsesChatClient(http, options, credential), input, cancellation.Token);
        Assert.Equal(new TextChatEvent("completed", "Answer"), Assert.Single(result));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, credential.Calls);
        Assert.Equal(headersBefore, http.DefaultRequestHeaders.ToString());
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Theory]
    [InlineData(1, 1, 2, 1, 20 * 1024 * 1024)]
    [InlineData(256000, 1000000, 1000, 16, 64 * 1024 * 1024)]
    public void InputBudgetBoundariesDoNotChangeOutputLimits(int prompt, int context, int messages, int images, int bytes)
    {
        var options = Options();
        options.MaxPromptCharacters = prompt;
        options.MaxContextCharacters = context;
        options.MaxContextMessages = messages;
        options.MaxContextImages = images;
        options.MaxContextImageBytes = bytes;
        Assert.Null(options.ConfigurationError());
        Assert.Equal(2048, options.MaxOutputTokens);
        Assert.Equal(12000, ResponsesChatClient.MaxResponseCharacters);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImagesUseResponsesPartsAndSignatureDetectedInlineMimeTypes(bool streaming)
    {
        var png = new ImageData(CoreTests.Png, "https://must-not-fetch.test/" + PrivateMarker);
        var jpeg = new ImageData([255, 216, 255, 224], "image/png");
        var input = new TextChatInput([
            new("user", "Earlier text", []),
            new("assistant", "Earlier answer", []),
            new("user", "Compare these images", [png, jpeg])
        ]);
        var handler = new StubHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal("Earlier text", messages[0].GetProperty("content").GetString());
            Assert.Equal("Earlier answer", messages[1].GetProperty("content").GetString());
            Assert.Equal("user", messages[2].GetProperty("role").GetString());
            var parts = messages[2].GetProperty("content").EnumerateArray().ToArray();
            Assert.Equal(3, parts.Length);
            AssertFields(parts[0], "type", "text");
            Assert.Equal("input_text", parts[0].GetProperty("type").GetString());
            Assert.Equal("Compare these images", parts[0].GetProperty("text").GetString());
            for (var i = 1; i < parts.Length; i++)
            {
                AssertFields(parts[i], "type", "image_url", "detail");
                Assert.Equal("input_image", parts[i].GetProperty("type").GetString());
                Assert.Equal("auto", parts[i].GetProperty("detail").GetString());
            }
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png.Bytes), parts[1].GetProperty("image_url").GetString());
            Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(jpeg.Bytes), parts[2].GetProperty("image_url").GetString());
            Assert.DoesNotContain(PrivateMarker, body.RootElement.GetRawText());
            return Response(streaming ? Completion(Final("Answer")) : Final("Answer"),
                streaming ? "text/event-stream" : "application/json");
        });
        using var http = Http(handler);
        Assert.Equal(new TextChatEvent("completed", "Answer"),
            Assert.Single(await Collect(new ResponsesChatClient(http, Options(streaming), new StubCredential()), input)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InputCharacterAndMessageBudgetsFailBeforeHttpOrAuthentication()
    {
        await AssertInputRejected(new([new("user", new string('p', 64001))]));
        await AssertInputRejected(new([new("user", new string('h', 64000)),
            new("assistant", new string('a', 128001)), new("user", new string('p', 64000))]));
        await AssertInputRejected(new(Enumerable.Repeat(new ChatInputMessage("user", "p"), 201).ToArray()));
        var options = Options();
        options.MaxPromptCharacters = 2;
        options.MaxContextCharacters = 4;
        options.MaxContextMessages = 2;
        await AssertInputRejected(new([new("assistant", "abc"), new("user", "de")]), options);
    }

    [Fact]
    public async Task ExactCharacterAndMessageBudgetsAreAcceptedIndependentlyOfOutputStorage()
    {
        var handler = Handler();
        using var http = Http(handler);
        var client = new ResponsesChatClient(http, Options(), new StubCredential());
        await Collect(client, new([new("user", new string('h', 64000)),
            new("assistant", new string('a', 128000)), new("user", new string('p', 64000))]));
        await Collect(client, new(Enumerable.Repeat(new ChatInputMessage("user", "p"), 200).ToArray()));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(12000, ResponsesChatClient.MaxResponseCharacters);
    }

    [Fact]
    public async Task PromptBudgetAppliesToTheLatestUserNotOlderContextAfterAnOperatorReduction()
    {
        var options = Options();
        options.MaxPromptCharacters = 1;
        var handler = Handler();
        using var http = Http(handler);
        await Collect(new ResponsesChatClient(http, options, new StubCredential()),
            new([new("user", "Earlier longer prompt"), new("assistant", "Earlier answer"), new("user", "p")]));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InvalidImagesAssistantImagesAndDisabledImagesFailBeforeHttpOrAuthentication()
    {
        foreach (var image in new ImageData[] {
            new(Encoding.UTF8.GetBytes("<svg>" + PrivateMarker + "</svg>"), "image/png"),
            new([], "image/png"), new([137, 80, 78, 71, 13, 10, 26, 10], "image/png"),
            new(null!, "image/png"), null!, new(PaddedPng(InputRules.MaxImageBytes + 1), "image/png") })
            await AssertInputRejected(new([new("user", PrivateMarker, [image])]));
        var png = new ImageData(CoreTests.Png, "image/png");
        await AssertInputRejected(new([new("assistant", PrivateMarker, [png]), new("user", "p")]));
        var options = Options();
        options.SupportsImages = false;
        await AssertInputRejected(new([new("user", PrivateMarker, [png])]), options);
    }

    [Fact]
    public async Task ImageCountAndRawByteBudgetsAreAggregateAcrossMessages()
    {
        var png = new ImageData(CoreTests.Png, "image/png");
        await AssertInputRejected(new([new("user", "Earlier", Enumerable.Repeat(png, 8).ToArray()), new("user", "Current", [png])]));
        await AssertInputRejected(new([new("user", "Earlier", [new(PaddedPng(16 * 1024 * 1024), "image/png")]),
            new("user", "Current", [new(PaddedPng(16 * 1024 * 1024 + 1), "image/png")])]));
        var options = Options();
        options.MaxContextImages = 1;
        await AssertInputRejected(new([new("user", "p", [png, png])]), options);
    }

    [Fact]
    public async Task ExactImageCountAndRawByteBudgetsAreAccepted()
    {
        var handler = Handler();
        using var http = Http(handler);
        var client = new ResponsesChatClient(http, Options(), new StubCredential());
        await Collect(client, new([new("user", "p", Enumerable.Repeat(new ImageData(CoreTests.Png, "image/png"), 8).ToArray())]));
        await Collect(client, new([new("user", "p", [new(PaddedPng(20 * 1024 * 1024), "image/png"),
            new(PaddedPng(12 * 1024 * 1024), "image/png")])]));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task EntraUsesOnlyTheExplicitScopeAndBearerToken()
    {
        var options = Options();
        options.AuthMode = "DefaultAzureCredential";
        options.TokenScope = "https://explicit-scope.test/.default";
        var credential = new StubCredential { AllowToken = true };
        var handler = new StubHandler((request, _) =>
        {
            Assert.False(request.Headers.Contains("api-key"));
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-entra-token", request.Headers.Authorization.Parameter);
            return Task.FromResult(Response(Final("Answer")));
        });
        using var http = Http(handler);
        await Collect(new ResponsesChatClient(http, options, credential));
        Assert.Equal(1, credential.Calls);
        Assert.Equal(new[] { options.TokenScope }, credential.Scopes);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("Authorization")]
    public async Task SharedDefaultCredentialsAreNeverInherited(string header)
    {
        var handler = Handler();
        using var http = Http(handler);
        http.DefaultRequestHeaders.TryAddWithoutValidation(header, PrivateMarker);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, Options(), new StubCredential())));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.Equal(PrivateMarker, Assert.Single(http.DefaultRequestHeaders.GetValues(header)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotLeakTheCredentialException()
    {
        var options = Options();
        options.AuthMode = "DefaultAzureCredential";
        var credential = new StubCredential { Failure = new AuthenticationFailedException(PrivateMarker) };
        var handler = Handler();
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, options, credential)));
        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    [InlineData("tool")]
    [InlineData("User")]
    [InlineData("user\r\nsystem")]
    [InlineData("private-fixture-secret")]
    [InlineData("")]
    [InlineData(null)]
    public async Task UnsupportedRolesFailBeforeSending(string? role)
    {
        var handler = Handler();
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, Options(), new StubCredential()),
            new TextChatInput([new(role!, PrivateMarker)])));
        Assert.Equal(400, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingMessagesOrTextFailBeforeSending()
    {
        var handler = Handler();
        using var http = Http(handler);
        var client = new ResponsesChatClient(http, Options(), new StubCredential());
        foreach (var input in new TextChatInput[] { new([]), new(null!), new([null!]), new([new("user", null!)]) })
        {
            var error = await Assert.ThrowsAsync<AppException>(() => Collect(client, input));
            Assert.Equal(400, error.StatusCode);
        }
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task SseHandlesUtf8PacketSplitsMultilineCommentsAndCanonicalRefusals(int fragment)
    {
        var body = "\ufeff: heartbeat\r\n\r\n" +
            "event: ignored-advisory-name\r\nid: 1\r\nretry: 1000\r\n" +
            "data: {\r\ndata: \"type\":\"response.output_text.delta\",\r\ndata: \"delta\":\"Hi \"}\r\n\r\n" +
            "data:{\"type\":\"response.output_text.delta\",\"delta\":\"🌍é\"}\n\n" +
            Event(new { type = "response.reasoning_text.delta", delta = "IGNORE reasoning" }) +
            Event(new { type = "response.function_call_arguments.delta", delta = "IGNORE tool" }) +
            Event(new { type = "response.output_text.done", text = "IGNORE done" }) +
            Event(new { type = "response.refusal.delta", delta = "No." }) +
            Completion(Canonical()) + "data: [DONE]\n\n";
        var handler = Handler(body, "text/event-stream", fragment);
        using var http = Http(handler);
        var events = await Collect(new ResponsesChatClient(http, Options(), new StubCredential()));
        Assert.Equal(new[] { new TextChatEvent("delta", "Hi "), new("delta", "🌍é"), new("delta", "No."),
            new("completed", "Final 🌍éNo.Next") }, events);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task SseAcceptsAllLineEndingsAndCompletionWithoutDeltas(string newline)
    {
        var body = (": comment\n\n" + Completion(Final("Answer"))).Replace("\n", newline);
        var handler = Handler(body, "text/event-stream", 1);
        using var http = Http(handler);
        var events = await Collect(new ResponsesChatClient(http, Options(), new StubCredential()));
        Assert.Equal(new TextChatEvent("completed", "Answer"), Assert.Single(events));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JsonReturnsOnlyCanonicalAssistantTextAndRefusals(bool streaming)
    {
        var handler = Handler("\ufeff" + Canonical(), "application/json", 1);
        using var http = Http(handler);
        var events = await Collect(new ResponsesChatClient(http, Options(streaming), new StubCredential()));
        Assert.Equal(new TextChatEvent("completed", "Final 🌍éNo.Next"), Assert.Single(events));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RefusalOnlyIsAVisibleCompletedResponse()
    {
        var body = Json(new { status = "completed", output = new[] { Message(new { type = "refusal", refusal = "No." }) } });
        var handler = Handler(Completion(body), "text/event-stream", 2);
        using var http = Http(handler);
        Assert.Equal(new TextChatEvent("completed", "No."), Assert.Single(await Collect(new ResponsesChatClient(http, Options(), new StubCredential()))));
    }

    public static IEnumerable<object[]> InvalidSse()
    {
        foreach (var type in new[] { "response.failed", "response.incomplete", "error", "response.error" })
            yield return [Event(new { type, error = new { message = PrivateMarker }, response = new { status = "failed" } })];
        foreach (var raw in new[] { "not-json-" + PrivateMarker, "null", "[]", "{}", "{\"type\":1}",
            "{\"type\":\"response.output_text.delta\"}", "{\"type\":\"response.refusal.delta\",\"delta\":null}",
            "{\"type\":\"response.output_text.delta\",\"delta\":{}}", "{\"type\":\"response.completed\"}",
            "{\"type\":\"response.completed\",\"response\":null}" })
            yield return ["data: " + raw + "\n\n"];
        yield return [""];
        yield return [": heartbeat\n\n"];
        yield return ["data:\n\n"];
        yield return ["data\n\n"];
        yield return ["data: [DONE]\n\n" + Completion(Final("Too late"))];
        yield return [Event(new { type = "", delta = PrivateMarker }) + Completion(Final("Too late"))];
        yield return ["data: {\"type\":\"response.output_text.delta\",\"delta\":\"\\uD800\"}\n\n" + Completion(Final("Too late"))];
        yield return [Delta("Partial")];
        yield return [Delta("Partial") + "data: [DONE]\n\n"];
        yield return [Completion(Final("Unterminated")).TrimEnd('\n')];
        yield return ["data: {\"type\":\"response.output_text.delta\",\"delta\":\"truncated"];
    }

    [Theory]
    [MemberData(nameof(InvalidSse))]
    public async Task InvalidSseAndEofNeverSucceedOrLeakRawBodies(string body) =>
        await AssertSafeFailure(body, "text/event-stream", fragment: 1);

    public static IEnumerable<object[]> InvalidJson()
    {
        foreach (var body in new[] { "not-json-" + PrivateMarker, "null", "[]", "{}",
            "{\"status\":\"completed\"}", "{\"status\":\"completed\",\"output\":null}",
            "{\"status\":\"completed\",\"output\":[null]}", "{\"status\":\"completed\",\"output\":{}}" })
            yield return [body];
        foreach (var status in new[] { "failed", "incomplete", "queued", "in_progress", "cancelled", "Completed", "" })
            yield return [Final(PrivateMarker, status)];
        yield return [Final("MARKER").Replace("MARKER", "\\uD800")];
        yield return [Final("")];
        yield return [Json(new { status = "completed", output_text = PrivateMarker, output = Array.Empty<object>() })];
        yield return [Json(new { status = "completed", output = new[] { new { type = "reasoning", text = PrivateMarker } } })];
        yield return [Json(new { status = "completed", output = new[] { new { type = "message", role = "user", content = PrivateMarker } } })];
        yield return [Json(new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = PrivateMarker } } })];
        yield return [Json(new { status = "completed", output = new[] { Message(new { type = "output_text", text = (string?)null }) } })];
        yield return [Json(new { status = "completed", output = new[] { Message(new { type = "refusal", refusal = 123 }) } })];
        yield return [Json(new { status = "completed", output = new[] { Message(new { type = "output_text" }) } })];
    }

    [Theory]
    [MemberData(nameof(InvalidJson))]
    public async Task InvalidCanonicalResponsesFailForJsonAndSse(string body)
    {
        await AssertSafeFailure(body, "application/json", streaming: false);
        await AssertSafeFailure(body, "application/json", streaming: true);
        // Non-JSON fixtures cannot be embedded in a completion wrapper, and remain malformed.
        await AssertSafeFailure(Completion(body), "text/event-stream");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(307)]
    public async Task HttpErrorsNeverRetrySwitchStreamingOrPoll(int status)
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var response = Response(PrivateMarker);
            response.StatusCode = (HttpStatusCode)status;
            response.Headers.Location = new Uri("https://must-not-fetch.test/" + PrivateMarker);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, Options(), new StubCredential())));
        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("text/plain", false)]
    [InlineData("text/event-stream", false)]
    public async Task UnexpectedContentTypesFailWithoutFallback(string mediaType, bool streaming) =>
        await AssertSafeFailure(Completion(Final("Answer")), mediaType, streaming);

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    public async Task InvalidUtf8IsRejectedSafely(string mediaType)
    {
        var bytes = Encoding.UTF8.GetBytes(mediaType == "application/json" ? Final("MARKER") : Delta("MARKER"));
        var marker = Encoding.UTF8.GetString(bytes).IndexOf("MARKER", StringComparison.Ordinal);
        bytes[marker] = 0xff;
        var handler = new StubHandler((_, _) => Task.FromResult(Response(new FragmentedStream(bytes, 1), mediaType)));
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, Options(), new StubCredential())));
        Assert.Equal(502, error.StatusCode);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderByteLimitIsEnforcedWithOrWithoutContentLength(bool sse)
    {
        var final = sse ? Completion(Final("OK")) : Final("OK");
        var padding = ProviderLimit - Encoding.UTF8.GetByteCount(final) - (sse ? 3 : 0);
        var body = sse ? ":" + new string(' ', padding) + "\n\n" + final : final + new string(' ', padding);
        Assert.Equal(ProviderLimit, Encoding.UTF8.GetByteCount(body));
        var mediaType = sse ? "text/event-stream" : "application/json";
        var handler = Handler(body, mediaType, 4093);
        using (var http = Http(handler))
            Assert.Equal(new TextChatEvent("completed", "OK"), Assert.Single(await Collect(new ResponsesChatClient(http, Options(), new StubCredential()))));
        await AssertSafeFailure(" " + body, mediaType, fragment: 4093);

        var oversized = new FragmentedStream(Encoding.UTF8.GetBytes(final), 1);
        var lengthHandler = new StubHandler((_, _) =>
        {
            var response = Response(oversized, mediaType);
            response.Content.Headers.ContentLength = ProviderLimit + 1;
            return Task.FromResult(response);
        });
        using var lengthHttp = Http(lengthHandler);
        await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(lengthHttp, Options(), new StubCredential())));
        Assert.Equal(0, oversized.Reads);
        Assert.True(oversized.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SseDataLimitCountsUtf8BytesAndMultilineSeparators(bool multiline)
    {
        var prefix = multiline ? "{\n\"type\":\"response.fixture\",\"ignored\":\"" : "{\"type\":\"response.fixture\",\"ignored\":\"";
        const string suffix = "\"}";
        var room = EventLimit - Encoding.UTF8.GetByteCount(prefix + suffix);
        var payload = prefix + new string('é', room / 2) + new string('x', room % 2) + suffix;
        Assert.Equal(EventLimit, Encoding.UTF8.GetByteCount(payload));
        var body = "data: " + payload.Replace("\n", "\ndata: ") + "\n\n" + Completion(Final("OK"));
        var handler = Handler(body, "text/event-stream", 4093);
        using (var http = Http(handler))
            Assert.Equal(new TextChatEvent("completed", "OK"), Assert.Single(await Collect(new ResponsesChatClient(http, Options(), new StubCredential()))));
        await AssertSafeFailure(body.Replace(suffix + "\n\n", "x" + suffix + "\n\n"), "text/event-stream", fragment: 4093);
    }

    [Fact]
    public async Task OutputLimitCountsCumulativeUtf16DeltasWithoutDoubleCountingTheFinal()
    {
        var half = string.Concat(Enumerable.Repeat("🌍", 3000));
        var full = half + half;
        Assert.Equal(ResponsesChatClient.MaxResponseCharacters, full.Length);
        var deltas = Delta(half) + Event(new { type = "response.refusal.delta", delta = half });
        var handler = Handler(deltas + Completion(Final(full)), "text/event-stream", 7);
        using (var http = Http(handler))
        {
            var events = await Collect(new ResponsesChatClient(http, Options(), new StubCredential()));
            Assert.Equal(3, events.Count);
            Assert.Equal(new TextChatEvent("completed", full), events[^1]);
        }
        await AssertSafeFailure(deltas + Delta("x") + Completion(Final("short")), "text/event-stream");
        await AssertSafeFailure(Delta(full + "x") + Completion(Final("short")), "text/event-stream");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalOutputLimitIncludesAllTextAndRefusalParts(bool sse)
    {
        var text = new string('x', ResponsesChatClient.MaxResponseCharacters);
        foreach (var extra in new[] { "", "x" })
        {
            var final = Json(new { status = "completed", output = new[] { Message(new { type = "output_text", text }, new { type = "refusal", refusal = extra }) } });
            var body = sse ? Completion(final) : final;
            var mediaType = sse ? "text/event-stream" : "application/json";
            if (extra.Length > 0) await AssertSafeFailure(body, mediaType);
            else
            {
                var handler = Handler(body, mediaType);
                using var http = Http(handler);
                Assert.Equal(new TextChatEvent("completed", text), Assert.Single(await Collect(new ResponsesChatClient(http, Options(), new StubCredential()))));
            }
        }
    }

    [Fact]
    public async Task DeltasAreDeliveredBeforeEofButNeverCommittedOnFailure()
    {
        var handler = Handler(Delta("Partial"), "text/event-stream", 1);
        using var http = Http(handler);
        await using var events = new ResponsesChatClient(http, Options(), new StubCredential()).GenerateAsync(Input, default).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(new TextChatEvent("delta", "Partial"), events.Current);
        var error = await Assert.ThrowsAsync<AppException>(async () => await events.MoveNextAsync());
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task CancellationDuringAReadPropagatesAndDisposesTheStream()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes(Delta("Partial")), 4096) { BlockAtEnd = true };
        var handler = new StubHandler((_, _) => Task.FromResult(Response(stream, "text/event-stream")));
        using var http = Http(handler);
        await using var events = new ResponsesChatClient(http, Options(), new StubCredential()).GenerateAsync(Input, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        var pending = events.MoveNextAsync().AsTask();
        await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationCannotCommitACompletionAlreadyBufferedWithADelta()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes(Delta("Partial") + Completion(Final("Do not commit"))), 4096);
        var handler = new StubHandler((_, _) => Task.FromResult(Response(stream, "text/event-stream")));
        using var http = Http(handler);
        // WithCancellation also exercises the iterator's EnumeratorCancellation contract.
        await using var events = new ResponsesChatClient(http, Options(), new StubCredential()).GenerateAsync(Input, default)
            .WithCancellation(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(1, stream.Reads);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync());
        Assert.Equal(1, stream.Reads);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task CancellationDuringSendPropagatesWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = Http(handler);
        var pending = Collect(new ResponsesChatClient(http, Options(), new StubCredential()), token: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationBeforeSendingDoesNotRequestCredentials()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = Handler();
        var credential = new StubCredential();
        using var http = Http(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(new ResponsesChatClient(http, Options(), credential), token: cancellation.Token));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public async Task TransportErrorsAreDelegatedToTheParentWithoutRetry()
    {
        var failure = new HttpRequestException("fixture transport error");
        var handler = new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(failure));
        using var http = Http(handler);
        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => Collect(new ResponsesChatClient(http, Options(), new StubCredential()))));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletionOrAbandoningEnumerationDisposesWithoutDrainingOrPolling(bool finish)
    {
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes(finish ? Completion(Final("OK")) : Delta("Partial")), 4096) { BlockAtEnd = true };
        var handler = new StubHandler((_, _) => Task.FromResult(Response(stream, "text/event-stream")));
        using var http = Http(handler);
        await using (var events = new ResponsesChatClient(http, Options(), new StubCredential()).GenerateAsync(Input, default).GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync());
            if (finish) Assert.False(await events.MoveNextAsync());
        }
        Assert.True(stream.Disposed);
        Assert.False(stream.Blocked.Task.IsCompleted);
        Assert.Equal(1, handler.Calls);
    }

    private static async Task AssertInputRejected(TextChatInput input, ChatModelOptions? options = null)
    {
        options ??= Options();
        options.AuthMode = "DefaultAzureCredential";
        var credential = new StubCredential();
        var handler = Handler();
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, options, credential), input));
        Assert.Equal(400, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(0, credential.Calls);
        Assert.Equal(0, handler.Calls);
    }

    private static byte[] PaddedPng(int bytes)
    {
        var result = new byte[bytes];
        CoreTests.Png.CopyTo(result, 0);
        return result;
    }

    private static ChatModelOptions Options(bool streaming = true) => new()
    {
        Endpoint = "https://provider.test", ApiKey = PrivateMarker, Streaming = streaming
    };

    private static HttpClient Http(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    private static StubHandler Handler(string? body = null, string mediaType = "application/json", int fragment = 4096) =>
        new((_, _) => Task.FromResult(Response(body ?? Final("Answer"), mediaType, fragment)));
    private static HttpResponseMessage Response(string body, string mediaType = "application/json", int fragment = 4096) =>
        Response(new FragmentedStream(Encoding.UTF8.GetBytes(body), fragment), mediaType);
    private static HttpResponseMessage Response(Stream stream, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        return response;
    }

    private static async Task<List<TextChatEvent>> Collect(ITextChatGenerator client, TextChatInput? input = null, CancellationToken token = default)
    {
        var events = new List<TextChatEvent>();
        await foreach (var item in client.GenerateAsync(input ?? Input, token)) events.Add(item);
        return events;
    }

    private static async Task AssertSafeFailure(string body, string mediaType, bool streaming = true, int fragment = 4096)
    {
        var handler = Handler(body, mediaType, fragment);
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, Options(streaming), new StubCredential())));
        Assert.Equal(502, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.DoesNotContain("provider.test", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    private static void AssertFields(JsonElement element, params string[] fields) =>
        Assert.Equal(fields.OrderBy(x => x), element.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
    private static object Message(params object[] content) => new { type = "message", role = "assistant", content };
    private static string Final(string text, string status = "completed") => Json(new
    {
        status, output = new[] { Message(new { type = "output_text", text }) }
    });
    private static string Completion(string response) => "data: {\"type\":\"response.completed\",\"response\":" + response + "}\n\n";
    private static string Event(object value) => "data: " + Json(value) + "\n\n";
    private static string Delta(string text) => Event(new { type = "response.output_text.delta", delta = text });
    private static string Canonical() => Json(new
    {
        status = "completed", output_text = "IGNORE noncanonical convenience field", tools = new { irrelevant = true },
        output = new object[]
        {
            new { type = "reasoning", summary = new[] { new { text = "IGNORE reasoning" } } },
            new { type = "function_call", arguments = "IGNORE tool" },
            new { type = "message", role = "user", content = new[] { new { type = "output_text", text = "IGNORE user" } } },
            Message(new { type = "output_text", text = "Final 🌍é" }, new { type = "refusal", refusal = "No." }, new { type = "reasoning_text", text = "IGNORE hidden" }),
            Message(new { type = "output_text", text = "Next" })
        }
    });

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private sealed class StubCredential : TokenCredential
    {
        public bool AllowToken { get; init; }
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public string[] Scopes { get; private set; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            Scopes = requestContext.Scopes;
            if (Failure is not null) throw Failure;
            Assert.True(AllowToken, "API-key mode must not call a credential.");
            return ValueTask.FromResult(new AccessToken("fixture-entra-token", DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    // Non-seekable and unknown length, deliberately splitting UTF-8, CRLF, and JSON tokens.
    private sealed class FragmentedStream(byte[] bytes, int fragment) : Stream
    {
        private int position;
        public bool BlockAtEnd { get; init; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            cancellationToken.ThrowIfCancellationRequested();
            if (position == bytes.Length && BlockAtEnd)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var count = Math.Min(Math.Min(fragment, buffer.Length), bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}