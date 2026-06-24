using UnityEngine;

public class PhantomBladeSizeAugment : MonoBehaviour
{
    [SerializeField] private float sizeBonus = 2f;
    [SerializeField] private float bladeDamageBonus = 1.5f;

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
        if (blade.isSecondary) return;
        blade.sizeMultiplier *= sizeBonus;
        blade.damageMultiplier *= bladeDamageBonus;
    }
}
