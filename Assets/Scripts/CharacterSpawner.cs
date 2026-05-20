using PurrNet;
using UnityEngine;

public class CharacterSpawner : MonoBehaviour
{
    public void SpawnCharacter(int index, PlayerData shell)
    {
        var prefab = CharacterRegistry.instance.GetPrefab(index);
        var character = Instantiate(prefab, shell.transform.position, shell.transform.rotation);
        character.GetComponent<NetworkIdentity>().GiveOwnership(shell.owner);
    }
}