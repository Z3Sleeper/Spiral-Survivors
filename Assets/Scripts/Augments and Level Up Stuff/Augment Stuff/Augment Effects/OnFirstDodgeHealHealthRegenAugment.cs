using PurrNet.Prediction;
using UnityEngine;

// On First Dodge (FD — CharacterStatsPD.AddFirstDodgeListener, fires once per dash-movement's own activation
// window — see CharacterStatsPD.NotifyDodge's own doc comment for the exact 3-condition Dodge contract),
// heals a flat baseHeal plus healthRegenPercent of the caster's own current HealthRegen — same "flat floor +
// percent-of-HealthRegen" shape OnHitHealHealthRegenAugment already uses, just triggered off First Dodge
// instead of OnHit, with no onHitPower multiply (Dodge carries no hit payload to scale by — _onFirstDodge is
// a plain, payload-free PredictedEvent). No luck roll — heals don't roll luck at all (see
// CharacterStatsPD.Heal's own doc comment).
public class OnFirstDodgeHealHealthRegenAugment : AugmentEffectPD<OnFirstDodgeHealHealthRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float baseHeal = 30f;
    // 1f = 100% (this codebase's percent fields store the fraction, e.g. 7.5f = 750%).
    public float healthRegenPercent = 7.5f;

    // For AugmentDescriptionFormatter's {LiveHealthRegenRatio} token — see
    // OnHitHealHealthRegenAugment.LiveHealthRegenRatio's own doc comment for the full convention.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseHeal + stats.HealthRegen * healthRegenPercent)} "
            : "";

    protected override void OnApply()
    {
        Stats.AddFirstDodgeListener(OnFirstDodge);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveFirstDodgeListener(OnFirstDodge);
    }

    private void OnFirstDodge()
    {
        if (Stats == null || Stats.IsDead) return;
        Stats.Heal(baseHeal + Stats.HealthRegen * healthRegenPercent);
    }
}
