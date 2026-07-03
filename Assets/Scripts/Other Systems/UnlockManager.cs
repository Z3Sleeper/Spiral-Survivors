using System.Collections.Generic;

public static class UnlockManager
{
    private static readonly Queue<string> _pending = new();

    public static bool HasPending => _pending.Count > 0;

    public static event System.Action OnUnlockQueued;

    public static void Queue(string message)
    {
        _pending.Enqueue(message);
        OnUnlockQueued?.Invoke();
    }

    public static string Dequeue() => _pending.Count > 0 ? _pending.Dequeue() : null;

    public static void Clear() => _pending.Clear();
}
