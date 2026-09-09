using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs.Models;

namespace ImageChat;

public sealed partial class AzureChatStore(AzureStorage storage) : IChatStore
{
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string ImageUrl(string id) => $"/api/images/{id}";
    private static AppException Busy() => new(409, "This chat changed or is busy. Refresh it before trying again.");
    private static TableTransactionAction Add(TableEntity entity) => new(TableTransactionActionType.Add, entity);

    private TableEntity Head(string id, string? ownerId = null)
    {
        var head = storage.Find("c" + id);
        if (head == null || head.GetBoolean("Deleted") == true || (ownerId != null && head.GetString("Owner") != ownerId))
            throw new AppException(404, "Chat not found.");
        return head;
    }

    public List<ChatSummary> List(string? ownerId = null) => storage.Rows("c")
        .Where(head => head.GetBoolean("Deleted") != true && (ownerId == null || head.GetString("Owner") == ownerId))
        .Select(RecoverExpiredText)
        .OrderByDescending(head => head.GetString("UpdatedAt"), StringComparer.Ordinal)
        .Select(head => new ChatSummary(head.GetString("Id")!, head.GetString("Title")!, head.GetString("CreatedAt")!,
            head.GetString("UpdatedAt")!, head.GetInt32("Count") ?? 0,
            head.GetString("Thumbnail") is { Length: > 0 } thumbnail ? ImageUrl(thumbnail) : null,
            !string.IsNullOrEmpty(head.GetString("Busy")))).ToList();

    public Chat Create(string? ownerId = null)
    {
        var chat = new Chat(Guid.NewGuid().ToString("N"), "New chat", Now(), Now(), []);
        storage.Table.AddEntity(new TableEntity("app", "c" + chat.Id)
        {
            ["Id"] = chat.Id, ["Owner"] = ownerId ?? "", ["Title"] = chat.Title,
            ["CreatedAt"] = chat.CreatedAt, ["UpdatedAt"] = chat.UpdatedAt, ["Count"] = 0, ["Busy"] = ""
        });
        return chat;
    }

