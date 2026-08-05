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
}
