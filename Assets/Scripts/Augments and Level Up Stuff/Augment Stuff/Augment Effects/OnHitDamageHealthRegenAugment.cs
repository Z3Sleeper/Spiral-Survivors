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
    [SerializeField] private float onHitHealthRegenPercent = 20f;
    [SerializeField] private float healthRegenBonusFlat    = 0.5f;

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
        float baseDamage = Stats.HealthRegen * onHitHealthRegenPercent * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamage(baseDamage, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        if (isHighroll) rolled *= 1f + Stats.HighrollBonus;

        if (data.isCrit) rolled *= Stats.CritDamage;

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = isHighroll, attacker = Stats, isPriority = false });
    }
}
