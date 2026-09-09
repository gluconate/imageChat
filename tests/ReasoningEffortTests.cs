using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;
using ImageChat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ImageChat.Tests;

public sealed class ReasoningEffortTests
{
    private const string FixtureKey = "reasoning-fixture-key-not-a-credential";
    private const string FixtureDeployment = "reasoning-fixture-deployment";
    private static readonly string[] KnownEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    [Fact]
    public void DefaultsPreservePositionalInputAndIndependentOptions()
    {
        var first = Options();
        var second = Options();
        Assert.Equal(new[] { "low", "medium", "high" }, first.ReasoningEfforts);
        Assert.NotSame(first.ReasoningEfforts, second.ReasoningEfforts);
        Assert.Null(new TextChatInput([new("user", "Old positional call")]).ReasoningEffort);
        Assert.Equal(2048, first.MaxOutputTokens);
        Assert.Equal(12000, ResponsesChatClient.MaxResponseCharacters);
        first.ReasoningEfforts[0] = "none";
        Assert.Equal(new[] { "low", "medium", "high" }, second.ReasoningEfforts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public void NullAndDefaultNormalizeToOmission(string? effort)
    {
        var options = Options();
        Assert.Null(options.ValidateReasoningEffort(effort));
        options.ReasoningEfforts = [];
        Assert.Null(options.ConfigurationError());
        Assert.Null(options.ValidateReasoningEffort(effort));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void EachKnownEffortRequiresExplicitOperatorApproval(string effort)
    {
        var options = Options();
        options.ReasoningEfforts = [effort];
        Assert.Null(options.ConfigurationError());
        Assert.Equal(effort, options.ValidateReasoningEffort(effort));
        options.ReasoningEfforts = [];
        Assert.Equal(400, Assert.Throws<AppException>(() => options.ValidateReasoningEffort(effort)).StatusCode);
    }

    [Theory]
    [InlineData(true, "low")]
    [InlineData(true, "medium")]
    [InlineData(true, "high")]
    [InlineData(false, "low")]
    [InlineData(false, "medium")]
    [InlineData(false, "high")]
    public async Task ExplicitEffortAddsOnlyNestedReasoningToExactPayload(bool streaming, string effort)
    {
        await AssertClientPayload(streaming, effort);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "default")]
    [InlineData(false, null)]
    [InlineData(false, "default")]
    public async Task DefaultPayloadRetainsExactlyTheBackwardCompatibleFields(bool streaming, string? effort)
    {
        await AssertClientPayload(streaming, effort);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task AdditionalWhitelistedEffortsAreSentLiterallyIncludingNone(string effort)
    {
        await AssertClientPayload(false, effort, [effort]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("unknown")]
    [InlineData("High")]
    [InlineData("LOW")]
    [InlineData("HIGH")]
    [InlineData("Default")]
    [InlineData("DEFAULT")]
    [InlineData("high ")]
    [InlineData(" high")]
    [InlineData("high,low")]
    [InlineData("high\n")]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task InvalidOrUnconfiguredEffortRejectsBeforeTokenOrHttp(string effort)
    {
        var options = Options();
        options.AuthMode = "DefaultAzureCredential";
        Assert.Equal(400, Assert.Throws<AppException>(() => options.ValidateReasoningEffort(effort)).StatusCode);
        var credential = new RejectingCredential();
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP call."));
        using var http = Http(handler);
        var client = new ResponsesChatClient(http, options, credential);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(client, Input("Invalid", effort)));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(0, credential.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("null-array")]
    [InlineData("null-entry")]
    [InlineData("unknown")]
    [InlineData("default")]
    [InlineData("empty")]
    [InlineData("case")]
    [InlineData("too-many")]
    public async Task InvalidConfigurationRejectsEvenDefaultBeforeTokenOrHttp(string kind)
    {
        var options = Options();
        options.AuthMode = "DefaultAzureCredential";
        options.ReasoningEfforts = InvalidConfiguration(kind);
        Assert.NotNull(options.ConfigurationError());
        var credential = new RejectingCredential();
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP call."));
        using var http = Http(handler);
        var client = new ResponsesChatClient(http, options, credential);
        foreach (var effort in new string?[] { null, "default", "high" })
        {
            Assert.Equal(503, Assert.Throws<AppException>(() => options.ValidateReasoningEffort(effort)).StatusCode);
            var error = await Assert.ThrowsAsync<AppException>(() => Collect(client, Input("Invalid configuration", effort)));
            Assert.Equal(503, error.StatusCode);
        }
        Assert.Equal(0, credential.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledEffortsAllowOnlyDefaultAndNeverSendReasoning(bool streaming)
    {
        var options = Options(streaming);
        options.ReasoningEfforts = [];
        var credential = new RejectingCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            AssertPayload(json.RootElement, Input("Default only"), options, null);
            return ProviderResponse(streaming);
        });
        using var http = Http(handler);
        var client = new ResponsesChatClient(http, options, credential);
        foreach (var effort in new string?[] { null, "default" })
            Assert.Equal(new TextChatEvent("completed", "Fixture answer"), Assert.Single(await Collect(client, Input("Default only", effort))));
        Assert.Equal(2, handler.Calls);
        foreach (var effort in KnownEfforts)
        {
            var error = await Assert.ThrowsAsync<AppException>(() => Collect(client, Input("Disabled", effort)));
            Assert.Equal(400, error.StatusCode);
        }
        Assert.Equal(2, handler.Calls);
        Assert.Equal(0, credential.Calls);
        Assert.Empty(options.ReasoningEfforts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentSharedClientKeepsEffortsRequestLocalAndDoesNotLeakIntoDefault(bool streaming)
    {
        var options = Options(streaming);
        var before = JsonSerializer.Serialize(options);
        var originalEfforts = options.ReasoningEfforts;
        var started = Channel.CreateUnbounded<string>();
        var lowRelease = Signal();
        var highRelease = Signal();
        var captures = new ConcurrentDictionary<string, JsonElement>();
        var credential = new RejectingCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement.Clone();
            var prompt = root.GetProperty("input")[0].GetProperty("content").GetString()!;
            Assert.True(captures.TryAdd(prompt, root));
            started.Writer.TryWrite(prompt);
            if (prompt == "Low request") await lowRelease.Task.WaitAsync(token);
            if (prompt == "High request") await highRelease.Task.WaitAsync(token);
            return ProviderResponse(streaming);
        });
        using var http = Http(handler);
        http.DefaultRequestHeaders.Add("X-Fixture", "unchanged");
        var headers = http.DefaultRequestHeaders.ToString();
        var client = new ResponsesChatClient(http, options, credential);
        var low = Collect(client, Input("Low request", "low"));
        var high = Collect(client, Input("High request", "high"));
        try
        {
            var entered = new[] { await started.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout),
                await started.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout) };
            Assert.Equal(new[] { "High request", "Low request" }, entered.OrderBy(value => value));
            Assert.False(low.IsCompleted);
            Assert.False(high.IsCompleted);
            AssertPayload(captures["Low request"], Input("Low request", "low"), options, "low");
            AssertPayload(captures["High request"], Input("High request", "high"), options, "high");
            highRelease.TrySetResult(true);
            Assert.Single(await high.WaitAsync(TestTimeout));
            Assert.False(low.IsCompleted);
            await Collect(client, Input("Default while low is pending"));
            AssertPayload(captures["Default while low is pending"], Input("Default while low is pending"), options, null);
            lowRelease.TrySetResult(true);
            Assert.Single(await low.WaitAsync(TestTimeout));
            Assert.Equal(3, handler.Calls);
            Assert.Equal(0, credential.Calls);
            Assert.Same(originalEfforts, options.ReasoningEfforts);
            Assert.Equal(before, JsonSerializer.Serialize(options));
            Assert.Equal(headers, http.DefaultRequestHeaders.ToString());
        }
        finally
        {
            lowRelease.TrySetResult(true);
            highRelease.TrySetResult(true);
            await Task.WhenAll(low, high).WaitAsync(TestTimeout);
        }
    }

    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ExplicitEffortProviderFailureNeverRetriesOrFallsBack(int status)
    {
        var options = Options();
        var credential = new RejectingCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            AssertPayload(json.RootElement, Input("No retry", "high"), options, "high");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private provider detail") };
        });
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<AppException>(() => Collect(new ResponsesChatClient(http, options, credential), Input("No retry", "high")));
        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain("private provider detail", error.Message);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, credential.Calls);
        Assert.Equal(new[] { "low", "medium", "high" }, options.ReasoningEfforts);
    }

    [Theory]
    [InlineData(true, 12000)]
    [InlineData(true, 12001)]
    [InlineData(false, 12000)]
    [InlineData(false, 12001)]
    public async Task ExplicitEffortPreservesConfiguredTokensAndCanonicalOutputLimit(bool streaming, int characters)
    {
        var options = Options(streaming);
        options.MaxOutputTokens = 3072;
        var handler = new StubHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            AssertPayload(json.RootElement, Input("Limit", "high"), options, "high");
            return ProviderResponse(streaming, new string('x', characters));
        });
        using var http = Http(handler);
        var client = new ResponsesChatClient(http, options, new RejectingCredential());
        if (characters == 12000)
            Assert.Equal(characters, Assert.Single(await Collect(client, Input("Limit", "high"))).Text.Length);
        else
            Assert.Equal(502, (await Assert.ThrowsAsync<AppException>(() => Collect(client, Input("Limit", "high")))).StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(3072, options.MaxOutputTokens);
        Assert.Equal(12000, ResponsesChatClient.MaxResponseCharacters);
    }

    [Fact]
    public async Task ExplicitEffortDoesNotRelaxCumulativeStreamingDeltaLimit()
    {
        var body = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = new string('x', 12000) }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = "x" }) + "\n\n";
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        }));
        using var http = Http(handler);
        var client = new ResponsesChatClient(http, Options(), new RejectingCredential());
        await using var events = client.GenerateAsync(Input("Limit", "high"), CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(new TextChatEvent("delta", new string('x', 12000)), events.Current);
        var error = await Assert.ThrowsAsync<AppException>(async () => { await events.MoveNextAsync(); });
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonAndMultipartHighReachCapturedInput(bool multipart)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var content = Submission(multipart, "Explain clearly", "high");
        using var response = await http.PostAsync(TextUrl(chat.Id), content);
        await AssertCompleted(response);
        var input = Assert.Single(fixture.Text.Inputs);
        Assert.Equal("high", input.ReasoningEffort);
        Assert.Equal(new ChatInputMessage("user", "Explain clearly"), Assert.Single(input.Messages));
        Assert.Equal(1, fixture.Store.BeginCalls);
        var saved = await GetChat(http, chat.Id);
        Assert.Equal(2, saved.Messages.Count);
        Assert.Equal("completed", saved.Messages[1].Status);
        fixture.AssertNoLiveCalls();
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, null, true)]
    [InlineData(false, "default", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "default", true)]
    public async Task MissingNullOrDefaultEndpointEffortReachesGeneratorAsNull(bool multipart, string? effort, bool includeEffort)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var content = Submission(multipart, "Compatible request", effort, includeEffort);
        using var response = await http.PostAsync(TextUrl(chat.Id), content);
        await AssertCompleted(response);
        Assert.Null(Assert.Single(fixture.Text.Inputs).ReasoningEffort);
        Assert.Equal(1, fixture.Store.BeginCalls);
        fixture.AssertNoLiveCalls();
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, " ")]
    [InlineData(true, " ")]
    [InlineData(false, "High")]
    [InlineData(true, "High")]
    [InlineData(false, "Default")]
    [InlineData(true, "Default")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "high,low")]
    [InlineData(true, "high,low")]
    [InlineData(false, " high ")]
    [InlineData(true, " high ")]
    public async Task InvalidEndpointEffortRejectsBeforeBeginTurnAndProvider(bool multipart, string effort)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var content = Submission(multipart, "Do not append", effort);
        using var response = await http.PostAsync(TextUrl(chat.Id), content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUntouched(fixture, http, chat.Id);
        // Validation must also release its lease; an explicit valid request can follow.
        using var retry = Submission(multipart, "Now valid", "high");
        using var accepted = await http.PostAsync(TextUrl(chat.Id), retry);
        await AssertCompleted(accepted);
        Assert.Equal(1, fixture.Store.BeginCalls);
        Assert.Equal("high", Assert.Single(fixture.Text.Inputs).ReasoningEffort);
    }

    [Theory]
    [InlineData("low", "high")]
    [InlineData("high", "high")]
    [InlineData("default", "default")]
    [InlineData("", "high")]
    public async Task DuplicateMultipartEffortRejectsEvenIdenticalValuesBeforeBeginTurn(string first, string second)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var form = Assert.IsType<MultipartFormDataContent>(Submission(true, "Do not append", first));
        form.Add(new StringContent(second), "reasoningEffort");
        using var response = await http.PostAsync(TextUrl(chat.Id), form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUntouched(fixture, http, chat.Id);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("null-array")]
    [InlineData("null-entry")]
    [InlineData("unknown")]
    public async Task InvalidEndpointConfigurationReturns503WithoutAppending(string kind)
    {
        await using var fixture = new Fixture(options => options.ReasoningEfforts = InvalidConfiguration(kind));
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var content = Submission(false, "No turn", "default");
        using var response = await http.PostAsync(TextUrl(chat.Id), content);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertUntouched(fixture, http, chat.Id);
        using var status = await http.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("chatModel").GetProperty("configured").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledEndpointEffortsRejectExplicitButStillAcceptOmittedAndDefault(bool multipart)
    {
        await using var fixture = new Fixture(options => options.ReasoningEfforts = []);
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using (var content = Submission(multipart, "Disabled high", "high"))
        using (var response = await http.PostAsync(TextUrl(chat.Id), content))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUntouched(fixture, http, chat.Id);
        foreach (var effort in new string?[] { null, "default" })
        {
            using var content = Submission(multipart, "Default works", effort, effort != null);
            using var response = await http.PostAsync(TextUrl(chat.Id), content);
            await AssertCompleted(response);
        }
        Assert.Equal(2, fixture.Store.BeginCalls);
        Assert.Equal(2, fixture.Text.Inputs.Length);
        Assert.All(fixture.Text.Inputs, input => Assert.Null(input.ReasoningEffort));
        Assert.Empty(fixture.Options.ReasoningEfforts);
        fixture.AssertNoLiveCalls();
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("disabled")]
    [InlineData("custom")]
    public async Task StatusAdvertisesDefaultAndOnlyConfiguredEffortsInOrder(string configuration)
    {
        string[] efforts = configuration switch
        {
            "disabled" => [],
            "custom" => ["max", "none", "xhigh", "minimal", "high", "medium", "low"],
            _ => ["low", "medium", "high"]
        };
        await using var fixture = new Fixture(options => options.ReasoningEfforts = efforts.ToArray());
        using var http = await fixture.Login();
        using var response = await http.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var model = json.RootElement.GetProperty("chatModel");
        Assert.True(model.GetProperty("configured").GetBoolean());
        Assert.Equal("default", model.GetProperty("defaultReasoningEffort").GetString());
        Assert.Equal(new[] { "default" }.Concat(efforts), model.GetProperty("reasoningEfforts").EnumerateArray().Select(value => value.GetString()));
        AssertFields(model, "id", "label", "mode", "configured", "deployment", "authMode", "streaming", "supportsImages",
            "maxPromptCharacters", "maxContextCharacters", "maxContextMessages", "maxContextImages", "maxContextImageBytes",
            "reasoningEfforts", "defaultReasoningEffort", "message");
        Assert.Equal(ChatModelOptions.ModelId, model.GetProperty("id").GetString());
        Assert.Equal("chat", model.GetProperty("mode").GetString());
        Assert.Equal(FixtureDeployment, model.GetProperty("deployment").GetString());
        Assert.DoesNotContain(FixtureKey, body);
        Assert.DoesNotContain("\"apiKey\"", body);
        Assert.DoesNotContain("\"tokenScope\"", body);
        Assert.Equal(efforts, fixture.Options.ReasoningEfforts);
        Assert.Equal(0, fixture.Store.BeginCalls);
        Assert.Empty(fixture.Text.Inputs);
        fixture.AssertNoLiveCalls();
    }

    [Fact]
    public async Task DifferentChatsCaptureDifferentEffortsConcurrentlyWithoutSharedState()
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var lowChat = await CreateChat(http);
        var highChat = await CreateChat(http);
        var lowRelease = Signal();
        var highRelease = Signal();
        fixture.Text.Wait = (input, token) => input.Messages[^1].Text switch
        {
            "Low chat" => lowRelease.Task.WaitAsync(token),
            "High chat" => highRelease.Task.WaitAsync(token),
            _ => Task.CompletedTask
        };
        var before = JsonSerializer.Serialize(fixture.Options);
        using var lowContent = Submission(true, "Low chat", "low");
        using var highContent = Submission(false, "High chat", "high");
        var low = http.PostAsync(TextUrl(lowChat.Id), lowContent);
        var high = http.PostAsync(TextUrl(highChat.Id), highContent);
        try
        {
            var first = await fixture.Text.Started.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
            var second = await fixture.Text.Started.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
            Assert.NotSame(first, second);
            var inputs = new[] { first, second }.ToDictionary(input => Assert.Single(input.Messages).Text);
            Assert.Equal("low", inputs["Low chat"].ReasoningEffort);
            Assert.Equal("high", inputs["High chat"].ReasoningEffort);
            Assert.False(low.IsCompleted);
            Assert.False(high.IsCompleted);
            Assert.Equal(2, fixture.Store.BeginCalls);
            highRelease.TrySetResult(true);
            await AssertCompleted(await high.WaitAsync(TestTimeout));
            Assert.False(low.IsCompleted);
            Assert.Equal("completed", (await GetChat(http, highChat.Id)).Messages[^1].Status);
            Assert.Equal("pending", (await GetChat(http, lowChat.Id)).Messages[^1].Status);
            using var followup = Submission(false, "No effort on next request", null, false);
            using var next = await http.PostAsync(TextUrl(highChat.Id), followup);
            await AssertCompleted(next);
            Assert.Null(fixture.Text.Inputs[^1].ReasoningEffort);
            Assert.Equal("low", inputs["Low chat"].ReasoningEffort);
            Assert.Equal("high", inputs["High chat"].ReasoningEffort);
            lowRelease.TrySetResult(true);
            await AssertCompleted(await low.WaitAsync(TestTimeout));
            Assert.Equal(3, fixture.Text.Inputs.Length);
            Assert.Equal(3, fixture.Store.BeginCalls);
            Assert.Equal(before, JsonSerializer.Serialize(fixture.Options));
            fixture.AssertNoLiveCalls();
        }
        finally
        {
            lowRelease.TrySetResult(true);
            highRelease.TrySetResult(true);
            var responses = await Task.WhenAll(low, high).WaitAsync(TestTimeout);
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task ReasoningEndpointStillRequiresRealAuthentication()
    {
        await using var fixture = new Fixture();
        using var owner = await fixture.Login();
        var chat = await CreateChat(owner);
        using var anonymous = fixture.NewClient();
        using var content = Submission(false, "Anonymous", "high");
        using var response = await anonymous.PostAsync(TextUrl(chat.Id), content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUntouched(fixture, owner, chat.Id);
    }

    private static ChatModelOptions Options(bool streaming = true) => new()
    {
        Endpoint = "https://reasoning.example.test/openai/v1/responses",
        Deployment = FixtureDeployment,
        AuthMode = "ApiKey",
        ApiKey = FixtureKey,
        Streaming = streaming
    };

    private static string[] InvalidConfiguration(string kind) => kind switch
    {
        "duplicate" => ["low", "low"],
        "null-array" => null!,
        "null-entry" => ["low", null!],
        "unknown" => ["low", "unsupported"],
        "default" => ["default"],
        "empty" => [""],
        "case" => ["High"],
        "too-many" => [.. KnownEfforts, "low"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static TextChatInput Input(string prompt, string? effort = null) => new([new("user", prompt)], effort);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpClient Http(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<List<TextChatEvent>> Collect(ITextChatGenerator client, TextChatInput input)
    {
        var events = new List<TextChatEvent>();
        await foreach (var item in client.GenerateAsync(input, CancellationToken.None)) events.Add(item);
        return events;
    }

    private static HttpResponseMessage ProviderResponse(bool streaming, string text = "Fixture answer")
    {
        var completed = new { status = "completed", output = new[] { new { type = "message", role = "assistant",
            content = new[] { new { type = "output_text", text } } } } };
        var body = streaming
            ? "data: " + JsonSerializer.Serialize(new { type = "response.completed", response = completed }) + "\n\n"
            : JsonSerializer.Serialize(completed);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
        };
    }

    private static async Task AssertClientPayload(bool streaming, string? effort, string[]? configured = null)
    {
        var options = Options(streaming);
        options.MaxOutputTokens = 3072;
        if (configured != null) options.ReasoningEfforts = configured;
        var before = JsonSerializer.Serialize(options);
        // Exercise the original positional constructor, not just an explicit null argument.
        var input = new TextChatInput([new("user", "A prompt: <>& 🌍\ntext"), new("assistant", "Prior answer"), new("user", "Continue")]);
        if (effort != null) input = input with { ReasoningEffort = effort };
        var credential = new RejectingCredential();
        var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://reasoning.example.test/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal(FixtureKey, Assert.Single(request.Headers.GetValues("api-key")));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(streaming ? "text/event-stream" : "application/json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            AssertPayload(json.RootElement, input, options, effort is null or "default" ? null : effort);
            return ProviderResponse(streaming);
        });
        using var http = Http(handler);
        var headers = http.DefaultRequestHeaders.ToString();
        var result = await Collect(new ResponsesChatClient(http, options, credential), input);
        Assert.Equal(new TextChatEvent("completed", "Fixture answer"), Assert.Single(result));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, credential.Calls);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        Assert.Equal(headers, http.DefaultRequestHeaders.ToString());
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    private static void AssertPayload(JsonElement root, TextChatInput input, ChatModelOptions options, string? effort)
    {
        string[] fields = ["model", "input", "stream", "store", "max_output_tokens"];
        AssertFields(root, effort == null ? fields : [.. fields, "reasoning"]);
        Assert.Equal(options.Deployment, root.GetProperty("model").GetString());
        Assert.Equal(options.Streaming, root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(options.MaxOutputTokens, root.GetProperty("max_output_tokens").GetInt32());
        // Exact field sets also forbid flattened reasoning_effort, mode, summary and temperature.
        if (effort != null)
        {
            var reasoning = root.GetProperty("reasoning");
            AssertFields(reasoning, "effort");
            Assert.Equal(effort, reasoning.GetProperty("effort").GetString());
        }
        var messages = root.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(input.Messages.Count, messages.Length);
        for (var index = 0; index < messages.Length; index++)
        {
            AssertFields(messages[index], "role", "content");
            Assert.Equal(input.Messages[index].Role, messages[index].GetProperty("role").GetString());
            Assert.Equal(input.Messages[index].Text, messages[index].GetProperty("content").GetString());
        }
    }

    private static void AssertFields(JsonElement element, params string[] names) =>
        Assert.Equal(names.OrderBy(name => name), element.EnumerateObject().Select(property => property.Name).OrderBy(name => name));

    private static string TextUrl(string id) => $"/api/chats/{id}/text-messages";

    private static HttpContent Submission(bool multipart, string prompt, string? effort, bool includeEffort = true)
    {
        if (!multipart)
        {
            var body = new Dictionary<string, object?> { ["prompt"] = prompt, ["model"] = ChatModelOptions.ModelId };
            if (includeEffort) body["reasoningEffort"] = effort;
            return JsonContent.Create(body);
        }
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent(ChatModelOptions.ModelId), "model");
        if (includeEffort) form.Add(new StringContent(effort ?? ""), "reasoningEffort");
        return form;
    }

    private static async Task<Chat> CreateChat(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync("/api/chats", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Chat>())!;
    }

    private static async Task<Chat> GetChat(HttpClient http, string id) => (await http.GetFromJsonAsync<Chat>($"/api/chats/{id}"))!;

    private static async Task AssertCompleted(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync().WaitAsync(TestTimeout);
        Assert.Equal(new[] { "event: started", "event: completed" },
            body.Split('\n').Where(line => line.StartsWith("event: ", StringComparison.Ordinal)).Select(line => line.TrimEnd('\r')));
        Assert.DoesNotContain(FixtureKey, body);
    }

    private static async Task AssertUntouched(Fixture fixture, HttpClient http, string id)
    {
        Assert.Equal(0, fixture.Store.BeginCalls);
        Assert.Empty(fixture.Text.Inputs);
        Assert.Empty((await GetChat(http, id)).Messages);
        var summaries = (await http.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!;
        Assert.False(Assert.Single(summaries, item => item.Id == id).IsGenerating);
        fixture.AssertNoLiveCalls();
        // Rejected requests may have telemetry, but must never have a persisted chat turn.
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return send(request, cancellationToken);
        }
    }

    private sealed class RejectingCredential : TokenCredential
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Tests must never acquire credentials.");
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Tests must never acquire credentials.");
        }
    }

    private sealed class StubTextGenerator : ITextChatGenerator
    {
        private readonly ConcurrentQueue<TextChatInput> inputs = new();
        public TextChatInput[] Inputs => inputs.ToArray();
        public Channel<TextChatInput> Started { get; } = Channel.CreateUnbounded<TextChatInput>();
        public Func<TextChatInput, CancellationToken, Task>? Wait { get; set; }
        public async IAsyncEnumerable<TextChatEvent> GenerateAsync(TextChatInput input,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            inputs.Enqueue(input);
            var wait = Wait;
            Started.Writer.TryWrite(input);
            if (wait != null) await wait(input, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new("completed", "Fixture answer");
        }
    }

    private sealed class RejectingImageGenerator : IImageGenerator
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task<ImageData> GenerateAsync(GenerationInput input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Reasoning tests must never generate images.");
        }
    }

    // Delegate to real local persistence, but observe BeginTurn itself instead of inferring
    // its absence from a final history snapshot. Effort is intentionally not a history field.
    private sealed class ObservedStore(ChatStore inner) : IChatStore
    {
        private int beginCalls;
        public int BeginCalls => Volatile.Read(ref beginCalls);
        public string BeginTurn(Chat chat, string text, string size, string quality, ImageData? attachment,
            string? telemetryId = null, string operation = "unknown", string? model = null, string? deployment = null)
        {
            Interlocked.Increment(ref beginCalls);
            return inner.BeginTurn(chat, text, size, quality, attachment, telemetryId, operation, model, deployment);
        }
        public List<ChatSummary> List(string? ownerId = null) => inner.List(ownerId);
        public Chat Create(string? ownerId = null) => inner.Create(ownerId);
        public Chat Get(string id, string? ownerId = null) => inner.Get(id, ownerId);
        public ImageData? GetImage(string id, string? ownerId = null) => inner.GetImage(id, ownerId);
        public ImageData? LatestImage(Chat chat) => inner.LatestImage(chat);
        public void SetRequestModel(string requestId, string model, string deployment) => inner.SetRequestModel(requestId, model, deployment);
        public void FinishTurn(string chatId, string messageId, ImageData? image, string? error) => inner.FinishTurn(chatId, messageId, image, error);
        public void UpdateTextTurn(string chatId, string messageId, string text) => inner.UpdateTextTurn(chatId, messageId, text);
        public void FinishTextTurn(string chatId, string messageId, string text, string? error, int httpStatus = 200) => inner.FinishTextTurn(chatId, messageId, text, error, httpStatus);
        public void Delete(string id, string? ownerId = null) => inner.Delete(id, ownerId);
        public void ClaimLegacyChats(string ownerId) => inner.ClaimLegacyChats(ownerId);
        public string StartRequest(string userId, string username, string? ip) => inner.StartRequest(userId, username, ip);
        public void EndRequest(string id, int httpStatus) => inner.EndRequest(id, httpStatus);
        public TelemetryReport Report(TelemetryFilter filter) => inner.Report(filter);
    }

    private sealed class Fixture : WebApplicationFactory<Program>
    {
        private const string Password = "Only-for-reasoning-tests-Password123!";
        private readonly string directory = Path.Combine(FindRepo(), "tests", ".cache", "reasoning-efforts", Guid.NewGuid().ToString("N"));
        private readonly RejectingCredential credential = new();
        private readonly RejectingImageGenerator images = new();
        public ChatModelOptions Options { get; } = ReasoningEffortTests.Options();
        public StubTextGenerator Text { get; } = new();
        public ObservedStore Store => Services.GetRequiredService<ObservedStore>();

        public Fixture(Action<ChatModelOptions>? configure = null) => configure?.Invoke(Options);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Program can load an absolute config path before service overrides. Refuse that
            // environment rather than reading operator settings or changing process globals.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IMAGECHAT_CONFIG_FILE")))
                throw new InvalidOperationException("Reasoning fixtures require IMAGECHAT_CONFIG_FILE to be unset.");
            Directory.CreateDirectory(directory);
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(directory);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:Provider"] = "Local",
                    ["Storage:Directory"] = directory,
                    ["ReverseProxy:Enabled"] = "false",
                    ["ReverseProxy:Provider"] = "",
                    ["Security:RequireHttps"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(directory));
                services.RemoveAll<UserStore>();
                services.AddSingleton(_ =>
                {
                    var users = new UserStore(directory);
                    users.Bootstrap("reasoningadmin", Password);
                    return users;
                });
                services.RemoveAll<ChatStore>();
                services.AddSingleton(_ => new ChatStore(directory));
                services.AddSingleton<ObservedStore>();
                services.RemoveAll<IChatStore>();
                services.AddSingleton<IChatStore>(provider => provider.GetRequiredService<ObservedStore>());
                services.RemoveAll<FoundryOptions>();
                services.AddSingleton(new FoundryOptions { Endpoint = "https://image.example.test", ApiKey = "" });
                services.RemoveAll<ChatModelOptions>();
                services.AddSingleton(Options);
                services.RemoveAll<ITextChatGenerator>();
                services.AddSingleton<ITextChatGenerator>(Text);
                services.RemoveAll<IImageGenerator>();
                services.AddSingleton<IImageGenerator>(images);
                services.RemoveAll<TokenCredential>();
                services.AddSingleton<TokenCredential>(credential);
            });
        }

        public HttpClient NewClient()
        {
            var http = CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("http://localhost")
            });
            http.Timeout = TestTimeout;
            http.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
            Assert.Same(Options, Services.GetRequiredService<ChatModelOptions>());
            Assert.Same(Text, Services.GetRequiredService<ITextChatGenerator>());
            Assert.Same(Store, Services.GetRequiredService<IChatStore>());
            Assert.Same(images, Services.GetRequiredService<IImageGenerator>());
            Assert.Same(credential, Services.GetRequiredService<TokenCredential>());
            return http;
        }

        public async Task<HttpClient> Login()
        {
            var http = NewClient();
            try
            {
                using var response = await http.PostAsJsonAsync("/api/auth/login", new { username = "reasoningadmin", password = Password });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.Contains("Set-Cookie"));
                return http;
            }
            catch
            {
                http.Dispose();
                throw;
            }
        }

        public void AssertNoLiveCalls()
        {
            Assert.Equal(0, images.Calls);
            Assert.Equal(0, credential.Calls);
        }

        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) LocalFileIO.RetrySharing(() => { Directory.Delete(directory, true); return 0; });
            }
        }

        private static string FindRepo()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "ImageChat.csproj"))) current = current.Parent;
            return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}