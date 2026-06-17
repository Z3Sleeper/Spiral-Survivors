using UnityEngine;

public class DashHealAugment : MonoBehaviour
{
    [SerializeField] private float _healPercent = 0.2f;

    private CharacterStats _stats;

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        PlayerController.OnDashStarted += OnDash;
    }

    private void OnDestroy()
    {
        PlayerController.OnDashStarted -= OnDash;
    }

    private void OnDash(PlayerController controller)
    {
        _stats.HealRpc(_stats.MaxHealth * _healPercent);
    }
}
