using UnityEngine;

public class HealthAugment : MonoBehaviour
{
    [SerializeField] private float _healthBonus = 50f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.AddMaxHealthFlat(_healthBonus);
        Destroy(gameObject);
    }
}
