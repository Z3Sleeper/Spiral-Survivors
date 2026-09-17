using PurrLobby.Providers;
using Steamworks;
using UnityEngine;

// DDOL, lobby-side holder for cross-scene game-configuration state that doesn't belong on CharacterRegistry
// (a character-PREFAB registry specifically, not a general-purpose settings bag) — named GamePresets since
// LobbyDataHolder (PurrLobby's own persistent lobby tracker) already owns that name. Barebones for now: just
// the difficulty selection and the application-quit logic migrated off CharacterRegistry (see
// OnApplicationQuit's own doc comment for why it has to live on a DDOL object at all).
public class GamePresets : MonoBehaviour
{
    public static GamePresets instance;

    public const int MinDifficulty = 1;
    public const int MaxDifficulty = 6;

    private const string SavedDifficultyKey = "LastDifficulty";

    // 1-6. Host-only control is enforced one layer up (CharacterSelectUI.IsHost gates the arrows themselves,
    // both visually and as a real logical guard) — this class doesn't know or care who's calling it. Still
    // missing cross-client sync (only the local machine's own value is used anywhere right now, per explicit
    // "no restrictions for now" scoping from 2026-08-20) — that's the one piece still deferred.
    [SerializeField] private int difficulty = 1;
    public int Difficulty => difficulty;

    // Clamped to BOTH [MinDifficulty, MaxDifficulty] AND ProgressionManager.HighestUnlockedDifficulty — the
    // single authoritative gate, so nothing that calls this (however it gets called) can ever land on a
    // locked difficulty, not just the arrow buttons that currently happen to be the only caller. Also
    // persists to PlayerPrefs immediately, same "written the instant the player actually picks" convention
    // GameStartData.SavedCharacterIndex/SavedColorIndex already use, so this survives an app restart too.
    public void SetDifficulty(int value)
    {
        difficulty = Mathf.Clamp(value, MinDifficulty, Mathf.Min(MaxDifficulty, ProgressionManager.HighestUnlockedDifficulty));
        PlayerPrefs.SetInt(SavedDifficultyKey, difficulty);
    }

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);

        // Restored once here, not re-read on every lobby entry like SavedCharacterIndex/SavedColorIndex need
        // to be — this object is itself DontDestroyOnLoad, so the in-memory value already survives every
        // later "return to lobby" for the rest of this app session on its own; only a genuine app restart
        // needs the PlayerPrefs read at all. Re-clamped against the CURRENT unlock state (not just trusted
        // as-is) in case the save was ever hand-edited, or a future difficulty-relocking feature ever
        // shrinks HighestUnlockedDifficulty below what was last saved.
        int saved = PlayerPrefs.GetInt(SavedDifficultyKey, MinDifficulty);
        difficulty = Mathf.Clamp(saved, MinDifficulty, Mathf.Min(MaxDifficulty, ProgressionManager.HighestUnlockedDifficulty));
    }

    // Migrated from CharacterRegistry (2026-08-20) — that class is a character-prefab registry specifically,
    // this quit logic doesn't semantically belong there, it just needed SOME object guaranteed alive at quit
    // time regardless of which scene (lobby or game) the player was in — see EnemyBasePD.ResetActiveCounts()
    // for why this reset exists at all. Practically low-value (the process is ending anyway) but kept for
    // symmetry with the other exit paths.
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
    // orphaned lobby per mid-match test-and-stop cycle in one continued Editor session). Resolved via
    // GameStartData.TryGetCurrentSteamLobbyId (backed by LobbyDataHolder, PurrLobby's own genuinely-
    // persistent, provider-agnostic lobby-tracking object — see its own DontDestroyOnLoad), which survives
    // into the Game scene even though LobbyManager doesn't, rather than needing the full LobbyManager/
    // provider stack to exist. Harmless to also fire when LobbySceneController's own path already ran
    // (e.g. quitting from the Lobby scene itself) —
    // leaving an already-left/invalid lobby is a silent no-op on Steam's side.
    private void OnApplicationQuit()
    {
        EnemyBasePD.ResetActiveCounts();

        if (GameStartData.TryGetCurrentSteamLobbyId(out CSteamID lobbyId))
        {
            Debug.Log($"[GamePresets] OnApplicationQuit: leaving lobby {lobbyId} directly via Steamworks (scene-scoped LobbyManager may not exist right now).");
            SteamMatchmaking.LeaveLobby(lobbyId);
        }
        else
        {
            Debug.Log("[GamePresets] OnApplicationQuit: no valid lobby to leave.");
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
        // an already-shut-down dispatcher. RequestSteamShutdown() itself now calls the real, idempotent
        // Shutdown() synchronously right here (2026-08-12 fix — it used to only set a stop flag for the
        // async loop's own next Task.Delay continuation to notice, which the Editor frequently never resumed
        // after Stop) — see that method's own doc comment for the full reasoning.
        Debug.Log("[GamePresets] OnApplicationQuit: requesting SteamAPI shutdown.");
        SteamLobbyProvider.RequestSteamShutdown();
    }
}
