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
            }
        }

        var sb = new System.Text.StringBuilder("Player List:\n");
        foreach (var member in lobby.Members)
        {
            string charName = "...";
            int colorIndex = 0;

            if (member.Id == GameStartData.LocalUserId)
            {
                charName = CharacterRegistry.instance.GetPrefab(GameStartData.LocalCharacterIndex).name;
                colorIndex = GameStartData.LocalColorIndex;
            }
            else
            {
                if (_charSelections.TryGetValue(member.Id, out int charIdx))
                    charName = CharacterRegistry.instance.GetPrefab(charIdx).name;
                if (_colorSelections.TryGetValue(member.Id, out int cIdx))
                    colorIndex = cIdx;
            }

            string colorHex = colorPalette != null && colorPalette.Count > 0
                ? ColorUtility.ToHtmlStringRGB(colorPalette.Get(colorIndex))
                : "FFFFFF";

            string readyState = member.IsReady ? "Ready" : "Not Ready";
            sb.AppendLine($"<color=#{colorHex}>■</color> {readyState} - {member.DisplayName} - {charName}");
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
        SelectCharacter(UnityEngine.Random.Range(0, CharacterRegistry.instance.PrefabCount()));
    }

    public void OnReadyClicked()
    {
        lobbyManager?.ToggleLocalReady();
    }

    private void SelectColor(int index)
    {
        GameStartData.LocalColorIndex = index;
        if (!string.IsNullOrEmpty(GameStartData.LocalUserId))
            _colorSelections[GameStartData.LocalUserId] = index;

        if (lobbyManager == null) return;
        var lobby = lobbyManager.CurrentLobby;
        if (!lobby.IsValid) return;

        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "color", index.ToString());

        RefreshFromLobby(lobby);
    }

    private void SelectCharacter(int index)
    {
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
