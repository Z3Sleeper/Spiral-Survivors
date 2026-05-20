using PurrNet;
using TMPro;
using UnityEngine;

public class CharacterSelectUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI playerListText;

    public void OnKnightClicked()
    {
        CharacterSelectManager.instance.SelectCharacter(0, NetworkManager.main.localPlayer);
    }

    public void OnRogueClicked()
    {
        CharacterSelectManager.instance.SelectCharacter(1, NetworkManager.main.localPlayer);
    }

    public void OnRandomClicked()
    {
        CharacterSelectManager.instance.SelectRandom(NetworkManager.main.localPlayer);
    }

    public void OnReadyClicked()
    {
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
        playerListText.text = sb.ToString();
    }
}