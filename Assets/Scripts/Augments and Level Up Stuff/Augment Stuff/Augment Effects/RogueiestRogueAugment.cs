using UnityEngine;

public class RogueiestRogueAugment : MonoBehaviour
{
    [SerializeField] private float _conversionBonus = 0.5f;

    private CharacterStats _stats;
    private RogueClassPassive _passive;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _passive = GetComponentInParent<RogueClassPassive>();
        if (_passive != null) _passive.conversionMultiplier += _conversionBonus;
    }

    private void OnDestroy()
    {
        if (_passive != null) _passive.conversionMultiplier -= _conversionBonus;
    }
}
