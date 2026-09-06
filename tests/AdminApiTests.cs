using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageChat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ImageChat.Tests;

public class AdminApiTests
{
    private const string ChangedPassword = "Changed-only-for-tests-456!";
    private const string WrongPassword = "Wrong-only-for-tests-789!";

    [Fact]
    public async Task AnonymousRequestsCannotReachExistingChatsImagesOrAdministratorEndpoints()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var chat = await CreateChat(admin);
        using var form = ApiTests.Form("Private test image");
        using var generated = await admin.PostAsync($"/api/chats/{chat.Id}/messages", form);
        generated.EnsureSuccessStatusCode();
        var image = (await generated.Content.ReadFromJsonAsync<Chat>())!.Messages[1].ImageUrl!;
        var account = (await admin.GetFromJsonAsync<List<UserView>>("/api/admin/users"))!.Single(u => u.Username == "testuser");

        using var anonymous = NewClient(factory);
        foreach (var path in new[] { "/", "/index.html", "/admin", "/admin.html" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/login", response.Headers.Location?.OriginalString);
        }
        foreach (var path in new[] { "/api/chats", $"/api/chats/{chat.Id}", image, image + "?download=true",
                     "/api/admin/users", "/api/admin/telemetry", "/api/status" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        using var create = await anonymous.PostAsJsonAsync("/api/chats", new { });
        using var delete = await anonymous.DeleteAsync($"/api/chats/{chat.Id}");
        using var upload = ApiTests.Form("Must not reach model");
        using var post = await anonymous.PostAsync($"/api/chats/{chat.Id}/messages", upload);
        using var createUser = await anonymous.PostAsJsonAsync("/api/admin/users", new CreateUserInput("intruder", ApiTests.AppFactory.TestPassword, "admin"));
        using var updateUser = await anonymous.PutAsJsonAsync($"/api/admin/users/{account.Id}", new UpdateUserInput("admin", true));
        using var deleteUser = await anonymous.DeleteAsync($"/api/admin/users/{account.Id}");
        foreach (var response in new[] { create, delete, post, createUser, updateUser, deleteUser })
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(factory.Generator.Inputs);
        Assert.Equal(2, (await admin.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages.Count);

        foreach (var path in new[] { "/login", "/login.html" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var session = (await anonymous.GetFromJsonAsync<Session>("/api/auth/session"))!;
        Assert.False(session.Authenticated);
        Assert.False(session.SetupRequired);
        Assert.Null(session.User);
    }

    [Fact]
    public async Task FirstRunIsPubliclyDiscoverableButCannotBootstrapAccountsOverHttp()
    {
        await using var factory = new ApiTests.AppFactory { BootstrapAccounts = false };
        using var anonymous = NewClient(factory);
        using var page = await anonymous.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var before = (await anonymous.GetFromJsonAsync<Session>("/api/auth/session"))!;
        Assert.True(before.SetupRequired);
        Assert.False(before.Authenticated);
        Assert.Null(before.User);
        Assert.False(File.Exists(Path.Combine(factory.DataDirectory, "users.json")));

        using var login = await Login(anonymous);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
        await SafeBody(login);
        foreach (var path in new[] { "/api/admin/users", "/api/auth/bootstrap", "/api/auth/setup", "/api/auth/register" })
        {
            using var response = await anonymous.PostAsJsonAsync(path,
                new CreateUserInput("webbootstrap", ApiTests.AppFactory.TestPassword, "admin"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(before, await anonymous.GetFromJsonAsync<Session>("/api/auth/session"));
        // Check existence only; never open any credential or data-protection key file.
        Assert.False(File.Exists(Path.Combine(factory.DataDirectory, "users.json")));
        Assert.Empty(factory.Generator.Inputs);
    }

    [Fact]
    public async Task RegularUserCannotReachAdminHtmlUserCrudOrTelemetry()
    {
        await using var factory = new ApiTests.AppFactory();
        using var user = await factory.CreateAuthenticatedClient("testuser");
        user.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var ownId = (await user.GetFromJsonAsync<Session>("/api/auth/session"))!.User!.Id;
        foreach (var path in new[] { "/admin", "/admin.html", "/admin/", "/api/admin/users", "/api/admin/telemetry" })
        {
            using var response = await user.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        using var create = await user.PostAsJsonAsync("/api/admin/users", new CreateUserInput("newadmin", ApiTests.AppFactory.TestPassword, "admin"));
        using var update = await user.PutAsJsonAsync($"/api/admin/users/{ownId}", new UpdateUserInput("admin", true));
        using var delete = await user.DeleteAsync($"/api/admin/users/{ownId}");
        foreach (var response in new[] { create, update, delete })
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("user", (await user.GetFromJsonAsync<Session>("/api/auth/session"))!.User!.Role);
        Assert.Empty(factory.Generator.Inputs);
    }

    [Fact]
    public async Task OwnershipIsolationAppliesToAdministratorsAsWellAsRegularUsers()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await factory.CreateAuthenticatedClient();
        using var user = await factory.CreateAuthenticatedClient("testuser");
        admin.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        user.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var adminChat = await CreateChat(admin);
        var userChat = await CreateChat(user);
        foreach (var (owner, other, chat) in new[] { (admin, user, adminChat), (user, admin, userChat) })
        {
            using var form = ApiTests.Form("Owner's image");
            using var generated = await owner.PostAsync($"/api/chats/{chat.Id}/messages", form);
            generated.EnsureSuccessStatusCode();
            var image = (await generated.Content.ReadFromJsonAsync<Chat>())!.Messages[1].ImageUrl!;
            using var get = await other.GetAsync($"/api/chats/{chat.Id}");
            using var delete = await other.DeleteAsync($"/api/chats/{chat.Id}");
            using var foreignForm = ApiTests.Form("Must not edit another owner's image");
            using var post = await other.PostAsync($"/api/chats/{chat.Id}/messages", foreignForm);
            using var view = await other.GetAsync(image);
            using var download = await other.GetAsync(image + "?download=true");
            foreach (var response in new[] { get, delete, post, view, download })
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(CoreTests.Png, await owner.GetByteArrayAsync(image));
            var ownList = (await owner.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!;
            Assert.Equal(chat.Id, Assert.Single(ownList).Id);
            Assert.Equal(2, (await owner.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages.Count);
        }
        Assert.Equal(2, factory.Generator.Inputs.Length);
    }

    [Fact]
    public async Task AdminCreatesListsUpdatesAndDeletesUsersWithoutExposingCredentialFields()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        using var created = await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserInput("created-user", ApiTests.AppFactory.TestPassword, "user"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await SafeBody(created));
        AssertUserView(createdJson.RootElement);
        var account = createdJson.RootElement.Deserialize<UserView>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(account.Enabled);
        Assert.Equal("user", account.Role);

        using var user = NewClient(factory);
        using var login = await Login(user, account.Username);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        AssertSessionCookie(login, secure: false);
        using var loginJson = JsonDocument.Parse(await SafeBody(login));
        AssertUserView(loginJson.RootElement.GetProperty("user"));
        using var sessionResponse = await user.GetAsync("/api/auth/session");
        using var sessionJson = JsonDocument.Parse(await SafeBody(sessionResponse));
        Assert.True(sessionJson.RootElement.GetProperty("authenticated").GetBoolean());
        AssertUserView(sessionJson.RootElement.GetProperty("user"));
        using var listed = await admin.GetAsync("/api/admin/users");
        using var listJson = JsonDocument.Parse(await SafeBody(listed));
        Assert.Equal(3, listJson.RootElement.GetArrayLength());
        foreach (var item in listJson.RootElement.EnumerateArray()) AssertUserView(item);

        // A no-op update must not needlessly invalidate a valid session.
        using var update = await admin.PutAsJsonAsync($"/api/admin/users/{account.Id}", new UpdateUserInput("user", true));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var updateJson = JsonDocument.Parse(await SafeBody(update));
        AssertUserView(updateJson.RootElement);
        Assert.True((await user.GetFromJsonAsync<Session>("/api/auth/session"))!.Authenticated);
        using var duplicate = await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserInput("CREATED-USER", ApiTests.AppFactory.TestPassword, "user"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var deleted = await admin.DeleteAsync($"/api/admin/users/{account.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.DoesNotContain((await admin.GetFromJsonAsync<List<UserView>>("/api/admin/users"))!, u => u.Id == account.Id);
        using var denied = await user.GetAsync("/api/chats");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("role")]
    public async Task AdministrativeSecurityChangesInvalidateEveryExistingCookie(string change)
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        using var first = await factory.CreateAuthenticatedClient("testuser");
        using var second = await factory.CreateAuthenticatedClient("testuser");
        var id = (await first.GetFromJsonAsync<Session>("/api/auth/session"))!.User!.Id;
        using var update = change == "delete"
            ? await admin.DeleteAsync($"/api/admin/users/{id}")
            : await admin.PutAsJsonAsync($"/api/admin/users/{id}", new UpdateUserInput(
                change == "role" ? "admin" : "user", change != "disable", change == "password" ? ChangedPassword : null));
        Assert.Equal(change == "delete" ? HttpStatusCode.NoContent : HttpStatusCode.OK, update.StatusCode);
        foreach (var stale in new[] { first, second })
        {
            using var denied = await stale.GetAsync("/api/chats");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            var session = (await stale.GetFromJsonAsync<Session>("/api/auth/session"))!;
            Assert.False(session.Authenticated);
            Assert.Null(session.User);
        }
        using var fresh = NewClient(factory);
        using var oldPassword = await Login(fresh, "testuser");
        Assert.Equal(change == "role" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        if (change == "password")
        {
            using var newPassword = await Login(fresh, "testuser", ChangedPassword);
            Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
        }
        if (change == "role")
        {
            using var allowed = await fresh.GetAsync("/api/admin/telemetry");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Fact]
    public async Task LastEnabledAdminCannotBeDeletedDisabledOrDemoted()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var id = (await admin.GetFromJsonAsync<Session>("/api/auth/session"))!.User!.Id;
        using var delete = await admin.DeleteAsync($"/api/admin/users/{id}");
        using var disable = await admin.PutAsJsonAsync($"/api/admin/users/{id}", new UpdateUserInput("admin", false));
        using var demote = await admin.PutAsJsonAsync($"/api/admin/users/{id}", new UpdateUserInput("user", true));
        foreach (var response in new[] { delete, disable, demote })
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var remaining = Assert.Single((await admin.GetFromJsonAsync<List<UserView>>("/api/admin/users"))!, u => u.Role == "admin");
        Assert.Equal(id, remaining.Id);
        Assert.True(remaining.Enabled);
        Assert.True((await admin.GetFromJsonAsync<Session>("/api/auth/session"))!.Authenticated);
    }

    [Fact]
    public async Task InvalidRolesPasswordsAndRequiredFieldsAreRejectedWithoutChangingAccounts()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var before = (await admin.GetFromJsonAsync<List<UserView>>("/api/admin/users"))!;
        var target = before.Single(u => u.Username == "testuser");
        foreach (var input in new[]
                 {
                     new CreateUserInput("valid-name", ApiTests.AppFactory.TestPassword, "owner"),
                     new CreateUserInput("valid-name", ApiTests.AppFactory.TestPassword, "Admin"),
                     new CreateUserInput("valid-name", "short", "user"),
                     new CreateUserInput("valid-name", new string('x', 129), "user"),
                     new CreateUserInput("invalid name", ApiTests.AppFactory.TestPassword, "user")
                 })
        {
            using var response = await admin.PostAsJsonAsync("/api/admin/users", input);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await SafeBody(response);
        }
        foreach (var input in new[] { new UpdateUserInput("owner", true), new UpdateUserInput("user", true, "short"),
                     new UpdateUserInput("user", true, new string('x', 129)) })
        {
            using var response = await admin.PutAsJsonAsync($"/api/admin/users/{target.Id}", input);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var missingPassword = await admin.PostAsJsonAsync("/api/admin/users", new { username = "valid-name", role = "user" });
        using var missingEnabled = await admin.PutAsJsonAsync($"/api/admin/users/{target.Id}", new { role = "user" });
        Assert.Equal(HttpStatusCode.BadRequest, missingPassword.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingEnabled.StatusCode);
        Assert.Equal(before.ToArray(), (await admin.GetFromJsonAsync<List<UserView>>("/api/admin/users"))!.ToArray());
    }

    [Fact]
    public async Task EveryMutationRequiresCustomHeaderAndRejectsCrossOriginAndCrossSiteRequests()
    {
        await using var factory = new ApiTests.AppFactory();
        using var admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        var chat = await CreateChat(admin);
        var user = (await admin.GetFromJsonAsync<List<UserView>>("/api/admin/users"))!.Single(u => u.Username == "testuser");
        admin.DefaultRequestHeaders.Remove("X-ImageChat-Request");
        (HttpMethod Method, string Path, Func<HttpContent?> Body)[] mutations =
        [
            (HttpMethod.Post, "/api/auth/login", () => JsonContent.Create(new LoginInput("testadmin", ApiTests.AppFactory.TestPassword))),
            (HttpMethod.Post, "/api/auth/logout", () => null),
            (HttpMethod.Post, "/api/auth/password", () => JsonContent.Create(new PasswordInput(ApiTests.AppFactory.TestPassword, ChangedPassword))),
            (HttpMethod.Post, "/api/chats", () => JsonContent.Create(new { })),
            (HttpMethod.Post, $"/api/chats/{chat.Id}/messages", () => ApiTests.Form("Must not generate")),
            (HttpMethod.Delete, $"/api/chats/{chat.Id}", () => null),
            (HttpMethod.Post, "/api/admin/users", () => JsonContent.Create(new CreateUserInput("csrf-user", ApiTests.AppFactory.TestPassword, "user"))),
            (HttpMethod.Put, $"/api/admin/users/{user.Id}", () => JsonContent.Create(new UpdateUserInput("admin", true))),
            (HttpMethod.Delete, $"/api/admin/users/{user.Id}", () => null)
        ];
        foreach (var mode in new[] { "missing-header", "cross-origin", "cross-site" })
        foreach (var mutation in mutations)
        {
            using var request = new HttpRequestMessage(mutation.Method, mutation.Path) { Content = mutation.Body() };
            if (mode != "missing-header") request.Headers.Add("X-ImageChat-Request", "1");
            if (mode == "cross-origin") request.Headers.Add("Origin", "https://other.example.test");
            if (mode == "cross-site") request.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var response = await admin.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.True((await admin.GetFromJsonAsync<Session>("/api/auth/session"))!.Authenticated);
        Assert.Empty((await admin.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!.Messages);
        var accounts = (await admin.GetFromJsonAsync<List<UserView>>("/api/admin/users"))!;
        Assert.Equal(2, accounts.Count);
        Assert.Equal("user", accounts.Single(u => u.Id == user.Id).Role);
        Assert.Empty(factory.Generator.Inputs);
        // Positive control: an explicitly same-origin mutation is accepted.
        using var sameOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/chats") { Content = JsonContent.Create(new { }) };
        sameOrigin.Headers.Add("X-ImageChat-Request", "1");
        sameOrigin.Headers.Add("Origin", admin.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var allowed = await admin.SendAsync(sameOrigin);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task LogoutAndSelfPasswordChangeRequireSessionAndLogoutClearsBrowserSession()
    {
        await using var factory = new ApiTests.AppFactory();
        using var anonymous = NewClient(factory);
        using var deniedLogout = await anonymous.PostAsync("/api/auth/logout", null);
        using var deniedPassword = await anonymous.PostAsJsonAsync("/api/auth/password", new PasswordInput(ApiTests.AppFactory.TestPassword, ChangedPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedPassword.StatusCode);
        using var user = await factory.CreateAuthenticatedClient("testuser");
        user.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        using var otherSession = await factory.CreateAuthenticatedClient("testuser");
        using var logout = await user.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.True(logout.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(c => c.StartsWith("ImageChat.Session=;", StringComparison.OrdinalIgnoreCase)), "Logout must clear the session cookie.");
        using var denied = await user.GetAsync("/api/chats");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.False((await user.GetFromJsonAsync<Session>("/api/auth/session"))!.Authenticated);
        using var revoked = await otherSession.GetAsync("/api/chats");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task SelfPasswordChangeInvalidatesOldPasswordAndAllCookiesWithoutLoggingSecrets()
    {
        await using var factory = new ApiTests.AppFactory();
        var logs = new CapturedLogs();
        await using var application = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var first = NewClient(application);
        using var second = NewClient(application);
        using var firstLogin = await Login(first, "testuser");
        using var secondLogin = await Login(second, "testuser");
        Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        // Retain only in memory for negative disclosure checks; never print cookie values.
        var cookie = firstLogin.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        using var wrongCurrent = await first.PostAsJsonAsync("/api/auth/password", new PasswordInput(WrongPassword, ChangedPassword));
        using var invalidNew = await first.PostAsJsonAsync("/api/auth/password", new PasswordInput(ApiTests.AppFactory.TestPassword, "short"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidNew.StatusCode);
        Assert.True((await first.GetFromJsonAsync<Session>("/api/auth/session"))!.Authenticated);
        using var change = await first.PostAsJsonAsync("/api/auth/password", new PasswordInput(ApiTests.AppFactory.TestPassword, ChangedPassword));
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        foreach (var stale in new[] { first, second })
        {
            using var denied = await stale.GetAsync("/api/chats");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        // Explicit replay proves revocation, independently of the logout Set-Cookie header.
        using var replay = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        using var replayed = await replay.GetAsync("/api/chats");
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        using var fresh = NewClient(application);
        using var oldLogin = await Login(fresh, "testuser");
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using var newLogin = await Login(fresh, "testuser", ChangedPassword);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        await SafeBody(newLogin, cookie);

        const string privatePrompt = "Private-test-prompt-never-log-31";
        const string privateException = "Private-test-upstream-body-never-log-32";
        factory.Generator.Handler = _ => Task.FromException<ImageData>(new HttpRequestException(privateException));
        var chat = await CreateChat(fresh);
        using var form = ApiTests.Form(privatePrompt);
        using var failed = await fresh.PostAsync($"/api/chats/{chat.Id}/messages", form);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        await SafeBody(failed, privatePrompt, privateException, cookie);
        Assert.True(logs.Messages.Any(message => message.Contains("Request failed with", StringComparison.Ordinal)),
            "Expected a sanitized request-failure log entry.");
        foreach (var secret in new[] { ApiTests.AppFactory.TestPassword, ChangedPassword, WrongPassword, "not-a-real-key", cookie,
                     cookie[(cookie.IndexOf('=') + 1)..], privatePrompt, privateException, "pbkdf2-sha256$" })
            Assert.False(logs.Messages.Any(message => message.Contains(secret, StringComparison.Ordinal)), "Application logs contained sensitive material.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpsRequirementCannotBeBypassedExceptThroughExplicitlyTrustedForwarding(bool trustProxy)
    {
        await using var factory = new ApiTests.AppFactory { RequireHttps = true, TrustTestProxy = trustProxy };
        using var http = NewClient(factory);
        using var plain = await Login(http);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.False(plain.Headers.Contains("Set-Cookie"));
        http.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");
        http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var forwarded = await Login(http);
        Assert.Equal(trustProxy ? HttpStatusCode.OK : HttpStatusCode.BadRequest, forwarded.StatusCode);
        if (trustProxy) AssertSessionCookie(forwarded, secure: true);
        else Assert.False(forwarded.Headers.Contains("Set-Cookie"));

        using var https = NewClient(factory, new Uri("https://localhost"));
        using var direct = await Login(https);
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        AssertSessionCookie(direct, secure: true);
        Assert.True((await https.GetFromJsonAsync<Session>("/api/auth/session"))!.Authenticated);
    }

    [Fact]
    public async Task LoginAndPasswordChangesShareTenRequestsPerMinutePerClientIp()
    {
        await using var factory = new ApiTests.AppFactory();
        using var user = await factory.CreateAuthenticatedClient("testuser"); // Permit 1.
        user.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        using var anonymous = NewClient(factory);
        // An untrusted forwarded IP must not obtain a separate window from the user's loopback IP.
        anonymous.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");
        // Unknown names avoid account lockout while still exercising the real password verifier.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var login = await Login(anonymous, "unknown-test-account");
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }
        using var tenth = await user.PostAsJsonAsync("/api/auth/password", new PasswordInput(WrongPassword, ChangedPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, tenth.StatusCode);
        using var eleventh = await Login(anonymous, "unknown-test-account");
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        using var passwordThrottled = await user.PostAsJsonAsync("/api/auth/password", new PasswordInput(ApiTests.AppFactory.TestPassword, ChangedPassword));
        Assert.Equal(HttpStatusCode.TooManyRequests, passwordThrottled.StatusCode);
        Assert.True((await user.GetFromJsonAsync<Session>("/api/auth/session"))!.Authenticated);
        Assert.Empty(factory.Generator.Inputs);
    }

    private static HttpClient NewClient(WebApplicationFactory<Program> factory, Uri? address = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = address ?? new Uri("http://localhost")
        });
        client.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        return client;
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string username = "testadmin", string password = ApiTests.AppFactory.TestPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginInput(username, password))
        };
        request.Headers.Add("X-ImageChat-Request", "1");
        return await client.SendAsync(request);
    }

    private static async Task<Chat> CreateChat(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/chats", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Chat>())!;
    }

    private static void AssertSessionCookie(HttpResponseMessage response, bool secure)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values), "Expected a session cookie.");
        var cookies = values!.Where(value => value.StartsWith("ImageChat.Session=", StringComparison.Ordinal)).ToArray();
        var attributes = Assert.Single(cookies).Split(';').Skip(1).Select(value => value.Trim().ToLowerInvariant()).ToArray();
        Assert.True(attributes.Contains("httponly"), "Session cookie must be HttpOnly.");
        Assert.True(attributes.Contains("samesite=strict"), "Session cookie must have SameSite=Strict.");
        Assert.True(attributes.Contains("path=/"), "Session cookie must cover the application.");
        Assert.Equal(secure, attributes.Contains("secure"));
    }

    private static void AssertUserView(JsonElement value)
    {
        Assert.Equal(new[] { "createdAt", "enabled", "id", "role", "username" },
            value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private static async Task<string> SafeBody(HttpResponseMessage response, params string[] additionalSecrets)
    {
        var body = await response.Content.ReadAsStringAsync();
        foreach (var secret in new[] { ApiTests.AppFactory.TestPassword, ChangedPassword, WrongPassword, "not-a-real-key",
                     "pbkdf2-sha256$", "passwordHash", "securityStamp", "security_stamp" }.Concat(additionalSecrets))
            Assert.False(body.Contains(secret, StringComparison.OrdinalIgnoreCase), "HTTP response contained credential or private material.");
        return body;
    }

    private sealed record Session(bool Authenticated, bool SetupRequired, UserView? User);

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        public void Dispose() { }

        private sealed class CapturedLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception != null) messages.Enqueue(exception.ToString());
            }
        }
    }
}