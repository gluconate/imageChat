using Microsoft.Data.Sqlite;

namespace ImageChat;

public sealed partial class ChatStore
{
    // BeginTransaction is immediate: validation and writes share one SQLite writer lock.
    private static void CheckTextBegin(SqliteConnection db, SqliteTransaction transaction, Chat chat)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT updated_at, (SELECT COUNT(*) FROM messages WHERE chat_id=$id),
                EXISTS (SELECT 1 FROM messages WHERE chat_id=$id AND status='pending')
            FROM chats WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", chat.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new AppException(404, "Chat not found.");
        if (reader.GetString(0) != chat.UpdatedAt || reader.GetInt32(1) != chat.Messages.Count || reader.GetBoolean(2))
            throw new AppException(409, "This chat changed or is busy. Refresh it before trying again.");
    }

    /// <summary>Persist a full streaming snapshot, never a delta. Shorter snapshots are ignored.</summary>
    public void UpdateTextTurn(string chatId, string messageId, string text) =>
        SaveTextTurn(chatId, messageId, text, null, 200, finish: false);

    /// <summary>Successful text is canonical; a failure keeps the longest saved partial snapshot.</summary>
    public void FinishTextTurn(string chatId, string messageId, string text, string? error, int httpStatus = 200) =>
        SaveTextTurn(chatId, messageId, text, error, httpStatus, finish: true);

    private void SaveTextTurn(string chatId, string messageId, string text, string? error, int httpStatus, bool finish)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > ResponsesChatClient.MaxResponseCharacters)
            throw new AppException(400, "Text replies must not exceed 12,000 characters.");
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT text,status FROM messages WHERE id=$id AND chat_id=$chat AND role='assistant'
                AND model=$model AND size='text' AND quality='text' AND image IS NULL;
            """;
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$chat", chatId);
        command.Parameters.AddWithValue("$model", ChatModelOptions.ModelId);
        string saved;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new AppException(404, "Text message not found.");
            if (reader.GetString(1) != "pending") return;
            saved = reader.GetString(0);
        }
        if (!finish && (text.Length < saved.Length || text == saved)) return;
        if (finish && error != null && text.Length < saved.Length) text = saved;
        command.CommandText = """
            UPDATE messages SET text=$text,status=$status,error=$error WHERE id=$id AND chat_id=$chat;
            UPDATE chats SET updated_at=$now WHERE id=$chat;
            """;
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$status", finish ? error == null ? "completed" : "failed" : "pending");
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
        if (finish)
        {
            using var telemetry = db.CreateCommand();
            telemetry.Transaction = transaction;
            telemetry.CommandText = """
                UPDATE image_requests SET operation='chat',status=$status,image_count=0,
                    completed_ms=$now,http_status=$http WHERE message_id=$message;
                """;
            telemetry.Parameters.AddWithValue("$status", error == null ? "completed" : "failed");
            telemetry.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            telemetry.Parameters.AddWithValue("$http", httpStatus);
            telemetry.Parameters.AddWithValue("$message", messageId);
            telemetry.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}