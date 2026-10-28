using PurrNet.Prediction;
using UnityEngine;

// Rogue Spin-specific augment (2026-10-04, revised twice same day — see git history for the original
// single-timer-refresh draft, then the plain-stacking-no-StackPower draft replaced with this one).
// This augment's own .asset must set requiredAbilities = [RogueSpin] — scripts-only convention, that part
// is Editor-side, not here.
//
// Grants a stack of +attackSpeedBonusPercent (30% default, Bonus tier via AddAttackSpeedFlat, NOT Total —
// see that method's own doc comment) for a fixed duration (3s default) starting the instant
// RogueSpinAbilityPD's own leap ENDS. A buff DURING the leap itself would be wasted anyway — weapons are
// disabled for the ability's whole active window (SetWeaponsDisabled(true)), so Attack Speed has nothing
// to act on until the leap is over. Genuine stacking effect (2026-10-04 revision) — each active stack's own
// contribution is multiplied by Stats.StackPower, same "perStackAmount * stackCount * Stats.StackPower"
// shape MissingHealthMoveSpeedStackingAugment already uses, recomputed live every tick so a StackPower
// change mid-buff retroactively rescales every currently-active stack, not just future ones.
//
// Capped at 5 CONCURRENT stacks, each independently timed — tracked via 5 individually-NAMED slot timers
// (stackTimer0-4), NOT a float[] array: PredictedIdentityEditor throws "Unsupported type" on any
// array-of-struct field (confirmed on Vector2[]/Vector3[], see
// feedback_purrnet_predictedidentity_vector2_unsupported memory), and float is also a value type under the
// hood, so named fields play it safe. When a 6th stack would-be grant arrives and all 5 slots are already
// occupied, it REPLACES whichever slot has the LEAST remaining time (soonest to expire anyway) by resetting
// that slot to a fresh `duration` — explicit design call (2026-10-04): a true unbounded/dynamic-count
// structure isn't possible here at all (every State struct in this codebase is a fixed-layout value type;
// a List<T> or similar growable collection can't be deep-copied/rolled back correctly by PurrDiction's own
// snapshot system), so 5 is a genuine hard cap, not a soft one — and "replace the weakest" means a 6th
// cast is never simply wasted/dropped the way the very first draft's soft-cap did.
//
// Pattern C (see KnightBerserkMoveSpeedAugment's own doc comment for Patterns A/B) — the whole timed-stack
// system lives in the augment, triggered by the EXISTING shared CharacterStatsPD._onAbilityEnd event
// (AddAbilityEndListener, same event PhantomBladeOnAbilityStartAugment already consumes off the sibling
// _onAbilityStart) filtered by ability.Identity == RogueSpin, zero changes to RogueSpinAbilityPD itself.
// Also means an INTERRUPTED leap correctly does NOT grant/refresh a stack (NotifyAbilityEnd is only ever
// called from RogueSpinAbilityPD's own non-interrupted end path — "reward on completion" listeners must
// not pay out on a punished interruption, per that file's own doc comment).
//
// Per AugmentEffectPD's own doc comment, a discrete one-shot Stats.AddAttackSpeedFlat call is NOT safe
// directly from Simulate() (risks double-firing during the client's own resimulation/reconciliation pass).
// So — same shape as MissingHealthMoveSpeedStackingAugment — Simulate() only ever recomputes
// state.desiredAmount fully and fresh every tick (a pure function of the 5 timers' own live state plus
// Stats.StackPower, safe to read directly), and the actual Stats.AddAttackSpeedFlat call only ever fires
// from UpdateView(), off verified state, for just the delta since last applied.
public class RogueSpinAttackSpeedAugment : AugmentEffectPD<RogueSpinAttackSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float stackTimer0;
        public float stackTimer1;
        public float stackTimer2;
        public float stackTimer3;
        public float stackTimer4;
        // Live-recomputed every tick in Simulate() — see class doc comment.
        public float desiredAmount;
        public void Dispose() { }
    }

    public float attackSpeedBonusPercent = 0.3f;
    public float duration = 3f;

    [Header("Buff Bar")]
    [SerializeField] private Sprite buffIcon;

    // Tracks how much has actually been granted via Stats.AddAttackSpeedFlat so far — mutated only from
    // UpdateView() off verified state, so the delta applied each time is always correct regardless of how
    // many times a given tick gets resimulated. Same role as MissingHealthMoveSpeedStackingAugment's own
    // _appliedAmount.
    private float _appliedAmount;

    // ONE icon represents all 5 possible stacks (not one icon per stack) — spawned the instant the FIRST
    // stack goes up, removed the instant the LAST one falls off, same transient add-on-grant/
    // remove-on-expiry shape as OnDodgeGainAttackSpeedAugment's own single-buff icon. Only ever non-null on
    // the owning client (see OnAbilityEnd's isOwner gate below), so the ungated removal in Simulate()
    // naturally no-ops on every other machine.
    private BuffIconPD _buffIconInstance;

    protected override void OnApply()
    {
        Stats.AddAbilityEndListener(OnAbilityEnd);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityEndListener(OnAbilityEnd);
        if (_buffIconInstance != null && BuffBarPD.instance != null)
            BuffBarPD.instance.RemoveBuffIcon(_buffIconInstance);
    }

    private void OnAbilityEnd(IAbilityPD ability)
    {
        if (Stats == null || Stats.IsDead || ability.Identity != AbilityIdentity.RogueSpin) return;
        StartOrReplaceStack(ref currentState);
        if (isOwner && BuffBarPD.instance != null && _buffIconInstance == null)
            _buffIconInstance = BuffBarPD.instance.AddBuffIcon(buffIcon);
    }

    // Starts a stack in the first empty (<=0) slot. If all 5 are already occupied, replaces whichever slot
    // has the LEAST remaining time (soonest to expire anyway) — see class doc comment for why this is a
    // hard cap with eviction, not a soft cap that silently drops the 6th cast.
    private void StartOrReplaceStack(ref State state)
    {
        if (state.stackTimer0 <= 0f) { state.stackTimer0 = duration; return; }
        if (state.stackTimer1 <= 0f) { state.stackTimer1 = duration; return; }
        if (state.stackTimer2 <= 0f) { state.stackTimer2 = duration; return; }
        if (state.stackTimer3 <= 0f) { state.stackTimer3 = duration; return; }
        if (state.stackTimer4 <= 0f) { state.stackTimer4 = duration; return; }

        int shortestIndex = 0;
        float shortestValue = state.stackTimer0;
        if (state.stackTimer1 < shortestValue) { shortestValue = state.stackTimer1; shortestIndex = 1; }
        if (state.stackTimer2 < shortestValue) { shortestValue = state.stackTimer2; shortestIndex = 2; }
        if (state.stackTimer3 < shortestValue) { shortestValue = state.stackTimer3; shortestIndex = 3; }
        if (state.stackTimer4 < shortestValue) { shortestValue = state.stackTimer4; shortestIndex = 4; }

        switch (shortestIndex)
        {
            case 0: state.stackTimer0 = duration; break;
            case 1: state.stackTimer1 = duration; break;
            case 2: state.stackTimer2 = duration; break;
            case 3: state.stackTimer3 = duration; break;
            case 4: state.stackTimer4 = duration; break;
        }
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null) return;

        state.stackTimer0 = Mathf.Max(0f, state.stackTimer0 - delta);
        state.stackTimer1 = Mathf.Max(0f, state.stackTimer1 - delta);
        state.stackTimer2 = Mathf.Max(0f, state.stackTimer2 - delta);
        state.stackTimer3 = Mathf.Max(0f, state.stackTimer3 - delta);
        state.stackTimer4 = Mathf.Max(0f, state.stackTimer4 - delta);

        int activeStacks = 0;
        if (state.stackTimer0 > 0f) activeStacks++;
        if (state.stackTimer1 > 0f) activeStacks++;
        if (state.stackTimer2 > 0f) activeStacks++;
        if (state.stackTimer3 > 0f) activeStacks++;
        if (state.stackTimer4 > 0f) activeStacks++;

        state.desiredAmount = activeStacks * attackSpeedBonusPercent * Stats.StackPower;

        // Last stack just fell off — remove the single shared icon (see _buffIconInstance's own doc
        // comment). Naturally no-ops on every non-owning machine, where _buffIconInstance is always null.
        if (activeStacks == 0 && _buffIconInstance != null && BuffBarPD.instance != null)
        {
            BuffBarPD.instance.RemoveBuffIcon(_buffIconInstance);
            _buffIconInstance = null;
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredAmount;
        if (!Mathf.Approximately(desired, _appliedAmount))
        {
            Stats.AddAttackSpeedFlat(desired - _appliedAmount);
            _appliedAmount = desired;
        }

        if (_buffIconInstance == null) return;

        // "Duration" for the fill means the NEWEST stack's own countdown — whichever slot was most
        // recently (re)started always holds the LARGEST remaining value among the 5 (it has the most time
        // left by definition, since every slot decays at the same rate from the same `duration`), so the
        // max across all 5 IS the newest stack's own remaining time, no separate "which slot is newest"
        // bookkeeping needed. Off viewState (cosmetic only), same tolerance as every other SetCooldownFill
        // call in this codebase.
        float maxRemaining = viewState.stackTimer0;
        if (viewState.stackTimer1 > maxRemaining) maxRemaining = viewState.stackTimer1;
        if (viewState.stackTimer2 > maxRemaining) maxRemaining = viewState.stackTimer2;
        if (viewState.stackTimer3 > maxRemaining) maxRemaining = viewState.stackTimer3;
        if (viewState.stackTimer4 > maxRemaining) maxRemaining = viewState.stackTimer4;
        _buffIconInstance.SetCooldownFill(duration > 0f ? 1f - maxRemaining / duration : 0f);

        // Stack count printed at the bottom only once 2+ are active — a single stack just shows the bare
        // icon, per design spec.
        int activeStacks = 0;
        if (viewState.stackTimer0 > 0f) activeStacks++;
        if (viewState.stackTimer1 > 0f) activeStacks++;
        if (viewState.stackTimer2 > 0f) activeStacks++;
        if (viewState.stackTimer3 > 0f) activeStacks++;
        if (viewState.stackTimer4 > 0f) activeStacks++;
        _buffIconInstance.SetStackText(activeStacks >= 2 ? activeStacks.ToString() : "");
    }
}
