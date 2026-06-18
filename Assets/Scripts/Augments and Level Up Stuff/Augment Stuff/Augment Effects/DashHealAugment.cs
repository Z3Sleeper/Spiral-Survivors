using UnityEngine;

public class DashHealAugment : MonoBehaviour
{
    [SerializeField] private float _healPercent = 0.2f;
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
    }
    
    private void Start()
    {
        _stats.dashHaste += _bonusDashHaste;
    }

    private void OnDestroy()
    {
        CharacterStats.OnVFXBroadcast -= OnVFX;
        PlayerController.OnDashEnded -= OnDash;
    }

    private void OnDash(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;
        foreach (var ally in CharacterStats.All)
        {
            if (ally.IsDead) continue;
            if (Vector3.Distance(_stats.transform.position, ally.transform.position) > _healRadius) continue;
            ally.ReceiveHealRpc(ally.MaxHealth * _healPercent);
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
        Destroy(vfx, duration);
    }
}
