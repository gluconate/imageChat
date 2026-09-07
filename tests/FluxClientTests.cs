using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;
using Azure.Identity;
using ImageChat;
using Xunit;
using static ImageChat.Tests.FluxTestFixture;

namespace ImageChat.Tests;

public class FluxClientTests
{
    [Theory]
    [InlineData("1024x1024", 1024, 1024)]
    [InlineData("1536x1024", 1536, 1024)]
    [InlineData("1024x1536", 1024, 1536)]
    [InlineData("1536x1536", 1536, 1536)]
    [InlineData("1920x1920", 1920, 1920)]
    public async Task GenerationSendsExactBflJsonAndBearerApiKey(string size, int width, int height)
    {
        var options = Options();
        var credential = new StubCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            AssertFluxRequest(request, options.Flux.ApiKey);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            AssertPayload(json.RootElement, "A green chair & a blue sky", width, height);
            return ImageResponse();
        });
        using var http = new HttpClient(handler);
        var result = await new FoundryClient(http, options, credential).GenerateAsync(
            new("A green chair & a blue sky", size, "default", null, ModelCatalog.Flux), default);
        Assert.Equal(CoreTests.Png, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(credential.Scopes);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData("image/png", "1024x1024", 1024, 1024)]
    [InlineData("image/png", "1536x1024", 1536, 1024)]
    [InlineData("image/png", "1024x1536", 1024, 1536)]
    [InlineData("image/png", "1536x1536", 1536, 1536)]
    [InlineData("image/png", "1920x1920", 1920, 1920)]
    [InlineData("image/jpeg", "1024x1024", 1024, 1024)]
    [InlineData("image/jpeg", "1536x1024", 1536, 1024)]
    [InlineData("image/jpeg", "1024x1536", 1024, 1536)]
    [InlineData("image/jpeg", "1536x1536", 1536, 1536)]
    [InlineData("image/jpeg", "1920x1920", 1920, 1920)]
    public async Task EditingUsesSameEndpointAndJsonWithRawBase64NotMultipartOrDataUrl(
        string contentType, string size, int width, int height)
    {
        var image = Reference(contentType);
        var options = Options();
        var credential = new StubCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            AssertFluxRequest(request, options.Flux.ApiKey);
            Assert.IsNotType<MultipartFormDataContent>(request.Content);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            AssertPayload(json.RootElement, "Make it blue", width, height, image);
            return ImageResponse();
        });
        using var http = new HttpClient(handler);
        var result = await new FoundryClient(http, options, credential).GenerateAsync(
            new("Make it blue", size, "default", image, ModelCatalog.Flux), default);
        Assert.Equal(CoreTests.Png, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(credential.Scopes);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntraUsesFluxSpecificScopeNotRootOrMaiAndNeverSendsApiKey(bool editing)
    {
        var options = Options();
        options.Flux.AuthMode = "DefaultAzureCredential";
        var credential = new StubCredential(allowToken: true);
        var handler = new StubHandler((request, _) =>
        {
            AssertFluxRequest(request, StubCredential.Token);
            Assert.DoesNotContain(options.Flux.ApiKey, request.Headers.ToString());
            return Task.FromResult(ImageResponse());
        });
        using var http = new HttpClient(handler);
        var result = await new FoundryClient(http, options, credential).GenerateAsync(
            new("Entra fixture", "1024x1024", "default", editing ? Reference("image/png") : null, ModelCatalog.Flux), default);
        Assert.Equal(CoreTests.Png, result.Bytes);
        Assert.Equal(new[] { options.Flux.TokenScope }, Assert.Single(credential.Scopes));
        Assert.NotEqual(options.TokenScope, options.Flux.TokenScope);
        Assert.NotEqual(options.Mai.TokenScope, options.Flux.TokenScope);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData("not-json private-marker", 200)]
    [InlineData("null", 200)]
    [InlineData("[]", 200)]
    [InlineData("{}", 200)]
    [InlineData("{\"data\":[]}", 200)]
    [InlineData("{\"data\":{}}", 200)]
    [InlineData("{\"data\":[null]}", 200)]
    [InlineData("{\"data\":[{},{}]}", 200)]
    [InlineData("{\"data\":[{\"b64_json\":123}]}", 200)]
    [InlineData("{\"data\":[{\"b64_json\":null}]}", 200)]
    [InlineData("{\"data\":[{\"b64_json\":\"private-marker!\"}]}", 200)]
    [InlineData("{\"data\":[{\"b64_json\":\"\"}]}", 200)]
    [InlineData("{\"data\":[{\"b64_json\":\"aGVsbG8=\"}]}", 200)]
    [InlineData("{\"data\":[{\"b64_json\":\"AAAAAAAAAAA=\"}]}", 200)]
    [InlineData("{\"data\":[{\"url\":\"https://private-marker.test/image?sig=secret\"}]}", 200)]
    [InlineData("{\"id\":\"private-marker\",\"status\":\"Pending\",\"polling_url\":\"https://private-marker.test/job\"}", 200)]
    [InlineData("{\"status\":\"Ready\",\"result\":{\"sample\":\"https://private-marker.test/image\"}}", 200)]
    [InlineData("{\"id\":\"private-marker\",\"status\":\"Pending\",\"polling_url\":\"https://private-marker.test/job\"}", 202)]
    [InlineData("", 202)]
    public async Task UnsupportedResponsesReturnSafe502WithoutDownloadingPollingOrExposingUrls(string body, int status)
    {
        var options = Options();
        var credential = new StubCredential();
        var handler = new StubHandler((request, _) =>
        {
            // Every attempted request is intercepted, including any accidental URL follow-up.
            AssertFluxRequest(request, options.Flux.ApiKey);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
            response.Headers.Location = new Uri("https://private-marker.test/download?sig=secret");
            response.Headers.Add("Operation-Location", "https://private-marker.test/poll?sig=secret");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, options, credential)
            .GenerateAsync(new("Private fixture prompt", "1024x1024", "default", null, ModelCatalog.Flux), default));
        Assert.Equal(502, exception.StatusCode);
        AssertSafeError(exception.Message, options);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(credential.Scopes);
    }

    [Theory]
    [InlineData(429, 429)]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(404, 502)]
    [InlineData(400, 502)]
    [InlineData(422, 502)]
    [InlineData(500, 502)]
    [InlineData(503, 502)]
    [InlineData(302, 502)]
    public async Task UpstreamErrorsAreSafeAndNeverRetriedOrRoutedToOtherConfiguredModels(int upstream, int expected)
    {
        var options = Options(); // Both potential fallback providers are configured, but must not be called.
        var handler = new StubHandler((request, _) =>
        {
            AssertFluxRequest(request, options.Flux.ApiKey);
            var response = new HttpResponseMessage((HttpStatusCode)upstream)
            {
                Content = new StringContent("private-marker " + options.Flux.Endpoint + " " + options.Flux.ApiKey)
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            response.Headers.Location = new Uri("https://private-marker.test/redirect");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, options, new StubCredential())
            .GenerateAsync(new("Private fixture prompt", "1024x1024", "default", null, ModelCatalog.Flux), default));
        Assert.Equal(expected, exception.StatusCode);
        AssertSafeError(exception.Message, options);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("3840x2160", "default", false)]
    [InlineData("3840x2160", "default", true)]
    [InlineData("2048x2048", "default", false)]
    [InlineData("2000x2000", "default", true)]
    [InlineData("auto", "default", false)]
    [InlineData("auto", "default", true)]
    [InlineData("1024x1024", "high", false)]
    [InlineData("1024x1024", "medium", true)]
    [InlineData("1024x1024", "low", false)]
    [InlineData("1024x1024", "", true)]
    public async Task UnsupportedSizeOrQualityIsRejectedBeforeTokenAcquisitionOrHttp(string size, string quality, bool editing)
    {
        var options = Options();
        options.Flux.AuthMode = "DefaultAzureCredential";
        var credential = new StubCredential();
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Validation must prevent HTTP."));
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, options, credential).GenerateAsync(
            new("Rejected fixture", size, quality, editing ? Reference("image/png") : null, ModelCatalog.Flux), default));
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(credential.Scopes);
    }

    [Theory]
    [InlineData("missing-endpoint", 503)]
    [InlineData("missing-key", 503)]
    [InlineData("missing-deployment", 503)]
    [InlineData("missing-version", 503)]
    [InlineData("bad-auth-mode", 503)]
    [InlineData("unknown-id", 400)]
    [InlineData("empty-id", 400)]
    public async Task InvalidConfigurationOrModelNeverFallsBackOrAcquiresCredentials(string scenario, int expected)
    {
        var options = Options();
        var model = ModelCatalog.Flux;
        switch (scenario)
        {
            case "missing-endpoint": options.Flux.Endpoint = ""; break;
            case "missing-key": options.Flux.ApiKey = ""; break;
            case "missing-deployment": options.Flux.Deployment = ""; break;
            case "missing-version": options.Flux.ApiVersion = ""; break;
            case "bad-auth-mode": options.Flux.AuthMode = "Bearer"; break;
            case "unknown-id": model = options.Flux.Deployment; break;
            case "empty-id": model = ""; break;
            default: throw new InvalidOperationException("Unknown fixture scenario.");
        }
        var credential = new StubCredential();
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Validation must prevent HTTP."));
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, options, credential)
            .GenerateAsync(new("No fallback", "1024x1024", "default", null, model), default));
        Assert.Equal(expected, exception.StatusCode);
        if (expected == 503) Assert.Contains("Foundry:Flux:", exception.Message);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(credential.Scopes);
    }

    [Fact]
    public async Task EntraAuthenticationFailureIsSafeAndDoesNotSendHttpOrFallback()
    {
        var options = Options();
        options.Flux.AuthMode = "DefaultAzureCredential";
        var credential = new StubCredential(allowToken: true, fail: true);
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Authentication failed before HTTP."));
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, options, credential)
            .GenerateAsync(new("Private fixture prompt", "1024x1024", "default", null, ModelCatalog.Flux), default));
        Assert.Equal(502, exception.StatusCode);
        AssertSafeError(exception.Message, options);
        Assert.Equal(new[] { options.Flux.TokenScope }, Assert.Single(credential.Scopes));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SharedClientKeepsThreeConcurrentConnectionsHeadersPayloadsAndOutputsIndependent()
    {
        var options = Options();
        var before = JsonSerializer.Serialize(options); // Exclusively synthetic configuration.
        var credential = new StubCredential();
        var started = Channel.CreateUnbounded<RequestSnapshot>();
        var gates = ModelIds.ToDictionary(id => id, _ => new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
        var outputs = ModelIds.Select((id, index) => (id, bytes: CoreTests.Png.Concat(new[] { (byte)(40 + index) }).ToArray()))
            .ToDictionary(pair => pair.id, pair => pair.bytes);
        var handler = new StubHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var id = ModelIds.Single(id => Deployment(id) == json.RootElement.GetProperty("model").GetString());
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
            started.Writer.TryWrite(new(id, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("api-key", out var keys) ? Assert.Single(keys) : null,
                request.Headers.Authorization?.ToString(), json.RootElement.Clone()));
            return ImageResponse(await gates[id].Task.WaitAsync(token));
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Add("X-Fixture", "unchanged");
        var headersBefore = http.DefaultRequestHeaders.ToString();
        var client = new FoundryClient(http, options, credential);
        var tasks = ModelIds.ToDictionary(id => id, id => client.GenerateAsync(
            new("Only " + id, "1024x1024", Quality(id), null, id), default));
        try
        {
            var snapshots = new List<RequestSnapshot>();
            foreach (var _ in ModelIds) snapshots.Add(await started.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
            Assert.All(tasks.Values, task => Assert.False(task.IsCompleted));
            foreach (var id in ModelIds)
            {
                var snapshot = Assert.Single(snapshots, item => item.Id == id);
                Assert.Equal("Only " + id, snapshot.Body.GetProperty("prompt").GetString());
                if (id == ModelCatalog.Flux)
                {
                    Assert.Equal(FluxUri, snapshot.Uri);
                    Assert.Null(snapshot.ApiKey);
                    Assert.Equal("Bearer " + options.Flux.ApiKey, snapshot.Authorization);
                    AssertPayload(snapshot.Body, "Only " + id, 1024, 1024);
                }
                else
                {
                    Assert.Null(snapshot.Authorization);
                    Assert.Equal(id == ModelCatalog.Gpt ? options.ApiKey : options.Mai.ApiKey, snapshot.ApiKey);
                    Assert.Equal(id == ModelCatalog.Gpt
                        ? "https://gpt-fixture.test/openai/deployments/fixture-gpt-deployment/images/generations?api-version=2025-04-01-preview"
                        : "https://mai-fixture.test/mai/v1/images/generations", snapshot.Uri);
                    AssertFields(snapshot.Body, id == ModelCatalog.Gpt
                        ? ["model", "prompt", "size", "quality", "n", "output_format"]
                        : ["model", "prompt", "width", "height"]);
                }
            }
            Assert.Equal(before, JsonSerializer.Serialize(options));
            Assert.Equal(headersBefore, http.DefaultRequestHeaders.ToString());
            foreach (var id in ModelIds.Reverse())
            {
                gates[id].SetResult(outputs[id]);
                var result = await tasks[id].WaitAsync(Deadline);
                Assert.Equal(outputs[id], result.Bytes);
                Assert.Equal("image/png", result.ContentType);
                foreach (var pending in ModelIds.Where(other => !gates[other].Task.IsCompleted))
                    Assert.False(tasks[pending].IsCompleted);
            }
            Assert.Equal(3, handler.Calls);
            Assert.Empty(credential.Scopes);
            Assert.Equal(before, JsonSerializer.Serialize(options));
            Assert.Equal(headersBefore, http.DefaultRequestHeaders.ToString());
        }
        finally
        {
            foreach (var id in ModelIds) gates[id].TrySetResult(outputs[id]);
            await Task.WhenAll(tasks.Values).WaitAsync(Deadline);
        }
    }

    private static void AssertPayload(JsonElement body, string prompt, int width, int height, ImageData? reference = null)
    {
        var fields = new[] { "model", "prompt", "width", "height", "output_format", "num_images" };
        AssertFields(body, reference == null ? fields : fields.Append("input_image").ToArray());
        Assert.Equal(FluxDeployment, body.GetProperty("model").GetString());
        Assert.Equal(prompt, body.GetProperty("prompt").GetString());
        Assert.Equal(width, body.GetProperty("width").GetInt32());
        Assert.Equal(height, body.GetProperty("height").GetInt32());
        Assert.Equal("png", body.GetProperty("output_format").GetString());
        Assert.Equal(1, body.GetProperty("num_images").GetInt32());
        if (reference != null)
        {
            var encoded = body.GetProperty("input_image").GetString()!;
            Assert.Equal(Convert.ToBase64String(reference.Bytes), encoded);
            Assert.Equal(reference.Bytes, Convert.FromBase64String(encoded));
            Assert.DoesNotContain("data:", encoded);
        }
    }

    private sealed record RequestSnapshot(string Id, string Uri, string? ApiKey, string? Authorization, JsonElement Body);
}

// Shared by the two FLUX test classes. All hosts, keys, tokens and images are synthetic;
// the handler intercepts every HTTP request and never delegates to a network transport.
internal static class FluxTestFixture
{
    internal const string FluxDeployment = "fixture-flux-deployment";
    internal const string FluxUri = "https://flux-fixture.test/providers/blackforestlabs/v1/flux-2-pro?api-version=preview";
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static readonly string[] ModelIds = [ModelCatalog.Gpt, ModelCatalog.Mai, ModelCatalog.Flux];
    internal static readonly string[] Sizes = ["1024x1024", "1536x1024", "1024x1536", "1536x1536", "1920x1920"];

    internal static FoundryOptions Options()
    {
        var options = new FoundryOptions();
        Configure(options);
        return options;
    }

    internal static void Configure(FoundryOptions options)
    {
        options.Endpoint = "https://gpt-fixture.test/";
        options.Deployment = Deployment(ModelCatalog.Gpt);
        options.ApiKey = "gpt-only-fixture-key";
        options.TokenScope = "https://gpt-scope-fixture.test/.default";
        options.Mai = new MaiOptions
        {
            Endpoint = "https://mai-fixture.test/", Deployment = Deployment(ModelCatalog.Mai),
            ApiKey = "mai-only-fixture-key", TokenScope = "https://mai-scope-fixture.test/.default"
        };
        options.Flux = new FluxOptions
        {
            Endpoint = "https://flux-fixture.test/", Deployment = FluxDeployment,
            ApiKey = "flux-only-fixture-key", TokenScope = "https://flux-scope-fixture.test/.default", TimeoutSeconds = 47
        };
    }

    internal static string Deployment(string id) => id switch
    {
        ModelCatalog.Gpt => "fixture-gpt-deployment",
        ModelCatalog.Mai => "fixture-mai-deployment",
        ModelCatalog.Flux => FluxDeployment,
        _ => throw new InvalidOperationException("Unknown fixture model.")
    };

    internal static string Quality(string id) => id == ModelCatalog.Gpt ? "medium" : "default";
    internal static ImageData Reference(string type) => new(
        type == "image/png" ? CoreTests.Png : [255, 216, 255, 224, 0, 16, 74, 70, 73, 70, 0, 1], type);

    internal static void AssertFluxRequest(HttpRequestMessage request, string bearer)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(FluxUri, request.RequestUri!.AbsoluteUri);
        Assert.False(request.Headers.Contains("api-key"));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(bearer, request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    internal static void AssertFields(JsonElement body, params string[] fields) =>
        Assert.Equal(fields.OrderBy(name => name).ToArray(),
            body.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());

    internal static void AssertSafeError(string text, FoundryOptions options)
    {
        foreach (var secret in new[] { "private-marker", "Private fixture prompt", "https://", "sig=secret",
                     options.ApiKey, options.Mai.ApiKey, options.Flux.ApiKey })
            Assert.DoesNotContain(secret, text);
    }

    internal static HttpResponseMessage ImageResponse(byte[]? bytes = null) => new(HttpStatusCode.OK)
    {
        // The documented Azure synchronous envelope; URL outputs are deliberately not supported.
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(bytes ?? CoreTests.Png) } }
        }), Encoding.UTF8, "application/json")
    };

    internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return action(request, cancellationToken);
        }
    }

    internal sealed class StubCredential(bool allowToken = false, bool fail = false) : TokenCredential
    {
        internal const string Token = "only-a-flux-fixture-entra-token";
        internal ConcurrentQueue<string[]> Scopes { get; } = new();
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synchronous authentication is not expected.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.Enqueue(requestContext.Scopes.ToArray());
            if (!allowToken) throw new InvalidOperationException("This fixture must not acquire credentials.");
            if (fail) throw new AuthenticationFailedException("private-marker https://private-marker.test/token");
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}