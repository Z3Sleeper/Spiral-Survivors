using PurrNet;
using TMPro;
using UnityEngine;

public class CharacterSelectUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI playerListText;

    public void OnKnightClicked()
    {
        if (CharacterSelectManager.instance == null || !CharacterSelectManager.instance.isSpawned) return;
        CharacterSelectManager.instance.SelectCharacter(0, NetworkManager.main.localPlayer);
    }

    public void OnRogueClicked()
    {
        if (CharacterSelectManager.instance == null || !CharacterSelectManager.instance.isSpawned) return;
        CharacterSelectManager.instance.SelectCharacter(1, NetworkManager.main.localPlayer);
    }

    public void OnArcherClicked()
    {
        if (CharacterSelectManager.instance == null || !CharacterSelectManager.instance.isSpawned) return;
        CharacterSelectManager.instance.SelectCharacter(2, NetworkManager.main.localPlayer);
    }

    public void OnBladeweaverClicked()
    {
        if (CharacterSelectManager.instance == null || !CharacterSelectManager.instance.isSpawned) return;
        CharacterSelectManager.instance.SelectCharacter(3, NetworkManager.main.localPlayer);
    }

    public void OnRandomClicked()
    {
        if (CharacterSelectManager.instance == null || !CharacterSelectManager.instance.isSpawned) return;
        CharacterSelectManager.instance.SelectRandom(NetworkManager.main.localPlayer);
    }

    public void OnReadyClicked()
    {
        if (CharacterSelectManager.instance == null || !CharacterSelectManager.instance.isSpawned) return;
        CharacterSelectManager.instance.ReadyUp(NetworkManager.main.localPlayer);
    }

    private void Update()
    {
        UpdatePlayerList();
    }

    private void UpdatePlayerList()
    {
        if (CharacterSelectManager.instance == null) return;
        if (CharacterRegistry.instance == null) return;

        var sb = new System.Text.StringBuilder("Player List:\n");
        foreach (var kvp in CharacterSelectManager.instance.playerCharacterChoices)
        {
            var characterName = CharacterRegistry.instance.GetPrefab(kvp.Value).name;
            var readyState = CharacterSelectManager.instance._readyStates.TryGetValue(kvp.Key, out bool ready) && ready 
                ? "Ready" 
                : "Not Ready";
            sb.AppendLine($"{readyState} - Player {kvp.Key} - {characterName}");
        }
        sb.AppendLine($"isServer: {CharacterSelectManager.instance.isServer}");
        sb.AppendLine($"players: {NetworkManager.main?.playerCount ?? -1}");
        playerListText.text = sb.ToString();
    }
}