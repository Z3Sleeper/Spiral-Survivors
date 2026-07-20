using PurrNet.Prediction;
using UnityEngine;

// Grants a second orbiting weapon ring ("outer track") — a full duplicate of the main track's weapon set
// (same WeaponCount, same weapon type), spinning counterclockwise (opposite the main track) and orbiting
// from just past the main track's own total reach (WeaponRadius + WeaponRange), dynamically rescaling with
// range automatically since it's derived from live stats every frame. Also -25% Weapon Range, applying to
// both rings (range is a shared player stat, not per-track) — the tradeoff for doubling weapon coverage.
//
// AddWeaponTracks(1) is the base compensation — Stats.weaponTracks going from 1 to 2 is what
// WeaponManagerPD.LateUpdate reads to actually spawn the outer ring (see its own targetOuterCount comment),
// and it's ALSO what WeaponDamage/OnHitPower already auto-divide by (see CharacterStatsPD.weaponTracks' own
// doc comment) — so per-hit damage/onhit power correctly halves to compensate for now having twice as many
// simultaneous weapons on its own. On top of that, +60% Weapon Damage: +50% to actually offset that halving
// (two tracks landing on DIFFERENT targets, unlike two weapons stacked on the same ring, don't inherently
// double your total output the way raw weapon count would), plus an extra +10% by design — the two rings
// spin opposite directions and periodically overlap, and a target caught by both tracks at once is meant to
// take net MORE total damage than the same target would from one track alone, not just break even.
public class OuterTrackAugment : AugmentEffectPD<OuterTrackAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float rangePenalty    = 0.25f;
    [SerializeField] private float damageBonus     = 0.6f;
    // OnHitPower auto-divides by weaponTracks the same way WeaponDamage does (see class comment) — a
    // separate tunable rather than reusing damageBonus, kept in case onhit-triggered effects (Phantom
    // Blade, OnHitDamageAugment, etc.) need to be tuned independently of raw weapon damage.
    [SerializeField] private float onHitPowerBonus = 0.6f;

    protected override void OnApply()
    {
        Stats.AddWeaponTracks(1);
        Stats.AddWeaponRangePercent(-rangePenalty);
        Stats.AddWeaponDamagePercent(damageBonus);
        Stats.MultiplyOnHitPowerMultiplier(1f + onHitPowerBonus);
    }
}
