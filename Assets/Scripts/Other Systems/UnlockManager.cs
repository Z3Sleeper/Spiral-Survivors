using System.Collections.Generic;

public static class UnlockManager
{
    private static readonly Queue<string> _pending = new();

    public static bool HasPending => _pending.Count > 0;
    // Read BEFORE Dequeue()-ing the current sequence's first item, so it captures the whole sequence's
    // total size — see LobbySceneController.ShowNextUnlock's own doc comment.
    public static int PendingCount => _pending.Count;

    public static event System.Action OnUnlockQueued;

    public static void Queue(string message)
    {
        _pending.Enqueue(message);
        OnUnlockQueued?.Invoke();
    }

    public static string Dequeue() => _pending.Count > 0 ? _pending.Dequeue() : null;

    public static void Clear() => _pending.Clear();
}
