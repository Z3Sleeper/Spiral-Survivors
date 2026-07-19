using PurrNet.Prediction;
using UnityEngine;

// Grants a second orbiting weapon ring ("outer track") — a full duplicate of the main track's weapon set
// (same WeaponCount, same weapon type), spinning counterclockwise (opposite the main track) and orbiting
// from just past the main track's own total reach (WeaponRadius + WeaponRange), dynamically rescaling with
// range automatically since it's derived from live stats every frame. Also -25% Weapon Range, applying to
// both rings (range is a shared player stat, not per-track) — the tradeoff for doubling weapon coverage.
//
// AddWeaponTracks(1) is the whole spawn trigger — Stats.weaponTracks going from 1 to 2 is what
// WeaponManagerPD.LateUpdate reads to actually spawn the outer ring (see its own targetOuterCount comment),
// and it's ALSO what WeaponDamage/OnHitPower already auto-divide by (see CharacterStatsPD.weaponTracks' own
// doc comment) — so per-hit damage/onhit power correctly halves to compensate for now having twice as many
// simultaneous weapons, with no separate compensating bonus needed here.
public class OuterTrackAugment : AugmentEffectPD<OuterTrackAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float rangePenalty = 0.25f;

    protected override void OnApply()
    {
        Stats.AddWeaponTracks(1);
        Stats.AddWeaponRangePercent(-rangePenalty);
    }
}
