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
        healthBar.value = _enemy.CurrentHealth / _enemy.maxHealth;
        healthText.text = $"<b>{_enemy.CurrentHealth}/{_enemy.maxHealth}</b>";
    }

    private void LateUpdate()
    {
        transform.forward = _camera.transform.forward;
    }
}