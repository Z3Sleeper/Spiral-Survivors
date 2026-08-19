using PurrNet.Prediction;
using UnityEngine;

// Loosely based on _OldSystemArchive/Augment Effects/HealthRegenOnHitAugment.cs, restructured for the new
// WeaponHitData/OnHit event pipeline. Deals bonus damage scaled off the player's OWN HealthRegen stat (not
// weapon damage) on every OnHit firing, plus a flat +0.5 HealthRegen grant. Rolls luck variance (the "*"
// annotation) and inherits data.isCrit from the triggering hit instead of rolling its own — see
// OnHitDamageAugment's own doc comment for the full reasoning, including why this deliberately doesn't
// call Stats.NotifyCritStrike on its own crit.
public class OnHitDamageHealthRegenAugment : AugmentEffectPD<OnHitDamageHealthRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 20f = 2000% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%, so 2000% = 20f).
    public float onHitHealthRegenPercent = 25f;
    [SerializeField] private float healthRegenBonusFlat    = 0f;
    // Flat floor added to the scaled portion below (2026-08-12, playtesting: this augment did literally
    // nothing for a build with zero HealthRegen investment) — added BEFORE the onHitPower multiply/luck roll
    // (see OnHit()), so it still scales with onHitPower/luck same as the rest of the hit, just guarantees a
    // non-zero base even at zero HealthRegen investment. Distinct from healthRegenBonusFlat above (a
    // one-time permanent +HealthRegen STAT grant) — this one is a per-hit damage floor instead.
    [SerializeField] private float baseDamage = 5f;

    // Baseline damage at 100% OnHitPower (the per-hit multiplier itself isn't included since it varies
    // hit-to-hit) for AugmentDescriptionFormatter's {LiveHealthRegenRatio} token — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseDamage + stats.HealthRegen * onHitHealthRegenPercent)} "
            : "";

    protected override void OnApply()
    {
        Stats.AddHealthRegenFlat(healthRegenBonusFlat);
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

        // Anything that deals damage rolls luck variance — no exceptions. baseDamage joins the scaled
        // portion INSIDE the onHitPower multiply (same convention as the live-preview formula, which already
        // assumes 100% OnHitPower) — see its own field comment.
        float preRoll = (baseDamage + Stats.HealthRegen * onHitHealthRegenPercent) * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamage(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = isHighroll, attacker = Stats, isPriority = false });
    }
}
