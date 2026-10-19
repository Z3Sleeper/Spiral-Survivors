using PurrNet.Prediction;
using UnityEngine;

// Loosely based on _OldSystemArchive/Augment Effects/HealthRegenOnHitAugment.cs, restructured for the new
// WeaponHitData/OnHit event pipeline. Deals bonus damage scaled off the player's OWN HealthRegen stat (not
// weapon damage) PLUS a flat onHitStrengthPercent (5%) of Strength, on every OnHit firing, plus a flat +0.5
// HealthRegen grant. The old flat baseDamage floor (2026-08-24) is gone, replaced by that Strength term
// instead — same "guarantee non-zero output even with zero investment in the primary scaling stat" purpose
// the old baseDamage served, just now itself a real scaling stat instead of a flat number; healthRegenBonusFlat
// is untouched, a separate one-time permanent stat grant, not a per-hit damage floor. Rolls luck variance
// (the "*" annotation) and inherits data.isCrit/data.isHighroll from the triggering hit (via
// WeaponDamageMath.RollDamageInherited) instead of rolling its own — see OnHitDamageAugment's own doc comment
// for the full reasoning, including why this deliberately doesn't call Stats.NotifyCritStrike on its own crit.
public class OnHitDamageHealthRegenAugment : AugmentEffectPD<OnHitDamageHealthRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 20f = 2000% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%, so 2000% = 20f).
    public float onHitHealthRegenPercent = 25f;
    [SerializeField] private float healthRegenBonusFlat    = 0f;
    // 0.05f = 5% Strength (2026-08-24, replaces the old flat baseDamage floor — see class doc comment).
    public float onHitStrengthPercent = 0.05f;

    // Baseline damage at 100% OnHitPower (the per-hit multiplier itself isn't included since it varies
    // hit-to-hit) for AugmentDescriptionFormatter's {LiveHealthRegenRatio} token — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.HealthRegen * onHitHealthRegenPercent + stats.Strength * onHitStrengthPercent)} "
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

        // Anything that deals damage rolls luck variance — no exceptions.
        float preRoll = (Stats.HealthRegen * onHitHealthRegenPercent + Stats.Strength * onHitStrengthPercent) * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamageInherited(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, data.isHighroll, Stats.NextFloat);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, data.isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default, data.isHighroll, Stats.executeThreshold, Stats.critExecuteThreshold);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = data.isHighroll, attacker = Stats, isPriority = false });
    }
}
