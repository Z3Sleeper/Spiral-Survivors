using System.Collections.Generic;
using PurrNet;

public static class GameStartData
{
    public static int LocalCharacterIndex;
    public static int LocalColorIndex;
    public static UnityEngine.Color LocalColor = UnityEngine.Color.white;
    public static string LocalUserId;
    public static string LobbySceneName = "PurrNet Lobby";
    public static string GameSceneName = "Purrdicted Game Scene";

    public static Dictionary<PlayerID, int> PlayerCharacterIndices = new();
    public static Dictionary<PlayerID, int> PlayerColorIndices = new();
}
