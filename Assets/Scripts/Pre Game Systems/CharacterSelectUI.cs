using System.Collections.Generic;
using PurrLobby;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CharacterSelectUI : MonoBehaviour
{
    public static CharacterSelectUI instance;

    [SerializeField] private TextMeshProUGUI playerListText;
    [SerializeField] private LobbyManager lobbyManager;
    [SerializeField] private CodeButton codeButton;
    [SerializeField] private PlayerColorPalette colorPalette;
    [SerializeField] private Transform colorPickerContainer;
    [SerializeField] private GameObject colorSwatchPrefab;

    private readonly Dictionary<string, int> _charSelections = new();
    private readonly Dictionary<string, int> _colorSelections = new();
    private readonly Dictionary<string, long> _powerLevels = new();
    private bool _isRandom;
    private bool _localIsReady;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        BuildColorPicker();
    }

    private void BuildColorPicker()
    {
        if (colorPickerContainer == null || colorSwatchPrefab == null || colorPalette == null) return;

        foreach (Transform child in colorPickerContainer)
            Destroy(child.gameObject);

        for (int i = 0; i < colorPalette.Count; i++)
        {
            int index = i;
            var obj = Instantiate(colorSwatchPrefab, colorPickerContainer);
            obj.GetComponent<Image>().color = colorPalette.Get(index);
            obj.GetComponent<Button>().onClick.AddListener(() => SelectColor(index));
        }
    }

    public void SetLocalDefault(string userId, int charIndex, int colorIndex)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            _charSelections[userId] = charIndex;
            _colorSelections[userId] = colorIndex;
        }
    }

    public UnityEngine.Color GetColor(int index) => colorPalette != null ? colorPalette.Get(index) : UnityEngine.Color.white;

    public int AssignUniqueColor(Lobby lobby)
    {
        if (colorPalette == null || colorPalette.Count == 0) return 0;

        var taken = new System.Collections.Generic.HashSet<int>();
        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
        {
            var lobbyCSteamID = new CSteamID(lobbyIdRaw);
            foreach (var member in lobby.Members)
            {
                if (member.Id == GameStartData.LocalUserId) continue;
                if (!ulong.TryParse(member.Id, out ulong memberIdRaw)) continue;
                string val = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, new CSteamID(memberIdRaw), "color");
                if (int.TryParse(val, out int idx)) taken.Add(idx);
            }
        }

        for (int i = 0; i < colorPalette.Count; i++)
            if (!taken.Contains(i)) return i;

        return 0;
    }

    public void RefreshFromLobby(Lobby lobby)
    {
        if (playerListText == null || CharacterRegistry.instance == null) return;
        codeButton?.Init(lobby.LobbyId);

        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
        {
            var lobbyCSteamID = new CSteamID(lobbyIdRaw);
            foreach (var member in lobby.Members)
            {
                if (member.Id == GameStartData.LocalUserId) continue;
                if (!ulong.TryParse(member.Id, out ulong memberIdRaw)) continue;
                var memberSteamID = new CSteamID(memberIdRaw);

                string charVal = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, memberSteamID, "char");
                if (int.TryParse(charVal, out int charIdx))
                    _charSelections[member.Id] = charIdx;

                string colorVal = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, memberSteamID, "color");
                if (int.TryParse(colorVal, out int colorIdx))
                    _colorSelections[member.Id] = colorIdx;

                string powerVal = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, memberSteamID, "power");
                if (long.TryParse(powerVal, out long power))
                    _powerLevels[member.Id] = power;
            }
        }

        var sb = new System.Text.StringBuilder("Player List:\n");
        foreach (var member in lobby.Members)
        {
            string charName = "...";
            int colorIndex = 0;

            if (member.Id == GameStartData.LocalUserId)
            {
                charName = _isRandom ? "Random" : CharacterRegistry.instance.GetPredictedPrefab(GameStartData.LocalCharacterIndex).name;
                colorIndex = GameStartData.LocalColorIndex;
            }
            else
            {
                if (_charSelections.TryGetValue(member.Id, out int charIdx))
                    charName = charIdx == -1 ? "Random" : CharacterRegistry.instance.GetPredictedPrefab(charIdx).name;
                if (_colorSelections.TryGetValue(member.Id, out int cIdx))
                    colorIndex = cIdx;
            }

            string colorHex = colorPalette != null && colorPalette.Count > 0
                ? ColorUtility.ToHtmlStringRGB(colorPalette.Get(colorIndex))
                : "FFFFFF";

            bool isReady = member.Id == GameStartData.LocalUserId ? _localIsReady : member.IsReady;
            string readyState = isReady ? "Ready" : "Not Ready";
            long power = member.Id == GameStartData.LocalUserId
                ? ProgressionManager.SpentPoints
                : (_powerLevels.TryGetValue(member.Id, out long p) ? p : 0L);
            string powerStr = ProgressionManager.FormatPoints(power);
            sb.AppendLine($"<color=#{colorHex}>■</color> {readyState} - {member.DisplayName} - {charName} - Spent Points: {powerStr}");
        }
        playerListText.text = sb.ToString();
    }

    public void OnKnightClicked()       => SelectCharacter(0);
    public void OnRogueClicked()        => SelectCharacter(1);
    public void OnArcherClicked()       => SelectCharacter(2);
    public void OnBladeweaverClicked()  => SelectCharacter(3);
    public void OnExecutionerClicked()  => SelectCharacter(4);

    public void OnRandomClicked()
    {
        _isRandom = true;
        GameStartData.LocalCharacterIndex = UnityEngine.Random.Range(0, CharacterRegistry.instance.PrefabCount());
        _charSelections[GameStartData.LocalUserId] = -1;

        if (lobbyManager != null)
        {
            var lobby = lobbyManager.CurrentLobby;
            if (lobby.IsValid && ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
                SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "char", "-1");
            RefreshFromLobby(lobby);
        }
    }

    public void OnReadyClicked()
    {
        if (lobbyManager == null) return;
        _localIsReady = !_localIsReady;
        // Explicit value, not ToggleLocalReady() — that toggles based on the lobby provider's own mirrored
        // localLobbyUser.IsReady, a separate state source from _localIsReady above that can drift out of
        // sync with it, which was causing client/host ready state mismatches (unreadying on the client
        // could read as readying on the host, since the two toggles didn't necessarily agree on the
        // starting value). Sending the exact bool we just computed removes that ambiguity entirely.
        lobbyManager.SetIsReady(GameStartData.LocalUserId, _localIsReady);
        RefreshFromLobby(lobbyManager.CurrentLobby);

        if (_localIsReady)
        {
            var lobby = lobbyManager.CurrentLobby;
            bool allOthersReady = true;
            foreach (var m in lobby.Members)
            {
                if (m.Id == GameStartData.LocalUserId) continue;
                if (!m.IsReady) { allOthersReady = false; break; }
            }
            if (allOthersReady && LoadingScreen.instance != null)
                LoadingScreen.instance.Show();
        }
    }

    // Renamed from ResetReadyState — that only ever cleared _localIsReady, but auditing turned up two more
    // local, lobby-specific fields with the exact same "never reset on leave" gap: _isRandom (so picking
    // Random in one lobby wouldn't silently keep showing "Random" as your char name in a later lobby even
    // after GameStartData.LocalCharacterIndex got reset back to 0 by InitializeForLobby), and the cached
    // OTHER-members' selections (_charSelections/_colorSelections/_powerLevels — keyed by userId, so a
    // player who hops between lobbies with you could otherwise briefly show stale data from the previous
    // lobby until the next RefreshFromLobby overwrites it). Called from both LobbySceneController.OnRoomLeft
    // and InitializeForLobby, so it's covered whichever path a lobby transition takes.
    public void ResetLocalLobbyState()
    {
        _localIsReady = false;
        _isRandom = false;
        _charSelections.Clear();
        _colorSelections.Clear();
        _powerLevels.Clear();
    }

    private void SelectColor(int index)
    {
        if (lobbyManager != null)
        {
            var lobby = lobbyManager.CurrentLobby;
            if (lobby.IsValid && ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            {
                var lobbyCSteamID = new CSteamID(lobbyIdRaw);
                foreach (var member in lobby.Members)
                {
                    if (member.Id == GameStartData.LocalUserId) continue;
                    if (!ulong.TryParse(member.Id, out ulong memberIdRaw)) continue;
                    string val = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, new CSteamID(memberIdRaw), "color");
                    if (int.TryParse(val, out int idx) && idx == index) return;
                }
                SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "color", index.ToString());
                GameStartData.LocalColorIndex = index;
                GameStartData.LocalColor = colorPalette.Get(index);
                if (!string.IsNullOrEmpty(GameStartData.LocalUserId))
                    _colorSelections[GameStartData.LocalUserId] = index;
                RefreshFromLobby(lobby);
                return;
            }
        }

        GameStartData.LocalColorIndex = index;
        if (!string.IsNullOrEmpty(GameStartData.LocalUserId))
            _colorSelections[GameStartData.LocalUserId] = index;
    }

    private void SelectCharacter(int index)
    {
        _isRandom = false;
        GameStartData.LocalCharacterIndex = index;
        _charSelections[GameStartData.LocalUserId] = index;

        if (lobbyManager == null) return;
        var lobby = lobbyManager.CurrentLobby;
        if (!lobby.IsValid) return;

        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "char", index.ToString());

        RefreshFromLobby(lobby);
    }
}
