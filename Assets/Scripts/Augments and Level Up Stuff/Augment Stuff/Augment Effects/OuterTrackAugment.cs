using PurrNet.Prediction;
using UnityEngine;

// Grants a second orbiting weapon ring ("outer track") — a full duplicate of the main track's weapon set
// (same WeaponCount, same weapon type), spinning counterclockwise (opposite the main track) and orbiting
// from just past the main track's own total reach (WeaponRadius + EffectiveWeaponRange), dynamically
// rescaling with range automatically since it's derived from live stats every frame. Also a flat -X% weapon
// reach, applying to both rings (EffectiveWeaponRange is a shared player stat, not per-track) — the tradeoff
// for doubling weapon coverage.
//
// Routed through AddWeaponRangeScalingPenaltyPercent (2026-09-14 fix, was AddWeaponRangeTotalPenaltyPercent —
// see BladeweaverStartingAugment's own identical 2026-09-14 doc comment for the full "why": that penalty
// applied to the shared WeaponRange stat itself, which every augment that scales off Range
// (PhantomBladeRangeAugment, OnHitDamageDistanceAugment) also reads as its own scaling input, so THIS
// tradeoff was silently making those augments worse for anyone who picked it too, the exact same "bait
// option" problem Bladeweaver had baked in from the start). AddWeaponRangeScalingPenaltyPercent instead
// scales ONLY the weapon's own actual physical reach (model size, hitbox depth, orbit offset — see
// CharacterStatsPD.EffectiveWeaponRange's own doc comment), leaving the WeaponRange stat itself completely
// untouched. Still the same multiplicative-discount shape as before (2026-09-12 fix, was
// AddWeaponRangeTotalPercent(-totalWeaponRangePercent) — a negative addend into the same additive tier
// genuine bonuses use, so a big enough bonus from elsewhere could swamp this tradeoff almost entirely by
// summing against it directly instead of discounting the whole total), just on the new Penalty tier's own
// weaponRangeScalingPenalty multiplier now instead of WeaponRange's own totalWeaponRangePenalty.
//
// AddWeaponTracks(1) — Stats.weaponTracks going from 1 to 2 is what WeaponManagerPD.LateUpdate reads to
// actually spawn the outer ring (see its own targetOuterCount comment), and it's ALSO what
// WeaponStrength/WeaponOnHitPower auto-divide by (see CharacterStatsPD.WeaponStrength's own doc comment) —
// so per-hit weapon-strike damage/onhit power correctly halves to compensate for now having twice as many
// simultaneous weapons, structurally, with no bonus needed on top. Used to also grant a compensating +60%
// Strength/+60% OnHitPower (2026-08-19 removed, same reasoning as WeaponCountAugment/MultiShotAugment) —
// that was silently over-buffing every non-weapon-strike consumer of Strength/OnHitPower too (Phantom
// Blade, AoE Effects, on-hit procs), none of which were ever divided by weaponTracks in the first place.
public class OuterTrackAugment : AugmentEffectPD<OuterTrackAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Renamed from totalWeaponRangePercent (2026-09-14, alongside the AddWeaponRangeScalingPenaltyPercent
    // routing change above — the old name was already stale/misleading by that point, so there's no reason
    // to keep it just for Inspector-value continuity this time). Plain positive magnitude, same convention as
    // before (e.g. 0.4 = 40% reduction) and as BladeweaverStartingAugment's own identical field.
    [SerializeField] private float weaponRangeScalingPenaltyPercent = 0.4f;

    protected override void OnApply()
    {
        Stats.AddWeaponTracks(1);
        Stats.AddWeaponRangeScalingPenaltyPercent(weaponRangeScalingPenaltyPercent);
    }
}
