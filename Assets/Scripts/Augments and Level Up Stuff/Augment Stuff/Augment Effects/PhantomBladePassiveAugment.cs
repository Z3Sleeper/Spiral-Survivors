using UnityEngine;

public class PhantomBladePassiveAugment : MonoBehaviour
{
    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float damageStatMultiplier = 0.3f;
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
        _stats.bonusWeaponSpeedFlat += attackSpeedBonus * _stats.baseWeaponSpeed;
        _timer = baseCooldown;
        PhantomBlade.OnSummon += OnSummon;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.bonusWeaponSpeedFlat -= attackSpeedBonus * _stats.baseWeaponSpeed;
        PhantomBlade.OnSummon -= OnSummon;
    }

    private void OnSummon(PhantomBlade blade)
    {
        if (blade.isSecondary) return;
        blade.damage += blade.baseDamage * bladeDamageBonus;
        blade.size += 0.5f * bladeDamageBonus;
    }

    private void Update()
    {
        _timer -= Time.deltaTime * (_stats.WeaponSpeed / _stats.baseWeaponSpeed);
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

            var blade = Instantiate(phantomBladePrefab, spawnPos, Quaternion.LookRotation(forward)).GetComponent<PhantomBlade>();
            blade.target = enemy;
            blade.Init(_stats, damageStatMultiplier);
            spawned++;
        }
    }
}
