using System.Collections;
using PurrNet;
using UnityEngine;

public class PlayerData : NetworkIdentity
{
    public SyncVar<int> characterIndex = new SyncVar<int>(-1);

    protected override void OnSpawned()
    {
        base.OnSpawned();
        Debug.Log($"[PlayerData.OnSpawned] isOwner={isOwner}, LocalCharacterIndex={GameStartData.LocalCharacterIndex}, LocalUserId={GameStartData.LocalUserId}");
        if (isOwner)
            ReportCharacterRpc(GameStartData.LocalCharacterIndex);
    }

    [ServerRpc]
    private void ReportCharacterRpc(int index)
    {
        Debug.Log($"[ReportCharacterRpc] Received on server, index={index}, GameManager.instance={GameManager.instance != null}, spawner={GameManager.instance?.characterSpawner != null}");
        characterIndex.value = index;
        GameManager.instance.characterSpawner.SpawnCharacter(index, this);
        StartCoroutine(NotifyLoaded());
    }

    private IEnumerator NotifyLoaded()
    {
        yield return new WaitForSeconds(0.5f);
        Debug.Log($"[NotifyLoaded] Calling PlayerLoaded. GameManager.instance={GameManager.instance != null}");
        GameManager.instance?.PlayerLoaded();
    }
}
