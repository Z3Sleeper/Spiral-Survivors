using PurrLobby;
using PurrLobby.Providers;
using Steamworks;
using UnityEngine;

public class CharacterRegistry : MonoBehaviour
{
    public static CharacterRegistry instance;

    [SerializeField] private GameObject[] predictedCharacterPrefabs;

    public int PrefabCount() => predictedCharacterPrefabs.Length;
    public GameObject GetPredictedPrefab(int index) => predictedCharacterPrefabs[index];

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // Lives here (rather than on a scene-scoped manager) because this is the one object guaranteed to
    // still be alive at quit time regardless of which scene (lobby or game) the player was in — see
    // EnemyBasePD.ResetActiveCounts() for why this reset exists at all. Practically low-value (the process
    // is ending anyway) but kept for symmetry with the other exit paths.
    //
    // Also the fix for a real Steam-lobby leak: LobbySceneController.OnApplicationQuit() (the "official"
    // leave-on-quit path, via LobbyManager.LeaveLobby()) only exists — and only fires — while the Lobby
    // scene is loaded, because LobbyManager/SteamLobbyProvider are themselves scene-scoped to that same
    // scene (see LobbyManager.SetupDataHolder()'s own restore-on-reload dance, which only exists because
    // both get destroyed and recreated on every Lobby scene load). Neither exists at all while the Game
    // scene is loaded, so a player who quits (Alt+F4, Steam overlay "Exit Game", stopping Play Mode in the
    // Editor) mid-match never left the Steam lobby through that path — it stayed open on Steam's backend
    // for the rest of this process's Steam session (an Editor Play Mode stop doesn't tear down the
    // Steamworks connection the way a real process exit eventually would, so this could accumulate one
    // orphaned lobby per mid-match test-and-stop cycle in one continued Editor session). LobbyDataHolder
    // (PurrLobby's own genuinely-persistent, provider-agnostic lobby-tracking object — see its own
    // DontDestroyOnLoad) survives into the Game scene even though LobbyManager doesn't, so it's read
    // directly here rather than needing the full LobbyManager/provider stack to exist. Harmless to also
    // fire when LobbySceneController's own path already ran (e.g. quitting from the Lobby scene itself) —
    // leaving an already-left/invalid lobby is a silent no-op on Steam's side.
    private void OnApplicationQuit()
    {
        EnemyBasePD.ResetActiveCounts();

        // TEMP diagnostic — confirming this cross-scene safety net actually engages on a mid-match quit
        // (rather than, say, LobbyDataHolder never being found, or CurrentLobby already reading invalid by
        // the time this runs). Remove once a mid-match quit has been confirmed to log the "leaving lobby"
        // line and that lobby is confirmed gone from Steam's own lobby list shortly after.
        var holder = FindFirstObjectByType<LobbyDataHolder>();
        if (holder != null && holder.CurrentLobby.IsValid && ulong.TryParse(holder.CurrentLobby.LobbyId, out ulong lobbyId))
        {
            Debug.Log($"[CharacterRegistry] OnApplicationQuit: leaving lobby {lobbyId} directly via Steamworks (scene-scoped LobbyManager may not exist right now).");
            SteamMatchmaking.LeaveLobby(new CSteamID(lobbyId));
        }
        else
        {
            Debug.Log($"[CharacterRegistry] OnApplicationQuit: no valid lobby to leave (holder null: {holder == null}).");
        }

        // Paired with SteamLobbyProvider.HandleSteamInit()'s SteamAPI.Init() call — nothing else in the
        // whole project ever calls Shutdown(), which meant every SteamLobbyProvider.InitializeAsync() (one
        // per Lobby scene load) stacked another Init() on a session that was never cleanly closed. In the
        // Editor specifically, since the native Steamworks DLL stays loaded for the Editor process's whole
        // lifetime (Unity's domain/scene reload never touches it), this accumulated across every Play
        // session and was the actual cause of a live "[SteamServer] Failed to create listen socket" bug
        // that a fresh standalone build never reproduced even once. Deliberately NOT called from
        // SteamLobbyProvider.OnDestroy() — that fires on every ordinary Lobby-scene unload, including the
        // completely normal transition into a match, which would tear down the exact Steam session the
        // game's own networking depends on mid-match. Only a genuine process quit is safe for this.
        //
        // Also deliberately NOT a direct SteamAPI.Shutdown() call here anymore — that raced against
        // SteamLobbyProvider's own free-floating RunSteamCallbacks() loop (still mid-cycle on the same main
        // thread) and caused a real native crash (access violation inside
        // SteamAPI_ManualDispatch_GetNextCallback) when that loop's next tick called RunCallbacks() against
        // an already-shut-down dispatcher. This just signals the flag; the loop calls the real Shutdown()
        // itself, from the only place that's actually safe to — see RequestSteamShutdown()'s own doc comment.
        Debug.Log("[CharacterRegistry] OnApplicationQuit: requesting SteamAPI shutdown.");
        SteamLobbyProvider.RequestSteamShutdown();
    }
}