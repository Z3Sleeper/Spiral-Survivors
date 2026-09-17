using PurrNet.Prediction;
using UnityEngine;

// On Dodge (CharacterStatsPD.AddDodgeListener, fires once per dash-movement's own activation window — see
// CharacterStatsPD.NotifyDodge's own doc comment for the exact 3-condition contract; a dodge only ever fires
// once per dash, "first dodge" and "dodge" are the same thing since the 2026-09-13 rework), grants a flat
// Bonus-tier Attack Speed buff (AddAttackSpeedFlat — flat over percent per explicit user preference; despite
// the "Flat" name it takes an intuitive fraction of baseAttackSpeed, e.g. 0.6 for +60%, see that method's own
// doc comment) for a fixed duration.
//
// Re-triggering while the buff is already active just refreshes the duration back to full — see OnDodge's
// own rising-edge guard — it does NOT re-grant/stack a second copy of the bonus, same "start/end pairing
// needs a granted guard" convention every timed on-trigger buff in this codebase follows. State.timer doubles
// as both the countdown AND the "is it currently active" flag (> 0 means active), no separate bool needed.
//
// Shows a buff-bar icon (2026-08-28) for the buff's active duration — same transient add-on-grant/
// remove-on-expiry shape as SurgeEnergySpentMoveSpeedAugment's own icon, sweeping fill as the timer counts down.
public class OnDodgeGainAttackSpeedAugment : AugmentEffectPD<OnDodgeGainAttackSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        public void Dispose() { }
    }

    public float attackSpeedPercent = 0.6f;
    public float duration = 5f;

    [Header("Buff Bar")]
    [SerializeField] private Sprite buffIcon;

    // Spawned only while the buff is active — removed the instant it ends (natural expiry or this
    // instance's own teardown), same transient-icon shape as SurgeEnergySpentMoveSpeedAugment's own
    // _buffIconInstance. Only ever non-null on the owning client (see OnDodge's isOwner gate below),
    // so the ungated removal in Simulate()/OnDestroy() naturally no-ops on every other machine.
    private BuffIconPD _buffIconInstance;

    protected override void OnApply()
    {
        Stats.AddDodgeListener(OnDodge);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDodgeListener(OnDodge);
        if (_buffIconInstance != null && BuffBarPD.instance != null)
            BuffBarPD.instance.RemoveBuffIcon(_buffIconInstance);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        // Same "fills UP as the buff's remaining time counts down" convention SurgeEnergySpentMoveSpeedAugment's
        // own icon uses.
        if (_buffIconInstance != null)
            _buffIconInstance.SetCooldownFill(1f - viewState.timer / duration);
    }

    private void OnDodge()
    {
        if (Stats == null || Stats.IsDead) return;
        // Only grants on the rising edge (see class doc comment) — a Dodge landing while the buff is already
        // up just refreshes the timer below, it doesn't stack a second copy of the bonus.
        if (currentState.timer <= 0f)
        {
            Stats.AddAttackSpeedFlat(attackSpeedPercent);
            if (isOwner && BuffBarPD.instance != null && _buffIconInstance == null)
                _buffIconInstance = BuffBarPD.instance.AddBuffIcon(buffIcon);
        }
        currentState.timer = duration;
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;
        if (state.timer <= 0f) return;

        state.timer -= delta;
        if (state.timer > 0f) return;

        state.timer = 0f;
        Stats.AddAttackSpeedFlat(-attackSpeedPercent);

        if (_buffIconInstance != null && BuffBarPD.instance != null)
        {
            BuffBarPD.instance.RemoveBuffIcon(_buffIconInstance);
            _buffIconInstance = null;
        }
    }
}
