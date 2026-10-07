using PurrNet.Prediction;
using UnityEngine;

// On Dodge (CharacterStatsPD.AddDodgeListener, fires once per dash-movement's own activation window — see
// CharacterStatsPD.NotifyDodge's own doc comment for the exact 3-condition contract; a dodge only ever fires
// once per dash, "first dodge" and "dodge" are the same thing since the 2026-09-13 rework), heals a flat
// baseHeal plus bonusHealthRegenPercent of the caster's own current BonusHealthRegen (2026-09-14, was the
// full/total HealthRegen stat — see that property's own doc comment) — same "flat floor + percent-of-Bonus-
// HealthRegen" shape OnHitHealHealthRegenAugment already uses, just triggered off Dodge instead of OnHit,
// with no onHitPower multiply (Dodge carries no hit payload to scale by — _onDodge is a plain, payload-free
// PredictedEvent). No luck roll — heals don't roll luck at all (see CharacterStatsPD.Heal's own doc comment).
public class OnDodgeHealHealthRegenAugment : AugmentEffectPD<OnDodgeHealHealthRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float baseHeal = 30f;
    // 1f = 100% (this codebase's percent fields store the fraction, e.g. 7.5f = 750%). Scales off
    // Stats.BonusHealthRegen (2026-09-14, was Stats.HealthRegen, the full/total stat) — see that property's
    // own doc comment. Renamed from healthRegenPercent to bonusHealthRegenPercent for family-wide naming
    // consistency.
    public float bonusHealthRegenPercent = 7.5f;

    // For AugmentDescriptionFormatter's {LiveHealthRegenRatio} token — see
    // OnHitHealHealthRegenAugment.LiveHealthRegenRatio's own doc comment for the full convention.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseHeal + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";

    protected override void OnApply()
    {
        Stats.AddDodgeListener(OnDodge);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDodgeListener(OnDodge);
    }

    private void OnDodge()
    {
        if (Stats == null || Stats.IsDead) return;
        Stats.Heal(baseHeal + Stats.BonusHealthRegen * bonusHealthRegenPercent);

        // Heal VFX opt-in (2026-09-26, user-curated per-augment, non-persistent heals only) — safe
        // unconditionally: this callback only ever runs once per real dodge, off a plain payload-free
        // PredictedEvent (see class doc comment; VFXManager.PlayHealVfx's own per-target cooldown handles
        // the anti-spam side of this).
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
