using UnityEngine;

public class DashHealAugment : MonoBehaviour
{
    [SerializeField] private float _baseHealMultiplier = 10.00f;
    [SerializeField] private float _variancePercent = 0.25f;
    [SerializeField] private float _healRadius = 10f;
    [SerializeField] private float _bonusDashHaste = 30f;
    [SerializeField] private GameObject _vfxPrefab;
    [SerializeField] private float _vfxDuration = 0.3f;

    private const string VFXTag = "DashHeal";
    private CharacterStats _stats;

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null) { Destroy(gameObject); return; }

        CharacterStats.OnVFXBroadcast += OnVFX;

        if (!_stats.isOwner) return;
        PlayerController.OnDashEnded += OnDash;
        _stats.dashHaste += _bonusDashHaste;
    }

    private void OnDestroy()
    {
        CharacterStats.OnVFXBroadcast -= OnVFX;
        PlayerController.OnDashEnded -= OnDash;
        if (_stats != null && _stats.isOwner)
            _stats.dashHaste -= _bonusDashHaste;
    }

    private void OnDash(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;
        foreach (var ally in CharacterStats.All.Values)
        {
            if (ally.IsDead) continue;
            if (Vector3.Distance(_stats.transform.position, ally.transform.position) > _healRadius) continue;
            float max = _baseHealMultiplier * (1f + _variancePercent);
            float min = Mathf.Lerp(_baseHealMultiplier * (1f - _variancePercent), max, _stats.LuckChance);
            float healAmount = _stats.HealthRegen * Random.Range(min, max);
            ally.ReceiveHealRpc(healAmount);
        }
        _stats.BroadcastVFXRpc(
            new Vector3(_stats.transform.position.x, 0f, _stats.transform.position.z),
            _healRadius * 2f,
            _vfxDuration,
            VFXTag);
    }

    private void OnVFX(CharacterStats source, Vector3 position, float size, float duration, string vfxTag)
    {
        if (source != _stats || vfxTag != VFXTag || _vfxPrefab == null) return;
        var vfx = Instantiate(_vfxPrefab, position, Quaternion.identity);
        vfx.transform.localScale = new(size, vfx.transform.localScale.y, size);
        StartCoroutine(DestroyAfterDelay(vfx, duration));
    }

    private System.Collections.IEnumerator DestroyAfterDelay(GameObject obj, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (obj != null) Destroy(obj);
    }
}
