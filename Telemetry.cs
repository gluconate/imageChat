using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;

namespace ImageChat;

public sealed record TelemetrySummary(long Requests, long GeneratedImages, long EditedImages, long LegacyImages,
    long TotalImages, long Failed, long Rejected, long Pending);
public sealed record TelemetryItem(string Id, string? UserId, string? Username, string? Ip, string RequestedAt,
    string? CompletedAt, string Operation, string Status, long ImageCount, int? HttpStatus, string? Model = null, string? Deployment = null);
public sealed record TelemetryReport(TelemetrySummary Summary, List<TelemetryItem> Items, int Page, int PageSize, long Total);
public sealed record TelemetryFilter(long? From, long? To, string? Ip, int Page = 1, int PageSize = 50)
{
    public static TelemetryFilter Parse(string? from, string? to, string? ip, int? page, int? pageSize)
    {
        static long? Timestamp(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            // Require an explicit timezone; never interpret ambiguous server-local input.
            if (value.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(value, @"(Z|[+-]\d{2}:\d{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new AppException(400, "Use ISO-8601 timestamps with a timezone for from/to filters.");
            return parsed.ToUnixTimeMilliseconds();
        }
        var start = Timestamp(from);
        var end = Timestamp(to);
        if (start != null && end != null && start >= end) throw new AppException(400, "The from time must be earlier than the to time.");
        string? address = null;
        if (!string.IsNullOrWhiteSpace(ip))
        {
            if (ip.Length > 64 || !IPAddress.TryParse(ip.Trim(), out var parsed)) throw new AppException(400, "Enter a valid IPv4 or IPv6 address.");
            address = NormalizeIp(parsed);
        }
        if (page is < 1 or > 1000000 || pageSize is < 1 or > 100) throw new AppException(400, "Page must be 1–1,000,000 and pageSize 1–100.");
        return new(start, end, address, page ?? 1, pageSize ?? 50);
    }

    public static string? NormalizeIp(IPAddress? ip) => ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4().ToString() : ip?.ToString();
}

public sealed partial class ChatStore
{
    private static void InitializeTelemetry(SqliteConnection db)
    {
        using var columns = db.CreateCommand();
        columns.CommandText = "PRAGMA table_info(chats)";
        var hasOwner = false;
        using (var reader = columns.ExecuteReader())
            while (reader.Read()) if (reader.GetString(1) == "owner_id") hasOwner = true;
        if (!hasOwner)
        {
            columns.CommandText = "ALTER TABLE chats ADD COLUMN owner_id TEXT";
            columns.ExecuteNonQuery();
        }
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS chats_owner ON chats(owner_id);
            CREATE TABLE IF NOT EXISTS image_requests (
                id TEXT PRIMARY KEY, message_id TEXT UNIQUE, user_id TEXT, username TEXT, ip TEXT,
                requested_ms INTEGER NOT NULL, completed_ms INTEGER, operation TEXT NOT NULL,
                status TEXT NOT NULL, image_count INTEGER NOT NULL DEFAULT 0, http_status INTEGER);
            CREATE INDEX IF NOT EXISTS image_requests_time ON image_requests(requested_ms);
            CREATE INDEX IF NOT EXISTS image_requests_ip_time ON image_requests(ip,requested_ms);
            CREATE TABLE IF NOT EXISTS app_migrations(name TEXT PRIMARY KEY);
            UPDATE image_requests SET status='interrupted',completed_ms=$now
                WHERE status='pending';
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        EnsureColumn(db, "image_requests", "model");
        EnsureColumn(db, "image_requests", "deployment");
        // Existing output records have no reliable source IP or generation-vs-edit metadata.
        // Backfill once, explicitly labelled legacy, without inventing request information.
        using var transaction = db.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM app_migrations WHERE name='telemetry-v1'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = """
                INSERT OR IGNORE INTO image_requests(id,message_id,user_id,username,ip,requested_ms,completed_ms,operation,status,image_count,http_status)
                SELECT 'legacy-'||m.id,m.id,c.owner_id,NULL,NULL,
                    CAST(unixepoch(m.created_at)*1000 AS INTEGER),CAST(unixepoch(m.created_at)*1000 AS INTEGER),
                    'unknown','legacy',1,NULL FROM messages m JOIN chats c ON c.id=m.chat_id
                    WHERE m.role='assistant' AND m.status='completed' AND m.image IS NOT NULL;
                INSERT INTO app_migrations VALUES('telemetry-v1');
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void ClaimLegacyChats(string ownerId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE chats SET owner_id=$owner WHERE owner_id IS NULL";
        command.Parameters.AddWithValue("$owner", ownerId);
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection db, string table, string column)
    {
        // table/column names are internal constants, never HTTP inputs.
        using var command = db.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (reader.GetString(1) == column) return;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT";
        command.ExecuteNonQuery();
    }

    public void SetRequestModel(string requestId, string model, string deployment)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE image_requests SET model=$model,deployment=$deployment WHERE id=$id";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$deployment", deployment);
        command.ExecuteNonQuery();
    }

    public string StartRequest(string userId, string username, string? ip)
    {
        var id = Guid.NewGuid().ToString("N");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO image_requests(id,user_id,username,ip,requested_ms,operation,status) VALUES($id,$user,$name,$ip,$time,'unknown','pending')";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$name", username);
        command.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        return id;
    }

    // Called after the HTTP pipeline. Successful images are already committed atomically
    // with FinishTurn; rejected/invalid/throttled attempts still get an outcome here.
    public void EndRequest(string id, int httpStatus)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE image_requests SET
                status=CASE WHEN status='pending' THEN $status ELSE status END,
                completed_ms=COALESCE(completed_ms,$time),
                http_status=COALESCE(http_status,$http) WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$http", httpStatus);
        command.Parameters.AddWithValue("$status", httpStatus is >= 400 and < 500 ? "rejected" : "failed");
        command.ExecuteNonQuery();
    }

    public TelemetryReport Report(TelemetryFilter filter)
    {
        using var db = Open();
        // Snapshot both aggregation and the page so concurrent completions cannot split totals.
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        const string where = " WHERE ($from IS NULL OR requested_ms >= $from) AND ($to IS NULL OR requested_ms < $to) AND ($ip IS NULL OR ip=$ip)";
        command.Parameters.AddWithValue("$from", (object?)filter.From ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)filter.To ?? DBNull.Value);
        command.Parameters.AddWithValue("$ip", (object?)filter.Ip ?? DBNull.Value);
        command.CommandText = """
            SELECT COUNT(*),
                COALESCE(SUM(CASE WHEN status='completed' AND operation='generation' THEN image_count ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN status='completed' AND operation='edit' THEN image_count ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN status='legacy' THEN image_count ELSE 0 END),0),
                COALESCE(SUM(image_count),0),
                COALESCE(SUM(CASE WHEN status IN ('failed','interrupted') THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN status='rejected' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END),0)
            FROM image_requests
            """ + where;
        TelemetrySummary summary;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            summary = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
        }
        command.CommandText = "SELECT id,user_id,username,ip,requested_ms,completed_ms,operation,status,image_count,http_status,model,deployment FROM image_requests" + where +
            " ORDER BY requested_ms DESC,id DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", filter.PageSize);
        command.Parameters.AddWithValue("$offset", (long)(filter.Page - 1) * filter.PageSize);
        using var items = command.ExecuteReader();
        var result = new List<TelemetryItem>();
        while (items.Read()) result.Add(new(items.GetString(0), items.IsDBNull(1) ? null : items.GetString(1),
            items.IsDBNull(2) ? null : items.GetString(2), items.IsDBNull(3) ? null : items.GetString(3),
            DateTimeOffset.FromUnixTimeMilliseconds(items.GetInt64(4)).ToString("O"),
            items.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(items.GetInt64(5)).ToString("O"),
            items.GetString(6), items.GetString(7), items.GetInt64(8), items.IsDBNull(9) ? null : items.GetInt32(9),
            items.IsDBNull(10) ? null : items.GetString(10), items.IsDBNull(11) ? null : items.GetString(11)));
        return new(summary, result, filter.Page, filter.PageSize, summary.Requests);
    }
}