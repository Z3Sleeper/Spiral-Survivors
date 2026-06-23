using UnityEngine;

public class PhantomBladeOnCritAugment : MonoBehaviour
{
    [SerializeField] private PhantomBlade phantomBladePrefab;
    private const float SpawnRadius = 3f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        WeaponBase.OnCritStrike += OnCritStrike;
    }

    private void OnDestroy()
    {
        WeaponBase.OnCritStrike -= OnCritStrike;
    }

    private void OnCritStrike(CharacterStats stats, EnemyBase enemy, float effectiveness)
    {
        if (stats != _stats) return;

        float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SpawnRadius;
        Vector3 spawnPos = new Vector3(
            enemy.transform.position.x + offset.x,
            0f,
            enemy.transform.position.z + offset.z);
        Vector3 forward = (enemy.transform.position - spawnPos).normalized;

        var blade = Instantiate(phantomBladePrefab, spawnPos, Quaternion.LookRotation(forward));
        blade.Init(_stats, effectiveness);
    }
}
