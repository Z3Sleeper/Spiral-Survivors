using UnityEngine;

public class StackPowerAugment : MonoBehaviour
{
    [SerializeField] private float stackPowerBonus = 0.5f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.stackPowerPercent += stackPowerBonus;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.stackPowerPercent -= stackPowerBonus;
    }
}
