using UnityEngine;

public class PhantomBladeCarnivalAugment : MonoBehaviour
{
    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float bladeDamagePenalty = 0.8f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        PhantomBlade.OnLateSummon += OnSummon;
    }

    private void OnDestroy()
    {
        PhantomBlade.OnLateSummon -= OnSummon;
    }

    private void OnSummon(PhantomBlade blade)
    {
        if (blade.ownerStats != _stats) return;
        blade.damageMultiplier *= 1f - bladeDamagePenalty;
        blade.sizeMultiplier *= 1f - bladeDamagePenalty;
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
        secondary.size = blade.size;
        secondary.sizeMultiplier = blade.sizeMultiplier;
        secondary.damageMultiplier = blade.damageMultiplier;
        secondary.Init(_stats, blade.damage / _stats.UnpenalizedWeaponDamage);
    }
}
