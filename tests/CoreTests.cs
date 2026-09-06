using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using ImageChat;
using Xunit;

namespace ImageChat.Tests;

public class CoreTests
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=");

    [Theory]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void EmptyInputIsRejected(string text, bool hasImage) =>
        Assert.Throws<AppException>(() => InputRules.ValidatePrompt(text, hasImage));

    [Fact]
    public void ImageOnlyGetsExplicitDefaultPrompt() => Assert.Equal(InputRules.ImageOnlyPrompt, InputRules.ValidatePrompt("", true));

    [Fact]
    public void OversizedPromptIsRejected() => Assert.Throws<AppException>(() => InputRules.ValidatePrompt(new string('a', 8001), false));

    [Fact]
    public void ImageTypeIsDetectedFromBytes()
    {
        Assert.Equal("image/png", InputRules.ValidateImage(Png).ContentType);
        Assert.Throws<AppException>(() => InputRules.ValidateImage(Encoding.UTF8.GetBytes("<svg>not a png</svg>")));
        Assert.Throws<AppException>(() => InputRules.ValidateImage(new byte[InputRules.MaxImageBytes + 1]));
    }

    [Theory]
    [InlineData("2048x2048", "medium")]
    [InlineData("1024x1024", "ultra")]
    public void UnsupportedSettingsRejected(string size, string quality) => Assert.Throws<AppException>(() => InputRules.ValidateOptions(size, quality));

    [Fact]
    public void HistoryIsBoundedAndExcludesFailures()
    {
        var chat = new Chat("1", "test", "", "", []);
        void Turn(string prompt, string status)
        {
            chat.Messages.Add(new("u", "user", prompt, null, "", "", "", "completed", null));
            chat.Messages.Add(new("a", "assistant", "caption", null, "", "", "", status, null));
        }
        Turn("Original scene", "completed");
        Turn("Failed instruction", "failed");
        for (var i = 0; i < 30; i++) Turn(new string('x', 8000), "completed");
        var actual = InputRules.BuildPrompt(chat, "Make it brighter");
        Assert.Contains("Original scene", actual);
        Assert.DoesNotContain("Failed instruction", actual);
        Assert.EndsWith("Current request:\nMake it brighter", actual);
        Assert.True(actual.Length < 32000);
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("https://example.test/api/projects/foo")]
    [InlineData("https://user:pass@example.test")]
    public void InvalidEndpointCannotBeUsed(string endpoint) => Assert.NotNull(new FoundryOptions { Endpoint = endpoint, ApiKey = "test" }.ConfigurationError());

    [Fact]
    public async Task GenerationUsesJsonDeploymentAndBase64()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("https://example.test/openai/deployments/my-deployment/images/generations?api-version=2025-04-01-preview", request.RequestUri!.ToString());
            Assert.Equal("test-key", request.Headers.GetValues("api-key").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("my-deployment", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("A green chair", body.RootElement.GetProperty("prompt").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("n").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("response_format", out _));
            return ImageResponse();
        });
        using var http = new HttpClient(handler);
        var client = new FoundryClient(http, Options(), new NoCredential());
        var result = await client.GenerateAsync(new("A green chair", "1024x1024", "medium", null), default);
        Assert.Equal(Png, result.Bytes);
    }

    [Fact]
    public async Task EditingUsesMultipartImage()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Contains("/images/edits", request.RequestUri!.ToString());
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToList();
            Assert.Contains(parts, part => part.Headers.ContentDisposition!.Name!.Trim('"') == "image");
            var prompt = parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "prompt");
            Assert.Equal("Make it blue", await prompt.ReadAsStringAsync());
            return ImageResponse();
        });
        using var http = new HttpClient(handler);
        await new FoundryClient(http, Options(), new NoCredential()).GenerateAsync(new("Make it blue", "1024x1024", "medium", new(Png, "image/png")), default);
    }

    [Fact]
    public async Task UpstreamErrorsAreSafeAndNotRetried()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private upstream body") });
        }));
        var exception = await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, Options(), new NoCredential())
            .GenerateAsync(new("prompt", "1024x1024", "medium", null), default));
        Assert.Equal(429, exception.StatusCode);
        Assert.DoesNotContain("private", exception.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvalidBase64IsRejected()
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"b64_json\":\"not valid base64!\"}]}") })));
        await Assert.ThrowsAsync<AppException>(() => new FoundryClient(http, Options(), new NoCredential())
            .GenerateAsync(new("prompt", "1024x1024", "medium", null), default));
    }

    private static FoundryOptions Options() => new() { Endpoint = "https://example.test", Deployment = "my-deployment", ApiKey = "test-key" };
    private static HttpResponseMessage ImageResponse() => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(Png) } } })) };
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }
    private sealed class NoCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("Test must not authenticate.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("Test must not authenticate.");
    }
}