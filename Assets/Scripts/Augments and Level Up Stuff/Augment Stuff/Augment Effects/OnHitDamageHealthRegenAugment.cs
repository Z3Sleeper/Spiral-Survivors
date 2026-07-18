using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Loosely based on _OldSystemArchive/Augment Effects/HealthRegenOnHitAugment.cs, restructured for the new
// WeaponHitData/OnHit event pipeline. Deals bonus damage scaled off the player's OWN HealthRegen stat (not
// weapon damage) on every OnHit firing, plus a flat +0.5 HealthRegen grant. Rolls luck variance (the "*"
// annotation) and optionally crits if Stats.onHitCanCrit — see OnHitDamageAugment's own doc comment for
// why this deliberately doesn't call Stats.NotifyCritStrike on its own crit.
public class OnHitDamageHealthRegenAugment : AugmentEffectPD<OnHitDamageHealthRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 20f = 2000% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%, so 2000% = 20f).
    [SerializeField] private float onHitHealthRegenPercent = 20f;
    [SerializeField] private float healthRegenBonusFlat    = 0.5f;

    private readonly List<(IEnemyPD enemy, float dmg, bool isCrit, bool isHighroll)> _hitFeedbackQueue = new();

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

        bool isCrit = Stats.onHitCanCrit && GameRandom.instance.NextFloat() < Stats.CritChance;
        if (isCrit) rolled *= Stats.CritDamage;

        enemy.TakeDamage(rolled, isCrit, Stats.owner ?? default);
        _hitFeedbackQueue.Add((enemy, rolled, isCrit, isHighroll));
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null) return;

        foreach (var (enemy, dmg, isCrit, isHighroll) in _hitFeedbackQueue)
            enemy.TriggerHitFeedback(dmg, isCrit, isHighroll, Stats);
        _hitFeedbackQueue.Clear();
    }
}
