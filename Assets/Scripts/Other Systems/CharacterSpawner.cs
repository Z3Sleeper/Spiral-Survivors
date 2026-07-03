using PurrNet;

public class CharacterSpawner : NetworkIdentity
{
    public void SpawnCharacter(int index, PlayerData shell)
    {
        var prefab = CharacterRegistry.instance.GetPrefab(index);
        var character = Instantiate(prefab, shell.transform.position, shell.transform.rotation);
        character.GetComponent<NetworkIdentity>().GiveOwnership(shell.owner);
        var stats = character.GetComponent<CharacterStats>();
        AugmentManager.instance?.InitializePlayerPool(shell.owner.Value, stats.characterClass);
    }
}