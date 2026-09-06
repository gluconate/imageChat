using Microsoft.Data.Sqlite;

namespace ImageChat;

// One SQLite database contains metadata AND image bytes so a backup stays consistent.
// Storage is intentionally single-instance. Replace this service for horizontal cloud scaling.
public sealed class ChatStore
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
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string ImageUrl(string id) => $"/api/images/{id}";

    public List<ChatSummary> List()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, c.created_at, c.updated_at,
                (SELECT COUNT(*) FROM messages WHERE chat_id=c.id),
                (SELECT id FROM messages WHERE chat_id=c.id AND image IS NOT NULL ORDER BY rowid DESC LIMIT 1)
            FROM chats c ORDER BY c.updated_at DESC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<ChatSummary>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.IsDBNull(5) ? null : ImageUrl(reader.GetString(5))));
        return result;
    }

    public Chat Create()
    {
        var chat = new Chat(Guid.NewGuid().ToString("N"), "New chat", Now(), Now(), []);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO chats VALUES ($id,$title,$created,$updated)";
        command.Parameters.AddWithValue("$id", chat.Id);
        command.Parameters.AddWithValue("$title", chat.Title);
        command.Parameters.AddWithValue("$created", chat.CreatedAt);
        command.Parameters.AddWithValue("$updated", chat.UpdatedAt);
        command.ExecuteNonQuery();
        return chat;
    }

    public Chat Get(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT title,created_at,updated_at FROM chats WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        Chat chat;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new AppException(404, "Chat not found.");
            chat = new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), []);
        }
        command.CommandText = "SELECT id,role,text,created_at,size,quality,status,error,image IS NOT NULL FROM messages WHERE chat_id=$id ORDER BY rowid";
        using var messages = command.ExecuteReader();
        while (messages.Read()) chat.Messages.Add(new(messages.GetString(0), messages.GetString(1), messages.GetString(2),
            messages.GetBoolean(8) ? ImageUrl(messages.GetString(0)) : null, messages.GetString(3), messages.GetString(4),
            messages.GetString(5), messages.GetString(6), messages.IsDBNull(7) ? null : messages.GetString(7)));
        return chat;
    }

    public ImageData? GetImage(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT image,content_type FROM messages WHERE id=$id AND image IS NOT NULL";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new((byte[])reader[0], reader.GetString(1)) : null;
    }

    public ImageData? LatestImage(Chat chat)
    {
        var message = chat.Messages.LastOrDefault(m => m.Role == "assistant" && m.Status == "completed" && m.ImageUrl != null);
        return message == null ? null : GetImage(message.Id);
    }

    public string BeginTurn(Chat chat, string text, string size, string quality, ImageData? attachment)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var assistantId = Guid.NewGuid().ToString("N");
        foreach (var role in new[] { "user", "assistant" })
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO messages VALUES($id,$chat,$role,$text,$created,$size,$quality,$status,NULL,$image,$type)";
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
        transaction.Commit();
    }

    public void Delete(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM chats WHERE id=$id AND NOT EXISTS (SELECT 1 FROM messages WHERE chat_id=$id AND status='pending')";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
        {
            Get(id);
            throw new AppException(409, "Wait for image generation to finish before deleting this chat.");
        }
    }
}