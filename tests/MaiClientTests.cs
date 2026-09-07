using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;
using ImageChat;
using Xunit;

namespace ImageChat.Tests;

public class MaiClientTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("1024x1024", 1024, 1024)]
    [InlineData("768x1024", 768, 1024)]
    [InlineData("1024x768", 1024, 768)]
    [InlineData("768x1365", 768, 1365)]
    [InlineData("1365x768", 1365, 768)]
    public async Task MaiGenerationSendsOnlyDeploymentPromptAndNumericDimensions(string size, int width, int height)
    {
        var options = Options();
        var credential = new StubTokenCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://mai-provider.test/mai/v1/images/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("", request.RequestUri.Query);
            Assert.Equal(options.Mai.ApiKey, Assert.Single(request.Headers.GetValues("api-key")));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var body = json.RootElement;
            AssertJsonFields(body, "model", "prompt", "width", "height");
            Assert.Equal(options.Mai.Deployment, body.GetProperty("model").GetString());
            Assert.Equal("A green chair & a blue sky", body.GetProperty("prompt").GetString());
            Assert.Equal(JsonValueKind.Number, body.GetProperty("width").ValueKind);
            Assert.Equal(JsonValueKind.Number, body.GetProperty("height").ValueKind);
            Assert.Equal(width, body.GetProperty("width").GetInt32());
            Assert.Equal(height, body.GetProperty("height").GetInt32());
            return ImageResponse();
        });
        using var http = new HttpClient(handler);
        var result = await new FoundryClient(http, options, credential)
            .GenerateAsync(new("A green chair & a blue sky", size, "default", null, ModelCatalog.Mai), default);
        Assert.Equal(CoreTests.Png, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(credential.Scopes);
        Assert.False(http.DefaultRequestHeaders.Contains("api-key"));
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("image/png", "reference.png")]
    [InlineData("image/jpeg", "reference.jpg")]
    public async Task MaiEditSendsOnlyModelPromptAndUnchangedImageMultipart(string contentType, string filename)
    {
        var options = Options();
        var image = new ImageData(contentType == "image/png" ? CoreTests.Png : new byte[] { 255, 216, 255, 224, 0, 0 }, contentType);
        var credential = new StubTokenCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://mai-provider.test/mai/v1/images/edits", request.RequestUri!.AbsoluteUri);
            Assert.Equal("", request.RequestUri.Query);
            Assert.Equal(options.Mai.ApiKey, Assert.Single(request.Headers.GetValues("api-key")));
            Assert.Null(request.Headers.Authorization);
            var parts = MultipartParts(request);
            Assert.Equal(new[] { "image", "model", "prompt" }, parts.Keys.OrderBy(name => name).ToArray());
            Assert.Equal(options.Mai.Deployment, await parts["model"].ReadAsStringAsync(token));
            Assert.Equal("Make it blue", await parts["prompt"].ReadAsStringAsync(token));
            Assert.Equal(image.Bytes, await parts["image"].ReadAsByteArrayAsync(token));
            Assert.Equal(contentType, parts["image"].Headers.ContentType!.MediaType);
            Assert.Equal(filename, parts["image"].Headers.ContentDisposition!.FileName!.Trim('"'));
            return ImageResponse();
        });
        using var http = new HttpClient(handler);
        var result = await new FoundryClient(http, options, credential)
            .GenerateAsync(new("Make it blue", "auto", "default", image, ModelCatalog.Mai), default);
        Assert.Equal(CoreTests.Png, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(credential.Scopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaiEntraAuthUsesOnlyMaiTokenScopeAndBearerWithoutApiKey(bool editing)
    {
        var options = Options();
        options.Mai.AuthMode = "DefaultAzureCredential";
        var credential = new StubTokenCredential(allowToken: true);
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("https://mai-provider.test/mai/v1/images/" + (editing ? "edits" : "generations"), request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("api-key"));
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(StubTokenCredential.Token, request.Headers.Authorization.Parameter);
            return Task.FromResult(ImageResponse());
        });
        using var http = new HttpClient(handler);
        var result = await new FoundryClient(http, options, credential).GenerateAsync(
            new("An authenticated fixture", editing ? "auto" : "1024x1024", "default",
                editing ? new ImageData(CoreTests.Png, "image/png") : null, ModelCatalog.Mai), default);
        Assert.Equal(CoreTests.Png, result.Bytes);
        Assert.Equal(new[] { options.Mai.TokenScope }, Assert.Single(credential.Scopes));
        Assert.Equal(1, handler.Calls);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.False(http.DefaultRequestHeaders.Contains("api-key"));
    }

    [Theory]
    [InlineData("not-json-private-marker")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[{},{}]}")]
    [InlineData("{\"data\":[{\"url\":\"https://private-marker.test/image.png\"}]}")]
    [InlineData("{\"data\":[{\"b64_json\":123}]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"private-marker-invalid-base64!\"}]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"aGVsbG8=\"}]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"AAAAAAAAAAA=\"}]}")]
    public async Task MalformedMaiResponsesAreSafe502WithoutRetryOrUrlFallback(string body)
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("https://mai-provider.test/mai/v1/images/generations", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, Options(), new StubTokenCredential())
            .GenerateAsync(new("A fixture", "1024x1024", "default", null, ModelCatalog.Mai), default));
        Assert.Equal(502, exception.StatusCode);
        Assert.DoesNotContain("private-marker", exception.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(429, 429)]
    [InlineData(401, 502)]
    [InlineData(404, 502)]
    [InlineData(422, 502)]
    [InlineData(500, 502)]
    public async Task MaiUpstreamErrorsNeverRetryOrFallBackToGptAndDoNotLeakBodies(int upstreamStatus, int appStatus)
    {
        var options = Options(); // GPT remains valid so an accidental fallback would be observable.
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("https://mai-provider.test/mai/v1/images/generations", request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage((HttpStatusCode)upstreamStatus)
            {
                Content = new StringContent("private-upstream-marker " + options.Mai.ApiKey + " " + options.Mai.Endpoint)
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, options, new StubTokenCredential())
            .GenerateAsync(new("A private fixture prompt", "1024x1024", "default", null, ModelCatalog.Mai), default));
        Assert.Equal(appStatus, exception.StatusCode);
        foreach (var secret in new[] { "private-upstream-marker", options.Mai.ApiKey, options.Mai.Endpoint, "A private fixture prompt" })
            Assert.DoesNotContain(secret, exception.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SharedClientKeepsParallelGptAndMaiConnectionsHeadersPayloadsAndOutputsIndependent()
    {
        var options = Options();
        var before = JsonSerializer.Serialize(options); // Synthetic options only, never application configuration.
        var credential = new StubTokenCredential();
        var started = Channel.CreateUnbounded<RequestSnapshot>();
        var completeGpt = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeMai = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maiBytes = CoreTests.Png.Concat(new byte[] { 43 }).ToArray();
        var handler = new StubHandler(async (request, token) =>
        {
            var snapshot = new RequestSnapshot(request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("api-key", out var keys) ? string.Join(",", keys) : null,
                request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(token));
            started.Writer.TryWrite(snapshot);
            var bytes = await (request.RequestUri.AbsolutePath.StartsWith("/mai/", StringComparison.Ordinal)
                ? completeMai.Task : completeGpt.Task).WaitAsync(token);
            return ImageResponse(bytes);
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Add("X-Fixture-Header", "unchanged");
        var originalHeaders = http.DefaultRequestHeaders.ToString();
        var client = new FoundryClient(http, options, credential);
        var gpt = client.GenerateAsync(new("Only GPT prompt", "1536x1024", "high", null, ModelCatalog.Gpt), default);
        var mai = client.GenerateAsync(new("Only MAI prompt", "768x1365", "default", null, ModelCatalog.Mai), default);
        try
        {
            var first = await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            var second = await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.False(gpt.IsCompleted);
            Assert.False(mai.IsCompleted);
            var snapshots = new[] { first, second };
            var gptRequest = Assert.Single(snapshots, request => request.Uri.StartsWith(options.Endpoint.TrimEnd('/'), StringComparison.Ordinal));
            var maiRequest = Assert.Single(snapshots, request => request.Uri.StartsWith(options.Mai.Endpoint.TrimEnd('/'), StringComparison.Ordinal));
            Assert.Equal("https://gpt-provider.test/openai/deployments/gpt-fixture-deployment/images/generations?api-version=2025-04-01-preview", gptRequest.Uri);
            Assert.Equal("https://mai-provider.test/mai/v1/images/generations", maiRequest.Uri);
            Assert.Equal(options.ApiKey, gptRequest.ApiKey);
            Assert.Equal(options.Mai.ApiKey, maiRequest.ApiKey);
            Assert.All(snapshots, request => Assert.Null(request.Authorization));
            using var gptJson = JsonDocument.Parse(gptRequest.Body);
            using var maiJson = JsonDocument.Parse(maiRequest.Body);
            AssertJsonFields(gptJson.RootElement, "model", "prompt", "size", "quality", "n", "output_format");
            Assert.Equal(options.Deployment, gptJson.RootElement.GetProperty("model").GetString());
            Assert.Equal("Only GPT prompt", gptJson.RootElement.GetProperty("prompt").GetString());
            Assert.Equal("1536x1024", gptJson.RootElement.GetProperty("size").GetString());
            Assert.Equal("high", gptJson.RootElement.GetProperty("quality").GetString());
            AssertJsonFields(maiJson.RootElement, "model", "prompt", "width", "height");
            Assert.Equal(options.Mai.Deployment, maiJson.RootElement.GetProperty("model").GetString());
            Assert.Equal("Only MAI prompt", maiJson.RootElement.GetProperty("prompt").GetString());
            Assert.Equal(768, maiJson.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(1365, maiJson.RootElement.GetProperty("height").GetInt32());
            Assert.Equal(before, JsonSerializer.Serialize(options));
            Assert.Equal(originalHeaders, http.DefaultRequestHeaders.ToString());

            completeMai.SetResult(maiBytes);
            var maiResult = await mai.WaitAsync(Deadline);
            Assert.Equal(maiBytes, maiResult.Bytes);
            Assert.Equal("image/png", maiResult.ContentType);
            Assert.False(gpt.IsCompleted);
            completeGpt.SetResult(CoreTests.Png);
            var gptResult = await gpt.WaitAsync(Deadline);
            Assert.Equal(CoreTests.Png, gptResult.Bytes);
            Assert.Equal("image/png", gptResult.ContentType);
            Assert.Equal(2, handler.Calls);
            Assert.Empty(credential.Scopes);
            Assert.Equal(before, JsonSerializer.Serialize(options));
            Assert.Equal(originalHeaders, http.DefaultRequestHeaders.ToString());
        }
        finally
        {
            completeGpt.TrySetResult(CoreTests.Png);
            completeMai.TrySetResult(maiBytes);
            await Task.WhenAll(gpt, mai).WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("deployments", false, "/openai/deployments/deployment%20with%20spaces/images/generations")]
    [InlineData("deployments", true, "/openai/deployments/deployment%20with%20spaces/images/edits")]
    [InlineData("v1", false, "/openai/v1/images/generations")]
    [InlineData("v1", true, "/openai/v1/images/edits")]
    public void LegacyGptRequestUriStylesStillEscapeDeploymentAndPreserveApiVersion(string style, bool editing, string path)
    {
        var options = Options();
        options.ApiStyle = style;
        options.Deployment = "deployment with spaces";
        options.ApiVersion = "fixture version+1";
        Assert.Null(options.ConfigurationError());
        Assert.Equal("https://gpt-provider.test" + path + "?api-version=fixture%20version%2B1", options.RequestUri(editing).AbsoluteUri);
        // Nested MAI settings must not change the root GPT routing contract.
        var selected = new ModelCatalog(options).Resolve(null);
        Assert.Equal(ModelCatalog.Gpt, selected.Id);
        Assert.Equal(options.RequestUri(editing), selected.Connection.RequestUri(editing));
    }

    [Theory]
    [InlineData("deployments", false)]
    [InlineData("deployments", true)]
    [InlineData("v1", false)]
    [InlineData("v1", true)]
    public async Task LegacyGptTransportRetainsSizeQualityCountFormatAndRootCredentials(string style, bool editing)
    {
        var options = Options();
        options.ApiStyle = style;
        var credential = new StubTokenCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            var path = style == "v1" ? "openai/v1/images" : "openai/deployments/gpt-fixture-deployment/images";
            Assert.Equal($"https://gpt-provider.test/{path}/{(editing ? "edits" : "generations")}?api-version=2025-04-01-preview",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(options.ApiKey, Assert.Single(request.Headers.GetValues("api-key")));
            Assert.Null(request.Headers.Authorization);
            if (editing)
            {
                var parts = MultipartParts(request);
                Assert.Equal(new[] { "image", "model", "n", "output_format", "prompt", "quality", "size" }, parts.Keys.OrderBy(name => name).ToArray());
                foreach (var (name, expected) in new[]
                         { ("model", options.Deployment), ("prompt", "GPT fixture"), ("size", "3840x2160"),
                           ("quality", "high"), ("n", "1"), ("output_format", "png") })
                    Assert.Equal(expected, await parts[name].ReadAsStringAsync(token));
                Assert.Equal(CoreTests.Png, await parts["image"].ReadAsByteArrayAsync(token));
                Assert.Equal("image/png", parts["image"].Headers.ContentType!.MediaType);
            }
            else
            {
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                var body = json.RootElement;
                AssertJsonFields(body, "model", "prompt", "size", "quality", "n", "output_format");
                Assert.Equal(options.Deployment, body.GetProperty("model").GetString());
                Assert.Equal("GPT fixture", body.GetProperty("prompt").GetString());
                Assert.Equal("3840x2160", body.GetProperty("size").GetString());
                Assert.Equal("high", body.GetProperty("quality").GetString());
                Assert.Equal(1, body.GetProperty("n").GetInt32());
                Assert.Equal("png", body.GetProperty("output_format").GetString());
            }
            return ImageResponse();
        });
        using var http = new HttpClient(handler);
        var result = await new FoundryClient(http, options, credential).GenerateAsync(
            new("GPT fixture", "3840x2160", "high", editing ? new ImageData(CoreTests.Png, "image/png") : null), default);
        Assert.Equal(CoreTests.Png, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(credential.Scopes);
    }

    [Theory]
    [InlineData("missing-endpoint", 503)]
    [InlineData("missing-key", 503)]
    [InlineData("missing-deployment", 503)]
    [InlineData("invalid-model", 400)]
    [InlineData("empty-model", 400)]
    [InlineData("quality", 400)]
    [InlineData("4k", 400)]
    [InlineData("auto-generation", 400)]
    [InlineData("explicit-edit-size", 400)]
    public async Task InvalidMaiSelectionOptionsOrConnectionNeverAcquireTokenOrSendHttp(string scenario, int status)
    {
        var options = Options();
        var model = ModelCatalog.Mai;
        var size = "1024x1024";
        var quality = "default";
        ImageData? image = null;
        switch (scenario)
        {
            case "missing-endpoint": options.Mai.Endpoint = ""; break;
            case "missing-key": options.Mai.ApiKey = ""; break;
            case "missing-deployment": options.Mai.Deployment = ""; break;
            case "invalid-model": model = options.Mai.Deployment; break;
            case "empty-model": model = ""; break;
            case "quality": quality = "high"; break;
            case "4k": size = "3840x2160"; break;
            case "auto-generation": size = "auto"; break;
            case "explicit-edit-size": image = new ImageData(CoreTests.Png, "image/png"); break;
            default: throw new InvalidOperationException("Unknown test scenario.");
        }
        var credential = new StubTokenCredential();
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Validation must prevent transport calls."));
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, options, credential)
            .GenerateAsync(new("A fixture", size, quality, image, model), default));
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(credential.Scopes);
    }

    private static FoundryOptions Options() => new()
    {
        Endpoint = "https://gpt-provider.test/", Deployment = "gpt-fixture-deployment", ApiKey = "gpt-fixture-key",
        TokenScope = "https://gpt-scope.test/.default",
        Mai = new MaiOptions
        {
            Endpoint = "https://mai-provider.test/", Deployment = "mai-fixture-deployment", ApiKey = "mai-fixture-key",
            TokenScope = "https://mai-scope.test/.default"
        }
    };

    private static void AssertJsonFields(JsonElement body, params string[] expected) =>
        Assert.Equal(expected.OrderBy(name => name).ToArray(), body.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());

    private static Dictionary<string, HttpContent> MultipartParts(HttpRequestMessage request)
    {
        var form = Assert.IsType<MultipartFormDataContent>(request.Content);
        Assert.Equal("multipart/form-data", form.Headers.ContentType!.MediaType);
        // ToDictionary also rejects duplicate field names, not just unexpected fields.
        return form.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
    }

    private static HttpResponseMessage ImageResponse(byte[]? bytes = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(bytes ?? CoreTests.Png) } }
        }), Encoding.UTF8, "application/json")
    };

    private sealed record RequestSnapshot(string Uri, string? ApiKey, string? Authorization, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return action(request, cancellationToken);
        }
    }

    private sealed class StubTokenCredential(bool allowToken = false) : TokenCredential
    {
        public const string Token = "only-a-fixture-entra-token";
        public ConcurrentQueue<string[]> Scopes { get; } = new();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synchronous token acquisition is not expected.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.Enqueue(requestContext.Scopes.ToArray());
            if (!allowToken) throw new InvalidOperationException("This fixture must not acquire a token.");
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}