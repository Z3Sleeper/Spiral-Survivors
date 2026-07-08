using PurrNet;
using UnityEngine;

public class PlayerData : NetworkIdentity
{
    private bool _hasSpawned;

    protected override void OnSpawned(bool asServer)
    {
        base.OnSpawned(asServer);
        if (!asServer && isOwner)
            SpawnCharacterRpc(GameStartData.LocalCharacterIndex);
    }

    [ServerRpc(requireOwnership: true)]
    private void SpawnCharacterRpc(int charIndex)
    {
        if (_hasSpawned) return;
        _hasSpawned = true;

        if (charIndex < 0 || charIndex >= CharacterRegistry.instance.PrefabCount())
        {
            Debug.LogError($"[PlayerData] Invalid charIndex {charIndex} — defaulting to 0");
            charIndex = 0;
        }

        var character = Instantiate(CharacterRegistry.instance.GetPrefab(charIndex), transform.position, transform.rotation);
        if (character.TryGetComponent(out NetworkIdentity identity))
            identity.GiveOwnership(owner.Value);
    }
}
