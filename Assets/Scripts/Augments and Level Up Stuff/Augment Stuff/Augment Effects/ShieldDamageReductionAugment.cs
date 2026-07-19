using PurrNet.Prediction;
using UnityEngine;

// While Shielded, take 40% less damage (Stats.AddDamageReductionPercent — see that method's own doc comment
// for the inverted-sign reasoning). Removed the tick shield breaks, reapplied the tick it recharges
// (whether naturally or via this same augment's own death save below), via CharacterStatsPD's
// _onShieldBreak/_onShieldRecharge events.
//
// Death save: if a hit would otherwise kill the player, leave them at 1 health instead
// (Stats.PreventLethalDamage, called synchronously from _onLethalDamageTaken — see that event's own doc
// comment on CharacterStatsPD for why this has to happen from inside TakeDamage's own call), grant 1s
// invincibility, and instantly recharge shield. Gated by a 30s cooldown tracked in this effect's own State,
// same self-timed-countdown shape as ShieldBreakFrenzyAugment's own buff timer.
public class ShieldDamageReductionAugment : AugmentEffectPD<ShieldDamageReductionAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // > 0 while the death save is on cooldown, ticking down toward 0 in Simulate().
        public float cooldownTimer;
        public void Dispose() { }
    }

    [SerializeField] private float damageReductionBonus      = 0.4f;
    [SerializeField] private float saveInvincibilityDuration  = 1f;
    [SerializeField] private float saveCooldown                = 30f;

    // Plain, not State — mutated only from PredictedEvent listener callbacks (OnShieldBreak/OnShieldRecharge
    // themselves guarantee exactly-once-per-real-occurrence, see CharacterStatsPD._onShieldBreak's own doc
    // comment), same toggle-tracking shape as ShieldBreakFrenzyAugment's own _grantedView.
    private bool _reductionActive;

    protected override void OnApply()
    {
        Stats.AddShieldBreakListener(OnShieldBreak);
        Stats.AddShieldRechargeListener(OnShieldRecharge);
        Stats.AddLethalDamageTakenListener(OnLethalDamageTaken);

        // Granted immediately if already shielded at pick time — otherwise this player would go unprotected
        // until their NEXT shield break+recharge cycle, rather than the very first one they already have.
        if (Stats.CurrentShield > 0f)
        {
            _reductionActive = true;
            Stats.AddDamageReductionPercent(damageReductionBonus);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveShieldBreakListener(OnShieldBreak);
        Stats.RemoveShieldRechargeListener(OnShieldRecharge);
        Stats.RemoveLethalDamageTakenListener(OnLethalDamageTaken);
    }

    private void OnShieldBreak()
    {
        if (!_reductionActive) return;
        _reductionActive = false;
        Stats.AddDamageReductionPercent(-damageReductionBonus);
    }

    private void OnShieldRecharge()
    {
        if (_reductionActive) return;
        _reductionActive = true;
        Stats.AddDamageReductionPercent(damageReductionBonus);
    }

    private void OnLethalDamageTaken()
    {
        if (currentState.cooldownTimer > 0f) return;
        currentState.cooldownTimer = saveCooldown;

        Stats.PreventLethalDamage();
        Stats.SetInvincible(saveInvincibilityDuration);
        Stats.RechargeShield();
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (state.cooldownTimer > 0f)
            state.cooldownTimer = Mathf.Max(0f, state.cooldownTimer - delta);
    }
}
