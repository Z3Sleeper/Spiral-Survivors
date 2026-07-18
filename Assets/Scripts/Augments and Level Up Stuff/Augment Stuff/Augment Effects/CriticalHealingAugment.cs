using PurrNet.Prediction;
using UnityEngine;

// Not a straight port — old system's CriticalHealingAugment rolled its bonus through its own
// CritDamage/variance formula off a global static CharacterStats.OnHeal event. New spec is simpler: a
// flat +30% (no asterisk, so no luck roll per the project's asterisk convention) on top of any heal this
// player receives, gated behind a per-heal roll against CritChance itself (not a separate proc stat).
public class CriticalHealingAugment : AugmentEffectPD<CriticalHealingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusCritChance     = 0.1f;
    [SerializeField] private float critHealBonusPercent = 0.3f;

    protected override void OnApply()
    {
        Stats.AddCritChanceFlat(bonusCritChance);
        Stats.AddHealListener(OnHeal);

        // TODO: also apply this bonus to healing GIVEN to allies once an ally-heal mechanic exists —
        // nothing to hook into yet, this augment only covers healing received in the meantime.
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveHealListener(OnHeal);
    }

    // HealSilent, not Heal — Heal() would re-invoke _onHeal and call this handler again, rolling (and
    // potentially applying) another bonus off its own top-up indefinitely.
    private void OnHeal(float amount)
    {
        if (GameRandom.instance.NextFloat() >= Stats.CritChance) return;
        Stats.HealSilent(amount * critHealBonusPercent);
    }
}
