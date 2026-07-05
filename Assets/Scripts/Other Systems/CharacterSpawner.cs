using PurrNet;
using UnityEngine;

public class CharacterSpawner : NetworkIdentity
{
    public void SpawnCharacter(int index, PlayerData shell)
    {
        var prefab = CharacterRegistry.instance.GetPrefab(index);
        Debug.Log($"[CharacterSpawner.SpawnCharacter] index={index}, prefab={prefab?.name}, shell.owner={shell.owner}");
        var character = Instantiate(prefab, shell.transform.position, shell.transform.rotation);
        character.GetComponent<NetworkIdentity>().GiveOwnership(shell.owner);
        var stats = character.GetComponent<CharacterStats>();
        AugmentManager.instance?.InitializePlayerPool(shell.owner.Value, stats.characterClass);
    }
}