using PurrNet.Prediction;
using UnityEngine;

// Not a port. Deals bonus weapon-damage-scaled damage on every OnHit firing. Rolls luck variance (the "*"
// annotation) same as every other damage source. Inherits data.isCrit from the hit that triggered this
// (WeaponHitData.isCrit, set by the weapon at the moment it rolled its own crit) instead of rolling an
// independent crit chance — same "if the trigger crit, this crits too" reasoning PhantomBladePD uses.
// Deliberately does NOT call Stats.NotifyCritStrike on its own crit though, since that would feed back into
// CritTriggerOnHitAugment's "crit triggers another onhit" chain and cascade indefinitely (onhit crits ->
// notifies crit -> triggers another onhit -> could crit again -> ...). isCrit here only affects this hit's
// own damage/feedback, nothing else.
public class OnHitDamageAugment : AugmentEffectPD<OnHitDamageAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    public float onHitDamagePercent = 0.2f;
    // Flat floor added to the scaled portion below (2026-08-12, playtesting: this augment did literally
    // nothing for a build with zero Strength investment) — added BEFORE the onHitPower multiply/luck roll
    // (see OnHit()), so it still scales with onHitPower/luck same as the rest of the hit, just guarantees a
    // non-zero base even at zero Strength investment.
    [SerializeField] private float baseDamage = 5f;

    // Baseline damage at 100% OnHitPower (the per-hit multiplier itself isn't included since it varies
    // hit-to-hit) for AugmentDescriptionFormatter's {LiveStrengthRatio} token — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention. Includes
    // baseDamage so the preview matches what OnHit actually deals.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseDamage + stats.Strength * onHitDamagePercent)} "
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
        // Anything that deals damage rolls luck variance — no exceptions. baseDamage joins the scaled
        // portion INSIDE the onHitPower multiply (same convention as the live-preview formula, which already
        // assumes 100% OnHitPower) — see its own field comment.
        float preRoll = (baseDamage + Stats.Strength * onHitDamagePercent) * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamage(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = isHighroll, attacker = Stats, isPriority = false });
    }
}
