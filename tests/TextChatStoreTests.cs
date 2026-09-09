using ImageChat;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ImageChat.Tests;

public sealed class TextChatStoreTests : IDisposable
{
    private const string Owner = "text-store-owner";
    private readonly string directory = FixtureDirectory();
    private readonly ChatStore store;

    public TextChatStoreTests() => store = new ChatStore(directory);

    private static string FixtureDirectory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ImageChat.csproj"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Tests must run beneath the ImageChat repository.");
        return Path.Combine(root.FullName, ".cache", "test-data", $"text-store-{Guid.NewGuid():N}");
    }

    internal static string LongPrompt(int length) => string.Concat(Enumerable.Repeat("文🙂\n", length / 4));

    internal static string Begin(IChatStore store, Chat chat, string prompt = "Explain this", ImageData? attachment = null,
        string? request = null) => store.BeginTurn(chat, prompt, "text", "text", attachment, request, "chat",
            ChatModelOptions.ModelId, "fixture-text-deployment");

    [Theory]
    [InlineData(64000)]
    [InlineData(256000)]
    public void LongUtf16PromptsRoundTripWithoutSchemaChanges(int length)
    {
        var prompt = LongPrompt(length);
        Assert.Equal(length, prompt.Length);
        var chat = store.Create(Owner);
        var id = Begin(store, chat, prompt);
        var pending = store.Get(chat.Id, Owner);
        Assert.Equal(prompt, pending.Messages[0].Text);
        Assert.Equal(prompt[..64], pending.Title);
        Assert.Equal("pending", pending.Messages[1].Status);
        Assert.Equal(id, pending.Messages[1].Id);
        Assert.All(pending.Messages, message => Assert.Equal(ChatModelOptions.ModelId, message.Model));
        store.FinishTextTurn(chat.Id, id, "Answer", null);
        Assert.Equal(prompt, store.Get(chat.Id, Owner).Messages[0].Text);
    }

    [Fact]
    public void ProgressCanonicalFinalAndTelemetryRemainTextOnlyAndSurviveDeletion()
    {
        var chat = store.Create(Owner);
        var request = store.StartRequest(Owner, "fixture-user", "127.0.0.1");
        store.SetRequestModel(request, ChatModelOptions.ModelId, "fixture-text-deployment");
        var id = Begin(store, chat, attachment: new(CoreTests.Png, "image/png"), request: request);
        store.UpdateTextTurn(chat.Id, id, "Partial snapshot");
        var partial = store.Get(chat.Id, Owner);
        Assert.Equal("Partial snapshot", partial.Messages[^1].Text);
        Assert.Equal("pending", partial.Messages[^1].Status);
        Assert.NotEqual(chat.UpdatedAt, partial.UpdatedAt);
        Assert.True(Assert.Single(store.List(Owner)).IsGenerating);
        Assert.Equal(1, store.Report(new(null, null, null)).Summary.Pending);
        Assert.Null(partial.Messages[^1].ImageUrl);
        Assert.Equal(409, Assert.Throws<AppException>(() => store.Delete(chat.Id, Owner)).StatusCode);
        store.UpdateTextTurn(chat.Id, id, "Old");
        Assert.Equal(partial, store.Get(chat.Id, Owner) with { Messages = partial.Messages });
        Assert.Equal("Partial snapshot", store.Get(chat.Id, Owner).Messages[^1].Text);

        store.FinishTextTurn(chat.Id, id, "Canonical", null);
        var final = store.Get(chat.Id, Owner);
        Assert.Equal("Canonical", final.Messages[^1].Text); // Shorter, different final is authoritative.
        Assert.Equal("completed", final.Messages[^1].Status);
        Assert.Null(final.Messages[^1].ImageUrl);
        Assert.Null(store.GetImage(id, Owner));
        Assert.False(Assert.Single(store.List(Owner)).IsGenerating);
        var item = Assert.Single(store.Report(new(null, null, null)).Items);
        Assert.Equal("chat", item.Operation);
        Assert.Equal("completed", item.Status);
        Assert.Equal(0, item.ImageCount);
        Assert.Equal(200, item.HttpStatus);
        Assert.NotNull(item.CompletedAt);
        Assert.Equal(ChatModelOptions.ModelId, item.Model);
        Assert.Equal("fixture-text-deployment", item.Deployment);
        store.FinishTextTurn(chat.Id, id, "different duplicate", "late failure", 502);
        store.UpdateTextTurn(chat.Id, id, "late and much longer update");
        Assert.Equal("Canonical", store.Get(chat.Id, Owner).Messages[^1].Text);
        Assert.Equal(final.UpdatedAt, store.Get(chat.Id, Owner).UpdatedAt);
        Assert.Equal(item, Assert.Single(store.Report(new(null, null, null)).Items));
        store.Delete(chat.Id, Owner);
        Assert.Empty(store.List(Owner));
        Assert.Null(store.GetImage(final.Messages[0].Id, Owner));
        Assert.Equal(item, Assert.Single(store.Report(new(null, null, null)).Items));
        Assert.Equal(0, store.Report(new(null, null, null)).Summary.TotalImages);
    }

    [Theory]
    [InlineData("", "Durable partial")]
    [InlineData("Durable partial plus latest", "Durable partial plus latest")]
    public void FailurePreservesPartialTextAndCannotBeRewritten(string failureText, string expected)
    {
        var chat = store.Create(Owner);
        var request = store.StartRequest(Owner, "fixture-user", null);
        var id = Begin(store, chat, request: request);
        store.UpdateTextTurn(chat.Id, id, "Durable partial");
        store.FinishTextTurn(chat.Id, id, failureText, "Interrupted", 503);
        store.FinishTextTurn(chat.Id, id, "Late success", null);
        store.UpdateTextTurn(chat.Id, id, "Late and longer than the partial snapshot");
        var message = store.Get(chat.Id, Owner).Messages[^1];
        Assert.Equal(expected, message.Text);
        Assert.Equal("failed", message.Status);
        Assert.Equal("Interrupted", message.Error);
        Assert.Null(message.ImageUrl);
        Assert.False(Assert.Single(store.List(Owner)).IsGenerating);
        var report = store.Report(new(null, null, null));
        Assert.Equal(1, report.Summary.Failed);
        Assert.Equal(0, report.Summary.Pending);
        Assert.Equal(503, Assert.Single(report.Items).HttpStatus);
        store.Delete(chat.Id, Owner);
        Assert.Equal(report.Items, store.Report(new(null, null, null)).Items);
    }

    [Fact]
    public void NullAndOversizedSnapshotsAreRejectedWithoutChangingPendingState()
    {
        var chat = store.Create(Owner);
        Assert.Throws<ArgumentNullException>(() => Begin(store, chat, null!));
        var id = Begin(store, chat);
        Assert.Throws<ArgumentNullException>(() => store.UpdateTextTurn(chat.Id, id, null!));
        Assert.Throws<ArgumentNullException>(() => store.FinishTextTurn(chat.Id, id, null!, null));
        foreach (var finish in new[] { false, true })
            Assert.Equal(400, Assert.Throws<AppException>(() =>
            {
                if (finish) store.FinishTextTurn(chat.Id, id, new string('x', 12001), null);
                else store.UpdateTextTurn(chat.Id, id, new string('x', 12001));
            }).StatusCode);
        var limit = LongPrompt(12000);
        store.UpdateTextTurn(chat.Id, id, limit);
        Assert.Equal(limit, store.Get(chat.Id, Owner).Messages[^1].Text);
        store.FinishTextTurn(chat.Id, id, limit, null);
        Assert.Equal(limit, store.Get(chat.Id, Owner).Messages[^1].Text);
    }

    [Fact]
    public void WrongChatUserMessageOrModelCannotModifyAnyTurn()
    {
        var chat = store.Create(Owner);
        var id = Begin(store, chat);
        var userId = store.Get(chat.Id, Owner).Messages[0].Id;
        var other = store.Create(Owner);
        var imageId = store.BeginTurn(other, "Draw", "1024x1024", "low", null, model: ModelCatalog.Gpt);
        var wrongModel = store.Create(Owner);
        var wrongModelId = store.BeginTurn(wrongModel, "Draw", "text", "text", null, model: ModelCatalog.Gpt);
        foreach (var (chatId, messageId) in new[] { (other.Id, id), (chat.Id, imageId), (chat.Id, userId),
            (chat.Id, "missing"), ("missing", id), (other.Id, imageId), (wrongModel.Id, wrongModelId) })
        {
            Assert.Equal(404, Assert.Throws<AppException>(() => store.UpdateTextTurn(chatId, messageId, "No")).StatusCode);
            Assert.Equal(404, Assert.Throws<AppException>(() => store.FinishTextTurn(chatId, messageId, "No", null)).StatusCode);
        }
        Assert.Equal("", store.Get(chat.Id, Owner).Messages[^1].Text);
        Assert.Equal("pending", store.Get(other.Id, Owner).Messages[^1].Status);
        store.FinishTurn(other.Id, imageId, new(CoreTests.Png, "image/png"), null);
        Assert.Equal("Here's your image. Tell me what you'd like to change next.", store.Get(other.Id, Owner).Messages[^1].Text);
        Assert.Equal(CoreTests.Png, store.GetImage(imageId, Owner)!.Bytes);
    }

    [Fact]
    public void AttachmentsAreOwnerScopedAndTextBeginCannotStealImageBusyState()
    {
        var chat = store.Create(Owner);
        var id = Begin(store, chat, attachment: new([255, 216, 255, 224], "image/jpeg"));
        var pending = store.Get(chat.Id, Owner);
        Assert.Equal(404, Assert.Throws<AppException>(() => store.Get(chat.Id, "another-owner")).StatusCode);
        Assert.Null(store.GetImage(pending.Messages[0].Id, "another-owner"));
        Assert.Equal("image/jpeg", store.GetImage(pending.Messages[0].Id, Owner)!.ContentType);
        Assert.Equal(409, Assert.Throws<AppException>(() => Begin(store, pending)).StatusCode);
        store.FinishTextTurn(chat.Id, id, "Answer", null);
        var imageId = store.BeginTurn(store.Get(chat.Id, Owner), "Draw", "1024x1024", "low", null);
        Assert.Equal(409, Assert.Throws<AppException>(() => Begin(store, store.Get(chat.Id, Owner))).StatusCode);
        store.UpdateTextTurn(chat.Id, id, "Late progress after starting an image");
        Assert.Equal("pending", store.Get(chat.Id, Owner).Messages[^1].Status);
        store.FinishTurn(chat.Id, imageId, new(CoreTests.Png, "image/png"), null);
    }

    [Fact]
    public async Task ConcurrentChatsAndOutOfOrderSnapshotsRemainIndependent()
    {
        var first = store.Create(Owner);
        var second = store.Create(Owner);
        var firstId = Begin(store, first);
        var secondId = Begin(store, second);
        await Task.WhenAll(Task.Run(() => store.UpdateTextTurn(first.Id, firstId, "Longest first snapshot")),
            Task.Run(() => store.UpdateTextTurn(first.Id, firstId, "Old")),
            Task.Run(() => store.UpdateTextTurn(second.Id, secondId, "Second snapshot")));
        Assert.Equal("Longest first snapshot", store.Get(first.Id, Owner).Messages[^1].Text);
        Assert.Equal("Second snapshot", store.Get(second.Id, Owner).Messages[^1].Text);
        await Task.WhenAll(Task.Run(() => store.FinishTextTurn(first.Id, firstId, "First final", null)),
            Task.Run(() => store.FinishTextTurn(second.Id, secondId, "Second final", null)));
        Assert.Equal("First final", store.Get(first.Id, Owner).Messages[^1].Text);
        Assert.Equal("Second final", store.Get(second.Id, Owner).Messages[^1].Text);
    }

    [Fact]
    public void StartupMarksPendingTextFailedWithoutDiscardingPartialOutput()
    {
        var chat = store.Create(Owner);
        var request = store.StartRequest(Owner, "fixture-user", null);
        var id = Begin(store, chat, request: request);
        store.UpdateTextTurn(chat.Id, id, "Saved before restart");
        var restarted = new ChatStore(directory); // Isolated fixture only, simulates process startup.
        restarted.FinishTextTurn(chat.Id, id, "Late success", null);
        var message = restarted.Get(chat.Id, Owner).Messages[^1];
        Assert.Equal("Saved before restart", message.Text);
        Assert.Equal("failed", message.Status);
        Assert.False(Assert.Single(restarted.List(Owner)).IsGenerating);
        Assert.Equal("interrupted", Assert.Single(restarted.Report(new(null, null, null)).Items).Status);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}