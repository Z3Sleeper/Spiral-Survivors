using UnityEngine;

public class LeaperLeapHitbox : MonoBehaviour
{
    private LeaperEnemy _leaperEnemy;

    private void Awake()
    {
        _leaperEnemy = GetComponentInParent<LeaperEnemy>();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!_leaperEnemy.isServer) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("PlayerHurtbox")) return;
        var player = other.GetComponentInParent<CharacterStats>();
        if (player == null || player.IsDead) return;
        player.TakeDamageRpc(_leaperEnemy.baseDamage);
    }
}