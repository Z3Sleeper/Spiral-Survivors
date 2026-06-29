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

    private static string FormatStat(float value) => value switch
    {
        >= 1_000_000_000_000f => $"{value / 1_000_000_000_000f:F2}tril",
        >= 1_000_000_000f     => $"{value / 1_000_000_000f:F2}bil",
        >= 1_000_000f         => $"{value / 1_000_000f:F2}mil",
        >= 1_000f             => $"{value / 1_000f:F2}k",
        _                     => $"{value:G3}"
    };

    private void LateUpdate()
    {
        transform.rotation = _camera.transform.rotation;
    }
}