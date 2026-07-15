using System.Collections.Generic;
using PurrNet;
using PurrNet.Modules;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ReturnToLobbyManager : NetworkIdentity
{
    public static ReturnToLobbyManager instance;

    // Safety net only — if a client never confirms (silent disconnect, etc.), the server still needs to
    // be able to proceed rather than hang the whole lobby transition forever.
    [SerializeField] private float confirmTimeoutSeconds = 5f;

    private readonly HashSet<PlayerID> _confirmedFlushed = new();
    private bool  _waitingForFlushConfirmation;
    private float _waitTimer;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    // Does NOT call LoadSceneAsync directly. Scene load only happens once every connected client has
    // explicitly confirmed it flushed its own upgrade points — an RPC firing is not proof it was
    // received and processed before the scene (and the objects that flush depends on) starts tearing
    // down. Waiting for real acknowledgment is the only way to be certain.
    //
    // LocalUpgradePointsTracker.Flush() is plain, synchronous, non-networked code — no PredictedIdentity
    // involved, so unlike the old CharacterStatsPD-based version of this flow, there's no "wait a tick
    // for it to land" concern here. It's safe to flush and confirm in the same call.
    public void ReturnToLobby()
    {
        if (!isServer || _waitingForFlushConfirmation) return;

        _waitingForFlushConfirmation = true;
        _waitTimer = 0f;
        _confirmedFlushed.Clear();

        LocalUpgradePointsTracker.instance.Flush();
        PurrLobby.LobbyFlags.IsReturningFromGame = true;
        EnemyBasePD.ResetActiveCounts();
        if (NetworkManager.main.isLocalPlayerReady)
            _confirmedFlushed.Add(NetworkManager.main.localPlayer);

        RequestFlushRpc();
    }

    // Also sets IsReturningFromGame here (rather than in a separate RPC fired right before
    // LoadSceneAsync) so it's guaranteed set on every machine before that machine's own confirmation is
    // sent — same "don't trust an RPC firing as proof of anything, wait for real acknowledgment"
    // reasoning as the flush itself. A separate post-confirmation RPC would race the scene load exactly
    // the way the old flush design raced CharacterStatsPD.
    [ObserversRpc]
    private void RequestFlushRpc()
    {
        if (isServer) return; // server already flushed directly in ReturnToLobby()

        LocalUpgradePointsTracker.instance.Flush();
        PurrLobby.LobbyFlags.IsReturningFromGame = true;
        EnemyBasePD.ResetActiveCounts();
        if (NetworkManager.main.isLocalPlayerReady)
            ConfirmFlushedRpc(NetworkManager.main.localPlayer);
    }

    [ServerRpc(requireOwnership: false)]
    private void ConfirmFlushedRpc(PlayerID player)
    {
        _confirmedFlushed.Add(player);
    }

    private void Update()
    {
        if (!_waitingForFlushConfirmation || !isServer) return;

        // Unscaled — the game is very likely still paused/frozen at this point, and this wait must not
        // stall alongside it.
        _waitTimer += Time.unscaledDeltaTime;

        bool allConfirmed = _confirmedFlushed.Count >= NetworkManager.main.playerCount;
        bool timedOut = _waitTimer >= confirmTimeoutSeconds;
        if (!allConfirmed && !timedOut) return;

        _waitingForFlushConfirmation = false;
        if (timedOut && !allConfirmed)
            Debug.LogWarning($"[ReturnToLobbyManager] Timed out waiting for flush confirmation ({_confirmedFlushed.Count}/{NetworkManager.main.playerCount} players) — proceeding to lobby anyway.");

        ProceedToLobby();
    }

    private void ProceedToLobby()
    {
        Time.timeScale = 1f;
        NetworkManager.main.sceneModule.LoadSceneAsync(GameStartData.LobbySceneName, new PurrSceneSettings
        {
            mode = LoadSceneMode.Single,
            isPublic = true
        });
    }
}
