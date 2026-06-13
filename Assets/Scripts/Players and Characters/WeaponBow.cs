using UnityEngine;

public class WeaponBow : WeaponBase
{
    [SerializeField] private GameObject arrowPrefab;
    [SerializeField] private LayerMask enemyHurtboxLayer;
    public float projectileSpeed = 20f;
    public float bowAttackSpeedScaling = 10f;

    private float _fireTimer;
    public override bool IsMelee => false;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
    }

    public override void Init(CharacterStats stats)
    {
        base.Init(stats);
        _fireTimer = 0f;
    }

    private void Update()
    {
        if (_stats == null || _stats.IsDead) return;

        _fireTimer -= Time.deltaTime;
        if (_fireTimer > 0f) return;

        if (_stats.continuousFire || HasEnemyInLoS())
        {
            Fire();
            _fireTimer = 1f / (_stats.AttackSpeed * bowAttackSpeedScaling);
        }
    }

    private bool HasEnemyInLoS()
    {
        var origin = transform.position - transform.forward * 0.5f;
        if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, _stats.WeaponRange + 0.5f, enemyHurtboxLayer))
            return hit.collider.GetComponentInParent<EnemyBase>() != null;
        return false;
    }

    private void Fire()
    {
        float rawPierce = _stats.Pierce;
        int pierce = (int)rawPierce;
        if (Random.value < rawPierce - pierce) pierce++;

        float rawRicochet = _stats.Ricochet;
        int ricochet = (int)rawRicochet;
        if (Random.value < rawRicochet - ricochet) ricochet++;

        var arrow = Instantiate(arrowPrefab, transform.position, transform.rotation);
        var arrowScript = arrow.GetComponent<WeaponArrow>();
        arrowScript.Init(_stats.WeaponDamage, _stats.WeaponRange * 1.5f, transform.forward, projectileSpeed, _stats.CritChance, _stats.CritDamage, pierce, ricochet, _stats.hitSound);
    }
}