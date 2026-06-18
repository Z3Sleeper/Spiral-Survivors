using UnityEngine;

public class Ricochetx2Augment : MonoBehaviour
{
    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.bonusRicochetPercent += 1f;
        }
        Destroy(gameObject);
    }
}
