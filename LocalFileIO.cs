namespace ImageChat;

internal static class LocalFileIO
{
    // Windows scanners/editors may briefly hold a new file without delete sharing.
    // Rename-over-existing can also return ACCESS_DENIED while a Windows scanner holds
    // the destination. Opt in only for that atomic replacement, never ordinary reads.
    // Persistent permission failures still propagate after a bounded retry; no ACL changes.
    internal static T RetrySharing<T>(Func<T> action, bool retryRenameAccessDenied = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (IOException error) when (OperatingSystem.IsWindows() &&
                (error.HResult & 0xffff) is 32 or 33 && attempt < 5)
            {
                Thread.Sleep(20 << attempt);
            }
            catch (UnauthorizedAccessException error) when (retryRenameAccessDenied && OperatingSystem.IsWindows() &&
                (error.HResult & 0xffff) == 5 && attempt < 5)
            {
                Thread.Sleep(20 << attempt);
            }
        }
    }
}