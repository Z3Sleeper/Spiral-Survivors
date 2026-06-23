using UnityEngine;

public class PhantomBladeSizeAugment : MonoBehaviour
{
    [SerializeField] private float sizeBonus = 2f;
    [SerializeField] private float bladeDamageBonus = 0.5f;

    private void Start()
    {
        if (!GetComponentInParent<CharacterStats>().isOwner) { Destroy(gameObject); return; }
        PhantomBlade.OnSummon += OnSummon;
    }

    private void OnDestroy()
    {
        PhantomBlade.OnSummon -= OnSummon;
    }

    private void OnSummon(PhantomBlade blade)
    {
        blade.transform.localScale *= sizeBonus;
        blade.halfExtents *= sizeBonus;
        blade.speed *= sizeBonus;
        blade.maxRange *= sizeBonus;
        blade.damage += blade.baseDamage * bladeDamageBonus;
        blade.transform.position -= blade.transform.forward * ((sizeBonus - 1f) * blade.spawnRadius);
    }
}
