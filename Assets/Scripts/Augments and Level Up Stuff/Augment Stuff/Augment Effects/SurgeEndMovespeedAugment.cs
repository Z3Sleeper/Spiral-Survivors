using UnityEngine;

public class SurgeEndMovespeedAugment : MonoBehaviour
{
    [SerializeField] private float _bonusMoveSpeedFlat = 0.1f;
    [SerializeField] private float _bonusEnergyRegenFlat = 5f;
    [SerializeField] private float _moveSpeedBonus = 0.25f;
    [SerializeField] private float _duration = 1f;

    private CharacterStats _stats;
    private float _timer;
    private bool _active;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }

        _stats.bonusMoveSpeedFlat  += _bonusMoveSpeedFlat;
        _stats.bonusEnergyRegenFlat += _bonusEnergyRegenFlat;

        PlayerController.OnSurgeEnded   += OnSurgeEnded;
        PlayerController.OnSurgeStarted += OnSurgeStarted;
    }

    private void OnDestroy()
    {
        PlayerController.OnSurgeEnded   -= OnSurgeEnded;
        PlayerController.OnSurgeStarted -= OnSurgeStarted;
        if (_active) _stats.bonusMoveSpeedPercent -= _moveSpeedBonus;
    }

    private void Update()
    {
        if (!_active) return;
        _timer -= Time.deltaTime;
        if (_timer <= 0f) Deactivate();
    }

    private void OnSurgeEnded(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;
        if (_active) return;
        _active = true;
        _timer = _duration;
        _stats.bonusMoveSpeedPercent += _moveSpeedBonus;
    }

    private void OnSurgeStarted(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;
        if (_active) Deactivate();
    }

    private void Deactivate()
    {
        _active = false;
        _stats.bonusMoveSpeedPercent -= _moveSpeedBonus;
    }
}
