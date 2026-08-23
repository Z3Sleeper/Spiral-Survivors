using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — this AugmentData can be offered as a normal pickable augment AND/OR dropped
// directly into Rogue's ClassPassiveData.passiveAugments (AugmentManagerPD registers it as a real pick
// either way — see that field's own doc comment). While Surging, doubles weapon count (via
// MultiplyWeaponCountMultiplier/DivideWeaponCountMultiplier — bonusWeaponCountMultiplier's own multiplicative
// wrapper, not AddWeaponCountFlat, since this is a temporary x2 rather than a permanent +N), applied on
// Surge start and reversed on Surge end. Used to also grant a compensating +30% Strength/+30% OnHitPower
// alongside it (2026-08-19 removed, same reasoning as WeaponCountAugment/MultiShotAugment) — the extra
// weapons' per-hit damage cost is already covered structurally by WeaponStrength/WeaponOnHitPower's own
// auto-divide-by-WeaponCount (see CharacterStatsPD.WeaponStrength's own doc comment), and the removed bonus
// was silently over-buffing every non-weapon-strike consumer of Strength/OnHitPower too.
//
// Merged with the former standalone SurgeCostReductionAugment (2026-08-19, deleted — never actually wired
// up as its own pick, and was judged too boring on its own) — a permanent, one-shot Surge Cost reduction
// grant now lives here too, alongside the conditional weapon-count doubling. Multiplicative diminishing-
// returns stacking against CharacterStatsPD's own surgeCostReductionPercent tier (neutral 1f baseline, so
// the default 0.25 lands it at *0.75 = -25%, lowered 2026-08-23 from -50%) — the reduction side can never
// mathematically drive SurgeCost to 0 or below, regardless of how many reduction/increase sources (e.g.
// SurgeCostIncreaseAugment) combine.
// Granted unconditionally in OnApply(), NOT gated behind Surge start/end like the weapon-count doubling
// above — this half is a flat, always-on grant, same shape as any other permanent flat-stat augment.
public class SurgeWeaponCountAugment : AugmentEffectPD<SurgeWeaponCountAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int bonusWeaponCountMultiplier = 2;
    [SerializeField] private float surgeCostPercentReduction = 0.25f;

    // Plain, not State — mutated only from PredictedEvent listener callbacks, same role/reasoning as
    // SurgeEndMoveSpeedAugment's own _bonusActive. Without this, picking this augment WHILE already Surging
    // subscribes to both events but misses the OnSurgeStarted that already fired for the current Surge — so
    // when that Surge ends, OnSurgeEnded still fires and unconditionally DIVIDES/subtracts a bonus that was
    // never actually granted, corrupting WeaponCount/Strength/OnHitPower (this was the "picking it mid-surge
    // wipes all my weapons" bug).
    private bool _surgeBonusGranted;

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddSurgeEndedListener(OnSurgeEnded);
        Stats.AddSurgeCostReductionPercent(surgeCostPercentReduction);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null)
        {
            Stats.RemoveSurgeStartedListener(OnSurgeStarted);
            Stats.RemoveSurgeEndedListener(OnSurgeEnded);
        }
    }

    private void OnSurgeStarted()
    {
        if (_surgeBonusGranted) return;
        _surgeBonusGranted = true;

        Stats.MultiplyWeaponCountMultiplier(bonusWeaponCountMultiplier);
    }

    private void OnSurgeEnded()
    {
        if (!_surgeBonusGranted) return;
        _surgeBonusGranted = false;

        Stats.DivideWeaponCountMultiplier(bonusWeaponCountMultiplier);
    }
}
