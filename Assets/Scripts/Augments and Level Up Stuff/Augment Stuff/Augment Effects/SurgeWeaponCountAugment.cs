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
// up as its own pick, and was judged too boring on its own) — a permanent, one-shot bonus now lives here
// too, alongside the conditional weapon-count doubling. Granted unconditionally in OnApply(), NOT gated
// behind Surge start/end like the weapon-count doubling above — this half is a flat, always-on grant, same
// shape as any other permanent flat-stat augment.
//
// RESHAPED 2026-08-29 — was a permanent -25% Surge Cost reduction (via AddSurgeCostReductionPercent). With
// SurgeInterruptAoEAugment's own rework the same day gating off ENERGY SPENT rather than a full-energy
// snapshot (see that augment's own class doc comment), a Surge Cost reduction here would now directly nerf
// that augment's own activation gate — spending less per Surge means a slower climb toward
// energySpentRequirement. Explicit decision: Surge Cost itself is done being tuned/touched going forward:
// this now grants a flat +25 Max Energy instead (Stats.AddMaxEnergyFlat — routes through ApplyStatChange, so
// Current Energy rises by the same delta as the new ceiling, e.g. 100->125 Max Energy also carries 75->100
// Current Energy; see that method's own doc comment). Same net effect of "more Surge uptime per full-to-empty
// cycle" the old Surge Cost reduction was going for, without ever touching SurgeCost itself again.
public class SurgeWeaponCountAugment : AugmentEffectPD<SurgeWeaponCountAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int bonusWeaponCountMultiplier = 2;
    public float bonusMaxEnergyFlat = 25f;

    // Plain, not State — mutated only from PredictedEvent listener callbacks, same role/reasoning as
    // SurgeEnergySpentMoveSpeedAugment's own _bonusActive. Without this, picking this augment WHILE already Surging
    // subscribes to both events but misses the OnSurgeStarted that already fired for the current Surge — so
    // when that Surge ends, OnSurgeEnded still fires and unconditionally DIVIDES/subtracts a bonus that was
    // never actually granted, corrupting WeaponCount/Strength/OnHitPower (this was the "picking it mid-surge
    // wipes all my weapons" bug).
    private bool _surgeBonusGranted;

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddSurgeEndedListener(OnSurgeEnded);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);

        // Picked mid-Surge (2026-09-03 fix) — the Surge already in progress fired its own OnSurgeStarted
        // before this subscription existed, so without this the weapon-count bonus wouldn't apply until
        // the NEXT Surge starts. Matters most for a Berserk Surge already in progress (can't be manually
        // ended early to "re-trigger" the bonus sooner). Reuses OnSurgeStarted() directly rather than
        // duplicating its body — already idempotent via _surgeBonusGranted, safe to call unconditionally.
        if (Stats.IsSurging) OnSurgeStarted();
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
