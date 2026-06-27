using PurrNet;
using UnityEngine;

public class CharacterSpawner : NetworkIdentity
{
    [SerializeField] private PlayerColorPalette colorPalette;

    public void SpawnCharacter(int index, int colorIndex, PlayerData shell)
    {
        var prefab = CharacterRegistry.instance.GetPrefab(index);
        var character = Instantiate(prefab, shell.transform.position, shell.transform.rotation);
        character.GetComponent<NetworkIdentity>().GiveOwnership(shell.owner);
        var stats = character.GetComponent<CharacterStats>();
        AugmentManager.instance?.InitializePlayerPool(shell.owner.Value, stats.characterClass);

        if (colorPalette != null && colorPalette.Count > 0)
        {
            Color playerColor = colorPalette.Get(colorIndex);
            stats.playerColor = playerColor;
            foreach (var applicator in character.GetComponentsInChildren<PlayerColorApplicator>(true))
                applicator.Apply(playerColor);
        }
    }
}