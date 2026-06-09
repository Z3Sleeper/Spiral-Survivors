using System.Collections;
using PurrNet;
using UnityEngine;

public class PlayerData : NetworkIdentity
{
    public SyncVar<int> characterIndex = new SyncVar<int>(-1);

    protected override void OnSpawned()
    {
        base.OnSpawned();
        if (isOwner)
            ReportCharacterRpc(GameStartData.LocalCharacterIndex);
    }

    [ServerRpc]
    private void ReportCharacterRpc(int index)
    {
        characterIndex.value = index;
        GameManager.instance.characterSpawner.SpawnCharacter(index, this);
        StartCoroutine(NotifyLoaded());
    }

    private IEnumerator NotifyLoaded()
    {
        yield return new WaitForSeconds(0.5f);
        GameManager.instance?.PlayerLoaded();
    }
}
