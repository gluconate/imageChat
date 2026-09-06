using ImageChat;
using Xunit;

namespace ImageChat.Tests;

public class ChatOperationsTests
{
    [Fact]
    public void OnlySameChatConflictsAndDisposalIsIdempotent()
    {
        var operations = new ChatOperations();
        var first = operations.Acquire("a");
        using var other = operations.Acquire("b");
        Assert.Equal(409, Assert.Throws<AppException>(() => operations.Acquire("a")).StatusCode);
        first.Dispose();
        using var next = operations.Acquire("a");
        first.Dispose();
        Assert.Equal(409, Assert.Throws<AppException>(() => operations.Acquire("a")).StatusCode);
    }

    [Fact]
    public void ExceptionReleasesLease()
    {
        var operations = new ChatOperations();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var operation = operations.Acquire("a");
            throw new InvalidOperationException();
        }));
        using var next = operations.Acquire("a");
    }

    [Fact]
    public async Task ContendingRequestsHaveExactlyOneOwner()
    {
        var operations = new ChatOperations();
        using var owner = operations.Acquire("a");
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            Assert.Equal(409, Assert.Throws<AppException>(() => operations.Acquire("a")).StatusCode);
        })));
    }
}