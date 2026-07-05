using System.Collections;
using PurrNet;
using UnityEngine;

public class PlayerData : NetworkIdentity
{
    public SyncVar<int> characterIndex = new SyncVar<int>(0);
    public SyncVar<int> colorIndex = new SyncVar<int>(0);

    private bool _hasReported;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        Debug.Log($"[PlayerData.OnSpawned] isOwner={isOwner}, isServer={isServer}");

        if (isOwner)
            TryReport();
    }

    protected override void OnOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner, bool asServer)
    {
        base.OnOwnerChanged(oldOwner, newOwner, asServer);
        Debug.Log($"[PlayerData.OnOwnerChanged] asServer={asServer}, isOwner={isOwner}, newOwner={newOwner}");
        if (!asServer && isOwner)
            TryReport();
    }

    private void TryReport()
    {
        if (_hasReported) { Debug.Log("[PlayerData.TryReport] Already reported, skipping."); return; }
        _hasReported = true;
        Debug.Log($"[PlayerData.TryReport] index={GameStartData.LocalCharacterIndex}, color={GameStartData.LocalColorIndex}");
        ReportCharacterRpc(GameStartData.LocalCharacterIndex, GameStartData.LocalColorIndex);
    }

    [ServerRpc(requireOwnership: false)]
    private void ReportCharacterRpc(int index, int color)
    {
        Debug.Log($"[ReportCharacterRpc] Received on server, index={index}, GameManager.instance={GameManager.instance != null}, spawner={GameManager.instance?.characterSpawner != null}");
        characterIndex.value = index;
        colorIndex.value = color;
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
