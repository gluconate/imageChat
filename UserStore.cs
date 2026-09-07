using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("ImageChat.Tests")]

namespace ImageChat;

public sealed record UserView(string Id, string Username, string Role, bool Enabled, DateTimeOffset CreatedAt);

// Credential-bearing records are deliberately internal. Only ToView() belongs in HTTP responses.
internal sealed record Account(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Username,
    [property: JsonRequired] string Role,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] string PasswordHash,
    [property: JsonRequired] string SecurityStamp,
    [property: JsonRequired] DateTimeOffset CreatedAt,
    int FailedLoginCount = 0,
    DateTimeOffset? FailureWindowStartedAt = null,
    DateTimeOffset? LockedUntil = null)
{
    public UserView ToView() => new(Id, Username, Role, Enabled, CreatedAt);
    public override string ToString() => "Account { [redacted] }";
}

public sealed class UserStore
{
    internal const string InvalidLoginMessage = "Invalid username or password.";
    internal const string SetupMessage = "No accounts are configured. An operator must run the offline --create-admin command.";
    private const string StorageError = "Account storage is unavailable or invalid. An operator must check the account file; it has not been reset.";
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaxUsers = 10_000;
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const int FailureLimit = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<string, StoreGate> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    // A random, unusable hash still incurs the same PBKDF2 cost for unknown usernames.
    private static readonly string DummyHash = EncodeHash(RandomNumberGenerator.GetBytes(SaltBytes),
        RandomNumberGenerator.GetBytes(HashBytes));
    private readonly string directory;
    private readonly string filePath;
    private readonly string lockPath;
    private readonly StoreGate gate;

    public UserStore(string dataDirectory)
    {
        directory = ResolveDataDirectory(dataDirectory);
        filePath = Path.Combine(directory, "users.json");
        lockPath = Path.Combine(directory, ".users.lock");
        gate = Gates.GetOrAdd(filePath, _ => new StoreGate());
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { throw Unavailable(ex); }
        // Opening an existing corrupt file fails closed, including during offline bootstrap.
        WithFile(users => users.Users.Count);
    }

    public bool HasUsers => WithFile(users => users.Users.Count != 0);

