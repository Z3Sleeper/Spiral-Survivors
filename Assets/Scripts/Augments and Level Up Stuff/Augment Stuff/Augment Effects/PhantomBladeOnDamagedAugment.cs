using UnityEngine;

public class PhantomBladeOnDamagedAugment : MonoBehaviour
{
    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float damageStatMultiplier = 0.3f;
    [SerializeField] private int bladeCount = 3;
    [SerializeField] private float bonusHealth = 10f;
    [SerializeField] private float bladeDamageBonus = 0.1f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.AddMaxHealthFlatRpc(bonusHealth);
        PlayerHurtboxReceiver.OnDamaged += OnDamaged;
        PhantomBlade.OnSummon += OnSummon;
    }

    private void OnDestroy()
    {
        if (_stats != null && _stats.isSpawned) _stats.AddMaxHealthFlatRpc(-bonusHealth);
        PlayerHurtboxReceiver.OnDamaged -= OnDamaged;
        PhantomBlade.OnSummon -= OnSummon;
    }

    private void OnSummon(PhantomBlade blade)
    {
        if (blade.isSecondary) return;
        blade.damage += blade.baseDamage * bladeDamageBonus;
        blade.size += 0.5f * bladeDamageBonus;
    }

    private void OnDamaged(CharacterStats stats, EnemyBase enemy)
    {
        if (stats != _stats || enemy == null || enemy._isDead) return;

        float spawnRadius = phantomBladePrefab.GetComponent<PhantomBlade>().spawnRadius;
        for (int i = 0; i < bladeCount; i++)
        {
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
            Vector3 spawnPos = new Vector3(
                enemy.transform.position.x + offset.x,
                0f,
                enemy.transform.position.z + offset.z);
            Vector3 forward = (enemy.transform.position - spawnPos).normalized;

            var blade = Instantiate(phantomBladePrefab, spawnPos, Quaternion.LookRotation(forward)).GetComponent<PhantomBlade>();
            blade.target = enemy;
            blade.Init(_stats, damageStatMultiplier);
        }
    }
}
