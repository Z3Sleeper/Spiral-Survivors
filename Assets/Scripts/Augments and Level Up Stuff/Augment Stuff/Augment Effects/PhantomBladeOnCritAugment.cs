using UnityEngine;

public class PhantomBladeOnCritAugment : MonoBehaviour
{
    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float damageStatMultiplier = 1f;
    [SerializeField] private float bonusCritChance = 0.1f;
    [SerializeField] private float bladeDamageBonus = 0.1f;
    [SerializeField] private int critsPerBlade = 3;

    private CharacterStats _stats;
    private int _critCount;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusCritChanceFlat += bonusCritChance;
        WeaponBase.OnCritStrike += OnCritStrike;
        PhantomBlade.OnSummon += OnSummon;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.bonusCritChanceFlat -= bonusCritChance;
        WeaponBase.OnCritStrike -= OnCritStrike;
        PhantomBlade.OnSummon -= OnSummon;
    }

    private void OnSummon(PhantomBlade blade)
    {
        if (blade.ownerStats != _stats) return;
        if (blade.isSecondary) return;
        blade.damage += blade.baseDamage * bladeDamageBonus;
        blade.size += 0.5f * bladeDamageBonus;
    }

    private void OnCritStrike(CharacterStats stats, EnemyBase enemy, float effectiveness)
    {
        if (stats != _stats) return;
        if (++_critCount < critsPerBlade) return;
        _critCount = 0;

        float spawnRadius = phantomBladePrefab.GetComponent<PhantomBlade>().spawnRadius;
        float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
        Vector3 spawnPos = new Vector3(
            enemy.transform.position.x + offset.x,
            0f,
            enemy.transform.position.z + offset.z);
        Vector3 forward = (enemy.transform.position - spawnPos).normalized;

        var blade = Instantiate(phantomBladePrefab, spawnPos, Quaternion.LookRotation(forward)).GetComponent<PhantomBlade>();
        blade.target = enemy;
        blade.Init(_stats, damageStatMultiplier, effectiveness);
    }
}
