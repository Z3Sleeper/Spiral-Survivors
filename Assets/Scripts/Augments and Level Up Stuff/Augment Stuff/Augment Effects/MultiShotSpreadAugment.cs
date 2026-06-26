using UnityEngine;

public class MultiShotSpreadAugment : MonoBehaviour
{
    [SerializeField] private float _spreadReduction = 0.8f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.multishotSpreadAngle *= 1f - _spreadReduction;
        }
        Destroy(gameObject);
    }
}
