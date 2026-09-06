using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ImageChat;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ImageChat.Tests;

public class TelemetryTests
{
    private const string ClientIp = "203.0.113.5";

    [Fact]
    public async Task GeneratedFollowupAndImageOnlyOutputsAreCountedOnceWithCorrectOperation()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await Authenticated(factory);
        var chat = await CreateChat(admin);
        await SendImage(admin, chat.Id, "First generation");
        await SendImage(admin, chat.Id, "Follow-up edit");
        using var imageOnly = ApiTests.Form("");
        imageOnly.Add(new ByteArrayContent(CoreTests.Png), "image", "fixture.png");
        using var uploaded = await admin.PostAsync($"/api/chats/{chat.Id}/messages", imageOnly);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        await SendImage(admin, chat.Id, "Independent generation", usePrevious: false);

        var report = await Report(admin);
        Assert.Equal(new TelemetrySummary(4, 2, 2, 0, 4, 0, 0, 0), report.Summary);
        Assert.Equal(4L, report.Total);
        Assert.Equal(4, report.Items.Count);
        Assert.Equal(2, report.Items.Count(item => item.Operation == "generation"));
        Assert.Equal(2, report.Items.Count(item => item.Operation == "edit"));
        Assert.All(report.Items, item =>
        {
            Assert.Equal("completed", item.Status);
            Assert.Equal(1L, item.ImageCount);
            Assert.Equal(200, item.HttpStatus);
            Assert.Equal("testadmin", item.Username);
            Assert.False(string.IsNullOrEmpty(item.UserId));
            Assert.NotNull(item.CompletedAt);
            Assert.True(ParseTime(item.CompletedAt!) >= ParseTime(item.RequestedAt));
        });
        var inputs = factory.Generator.Inputs;
        Assert.Equal(4, inputs.Length);
        Assert.Null(inputs[0].Image);
        Assert.Equal(CoreTests.Png, inputs[1].Image!.Bytes);
        Assert.Equal(CoreTests.Png, inputs[2].Image!.Bytes);
        Assert.Contains(InputRules.ImageOnlyPrompt, inputs[2].Prompt);
        Assert.Null(inputs[3].Image);
        var stored = (await admin.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!;
        Assert.Equal(5, stored.Messages.Count(message => message.ImageUrl != null)); // Four outputs plus the upload.
    }

