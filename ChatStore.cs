using Microsoft.Data.Sqlite;

namespace ImageChat;

// One SQLite database contains metadata AND image bytes so a backup stays consistent.
// Storage is intentionally single-instance. Replace this service for horizontal cloud scaling.
public sealed partial class ChatStore : IChatStore
{
    private readonly string connectionString;
    public ChatStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "imagechat.db"), ForeignKeys = true }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS chats(id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages(id TEXT PRIMARY KEY, chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                role TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL, size TEXT NOT NULL, quality TEXT NOT NULL,
                status TEXT NOT NULL, error TEXT, image BLOB, content_type TEXT);
            CREATE INDEX IF NOT EXISTS messages_chat ON messages(chat_id);
            UPDATE messages SET status='failed', error='The application restarted before this generation finished. You can submit the request again.' WHERE status='pending';
            """;
        command.ExecuteNonQuery();
        InitializeTelemetry(db);
        EnsureColumn(db, "messages", "model");
        EnsureColumn(db, "messages", "deployment");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string ImageUrl(string id) => $"/api/images/{id}";

    public List<ChatSummary> List(string? ownerId = null)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, c.created_at, c.updated_at,
                (SELECT COUNT(*) FROM messages WHERE chat_id=c.id),
                (SELECT id FROM messages WHERE chat_id=c.id AND image IS NOT NULL ORDER BY rowid DESC LIMIT 1),
                EXISTS (SELECT 1 FROM messages WHERE chat_id=c.id AND status='pending')
            FROM chats c WHERE $owner IS NULL OR c.owner_id=$owner ORDER BY c.updated_at DESC;
            """;
        command.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<ChatSummary>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.IsDBNull(5) ? null : ImageUrl(reader.GetString(5)), reader.GetBoolean(6)));
        return result;
    }

    public Chat Create(string? ownerId = null)
    {
        var chat = new Chat(Guid.NewGuid().ToString("N"), "New chat", Now(), Now(), []);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO chats (id,title,created_at,updated_at,owner_id) VALUES ($id,$title,$created,$updated,$owner)";
        command.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", chat.Id);
        command.Parameters.AddWithValue("$title", chat.Title);
        command.Parameters.AddWithValue("$created", chat.CreatedAt);
        command.Parameters.AddWithValue("$updated", chat.UpdatedAt);
        command.ExecuteNonQuery();
        return chat;
    }

    public Chat Get(string id, string? ownerId = null)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT title,created_at,updated_at FROM chats WHERE id=$id AND ($owner IS NULL OR owner_id=$owner)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
        Chat chat;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new AppException(404, "Chat not found.");
            chat = new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), []);
        }
        command.CommandText = "SELECT id,role,text,created_at,size,quality,status,error,image IS NOT NULL,model,deployment FROM messages WHERE chat_id=$id ORDER BY rowid";
        using var messages = command.ExecuteReader();
        while (messages.Read()) chat.Messages.Add(new(messages.GetString(0), messages.GetString(1), messages.GetString(2),
            messages.GetBoolean(8) ? ImageUrl(messages.GetString(0)) : null, messages.GetString(3), messages.GetString(4),
            messages.GetString(5), messages.GetString(6), messages.IsDBNull(7) ? null : messages.GetString(7),
            messages.IsDBNull(9) ? null : messages.GetString(9), messages.IsDBNull(10) ? null : messages.GetString(10)));
        return chat;
    }

    public ImageData? GetImage(string id, string? ownerId = null)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT image,content_type FROM messages m JOIN chats c ON c.id=m.chat_id WHERE m.id=$id AND image IS NOT NULL AND ($owner IS NULL OR c.owner_id=$owner)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new((byte[])reader[0], reader.GetString(1)) : null;
    }

    public ImageData? LatestImage(Chat chat)
    {
        var message = chat.Messages.LastOrDefault(m => m.Role == "assistant" && m.Status == "completed" && m.ImageUrl != null);
        return message == null ? null : GetImage(message.Id);
    }

    public string BeginTurn(Chat chat, string text, string size, string quality, ImageData? attachment, string? telemetryId = null,
        string operation = "unknown", string? model = null, string? deployment = null)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var assistantId = Guid.NewGuid().ToString("N");
        foreach (var role in new[] { "user", "assistant" })
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO messages(id,chat_id,role,text,created_at,size,quality,status,error,image,content_type,model,deployment) VALUES($id,$chat,$role,$text,$created,$size,$quality,$status,NULL,$image,$type,$model,$deployment)";
            command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
            command.Parameters.AddWithValue("$deployment", (object?)deployment ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", role == "user" ? Guid.NewGuid().ToString("N") : assistantId);
            command.Parameters.AddWithValue("$chat", chat.Id);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$text", role == "user" ? text : "");
            command.Parameters.AddWithValue("$created", Now());
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$quality", quality);
            command.Parameters.AddWithValue("$status", role == "user" ? "completed" : "pending");
            command.Parameters.Add("$image", SqliteType.Blob).Value = role == "user" && attachment != null ? attachment.Bytes : DBNull.Value;
            command.Parameters.AddWithValue("$type", role == "user" && attachment != null ? attachment.ContentType : DBNull.Value);
            command.ExecuteNonQuery();
        }
        using var update = db.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE chats SET title=$title,updated_at=$now WHERE id=$id";
        update.Parameters.AddWithValue("$title", chat.Messages.Count == 0 ? text[..Math.Min(text.Length, 64)] : chat.Title);
        update.Parameters.AddWithValue("$now", Now());
        update.Parameters.AddWithValue("$id", chat.Id);
        update.ExecuteNonQuery();
        if (telemetryId != null)
        {
            using var telemetry = db.CreateCommand();
            telemetry.Transaction = transaction;
            telemetry.CommandText = "UPDATE image_requests SET message_id=$message,operation=$operation WHERE id=$id";
            telemetry.Parameters.AddWithValue("$message", assistantId);
            telemetry.Parameters.AddWithValue("$operation", operation);
            telemetry.Parameters.AddWithValue("$id", telemetryId);
            telemetry.ExecuteNonQuery();
        }
        transaction.Commit();
        return assistantId;
    }

    public void FinishTurn(string chatId, string messageId, ImageData? image, string? error)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE messages SET status=$status,text=$text,error=$error,image=$image,content_type=$type WHERE id=$id; UPDATE chats SET updated_at=$now WHERE id=$chat";
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$chat", chatId);
        command.Parameters.AddWithValue("$status", error == null ? "completed" : "failed");
        command.Parameters.AddWithValue("$text", error == null ? "Here's your image. Tell me what you'd like to change next." : "Image generation failed.");
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.Add("$image", SqliteType.Blob).Value = image != null ? image.Bytes : DBNull.Value;
        command.Parameters.AddWithValue("$type", (object?)image?.ContentType ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
        using var telemetry = db.CreateCommand();
        telemetry.Transaction = transaction;
        telemetry.CommandText = "UPDATE image_requests SET status=$status,image_count=$count,completed_ms=$now,http_status=$http WHERE message_id=$message AND status='pending'";
        telemetry.Parameters.AddWithValue("$status", error == null ? "completed" : "failed");
        telemetry.Parameters.AddWithValue("$count", error == null && image != null ? 1 : 0);
        telemetry.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        telemetry.Parameters.AddWithValue("$http", error == null ? 200 : DBNull.Value);
        telemetry.Parameters.AddWithValue("$message", messageId);
        telemetry.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Delete(string id, string? ownerId = null)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM chats WHERE id=$id AND ($owner IS NULL OR owner_id=$owner) AND NOT EXISTS (SELECT 1 FROM messages WHERE chat_id=$id AND status='pending')";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
        if (command.ExecuteNonQuery() == 0)
        {
            Get(id, ownerId);
            throw new AppException(409, "Wait for image generation to finish before deleting this chat.");
        }
    }
}