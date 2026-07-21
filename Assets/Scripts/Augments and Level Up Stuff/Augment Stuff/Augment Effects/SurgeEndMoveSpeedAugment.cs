using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. On Surge End, gain +60% Move Speed for 2 seconds — cut short immediately if a new
// Surge is started before the buff naturally expires, rather than stacking or refreshing. Buff icon is
// fully transient (added the instant the bonus activates, removed the instant it ends, whether by natural
// expiry or an early Surge-start cutoff) — unlike ShieldDamageReductionAugment's persistent "always shown"
// icon, which only ever sweeps a cooldown that's already visible beforehand.
public class SurgeEndMoveSpeedAugment : AugmentEffectPD<SurgeEndMoveSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // > 0 while the buff is active, ticking down toward 0 in Simulate().
        public float buffTimer;
        public void Dispose() { }
    }

    [SerializeField] private float moveSpeedBonus = 0.6f;
    [SerializeField] private float buffDuration    = 2f;

    [Header("Buff Bar")]
    [SerializeField] private Sprite buffIcon;

    // Plain, not State — mutated only from PredictedEvent listener callbacks (OnSurgeStarted/OnSurgeEnded
    // themselves guarantee exactly-once-per-real-occurrence, see CharacterStatsPD._onSurgeStarted/_onSurgeEnded's
    // own doc comments) or from Simulate() itself, same toggle-tracking shape as
    // ShieldDamageReductionAugment's own _reductionActive.
    private bool _bonusActive;

    // Spawned only while the buff is active — removed the instant it ends, unlike
    // ShieldDamageReductionAugment's persistent icon.
    private BuffIconPD _buffIconInstance;

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddSurgeEndedListener(OnSurgeEnded);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null)
        {
            Stats.RemoveSurgeStartedListener(OnSurgeStarted);
            Stats.RemoveSurgeEndedListener(OnSurgeEnded);
        }
        if (_buffIconInstance != null && BuffBarPD.instance != null)
            BuffBarPD.instance.RemoveBuffIcon(_buffIconInstance);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        // Inverted from a typical cooldown sweep (which starts full and empties) — this fills UP from 0 as
        // the buff's remaining time counts down, so it reads as "how much of the buff has elapsed" rather
        // than "how much is left."
        if (_buffIconInstance != null)
            _buffIconInstance.SetCooldownFill(1f - viewState.buffTimer / buffDuration);
    }

    private void OnSurgeStarted()
    {
        // A new Surge cuts the bonus short immediately instead of letting it run out on its own.
        currentState.buffTimer = 0f;
        EndBonus();
    }

    private void OnSurgeEnded()
    {
        currentState.buffTimer = buffDuration;
        if (_bonusActive) return;

        _bonusActive = true;
        Stats.AddMoveSpeedPercent(moveSpeedBonus);

        if (isOwner && BuffBarPD.instance != null && _buffIconInstance == null)
            _buffIconInstance = BuffBarPD.instance.AddBuffIcon(buffIcon);
    }

    // Only reverts the stat/icon — callers are responsible for zeroing buffTimer themselves (OnSurgeStarted
    // sets it directly; Simulate()'s natural-expiry path has already clamped it to 0 by the time this runs).
    private void EndBonus()
    {
        if (!_bonusActive) return;
        _bonusActive = false;
        Stats.AddMoveSpeedPercent(-moveSpeedBonus);

        if (_buffIconInstance != null && BuffBarPD.instance != null)
        {
            BuffBarPD.instance.RemoveBuffIcon(_buffIconInstance);
            _buffIconInstance = null;
        }
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (state.buffTimer <= 0f) return;

        state.buffTimer = Mathf.Max(0f, state.buffTimer - delta);
        if (state.buffTimer <= 0f)
            EndBonus();
    }
}
