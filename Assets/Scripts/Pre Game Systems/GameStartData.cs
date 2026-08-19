using PurrLobby;
using Steamworks;
using UnityEngine;

public static class GameStartData
{
    public static int LocalCharacterIndex;
    public static int LocalColorIndex;
    public static UnityEngine.Color LocalColor = UnityEngine.Color.white;
    public static string LocalUserId;
    public static string LobbySceneName = "PurrNet Lobby";
    public static string GameSceneName = "Purrdicted Game Scene";

    // Host-only: the exact Steam-ID-to-color map CharacterSelectUI.ResolveColorAssignments resolved at the
    // moment the match locked in (LobbySceneController.OnAllReady). PlayerDataPD.SanitizeInput reads this
    // server-side to assign each player's real color authoritatively, instead of trusting the client's own
    // (possibly stale) GameStartData.LocalColorIndex. Never populated/read on a non-host machine. Cleared in
    // LobbySceneController.OnRoomLeft() so a later lobby session never inherits a previous one's map.
    public static System.Collections.Generic.Dictionary<string, int> HostColorAssignments = new();

    private const string SavedCharacterIndexKey = "LastCharacterIndex";
    private const string SavedColorIndexKey     = "LastColorIndex";

    // Persisted locally (PlayerPrefs — same "local per-machine preference" convention Settings.cs already
    // uses for volume/framerate/etc.) so a player's own character/color choice carries over automatically
    // the next time they enter a lobby, whether that's a fresh CreateLobby or returning here after a match
    // ends, instead of silently resetting to the first character/color every time. Written the instant the
    // player actually picks (CharacterSelectUI.SelectCharacter/SelectColor), read back in
    // LobbySceneController.InitializeForLobby. Color is only ever a REQUEST even once restored — the host
    // still arbitrates via ResolveColorAssignments, so a saved color already taken by someone with priority
    // (the host) still resolves to something else, same as any other color request.
    public static int SavedCharacterIndex
    {
        get => PlayerPrefs.GetInt(SavedCharacterIndexKey, 0);
        set => PlayerPrefs.SetInt(SavedCharacterIndexKey, value);
    }

    public static int SavedColorIndex
    {
        get => PlayerPrefs.GetInt(SavedColorIndexKey, 0);
        set => PlayerPrefs.SetInt(SavedColorIndexKey, value);
    }

    // Resolves the current Steam lobby via LobbyDataHolder — PurrLobby's own persistent, provider-agnostic
    // lobby tracker (DontDestroyOnLoad), the one thing that still reliably knows "which lobby am I in" from
    // OUTSIDE the Lobby scene, where the full LobbyManager doesn't exist at all. Shared by anything that
    // needs to touch Steam lobby data from the Game scene or at quit time (CharacterRegistry's quit-time
    // leave, ReturnToLobbyManager's pre-scene-load "Started" reset) instead of each duplicating the same
    // FindFirstObjectByType/parse dance.
    public static bool TryGetCurrentSteamLobbyId(out CSteamID lobbyId)
    {
        lobbyId = default;
        var holder = Object.FindFirstObjectByType<LobbyDataHolder>();
        if (holder == null || !holder.CurrentLobby.IsValid || !ulong.TryParse(holder.CurrentLobby.LobbyId, out ulong rawId))
            return false;

        lobbyId = new CSteamID(rawId);
        return true;
    }
}
