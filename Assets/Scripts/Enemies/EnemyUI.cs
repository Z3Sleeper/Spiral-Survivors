using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class EnemyUI : MonoBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private TextMeshProUGUI healthText;
    private EnemyBase _enemy;
    private Camera _camera;

    private void Awake()
    {
        _enemy = GetComponentInParent<EnemyBase>();
        _camera = Camera.main;
    }

    private void Update()
    {
        if (_enemy._isDead)
        {
            healthBar.gameObject.SetActive(false);
            healthText.gameObject.SetActive(false);
            return;
        }
        healthBar.value = _enemy.CurrentHealth / _enemy.maxHealth;
        healthText.text = $"<b>{FormatStat(_enemy.CurrentHealth)}/{FormatStat(_enemy.maxHealth)}</b>";
    }

    private static string FormatStat(float value)
    {
        if (value >= 1_000_000_000_000f) return $"{value / 1_000_000_000_000f:F2}tril";
        if (value >= 1_000_000_000f)     return $"{value / 1_000_000_000f:F2}bil";
        if (value >= 1_000_000f)         return $"{value / 1_000_000f:F2}mil";
        if (value >= 1_000f)             return $"{value / 1_000f:F2}k";
        return $"{Mathf.RoundToInt(value)}";
    }

    private void LateUpdate()
    {
        if (_camera == null) _camera = Camera.main;
        if (_camera != null) transform.rotation = _camera.transform.rotation;
    }
}