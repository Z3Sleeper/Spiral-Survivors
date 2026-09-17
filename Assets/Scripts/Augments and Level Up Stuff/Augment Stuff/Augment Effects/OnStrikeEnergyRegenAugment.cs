using PurrNet.Prediction;
using UnityEngine;

// Gains energy on every real weapon Strike — deliberately the separate OnStrike chain (AddStrikeListener),
// not OnHit. Strike fires exactly once per genuine weapon connection with an enemy and is never touched by
// anything that re-invokes OnHit (e.g. CritTriggerOnHitAugment calling Stats.NotifyHit again from its own
// crit listener) — Strike and OnHit are two separate conventions that both happen to use onHitPower for
// scaling triggered effects, not the same event under two names. energyPerStrike is now a PERCENT of
// MaxEnergy per strike (2026-09-03, was a flat absolute amount — same "percent of MaxEnergy" shift
// OnAbilityStartRefillEnergyAugment/EnergyRegen itself already went through), still scaled by
// data.onHitPower so this keeps scaling down with WeaponCount the same way OnHit-triggered effects do — see
// OnHitDamageAugment's own doc comment for the WeaponCount-divide reasoning. Field name kept unchanged
// despite the unit change, per explicit request, so existing description tokens referencing it don't
// break. Doesn't scale with data.isCrit — matches OnStrikeHealAllyAugment's own precedent that a restore
// triggered off a strike event doesn't crit-scale, unlike the damage-dealing OnHit augments.
//
// No Surge-specific reduction anymore (2026-09-03, was a surgeEffectivenessMultiplier — deliberately
// weaker while Surging) — see CharacterStatsPD.GainEnergy's own doc comment: the infinite-Surge-loop
// concern this used to guard against is now handled centrally there (a hard zero specifically during
// Berserk/forced Surge), not per-augment, and a normal Surge was never actually the problem — a real Surge
// still drains energy the whole time regardless of how fast it's being refilled.
public class OnStrikeEnergyRegenAugment : AugmentEffectPD<OnStrikeEnergyRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.01f = 1% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float energyPerStrike = 0.01f;

    // energyPerStrike itself doesn't end in "Percent", so AugmentDescriptionFormatter's *100 convention
    // wouldn't apply to it directly — this alias exists so a description can reference
    // {EnergyPerStrikePercent} to print "1" instead of "0.01" (same shape as DashHealAugment's own
    // HealthRegenMultiplierPercent alias).
    public float EnergyPerStrikePercent => energyPerStrike;

    // Same flat, unconditional +25 Max Energy every other Surge-family energy augment grants
    // (SurgeWeaponCountAugment/SurgeStackingAttackSpeedAugment/SurgeHealAugment/FullEnergyBerserkSurgeAugment/
    // DeathPreventedBerserkAugment) — 2026-09-17, granted once in OnApply(), same shape as those.
    [SerializeField] private float bonusMaxEnergyFlat = 25f;

    protected override void OnApply()
    {
        Stats.AddStrikeListener(OnStrike);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStrikeListener(OnStrike);
    }

    private void OnStrike(WeaponHitData data)
    {
        // No luck roll — energy restores aren't damage/healing, so this doesn't get luck variance by
        // default (see feedback_luckroll_scope memory).
        Stats.GainEnergy(energyPerStrike * data.onHitPower * Stats.MaxEnergy);
    }
}
