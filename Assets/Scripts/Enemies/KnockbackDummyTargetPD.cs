using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Second variant of DummyTargetPD<TState> (2026-09-02) — independently supplies its own State (see that
// class's own doc comment for why this is the correct shape, not a concrete-to-concrete subclass), so its
// own knockbackTimer field costs nothing on the plain DummyTargetPD. Inherits the whole stationary/kinematic/
// CC-immune shell from DummyTargetPD<TState> as-is and adds only what's actually new: one continuously-
// repeating attack, a King-Orbiter-style radial knockback burst (Physics.OverlapSphereNonAlloc against
// CombatLayerMasks.FriendlyHurtboxMask, AddKnockbackVelocity per hit player — see KingOrbiterPD.FireKnockback
// for the shape this mirrors). No separate "cooldown" state at all, per explicit design — the instant one
// burst fires, the windup timer resets straight back to full and starts counting down again, forever, for as
// long as this dummy stays alive.
//
// Windup ticks via delta * EffectiveAttackSpeed, same as every other timed windup in this codebase — dummies
// already get their own attackSpeed field scaled by Threat Level through the generic EnemyBasePD.ConfigureSpawn
// composition (EventTargetScalingPD.ApplyTierScaling calls it same as EnemySpawnerPD does for real enemies),
// so this speeds up automatically at higher Threat Levels with no extra wiring needed here.
public class KnockbackDummyTargetPD : DummyTargetPD<KnockbackDummyTargetPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        // Counts down from knockbackWindupDuration to 0, fires the burst, then immediately resets back to
        // knockbackWindupDuration — no separate cooldown phase, see class doc comment.
        public float knockbackTimer;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    [Header("Knockback")]
    [SerializeField] private float knockbackWindupDuration = 10f;
    [SerializeField] private float knockbackForce           = 50f;
    // Multiplied by this enemy's own `size` at the actual overlap-check site (EnemyBasePD's generic
    // per-enemy scalar) — same "radius scales with Size" convention every other size-scaled AoE attack in
    // this codebase already follows (e.g. SpearGloomPD's own stab zone).
    [SerializeField] private float knockbackRadius          = 5f;

    [Header("Indicator")]
    // Both optional — this dummy only ever has the one state (always charging toward its next burst), so
    // unlike every multi-state enemy's own indicator, there's no show/hide toggling here: if wired up, it
    // just stays active and the fill loops 0->1 repeatedly for as long as this dummy is alive.
    [SerializeField] private GameObject knockbackIndicator;
    [SerializeField] private WindupFillPD knockbackFill;

    // See SpearGloomPD._stabOverlapResults's own doc comment for the buffer-size reasoning.
    private static readonly Collider[] _knockbackOverlapResults = new Collider[8];

    // Empty, not calling base — DummyTargetPD<TState> has no HandleActiveState body of its own to call (that
    // hook is left fully to each concrete variant, same as EnemyBasePD's own default would chase a target,
    // which no dummy variant wants). This drives the windup/fire cycle instead; HandleMovement is never
    // called here either, so this stays just as stationary as the plain dummy.
    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        state.knockbackTimer -= delta * EffectiveAttackSpeed;
        if (state.knockbackTimer > 0f) return;

        FireKnockback(ref state);
        // Immediately restart — no separate cooldown phase, see class doc comment.
        state.knockbackTimer = knockbackWindupDuration;
    }

    private void FireKnockback(ref State state)
    {
        Vector3 origin = transform.position;
        float radius = knockbackRadius * size;
        // Physics.OverlapSphereNonAlloc against CombatLayerMasks.FriendlyHurtboxMask — same convention every
        // enemy AoE attack in this codebase uses (see SpearGloomPD.EnterStab's own doc comment), mirroring
        // KingOrbiterPD.FireKnockback's own identical shape.
        int n = Physics.OverlapSphereNonAlloc(origin, radius, _knockbackOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _knockbackOverlapResults[i].GetComponent<IFriendlyPD>();
            if (stats == null || stats.IsDead) continue;
            Vector3 toPlayer = stats.transform.position - origin;
            Vector3 dir = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : Vector3.forward;
            stats.AddKnockbackVelocity(dir * knockbackForce);
        }
    }

    protected override void OnRespawnReset()
    {
        currentState.knockbackTimer = knockbackWindupDuration;
        if (knockbackFill != null) knockbackFill.Hide();
    }

    protected override void OnDeathStop()
    {
        if (knockbackIndicator != null) knockbackIndicator.SetActive(false);
        if (knockbackFill != null) knockbackFill.Hide();
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        // Active whenever alive, hidden once dead (2026-09-03 fix — was unconditionally SetActive(true)
        // every frame with no isDead check at all, so OnDeathStop's own hide got silently re-shown the very
        // next render frame during the death-hold window before actual deletion; King Spike's own
        // spikeBurstIndicator avoids this via an edge-detected _lastVisualState state-change guard — see
        // KingSpikePD.UpdateView's own comment — but this dummy only has one binary alive/dead condition to
        // gate on, not several mutually-exclusive states, so checking viewState.b.isDead directly every
        // frame is simpler and self-correcting rather than needing a one-shot transition guard).
        // 2026-09-02 fix preserved below this: was never actually turned on anywhere at all before that,
        // only ever turned off in OnDeathStop, so whether it showed while alive depended purely on the
        // prefab's own default active state instead of this script.
        if (knockbackIndicator != null)
        {
            knockbackIndicator.SetActive(!viewState.b.isDead);
            if (!viewState.b.isDead && knockbackFill != null)
                // Re-applied every frame, not just once at spawn — same "never silently drift out of sync
                // with the real hit-check" reasoning KingSpikePD's own spikeBurstIndicator uses.
                // centerForwardOffset left at its 0 default — knockbackIndicator's own current position
                // (WindupFillPD's own transform) already IS the center.
                knockbackFill.ShowCircle(knockbackRadius * size);
        }

        // Also gated on !isDead (2026-09-03, same fix as the indicator above) — knockbackTimer freezes the
        // instant b.isDead flips (base Simulate() returns early before HandleActiveState ever runs again),
        // so an ungated SetProgress here would keep re-displaying that one frozen value forever, undoing
        // OnDeathStop's own ResetFill() the very next frame.
        if (knockbackFill != null && !viewState.b.isDead)
            knockbackFill.SetProgress(1f - Mathf.Clamp01(viewState.knockbackTimer / knockbackWindupDuration));
    }
}
