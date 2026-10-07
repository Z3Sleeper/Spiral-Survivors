using PurrNet.Prediction;
using UnityEngine;

// A standalone ground hazard spawned by KingSpikePD's remote spike burst sequence — not IEnemyPD, not a
// weapon, just a short-lived spawned effect with a charge-then-detonate timer, same category as
// WeaponRangedProjectilePD ("deliberately not X"). Lives independently of the boss once spawned: it keeps
// ticking and will still detonate even if the boss that spawned it has since died.
//
// chargeTimer scales by _chargeAttackSpeed (2026-09-14) — a ONE-TIME snapshot of King Spike's own
// EffectiveAttackSpeed taken at Launch(), same "firer could die/get pooled-and-reused mid-charge, so snapshot
// rather than read a live reference" shape MortarHazardPD's own _windupAttackSpeed already uses (see that
// field's own doc comment for the full reasoning). Not read live off a stored King Spike reference on purpose.
public class RemoteSpikeBurstPD : PredictedIdentity<RemoteSpikeBurstPD.State>
{
    public struct State : IPredictedData<State>
    {
        public float chargeTimer;
        // Set once the charge timer expires; the actual hierarchy.Delete() is deferred to UpdateView() off
        // verified state, same pattern as WeaponRangedProjectilePD — deleting directly from LateSimulate()
        // would be acting on a not-yet-confirmed tick.
        public bool  expired;
        // Detonation radius/damage — live in State, not a plain field (2026-09-22), for the same reason
        // EnemyBaseState.scaledMaxHealth does: this burst can be created on a client via PredictedHierarchy
        // reconciling a misprediction rather than that machine's own local Launch() call, and a plain field
        // would then silently stay at its C# default (0) — a zero-radius, zero-damage detonation — until
        // (or unless) replication corrects it. Being in State means it's replicated as part of the same
        // bundle as chargeTimer, which already needs this protection.
        public float radius;
        public float damage;
        public void Dispose() { }
    }

    [SerializeField] private WindupFillPD _windupFill;

    // See SpearGloomPD._stabOverlapResults's own doc comment for the buffer-size reasoning.
    private static readonly Collider[] _detonationOverlapResults = new Collider[8];

    // This life's own total charge duration, captured once at Launch() — the denominator for UpdateView's
    // own live SetProgress() ratio (see its own doc comment for why this replaced a self-timed StartFill()).
    private float _chargeDuration;
    // One-time snapshot of King Spike's own EffectiveAttackSpeed at Launch() — see class doc comment for why
    // this is scaled-once, not read live.
    private float _chargeAttackSpeed = 1f;

    // Called by KingSpikePD.SpawnRemoteBurstWave()/SpawnPersistentRemoteBurst() (via the shared
    // SpawnRemoteBurstFor helper) immediately after hierarchy.Create() returns — synchronous and
    // deterministic, same "mutate currentState right after Create()" pattern as
    // XPOrbPD.SetValue()/WeaponRangedProjectilePD.Launch().
    public void Launch(float chargeTime, float radius, float damage, float chargeAttackSpeed = 1f)
    {
        currentState.radius = radius;
        currentState.damage = damage;
        _chargeDuration = chargeTime;
        _chargeAttackSpeed = Mathf.Max(0.0001f, chargeAttackSpeed);
        currentState.chargeTimer = chargeTime;

        // A pooled instance's PREVIOUS life ends with the fill fully complete (that's what a finished
        // windup looks like right before detonation) — WindupFillPD's own Awake() (where it resets to
        // empty) never re-fires on reuse, only on true first creation. UpdateView's own SetProgress() call
        // waits on verified.HasValue (see its own doc comment) rather than firing near-instantly off
        // viewState, so without this explicit reset a reused instance would visibly sit fully-filled for a
        // moment before that first verified snapshot arrives and progress actually starts moving.
        if (_windupFill != null) _windupFill.Hide();
    }

    protected override State GetInitialState() => default;

    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        state.chargeTimer -= delta * _chargeAttackSpeed;
        if (state.chargeTimer > 0f) return;

