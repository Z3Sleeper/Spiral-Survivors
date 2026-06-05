using UnityEngine;

public class WeaponBow : WeaponBase
{
    [SerializeField] private GameObject arrowPrefab;
    [SerializeField] private LayerMask enemyHurtboxLayer;
    public float projectileSpeed = 20f;

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
            _fireTimer = 1f / (_stats.AttackSpeed * 6);
        }
    }

    private bool HasEnemyInLoS()
    {
        if (Physics.Raycast(transform.position, transform.forward, out RaycastHit hit, _stats.WeaponRange, enemyHurtboxLayer))
            return hit.collider.GetComponentInParent<EnemyBase>() != null;
        return false;
    }

    private void Fire()
    {
        var arrow = Instantiate(arrowPrefab, transform.position, transform.rotation);
        var arrowScript = arrow.GetComponent<WeaponArrow>();
        arrowScript.Init(_stats.WeaponDamage, _stats.WeaponRange * 1.5f, transform.forward, projectileSpeed, _stats, _controller);
    }
}