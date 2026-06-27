using UnityEngine;

public class MultiShotSpreadAugment : MonoBehaviour
{
    [SerializeField] private float _spreadReduction = 0.8f;
    [SerializeField] private float _dropoffReduction = 0.5f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.multishotSpreadAngle *= 1f - _spreadReduction;
            stats.projectileDamageDropoffReduction *= 1f - _dropoffReduction;
        }
        Destroy(gameObject);
    }
}
