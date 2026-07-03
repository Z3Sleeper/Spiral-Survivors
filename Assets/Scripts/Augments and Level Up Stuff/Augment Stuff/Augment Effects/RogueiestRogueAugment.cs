using UnityEngine;

public class RogueiestRogueAugment : MonoBehaviour
{
    [SerializeField] private float _conversionBonus = 0.5f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        var passive = GetComponentInParent<RogueClassPassive>();
        if (passive != null) passive.conversionMultiplier += _conversionBonus;
        Destroy(gameObject);
    }
}
