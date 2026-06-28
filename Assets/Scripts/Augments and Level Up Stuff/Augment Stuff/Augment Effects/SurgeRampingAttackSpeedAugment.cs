using UnityEngine;

public class SurgeRampingAttackSpeedAugment : MonoBehaviour
{
    [SerializeField] private float _rampPercent = 0.1f;
    [SerializeField] private float _rampInterval = 0.5f;

    private CharacterStats _stats;
    private float _timer;
    private float _accumulated;
    private bool _surging;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        PlayerController.OnSurgeStarted += OnSurgeStarted;
        PlayerController.OnSurgeEnded   += OnSurgeEnded;
    }

    private void OnDestroy()
    {
        PlayerController.OnSurgeStarted -= OnSurgeStarted;
        PlayerController.OnSurgeEnded   -= OnSurgeEnded;
        if (_accumulated > 0f && _stats != null)
            _stats.attackSpeedPercent -= _accumulated;
    }

    private void Update()
    {
        if (!_surging) return;
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = _rampInterval;
        float bonus = _rampPercent * _stats.baseAttackSpeed * _stats.StackPower;
        _stats.attackSpeedPercent += bonus;
        _accumulated += bonus;
    }

    private void OnSurgeStarted(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;
        _surging = true;
        _timer = _rampInterval;
    }

    private void OnSurgeEnded(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;
        _surging = false;
        _stats.attackSpeedPercent -= _accumulated;
        _accumulated = 0f;
    }
}
