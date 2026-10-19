using PurrNet.Prediction;
using UnityEngine;

// Not a port. Deals bonus weapon-damage-scaled damage on every OnHit firing — a pure Strength scalar, no
// flat base damage (removed 2026-08-24, alongside bumping onHitDamagePercent to a flat 25% — this augment
// already scaled purely off Strength with nothing else to add a floor for, unlike the BonusHealth/HealthRegen
// -scaled OnHitDamage siblings). Rolls luck variance (the "*" annotation) same as every other damage source,
// but the highroll STATUS (2026-09-14, was independently rolled) is inherited from data.isHighroll via
// WeaponDamageMath.RollDamageInherited, same as isCrit below — magnitude still varies via luck, only the
// highroll snap is shared with the triggering hit.
// Inherits data.isCrit from the hit that triggered this (WeaponHitData.isCrit, set by the weapon at the
// moment it rolled its own crit) instead of rolling an independent crit chance — same "if the trigger crit,
// this crits too" reasoning PhantomBladePD uses. Deliberately does NOT call Stats.NotifyCritStrike on its own
// crit though, since that would feed back into CritTriggerOnHitAugment's "crit triggers another onhit" chain
// and cascade indefinitely (onhit crits -> notifies crit -> triggers another onhit -> could crit again -> ...).
// isCrit here only affects this hit's own damage/feedback, nothing else.
public class OnHitDamageAugment : AugmentEffectPD<OnHitDamageAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    public float onHitDamagePercent = 0.25f;

    // Baseline damage at 100% OnHitPower (the per-hit multiplier itself isn't included since it varies
    // hit-to-hit) for AugmentDescriptionFormatter's {LiveStrengthRatio} token — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * onHitDamagePercent)} "
            : "";

    protected override void OnApply()
    {
        Stats.AddHitListener(OnHit);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveHitListener(OnHit);
    }

    private void OnHit(WeaponHitData data)
    {
        if (!data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        // data.weaponDamage and data.onHitPower each independently already divide by WeaponCount (see
        // CharacterStatsPD.WeaponStrength/WeaponOnHitPower, whichever literal weapon fired this hit) —
        // multiplying them directly would compound that into 1/WeaponCount², shrinking this augment's total
        // output as you stack more weapons instead of holding flat like every other weapon-damage-scaled
        // effect. Using Stats.Strength (undivided) instead of weaponDamage leaves onHitPower as the only
        // WeaponCount divisor — same convention OnStrikeHealAllyAugment/OnHitDamageHealthRegenAugment
        // already use (onHitPower multiplied against a non-divided base stat), while still letting this
        // scale with any real onHitPower stat investment, unlike a ratio-normalized approach would.
        // Anything that deals damage rolls luck variance — no exceptions.
        float preRoll = Stats.Strength * onHitDamagePercent * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamageInherited(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, data.isHighroll, Stats.NextFloat);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, data.isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default, data.isHighroll, Stats.executeThreshold, Stats.critExecuteThreshold);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = data.isHighroll, attacker = Stats, isPriority = false });
    }
}
