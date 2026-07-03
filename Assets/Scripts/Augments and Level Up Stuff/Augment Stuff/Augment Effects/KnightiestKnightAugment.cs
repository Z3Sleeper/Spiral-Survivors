using UnityEngine;

public class KnightiestKnightAugment : MonoBehaviour
{
    [SerializeField] private float _bonusMaxHealthPercent = 0.25f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.AddMaxHealthPercent(_bonusMaxHealthPercent);
        Destroy(gameObject);
    }
}
