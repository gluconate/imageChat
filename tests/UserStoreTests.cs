using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageChat;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ImageChat.Tests;

public sealed class UserStoreTests : IDisposable
{
    // Test-only passwords; these never configure the application or touch its actual data directory.
    private const string Password = "test-password-alpha-2026";
    private const string OtherPassword = "test-password-bravo-2026";
    private readonly string directory;
    private string UsersPath => Path.Combine(directory, "users.json");

    public UserStoreTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ImageChat.csproj"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Tests must run beneath the ImageChat repository.");
        directory = Path.Combine(root.FullName, ".cache", "test-data", $"users-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void EmptyStoreHasNoDefaultCredentialsAndOnlyOfflineBootstrapCanCreateFirstUser()
    {
        var store = new UserStore(directory);
        Assert.False(store.HasUsers);
        Assert.Empty(store.List());
        Assert.False(File.Exists(UsersPath));
        Assert.Null(store.Authenticate("admin", Password));
        Assert.Equal(503, Assert.Throws<AppException>(() => store.Create("admin", Password, "admin")).StatusCode);
        var admin = store.Bootstrap("Local.Admin", Password);
        Assert.True(store.HasUsers);
        Assert.Equal("admin", admin.Role);
        Assert.True(admin.Enabled);
        Assert.Equal("Local.Admin", admin.Username);
        Assert.Equal(409, Assert.Throws<AppException>(() => store.Bootstrap("another-admin", Password)).StatusCode);
        Assert.Single(store.List());
    }

    [Fact]
    public void PasswordsUseVersionedSaltedPbkdf2AndNeverPersistPlaintext()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var user = store.Create("person", Password, "user");
        var first = Assert.IsType<Account>(store.Find(admin.Id));
        var second = Assert.IsType<Account>(store.Find(user.Id));
        Assert.NotEqual(first.PasswordHash, second.PasswordHash);
        Assert.NotEqual(first.SecurityStamp, second.SecurityStamp);
        foreach (var account in new[] { first, second })
        {
            var parts = account.PasswordHash.Split('$');
            Assert.Equal(4, parts.Length);
            Assert.Equal("pbkdf2-sha256", parts[0]);
            Assert.Equal("600000", parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var hash = Convert.FromBase64String(parts[3]);
            Assert.Equal(16, salt.Length);
            Assert.Equal(32, hash.Length);
            Assert.Equal(32, Convert.FromBase64String(account.SecurityStamp).Length);
            Assert.Equal(Rfc2898DeriveBytes.Pbkdf2(Password, salt, 600_000, HashAlgorithmName.SHA256, 32), hash);
            Assert.Equal(account.Id, store.Authenticate(account.Username, Password)?.Id);
            Assert.DoesNotContain(account.PasswordHash, account.ToString());
            Assert.DoesNotContain(account.SecurityStamp, account.ToString());
        }
        var json = File.ReadAllText(UsersPath);
        Assert.DoesNotContain(Password, json);
        Assert.Contains("pbkdf2-sha256$600000$", json);
        Assert.Empty(Directory.GetFiles(directory, ".users-*.tmp"));
    }

    [Fact]
    public void PublicViewsContainOnlyNonSecretFields()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var account = Assert.IsType<Account>(store.Find(admin.Id));
        foreach (var view in new[] { admin, account.ToView(), Assert.Single(store.List()) })
        {
            var json = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var document = JsonDocument.Parse(json);
            Assert.Equal(new[] { "createdAt", "enabled", "id", "role", "username" },
                document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
            Assert.DoesNotContain(Password, json);
            Assert.DoesNotContain(account.PasswordHash, json);
            Assert.DoesNotContain(account.SecurityStamp, json);
        }
    }

    [Fact]
    public void UnknownAndWrongPasswordsFailWithoutCreatingAccounts()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var before = File.ReadAllText(UsersPath);
        Assert.Null(store.Authenticate("unknown", Password));
        Assert.Null(store.Authenticate("another-unknown", OtherPassword));
        Assert.Null(store.Authenticate("invalid username", Password));
        Assert.Equal(before, File.ReadAllText(UsersPath));
        Assert.Null(store.Authenticate("admin", OtherPassword));
        Assert.Equal(1, store.Find(admin.Id)!.FailedLoginCount);
        Assert.Single(store.List());
        Assert.Equal(admin.Id, store.Authenticate("ADMIN", Password)?.Id);
        Assert.Equal(0, store.Find(admin.Id)!.FailedLoginCount);
    }

    [Fact]
    public void UsernamesAreCaseInsensitiveUniqueAndRemainStableOnUpdate()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("Local.Admin_1", Password);
        Assert.Equal(409, Assert.Throws<AppException>(() => store.Create("local.ADMIN_1", OtherPassword, "user")).StatusCode);
        var updated = store.Update(admin.Id, "admin", true, OtherPassword);
        Assert.Equal(admin.Id, updated.Id);
        Assert.Equal(admin.Username, updated.Username);
        Assert.Equal(admin.CreatedAt, updated.CreatedAt);
        Assert.Equal(admin.Id, store.Authenticate("LOCAL.admin_1", OtherPassword)?.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("usér")]
    public void InvalidUsernamesCannotBootstrap(string? username)
    {
        var store = new UserStore(directory);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.Bootstrap(username!, Password)).StatusCode);
        Assert.False(store.HasUsers);
        Assert.False(File.Exists(UsersPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(129)]
    public void PasswordLengthIsValidatedBeforeWriting(int length)
    {
        var store = new UserStore(directory);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.Bootstrap("admin", new string('x', length))).StatusCode);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.Bootstrap("admin", null!)).StatusCode);
        Assert.False(File.Exists(UsersPath));
    }

    [Fact]
    public void UsernameAndPasswordBoundaryLengthsAreAccepted()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("abc", new string('x', 12));
        var user = store.Create(new string('A', 64), new string('z', 128), "user");
        Assert.Equal(admin.Id, store.Authenticate("ABC", new string('x', 12))?.Id);
        Assert.Equal(user.Id, store.Authenticate(new string('a', 64), new string('z', 128))?.Id);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.Create(new string('a', 65), Password, "user")).StatusCode);
    }

    [Fact]
    public void InvalidMutationsLeaveTheExistingFileUntouched()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var before = File.ReadAllText(UsersPath);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.Create("user", Password, "ADMIN")).StatusCode);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.Update(admin.Id, "invalid", true, null)).StatusCode);
        Assert.Equal(400, Assert.Throws<AppException>(() => store.Update(admin.Id, "admin", true, "short")).StatusCode);
        Assert.Equal(404, Assert.Throws<AppException>(() => store.Update("missing", "user", true, null)).StatusCode);
        Assert.Equal(404, Assert.Throws<AppException>(() => store.Delete("missing")).StatusCode);
        Assert.Equal(before, File.ReadAllText(UsersPath));
        Assert.Empty(Directory.GetFiles(directory, ".users-*.tmp"));
    }

    [Fact]
    public void LastEnabledAdministratorCannotBeDemotedDisabledOrDeleted()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var disabledAdmin = store.Create("disabled-admin", Password, "admin");
        store.Update(disabledAdmin.Id, "admin", false, null);
        var before = File.ReadAllText(UsersPath);
        Assert.Equal(409, Assert.Throws<AppException>(() => store.Update(admin.Id, "user", true, null)).StatusCode);
        Assert.Equal(409, Assert.Throws<AppException>(() => store.Update(admin.Id, "admin", false, null)).StatusCode);
        Assert.Equal(409, Assert.Throws<AppException>(() => store.Delete(admin.Id)).StatusCode);
        Assert.Equal(before, File.ReadAllText(UsersPath));
    }

    [Theory]
    [InlineData("demote")]
    [InlineData("disable")]
    [InlineData("delete")]
    public void AnotherEnabledAdministratorAllowsAdministrativeRemoval(string operation)
    {
        var store = new UserStore(directory);
        var first = store.Bootstrap("first-admin", Password);
        var second = store.Create("second-admin", Password, "admin");
        if (operation == "delete")
        {
            store.Delete(first.Id);
            Assert.Null(store.Find(first.Id));
        }
        else
        {
            var role = operation == "demote" ? "user" : "admin";
            var enabled = operation != "disable";
            var updated = store.Update(first.Id, role, enabled, null);
            Assert.Equal(role, updated.Role);
            Assert.Equal(enabled, updated.Enabled);
        }
        Assert.Equal(second.Id, store.Authenticate("second-admin", Password)?.Id);
    }

    [Fact]
    public void RoleDisableEnableAndPasswordResetRotateStampsButNoOpDoesNot()
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", Password);
        var user = store.Create("person", Password, "user");
        var original = store.Find(user.Id)!;
        store.Update(user.Id, "user", true, null);
        Assert.Equal(original, store.Find(user.Id));
        store.Update(user.Id, "admin", true, null);
        var promoted = store.Find(user.Id)!;
        Assert.NotEqual(original.SecurityStamp, promoted.SecurityStamp);
        store.Update(user.Id, "admin", false, null);
        var disabled = store.Find(user.Id)!;
        Assert.NotEqual(promoted.SecurityStamp, disabled.SecurityStamp);
        Assert.Null(store.Authenticate("person", Password));
        Assert.Equal(0, store.Find(user.Id)!.FailedLoginCount);
        store.Update(user.Id, "admin", true, null);
        var enabled = store.Find(user.Id)!;
        Assert.NotEqual(disabled.SecurityStamp, enabled.SecurityStamp);
        Assert.Equal(user.Id, store.Authenticate("person", Password)?.Id);
        store.Update(user.Id, "user", true, OtherPassword);
        var reset = store.Find(user.Id)!;
        Assert.NotEqual(enabled.SecurityStamp, reset.SecurityStamp);
        Assert.NotEqual(enabled.PasswordHash, reset.PasswordHash);
        Assert.Null(store.Authenticate("person", Password));
        Assert.Equal(user.Id, store.Authenticate("person", OtherPassword)?.Id);
        store.Update(user.Id, "user", true, OtherPassword);
        Assert.NotEqual(reset.SecurityStamp, store.Find(user.Id)!.SecurityStamp);
    }

    [Fact]
    public void DeletedAccountsCannotAuthenticateAndRecreationHasANewIdentity()
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", Password);
        var user = store.Create("person", Password, "user");
        var original = store.Find(user.Id)!;
        store.Delete(user.Id);
        Assert.Null(store.Find(user.Id));
        Assert.Null(store.Authenticate("person", Password));
        var replacement = store.Create("person", OtherPassword, "user");
        Assert.NotEqual(user.Id, replacement.Id);
        Assert.NotEqual(original.SecurityStamp, store.Find(replacement.Id)!.SecurityStamp);
        Assert.Null(store.Find(user.Id));
    }

    [Fact]
    public void CredentialsAndFailedAttemptsPersistAcrossStoreInstances()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        Assert.Null(store.Authenticate("admin", OtherPassword));
        var account = store.Find(admin.Id)!;
        var reopened = new UserStore(directory);
        Assert.True(reopened.HasUsers);
        Assert.Equal(account, reopened.Find(admin.Id));
        Assert.Equal(admin, Assert.Single(reopened.List()));
        Assert.Equal(admin.Id, reopened.Authenticate("admin", Password)?.Id);
        Assert.Equal(0, store.Find(admin.Id)!.FailedLoginCount);
    }

    [Fact]
    public void LiveFileEditsAreReloadedForFindAndAuthentication()
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", Password);
        var user = store.Create("person", Password, "user");
        var stamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        EditAccount(user.Id, account => account["securityStamp"] = stamp);
        Assert.Equal(stamp, store.Find(user.Id)!.SecurityStamp);
        Assert.Equal(stamp, store.Authenticate("person", Password)!.SecurityStamp);
        EditAccount(user.Id, account => account["enabled"] = false);
        Assert.False(store.Find(user.Id)!.Enabled);
        Assert.Null(store.Authenticate("person", Password));
    }

    [Fact]
    public void ManuallyDisablingAllAccountsDoesNotEnableBootstrapOrLogin()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        EditAccount(admin.Id, account => account["enabled"] = false);
        Assert.True(store.HasUsers);
        Assert.False(store.Find(admin.Id)!.Enabled);
        Assert.Null(store.Authenticate("admin", Password));
        Assert.Equal(409, Assert.Throws<AppException>(() => store.Bootstrap("replacement", OtherPassword)).StatusCode);
    }

    [Fact]
    public void FiveWrongAttemptsLockOnlyThatAccountAndLockoutIsDurable()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var user = store.Create("person", Password, "user");
        var originalStamp = store.Find(user.Id)!.SecurityStamp;
        var started = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Null(new UserStore(directory).Authenticate("PERSON", OtherPassword));
            Assert.Equal(attempt, store.Find(user.Id)!.FailedLoginCount);
        }
        var locked = store.Find(user.Id)!;
        Assert.InRange(locked.LockedUntil!.Value, started.AddMinutes(15), DateTimeOffset.UtcNow.AddMinutes(15));
        Assert.Equal(originalStamp, locked.SecurityStamp);
        var before = File.ReadAllText(UsersPath);
        Assert.Null(new UserStore(directory).Authenticate("person", Password));
        Assert.Null(store.Authenticate("person", OtherPassword));
        Assert.Equal(before, File.ReadAllText(UsersPath));
        Assert.Equal(admin.Id, store.Authenticate("admin", Password)?.Id);
    }

    [Fact]
    public void ExpiredLockoutAllowsLoginAndClearsFailures()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var now = DateTimeOffset.UtcNow;
        EditAccount(admin.Id, account =>
        {
            account["failedLoginCount"] = 5;
            account["failureWindowStartedAt"] = now.AddMinutes(-31);
            account["lockedUntil"] = now.AddMinutes(-16);
        });
        var result = Assert.IsType<Account>(store.Authenticate("admin", Password));
        Assert.Equal(0, result.FailedLoginCount);
        Assert.Null(result.FailureWindowStartedAt);
        Assert.Null(result.LockedUntil);
        Assert.Equal(result, new UserStore(directory).Find(admin.Id));
    }

    [Fact]
    public void WrongAttemptAfterExpiredWindowStartsANewWindow()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        EditAccount(admin.Id, account =>
        {
            account["failedLoginCount"] = 4;
            account["failureWindowStartedAt"] = DateTimeOffset.UtcNow.AddMinutes(-16);
        });
        Assert.Null(store.Authenticate("admin", OtherPassword));
        var account = store.Find(admin.Id)!;
        Assert.Equal(1, account.FailedLoginCount);
        Assert.Null(account.LockedUntil);
        Assert.True(account.FailureWindowStartedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void AdministratorPasswordResetClearsLockoutAndRevokesPreviousStamp()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var originalStamp = store.Find(admin.Id)!.SecurityStamp;
        EditAccount(admin.Id, account =>
        {
            account["failedLoginCount"] = 5;
            account["failureWindowStartedAt"] = DateTimeOffset.UtcNow;
            account["lockedUntil"] = DateTimeOffset.UtcNow.AddMinutes(15);
        });
        store.Update(admin.Id, "admin", true, OtherPassword);
        var account = store.Find(admin.Id)!;
        Assert.Equal(0, account.FailedLoginCount);
        Assert.Null(account.FailureWindowStartedAt);
        Assert.Null(account.LockedUntil);
        Assert.NotEqual(originalStamp, account.SecurityStamp);
        Assert.Equal(admin.Id, store.Authenticate("admin", OtherPassword)?.Id);
    }

    [Fact]
    public void SelfServicePasswordChangeRequiresCurrentPasswordAndCurrentStamp()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var stamp = store.Find(admin.Id)!.SecurityStamp;
        Assert.Equal(401, Assert.Throws<AppException>(() => store.ChangePassword(admin.Id, OtherPassword, OtherPassword, stamp)).StatusCode);
        store.ChangePassword(admin.Id, Password, OtherPassword, stamp);
        Assert.NotEqual(stamp, store.Find(admin.Id)!.SecurityStamp);
        Assert.Null(store.Authenticate("admin", Password));
        Assert.Equal(admin.Id, store.Authenticate("admin", OtherPassword)?.Id);
        Assert.Equal(401, Assert.Throws<AppException>(() => store.ChangePassword(admin.Id, OtherPassword, Password, stamp)).StatusCode);
        Assert.Equal(admin.Id, store.Authenticate("admin", OtherPassword)?.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"users\":[]}")]
    [InlineData("{\"version\":1,\"users\":null}")]
    [InlineData("{\"version\":1,\"users\":[],\"version\":1}")]
    [InlineData("{\"version\":1,\"users\":[],\"unexpected\":true}")]
    public void CorruptFilesFailClosedAndAreNeverReset(string json)
    {
        var store = new UserStore(directory);
        File.WriteAllText(UsersPath, json);
        Assert.Equal(503, Assert.Throws<AppException>(() => store.HasUsers).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => store.List()).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => store.Authenticate("admin", Password)).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => store.Bootstrap("admin", Password)).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => new UserStore(directory)).StatusCode);
        Assert.Equal(json, File.ReadAllText(UsersPath));
    }

    [Theory]
    [InlineData("passwordHash", "plaintext-is-not-a-hash")]
    [InlineData("passwordHash", "pbkdf2-sha256$1$invalid$invalid")]
    [InlineData("securityStamp", "short")]
    [InlineData("role", "owner")]
    [InlineData("username", "not valid")]
    [InlineData("id", "not-an-id")]
    public void InvalidCredentialSchemaIsRejectedWithoutOverwriting(string field, string value)
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        EditAccount(admin.Id, account => account[field] = value);
        var before = File.ReadAllText(UsersPath);
        Assert.Equal(503, Assert.Throws<AppException>(() => store.Find(admin.Id)).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => store.Update(admin.Id, "admin", true, OtherPassword)).StatusCode);
        Assert.Equal(before, File.ReadAllText(UsersPath));
    }

    [Fact]
    public void MissingEnabledFlagIsNotSilentlyDefaulted()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        EditAccount(admin.Id, account => account.Remove("enabled"));
        Assert.Equal(503, Assert.Throws<AppException>(() => store.Find(admin.Id)).StatusCode);
    }

    [Fact]
    public void DuplicateUsernamesInManuallyEditedFileAreRejected()
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", Password);
        var user = store.Create("person", Password, "user");
        EditAccount(user.Id, account => account["username"] = "ADMIN");
        Assert.Equal(503, Assert.Throws<AppException>(() => store.List()).StatusCode);
    }

    [Fact]
    public void RemovingAnObservedFileDoesNotSilentlyResetTheStore()
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", Password);
        File.Delete(UsersPath);
        Assert.Equal(503, Assert.Throws<AppException>(() => store.HasUsers).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => store.Bootstrap("replacement", OtherPassword)).StatusCode);
        Assert.Equal(503, Assert.Throws<AppException>(() => new UserStore(directory)).StatusCode);
        Assert.False(File.Exists(UsersPath));
    }

    [Fact]
    public async Task ConcurrentFailuresCannotLoseIncrementsOrExceedTheLockoutLimit()
    {
        var store = new UserStore(directory);
        var admin = store.Bootstrap("admin", Password);
        var results = await Task.WhenAll(Enumerable.Range(0, 7).Select(_ => Task.Run(() =>
            new UserStore(directory).Authenticate("admin", OtherPassword))));
        Assert.All(results, result => Assert.Null(result));
        Assert.Equal(5, store.Find(admin.Id)!.FailedLoginCount);
        Assert.NotNull(store.Find(admin.Id)!.LockedUntil);
    }

    [Fact]
    public async Task ConcurrentStoresCannotCreateCaseInsensitiveDuplicates()
    {
        var store = new UserStore(directory);
        store.Bootstrap("admin", Password);
        var attempts = await Task.WhenAll(new[] { "Person", "PERSON" }.Select(username => Task.Run(() =>
        {
            try { new UserStore(directory).Create(username, Password, "user"); return 201; }
            catch (AppException exception) { return exception.StatusCode; }
        })));
        Assert.Equal(new[] { 201, 409 }, attempts.OrderBy(status => status).ToArray());
        Assert.Equal(2, store.List().Count);
        Assert.Equal(2, new UserStore(directory).List().Count);
    }

    [Fact]
    public void FailedAtomicReplacementPreservesOldFileAndCleansTemporaryFile()
    {
        // Windows sharing modes allow reads but deny rename/replacement, providing a real failed commit.
        // Unix permits renaming open files, so this failure injection is Windows-specific.
        if (!OperatingSystem.IsWindows()) return;
        var store = new UserStore(directory);
        store.Bootstrap("admin", Password);
        var user = store.Create("person", Password, "user");
        var before = File.ReadAllText(UsersPath);
        using (var held = new FileStream(UsersPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(503, Assert.Throws<AppException>(() => store.Update(user.Id, "admin", true, null)).StatusCode);
        Assert.Equal(before, File.ReadAllText(UsersPath));
        Assert.Equal("user", new UserStore(directory).Find(user.Id)!.Role);
        Assert.Empty(Directory.GetFiles(directory, ".users-*.tmp"));
        Assert.Equal("admin", store.Update(user.Id, "admin", true, null).Role);
    }

    [Fact]
    public void AccountStorageCannotBePlacedInTheConventionalWebRoot()
    {
        var unsafePath = Path.Combine(directory, "wwwroot", "private");
        Assert.Equal(503, Assert.Throws<AppException>(() => new UserStore(unsafePath)).StatusCode);
        Assert.False(Directory.Exists(unsafePath));
    }

    [Fact]
    public void DataProtectionAndStoreUseLateResolvedAppPaths()
    {
        var original = Path.Combine(directory, "must-not-be-used");
        var replacement = Path.Combine(directory, "override");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AppPaths(original));
        AccountSecurity.AddAccountSecurity(services, new ConfigurationBuilder().Build());
        Assert.False(Directory.Exists(original));
        services.AddSingleton(new AppPaths(replacement));
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<UserStore>().HasUsers);
        var options = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var repository = Assert.IsType<FileSystemXmlRepository>(options.XmlRepository);
        Assert.Equal(Path.Combine(replacement, "keys"), repository.Directory.FullName);
        Assert.True(Directory.Exists(replacement));
        Assert.False(Directory.Exists(original));
    }

    private void EditAccount(string id, Action<JsonObject> edit)
    {
        var document = JsonNode.Parse(File.ReadAllText(UsersPath))!.AsObject();
        var account = document["users"]!.AsArray().Single(user => user!["id"]!.GetValue<string>() == id)!.AsObject();
        edit(account);
        File.WriteAllText(UsersPath, document.ToJsonString());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}