    [Fact]
    public async Task ModelFailuresAndValidationOrMissingChatRejectionsNeverCountImages()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await Authenticated(factory);
        var chat = await CreateChat(admin);
        factory.Generator.Fail = true;
        using var generation = ApiTests.Form("Fail generation");
        using var failed = await admin.PostAsync($"/api/chats/{chat.Id}/messages", generation);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        using var edit = ApiTests.Form("");
        edit.Add(new ByteArrayContent(CoreTests.Png), "image", "fixture.png");
        using var failedEdit = await admin.PostAsync($"/api/chats/{chat.Id}/messages", edit);
        Assert.Equal(HttpStatusCode.BadGateway, failedEdit.StatusCode);
        using var invalid = ApiTests.Form("");
        using var rejected = await admin.PostAsync($"/api/chats/{chat.Id}/messages", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var missingForm = ApiTests.Form("Missing chat");
        using var missing = await admin.PostAsync($"/api/chats/{Guid.NewGuid():N}/messages", missingForm);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var report = await Report(admin);
        Assert.Equal(new TelemetrySummary(4, 0, 0, 0, 0, 2, 2, 0), report.Summary);
        var modelFailures = report.Items.Where(item => item.Status == "failed").ToArray();
        Assert.Equal(2, modelFailures.Length);
        Assert.Equal(new[] { "edit", "generation" }, modelFailures.Select(item => item.Operation).OrderBy(operation => operation).ToArray());
        Assert.All(modelFailures, item => Assert.Equal(502, item.HttpStatus));
        var rejections = report.Items.Where(item => item.Status == "rejected").ToArray();
        Assert.Equal(new int?[] { 400, 404 }, rejections.Select(item => item.HttpStatus).OrderBy(status => status).ToArray());
        Assert.All(rejections, item => Assert.Equal("unknown", item.Operation));
        Assert.All(report.Items, item =>
        {
            Assert.Equal(0L, item.ImageCount);
            Assert.NotNull(item.CompletedAt);
        });
        Assert.Equal(2, factory.Generator.Inputs.Length);
        var stored = (await admin.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!;
        Assert.Equal(4, stored.Messages.Count);
        Assert.Single(stored.Messages, message => message.ImageUrl != null); // Failed edit's input is not an output.
    }

    [Fact]
    public async Task FiveGenerationPermitsAreSharedAcrossUsersAndThrottledAttemptsAreRecorded()
    {
        await using var factory = new ApiTests.AppFactory { TrustTestProxy = true };
        using var admin = await Authenticated(factory);
        using var user = await Authenticated(factory, "testuser");
        // Different authenticated users and resolved IPs must still share the model budget.
        user.DefaultRequestHeaders.Add("X-Forwarded-For", ClientIp);
        user.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        var adminChat = await CreateChat(admin);
        var userChat = await CreateChat(user);
        for (var attempt = 0; attempt < 5; attempt++)
            await SendImage(attempt % 2 == 0 ? admin : user, attempt % 2 == 0 ? adminChat.Id : userChat.Id,
                $"Shared-budget generation {attempt}", usePrevious: false);
        foreach (var (client, chat) in new[] { (user, userChat), (admin, adminChat) })
        {
            using var form = ApiTests.Form("Over shared limit");
            using var response = await client.PostAsync($"/api/chats/{chat.Id}/messages", form);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        Assert.Equal(5, factory.Generator.Inputs.Length);
        var report = await Report(admin);
        Assert.Equal(new TelemetrySummary(7, 5, 0, 0, 5, 0, 2, 0), report.Summary);
        Assert.Equal(7L, report.Total);
        var rejected = report.Items.Where(item => item.Status == "rejected").ToArray();
        Assert.Equal(2, rejected.Length);
        Assert.All(rejected, item =>
        {
            Assert.Equal(429, item.HttpStatus);
            Assert.Equal("unknown", item.Operation);
            Assert.Equal(0L, item.ImageCount);
            Assert.NotNull(item.CompletedAt);
        });
        Assert.Equal(new[] { "testadmin", "testuser" }, rejected.Select(item => item.Username).OrderBy(name => name).ToArray());
        Assert.Equal(new TelemetrySummary(3, 2, 0, 0, 2, 0, 1, 0), (await Report(admin, "?ip=" + ClientIp)).Summary);
        Assert.Equal(new TelemetrySummary(4, 3, 0, 0, 3, 0, 1, 0), (await Report(admin, "?ip=127.0.0.1")).Summary);
        Assert.Equal(6, (await admin.GetFromJsonAsync<Chat>($"/api/chats/{adminChat.Id}"))!.Messages.Count);
        Assert.Equal(4, (await user.GetFromJsonAsync<Chat>($"/api/chats/{userChat.Id}"))!.Messages.Count);
    }

    [Fact]
    public async Task FilteredTotalsAndStablePagesSurviveChatDeletionAndHonorHalfOpenUtcRanges()
    {
        await using var factory = new ApiTests.AppFactory { TrustTestProxy = true };
        using var admin = await Authenticated(factory);
        admin.DefaultRequestHeaders.Add("X-Forwarded-For", ClientIp);
        admin.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        var chat = await CreateChat(admin);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 3; i++) await SendImage(admin, chat.Id, $"Filtered generation {i}", usePrevious: false);
        var afterExclusive = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1;
        var query = $"?from={UtcQuery(before)}&to={UtcQuery(afterExclusive)}&ip={ClientIp}";
        var full = await Report(admin, query);
        Assert.Equal(new TelemetrySummary(3, 3, 0, 0, 3, 0, 0, 0), full.Summary);
        Assert.Equal(3, full.Items.Count);
        Assert.All(full.Items, item =>
        {
            Assert.Equal(ClientIp, item.Ip);
            Assert.InRange(ParseTime(item.RequestedAt), before, afterExclusive - 1);
        });
        using var deletion = await admin.DeleteAsync($"/api/chats/{chat.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!);
        var retained = await Report(admin, query);
        Assert.Equal(full.Summary, retained.Summary);
        Assert.Equal(full.Items.ToArray(), retained.Items.ToArray());
        var pageIds = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await Report(admin, query + $"&page={page}&pageSize=1");
            Assert.Equal(page, result.Page);
            Assert.Equal(1, result.PageSize);
            Assert.Equal(3L, result.Total);
            Assert.Equal(full.Summary, result.Summary);
            if (page <= 3) pageIds.Add(Assert.Single(result.Items).Id);
            else Assert.Empty(result.Items);
        }
        Assert.Equal(full.Items.Select(item => item.Id).ToArray(), pageIds.ToArray());
        Assert.Equal(3, pageIds.Distinct().Count());

        // Derive boundaries from persisted rows; tied millisecond timestamps are valid.
        var earliest = full.Items.Min(item => ParseTime(item.RequestedAt));
        var latest = full.Items.Max(item => ParseTime(item.RequestedAt));
        var inclusive = await Report(admin, $"?from={UtcQuery(earliest)}&to={UtcQuery(latest + 1)}&ip={ClientIp}");
        Assert.Equal(3L, inclusive.Total);
        var atLatest = await Report(admin, $"?from={UtcQuery(latest)}&to={UtcQuery(latest + 1)}&ip={ClientIp}");
        Assert.Equal(full.Items.LongCount(item => ParseTime(item.RequestedAt) == latest), atLatest.Total);
        var exclusive = await Report(admin, $"?to={UtcQuery(latest)}&ip={ClientIp}");
        Assert.Equal(full.Items.LongCount(item => ParseTime(item.RequestedAt) < latest), exclusive.Total);
        var future = await Report(admin, $"?from={UtcQuery(latest + 1)}");
        Assert.Equal(0L, future.Total);
        var wrongIp = await Report(admin, "?ip=127.0.0.1");
        Assert.Equal(new TelemetrySummary(0, 0, 0, 0, 0, 0, 0, 0), wrongIp.Summary);
        var mapped = await Report(admin, "?ip=" + Uri.EscapeDataString("::ffff:" + ClientIp));
        Assert.Equal(full.Summary, mapped.Summary);
    }

    [Fact]
    public async Task InvalidTimeIpAndPaginationFiltersReturnBadRequest()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await Authenticated(factory);
        foreach (var query in new[]
                 {
                     "?from=not-a-time", "?from=2026-01-01T00%3A00%3A00", "?to=not-a-time",
                     "?from=2026-01-02T00%3A00%3A00Z&to=2026-01-01T00%3A00%3A00Z",
                     "?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A00%3A00Z",
                     "?ip=not-an-ip", "?ip=203.0.113.999", "?ip=203.0.113.5%2F24",
                     "?page=0", "?page=-1", "?page=1000001", "?pageSize=0", "?pageSize=101", "?page=invalid"
                 })
        {
            using var response = await admin.GetAsync("/api/admin/telemetry" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0L, (await Report(admin)).Total);
        Assert.Empty(factory.Generator.Inputs);
    }

    [Fact]
    public async Task OnlyExplicitlyTrustedProxyCanReplaceTheLoopbackClientAddress()
    {
        foreach (var trusted in new[] { false, true })
        {
            await using var factory = new ApiTests.AppFactory { TrustTestProxy = trusted };
            using var admin = await Authenticated(factory);
            admin.DefaultRequestHeaders.Add("X-Forwarded-For", ClientIp);
            admin.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
            var chat = await CreateChat(admin);
            await SendImage(admin, chat.Id, "Proxy identity test");
            var report = await Report(admin);
            var item = Assert.Single(report.Items);
            Assert.Equal(trusted ? ClientIp : IPAddress.Loopback.ToString(), item.Ip);
            Assert.Equal(trusted ? 1L : 0L, (await Report(admin, "?ip=" + ClientIp)).Total);
            Assert.Equal(trusted ? 0L : 1L, (await Report(admin, "?ip=127.0.0.1")).Total);
        }
    }

    [Fact]
    public async Task InFlightRequestIsPendingAndCompletionDoesNotDoubleCountOrLoseConcurrentRejection()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await Authenticated(factory);
        var chat = await CreateChat(admin);
        var completion = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Generator.Handler = _ => completion.Task;
        using var form = ApiTests.Form("Pending image");
        var request = admin.PostAsync($"/api/chats/{chat.Id}/messages", form);
        string? requestId = null;
        try
        {
            await factory.Generator.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var pending = await Report(admin);
            Assert.Equal(new TelemetrySummary(1, 0, 0, 0, 0, 0, 0, 1), pending.Summary);
            var item = Assert.Single(pending.Items);
            requestId = item.Id;
            Assert.Equal("pending", item.Status);
            Assert.Equal("generation", item.Operation);
            Assert.Null(item.CompletedAt);
            Assert.Null(item.HttpStatus);
            Assert.Equal(0L, item.ImageCount);
            Assert.False(request.IsCompleted);
            using var duplicate = ApiTests.Form("Concurrent duplicate");
            using var rejected = await admin.PostAsync($"/api/chats/{chat.Id}/messages", duplicate);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal(new TelemetrySummary(2, 0, 0, 0, 0, 0, 1, 1), (await Report(admin)).Summary);
        }
        finally
        {
            completion.TrySetResult(new ImageData(CoreTests.Png, "image/png"));
            using var finished = await request.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        }
        var report = await Report(admin);
        Assert.Equal(new TelemetrySummary(2, 1, 0, 0, 1, 0, 1, 0), report.Summary);
        var completed = Assert.Single(report.Items, item => item.Id == requestId);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(200, completed.HttpStatus);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(1L, completed.ImageCount);
        Assert.Single(factory.Generator.Inputs);
    }

