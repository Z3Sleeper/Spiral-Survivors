using PurrLobby;
using TMPro;
using UnityEngine;

public class CharacterSelectUI : MonoBehaviour
{
    public static CharacterSelectUI instance;

    [SerializeField] private TextMeshProUGUI playerListText;
    [SerializeField] private LobbyManager lobbyManager;
    [SerializeField] private CodeButton codeButton;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    public void RefreshFromLobby(Lobby lobby)
    {
        if (playerListText == null || CharacterRegistry.instance == null) return;
        codeButton?.Init(lobby.LobbyId);

        var sb = new System.Text.StringBuilder("Player List:\n");
        foreach (var member in lobby.Members)
        {
            string charName = "...";
            if (lobby.Properties != null
                && lobby.Properties.TryGetValue($"char_{member.Id}", out string val)
                && int.TryParse(val, out int idx))
                charName = CharacterRegistry.instance.GetPrefab(idx).name;

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
        lobbyManager.ToggleLocalReady();
    }

    private void SelectCharacter(int index)
    {
        if (string.IsNullOrEmpty(GameStartData.LocalUserId)) return;
        GameStartData.LocalCharacterIndex = index;
        lobbyManager.SetLobbyData($"char_{GameStartData.LocalUserId}", index.ToString());
    }
}
