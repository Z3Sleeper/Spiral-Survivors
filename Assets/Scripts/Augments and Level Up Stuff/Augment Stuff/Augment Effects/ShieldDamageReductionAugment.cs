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

    [SerializeField] private float damageReductionPercent      = 0.4f;
    [SerializeField] private float saveInvincibilityDuration  = 1f;
    [SerializeField] private float saveCooldown                = 30f;

    [Header("Buff Bar")]
    [SerializeField] private Sprite buffIcon;

    // Plain, not State — mutated only from PredictedEvent listener callbacks (OnShieldBreak/OnShieldRecharge
    // themselves guarantee exactly-once-per-real-occurrence, see CharacterStatsPD._onShieldBreak's own doc
    // comment), same toggle-tracking shape as ShieldBreakFrenzyAugment's own _grantedView.
    private bool _reductionActive;

    // Spawned once, kept for the rest of the run as a persistent "death save ready" indicator (not just
    // shown while on cooldown) — same "always visible, sweep only animates when actually cooling down"
    // convention as a typical ability-cooldown icon.
    private BuffIconPD _buffIconInstance;

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
            Stats.AddDamageReductionPercent(damageReductionPercent);
        }

        // Owner-only — every player's own copy of this augment effect exists on every machine (see
        // AugmentEffectPD's own doc comment), but the buff bar is this client's own local HUD, not
        // something to show for other players' augments too.
        if (isOwner && BuffBarPD.instance != null)
            _buffIconInstance = BuffBarPD.instance.AddBuffIcon(buffIcon);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_buffIconInstance != null && BuffBarPD.instance != null)
            BuffBarPD.instance.RemoveBuffIcon(_buffIconInstance);
        if (Stats == null) return;
        Stats.RemoveShieldBreakListener(OnShieldBreak);
        Stats.RemoveShieldRechargeListener(OnShieldRecharge);
        Stats.RemoveLethalDamageTakenListener(OnLethalDamageTaken);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (_buffIconInstance != null)
            _buffIconInstance.SetCooldownFill(viewState.cooldownTimer / saveCooldown);
    }

    private void OnShieldBreak()
    {
        if (!_reductionActive) return;
        _reductionActive = false;
        Stats.AddDamageReductionPercent(-damageReductionPercent);
    }

    private void OnShieldRecharge()
    {
        if (_reductionActive) return;
        _reductionActive = true;
        Stats.AddDamageReductionPercent(damageReductionPercent);
    }

    private void OnLethalDamageTaken()
    {
        if (currentState.cooldownTimer > 0f) return;
        currentState.cooldownTimer = saveCooldown;

        Stats.PreventLethalDamage();
        Stats.GrantInvincibility(saveInvincibilityDuration);
        Stats.RechargeShield();
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.cooldownTimer > 0f)
            state.cooldownTimer = Mathf.Max(0f, state.cooldownTimer - delta);
    }
}
