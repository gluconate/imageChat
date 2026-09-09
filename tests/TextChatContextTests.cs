using System.Reflection;
using System.Text;
using ImageChat;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ImageChat.Tests;

public sealed class TextChatContextTests : IDisposable
{
    private const string Owner = "context-test-owner";
    private const string PrivateMarker = "private-context-fixture";
    private readonly string directory;
    private readonly ChatStore store;
    private readonly IChatStore imageStore;
    private readonly ImageReadSpy reads;

    public TextChatContextTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ImageChat.csproj"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Tests must run beneath the ImageChat repository.");
        directory = Path.Combine(root.FullName, ".cache", "test-data", $"text-context-{Guid.NewGuid():N}");
        store = new ChatStore(directory);
        imageStore = DispatchProxy.Create<IChatStore, ImageReadSpy>();
        reads = (ImageReadSpy)imageStore;
        reads.Store = store;
    }

    [Fact]
    public void PromptValidationTrimsAndUsesTheAnalysisDefaultNotTheImageVariationDefault()
    {
        var options = new ChatModelOptions(); // Deliberately no provider credentials.
        Assert.Equal("Explain this", TextChatContext.ValidatePrompt(" \r\nExplain this\t ", false, options));
        Assert.Equal("Describe this image and help me understand it.", TextChatContext.ValidatePrompt(" \r\n", true, options));
        Assert.NotEqual(InputRules.ImageOnlyPrompt, TextChatContext.ImageOnlyPrompt);
        Assert.Equal(new string('p', 64000), TextChatContext.ValidatePrompt(" " + new string('p', 64000) + " ", false, options));
        Assert.Equal(400, Assert.Throws<AppException>(() => TextChatContext.ValidatePrompt(new string('p', 64001), false, options)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void EmptyPromptWithoutAnImageFailsInBothEntryPoints(string? prompt)
    {
        var chat = store.Create(Owner);
        Assert.Equal(400, Assert.Throws<AppException>(() => TextChatContext.ValidatePrompt(prompt!, false, new())).StatusCode);
        Assert.Equal(400, Assert.Throws<AppException>(() => Build(chat, prompt!)).StatusCode);
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void ImageOnlyCurrentMessageContainsTheActualValidatedPromptAndMimeType()
    {
        var chat = store.Create(Owner);
        var result = Build(chat, " ", new(CoreTests.Png, "text/html"));
        var current = Assert.Single(result.Input.Messages);
        Assert.Equal("user", current.Role);
        Assert.Equal(TextChatContext.ImageOnlyPrompt, current.Text);
        Assert.Equal("image/png", Assert.Single(current.Images!).ContentType);
        Assert.False(result.Truncated);
        Assert.Empty(reads.Calls);
        Assert.Empty(chat.Messages); // Building never appends the current turn to storage/snapshot.
    }

    [Theory]
    [InlineData("MaxPromptCharacters", 0)]
    [InlineData("MaxPromptCharacters", 256001)]
    [InlineData("MaxContextCharacters", 63999)]
    [InlineData("MaxContextCharacters", 1000001)]
    [InlineData("MaxContextMessages", 1)]
    [InlineData("MaxContextMessages", 1001)]
    [InlineData("MaxContextImages", 0)]
    [InlineData("MaxContextImages", 17)]
    [InlineData("MaxContextImageBytes", 20971519)]
    [InlineData("MaxContextImageBytes", 67108865)]
    public void InvalidBudgetsFailWithoutImageReadsOrProviderConfiguration(string property, int value)
    {
        var options = new ChatModelOptions();
        typeof(ChatModelOptions).GetProperty(property)!.SetValue(options, value);
        Assert.Equal(503, Assert.Throws<AppException>(() => TextChatContext.ValidatePrompt("p", false, options)).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => Build(store.Create(Owner), "p", options: options)).StatusCode);
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void TinyPromptBudgetAlsoAppliesToTheImageOnlyDefault()
    {
        var options = new ChatModelOptions { MaxPromptCharacters = 1, MaxContextCharacters = 1 };
        Assert.Equal("p", TextChatContext.ValidatePrompt(" p ", false, options));
        Assert.Equal(400, Assert.Throws<AppException>(() => TextChatContext.ValidatePrompt("", true, options)).StatusCode);
    }

    [Fact]
    public void DefaultTextWindowIs256000CharactersAndCurrentPromptCanBe64000()
    {
        var chat = store.Create(Owner);
        AddTextTurn(chat, new string('u', 96000), new string('a', 96000));
        var result = Build(chat, new string('p', 64000));
        Assert.False(result.Truncated);
        Assert.Equal(3, result.Input.Messages.Count);
        Assert.Equal(256000, result.Input.Messages.Sum(message => message.Text.Length));
        Assert.All(result.Input.Messages, message => Assert.Null(message.Images));
        Assert.Equal(12000, ResponsesChatClient.MaxResponseCharacters);
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void HistoryUsesOnlyAdjacentFullySuccessfulPairsAndExactTextModelAnswers()
    {
        var chat = store.Create(Owner);
        AddTextTurn(chat, "First request", "First answer");
        AddTextTurn(chat, "Failed request", PrivateMarker, status: "failed", imageUrl: "/never-read/failed");
        AddTextTurn(chat, "Pending request", PrivateMarker, status: "pending", imageUrl: "/never-read/pending");
        AddTextTurn(chat, "Error despite completed", PrivateMarker, error: PrivateMarker, imageUrl: "/never-read/error");
        AddTextTurn(chat, "Incomplete user", PrivateMarker, userStatus: "pending");
        AddTextTurn(chat, "Errored user", PrivateMarker, userError: PrivateMarker);
        chat.Messages.Add(Message("system", PrivateMarker));
        chat.Messages.Add(Message("assistant", PrivateMarker, model: ChatModelOptions.ModelId));
        chat.Messages.Add(Message("tool", PrivateMarker));
        chat.Messages.Add(Message("user", "Unpaired request"));
        chat.Messages.Add(Message("developer", PrivateMarker));
        AddTextTurn(chat, "Latest request", "Latest answer");

        var result = Build(chat, "Current request");
        Assert.Equal(new[] { "First request", "First answer", "Latest request", "Latest answer", "Current request" },
            result.Input.Messages.Select(message => message.Text));
        Assert.Equal(new[] { "user", "assistant", "user", "assistant", "user" }, result.Input.Messages.Select(message => message.Role));
        Assert.False(result.Truncated); // Failed/pending/malformed records are not valid history.
        Assert.Empty(reads.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("GPT-5.6-SOL")]
    [InlineData(ModelCatalog.Gpt)]
    public void OtherModelsDoNotBecomeTextAssistantHistory(string? model)
    {
        var chat = store.Create(Owner);
        AddTextTurn(chat, "Earlier non-text request", PrivateMarker, model: model);
        var result = Build(chat, "Current");
        Assert.Equal("Current", Assert.Single(result.Input.Messages).Text);
        Assert.True(result.Truncated);
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void CharacterSelectionStopsAtFirstOversizedWholeTurnWithoutBackfillingOlderTurns()
    {
        var chat = store.Create(Owner);
        AddTextTurn(chat, "o", "a"); // Would fit, but must not jump over the middle turn.
        AddTextTurn(chat, "xxxx", "a", imageUrl: "/never-read/over-budget");
        AddTextTurn(chat, "n", "a");
        var result = Build(chat, "p", options: new() { MaxPromptCharacters = 1, MaxContextCharacters = 5 });
        Assert.Equal(new[] { "n", "a", "p" }, result.Input.Messages.Select(message => message.Text));
        Assert.True(result.Truncated);
        Assert.Empty(reads.Calls);
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, true)]
    [InlineData(5, 5, false)]
    public void MessageBudgetRetainsNewestWholeTurnsPlusTheMandatoryCurrentMessage(int budget, int expected, bool truncated)
    {
        var chat = store.Create(Owner);
        AddTextTurn(chat, "Old request", "Old answer");
        AddTextTurn(chat, "New request", "New answer");
        var result = Build(chat, "Current", options: new() { MaxContextMessages = budget });
        Assert.Equal(expected, result.Input.Messages.Count);
        Assert.Equal(truncated, result.Truncated);
        Assert.Equal("Current", result.Input.Messages[^1].Text);
        if (expected == 3) Assert.Equal("New request", result.Input.Messages[0].Text);
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void GeneratedImagesAreUserReferencesAndAttachmentsKeepTheirOriginalRequest()
    {
        var chat = store.Create(Owner);
        var upload = new ImageData(CoreTests.Png, "not/trusted");
        var generated = new ImageData([255, 216, 255, 224], "image/png");
        AddStoredTurn(chat, "Make a diagram", upload, generated);
        var userId = chat.Messages[0].Id;
        var assistantId = chat.Messages[1].Id;
        chat.Messages[0] = chat.Messages[0] with { ImageUrl = "file:///must-not-open/" + PrivateMarker };
        chat.Messages[1] = chat.Messages[1] with { ImageUrl = "https://must-not-fetch.test/" + PrivateMarker };

        var result = Build(chat, "Explain the diagram");
        Assert.False(result.Truncated);
        Assert.Equal(3, result.Input.Messages.Count);
        var original = result.Input.Messages[0];
        Assert.Equal("user", original.Role);
        Assert.Equal("Make a diagram", original.Text);
        Assert.Equal("image/png", Assert.Single(original.Images!).ContentType);
        var reference = result.Input.Messages[1];
        Assert.Equal("user", reference.Role);
        Assert.Equal(TextChatContext.GeneratedImageReference, reference.Text);
        Assert.Equal("image/jpeg", Assert.Single(reference.Images!).ContentType);
        Assert.DoesNotContain(result.Input.Messages, message => message.Text.Contains("Here's your image", StringComparison.Ordinal));
        Assert.Equal(new[] { (assistantId, (string?)Owner), (userId, (string?)Owner) }, reads.Calls);
        Assert.Equal(2, chat.Messages.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ModelCatalog.Gpt)]
    [InlineData(ChatModelOptions.ModelId)]
    public void UserAttachmentsFromAnySuccessfulPairAreIncludedButOnlyTextModelAnswersAreKept(string? model)
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Explain my upload", new(CoreTests.Png, "image/png"), null, model);
        chat.Messages[1] = chat.Messages[1] with { Text = "Actual text answer" };
        var result = Build(chat, "Continue");
        Assert.False(result.Truncated);
        Assert.Equal("Explain my upload", result.Input.Messages[0].Text);
        Assert.Single(result.Input.Messages[0].Images!);
        Assert.Equal(model == ChatModelOptions.ModelId ? 3 : 2, result.Input.Messages.Count);
        Assert.Equal(model == ChatModelOptions.ModelId, result.Input.Messages.Any(message => message.Role == "assistant"));
        Assert.Equal((chat.Messages[0].Id, (string?)Owner), Assert.Single(reads.Calls));
    }

    [Fact]
    public void ImageBearingAssistantNeverBecomesAssistantImageEvenWithATextModelId()
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Earlier", null, new(CoreTests.Png, "image/png"), ChatModelOptions.ModelId);
        chat.Messages[1] = chat.Messages[1] with { Text = PrivateMarker };
        var result = Build(chat, "Current");
        Assert.All(result.Input.Messages, message => Assert.Equal("user", message.Role));
        Assert.DoesNotContain(result.Input.Messages, message => message.Text == PrivateMarker);
        Assert.Equal(TextChatContext.GeneratedImageReference, result.Input.Messages[1].Text);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void GeneratedReferenceCharactersArePartOfWholeTurnSelectionBeforeImagesAreRead(int deficit, bool truncated)
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "u", null, new(CoreTests.Png, "image/png"));
        var result = Build(chat, "p", options: new() {
            MaxPromptCharacters = 1, MaxContextCharacters = TextChatContext.GeneratedImageReference.Length + 2 - deficit });
        Assert.Equal(truncated, result.Truncated);
        Assert.Equal(truncated ? 1 : 3, result.Input.Messages.Count);
        Assert.Equal(truncated ? 0 : 1, reads.Calls.Count);
    }

    [Fact]
    public void GeneratedReferenceMessageIsReservedBeforeAnyImageRead()
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Earlier", null, new(CoreTests.Png, "image/png"));
        var result = Build(chat, "Current", options: new() { MaxContextMessages = 2 });
        Assert.Equal("Current", Assert.Single(result.Input.Messages).Text);
        Assert.True(result.Truncated);
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void CurrentImageWinsAndOptionalHistoryImagesAreNotReadWhenCountIsExhausted()
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Earlier", new(CoreTests.Png, "image/png"), new(CoreTests.Png, "image/png"));
        var result = Build(chat, "Current", new(CoreTests.Png, "image/png"), new() { MaxContextImages = 1 });
        Assert.True(result.Truncated);
        Assert.Equal(new[] { "Earlier", "Current" }, result.Input.Messages.Select(message => message.Text));
        Assert.Null(result.Input.Messages[0].Images);
        Assert.Single(result.Input.Messages[^1].Images!);
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void NewestGeneratedImageWinsOverTheSameTurnsOlderUserAttachment()
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Earlier", new(CoreTests.Png, "image/png"), new(CoreTests.Png, "image/png"));
        var result = Build(chat, "Current", options: new() { MaxContextImages = 1 });
        Assert.True(result.Truncated);
        Assert.Null(result.Input.Messages[0].Images);
        Assert.Single(result.Input.Messages[1].Images!);
        Assert.Equal((chat.Messages[1].Id, (string?)Owner), Assert.Single(reads.Calls));
    }

    [Fact]
    public void ThousandsOfHistoricalImageFlagsCannotCauseUnboundedImageReads()
    {
        var chat = store.Create(Owner);
        for (var i = 0; i < 2000; i++)
            AddTextTurn(chat, "Old " + i, PrivateMarker, imageUrl: "/not-stored/" + i);
        for (var i = 0; i < 8; i++)
            AddStoredTurn(chat, "Recent " + i, null, new(CoreTests.Png, "image/png"));
        var expectedIds = chat.Messages.TakeLast(16).Where(message => message.Role == "assistant")
            .Select(message => message.Id).Reverse().ToArray();

        var result = Build(chat, "Current");
        Assert.True(result.Truncated);
        Assert.True(result.Input.Messages.Count <= 200);
        Assert.Equal(8, result.Input.Messages.Sum(message => message.Images?.Count ?? 0));
        Assert.Equal(expectedIds, reads.Calls.Select(call => call.Id));
        Assert.All(reads.Calls, call => Assert.Equal(Owner, call.OwnerId));
        Assert.DoesNotContain(result.Input.Messages, message => message.Text == PrivateMarker);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void RawImageByteBudgetKeepsCurrentAndNewestThenStopsWithoutTryingSmallerOlderImages(int extra, bool newestFits)
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Older small", null, new(CoreTests.Png, "image/png"));
        AddStoredTurn(chat, "Newest", null, Image(12 * 1024 * 1024 + extra));
        var newestId = chat.Messages[^1].Id;
        var result = Build(chat, "Current", Image(20 * 1024 * 1024));
        Assert.True(result.Truncated);
        var images = result.Input.Messages.SelectMany(message => message.Images ?? []).ToArray();
        Assert.Equal(newestFits ? 2 : 1, images.Length);
        Assert.Equal(newestFits ? 32 * 1024 * 1024 : 20 * 1024 * 1024, images.Sum(image => image.Bytes.Length));
        Assert.Single(result.Input.Messages[^1].Images!);
        Assert.Equal((newestId, (string?)Owner), Assert.Single(reads.Calls));
        Assert.Equal(newestFits ? 1 : 0, result.Input.Messages.Count(message => message.Text == TextChatContext.GeneratedImageReference));
    }

    [Fact]
    public void DisablingImagesRejectsCurrentButOmitsHistoryWithoutReadingOrPretendingToSendIt()
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Image request", new(CoreTests.Png, "image/png"), new(CoreTests.Png, "image/png"));
        AddStoredTurn(chat, "Text request with upload", new(CoreTests.Png, "image/png"), null, ChatModelOptions.ModelId);
        chat.Messages[^1] = chat.Messages[^1] with { Text = "Text answer" };
        var options = new ChatModelOptions { SupportsImages = false };
        var result = Build(chat, "Current", options: options);
        Assert.True(result.Truncated);
        Assert.Equal(new[] { "Image request", "Text request with upload", "Text answer", "Current" },
            result.Input.Messages.Select(message => message.Text));
        Assert.All(result.Input.Messages, message => Assert.Null(message.Images));
        Assert.DoesNotContain(result.Input.Messages, message => message.Text == TextChatContext.GeneratedImageReference);
        Assert.Equal(400, Assert.Throws<AppException>(() => Build(chat, "Current", new(CoreTests.Png, "image/png"), options)).StatusCode);
        Assert.Equal(400, Assert.Throws<AppException>(() => TextChatContext.ValidatePrompt("", true, options)).StatusCode);
        Assert.Empty(reads.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("oversized-generated")]
    public void EligibleUnavailableOrInvalidHistoryImagesFailWithSafe409InsteadOfSilentFallback(string kind)
    {
        var chat = store.Create(Owner);
        if (kind == "missing") AddTextTurn(chat, PrivateMarker, "unused", imageUrl: "/missing/" + PrivateMarker);
        else AddStoredTurn(chat, PrivateMarker, null, kind == "invalid"
            ? new(Encoding.UTF8.GetBytes(PrivateMarker), "image/png")
            : Image(32 * 1024 * 1024)); // Persisted generated images may exceed the 20 MB input cap.

        var error = Assert.Throws<AppException>(() => Build(chat, "Current"));
        Assert.Equal(409, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.DoesNotContain(directory, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(Owner, Assert.Single(reads.Calls).OwnerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreImageFailuresBecomeSafe409WithoutLeakingStorageDiagnostics(bool cloud)
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Earlier", null, new(CoreTests.Png, "image/png"));
        reads.Failure = cloud ? new Azure.RequestFailedException(404, PrivateMarker) : new AppException(404, PrivateMarker);
        var error = Assert.Throws<AppException>(() => Build(chat, "Current"));
        Assert.Equal(409, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Single(reads.Calls);
    }

    [Fact]
    public void CurrentInvalidImageIsRejectedRatherThanDroppedOrReplacedWithHistory()
    {
        var chat = store.Create(Owner);
        AddStoredTurn(chat, "Earlier", null, new(CoreTests.Png, "image/png"));
        foreach (var attachment in new ImageData[] { new([], "image/png"), new(null!, "image/png"),
            new(Encoding.UTF8.GetBytes(PrivateMarker), "image/png"), Image(InputRules.MaxImageBytes + 1) })
        {
            var error = Assert.Throws<AppException>(() => Build(chat, "Current", attachment));
            Assert.Equal(400, error.StatusCode);
            Assert.DoesNotContain(PrivateMarker, error.ToString());
        }
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void ImageLookupsCannotUseAnUnscopedOwnerOrRetrieveAnotherOwnersImage()
    {
        var other = store.Create("other-owner");
        AddStoredTurn(other, PrivateMarker, null, new(CoreTests.Png, "image/png"), ownerId: "other-owner");
        var error = Assert.Throws<AppException>(() => Build(other, "Current"));
        Assert.Equal(409, error.StatusCode);
        Assert.DoesNotContain(PrivateMarker, error.ToString());
        Assert.Equal((other.Messages[^1].Id, (string?)Owner), Assert.Single(reads.Calls));
        reads.Calls.Clear();
        foreach (var owner in new[] { null, "", " " })
            Assert.Equal(404, Assert.Throws<AppException>(() => TextChatContext.Build(other, "Current", null, imageStore, owner!, new())).StatusCode);
        Assert.Empty(reads.Calls);
    }

    [Fact]
    public void SeparateChatsNeverShareTextOrImageContext()
    {
        var first = store.Create(Owner);
        AddStoredTurn(first, "First session request", null, new(CoreTests.Png, "image/png"));
        var second = store.Create(Owner);
        AddTextTurn(second, "Second session request", "Second session answer");
        var firstResult = Build(first, "First current");
        var secondResult = Build(second, "Second current");
        Assert.DoesNotContain(firstResult.Input.Messages, message => message.Text.Contains("Second", StringComparison.Ordinal));
        Assert.DoesNotContain(secondResult.Input.Messages, message => message.Text.Contains("First", StringComparison.Ordinal));
        Assert.All(secondResult.Input.Messages, message => Assert.Null(message.Images));
        Assert.Equal((first.Messages[^1].Id, (string?)Owner), Assert.Single(reads.Calls));
    }

    private ChatContextResult Build(Chat chat, string prompt, ImageData? image = null, ChatModelOptions? options = null) =>
        TextChatContext.Build(chat, prompt, image, imageStore, Owner, options ?? new());

    private static ChatMessage Message(string role, string text, string status = "completed", string? error = null,
        string? model = null, string? imageUrl = null) =>
        new(Guid.NewGuid().ToString("N"), role, text, imageUrl, "", "", "", status, error, model);

    private static void AddTextTurn(Chat chat, string prompt, string answer, string status = "completed",
        string? error = null, string? model = ChatModelOptions.ModelId, string? imageUrl = null,
        string userStatus = "completed", string? userError = null)
    {
        chat.Messages.Add(Message("user", prompt, userStatus, userError));
        chat.Messages.Add(Message("assistant", answer, status, error, model, imageUrl));
    }

    private void AddStoredTurn(Chat chat, string prompt, ImageData? attachment, ImageData? generated,
        string? model = ModelCatalog.Gpt, string ownerId = Owner)
    {
        var id = store.BeginTurn(chat, prompt, "1024x1024", "medium", attachment, model: model);
        store.FinishTurn(chat.Id, id, generated, null);
        chat.Messages.AddRange(store.Get(chat.Id, ownerId).Messages.TakeLast(2));
    }

    private static ImageData Image(int length)
    {
        var bytes = new byte[length];
        CoreTests.Png.CopyTo(bytes, 0);
        return new(bytes, "image/png");
    }

    // Forward only image reads to the real isolated SQLite store. DispatchProxy deliberately
    // avoids duplicating IChatStore's unrelated methods while its text-turn API is evolving.
    public class ImageReadSpy : DispatchProxy
    {
        public ChatStore Store { get; set; } = null!;
        public Exception? Failure { get; set; }
        public List<(string Id, string? OwnerId)> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IChatStore.GetImage) || args is not { Length: 2 })
                throw new InvalidOperationException("Context may only perform scoped image lookups.");
            var id = (string)args[0]!;
            var ownerId = (string?)args[1];
            Calls.Add((id, ownerId));
            Assert.False(string.IsNullOrWhiteSpace(ownerId));
            if (Failure is not null) throw Failure;
            return Store.GetImage(id, ownerId);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}