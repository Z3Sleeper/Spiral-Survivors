using UnityEngine;

public class DashHasteAugment : MonoBehaviour
{
    [SerializeField] private float _dashHasteBonus = 100f;
    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.dashHaste += _dashHasteBonus;
        }
        Destroy(gameObject);
    }
}
