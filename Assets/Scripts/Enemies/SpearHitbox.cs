using UnityEngine;

public class SpearHitbox : MonoBehaviour
{
    private SpearEnemy _spear;

    private void Awake()
    {
        _spear = GetComponentInParent<SpearEnemy>();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!_spear.isServer) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("PlayerHurtbox")) return;
        var player = other.GetComponentInParent<CharacterStats>();
        if (player == null) return;
        player.TakeDamageRpc(_spear.baseDamage * _spear.stabDamageMultiplier);
    }
}