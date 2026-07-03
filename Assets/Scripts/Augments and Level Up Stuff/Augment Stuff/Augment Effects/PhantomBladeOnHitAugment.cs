using UnityEngine;

public class PhantomBladeOnHitAugment : MonoBehaviour
{
    [SerializeField] private float bladeDamageBonus = 0.3f;
    [SerializeField] private float _baseDamagePercent = 0.10f;
    [SerializeField] private float _variancePercent = 0.25f;

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

    private void OnHit(CharacterStats stats, EnemyBase enemy, float effectiveness, bool isCrit)
    {
        if (stats != _stats) return;
        float max = _baseDamagePercent * (1f + _variancePercent);
        float min = Mathf.Lerp(_baseDamagePercent * (1f - _variancePercent), max, _stats.LuckChance);
        enemy.TakeDamageRpc(_stats.WeaponDamage * Random.Range(min, max) * _stats.OnHitEffectiveness * effectiveness);
    }
}
