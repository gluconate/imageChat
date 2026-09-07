using System.Net;
using System.Net.Http.Json;
using ImageChat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ImageChat.Tests;

public sealed class AdminPasswordResetTests : IDisposable
{
    private const string OriginalPassword = "reset-fixture-original-2026";
    private const string NewPassword = "reset-fixture-replacement-2026";
    private readonly string directory;
    private string UsersPath => Path.Combine(directory, "users.json");

    public AdminPasswordResetTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ImageChat.csproj"))) root = root.Parent;
        directory = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository not found."),
            ".cache", "test-data", $"admin-reset-{Guid.NewGuid():N}");
    }

    [Fact]
    public void ResetPreservesIdentityAndOtherAccountsButRotatesPasswordAndStamp()
    {
        var store = new UserStore(directory);
        var original = store.Bootstrap("Local.Admin", OriginalPassword);
        var other = store.Create("other-user", OriginalPassword, "user");
        var before = store.Find(original.Id)!;
        var otherBefore = store.Find(other.Id);

        var updated = new UserStore(directory).ResetAdminPassword("LOCAL.admin", NewPassword);

        Assert.Equal(original, updated);
        var after = store.Find(original.Id)!;
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(otherBefore, store.Find(other.Id));
        Assert.Null(store.Authenticate(original.Username, OriginalPassword));
        Assert.Equal(original.Id, store.Authenticate(original.Username, NewPassword)?.Id);
        Assert.StartsWith("pbkdf2-sha256$600000$", after.PasswordHash);
        Assert.DoesNotContain(NewPassword, File.ReadAllText(UsersPath));
        Assert.DoesNotContain(OriginalPassword, File.ReadAllText(UsersPath));
        Assert.Empty(Directory.GetFiles(directory, ".users-*.tmp"));
    }

    [Fact]
    public void ResetClearsFailedLoginLockout()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", OriginalPassword);
        for (var attempt = 0; attempt < 5; attempt++) Assert.Null(store.Authenticate("admin", NewPassword));
        Assert.NotNull(store.Find(admin.Id)!.LockedUntil);

        store.ResetAdminPassword("admin", NewPassword);

        var updated = store.Find(admin.Id)!;
        Assert.Equal(0, updated.FailedLoginCount);
        Assert.Null(updated.FailureWindowStartedAt);
        Assert.Null(updated.LockedUntil);
        Assert.Equal(admin.Id, store.Authenticate("admin", NewPassword)?.Id);
    }

    [Fact]
    public void ResetDoesNotReenableDisabledAdministrator()
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", OriginalPassword);
        var disabled = store.Create("disabled-admin", OriginalPassword, "admin");
        store.Update(disabled.Id, "admin", false);
        var result = store.ResetAdminPassword(disabled.Username, NewPassword);
        Assert.False(result.Enabled);
        Assert.Equal("admin", result.Role);
        Assert.Null(store.Authenticate(disabled.Username, NewPassword));
    }

    [Theory]
    [InlineData("missing-admin", 404)]
    [InlineData("regular-user", 404)]
    [InlineData("bad username", 400)]
    public void InvalidTargetNeverCreatesAccountsOrChangesUserRoles(string username, int status)
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", OriginalPassword);
        store.Create("regular-user", OriginalPassword, "user");
        var before = File.ReadAllBytes(UsersPath);
        Assert.Equal(status, Assert.Throws<AppException>(() => store.ResetAdminPassword(username, NewPassword)).StatusCode);
        Assert.Equal(before, File.ReadAllBytes(UsersPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(129)]
    public void InvalidPasswordNeverChangesAccount(int length)
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", OriginalPassword);
        var before = File.ReadAllBytes(UsersPath);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.ResetAdminPassword("admin", new string('x', length))).StatusCode);
        Assert.Equal(before, File.ReadAllBytes(UsersPath));
    }

    [Fact]
    public void EmptyStoreCannotBeBootstrappedByReset()
    {
        var store = new UserStore(directory);
        Assert.Equal(404, Assert.Throws<AppException>(() => store.ResetAdminPassword("admin", NewPassword)).StatusCode);
        Assert.False(store.HasUsers);
        Assert.False(File.Exists(UsersPath));
    }

    [Fact]
    public async Task OfflineResetRevokesCookiesAndPreservesChatOwnership()
    {
        await using var factory = new ApiTests.AppFactory();
        using var first = await factory.CreateAuthenticatedClient();
        using var second = await factory.CreateAuthenticatedClient();
        first.DefaultRequestHeaders.Add("X-ImageChat-Request", "1");
        using var created = await first.PostAsJsonAsync("/api/chats", new { });
        created.EnsureSuccessStatusCode();
        var chat = (await created.Content.ReadFromJsonAsync<Chat>())!;
        var users = factory.Services.GetRequiredService<UserStore>();
        var before = users.List().Single(user => user.Username == "testadmin");

        // Same mechanism as an independent offline command; no live application data touched.
        var result = new UserStore(factory.DataDirectory).ResetAdminPassword(before.Username, NewPassword);

        Assert.Equal(before, result);
        foreach (var client in new[] { first, second })
        {
            using var denied = await client.GetAsync("/api/chats");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var fresh = await factory.CreateAuthenticatedClient("testadmin", NewPassword);
        var saved = (await fresh.GetFromJsonAsync<Chat>($"/api/chats/{chat.Id}"))!;
        Assert.Equal(chat.Id, saved.Id);
        Assert.Equal(chat.Title, saved.Title);
        Assert.Empty(saved.Messages);
        Assert.Equal(chat.Id, Assert.Single((await fresh.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!).Id);
        Assert.Empty(factory.Generator.Inputs);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            LocalFileIO.RetrySharing(() => { Directory.Delete(directory, recursive: true); return 0; });
    }
}