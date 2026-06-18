using UnityEngine;

public class RicochetPlus1Augment : MonoBehaviour
{
    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.bonusRicochetFlat += 1f;
        }
        Destroy(gameObject);
    }
}
