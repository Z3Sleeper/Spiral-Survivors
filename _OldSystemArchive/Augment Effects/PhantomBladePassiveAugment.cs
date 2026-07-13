using UnityEngine;

public class PhantomBladePassiveAugment : MonoBehaviour
{
    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float _baseDamageMultiplier = 0.50f;
    [SerializeField] private float _variancePercent = 0.20f;
    [SerializeField] private int targetCount = 3;
    [SerializeField] private float baseCooldown = 5f;
    [SerializeField] private float bladeDamageBonus = 0.1f;
    [SerializeField] private float attackSpeedBonus = 0.1f;

    private CharacterStats _stats;
    private float _timer;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.attackSpeedPercent += attackSpeedBonus * _stats.baseAttackSpeed;
        _timer = baseCooldown;
        PhantomBlade.OnSummon += OnSummon;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.attackSpeedPercent -= attackSpeedBonus * _stats.baseAttackSpeed;
        PhantomBlade.OnSummon -= OnSummon;
    }

    private void OnSummon(PhantomBlade blade)
    {
        if (blade.ownerStats != _stats) return;
        if (blade.isSecondary) return;
        blade.damage += blade.baseDamage * bladeDamageBonus;
        blade.size += 0.5f * bladeDamageBonus;
    }

    private void Update()
    {
        if (_stats.IsDead) return;
        _timer -= Time.deltaTime * (_stats.AttackSpeed / _stats.baseAttackSpeed);
        if (_timer > 0f) return;

        _timer = baseCooldown;

        var enemies = FindObjectsByType<EnemyBase>(FindObjectsSortMode.None);
        System.Array.Sort(enemies, (a, b) =>
            Vector3.Distance(_stats.transform.position, a.transform.position)
                .CompareTo(Vector3.Distance(_stats.transform.position, b.transform.position)));

        float spawnRadius = phantomBladePrefab.GetComponent<PhantomBlade>().spawnRadius;
        int spawned = 0;
        foreach (var enemy in enemies)
        {
            if (enemy._isDead) continue;
            if (spawned >= targetCount) break;

            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
            Vector3 spawnPos = new Vector3(
                enemy.transform.position.x + offset.x,
                0f,
                enemy.transform.position.z + offset.z);
            Vector3 forward = (enemy.transform.position - spawnPos).normalized;

            float max = _baseDamageMultiplier * (1f + _variancePercent);
            float min = Mathf.Lerp(_baseDamageMultiplier * (1f - _variancePercent), max, _stats.LuckChance);
            var blade = Instantiate(phantomBladePrefab, spawnPos, Quaternion.LookRotation(forward)).GetComponent<PhantomBlade>();
            blade.target = enemy;
            blade.Init(_stats, Random.Range(min, max));
            spawned++;
        }
    }
}
