using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class EnemyUIPD : MonoBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private TextMeshProUGUI healthText;

    private IEnemyPD _enemy;
    private Camera _camera;
    // Last values actually displayed — with up to ~500 concurrent enemies (see EnemySpawnerPD's own caps),
    // rebuilding healthText's string every frame regardless of whether health actually moved was a real,
    // chronic per-frame GC source (confirmed via Profiler: EnemyUIPD showing up as a top GC-allocation
    // contributor across several consecutive frames). Skipping the Slider/text write entirely when nothing
    // changed since last frame removes both the allocation and the redundant TMP rebuild for every enemy
    // that isn't currently taking damage, which is the overwhelming majority on any given frame.
    private float _lastCurrentHealth = -1f;
    private float _lastMaxHealth = -1f;

    private void Awake()
    {
        _enemy = GetComponentInParent<IEnemyPD>();
        _camera = Camera.main;
        if (healthBar != null) healthBar.value = 1f;
    }

    private void Update()
    {
        // No _isDead early-return — a kill-launch corpse stays visible (and its healthbar with it) for a
        // while after death, so the bar needs to keep updating to actually show 0/max instead of freezing
        // at whatever it last displayed the instant before the killing blow landed.
        if (_enemy == null) return;
        if (_enemy.MaxHealth <= 0f) return;

        float current = _enemy.CurrentHealth;
        float max = _enemy.MaxHealth;
        if (current == _lastCurrentHealth && max == _lastMaxHealth) return;
        _lastCurrentHealth = current;
        _lastMaxHealth = max;

        healthBar.value = current / max;
        healthText.text = $"<b>{FormatStat(current)}/{FormatStat(max)}</b>";
    }

    private void LateUpdate()
    {
        if (_camera == null) _camera = Camera.main;
        if (_camera != null) transform.rotation = _camera.transform.rotation;
    }

    private static string FormatStat(float value)
    {
        if (value >= 1_000_000_000_000f) return $"{value / 1_000_000_000_000f:F2}tril";
        if (value >= 1_000_000_000f)     return $"{value / 1_000_000_000f:F2}bil";
        if (value >= 1_000_000f)         return $"{value / 1_000_000f:F2}mil";
        if (value >= 1_000f)             return $"{value / 1_000f:F2}k";
        return $"{Mathf.RoundToInt(value)}";
    }
}
