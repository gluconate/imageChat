using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs.Models;

namespace ImageChat;

public sealed partial class AzureChatStore
{
    private const string InterruptedText = "The text response was interrupted before it finished. Review the saved partial response before submitting again.";

    private void UploadMessageText(string messageId, string text) =>
        storage.Work.GetBlobClient("text/" + messageId).Upload(BinaryData.FromString(text), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "text/plain; charset=utf-8" },
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
        });

    // Only called after Head has authorized the chat. The flag is not a caller-controlled URL.
    private string ReadMessageText(TableEntity message) => message.GetBoolean("TextBlob") == true
        ? storage.Work.GetBlobClient("text/" + message.GetString("Id")!).DownloadContent().Value.Content.ToString()
        : message.GetString("Text") ?? "";

    public void UpdateTextTurn(string chatId, string messageId, string text)
    {
        ValidateTextReply(text);
        SaveTextTurn(chatId, messageId, text, null, 200, finish: false);
    }

    public void FinishTextTurn(string chatId, string messageId, string text, string? error, int httpStatus = 200)
    {
        ValidateTextReply(text);
        SaveTextTurn(chatId, messageId, text, error, httpStatus, finish: true);
    }

    private static void ValidateTextReply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > ResponsesChatClient.MaxResponseCharacters)
            throw new AppException(400, "Text replies must not exceed 12,000 characters.");
    }

    private static bool IsTextAssistant(TableEntity message) =>
        message.GetString("Role") == "assistant" && message.GetString("Model") == ChatModelOptions.ModelId &&
        message.GetString("Size") == "text" && message.GetString("Quality") == "text" && message.GetString("ContentType") == null;

    // Pin recovery to this exact busy message and fixed deadline. Both the message and head
    // ETags participate in the completion transaction: a stale recovery cannot win over
    // a completion or release a newer turn (especially an active image generation).
    internal TableEntity RecoverExpiredText(TableEntity head)
    {
        if (head.GetBoolean("Deleted") == true || string.IsNullOrEmpty(head.GetString("Busy")) ||
            head.GetDateTimeOffset("TextDeadline") is not { } deadline || deadline > DateTimeOffset.UtcNow)
            return head;
        SaveTextTurn(head.GetString("Id")!, head.GetString("Busy")!, "", InterruptedText, 503, finish: true, expiredHead: head);
        return Head(head.GetString("Id")!);
    }

    private void SaveTextTurn(string chatId, string messageId, string text, string? error, int httpStatus,
        bool finish, TableEntity? expiredHead = null)
    {
        // The entity itself is mutated below; retain the original deadline across CAS retries.
        var expectedDeadline = expiredHead?.GetDateTimeOffset("TextDeadline");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var head = attempt == 0 && expiredHead != null ? expiredHead : Head(chatId);
            if (expiredHead != null && (head.GetString("Busy") != messageId ||
                head.GetDateTimeOffset("TextDeadline") != expectedDeadline ||
                head.GetDateTimeOffset("TextDeadline") is not { } deadline || deadline > DateTimeOffset.UtcNow)) return;

            var index = storage.Find("x" + messageId);
            var message = index?.GetString("ChatId") == chatId && index.GetString("MessageKey") is { } key
                ? storage.Find(key) : null;
            if (message == null || message.GetString("Id") != messageId || !IsTextAssistant(message))
            {
                if (expiredHead != null) return; // Never recover an image turn or an unknown message.
                throw new AppException(404, "Text message not found.");
            }
            if (message.GetString("Status") != "pending") return;
            if (head.GetString("Busy") != messageId) throw Busy();
            var saved = message.GetString("Text") ?? "";
            if (!finish && (text.Length < saved.Length || text == saved)) return;
            // Recovery always takes the latest durable partial, including an update that
            // raced a previous CAS attempt. Only a successful final response is canonical.
            var next = expiredHead != null || (finish && error != null && text.Length < saved.Length) ? saved : text;
            message["Text"] = next;
            head["UpdatedAt"] = Now();
            var changes = new List<TableTransactionAction>();
            if (finish)
            {
                message["Status"] = error == null ? "completed" : "failed";
                if (error != null) message["Error"] = error;
                else message.Remove("Error");
                head["Busy"] = "";
                head.Remove("TextDeadline");
                if (index!.GetString("RequestId") is { } requestId)
                {
                    var request = storage.Find("r" + requestId) ?? throw AzureStorage.Unavailable();
                    request["Operation"] = "chat";
                    request["Status"] = error == null ? "completed" : "failed";
                    request["ImageCount"] = 0L;
                    request["CompletedMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    request["HttpStatus"] = httpStatus;
                    changes.Add(AzureStorage.Replace(request));
                }
            }
            changes.Add(AzureStorage.Replace(head));
            changes.Add(AzureStorage.Replace(message));
            try { storage.Table.SubmitTransaction(changes); return; }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412) { }
        }
        throw Busy();
    }
}