    [Fact]
    public async Task LegacyDatabaseBackfillsOnceAndBootstrapClaimsChatsForOnlyTheOriginalAdmin()
    {
        await using var factory = new ApiTests.AppFactory();
        Directory.CreateDirectory(factory.DataDirectory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(factory.DataDirectory, "imagechat.db"),
            Pooling = false
        }.ToString();
        const string chatId = "legacy-chat";
        const string imageId = "legacy-output";
        const string created = "2025-01-02T03:04:05.0000000+00:00";
        // Create the pre-owner/pre-telemetry schema directly, never using real application data.
        using (var db = new SqliteConnection(connectionString))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE chats(id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE messages(id TEXT PRIMARY KEY, chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                    role TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL, size TEXT NOT NULL, quality TEXT NOT NULL,
                    status TEXT NOT NULL, error TEXT, image BLOB, content_type TEXT);
                INSERT INTO chats VALUES($chat,'Legacy fixture',$time,$time);
                INSERT INTO messages VALUES('legacy-input',$chat,'user','Legacy fixture',$time,'1024x1024','medium','completed',NULL,$png,'image/png');
                INSERT INTO messages VALUES($output,$chat,'assistant','Legacy output',$time,'1024x1024','medium','completed',NULL,$png,'image/png');
                INSERT INTO messages VALUES('legacy-failure',$chat,'assistant','Failed fixture',$time,'1024x1024','medium','failed','Safe failure',NULL,NULL);
                """;
            command.Parameters.AddWithValue("$chat", chatId);
            command.Parameters.AddWithValue("$output", imageId);
            command.Parameters.AddWithValue("$time", created);
            command.Parameters.Add("$png", SqliteType.Blob).Value = CoreTests.Png;
            command.ExecuteNonQuery();
        }
        var migrated = new ChatStore(factory.DataDirectory);
        var beforeClaim = migrated.Report(new TelemetryFilter(null, null, null));
        Assert.Equal(new TelemetrySummary(1, 0, 0, 1, 1, 0, 0, 0), beforeClaim.Summary);
        var legacy = Assert.Single(beforeClaim.Items);
        Assert.Equal("legacy-" + imageId, legacy.Id);
        Assert.Equal("unknown", legacy.Operation);
        Assert.Equal("legacy", legacy.Status);
        Assert.Null(legacy.Ip);
        Assert.Null(legacy.UserId);
        Assert.Null(legacy.Username);
        Assert.Null(legacy.HttpStatus);
        Assert.Equal(ParseTime(created), ParseTime(legacy.RequestedAt));
        Assert.Equal(legacy.RequestedAt, legacy.CompletedAt);

        // App startup bootstraps the fixture admin and claims the previously ownerless chat.
        using var owner = await Authenticated(factory);
        using var user = await Authenticated(factory, "testuser");
        using var newAdmin = await owner.PostAsJsonAsync("/api/admin/users",
            new CreateUserInput("later-admin", ApiTests.AppFactory.TestPassword, "admin"));
        Assert.Equal(HttpStatusCode.Created, newAdmin.StatusCode);
        using var laterAdmin = await Authenticated(factory, "later-admin");
        Assert.Equal(chatId, Assert.Single((await owner.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!).Id);
        Assert.Equal(CoreTests.Png, await owner.GetByteArrayAsync($"/api/images/{imageId}"));
        foreach (var other in new[] { user, laterAdmin })
        {
            Assert.Empty((await other.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!);
            using var chat = await other.GetAsync($"/api/chats/{chatId}");
            using var image = await other.GetAsync($"/api/images/{imageId}");
            using var delete = await other.DeleteAsync($"/api/chats/{chatId}");
            Assert.Equal(HttpStatusCode.NotFound, chat.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }
        var afterStartup = await Report(owner);
        Assert.Equal(beforeClaim.Summary, afterStartup.Summary);
        Assert.Equal(legacy, Assert.Single(afterStartup.Items));
        Assert.Equal(0L, (await Report(owner, "?ip=" + ClientIp)).Total);
        var reopened = new ChatStore(factory.DataDirectory);
        Assert.Equal(legacy, Assert.Single(reopened.Report(new TelemetryFilter(null, null, null)).Items));
        // A second claimant cannot reassign data already claimed by the original administrator.
        var secondId = (await newAdmin.Content.ReadFromJsonAsync<UserView>())!.Id;
        reopened.ClaimLegacyChats(secondId);
        using var stillHidden = await laterAdmin.GetAsync($"/api/chats/{chatId}");
        Assert.Equal(HttpStatusCode.NotFound, stillHidden.StatusCode);
        using var deleted = await owner.DeleteAsync($"/api/chats/{chatId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(legacy, Assert.Single((await Report(owner)).Items));
        Assert.Empty(factory.Generator.Inputs);
    }

    private static async Task<HttpClient> Authenticated(ApiTests.AppFactory factory, string username = "testadmin")
    {
        var client = await factory.CreateAuthenticatedClient(username);
        client.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        return client;
    }

    private static async Task<Chat> CreateChat(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/chats", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Chat>())!;
    }

    private static async Task SendImage(HttpClient client, string chatId, string prompt, bool usePrevious = true)
    {
        using var form = ApiTests.Form(prompt, usePrevious);
        using var response = await client.PostAsync($"/api/chats/{chatId}/messages", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<TelemetryReport> Report(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync("/api/admin/telemetry" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TelemetryReport>())!;
    }

    private static long ParseTime(string timestamp) => DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static string UtcQuery(long milliseconds) => Uri.EscapeDataString(
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
}