using PurrNet.Prediction;
using UnityEngine;

// Grants CharacterStatsPD.CritHealPower (see its own doc comment) — the actual crit-heal roll and
// amplification now live directly in CharacterStatsPD.Heal() itself, not in this augment, so this is just a
// plain stat grant like most other augments (AddCritHealPowerPercent), reversed in OnDestroy(). No longer a
// listener: an earlier version rolled its own bonus off AddHealListener and applied it as a SEPARATE
// HealSilent top-up, which meant anything reading the original heal amount never saw the crit bonus folded
// in (or worse, would double-count it if it also listened for the top-up). Moving the roll into Heal()
// itself means every heal reader sees one true, already-amplified number.
//
// 0.3 (+30%) matches the old system's tuned default (CritDamage 2.0 * critHealBonusPercent 0.15 = 0.3) — but
// this is now a FLAT grant, not re-derived from CritDamage every heal the way the old formula was. A player
// stacking more Crit Damage later in a run no longer automatically grows this bonus the way it used to;
// it's its own independent dial now, same as every other CharacterStatsPD stat.
public class CriticalHealingAugment : AugmentEffectPD<CriticalHealingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float critHealPowerPercent = 0.3f;

    protected override void OnApply()
    {
        Stats.AddCritHealPowerPercent(critHealPowerPercent);

        // TODO: also apply this bonus to healing GIVEN to allies once an ally-heal mechanic exists — Heal()
        // only covers healing received in the meantime.
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.AddCritHealPowerPercent(-critHealPowerPercent);
    }
}
