using PurrNet.Prediction;
using UnityEngine;

// Deals bonus damage scaled off BonusHealth (MaxHealth minus the character's raw baseMaxHealth — i.e. only
// the health gained from augments/meta-progression/etc., not weapon damage or unmodified starting health)
// PLUS a flat onHitStrengthPercent (5%) of Strength, on every OnHit firing — same structure as
// OnHitDamageHealthRegenAugment but no flat stat grant. The old flat baseDamage floor (2026-08-24) is gone,
// replaced by that Strength term instead — same "guarantee non-zero output even with zero investment in the
// primary scaling stat" purpose the old baseDamage served, just now itself a real scaling stat instead of a
// flat number. Rolls luck variance and inherits data.isCrit/data.isHighroll from the triggering hit (via
// WeaponDamageMath.RollDamageInherited) — see OnHitDamageAugment's own doc comment for the full reasoning,
// including why this deliberately doesn't call Stats.NotifyCritStrike on its own crit.
//
// onHitBonusHealthPercent/onHitStrengthPercent are public (not [SerializeField] private) and
// LiveBonusHealthRatio exists specifically so AugmentData.description can reference
// {onHitBonusHealthPercent}/{onHitStrengthPercent}/{LiveBonusHealthRatio} directly — see
// AugmentDescriptionFormatter's own doc comment for the full reflection-token contract.
public class OnHitDamageHealthAugment : AugmentEffectPD<OnHitDamageHealthAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 0.5f = 50% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float onHitBonusHealthPercent = 0.5f;
    // 0.05f = 5% Strength (2026-08-24, replaces the old flat baseDamage floor — see class doc comment).
    public float onHitStrengthPercent = 0.05f;

    // Baseline damage this would currently deal (BonusHealth * percent + Strength * onHitStrengthPercent, at
    // 100% OnHitPower — the per-hit OnHitPower multiplier itself isn't included since that varies hit-to-hit,
    // not a stat to preview) for AugmentDescriptionFormatter's {LiveBonusHealthRatio} token. Blank while not
    // actually in a match, or with no character context to compute against, rather than showing a misleading
    // number.
    public string LiveBonusHealthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.BonusHealth * onHitBonusHealthPercent + stats.Strength * onHitStrengthPercent)} "
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

        // Anything that deals damage rolls luck variance — no exceptions.
        float preRoll = (Stats.BonusHealth * onHitBonusHealthPercent + Stats.Strength * onHitStrengthPercent) * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamageInherited(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, data.isHighroll);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, data.isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default, data.isHighroll);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = data.isHighroll, attacker = Stats, isPriority = false });
    }
}
