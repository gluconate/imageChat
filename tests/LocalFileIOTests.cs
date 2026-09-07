using ImageChat;
using Xunit;

namespace ImageChat.Tests;

public class LocalFileIOTests
{
    [Fact]
    public void OrdinaryAccessDeniedIsNotRetried()
    {
        var attempts = 0;
        Assert.Throws<UnauthorizedAccessException>(() => LocalFileIO.RetrySharing<int>(() =>
        {
            attempts++;
            throw new UnauthorizedAccessException();
        }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void TemporaryWindowsRenameDenialCanRecoverWithoutChangingPermissions()
    {
        if (!OperatingSystem.IsWindows()) return;
        var attempts = 0;
        var result = LocalFileIO.RetrySharing(() =>
        {
            if (++attempts < 3) throw new UnauthorizedAccessException();
            return 42;
        }, retryRenameAccessDenied: true);
        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void PersistentRenameDenialRemainsAnErrorAfterBoundedRetries()
    {
        var attempts = 0;
        Assert.Throws<UnauthorizedAccessException>(() => LocalFileIO.RetrySharing<int>(() =>
        {
            attempts++;
            throw new UnauthorizedAccessException();
        }, retryRenameAccessDenied: true));
        Assert.Equal(OperatingSystem.IsWindows() ? 6 : 1, attempts);
    }

    [Fact]
    public void UnrelatedIoErrorsAreNeverRetried()
    {
        var attempts = 0;
        Assert.Throws<IOException>(() => LocalFileIO.RetrySharing<int>(() =>
        {
            attempts++;
            throw new IOException("Synthetic non-sharing failure.");
        }, retryRenameAccessDenied: true));
        Assert.Equal(1, attempts);
    }
}