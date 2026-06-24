using UnityEngine;

public class PhantomBladeCarnivalAugment : MonoBehaviour
{
    [SerializeField] private GameObject phantomBladePrefab;
    [Header("DANGER: Do not set this above 1")]
    [SerializeField] private float bladeDamagePenalty = 0.2f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        PhantomBlade.OnSummon += OnSummon;
    }

    private void OnDestroy()
    {
        PhantomBlade.OnSummon -= OnSummon;
    }

    private void OnSummon(PhantomBlade blade)
    {
        blade.damage -= blade.baseDamage * bladeDamagePenalty;

        if (blade.isSecondary || blade.target == null || blade.target._isDead) return;

        float spawnRadius = phantomBladePrefab.GetComponent<PhantomBlade>().spawnRadius;
        float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
        Vector3 spawnPos = new Vector3(
            blade.target.transform.position.x + offset.x,
            0f,
            blade.target.transform.position.z + offset.z);
        Vector3 forward = (blade.target.transform.position - spawnPos).normalized;

        var secondary = Instantiate(phantomBladePrefab, spawnPos, Quaternion.LookRotation(forward)).GetComponent<PhantomBlade>();
        secondary.target = blade.target;
        secondary.isSecondary = true;
        secondary.Init(_stats);
    }
}
