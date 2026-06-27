using UnityEngine;

public class RicochetPlus1Augment : MonoBehaviour
{
    [SerializeField] private float _dropoffReduction = 0.5f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.bonusRicochetFlat += 1f;
            stats.projectileDamageDropoffReduction *= 1f - _dropoffReduction;
        }
        Destroy(gameObject);
    }
}
