using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Not a port. Deals bonus weapon-damage-scaled damage on every OnHit firing. Rolls luck variance (the "*"
// annotation) same as every other damage source, and optionally crits if Stats.onHitCanCrit has been
// turned on (e.g. by OnHitCritAugment) — deliberately does NOT call Stats.NotifyCritStrike on its own crit
// though, since that would feed back into CritTriggerOnHitAugment's "crit triggers another onhit" chain
// and cascade indefinitely (onhit crits -> notifies crit -> triggers another onhit -> could crit again ->
// ...). isCrit here only affects this hit's own damage/feedback, nothing else.
public class OnHitDamageAugment : AugmentEffectPD<OnHitDamageAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float onHitDamagePercent = 0.2f;

    // Plain (not predicted State) — same "queue in Simulate-context, drain in UpdateView" split as every
    // weapon's own _hitFeedbackQueue, so damage numbers/flash fire at render rate instead of risking a
    // resimulation replaying TriggerHitFeedback's non-idempotent coroutine/SFX calls multiple times.
    private readonly List<(IEnemyPD enemy, float dmg, bool isCrit, bool isHighroll)> _hitFeedbackQueue = new();

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
        float baseDamage = data.weaponDamage * onHitDamagePercent * data.onHitPower;
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
