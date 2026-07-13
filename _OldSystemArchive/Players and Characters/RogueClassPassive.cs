using UnityEngine;

public class RogueClassPassive : MonoBehaviour
{
    private const float ConversionRate = 1f;
    public float conversionMultiplier = 1f;

    private CharacterStats _stats;
    private float _lastBonus;

    public void Init(CharacterStats stats)
    {
        _stats = stats;
        _lastBonus = ComputeBonus();
        _stats.attackSpeedPercent += _lastBonus;
    }

    private void Update()
    {
        float newBonus = ComputeBonus();
        if (Mathf.Abs(newBonus - _lastBonus) > 0.0001f)
        {
            _stats.attackSpeedPercent += newBonus - _lastBonus;
            _lastBonus = newBonus;
        }
    }

    private void OnDestroy()
    {
        if (_stats == null || !_stats.isOwner || !_stats.isSpawned) return;
        _stats.attackSpeedPercent -= _lastBonus;
    }

    private float ComputeBonus() =>
        (_stats.baseMoveSpeed + _stats.bonusMoveSpeedFlat) * _stats.bonusMoveSpeedPercent * ConversionRate * conversionMultiplier * _stats.baseAttackSpeed;
}
