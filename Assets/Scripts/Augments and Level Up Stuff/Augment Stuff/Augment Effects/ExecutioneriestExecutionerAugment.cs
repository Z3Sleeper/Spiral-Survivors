using UnityEngine;

public class ExecutioneriestExecutionerAugment : MonoBehaviour
{
    [SerializeField] private float _bonusCritChancePercent = 0.25f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.bonusCritChancePercent += _bonusCritChancePercent;
        Destroy(gameObject);
    }
}
