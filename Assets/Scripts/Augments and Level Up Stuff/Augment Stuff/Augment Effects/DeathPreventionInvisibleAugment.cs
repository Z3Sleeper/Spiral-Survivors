using PurrNet.Prediction;
using UnityEngine;

// Passive: +20% (flat, not Total) Attack Speed permanently on pick, extended to a total +60% for the whole
// time the user is invisible, from ANY source (2026-08-31 — was +60% ONLY while invisible, no baseline at
// all; that left this augment's entire payoff locked behind actually going invisible, same "no immediate
// incentive" gap DeathPreventedBerserkAugment's own Max Energy grant fixes). The two numbers are meant to
// read as ONE bonus, not stacked — +20% baseline, growing to +60% (not +80%) while invisible; see
// ExtraAttackSpeedWhileInvisible's own doc comment for how that's computed. Stats.IsInvisible/
// _onInvisibleChanged is a single shared flag (CharacterStatsPD.GrantInvisibility), so the extra portion
// reacts identically whether invisibility came from this augment's own death-prevented grant below,
// RogueVanishAbilityPD, or any future source. The extra portion is granted immediately if already invisible
// at pick time, same "don't wait for the next natural transition" convention as DeathPreventionShieldDRAugment's
// own already-shielded check.
//
// Also activates CharacterStatsPD's Death Prevention mechanic (Stats.ActivateDeathPreventions() — see its
// own doc comment for the "already active elsewhere, shrink the grant timer instead" stacking rule). On a
// save landing (_onDeathPrevented), grants invisibility + a flat 5 Move Speed for 3 seconds.
//
// The buff timer is armed directly inside the PredictedEvent listener (OnDeathPrevented) — safe, since
// PredictedEvent guarantees a listener fires exactly once per real occurrence regardless of resimulation,
// same convention DeathPreventedBerserkAugment's own OnDeathPrevented reacts off. The actual grant/revert of
// the move speed bonus only happens off a CONFIRMED transition in verified state (UpdateView), not directly
// off the timer ticking in Simulate() — a raw Simulate()-detected transition isn't safe to act on directly
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

    // AddAttackSpeedFlat takes an intuitive fraction of baseAttackSpeed (e.g. 0.2 for +20%), despite the
    // "Flat" name — see that method's own doc comment. Not a Total-tier multiplier. Granted unconditionally,
    // permanently, the instant this augment is picked.
    [SerializeField] private float baseAttackSpeedFlat           = 0.2f;
    // The TOTAL desired Attack Speed bonus while invisible (not an additional stack on top of the baseline
    // above) — ExtraAttackSpeedWhileInvisible derives the actual incremental grant from the difference
    // between this and baseAttackSpeedFlat, so retuning either field independently keeps composing correctly.
    [SerializeField] private float attackSpeedFlatWhileInvisible = 0.6f;
    [SerializeField] private float onSaveMoveSpeedFlat                   = 5f;
    [SerializeField] private float onSaveDuration                       = 3f;

    // The actual amount granted/reverted by OnInvisibleChanged — see attackSpeedFlatWhileInvisible's own doc
    // comment for why this is a difference, not the raw field.
    private float ExtraAttackSpeedWhileInvisible => attackSpeedFlatWhileInvisible - baseAttackSpeedFlat;

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

        // Permanent baseline — see class doc comment.
        Stats.AddAttackSpeedFlat(baseAttackSpeedFlat);

        if (Stats.IsInvisible)
        {
            _attackSpeedGranted = true;
            Stats.AddAttackSpeedFlat(ExtraAttackSpeedWhileInvisible);
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
        Stats.AddAttackSpeedFlat(invisible ? ExtraAttackSpeedWhileInvisible : -ExtraAttackSpeedWhileInvisible);
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
            Stats.AddMoveSpeedFlat(onSaveMoveSpeedFlat);
        }
        else
        {
            // Only reverts the Move Speed half — invisibility itself is left to the shared timer (see class
            // doc comment), not explicitly turned off here.
            Stats.AddMoveSpeedFlat(-onSaveMoveSpeedFlat);
        }
    }
}
