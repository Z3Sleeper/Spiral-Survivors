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
        healthText.text = $"<b>{(int)_enemy.CurrentHealth}/{(int)_enemy.maxHealth}</b>";
    }

    private void LateUpdate()
    {
        transform.forward = _camera.transform.forward;
    }
}