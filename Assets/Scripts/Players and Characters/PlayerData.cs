using System.Collections;
using PurrNet;
using UnityEngine;

public class PlayerData : NetworkIdentity
{
    public SyncVar<int> characterIndex = new SyncVar<int>(0);
    public SyncVar<int> colorIndex = new SyncVar<int>(0);

    protected override void OnSpawned()
    {
        base.OnSpawned();
        Debug.Log($"[PlayerData.OnSpawned] isOwner={isOwner}, LocalCharacterIndex={GameStartData.LocalCharacterIndex}, LocalUserId={GameStartData.LocalUserId}");
        if (isOwner)
            ReportCharacterRpc(GameStartData.LocalCharacterIndex, GameStartData.LocalColorIndex);
    }

    [ServerRpc]
    private void ReportCharacterRpc(int index, int color)
    {
        Debug.Log($"[ReportCharacterRpc] Received on server, index={index}, GameManager.instance={GameManager.instance != null}, spawner={GameManager.instance?.characterSpawner != null}");
        characterIndex.value = index;
        colorIndex.value = color;
        GameManager.instance.characterSpawner.SpawnCharacter(index, color, this);
        StartCoroutine(NotifyLoaded());
    }

    private IEnumerator NotifyLoaded()
    {
        yield return new WaitForSeconds(0.5f);
        Debug.Log($"[NotifyLoaded] Calling PlayerLoaded. GameManager.instance={GameManager.instance != null}");
        GameManager.instance?.PlayerLoaded();
    }
}