    public Chat Get(string id, string? ownerId = null)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var head = RecoverExpiredText(Head(id, ownerId));
            var messages = storage.Rows("m" + id + ":").Select(row => new ChatMessage(row.GetString("Id")!, row.GetString("Role")!,
                ReadMessageText(row), row.GetString("ContentType") == null ? null : ImageUrl(row.GetString("Id")!),
                row.GetString("CreatedAt")!, row.GetString("Size")!, row.GetString("Quality")!, row.GetString("Status")!, row.GetString("Error"),
                row.GetString("Model"), row.GetString("Deployment"))).ToList();
            if (Head(id, ownerId).ETag != head.ETag) continue;
            return new(id, head.GetString("Title")!, head.GetString("CreatedAt")!, head.GetString("UpdatedAt")!, messages);
        }
        throw Busy();
    }

    public ImageData? GetImage(string id, string? ownerId = null)
    {
        var index = storage.Find("x" + id);
        if (index?.GetString("ContentType") is not { } contentType) return null;
        Head(index.GetString("ChatId")!, ownerId);
        return new(storage.Images.GetBlobClient(id).DownloadContent().Value.Content.ToArray(), contentType);
    }

    public ImageData? LatestImage(Chat chat)
    {
        var message = chat.Messages.LastOrDefault(message => message.Role == "assistant" && message.Status == "completed" && message.ImageUrl != null);
        return message == null ? null : GetImage(message.Id);
    }

    internal void UploadImage(string id, ImageData image)
    {
        try
        {
            storage.Images.GetBlobClient(id).Upload(new BinaryData(image.Bytes), new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = image.ContentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            });
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { }
    }

    public string BeginTurn(Chat chat, string text, string size, string quality, ImageData? attachment,
        string? telemetryId = null, string operation = "unknown", string? model = null, string? deployment = null) =>
        StartTurn(chat, text, size, quality, attachment, telemetryId, operation, null, model, deployment);

    private string StartTurn(Chat chat, string text, string size, string quality, ImageData? attachment,
        string? telemetryId, string operation, GenerationInput? input, string? model = null, string? deployment = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (input != null && model == ChatModelOptions.ModelId)
            throw new AppException(400, "Text turns must not use the image queue.");
        if (operation == "chat")
        {
            if (model != ChatModelOptions.ModelId || size != "text" || quality != "text" || input != null)
                throw new AppException(400, "Text turns require the chat model and text settings.");
            if (attachment != null) attachment = InputRules.ValidateImage(attachment.Bytes);
        }
        var head = RecoverExpiredText(Head(chat.Id));
        if (!string.IsNullOrEmpty(head.GetString("Busy")) || head.GetInt32("Count") != chat.Messages.Count || head.GetString("UpdatedAt") != chat.UpdatedAt)
            throw Busy();
        var assistantId = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid().ToString("N");
        if (attachment != null) UploadImage(userId, attachment);
        // Table strings are limited to 64 KiB of UTF-16. Keep chat prompts (and any
        // other long prompt) in private Work storage, never in an image/public URL.
        var textBlob = operation == "chat" || text.Length > 32000;
        if (textBlob) UploadMessageText(userId, text);
        if (input != null) storage.Work.GetBlobClient("input/" + assistantId).Upload(BinaryData.FromObjectAsJson(input));
        var changes = new List<TableTransactionAction>();
        foreach (var role in new[] { "user", "assistant" })
        {
            var messageId = role == "user" ? userId : assistantId;
            var messageKey = $"m{chat.Id}:{chat.Messages.Count + (role == "user" ? 0 : 1):D10}";
            var message = new TableEntity("app", messageKey)
            {
                ["Id"] = messageId, ["Role"] = role, ["Text"] = role == "user" && !textBlob ? text : "", ["CreatedAt"] = Now(),
                ["Size"] = size, ["Quality"] = quality, ["Status"] = role == "user" ? "completed" : "pending"
            };
            var index = new TableEntity("app", "x" + messageId) { ["ChatId"] = chat.Id, ["MessageKey"] = messageKey };
            if (role == "user" && textBlob) message["TextBlob"] = true;
            if (model != null) message["Model"] = model;
            if (deployment != null) message["Deployment"] = deployment;
            if (role == "user" && attachment != null)
            {
                message["ContentType"] = attachment.ContentType;
                index["ContentType"] = attachment.ContentType;
            }
            if (role == "assistant" && telemetryId != null) index["RequestId"] = telemetryId;
            changes.Add(Add(message));
            changes.Add(Add(index));
        }
        head["Count"] = chat.Messages.Count + 2;
        head["Busy"] = assistantId;
        head["UpdatedAt"] = Now();
        if (operation == "chat") head["TextDeadline"] = DateTimeOffset.UtcNow.AddMinutes(5);
        else head.Remove("TextDeadline");
        if (chat.Messages.Count == 0) head["Title"] = text[..Math.Min(text.Length, 64)];
        changes.Add(AzureStorage.Replace(head));
        if (telemetryId != null)
        {
            var request = storage.Find("r" + telemetryId) ?? throw AzureStorage.Unavailable();
            request["MessageId"] = assistantId;
            request["Operation"] = operation;
            changes.Add(AzureStorage.Replace(request));
        }
        if (input != null) changes.Add(Add(new TableEntity("app", "j" + assistantId)
        {
            ["ChatId"] = chat.Id, ["State"] = "queued", ["CreatedAt"] = DateTimeOffset.UtcNow
        }));
        try { storage.Table.SubmitTransaction(changes); }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { throw Busy(); }
        return assistantId;
    }

    public void FinishTurn(string chatId, string messageId, ImageData? image, string? error)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var index = storage.Find("x" + messageId) ?? throw new AppException(404, "Message not found.");
            if (index.GetString("ChatId") != chatId) throw new AppException(404, "Message not found.");
            var message = storage.Find(index.GetString("MessageKey")!) ?? throw AzureStorage.Unavailable();
            if (message.GetString("Status") != "pending") return;
            var head = Head(chatId);
            if (head.GetString("Busy") != messageId) throw Busy();
            if (error == null && image == null) throw new ArgumentException("A successful turn requires an image.");
            if (error == null && image != null)
            {
                UploadImage(messageId, image);
                message["ContentType"] = image.ContentType;
                index["ContentType"] = image.ContentType;
                head["Thumbnail"] = messageId;
            }
            message["Status"] = error == null ? "completed" : "failed";
            message["Text"] = error == null ? "Here's your image. Tell me what you'd like to change next." : "Image generation failed.";
            if (error != null) message["Error"] = error;
            head["Busy"] = "";
            head["UpdatedAt"] = Now();
            var changes = new List<TableTransactionAction> { AzureStorage.Replace(head), AzureStorage.Replace(message), AzureStorage.Replace(index) };
            if (index.GetString("RequestId") is { } requestId)
            {
                var request = storage.Find("r" + requestId) ?? throw AzureStorage.Unavailable();
                request["Status"] = error == null ? "completed" : "failed";
                request["ImageCount"] = error == null ? 1L : 0L;
                request["CompletedMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                request["HttpStatus"] = error == null ? 200 : 502;
                changes.Add(AzureStorage.Replace(request));
            }
            if (storage.Find("j" + messageId) is { } job)
            {
                job["State"] = error == null ? "completed" : "failed";
                changes.Add(AzureStorage.Replace(job));
            }
            try { storage.Table.SubmitTransaction(changes); return; }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412) { }
        }
        throw Busy();
    }

    public void Delete(string id, string? ownerId = null)
    {
        var head = RecoverExpiredText(Head(id, ownerId));
        if (!string.IsNullOrEmpty(head.GetString("Busy"))) throw Busy();
        head["Deleted"] = true;
        try { storage.Table.UpdateEntity(head, head.ETag, TableUpdateMode.Replace); }
        catch (RequestFailedException exception) when (exception.Status == 412) { throw Busy(); }
        CleanupDeletedChat(id);
    }

    internal void CleanupDeletedChat(string id)
    {
        var rows = storage.Rows("m" + id + ":");
        foreach (var row in rows)
        {
            var messageId = row.GetString("Id")!;
            storage.Images.GetBlobClient(messageId).DeleteIfExists();
            storage.Work.GetBlobClient("input/" + messageId).DeleteIfExists();
            // Delete blobs before metadata, so dispatcher retries can finish partial cleanup.
            storage.Work.GetBlobClient("text/" + messageId).DeleteIfExists();
            storage.Table.DeleteEntity("app", "x" + messageId, ETag.All);
            storage.Table.DeleteEntity("app", row.RowKey, ETag.All);
        }
    }

    public void ClaimLegacyChats(string ownerId) { }

    public string StartRequest(string userId, string username, string? ip)
    {
        var id = Guid.NewGuid().ToString("N");
        var row = new TableEntity("app", "r" + id)
        {
            ["Id"] = id, ["UserId"] = userId, ["Username"] = username, ["RequestedMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["Operation"] = "unknown", ["Status"] = "pending", ["ImageCount"] = 0L
        };
        if (ip != null) row["Ip"] = ip;
        storage.Table.AddEntity(row);
        return id;
    }

    public void SetRequestModel(string requestId, string model, string deployment)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var row = storage.Find("r" + requestId) ?? throw AzureStorage.Unavailable();
            row["Model"] = model;
            row["Deployment"] = deployment;
            try { storage.Table.UpdateEntity(row, row.ETag, TableUpdateMode.Replace); return; }
            catch (RequestFailedException exception) when (exception.Status == 412) { }
        }
        throw AzureStorage.Unavailable();
    }

    public void EndRequest(string id, int httpStatus)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var row = storage.Find("r" + id) ?? throw AzureStorage.Unavailable();
            if (row.GetString("Status") != "pending" || row.GetString("MessageId") != null) return;
            row["Status"] = httpStatus is >= 400 and < 500 ? "rejected" : "failed";
            row["CompletedMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row["HttpStatus"] = httpStatus;
            try { storage.Table.UpdateEntity(row, row.ETag, TableUpdateMode.Replace); return; }
            catch (RequestFailedException exception) when (exception.Status == 412) { }
        }
        throw AzureStorage.Unavailable();
    }

    public TelemetryReport Report(TelemetryFilter filter)
    {
        var rows = storage.Rows("r").Where(row => (filter.From == null || row.GetInt64("RequestedMs") >= filter.From) &&
            (filter.To == null || row.GetInt64("RequestedMs") < filter.To) && (filter.Ip == null || row.GetString("Ip") == filter.Ip))
            .OrderByDescending(row => row.GetInt64("RequestedMs")).ThenByDescending(row => row.RowKey, StringComparer.Ordinal).ToList();
        long Images(string operation) => rows.Where(row => row.GetString("Status") == "completed" && row.GetString("Operation") == operation)
            .Sum(row => row.GetInt64("ImageCount") ?? 0);
        var summary = new TelemetrySummary(rows.Count, Images("generation"), Images("edit"), 0,
            rows.Sum(row => row.GetInt64("ImageCount") ?? 0), rows.LongCount(row => row.GetString("Status") is "failed" or "interrupted"),
            rows.LongCount(row => row.GetString("Status") == "rejected"), rows.LongCount(row => row.GetString("Status") == "pending"));
        var items = rows.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).Select(row => new TelemetryItem(
            row.GetString("Id")!, row.GetString("UserId"), row.GetString("Username"), row.GetString("Ip"),
            DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64("RequestedMs")!.Value).ToString("O"),
            row.GetInt64("CompletedMs") is { } completed ? DateTimeOffset.FromUnixTimeMilliseconds(completed).ToString("O") : null,
            row.GetString("Operation")!, row.GetString("Status")!, row.GetInt64("ImageCount") ?? 0, row.GetInt32("HttpStatus"),
            row.GetString("Model"), row.GetString("Deployment"))).ToList();
        return new(summary, items, filter.Page, filter.PageSize, rows.Count);
    }
}