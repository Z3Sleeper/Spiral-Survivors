using UnityEngine;

public class WeaponBow : WeaponBase
{
    [SerializeField] private GameObject arrowPrefab;
    [SerializeField] private LayerMask enemyHurtboxLayer;
    public float projectileSpeed = 20f;
    public float bowAttackSpeedScaling = 10f;
    [SerializeField] private float multishotSpreadAngle = 60f;
    [SerializeField] private float shotDelay = 0.05f;

    private float _fireTimer;
    private float _shotDelayTimer = -1f;
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
        if (_fireTimer > 0f) { _shotDelayTimer = -1f; return; }

        if (_stats.continuousFire || HasEnemyInLoS())
        {
            if (_shotDelayTimer < 0f)
                _shotDelayTimer = shotDelay / _stats.AttackSpeed;

            _shotDelayTimer -= Time.deltaTime;
            if (_shotDelayTimer > 0f) return;

            _shotDelayTimer = -1f;
            Fire();
            _fireTimer = 1f / (_stats.AttackSpeed * bowAttackSpeedScaling);
        }
        else
        {
            _shotDelayTimer = -1f;
        }
    }

    private bool HasEnemyInLoS()
    {
        var origin = transform.position - transform.forward * 1.0f;
        if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, _stats.WeaponRange + 1f, enemyHurtboxLayer))
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

        float rawMultishot = _stats.Multishot;
        int bonusShots = (int)rawMultishot;
        if (Random.value < rawMultishot - bonusShots) bonusShots++;
        int multishot = 1 + bonusShots;

        float startAngle = multishot > 1 ? -multishotSpreadAngle / 2f : 0f;
        float angleStep = multishot > 1 ? multishotSpreadAngle / (multishot - 1) : 0f;

        for (int i = 0; i < multishot; i++)
        {
            Vector3 dir = Quaternion.Euler(0, startAngle + i * angleStep, 0) * transform.forward;
            var arrow = Instantiate(arrowPrefab, transform.position, Quaternion.LookRotation(dir));
            arrow.transform.localScale *= _stats.WeaponSize;
            arrow.GetComponent<WeaponArrow>().Init(_stats.WeaponDamage, _stats.WeaponRange * 1.5f, dir, projectileSpeed, _stats.CritChance, _stats.CritDamage, pierce, ricochet, _stats.hitSound);
        }
    }
}