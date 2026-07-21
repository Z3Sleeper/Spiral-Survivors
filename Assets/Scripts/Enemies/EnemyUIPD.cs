using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class EnemyUIPD : MonoBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private TextMeshProUGUI healthText;

    private IEnemyPD _enemy;
    private Camera _camera;

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
        healthBar.value = _enemy.CurrentHealth / _enemy.MaxHealth;
        healthText.text = $"<b>{FormatStat(_enemy.CurrentHealth)}/{FormatStat(_enemy.MaxHealth)}</b>";
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
