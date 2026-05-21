using System.Collections;
using PurrNet;
using UnityEngine;

public class PlayerData : NetworkIdentity
{
    public SyncVar<int> characterIndex = new SyncVar<int>(-1);

    protected override void OnSpawned()
    {
        base.OnSpawned();
        if (!isServer) return;

        int index = CharacterSelectManager.instance != null
            ? CharacterSelectManager.instance.playerCharacterChoices[owner.Value]
            : 0;

        characterIndex.value = index;
        GameManager.instance.characterSpawner.SpawnCharacter(index, this);
        
        StartCoroutine(NotifyLoaded());
    }

    private IEnumerator NotifyLoaded()
    {
        yield return new WaitForSeconds(0.5f); // change from yield return null to this
        CharacterSelectManager.instance?.PlayerLoaded(owner.Value);
    }
}