    public List<UserView> List() => WithFile(users => users.Users
        .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase).Select(user => user.ToView()).ToList());

    internal Account? Find(string id) => WithFile(users => users.Users.FirstOrDefault(user => user.Id == id));

    public UserView Bootstrap(string username, string password)
    {
        ValidateUsername(username);
        ValidatePassword(password);
        WithFile(users => { EnsureEmpty(users); return 0; });
        var hash = HashPassword(password);
        return WithFile(users =>
        {
            EnsureEmpty(users);
            var account = NewAccount(username, "admin", hash);
            users.Users.Add(account);
            Save(users);
            return account.ToView();
        });
    }

    public UserView Create(string username, string password, string role)
    {
        ValidateUsername(username);
        ValidatePassword(password);
        ValidateRole(role);
        WithFile(users => { EnsureCanCreate(users, username); return 0; });
        var hash = HashPassword(password);
        return WithFile(users =>
        {
            EnsureCanCreate(users, username);
            var account = NewAccount(username, role, hash);
            users.Users.Add(account);
            Save(users);
            return account.ToView();
        });
    }

    // Offline operator recovery only; deliberately not exposed through an HTTP endpoint.
    // Preserve identity/role/enabled state so resetting never reassigns chats or grants access.
    internal UserView ResetAdminPassword(string username, string password)
    {
        ValidateUsername(username);
        ValidatePassword(password);
        var snapshot = WithFile(users => users.Users.FirstOrDefault(user =>
            string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase) && user.Role == "admin"))
            ?? throw new AppException(404, "Administrator account not found. No account was changed.");
        var hash = HashPassword(password);
        return WithFile(users =>
        {
            var current = users.Users.FirstOrDefault(user => user.Id == snapshot.Id);
            if (current == null || !SameCredentials(current, snapshot))
                throw new AppException(409, "The administrator account changed during reset. Try again.");
            var updated = current with
            {
                PasswordHash = hash,
                SecurityStamp = NewStamp(),
                FailedLoginCount = 0,
                FailureWindowStartedAt = null,
                LockedUntil = null
            };
            Replace(users, updated);
            Save(users);
            return updated.ToView();
        });
    }

    public UserView Update(string id, string role, bool enabled, string? password = null)
    {
        ValidateRole(role);
        if (password != null) ValidatePassword(password);
        WithFile(users => { EnsureAdminRemains(users, GetAccount(users, id), role, enabled); return 0; });
        var hash = password == null ? null : HashPassword(password);
        return WithFile(users =>
        {
            var account = GetAccount(users, id);
            EnsureAdminRemains(users, account, role, enabled);
            var changed = account.Role != role || account.Enabled != enabled || hash != null;
            if (!changed) return account.ToView();
            var updated = account with
            {
                Role = role,
                Enabled = enabled,
                PasswordHash = hash ?? account.PasswordHash,
                SecurityStamp = NewStamp(),
                FailedLoginCount = hash == null ? account.FailedLoginCount : 0,
                FailureWindowStartedAt = hash == null ? account.FailureWindowStartedAt : null,
                LockedUntil = hash == null ? account.LockedUntil : null
            };
            Replace(users, updated);
            Save(users);
            return updated.ToView();
        });
    }

    public void Delete(string id) => WithFile(users =>
    {
        var account = GetAccount(users, id);
        EnsureAdminRemains(users, account, account.Role, false);
        users.Users.Remove(account);
        Save(users);
        return 0;
    });

    internal void RevokeSessions(string id, string expectedStamp) => WithFile(users =>
    {
        var account = users.Users.FirstOrDefault(user => user.Id == id);
        if (account != null && account.SecurityStamp == expectedStamp)
        {
            Replace(users, account with { SecurityStamp = NewStamp() });
            Save(users);
        }
        return 0;
    });

    internal Account? Authenticate(string username, string password)
    {
        var snapshot = WithFile(users => IsUsernameValid(username)
            ? users.Users.FirstOrDefault(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))
            : null);
        // Neither the process monitor nor the cross-process file lock is held during PBKDF2.
        var matches = VerifyPassword(snapshot?.PasswordHash ?? DummyHash, password);
        if (snapshot == null) return null;
        return WithFile<Account?>(users =>
        {
            var current = users.Users.FirstOrDefault(user => user.Id == snapshot.Id);
            if (current == null || !current.Enabled || !SameCredentials(current, snapshot)) return null;
            var now = DateTimeOffset.UtcNow;
            if (current.LockedUntil > now) return null;
            if (!matches)
            {
                var newWindow = current.FailureWindowStartedAt == null ||
                    current.FailureWindowStartedAt <= now - LockoutWindow || current.LockedUntil != null;
                var failures = newWindow ? 1 : current.FailedLoginCount + 1;
                var failed = current with
                {
                    FailedLoginCount = failures,
                    FailureWindowStartedAt = newWindow ? now : current.FailureWindowStartedAt,
                    LockedUntil = failures >= FailureLimit ? now + LockoutWindow : null
                };
                Replace(users, failed);
                Save(users);
                return null;
            }
            if (current.FailedLoginCount != 0 || current.LockedUntil != null)
            {
                current = current with { FailedLoginCount = 0, FailureWindowStartedAt = null, LockedUntil = null };
                Replace(users, current);
                Save(users);
            }
            return current;
        });
    }

    // A self-service change must not overwrite an intervening administrator reset or role change.
    internal void ChangePassword(string id, string currentPassword, string newPassword, string expectedStamp)
    {
        ValidatePassword(newPassword);
        var snapshot = Find(id);
        if (snapshot == null || !snapshot.Enabled || snapshot.SecurityStamp != expectedStamp)
            throw new AppException(401, InvalidLoginMessage);
        var authenticated = Authenticate(snapshot.Username, currentPassword);
        if (authenticated == null || !SameCredentials(authenticated, snapshot))
            throw new AppException(401, InvalidLoginMessage);
        var hash = HashPassword(newPassword);
        WithFile(users =>
        {
            var current = users.Users.FirstOrDefault(user => user.Id == id);
            if (current == null || !current.Enabled || !SameCredentials(current, authenticated) ||
                current.LockedUntil > DateTimeOffset.UtcNow)
                throw new AppException(401, InvalidLoginMessage);
            Replace(users, current with
            {
                PasswordHash = hash,
                SecurityStamp = NewStamp(),
                FailedLoginCount = 0,
                FailureWindowStartedAt = null,
                LockedUntil = null
            });
            Save(users);
            return 0;
        });
    }

    private T WithFile<T>(Func<UsersFile, T> action)
    {
        lock (gate)
        {
            try
            {
                // Kept on disk (empty) so two processes cannot lock different unlinked files.
                // A competing process fails closed rather than overwriting another update.
                using var fileLock = LocalFileIO.RetrySharing(() => new FileStream(lockPath, PrivateFileOptions(FileMode.OpenOrCreate)));
                return action(Load());
            }
            catch (Exception ex) when (IsStorageFailure(ex)) { throw Unavailable(ex); }
        }
    }

    private UsersFile Load()
    {
        byte[] bytes;
        try
        {
            using var file = LocalFileIO.RetrySharing(() => new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            gate.HasObservedFile = true;
            if (file.Length is <= 0 or > MaxFileBytes) throw Unavailable();
            bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) throw Unavailable();
        }
        catch (FileNotFoundException)
        {
            if (gate.HasObservedFile) throw Unavailable();
            return new(1, []);
        }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        EnsureNoDuplicateProperties(document.RootElement);
        var users = document.RootElement.Deserialize<UsersFile>(JsonOptions) ?? throw Unavailable();
        ValidateFile(users);
        return users;
    }

    private void Save(UsersFile users)
    {
        ValidateFile(users);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(users, JsonOptions);
        if (bytes.Length > MaxFileBytes) throw Unavailable();
        var temporary = Path.Combine(directory, $".users-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = PrivateFileOptions(FileMode.CreateNew);
            options.Options = FileOptions.WriteThrough;
            using (var file = new FileStream(temporary, options))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            // Same-directory rename with replacement: readers see an entire old or new document.
            LocalFileIO.RetrySharing(() => { File.Move(temporary, filePath, overwrite: true); return 0; }, retryRenameAccessDenied: true);
            gate.HasObservedFile = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            try { File.Delete(temporary); }
            catch (Exception ex) when (IsStorageFailure(ex)) { /* Never expose paths or credential data. */ }
        }
    }

    private static FileStreamOptions PrivateFileOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static void ValidateFile(UsersFile file)
    {
        if (file.Version != 1 || file.Users == null || file.Users.Count > MaxUsers) throw Unavailable();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in file.Users)
        {
            if (account == null || !Guid.TryParseExact(account.Id, "N", out _) ||
                !ids.Add(account.Id) || !IsUsernameValid(account.Username) || !usernames.Add(account.Username) ||
                account.Role is not ("admin" or "user") || account.CreatedAt <= DateTimeOffset.UnixEpoch ||
                !TryDecodeHash(account.PasswordHash, out _, out _) || !IsCanonicalBase64(account.SecurityStamp, HashBytes) ||
                account.FailedLoginCount is < 0 or > FailureLimit ||
                (account.FailedLoginCount == 0) != (account.FailureWindowStartedAt == null) ||
                (account.FailedLoginCount == FailureLimit) != (account.LockedUntil != null) ||
                account.FailureWindowStartedAt <= DateTimeOffset.UnixEpoch || account.LockedUntil <= DateTimeOffset.UnixEpoch ||
                (account.LockedUntil != null && account.LockedUntil < account.FailureWindowStartedAt))
                throw Unavailable();
        }
        // An operator may intentionally disable all accounts in the file. That is not first-run
        // setup: HasUsers stays true and bootstrap remains forbidden. API writes guard the last admin.
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Unavailable();
                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) EnsureNoDuplicateProperties(child);
    }

    private static bool IsStorageFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or JsonException or NotSupportedException;
    private static AppException Unavailable(Exception? cause = null) => new(503, cause == null ? StorageError :
        $"{StorageError} ({cause.GetType().Name}, code 0x{cause.HResult:X8}.)");
    internal static string ResolveDataDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        if (fullPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => string.Equals(segment, "wwwroot", StringComparison.OrdinalIgnoreCase)))
            throw new AppException(503, "Account storage must be outside the web root.");
        return fullPath;
    }
    private static string NewStamp() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(HashBytes));
    private static Account NewAccount(string username, string role, string hash) =>
        new(Guid.NewGuid().ToString("N"), username, role, true, hash, NewStamp(), DateTimeOffset.UtcNow);
    private static bool SameCredentials(Account left, Account right) =>
        left.Id == right.Id && left.Username == right.Username && left.Role == right.Role &&
        left.Enabled == right.Enabled && left.PasswordHash == right.PasswordHash && left.SecurityStamp == right.SecurityStamp;
    private static Account GetAccount(UsersFile users, string id) =>
        users.Users.FirstOrDefault(account => account.Id == id) ?? throw new AppException(404, "Account not found.");
    private static void Replace(UsersFile users, Account account) =>
        users.Users[users.Users.FindIndex(existing => existing.Id == account.Id)] = account;
    private static void EnsureEmpty(UsersFile users)
    {
        if (users.Users.Count != 0) throw new AppException(409, "Accounts already exist. Offline bootstrap is not allowed.");
    }
    private static void EnsureCanCreate(UsersFile users, string username)
    {
        if (users.Users.Count == 0) throw new AppException(503, SetupMessage);
        if (users.Users.Count >= MaxUsers) throw new AppException(409, "The account limit has been reached.");
        if (users.Users.Any(account => string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase)))
            throw new AppException(409, "That username is already in use.");
    }
    private static void EnsureAdminRemains(UsersFile users, Account account, string role, bool enabled)
    {
        if (account.Role == "admin" && account.Enabled && (role != "admin" || !enabled) &&
            users.Users.Count(user => user.Role == "admin" && user.Enabled) == 1)
            throw new AppException(409, "At least one enabled administrator is required.");
    }
    private static bool IsUsernameValid(string? username) => username is { Length: >= 3 and <= 64 } &&
        username.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static void ValidateUsername(string username)
    {
        if (!IsUsernameValid(username))
            throw new AppException(400, "Usernames must be 3 to 64 ASCII letters, digits, periods, underscores, or hyphens.");
    }
    private static void ValidateRole(string role)
    {
        if (role is not ("admin" or "user")) throw new AppException(400, "Role must be admin or user.");
    }
    private static void ValidatePassword(string password)
    {
        if (password is not { Length: >= 12 and <= 128 })
            throw new AppException(400, "Passwords must be between 12 and 128 characters.");
    }
    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        try { return EncodeHash(salt, hash); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
    private static string EncodeHash(byte[] salt, byte[] hash) =>
        $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    private static bool VerifyPassword(string encoded, string? password)
    {
        if (!TryDecodeHash(encoded, out var salt, out var expected)) throw Unavailable();
        var validLength = password is { Length: >= 12 and <= 128 };
        var actual = Rfc2898DeriveBytes.Pbkdf2(validLength ? password! : "invalid-account-password",
            salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        try { return CryptographicOperations.FixedTimeEquals(actual, expected) & validLength; }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }
    private static bool TryDecodeHash(string? encoded, out byte[] salt, out byte[] hash)
    {
        salt = [];
        hash = [];
        if (encoded is not { Length: <= 128 }) return false;
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || parts[1] != "600000" ||
            !IsCanonicalBase64(parts[2], SaltBytes) || !IsCanonicalBase64(parts[3], HashBytes)) return false;
        salt = Convert.FromBase64String(parts[2]);
        hash = Convert.FromBase64String(parts[3]);
        return true;
    }
    private static bool IsCanonicalBase64(string? value, int bytes)
    {
        if (value == null || value.Length != 4 * ((bytes + 2) / 3)) return false;
        Span<byte> buffer = stackalloc byte[bytes];
        return Convert.TryFromBase64String(value, buffer, out var written) && written == bytes &&
            Convert.ToBase64String(buffer) == value;
    }

    private sealed record UsersFile([property: JsonRequired] int Version, [property: JsonRequired] List<Account> Users);
    private sealed class StoreGate
    {
        public bool HasObservedFile { get; set; }
    }
}