namespace ImageChat;

internal static class LocalFileIO
{
    // Windows scanners/editors may briefly hold a new file without delete sharing.
    // Retry only sharing/lock violations, never invalid data or denied permissions.
    internal static T RetrySharing<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (IOException error) when (OperatingSystem.IsWindows() &&
                (error.HResult & 0xffff) is 32 or 33 && attempt < 5)
            {
                Thread.Sleep(20 << attempt);
            }
        }
    }
}