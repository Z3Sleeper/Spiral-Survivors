using UnityEngine;

public class GloomShieldHitbox : MonoBehaviour, IEnemyAttack
{
    private EnemyBase _source;
    private GloomShield _shield;

    private void Awake()
    {
        _source = GetComponentInParent<EnemyBase>();
        _shield = GetComponentInParent<GloomShield>();
    }

    public EnemyBase Source => _source;
    public float GetDamage() => _shield != null && _source != null ? _shield.explosionMultiplier * _source.damage : 0f;
}
