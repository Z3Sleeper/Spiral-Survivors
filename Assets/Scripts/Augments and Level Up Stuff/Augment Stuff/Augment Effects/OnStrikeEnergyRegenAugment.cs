using PurrNet.Prediction;
using UnityEngine;

// Gains energy on every real weapon Strike — deliberately the separate OnStrike chain (AddStrikeListener),
// not OnHit. Strike fires exactly once per genuine weapon connection with an enemy and is never touched by
// anything that re-invokes OnHit (e.g. CritTriggerOnHitAugment calling Stats.NotifyHit again from its own
// crit listener) — Strike and OnHit are two separate conventions that both happen to use onHitPower for
// scaling triggered effects, not the same event under two names. Scaled by data.onHitPower (not a flat
// MaxEnergy% grant) so this still scales down with WeaponCount the same way OnHit-triggered effects do —
// see OnHitDamageAugment's own doc comment for the WeaponCount-divide reasoning. No flat stat grant. Doesn't
// scale with data.isCrit — matches OnStrikeHealAllyAugment's own precedent that a restore triggered off a
// strike event doesn't crit-scale, unlike the damage-dealing OnHit augments.
//
// Deliberately weaker while Surging (surgeEffectivenessMultiplier, default 0.1f = 90% less effective) —
// this is intentionally strong enough unconditionally to feed a near-permanent Surge loop (Strike -> Energy
// -> Surge again), and this multiplier is the separate knob for tuning THAT specific self-sustain case
// without touching the baseline out-of-Surge value at all.
public class OnStrikeEnergyRegenAugment : AugmentEffectPD<OnStrikeEnergyRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.1f = 10% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float onStrikeEnergyRegenPercent = 0.5f;
    // 0.1f = 10% of normal effectiveness while Surging (i.e. 90% less) — see class doc comment.
    [SerializeField] private float surgeEffectivenessMultiplier = 0.25f;

    // Baseline gain at 100% OnHitPower, non-Surging (both vary hit-to-hit/moment-to-moment, so neither is
    // baked into the preview — see OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the
    // full convention) for AugmentDescriptionFormatter's {LiveEnergyRegenRatio} token.
    public string LiveEnergyRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.EnergyRegen * onStrikeEnergyRegenPercent)} "
            : "";

    protected override void OnApply()
    {
        Stats.AddStrikeListener(OnStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStrikeListener(OnStrike);
    }

    private void OnStrike(WeaponHitData data)
    {
        float surgeMultiplier = Stats.IsSurging ? surgeEffectivenessMultiplier : 1f;

        // No luck roll — energy restores aren't damage/healing, so this doesn't get luck variance by
        // default (see feedback_luckroll_scope memory).
        Stats.GainEnergy(Stats.EnergyRegen * onStrikeEnergyRegenPercent * data.onHitPower * surgeMultiplier);
    }
}
