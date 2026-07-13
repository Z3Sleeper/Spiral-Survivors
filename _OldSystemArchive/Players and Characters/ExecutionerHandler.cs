using UnityEngine;

public class ExecutionerHandler : MonoBehaviour
{
    [Header("Outer (Blade) Damage")]
    public float outerDamagePercent = 1.35f;
    public float outerDamagePenalty = 1f;
    public float OuterDamageMultiplier => outerDamagePercent * outerDamagePenalty;

    [Header("Inner (Handle) Damage")]
    public float innerDamagePercent = 0.5f;
    public float innerDamagePenalty = 1f;
    public float InnerDamageMultiplier => innerDamagePercent * innerDamagePenalty;
}
