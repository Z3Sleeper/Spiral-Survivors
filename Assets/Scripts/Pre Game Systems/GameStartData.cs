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
