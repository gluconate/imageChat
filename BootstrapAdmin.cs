namespace ImageChat;

public static class BootstrapAdmin
{
    public static void Run(string dataDirectory, bool resetPassword = false) =>
        Run(() => new UserStore(dataDirectory), resetPassword);

    public static void Run(Func<UserStore> createStore, bool resetPassword = false)
    {
        try
        {
            // Never accept piped passwords, which commonly end up in shell history or deployment logs.
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new AppException(400, "Offline administrator setup/reset requires an interactive terminal (use docker -it for containers).");
            var store = createStore();
            if (!resetPassword && store.HasUsers)
                throw new AppException(409, "Accounts already exist. To recover an administrator password, use --reset-admin-password.");
            if (resetPassword && !store.HasUsers)
                throw new AppException(404, "No accounts exist. Use --create-admin for first-time setup.");
            Console.Write("Administrator username: ");
            var username = Console.ReadLine();
            if (username == null) throw new AppException(400, "Administrator operation was cancelled.");
            if (resetPassword && !store.List().Any(user => user.Role == "admin" &&
                    string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)))
                throw new AppException(404, "Administrator account not found. No account was changed.");
            Console.Write(resetPassword ? "New password (12 to 128 characters): " : "Password (12 to 128 characters): ");
            var password = ReadPassword();
            Console.Write("Confirm password: ");
            var confirmation = ReadPassword();
            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
                throw new AppException(400, "Passwords do not match. No account was changed.");
            if (resetPassword)
            {
                var account = store.ResetAdminPassword(username, password);
                Console.WriteLine("Administrator password reset. Existing sessions are invalidated on their next request; login lockout is cleared. Chats and account identity are unchanged.");
                Console.WriteLine(account.Enabled ? "Sign in with your new password. No server restart is required."
                    : "This account remains disabled. An enabled administrator must enable it before it can sign in.");
            }
            else
            {
                store.Bootstrap(username, password);
                Console.WriteLine("Administrator account created. Sign in with the credentials you entered.");
            }
        }
        catch (AppException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("Administrator operation failed. Check terminal access and account storage permissions. No credentials are displayed.");
            Environment.ExitCode = 1;
        }
    }

    private static string ReadPassword()
    {
        var characters = new char[128];
        var length = 0;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return new string(characters, 0, length);
                }
                if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                {
                    Console.WriteLine();
                    throw new AppException(400, "Administrator operation was cancelled. No account was changed.");
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0) characters[--length] = '\0';
                    continue;
                }
                if (char.IsControl(key.KeyChar)) continue;
                if (length == characters.Length)
                {
                    Console.WriteLine();
                    throw new AppException(400, "Passwords must be between 12 and 128 characters. No account was changed.");
                }
                characters[length++] = key.KeyChar;
            }
        }
        finally { Array.Clear(characters); }
    }
}