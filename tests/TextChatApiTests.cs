using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ImageChat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ImageChat.Tests;

public sealed class TextChatApiTests
{
    private const string FixtureKey = "fixture-only-chat-key-not-a-credential";
    private const string FixtureDeployment = "fixture-text-deployment";
    private const string ProviderSecret = "fixture-provider-secret-must-not-leak";
    private const string FailureMessage = "The chat response failed or was interrupted. The partial reply is saved. Check history before retrying; another request may incur another charge.";
    private const string TimeoutMessage = "The chat request timed out or the server stopped. The partial reply is saved. Check history before retrying; another request may incur another charge.";
    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task StatusExposesIndependentChatReadinessAndSafeDefaultBudgets(bool imageReady, bool chatReady)
    {
        await using var fixture = new Fixture(
            chat => chat.ApiKey = chatReady ? FixtureKey : "",
            image => image.ApiKey = imageReady ? "not-a-real-key" : "");
        using var http = await fixture.Login();
        using var response = await http.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var status = document.RootElement;
        Assert.Equal(imageReady, status.GetProperty("configured").GetBoolean());
        Assert.Equal(ModelCatalog.Gpt, status.GetProperty("defaultModel").GetString());
        var imageModels = status.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(imageReady, Assert.Single(imageModels, model => model.GetProperty("id").GetString() == ModelCatalog.Gpt)
            .GetProperty("configured").GetBoolean());
        Assert.DoesNotContain(imageModels, model => model.GetProperty("id").GetString() == ChatModelOptions.ModelId);

        var chat = status.GetProperty("chatModel");
        Assert.Equal(ChatModelOptions.ModelId, chat.GetProperty("id").GetString());
        Assert.Equal("chat", chat.GetProperty("mode").GetString());
        Assert.Equal(chatReady, chat.GetProperty("configured").GetBoolean());
        Assert.Equal(FixtureDeployment, chat.GetProperty("deployment").GetString());
        Assert.Equal("ApiKey", chat.GetProperty("authMode").GetString());
        Assert.True(chat.GetProperty("streaming").GetBoolean());
        Assert.True(chat.GetProperty("supportsImages").GetBoolean());
        Assert.Equal(64000, chat.GetProperty("maxPromptCharacters").GetInt32());
        Assert.Equal(256000, chat.GetProperty("maxContextCharacters").GetInt32());
        Assert.Equal(200, chat.GetProperty("maxContextMessages").GetInt32());
        Assert.Equal(8, chat.GetProperty("maxContextImages").GetInt32());
        Assert.Equal(32 * 1024 * 1024, chat.GetProperty("maxContextImageBytes").GetInt32());
        string[] publicFields = ["id", "label", "mode", "configured", "deployment", "authMode", "streaming",
            "supportsImages", "maxPromptCharacters", "maxContextCharacters", "maxContextMessages",
            "maxContextImages", "maxContextImageBytes", "message"];
        Assert.Equal(publicFields.OrderBy(name => name), chat.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        AssertNoProviderDetails(body);
        Assert.Empty(fixture.Text.Inputs);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Fact]
    public async Task AnonymousTextSubmissionRequiresAuthenticationEvenWithCsrfHeader()
    {
        await using var fixture = new Fixture();
        using var owner = await fixture.Login();
        var chat = await CreateChat(owner);
        using var anonymous = fixture.NewClient();
        using var response = await anonymous.PostAsJsonAsync(TextUrl(chat.Id), new { prompt = "Do not submit", model = ChatModelOptions.ModelId });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty((await GetChat(owner, chat.Id)).Messages);
        Assert.Empty(fixture.Text.Inputs);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    public async Task MissingOrInvalidCsrfHeaderRejectsAuthenticatedTextSubmission(string? header)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        http.DefaultRequestHeaders.Remove("X-ImageChat-Request");
        if (header != null) http.DefaultRequestHeaders.Add("X-ImageChat-Request", header);
        using var response = await http.PostAsJsonAsync(TextUrl(chat.Id), new { prompt = "Do not submit", model = ChatModelOptions.ModelId });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await GetChat(http, chat.Id)).Messages);
        Assert.Empty(fixture.Text.Inputs);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Fact]
    public async Task ForeignOwnerCannotReadOrAppendToAnExistingChat()
    {
        await using var fixture = new Fixture();
        using var owner = await fixture.Login();
        var chat = await CreateChat(owner);
        using var other = await fixture.Login("testuser");
        using var response = await other.PostAsJsonAsync(TextUrl(chat.Id), new { prompt = "Foreign turn", model = ChatModelOptions.ModelId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var read = await other.GetAsync($"/api/chats/{chat.Id}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Empty((await GetChat(owner, chat.Id)).Messages);
        Assert.Empty(fixture.Text.Inputs);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData("A prompt", "not-a-supported-model")]
    [InlineData("A prompt", ModelCatalog.Gpt)]
    [InlineData("A prompt", "")]
    [InlineData("", ChatModelOptions.ModelId)]
    [InlineData(" \r\n\t ", ChatModelOptions.ModelId)]
    public async Task UnknownModelOrBlankPromptReturns400WithoutAppendingOrCallingEitherGenerator(string prompt, string model)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var response = await http.PostAsJsonAsync(TextUrl(chat.Id), new { prompt, model });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertNoProviderDetails(await response.Content.ReadAsStringAsync());
        Assert.Empty((await GetChat(http, chat.Id)).Messages);
        Assert.Empty(fixture.Text.Inputs);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Fact]
    public async Task ImageOnlyMultipartUsesChatFallbackAndPassesValidatedBytes()
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var form = TextForm(" \n\t", CoreTests.Png);
        using var response = await http.PostAsync(TextUrl(chat.Id), form);
        var events = await ReadEvents(response);
        Assert.Equal(new[] { "started", "completed" }, events.Select(item => item.Type));
        AssertEnvelope(events[0], chat.Id, "pending");
        AssertEnvelope(events[1], chat.Id, "completed");
        var message = Assert.Single(Assert.Single(fixture.Text.Inputs).Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal(TextChatContext.ImageOnlyPrompt, message.Text);
        Assert.NotEqual(InputRules.ImageOnlyPrompt, message.Text);
        AssertImage(message, CoreTests.Png, "image/png");
        var saved = await GetChat(http, chat.Id);
        Assert.Equal(TextChatContext.ImageOnlyPrompt, saved.Messages[0].Text);
        Assert.NotNull(saved.Messages[0].ImageUrl);
        Assert.Equal(CoreTests.Png, await http.GetByteArrayAsync(saved.Messages[0].ImageUrl!));
        Assert.Null(saved.Messages[1].ImageUrl);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Fact]
    public async Task ImagesDisabledRejectsAttachmentButLeavesChatAvailableForText()
    {
        await using var fixture = new Fixture(options => options.SupportsImages = false);
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var form = TextForm("Describe it", CoreTests.Png);
        using var response = await http.PostAsync(TextUrl(chat.Id), form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await GetChat(http, chat.Id)).Messages);
        Assert.Empty(fixture.Text.Inputs);
        var events = await PostText(http, chat.Id, "Text still works");
        AssertEnvelope(events[^1], chat.Id, "completed");
        Assert.Null(Assert.Single(Assert.Single(fixture.Text.Inputs).Messages).Images);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full64000CharacterPromptSurvivesJsonOrMultipartSubmissionAndGet(bool multipart)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        var prompt = LongPrompt("long");
        Assert.Equal(64000, prompt.Length);
        Assert.True(prompt.Length > InputRules.MaxPromptCharacters);
        Assert.True(prompt.Length > ResponsesChatClient.MaxResponseCharacters);
        using HttpContent content = multipart ? TextForm(prompt) : JsonContent.Create(new { prompt, model = ChatModelOptions.ModelId });
        using var response = await http.PostAsync(TextUrl(chat.Id), content);
        var events = await ReadEvents(response);
        Assert.Equal(new[] { "started", "completed" }, events.Select(item => item.Type));
        AssertEnvelope(events[0], chat.Id, "pending");
        AssertEnvelope(events[^1], chat.Id, "completed");
        Assert.Equal(prompt, Assert.Single(Assert.Single(fixture.Text.Inputs).Messages).Text);
        var saved = await GetChat(http, chat.Id);
        Assert.Equal(prompt, saved.Messages[0].Text);
        Assert.Equal(prompt[..64], saved.Title);
        Assert.Equal(prompt, new ChatStore(fixture.Parent.DataDirectory).Get(chat.Id).Messages[0].Text);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64000)]
    public async Task PromptAboveConfiguredLimitReturns400BeforeGeneration(int limit)
    {
        await using var fixture = new Fixture(options => options.MaxPromptCharacters = limit);
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        using var response = await http.PostAsJsonAsync(TextUrl(chat.Id), new { prompt = new string('x', limit + 1), model = ChatModelOptions.ModelId });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await GetChat(http, chat.Id)).Messages);
        Assert.Empty(fixture.Text.Inputs);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Fact]
    public async Task ContextIncludesCurrentAndPriorImagesAndAssistantTextButExcludesFailedTurns()
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        byte[] originalUpload = [255, 216, 255, 224];
        byte[] currentUpload = [255, 216, 255, 225];
        using (var imageForm = ApiTests.Form("Draw the original scene"))
        {
            imageForm.Add(new ByteArrayContent(originalUpload), "image", "original.jpg");
            using var imageResponse = await http.PostAsync($"/api/chats/{chat.Id}/messages", imageForm);
            Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
        }

        fixture.Text.Enqueue(new Reply([new("completed", "Earlier explanation")]));
        using (var form = TextForm("Explain the original scene", CoreTests.Png))
        using (var response = await http.PostAsync(TextUrl(chat.Id), form))
            AssertEnvelope((await ReadEvents(response))[^1], chat.Id, "completed");

        fixture.Text.Enqueue(new Reply([new("delta", "Failed partial must not become history")], failure: new AppException(502, ProviderSecret)));
        using (var form = TextForm("Failed prompt must not become history", currentUpload))
        using (var response = await http.PostAsync(TextUrl(chat.Id), form))
            Assert.Equal("failed", (await ReadEvents(response))[^1].Type);

        using (var form = TextForm("Compare this new image", currentUpload))
        using (var response = await http.PostAsync(TextUrl(chat.Id), form))
            AssertEnvelope((await ReadEvents(response))[^1], chat.Id, "completed");

        Assert.Equal(3, fixture.Text.Calls);
        var messages = fixture.Text.Inputs[2].Messages;
        Assert.Collection(messages,
            message => { Assert.Equal("user", message.Role); Assert.Equal("Draw the original scene", message.Text); AssertImage(message, originalUpload, "image/jpeg"); },
            message => { Assert.Equal("user", message.Role); Assert.Equal(TextChatContext.GeneratedImageReference, message.Text); AssertImage(message, CoreTests.Png, "image/png"); },
            message => { Assert.Equal("user", message.Role); Assert.Equal("Explain the original scene", message.Text); AssertImage(message, CoreTests.Png, "image/png"); },
            message => { Assert.Equal("assistant", message.Role); Assert.Equal("Earlier explanation", message.Text); Assert.Null(message.Images); },
            message => { Assert.Equal("user", message.Role); Assert.Equal("Compare this new image", message.Text); AssertImage(message, currentUpload, "image/jpeg"); });
        Assert.DoesNotContain(messages, message => message.Text.Contains("Failed", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Text.Contains("Here's your image", StringComparison.Ordinal));
        var saved = await GetChat(http, chat.Id);
        Assert.Equal(8, saved.Messages.Count);
        Assert.Equal("failed", saved.Messages[5].Status);
        Assert.Equal("Failed prompt must not become history", saved.Messages[4].Text);
        Assert.Equal("completed", saved.Messages[^1].Status);
        Assert.Single(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExceededCharacterOrMessageBudgetDropsWholeTurnsAndReportsTruncation(bool characterBudget)
    {
        await using var fixture = new Fixture(options =>
        {
            options.MaxPromptCharacters = 32;
            options.MaxContextCharacters = characterBudget ? 40 : 256000;
            options.MaxContextMessages = characterBudget ? 200 : 2;
        });
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        fixture.Text.Enqueue(new Reply([new("completed", "Previous reply")]));
        AssertEnvelope((await PostText(http, chat.Id, "Previous prompt"))[0], chat.Id, "pending");
        var events = await PostText(http, chat.Id, "Current prompt");
        AssertEnvelope(events[0], chat.Id, "pending", truncated: true);
        AssertEnvelope(events[^1], chat.Id, "completed", truncated: true);
        Assert.Equal("Current prompt", Assert.Single(fixture.Text.Inputs[1].Messages).Text);
        Assert.Equal(4, (await GetChat(http, chat.Id)).Messages.Count);
    }

    [Fact]
    public async Task LongStoredHistoryUsesCompactSseEnvelopesWhileGetKeepsEveryFullPrompt()
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        var prompts = Enumerable.Range(0, 6).Select(index => LongPrompt(index.ToString("D4"))).ToArray();
        foreach (var prompt in prompts)
        {
            var earlier = await PostText(http, chat.Id, prompt);
            Assert.Equal(new[] { "started", "completed" }, earlier.Select(item => item.Type));
            Assert.Equal(2, EnvelopeChat(earlier[0]).Messages.Count);
            Assert.Equal("", EnvelopeChat(earlier[0]).Messages[0].Text);
        }
        var current = LongPrompt("last");
        var events = await PostText(http, chat.Id, current);
        AssertEnvelope(events[0], chat.Id, "pending", truncated: true);
        AssertEnvelope(events[^1], chat.Id, "completed", truncated: true);
        var input = fixture.Text.Inputs[^1];
        Assert.Equal(new[] { prompts[4], "Fixture answer", prompts[5], "Fixture answer", current }, input.Messages.Select(message => message.Text));
        Assert.True(input.Messages.Sum(message => message.Text.Length) <= fixture.Options.MaxContextCharacters);
        var saved = await GetChat(http, chat.Id);
        Assert.Equal(14, saved.Messages.Count);
        Assert.Equal(prompts.Append(current), saved.Messages.Where(message => message.Role == "user").Select(message => message.Text));
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(saved)) > 256 * 1024);
        Assert.Equal(7, fixture.Text.Calls);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Fact]
    public async Task ResponseHeadersReadDeliversDeltaBeforeCompletionAndBusyLeaseIsPerChatAcrossBothApis()
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var busy = await CreateChat(http);
        var other = await CreateChat(http);
        var reply = new Reply([new("delta", "Visible before completion")], [new("completed", "Canonical final")], blocked: true);
        fixture.Text.Enqueue(reply);
        using var request = new HttpRequestMessage(HttpMethod.Post, TextUrl(busy.Id))
        {
            Content = JsonContent.Create(new { prompt = "Hold this chat", model = ChatModelOptions.ModelId })
        };
        var sending = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        HttpResponseMessage? response = null;
        StreamReader? reader = null;
        try
        {
            response = await sending.WaitAsync(TestTimeout);
            AssertSseResponse(response);
            reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var started = Assert.IsType<SseEvent>(await ReadEvent(reader));
            Assert.Equal("started", started.Type);
            var startedChat = AssertEnvelope(started, busy.Id, "pending");
            var delta = Assert.IsType<SseEvent>(await ReadEvent(reader));
            Assert.Equal("delta", delta.Type);
            Assert.Equal(startedChat.Messages[1].Id, delta.Data.GetProperty("messageId").GetString());
            Assert.Equal("Visible before completion", delta.Data.GetProperty("text").GetString());
            await reply.Waiting.Task.WaitAsync(TestTimeout);
            Assert.False(reply.Release.Task.IsCompleted);
            Assert.False(reply.Finished.Task.IsCompleted);
            Assert.Equal("pending", (await GetChat(http, busy.Id)).Messages[^1].Status);

            using (var duplicate = await http.PostAsJsonAsync(TextUrl(busy.Id), new { prompt = "Duplicate text", model = ChatModelOptions.ModelId }))
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            using (var image = ApiTests.Form("Duplicate image"))
            using (var duplicate = await http.PostAsync($"/api/chats/{busy.Id}/messages", image))
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            using (var deletion = await http.DeleteAsync($"/api/chats/{busy.Id}"))
                Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode);
            Assert.Equal(2, (await GetChat(http, busy.Id)).Messages.Count);
            Assert.Equal(1, fixture.Text.Calls);
            Assert.Empty(fixture.Parent.Generator.Inputs);

            var otherEvents = await PostText(http, other.Id, "Another chat works now");
            AssertEnvelope(otherEvents[^1], other.Id, "completed");
            Assert.Equal("Another chat works now", Assert.Single(fixture.Text.Inputs[1].Messages).Text);
            Assert.False(reply.Finished.Task.IsCompleted);
            var summaries = (await http.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!;
            Assert.True(summaries.Single(summary => summary.Id == busy.Id).IsGenerating);
            Assert.False(summaries.Single(summary => summary.Id == other.Id).IsGenerating);

            reply.Release.TrySetResult(true);
            var completed = Assert.IsType<SseEvent>(await ReadEvent(reader));
            Assert.Equal("completed", completed.Type);
            Assert.Equal("Canonical final", AssertEnvelope(completed, busy.Id, "completed").Messages[1].Text);
            Assert.Null(await ReadEvent(reader));
            await reply.Finished.Task.WaitAsync(TestTimeout);
            Assert.Equal("Canonical final", (await GetChat(http, busy.Id)).Messages[1].Text);
            Assert.False((await http.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!.Single(summary => summary.Id == busy.Id).IsGenerating);
            AssertEnvelope((await PostText(http, busy.Id, "Now continue"))[^1], busy.Id, "completed");
            Assert.Equal(3, fixture.Text.Calls);
            using var deleted = await http.DeleteAsync($"/api/chats/{busy.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        finally
        {
            // Release even when an assertion or HTTP read fails; never leave the server
            // or a test factory waiting for a simulated billable operation to finish.
            reply.Release.TrySetResult(true);
            try
            {
                response ??= await sending.WaitAsync(TestTimeout);
                if (reader != null) await reader.ReadToEndAsync().WaitAsync(TestTimeout);
                else await response.Content.CopyToAsync(Stream.Null).WaitAsync(TestTimeout);
            }
            finally
            {
                reader?.Dispose();
                response?.Dispose();
            }
        }
    }

    [Fact]
    public async Task CanonicalFinalReplacesDeltasPersistsAcrossReloadAndRecordsChatTelemetryWithZeroImages()
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        fixture.Text.Enqueue(new Reply([new("delta", "Long draft "), new("delta", "that must be replaced"), new("completed", "Final answer")]));
        var events = await PostText(http, chat.Id, "Remember the full question");
        Assert.Equal(new[] { "started", "delta", "delta", "completed" }, events.Select(item => item.Type));
        AssertEnvelope(events[0], chat.Id, "pending");
        var canonical = AssertEnvelope(events[^1], chat.Id, "completed");
        Assert.Equal("Final answer", canonical.Messages[1].Text);
        Assert.All(events, item => Assert.Equal(canonical.Messages[1].Id, item.Data.GetProperty("messageId").GetString()));
        var saved = await GetChat(http, chat.Id);
        Assert.Equal("Remember the full question", saved.Messages[0].Text);
        Assert.Equal(canonical.Messages[1], saved.Messages[1]);
        Assert.Null(saved.Messages[1].ImageUrl);
        Assert.Null(saved.Messages[1].Error);
        var reopened = new ChatStore(fixture.Parent.DataDirectory);
        Assert.Equal(saved.Messages.ToArray(), reopened.Get(chat.Id).Messages.ToArray());
        Assert.False(Assert.Single(reopened.List()).IsGenerating);

        var report = (await http.GetFromJsonAsync<TelemetryReport>("/api/admin/telemetry"))!;
        var item = Assert.Single(report.Items);
        Assert.Equal(1, report.Summary.Requests);
        Assert.Equal(0, report.Summary.Pending);
        Assert.Equal(0, report.Summary.Failed);
        Assert.Equal(0, report.Summary.TotalImages);
        Assert.Equal(0, report.Summary.GeneratedImages);
        Assert.Equal(0, report.Summary.EditedImages);
        Assert.Equal("chat", item.Operation);
        Assert.Equal("completed", item.Status);
        Assert.Equal(200, item.HttpStatus);
        Assert.Equal(0, item.ImageCount);
        Assert.Equal("testadmin", item.Username);
        Assert.Equal("127.0.0.1", item.Ip);
        Assert.NotNull(item.UserId);
        Assert.NotNull(item.CompletedAt);
        Assert.Equal(ChatModelOptions.ModelId, item.Model);
        Assert.Equal(FixtureDeployment, item.Deployment);
        Assert.Equal(item, Assert.Single(reopened.Report(new(null, null, null)).Items));
        Assert.Equal(1, fixture.Text.Calls);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData("app", 418, 502)]
    [InlineData("app", 503, 502)]
    [InlineData("unexpected", 0, 502)]
    [InlineData("cancelled", 0, 504)]
    public async Task ProviderFailureSavesPartialWithGenericErrorAndUnlocksOnlyForExplicitRetry(string kind, int providerStatus, int savedStatus)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        var detail = $"{ProviderSecret}; api-key={FixtureKey}; arbitrary provider detail";
        Exception failure = kind switch
        {
            "app" => new AppException(providerStatus, detail),
            "cancelled" => new OperationCanceledException(detail),
            _ => new InvalidOperationException(detail)
        };
        fixture.Text.Enqueue(new Reply([new("delta", "Partial reply")], failure: failure));
        var events = await PostText(http, chat.Id, "Failed user question");
        // HTTP headers have already been sent: failure is an SSE event, not a new
        // HTTP status or the arbitrary status/message carried by an AppException.
        Assert.Equal(new[] { "started", "delta", "failed" }, events.Select(item => item.Type));
        var expectedError = kind == "cancelled" ? TimeoutMessage : FailureMessage;
        var failed = AssertEnvelope(events[^1], chat.Id, "failed");
        Assert.Equal(expectedError, events[^1].Data.GetProperty("error").GetString());
        Assert.Equal(expectedError, failed.Messages[1].Error);
        Assert.Equal("Partial reply", failed.Messages[1].Text);
        Assert.All(events, item => AssertNoProviderDetails(item.Data.GetRawText()));
        var saved = await GetChat(http, chat.Id);
        Assert.Equal("Failed user question", saved.Messages[0].Text);
        Assert.Equal(failed.Messages[1], saved.Messages[1]);
        AssertNoProviderDetails(JsonSerializer.Serialize(saved));
        Assert.False(Assert.Single((await http.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!).IsGenerating);
        var report = (await http.GetFromJsonAsync<TelemetryReport>("/api/admin/telemetry"))!;
        var item = Assert.Single(report.Items);
        Assert.Equal("chat", item.Operation);
        Assert.Equal("failed", item.Status);
        Assert.Equal(savedStatus, item.HttpStatus);
        Assert.Equal(0, item.ImageCount);
        Assert.Equal(1, report.Summary.Failed);
        Assert.Equal(0, report.Summary.Pending);
        AssertNoProviderDetails(JsonSerializer.Serialize(report));
        Assert.Equal(1, fixture.Text.Calls); // GET/reload never resubmits the provider POST.

        var retry = await PostText(http, chat.Id, "Explicit retry");
        AssertEnvelope(retry[^1], chat.Id, "completed");
        Assert.Equal(2, fixture.Text.Calls);
        Assert.Equal("Explicit retry", Assert.Single(fixture.Text.Inputs[1].Messages).Text);
        Assert.Equal(4, (await GetChat(http, chat.Id)).Messages.Count);
        using var deleted = await http.DeleteAsync($"/api/chats/{chat.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbruptProviderEofWithoutCompletedEventIsFailureEvenAfterVisibleText(bool withDelta)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        fixture.Text.Enqueue(new Reply(withDelta ? [new("delta", "Unconfirmed partial")] : []));
        var events = await PostText(http, chat.Id, "EOF test");
        Assert.Equal(withDelta ? new[] { "started", "delta", "failed" } : new[] { "started", "failed" }, events.Select(item => item.Type));
        var failed = AssertEnvelope(events[^1], chat.Id, "failed");
        Assert.Equal(withDelta ? "Unconfirmed partial" : "", failed.Messages[1].Text);
        Assert.Equal(FailureMessage, failed.Messages[1].Error);
        Assert.Equal(failed.Messages[1], (await GetChat(http, chat.Id)).Messages[1]);
        var report = (await http.GetFromJsonAsync<TelemetryReport>("/api/admin/telemetry"))!;
        Assert.Equal(502, Assert.Single(report.Items).HttpStatus);
        Assert.Equal(1, report.Summary.Failed);
        Assert.Equal(0, report.Summary.TotalImages);
        Assert.Equal(1, fixture.Text.Calls);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData("first-delta")]
    [InlineData("cumulative-deltas")]
    [InlineData("oversized-final")]
    [InlineData("empty-final")]
    public async Task OutputLimitOrInvalidFinalFailsWithoutForwardingInvalidTextOrLosingValidPartial(string kind)
    {
        await using var fixture = new Fixture();
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        Assert.Equal(12000, ResponsesChatClient.MaxResponseCharacters);
        TextChatEvent[] script = kind switch
        {
            "first-delta" => [new("delta", new string('x', 12001)), new("completed", "Must not succeed")],
            "cumulative-deltas" => [new("delta", "p"), new("delta", new string('x', 12000)), new("completed", "Must not succeed")],
            "oversized-final" => [new("delta", "p"), new("completed", new string('x', 12001))],
            _ => [new("delta", "p"), new("completed", "")]
        };
        fixture.Text.Enqueue(new Reply(script));
        var events = await PostText(http, chat.Id, "Output limit test");
        Assert.Equal("started", events[0].Type);
        Assert.Equal("failed", events[^1].Type);
        Assert.DoesNotContain(events, item => item.Type == "completed");
        var partial = kind == "first-delta" ? "" : "p";
        Assert.Equal(partial, string.Concat(events.Where(item => item.Type == "delta").Select(item => item.Data.GetProperty("text").GetString())));
        var failed = AssertEnvelope(events[^1], chat.Id, "failed");
        Assert.Equal(partial, failed.Messages[1].Text);
        Assert.Equal(FailureMessage, failed.Messages[1].Error);
        Assert.Equal(failed.Messages[1], (await GetChat(http, chat.Id)).Messages[1]);
        Assert.Equal(502, Assert.Single((await http.GetFromJsonAsync<TelemetryReport>("/api/admin/telemetry"))!.Items).HttpStatus);
        Assert.Equal(1, fixture.Text.Calls);
        AssertEnvelope((await PostText(http, chat.Id, "Explicit retry after invalid output"))[^1], chat.Id, "completed");
        Assert.Equal(2, fixture.Text.Calls);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedOnlyProviderReplySupportsBothStreamingSettingsAndExactly12000Characters(bool streaming)
    {
        await using var fixture = new Fixture(options => options.Streaming = streaming);
        using var http = await fixture.Login();
        var chat = await CreateChat(http);
        var answer = new string('x', 12000);
        fixture.Text.Enqueue(new Reply([new("completed", answer)]));
        var events = await PostText(http, chat.Id, "Completed-only response");
        Assert.Equal(new[] { "started", "completed" }, events.Select(item => item.Type));
        AssertEnvelope(events[0], chat.Id, "pending");
        Assert.Equal(answer, AssertEnvelope(events[1], chat.Id, "completed").Messages[1].Text);
        Assert.Equal(answer, (await GetChat(http, chat.Id)).Messages[1].Text);
        Assert.Equal(1, fixture.Text.Calls);
        Assert.Empty(fixture.Parent.Generator.Inputs);
    }

    private static string TextUrl(string id) => $"/api/chats/{id}/text-messages";

    // Non-whitespace endings preserve the exact UTF-16 length through prompt.Trim().
    private static string LongPrompt(string prefix) => prefix + string.Concat(Enumerable.Repeat("文🙂x", 15999));

    private static MultipartFormDataContent TextForm(string prompt, byte[]? image = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent(ChatModelOptions.ModelId), "model" }
        };
        if (image != null)
        {
            var upload = new ByteArrayContent(image);
            upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(upload, "image", "fixture.bin"); // Signature, not filename/MIME, selects PNG/JPEG.
        }
        return form;
    }

    private static async Task<Chat> CreateChat(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync("/api/chats", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<Chat>(await response.Content.ReadFromJsonAsync<Chat>());
    }

    private static async Task<Chat> GetChat(HttpClient http, string id)
    {
        using var response = await http.GetAsync($"/api/chats/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<Chat>(await response.Content.ReadFromJsonAsync<Chat>());
    }

    private static async Task<List<SseEvent>> PostText(HttpClient http, string id, string prompt)
    {
        using var response = await http.PostAsJsonAsync(TextUrl(id), new { prompt, model = ChatModelOptions.ModelId });
        return await ReadEvents(response);
    }

    private static void AssertSseResponse(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    private static async Task<List<SseEvent>> ReadEvents(HttpResponseMessage response)
    {
        AssertSseResponse(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var events = new List<SseEvent>();
        while (await ReadEvent(reader) is { } item) events.Add(item);
        Assert.NotEmpty(events);
        return events;
    }

    private static async Task<SseEvent?> ReadEvent(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        string? type = null;
        var data = new StringBuilder();
        var wire = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line == null)
            {
                Assert.Equal(0, wire.Length); // A truncated final SSE frame is not a valid completion.
                return null;
            }
            wire.Append(line).Append('\n');
            if (line.Length == 0)
            {
                Assert.False(string.IsNullOrEmpty(type));
                Assert.True(Encoding.UTF8.GetByteCount(wire.ToString()) < 256 * 1024, "Each SSE event must remain below 256 KiB, including its envelope.");
                using var json = JsonDocument.Parse(data.ToString());
                return new SseEvent(type!, json.RootElement.Clone());
            }
            if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                if (data.Length != 0) data.Append('\n');
                data.Append(line[6..]);
            }
        }
    }

    private static Chat EnvelopeChat(SseEvent item) => Assert.IsType<Chat>(item.Data.GetProperty("chat")
        .Deserialize<Chat>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static Chat AssertEnvelope(SseEvent item, string chatId, string status, bool truncated = false)
    {
        Assert.True(item.Data.GetProperty("partialHistory").GetBoolean());
        Assert.Equal(truncated, item.Data.GetProperty("contextTruncated").GetBoolean());
        var chat = EnvelopeChat(item);
        Assert.Equal(chatId, chat.Id);
        Assert.Equal(2, chat.Messages.Count);
        Assert.Equal("user", chat.Messages[0].Role);
        Assert.Equal("", chat.Messages[0].Text);
        Assert.Equal("completed", chat.Messages[0].Status);
        Assert.Equal("assistant", chat.Messages[1].Role);
        Assert.Equal(item.Data.GetProperty("messageId").GetString(), chat.Messages[1].Id);
        Assert.Equal(status, chat.Messages[1].Status);
        Assert.Null(chat.Messages[1].ImageUrl);
        Assert.All(chat.Messages, message =>
        {
            Assert.Equal(ChatModelOptions.ModelId, message.Model);
            Assert.Equal(FixtureDeployment, message.Deployment);
            Assert.Equal("text", message.Size);
            Assert.Equal("text", message.Quality);
        });
        if (status != "failed")
        {
            Assert.Equal(JsonValueKind.Null, item.Data.GetProperty("error").ValueKind);
            Assert.Null(chat.Messages[1].Error);
        }
        return chat;
    }

    private static void AssertImage(ChatInputMessage message, byte[] bytes, string contentType)
    {
        Assert.NotNull(message.Images);
        var image = Assert.Single(message.Images);
        Assert.Equal(bytes, image.Bytes);
        Assert.Equal(contentType, image.ContentType);
    }

    private static void AssertNoProviderDetails(string value)
    {
        Assert.DoesNotContain(FixtureKey, value);
        Assert.DoesNotContain("not-a-real-key", value);
        Assert.DoesNotContain(ProviderSecret, value);
        Assert.DoesNotContain("arbitrary provider detail", value);
        Assert.DoesNotContain("chat.example.test", value);
        Assert.DoesNotContain("\"apiKey\"", value);
        Assert.DoesNotContain("\"tokenScope\"", value);
    }

    private sealed record SseEvent(string Type, JsonElement Data);

    private sealed class Reply(TextChatEvent[] prefix, TextChatEvent[]? suffix = null, Exception? failure = null, bool blocked = false)
    {
        public TextChatEvent[] Prefix { get; } = prefix;
        public TextChatEvent[] Suffix { get; } = suffix ?? [];
        public Exception? Failure { get; } = failure;
        public bool Blocked { get; } = blocked;
        public TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class StubTextGenerator : ITextChatGenerator
    {
        private readonly ConcurrentQueue<TextChatInput> inputs = new();
        private readonly ConcurrentQueue<Reply> replies = new();
        public TextChatInput[] Inputs => inputs.ToArray();
        public int Calls => inputs.Count;
        public void Enqueue(Reply reply) => replies.Enqueue(reply);

        public async IAsyncEnumerable<TextChatEvent> GenerateAsync(TextChatInput input,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            inputs.Enqueue(input);
            var reply = replies.TryDequeue(out var planned) ? planned : new Reply([new("completed", "Fixture answer")]);
            try
            {
                foreach (var item in reply.Prefix)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                }
                if (reply.Blocked)
                {
                    reply.Waiting.TrySetResult(true);
                    await reply.Release.Task.WaitAsync(cancellationToken);
                }
                foreach (var item in reply.Suffix)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                }
                if (reply.Failure != null) throw reply.Failure;
            }
            finally { reply.Finished.TrySetResult(true); }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ApiTests.AppFactory Parent { get; } = new();
        public ChatModelOptions Options { get; } = new()
        {
            Endpoint = "https://chat.example.test/openai/v1/responses",
            ApiKey = FixtureKey,
            AuthMode = "ApiKey",
            Deployment = FixtureDeployment
        };
        public StubTextGenerator Text { get; } = new();
        private WebApplicationFactory<Program> Application { get; }

        public Fixture(Action<ChatModelOptions>? configureChat = null, Action<FoundryOptions>? configureImage = null)
        {
            configureChat?.Invoke(Options);
            configureImage?.Invoke(Parent.ModelOptions);
            // Never start Parent separately: its inherited fake image generator and real
            // synthetic account bootstrap must belong to the same overridden HTTP host.
            Directory.CreateDirectory(Parent.DataDirectory);
            Application = Parent.WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(Parent.DataDirectory);
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.Sources.Clear();
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Storage:Provider"] = "Local",
                        ["Storage:Directory"] = Parent.DataDirectory,
                        ["ReverseProxy:Enabled"] = "false",
                        ["ReverseProxy:Provider"] = "",
                        ["Security:RequireHttps"] = "false"
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ChatModelOptions>();
                    services.AddSingleton(Options);
                    services.RemoveAll<ITextChatGenerator>();
                    services.AddSingleton<ITextChatGenerator>(Text);
                    services.RemoveAll<IChatStore>();
                    services.AddSingleton<IChatStore>(provider => provider.GetRequiredService<ChatStore>());
                });
            });
        }

        public HttpClient NewClient()
        {
            var client = Application.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
                BaseAddress = new Uri("http://localhost")
            });
            client.Timeout = TestTimeout;
            client.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
            Assert.IsType<ChatStore>(Application.Services.GetRequiredService<IChatStore>());
            Assert.Same(Text, Application.Services.GetRequiredService<ITextChatGenerator>());
            Assert.Same(Parent.Generator, Application.Services.GetRequiredService<IImageGenerator>());
            return client;
        }

        public async Task<HttpClient> Login(string username = "testadmin")
        {
            // Mirror AppFactory.CreateAuthenticatedClient on the child factory: use the
            // real account endpoint and cookie jar, never replace authentication/claims.
            var client = NewClient();
            try
            {
                using var response = await client.PostAsJsonAsync("/api/auth/login", new
                {
                    username,
                    password = ApiTests.AppFactory.TestPassword
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.Contains("Set-Cookie"));
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Application.DisposeAsync(); }
            finally { await Parent.DisposeAsync(); }
        }
    }
}