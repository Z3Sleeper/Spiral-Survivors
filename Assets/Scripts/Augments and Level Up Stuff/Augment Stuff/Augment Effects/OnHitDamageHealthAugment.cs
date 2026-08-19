using PurrNet.Prediction;
using UnityEngine;

// Deals bonus damage scaled off BonusHealth (MaxHealth minus the character's raw baseMaxHealth — i.e. only
// the health gained from augments/meta-progression/etc., not weapon damage or unmodified starting health)
// on every OnHit firing, same structure as OnHitDamageHealthRegenAugment but no flat stat grant. Rolls luck
// variance and inherits data.isCrit from the triggering hit — see OnHitDamageAugment's own doc comment for
// the full reasoning, including why this deliberately doesn't call Stats.NotifyCritStrike on its own crit.
//
// onHitBonusHealthPercent is public (not [SerializeField] private) and LiveBonusHealthRatio exists
// specifically so AugmentData.description can reference {onHitBonusHealthPercent} / {LiveBonusHealthRatio}
// directly — see AugmentDescriptionFormatter's own doc comment for the full reflection-token contract.
public class OnHitDamageHealthAugment : AugmentEffectPD<OnHitDamageHealthAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 0.5f = 50% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float onHitBonusHealthPercent = 0.5f;
    // Flat floor added to the scaled portion below (2026-08-12, playtesting: this augment did literally
    // nothing for a build with zero BonusHealth investment) — added BEFORE the onHitPower multiply/luck roll
    // (see OnHit()), so it still scales with onHitPower/luck same as the rest of the hit, just guarantees a
    // non-zero base even at zero BonusHealth investment.
    [SerializeField] private float baseDamage = 5f;

    // Baseline damage this would currently deal (baseDamage + BonusHealth * percent, at 100% OnHitPower —
    // the per-hit OnHitPower multiplier itself isn't included since that varies hit-to-hit, not a stat to
    // preview) for AugmentDescriptionFormatter's {LiveBonusHealthRatio} token. Blank while not actually in a
    // match, or with no character context to compute against, rather than showing a misleading number.
    public string LiveBonusHealthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseDamage + stats.BonusHealth * onHitBonusHealthPercent)} "
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

        // Anything that deals damage rolls luck variance — no exceptions. baseDamage joins the scaled
        // portion INSIDE the onHitPower multiply (same convention as the live-preview formula, which already
        // assumes 100% OnHitPower) — see its own field comment.
        float preRoll = (baseDamage + Stats.BonusHealth * onHitBonusHealthPercent) * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamage(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = isHighroll, attacker = Stats, isPriority = false });
    }
}
