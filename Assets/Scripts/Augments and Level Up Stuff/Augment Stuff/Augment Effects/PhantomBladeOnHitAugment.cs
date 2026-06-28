using UnityEngine;

public class PhantomBladeOnHitAugment : MonoBehaviour
{
    [SerializeField] private float bladeDamageBonus = 0.3f;
    [SerializeField] private float percentDamageOnHit = 0.1f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        PhantomBlade.OnSummon += OnSummon;
        PhantomBlade.OnBladeHit += OnBladeHit;
        WeaponBase.OnHit += OnHit;
    }

    private void OnDestroy()
    {
        PhantomBlade.OnSummon -= OnSummon;
        PhantomBlade.OnBladeHit -= OnBladeHit;
        WeaponBase.OnHit -= OnHit;
    }

    private void OnSummon(PhantomBlade blade)
    {
        if (blade.ownerStats != _stats) return;
        if (blade.isSecondary) return;
        blade.damage += blade.baseDamage * bladeDamageBonus;
        blade.size += 0.5f * bladeDamageBonus;
    }

    private void OnBladeHit(CharacterStats stats, EnemyBase enemy, float damageRatio)
    {
        if (stats != _stats) return;
        WeaponBase.RaiseOnHit(stats, enemy, damageRatio * 0.5f);
    }

    private void OnHit(CharacterStats stats, EnemyBase enemy, float effectiveness)
    {
        if (stats != _stats) return;
        enemy.TakeDamageRpc(_stats.WeaponDamage * percentDamageOnHit * _stats.OnHitEffectiveness * effectiveness);
    }
}
