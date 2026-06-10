using System.Collections.Generic;
using PurrLobby;
using TMPro;
using UnityEngine;

public class CharacterSelectUI : MonoBehaviour
{
    public static CharacterSelectUI instance;

    [SerializeField] private TextMeshProUGUI playerListText;
    [SerializeField] private LobbyManager lobbyManager;
    [SerializeField] private CodeButton codeButton;

    private readonly Dictionary<string, int> _charSelections = new();

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    public void SetLocalDefault(string userId, int index)
    {
        if (!string.IsNullOrEmpty(userId))
            _charSelections[userId] = index;
    }

    public void RefreshFromLobby(Lobby lobby)
    {
        if (playerListText == null || CharacterRegistry.instance == null) return;
        codeButton?.Init(lobby.LobbyId);

        if (lobby.Properties != null)
        {
            foreach (var member in lobby.Members)
            {
                if (lobby.Properties.TryGetValue($"char_{member.Id}", out string val)
                    && int.TryParse(val, out int idx))
                    _charSelections[member.Id] = idx;
            }
        }

        var sb = new System.Text.StringBuilder("Player List:\n");
        foreach (var member in lobby.Members)
        {
            string charName = "...";
            if (_charSelections.TryGetValue(member.Id, out int charIdx))
                charName = CharacterRegistry.instance.GetPrefab(charIdx).name;

            string readyState = member.IsReady ? "Ready" : "Not Ready";
            sb.AppendLine($"{readyState} - {member.DisplayName} - {charName}");
        }
        playerListText.text = sb.ToString();
    }

    public void OnKnightClicked()      => SelectCharacter(0);
    public void OnRogueClicked()       => SelectCharacter(1);
    public void OnArcherClicked()      => SelectCharacter(2);
    public void OnBladeweaverClicked() => SelectCharacter(3);

    public void OnRandomClicked()
    {
        SelectCharacter(UnityEngine.Random.Range(0, CharacterRegistry.instance.PrefabCount()));
    }

    public void OnReadyClicked()
    {
        lobbyManager?.ToggleLocalReady();
    }

    private void SelectCharacter(int index)
    {
        GameStartData.LocalCharacterIndex = index;

        if (!string.IsNullOrEmpty(GameStartData.LocalUserId))
        {
            _charSelections[GameStartData.LocalUserId] = index;
            lobbyManager?.SetLobbyData($"char_{GameStartData.LocalUserId}", index.ToString());
        }

        if (lobbyManager == null) return;
        var lobby = lobbyManager.CurrentLobby;
        if (lobby.IsValid)
            RefreshFromLobby(lobby);
    }
}
