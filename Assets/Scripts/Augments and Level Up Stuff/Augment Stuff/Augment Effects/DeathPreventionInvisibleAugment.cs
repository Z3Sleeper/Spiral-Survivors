using PurrNet.Prediction;
using UnityEngine;

// Passive: +25% Total Attack Speed for the whole time the user is invisible, from ANY source — Stats.
// IsInvisible/_onInvisibleChanged is a single shared flag (CharacterStatsPD.GrantInvisibility), so this
// reacts identically whether invisibility came from this augment's own death-prevented grant below,
// RogueVanishAbilityPD, or any future source. Granted immediately if already invisible at pick time, same
// "don't wait for the next natural transition" convention as DeathPreventionShieldDRAugment's own
// already-shielded check.
//
// Also activates CharacterStatsPD's Death Prevention mechanic (Stats.ActivateDeathPreventions() — see its
// own doc comment for the "already active elsewhere, shrink the grant timer instead" stacking rule). On a
// save landing (_onDeathPrevented), grants invisibility + 25% Total Move Speed for 3 seconds.
//
// The buff timer is armed directly inside the PredictedEvent listener (OnDeathPrevented) — safe, since
// PredictedEvent guarantees a listener fires exactly once per real occurrence regardless of resimulation,
// same convention ShieldBreakFrenzyAugment's own (now-reworked) buff used. The actual grant/revert of the
// move speed bonus only happens off a CONFIRMED transition in verified state (UpdateView), not directly off
// the timer ticking in Simulate() — a raw Simulate()-detected transition isn't safe to act on directly
// (double-fire risk during a client's own in-place resimulation pass, see AugmentEffectPD's own doc
// comment) — guarded by _moveSpeedGrantedView so it fires exactly once per real activation and once per
// real expiry. Invisibility itself is only ever GRANTED here (a duration contribution, on the rising edge),
// never explicitly revoked — CharacterStatsPD's own shared invisibleTimer handles turning it back off
// naturally, so this augment's own 3s window ending can't cut short some OTHER overlapping invisibility
// source (see GrantInvisibility's own doc comment).
public class DeathPreventionInvisibleAugment : AugmentEffectPD<DeathPreventionInvisibleAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // > 0 while the post-save move-speed buff is active, ticking down toward 0 in Simulate(). "Active"
        // is just buffTimer > 0 — no separate bool needed. Invisibility's own duration is tracked
        // independently by CharacterStatsPD's shared invisibleTimer, not by this.
        public float buffTimer;
        public void Dispose() { }
    }

    [SerializeField] private float attackSpeedTotalPercentWhileInvisible = 0.25f;
    [SerializeField] private float onSaveMoveSpeedTotalPercent           = 0.25f;
    [SerializeField] private float onSaveDuration                       = 3f;

    // Plain, not State — mutated only from the _onInvisibleChanged listener callback, which (like every
    // PredictedEvent in this class) fires exactly once per real occurrence regardless of resimulation.
    private bool _attackSpeedGranted;

    // Tracks whether the CONFIRMED (verified) post-save move-speed buff is currently granted — mutated only
    // from UpdateView() off verified state, so grant/revert each only ever fire once per real occurrence.
    private bool _moveSpeedGrantedView;

    protected override void OnApply()
    {
        Stats.AddInvisibleChangedListener(OnInvisibleChanged);
        Stats.AddDeathPreventedListener(OnDeathPrevented);
        Stats.ActivateDeathPreventions();

        if (Stats.IsInvisible)
        {
            _attackSpeedGranted = true;
            Stats.AddAttackSpeedTotalPercent(attackSpeedTotalPercentWhileInvisible);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveInvisibleChangedListener(OnInvisibleChanged);
        Stats.RemoveDeathPreventedListener(OnDeathPrevented);
    }

    private void OnInvisibleChanged(bool invisible)
    {
        if (invisible == _attackSpeedGranted) return;
        _attackSpeedGranted = invisible;
        Stats.AddAttackSpeedTotalPercent(invisible ? attackSpeedTotalPercentWhileInvisible : -attackSpeedTotalPercentWhileInvisible);
    }

    private void OnDeathPrevented() => currentState.buffTimer = onSaveDuration;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.buffTimer > 0f)
            state.buffTimer = Mathf.Max(0f, state.buffTimer - delta);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        bool active = verified.Value.buffTimer > 0f;
        if (active == _moveSpeedGrantedView) return;
        _moveSpeedGrantedView = active;

        if (active)
        {
            // GrantInvisibility fires _onInvisibleChanged itself (rising edge) — OnInvisibleChanged above
            // reacts to it, so the Attack Speed passive composes automatically with no extra call here.
            Stats.GrantInvisibility(onSaveDuration);
            Stats.AddMoveSpeedTotalPercent(onSaveMoveSpeedTotalPercent);
        }
        else
        {
            // Only reverts the Move Speed half — invisibility itself is left to the shared timer (see class
            // doc comment), not explicitly turned off here.
            Stats.AddMoveSpeedTotalPercent(-onSaveMoveSpeedTotalPercent);
        }
    }
}
