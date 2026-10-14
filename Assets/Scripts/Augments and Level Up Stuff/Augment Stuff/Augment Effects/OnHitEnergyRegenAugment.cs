using PurrNet.Prediction;
using UnityEngine;

// Gains energy on every real weapon hit — moved from the separate OnStrike chain onto the generic OnHit
// chain (AddHitListener, 2026-10-14, explicit request). Unlike Strike, OnHit CAN fire twice for a single
// real connection if the player also has CritTriggerOnHitAugment (which re-invokes NotifyHit on a crit
// specifically to make other onhit-triggered effects treat it as two hits) — same intentional synergy
// RogueVanishOnHitDurationAugment's own doc comment describes: a player stacking both gets double energy
// on a crit, not an accidental interaction. energyPerHit (renamed from energyPerStrike in the same pass) is
// a PERCENT of MaxEnergy per hit (2026-09-03, was a flat absolute amount — same "percent of MaxEnergy" shift
// OnAbilityStartRefillEnergyAugment/EnergyRegen itself already went through), still scaled by
// data.onHitPower so this keeps scaling down with WeaponCount the same way every other OnHit-triggered
// effect does — see OnHitDamageAugment's own doc comment for the WeaponCount-divide reasoning. Doesn't scale
// with data.isCrit — matches OnStrikeHealAllyAugment's own precedent that a restore triggered off a hit
// event doesn't crit-scale.
//
// No Surge-specific reduction anymore (2026-09-03, was a surgeEffectivenessMultiplier — deliberately
// weaker while Surging) — see CharacterStatsPD.GainEnergy's own doc comment: the infinite-Surge-loop
// concern this used to guard against is now handled centrally there (a hard zero specifically during
// Berserk/forced Surge), not per-augment, and a normal Surge was never actually the problem — a real Surge
// still drains energy the whole time regardless of how fast it's being refilled.
public class OnHitEnergyRegenAugment : AugmentEffectPD<OnHitEnergyRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.01f = 1% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%). Renamed from
    // energyPerStrike (2026-10-14, OnStrike -> OnHit move) — default already matched the live prefab value,
    // no numeric change.
    public float energyPerHit = 0.01f;

    // energyPerHit itself doesn't end in "Percent", so AugmentDescriptionFormatter's *100 convention
    // wouldn't apply to it directly — this alias exists so a description can reference
    // {EnergyPerHitPercent} to print "1" instead of "0.01" (same shape as DashHealAugment's own
    // HealthRegenMultiplierPercent alias).
    public float EnergyPerHitPercent => energyPerHit;

    // Same flat, unconditional Max Energy grant every other Surge-family energy augment grants
    // (SurgeWeaponCountAugment/SurgeStackingAttackSpeedAugment/SurgeHealAugment/FullEnergyBerserkSurgeAugment/
    // DeathPreventionBerserkAugment) — 2026-09-17, granted once in OnApply(), same shape as those. Dropped
    // from 25 to 10 (2026-10-14, OnStrike -> OnHit move) to match the live prefab value.
    public float bonusMaxEnergyFlat = 10f;

    protected override void OnApply()
    {
        Stats.AddHitListener(OnHit);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveHitListener(OnHit);
    }

    private void OnHit(WeaponHitData data)
    {
        // No luck roll — energy restores aren't damage/healing, so this doesn't get luck variance by
        // default (see feedback_luckroll_scope memory).
        Stats.GainEnergy(energyPerHit * data.onHitPower * Stats.MaxEnergy);
    }
}
