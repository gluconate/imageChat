namespace ImageChat;

// A lease spans the entire generation (or deletion), but only for its own chat.
// The short monitor protects membership, never a network call or database transaction.
// Entries are removed on release so viewing/creating many chats cannot leak semaphores.
public sealed class ChatOperations
{
    private readonly HashSet<string> active = new(StringComparer.Ordinal);

    public IDisposable Acquire(string chatId)
    {
        lock (active)
        {
            if (!active.Add(chatId))
                throw new AppException(409, "This chat is busy. Wait for its current operation to finish; you can work in another chat.");
        }
        return new Lease(this, chatId);
    }

    private sealed class Lease(ChatOperations owner, string chatId) : IDisposable
    {
        private ChatOperations? owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current == null) return;
            lock (current.active) current.active.Remove(chatId);
        }
    }
}