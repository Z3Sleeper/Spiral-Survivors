using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Third variant of DummyTargetPD<TState> (2026-10-14) — same shape as KnockbackDummyTargetPD, just dealing
// AoE damage instead of a radial knockback burst. Independently supplies its own State (see
// DummyTargetPD<TState>'s own doc comment for why this is the correct shape, not a concrete-to-concrete
// subclass), so its own damageTimer field costs nothing on the plain DummyTargetPD or
// KnockbackDummyTargetPD. Inherits the whole stationary/kinematic/CC-immune shell from DummyTargetPD<TState>
// as-is and adds only what's actually new: one continuously-repeating attack, a King-Spike-style AoE burst
// (Physics.OverlapSphereNonAlloc against CombatLayerMasks.FriendlyHurtboxMask, IFriendlyPD.TakeDamage per hit
// player — see RemoteSpikeBurstPD's own identical TakeDamage call shape). No separate "cooldown" state at
// all, per explicit design — the instant one burst fires, the windup timer resets straight back to full and
// starts counting down again, forever, for as long as this dummy stays alive — same convention
// KnockbackDummyTargetPD already established.
//
// Damage itself reads GetDamage() (EnemyBasePD's own generic damage/EnrageDamageMultiplier/SetDamageOverride
// pipeline), not a separate dedicated field — same `damage` Inspector field every other enemy already
// tunes, automatically Threat-Level/tier-scaled the same generic way, with zero extra wiring needed here.
//
// Windup ticks via delta * EffectiveAttackSpeed, same as every other timed windup in this codebase — dummies
// already get their own attackSpeed field scaled by Threat Level through the generic EnemyBasePD.ConfigureSpawn
// composition (EventTargetScalingPD.ApplyTierScaling calls it same as EnemySpawnerPD does for real enemies),
// so this speeds up automatically at higher Threat Levels with no extra wiring needed here.
public class DamageDummyTargetPD : DummyTargetPD<DamageDummyTargetPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        // Counts down from damageWindupDuration to 0, fires the burst, then immediately resets back to
        // damageWindupDuration — no separate cooldown phase, see class doc comment.
        public float damageTimer;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    [Header("Damage")]
    [SerializeField] private float damageWindupDuration = 10f;
    // Multiplied by this enemy's own `size` at the actual overlap-check site (EnemyBasePD's generic
    // per-enemy scalar) — same "radius scales with Size" convention every other size-scaled AoE attack in
    // this codebase already follows (e.g. SpearGloomPD's own stab zone), and the exact same shape
    // KnockbackDummyTargetPD's own knockbackRadius already uses.
    [SerializeField] private float damageRadius = 5f;

    [Header("Indicator")]
    // Both optional — this dummy only ever has the one state (always charging toward its next burst), so
    // unlike every multi-state enemy's own indicator, there's no show/hide toggling here: if wired up, it
    // just stays active and the fill loops 0->1 repeatedly for as long as this dummy is alive.
    [SerializeField] private GameObject damageIndicator;
    [SerializeField] private WindupFillPD damageFill;

    // See SpearGloomPD._stabOverlapResults's own doc comment for the buffer-size reasoning.
    private static readonly Collider[] _damageOverlapResults = new Collider[8];

    // Empty, not calling base — DummyTargetPD<TState> has no HandleActiveState body of its own to call (that
    // hook is left fully to each concrete variant, same as EnemyBasePD's own default would chase a target,
    // which no dummy variant wants). This drives the windup/fire cycle instead; HandleMovement is never
    // called here either, so this stays just as stationary as the plain dummy.
    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        state.damageTimer -= delta * EffectiveAttackSpeed;
        if (state.damageTimer > 0f) return;

        FireDamage(ref state);
        // Immediately restart — no separate cooldown phase, see class doc comment.
        state.damageTimer = damageWindupDuration;
    }

    private void FireDamage(ref State state)
    {
        Vector3 origin = transform.position;
        float radius = damageRadius * size;
        // Physics.OverlapSphereNonAlloc against CombatLayerMasks.FriendlyHurtboxMask — same convention every
        // enemy AoE attack in this codebase uses (see SpearGloomPD.EnterStab's own doc comment), mirroring
        // KnockbackDummyTargetPD.FireKnockback's own identical shape.
        int n = Physics.OverlapSphereNonAlloc(origin, radius, _damageOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _damageOverlapResults[i].GetComponent<IFriendlyPD>();
            if (stats == null || stats.IsDead) continue;
            // isCrit always false, attacker passed default — same shape RemoteSpikeBurstPD's own identical
            // AoE-damage call uses (see IFriendlyPD.TakeDamage's own doc comment: neither is wired to
            // anything yet for an enemy-on-friendly hit).
            stats.TakeDamage(GetDamage(), false, default);
        }
    }

    protected override void OnRespawnReset()
    {
        currentState.damageTimer = damageWindupDuration;
        if (damageFill != null) damageFill.Hide();
    }

    protected override void OnDeathStop()
    {
        if (damageIndicator != null) damageIndicator.SetActive(false);
        if (damageFill != null) damageFill.Hide();
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        // Active whenever alive, hidden once dead — same fix/reasoning as KnockbackDummyTargetPD's own
        // identical guard (checking viewState.b.isDead directly every frame is simpler and self-correcting
        // rather than needing a one-shot transition guard, since this dummy only has one binary alive/dead
        // condition to gate on, not several mutually-exclusive states).
        if (damageIndicator != null)
        {
            damageIndicator.SetActive(!viewState.b.isDead);
            if (!viewState.b.isDead && damageFill != null)
                // Re-applied every frame, not just once at spawn — same "never silently drift out of sync
                // with the real hit-check" reasoning KingSpikePD's own spikeBurstIndicator uses.
                // centerForwardOffset left at its 0 default — damageIndicator's own current position
                // (WindupFillPD's own transform) already IS the center.
                damageFill.ShowCircle(damageRadius * size);
        }

        // Also gated on !isDead — damageTimer freezes the instant b.isDead flips (base Simulate() returns
        // early before HandleActiveState ever runs again), so an ungated SetProgress here would keep
        // re-displaying that one frozen value forever, undoing OnDeathStop's own ResetFill() the very next
        // frame.
        if (damageFill != null && !viewState.b.isDead)
            damageFill.SetProgress(1f - Mathf.Clamp01(viewState.damageTimer / damageWindupDuration));
    }
}
