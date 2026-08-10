using System.Collections.Generic;
using PurrNet;
using PurrNet.Modules;
using Steamworks;
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

        // Reset the Steam lobby's "Started" flag back to False as the very first thing this method does —
        // nothing else in the whole return-to-lobby flow ever touches it, so without this it stays stuck at
        // "True" from the original match-start forever. LobbySceneController.Start() also resets it
        // (host-only, kept as a harmless backstop), but that only runs AFTER the host's own Lobby scene has
        // already reloaded — far too late to help a CLIENT whose own fresh Lobby scene can finish loading
        // (via PurrNet's own networked scene sync, a completely separate system from this Steam data write)
        // and check OnRoomUpdated before this write has had any chance to propagate through Steam's own,
        // independent callback system. Doing it here instead, before the flush RPC / scene load even begin,
        // gives it the whole multi-second confirm window as a head start — a client reading stale "True"
        // left over from the original match was skipping straight into ConnectToHostWithRetry() the instant
        // its own scene reloaded, colliding with objects that hadn't finished settling yet.
        if (GameStartData.TryGetCurrentSteamLobbyId(out CSteamID lobbyId))
        {
            SteamMatchmaking.SetLobbyData(lobbyId, "Started", "False");
            // Same reset, same reasoning — see LobbySceneController.OnAllReady's own comment on the
            // "Locking" write. A stale "True" left over from the original match-start would show every
            // client's LoadingScreen (and lock every lobby button via its IsShowing guards) immediately on
            // landing back in a fresh Lobby scene, with nothing left to ever clear it.
            SteamMatchmaking.SetLobbyData(lobbyId, "Locking", "False");
        }

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
