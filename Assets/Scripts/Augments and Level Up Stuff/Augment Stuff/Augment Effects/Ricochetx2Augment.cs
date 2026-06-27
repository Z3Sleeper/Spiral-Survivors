using UnityEngine;

public class Ricochetx2Augment : MonoBehaviour
{
    [SerializeField] private float _dropoffReduction = 0.5f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.bonusRicochetPercent += 1f;
            stats.projectileDamageDropoffReduction *= 1f - _dropoffReduction;
        }
        Destroy(gameObject);
    }
}