        // Physics.OverlapSphereNonAlloc against CombatLayerMasks.FriendlyHurtboxMask (2026-08-30, was a
        // manual FriendlyTargetRegistryPD distance check) — see SpearGloomPD.EnterStab's own doc comment for
        // why enemy AoE attacks now match every player weapon's own collider-query convention. Deliberately
        // kept as FriendlyHurtboxMask (any friendly), not players-only, even though KingSpikePD's own
        // spawn-target SELECTION (SpawnRemoteBurstWave/SpawnPersistentRemoteBurst) is players-only — per
        // explicit user direction, Remote Spike Burst only ever TARGETS (aims/spawns at) a player, but once
        // it's actually landed and detonating, it damages any friendly caught in the blast radius, same as
        // every other AoE on this boss.
        int n = Physics.OverlapSphereNonAlloc(transform.position, state.radius, _detonationOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _detonationOverlapResults[i].GetComponent<IFriendlyPD>();
            // No IsInvincible pre-check (2026-08-24 fix) — TakeDamage() already no-ops safely against
            // invincibility on its own, AND is the only place Dodge credit gets awarded (see
            // CharacterStatsPD.NotifyDodge's own doc comment); skipping the call here silently ate every
            // Dodge this attack should have granted while the player was dash-invincible.
            if (stats == null || stats.IsDead) continue;
            // No firer-identity field on this spawned burst — attacker passed default, matches this
            // interface param's "reserved for later, not wired to anything yet" status (see
            // IFriendlyPD.TakeDamage's own doc comment).
            stats.TakeDamage(state.damage, false, default);
        }

        state.expired = true;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Re-applied every frame, not just once at Launch() — if radius is ever changed mid-life, the
        // visible footprint (both the outline and the fill mesh) should never silently drift out of sync
        // with the actual AoE damage radius. centerForwardOffset left at its 0 default — this object's own
        // position already IS the center, fixed for its whole life once landed, never moves.
        if (_windupFill != null) _windupFill.ShowCircle(viewState.radius);

        // Live, every frame — see feedback_windupfill_self_timed_drift: a self-timed StartFill() runs on its
        // own Time.deltaTime clock, independent of the tick-based chargeTimer that actually gates the real
        // detonation in LateSimulate(), and can drift out of sync with it. SetProgress() instead reads the
        // live ratio directly, so it can't drift — it's a direct function of the same timer, not a second
        // independent countdown aimed at the same starting number.
        //
        // Gate reads verified (not viewState) — same reason as KingSpikePD's chargeDirection fix: viewState
        // linearly interpolates every STATE field by default, including plain floats like chargeTimer. Right
        // after Launch() sets currentState.chargeTimer to the real duration, viewState can still be blending
        // in from whatever this pooled instance's chargeTimer happened to be at the end of its PREVIOUS life
        // (typically near 0, since that's how the previous life expired) — which would make this ratio read
        // as "almost done" for a tick or two right after a fresh Launch(). verified is the exact,
        // non-interpolated snapshot, so it doesn't have that blending problem.
        // Gated on chargeTimer > 0, not just verified.HasValue — verified can legitimately turn non-null
        // for a tick or two before Launch()'s synchronous currentState.chargeTimer write has actually
        // propagated through to a verified snapshot, reading as the struct's default 0 in the meantime;
        // without this gate that transient would read as "already 100% done" instead of "not started yet".
        //
        // The actual RATIO, once gated open, reads viewState instead of verified — verified only updates as
        // often as a new snapshot gets confirmed, which reads noticeably steppier than viewState's own
        // every-render-frame interpolation. The blending risk above only exists in the brief window BEFORE
        // this gate opens (which is exactly what the gate is for); by the time verified confirms a real
        // positive chargeTimer, viewState's own interpolation has virtually always already caught up too
        // (same order-of-magnitude window), and Mathf.Clamp01 below floors/ceilings the rare stray frame
        // where it hasn't rather than showing something wildly wrong.
        if (_windupFill != null && verified.HasValue && verified.Value.chargeTimer > 0f)
            _windupFill.SetProgress(1f - Mathf.Clamp01(viewState.chargeTimer / _chargeDuration));

        // State-critical — only act on server-confirmed expiry, same reasoning as
        // WeaponRangedProjectilePD/EnemyBasePD's death handling.
        if (verified.HasValue && verified.Value.expired)
            hierarchy.Delete(this);
    }
}
