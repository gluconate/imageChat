using Azure;
using Azure.Core;
using Azure.Data.Tables;
using ImageChat;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ImageChat.Tests;

public sealed class CloudTextChatTests : IDisposable
{
    private const string Owner = "cloud-text-owner";
    private readonly AzureStorage storage;
    private readonly AzureChatStore store;

    public CloudTextChatTests()
    {
        // Explicit emulator endpoints only. Never read application settings or authenticate.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["Storage:ResourceSuffix"] = Guid.NewGuid().ToString("N")
        }).Build();
        storage = new AzureStorage(configuration, new NoCredential());
        storage.Initialize();
        store = new AzureChatStore(storage);
    }

    private string Begin(Chat chat, string prompt = "Explain this", ImageData? attachment = null, string? request = null) =>
        TextChatStoreTests.Begin(store, chat, prompt, attachment, request);

    [CloudFact]
    public void LongPromptsUsePrivateWorkBlobsAndDeleteRemovesThem()
    {
        foreach (var length in new[] { 64000, 256000 })
        {
            var prompt = TextChatStoreTests.LongPrompt(length);
            var chat = store.Create(Owner);
            var id = Begin(chat, prompt);
            var pending = new AzureChatStore(storage).Get(chat.Id, Owner);
            Assert.Equal(prompt, pending.Messages[0].Text);
            Assert.Equal(prompt[..64], pending.Title);
            Assert.All(pending.Messages, message => Assert.Equal(ChatModelOptions.ModelId, message.Model));
            var user = storage.Rows("m" + chat.Id + ":")[0];
            Assert.Equal("", user.GetString("Text"));
            Assert.True(user.GetBoolean("TextBlob"));
            Assert.Equal(prompt, storage.Work.GetBlobClient("text/" + user.GetString("Id")).DownloadContent().Value.Content.ToString());
            Assert.Null(pending.Messages[0].ImageUrl);
            Assert.False(storage.Images.GetBlobClient(pending.Messages[0].Id).Exists().Value);
            Assert.Empty(storage.Rows("j"));
            Assert.Empty(storage.Queue.PeekMessages().Value);
            store.FinishTextTurn(chat.Id, id, "Canonical", null);
            Assert.Null(storage.Find("c" + chat.Id)!.GetDateTimeOffset("TextDeadline"));
            store.Delete(chat.Id, Owner);
            store.CleanupDeletedChat(chat.Id); // Retried cleanup is harmless.
            Assert.False(storage.Work.GetBlobClient("text/" + user.GetString("Id")).Exists().Value);
            Assert.Empty(storage.Rows("m" + chat.Id + ":"));
        }
    }

    [CloudFact]
    public void ProgressCanonicalFinalAndTelemetryAreAtomicTextOnlyAndIdempotent()
    {
        var chat = store.Create(Owner);
        var request = store.StartRequest(Owner, "fixture-user", "127.0.0.1");
        store.SetRequestModel(request, ChatModelOptions.ModelId, "fixture-text-deployment");
        var beforeBegin = DateTimeOffset.UtcNow;
        var id = Begin(chat, attachment: new(CoreTests.Png, "image/png"), request: request);
        var deadline = storage.Find("c" + chat.Id)!.GetDateTimeOffset("TextDeadline")!.Value;
        Assert.InRange(deadline, beforeBegin.AddMinutes(5), DateTimeOffset.UtcNow.AddMinutes(5));
        store.EndRequest(request, 200); // The streaming response may already have sent its headers.
        store.UpdateTextTurn(chat.Id, id, "Durable partial snapshot");
        var second = new AzureChatStore(storage);
        var partial = second.Get(chat.Id, Owner);
        Assert.Equal("Durable partial snapshot", partial.Messages[^1].Text);
        Assert.Equal("pending", partial.Messages[^1].Status);
        Assert.Null(partial.Messages[^1].ImageUrl);
        Assert.NotEqual(chat.UpdatedAt, partial.UpdatedAt);
        Assert.True(Assert.Single(second.List(Owner)).IsGenerating);
        Assert.Equal(409, Assert.Throws<AppException>(() => second.Delete(chat.Id, Owner)).StatusCode);
        var partialHead = storage.Find("c" + chat.Id)!;
        second.UpdateTextTurn(chat.Id, id, "Old");
        Assert.Equal(partialHead.ETag, storage.Find("c" + chat.Id)!.ETag);
        Assert.Equal(deadline, storage.Find("c" + chat.Id)!.GetDateTimeOffset("TextDeadline"));
        second.FinishTextTurn(chat.Id, id, "Canonical", null);
        var final = store.Get(chat.Id, Owner);
        var item = Assert.Single(store.Report(new(null, null, null)).Items);
        Assert.Equal("Canonical", final.Messages[^1].Text);
        Assert.Equal("completed", final.Messages[^1].Status);
        Assert.Null(final.Messages[^1].ImageUrl);
        Assert.Null(store.GetImage(id, Owner));
        Assert.False(Assert.Single(store.List(Owner)).IsGenerating);
        Assert.Equal("chat", item.Operation);
        Assert.Equal("completed", item.Status);
        Assert.Equal(0, item.ImageCount);
        Assert.Equal(200, item.HttpStatus);
        Assert.NotNull(item.CompletedAt);
        Assert.Equal(ChatModelOptions.ModelId, item.Model);
        Assert.Equal("fixture-text-deployment", item.Deployment);
        second.FinishTextTurn(chat.Id, id, "Late duplicate", "Late error", 502);
        second.UpdateTextTurn(chat.Id, id, "Late snapshot longer than the canonical result");
        Assert.Equal("Canonical", store.Get(chat.Id, Owner).Messages[^1].Text);
        Assert.Equal(final.UpdatedAt, store.Get(chat.Id, Owner).UpdatedAt);
        Assert.Equal(item, Assert.Single(store.Report(new(null, null, null)).Items));
        store.Delete(chat.Id, Owner);
        Assert.False(storage.Images.GetBlobClient(final.Messages[0].Id).Exists().Value);
        Assert.Equal(item, Assert.Single(store.Report(new(null, null, null)).Items));
        Assert.Equal(0, store.Report(new(null, null, null)).Summary.TotalImages);
    }

    [CloudFact]
    public void FailedPartialOutputCannotBecomeSuccessAndRetainsItsHttpStatus()
    {
        foreach (var failureText in new[] { "", "Durable partial with newer content" })
        {
            var chat = store.Create(Owner);
            var request = store.StartRequest(Owner, "fixture-user", null);
            var id = Begin(chat, request: request);
            store.UpdateTextTurn(chat.Id, id, "Durable partial");
            store.FinishTextTurn(chat.Id, id, failureText, "Interrupted", 502);
            store.FinishTextTurn(chat.Id, id, "Late success", null);
            store.UpdateTextTurn(chat.Id, id, "Late update that is much longer than the saved text");
            var message = store.Get(chat.Id, Owner).Messages[^1];
            Assert.Equal(failureText.Length == 0 ? "Durable partial" : failureText, message.Text);
            Assert.Equal("failed", message.Status);
            Assert.Equal("Interrupted", message.Error);
            Assert.Null(message.ImageUrl);
            var item = Assert.Single(store.Report(new(null, null, null)).Items, item => item.Id == request);
            Assert.Equal("failed", item.Status);
            Assert.Equal("chat", item.Operation);
            Assert.Equal(0, item.ImageCount);
            Assert.Equal(502, item.HttpStatus);
            store.Delete(chat.Id, Owner);
            Assert.Contains(item, store.Report(new(null, null, null)).Items);
        }
    }

    [CloudFact]
    public void ReplyLengthAndNullValidationNeverCorruptPendingMessages()
    {
        var chat = store.Create(Owner);
        Assert.Throws<ArgumentNullException>(() => Begin(chat, null!));
        var id = Begin(chat);
        Assert.Throws<ArgumentNullException>(() => store.UpdateTextTurn(chat.Id, id, null!));
        Assert.Throws<ArgumentNullException>(() => store.FinishTextTurn(chat.Id, id, null!, null));
        Assert.Equal(400, Assert.Throws<AppException>(() => store.UpdateTextTurn(chat.Id, id, new string('x', 12001))).StatusCode);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.FinishTextTurn(chat.Id, id, new string('x', 12001), "failure", 502)).StatusCode);
        var reply = TextChatStoreTests.LongPrompt(12000);
        store.UpdateTextTurn(chat.Id, id, reply);
        Assert.Equal(reply, store.Get(chat.Id, Owner).Messages[^1].Text);
        store.FinishTextTurn(chat.Id, id, reply, null);
        Assert.Equal(reply, store.Get(chat.Id, Owner).Messages[^1].Text);
    }

    [CloudFact]
    public void AuthorizationPrecedesPrivateBlobReadsAndInvalidMessageIdsCannotWrite()
    {
        var chat = store.Create(Owner);
        var id = Begin(chat, TextChatStoreTests.LongPrompt(64000), new(CoreTests.Png, "image/png"));
        var userId = store.Get(chat.Id, Owner).Messages[0].Id;
        var other = store.Create("other-owner");
        var imageId = store.BeginTurn(other, "Draw", "1024x1024", "low", null, model: ModelCatalog.Gpt);
        foreach (var (chatId, messageId) in new[] { (other.Id, id), (chat.Id, imageId), (chat.Id, userId),
            (chat.Id, "missing"), ("missing", id), (other.Id, imageId) })
        {
            Assert.Equal(404, Assert.Throws<AppException>(() => store.UpdateTextTurn(chatId, messageId, "No")).StatusCode);
            Assert.Equal(404, Assert.Throws<AppException>(() => store.FinishTextTurn(chatId, messageId, "No", null)).StatusCode);
        }
        Assert.Equal("", store.Get(chat.Id, Owner).Messages[^1].Text);
        // Missing blobs would raise RequestFailedException if authorization did not run first.
        storage.Work.GetBlobClient("text/" + userId).DeleteIfExists();
        storage.Images.GetBlobClient(userId).DeleteIfExists();
        Assert.Equal(404, Assert.Throws<AppException>(() => store.Get(chat.Id, "other-owner")).StatusCode);
        Assert.Equal(404, Assert.Throws<AppException>(() => store.GetImage(userId, "other-owner")).StatusCode);
        Assert.Equal(404, Assert.Throws<AppException>(() => store.Delete(chat.Id, "other-owner")).StatusCode);
    }

    [CloudFact]
    public void ExpirationRecoversOnGetListBeginDeleteAndDispatcherWithoutQueueingText()
    {
        foreach (var trigger in new[] { "get", "list", "begin", "delete", "dispatch" })
        {
            var chat = store.Create(Owner);
            var request = store.StartRequest(Owner, "fixture-user", null);
            var id = Begin(chat, request: request);
            store.UpdateTextTurn(chat.Id, id, "Survives interruption");
            Expire(chat.Id);
            switch (trigger)
            {
                case "get": store.Get(chat.Id, Owner); break;
                case "list": store.List(Owner); break;
                case "begin": Assert.Equal(409, Assert.Throws<AppException>(() => Begin(chat)).StatusCode); break;
                case "delete": store.Delete(chat.Id, Owner); break;
                case "dispatch": store.DispatchPendingJobs(); break;
            }
            var head = storage.Find("c" + chat.Id)!;
            Assert.Equal("", head.GetString("Busy"));
            Assert.Null(head.GetDateTimeOffset("TextDeadline"));
            if (trigger != "delete")
            {
                var message = store.Get(chat.Id, Owner).Messages[^1];
                Assert.Equal("failed", message.Status);
                Assert.Equal("Survives interruption", message.Text);
                Assert.Contains("interrupted", message.Error);
                store.FinishTextTurn(chat.Id, id, "Late success", null);
                Assert.Equal("failed", store.Get(chat.Id, Owner).Messages[^1].Status);
                store.Delete(chat.Id, Owner);
            }
            var item = Assert.Single(store.Report(new(null, null, null)).Items, item => item.Id == request);
            Assert.Equal("failed", item.Status);
            Assert.Equal("chat", item.Operation);
            Assert.Equal(503, item.HttpStatus);
            Assert.Equal(0, item.ImageCount);
            Assert.Empty(storage.Rows("j"));
            Assert.Empty(storage.Queue.PeekMessages().Value);
        }
    }

    [CloudFact]
    public void RecoveryCasRetriesKeepLatestPartialAndCannotStealANewerImageTurn()
    {
        var chat = store.Create(Owner);
        var id = Begin(chat);
        store.UpdateTextTurn(chat.Id, id, "Initial partial");
        var expired = Expire(chat.Id);
        store.UpdateTextTurn(chat.Id, id, "Newest durable partial after the recovery read");
        store.RecoverExpiredText(expired); // Forces an ETag failure and retry of the captured head.
        var failed = store.Get(chat.Id, Owner);
        Assert.Equal("failed", failed.Messages[^1].Status);
        Assert.Equal("Newest durable partial after the recovery read", failed.Messages[^1].Text);

        var another = store.Create(Owner);
        var anotherId = Begin(another);
        var stale = Expire(another.Id);
        store.FinishTextTurn(another.Id, anotherId, "Completion wins", null);
        store.RecoverExpiredText(stale);
        Assert.Equal("Completion wins", store.Get(another.Id, Owner).Messages[^1].Text);
        var imageId = store.BeginTurn(store.Get(another.Id, Owner), "Draw", "1024x1024", "low", null);
        store.RecoverExpiredText(stale);
        store.DispatchPendingJobs();
        Assert.Equal(imageId, storage.Find("c" + another.Id)!.GetString("Busy"));
        Assert.Equal("pending", store.Get(another.Id, Owner).Messages[^1].Status);
        store.FinishTurn(another.Id, imageId, new(CoreTests.Png, "image/png"), null);
    }

    [CloudFact]
    public async Task ConcurrentInstancesKeepSnapshotsMonotonicAcrossIndependentChats()
    {
        var first = store.Create(Owner);
        var second = store.Create(Owner);
        var firstId = Begin(first);
        var secondId = Begin(second);
        var replica = new AzureChatStore(storage);
        await Task.WhenAll(Task.Run(() => store.UpdateTextTurn(first.Id, firstId, "Longest first snapshot")),
            Task.Run(() => replica.UpdateTextTurn(first.Id, firstId, "Old")),
            Task.Run(() => replica.UpdateTextTurn(second.Id, secondId, "Second snapshot")));
        Assert.Equal("Longest first snapshot", store.Get(first.Id, Owner).Messages[^1].Text);
        Assert.Equal("Second snapshot", store.Get(second.Id, Owner).Messages[^1].Text);
        await Task.WhenAll(Task.Run(() => store.FinishTextTurn(first.Id, firstId, "First final", null)),
            Task.Run(() => replica.FinishTextTurn(first.Id, firstId, "First final", null)),
            Task.Run(() => replica.FinishTextTurn(second.Id, secondId, "Second final", null)));
        Assert.Equal("First final", store.Get(first.Id, Owner).Messages[^1].Text);
        Assert.Equal("Second final", store.Get(second.Id, Owner).Messages[^1].Text);
    }

    [CloudFact]
    public async Task ImageQueueStillGeneratesImagesAndAlsoSupportsLongNonChatText()
    {
        var chat = store.Create(Owner);
        var request = store.StartRequest(Owner, "fixture-user", null);
        var prompt = TextChatStoreTests.LongPrompt(64000);
        var pending = store.QueueTurn(chat, prompt, "1024x1024", "low", null, request, new("Draw", "1024x1024", "low", null));
        Assert.Equal(prompt, pending.Messages[0].Text);
        Assert.Null(storage.Find("c" + chat.Id)!.GetDateTimeOffset("TextDeadline"));
        store.DispatchPendingJobs();
        Assert.True(Assert.Single(store.List(Owner)).IsGenerating);
        var generator = new FakeImageGenerator();
        Assert.True(await store.ProcessOneAsync(generator, default));
        Assert.Equal(1, generator.Calls);
        var final = store.Get(chat.Id, Owner);
        Assert.Equal("Here's your image. Tell me what you'd like to change next.", final.Messages[^1].Text);
        Assert.Equal(CoreTests.Png, store.GetImage(final.Messages[^1].Id, Owner)!.Bytes);
        Assert.Equal(1, store.Report(new(null, null, null)).Summary.GeneratedImages);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.QueueTurn(store.Create(Owner), "No text queue", "text", "text", null,
            null, new("No text queue", "text", "text", null, ChatModelOptions.ModelId))).StatusCode);
        store.Delete(chat.Id, Owner);
        Assert.False(storage.Work.GetBlobClient("text/" + pending.Messages[0].Id).Exists().Value);
    }

    private TableEntity Expire(string chatId)
    {
        var head = storage.Find("c" + chatId)!;
        head["TextDeadline"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        storage.Table.UpdateEntity(head, head.ETag, TableUpdateMode.Replace);
        return storage.Find("c" + chatId)!;
    }

    private sealed class FakeImageGenerator : IImageGenerator
    {
        public int Calls { get; private set; }
        public Task<ImageData> GenerateAsync(GenerationInput input, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ImageData(CoreTests.Png, "image/png"));
        }
    }

    private sealed class NoCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Storage tests must not authenticate.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Storage tests must not authenticate.");
    }

    public void Dispose()
    {
        storage.Table.Delete();
        storage.Images.DeleteIfExists();
        storage.Work.DeleteIfExists();
        storage.Keys.DeleteIfExists();
        storage.Queue.DeleteIfExists();
    }
}