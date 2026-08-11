using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Rogue's first class ability, built on the IAbilityPD architecture — gated by a 10s cooldown (Slot is
// Ability1), same gating shape as its sibling RogueVanishAbilityPD. The "somersault" leap-dash movement,
// plus a periodic AoE damage tick around the player — deliberately NOT the AreaEffectSize/Power/Count "AoE
// Effects" system, no VFX, just damage.
public class RogueSpinAbilityPD : PredictedIdentity<RogueSpinAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public bool  isActive;
        public float activeTimer;
        public float cooldownTimer;
        // How many of the _effectiveHitCount scheduled hits have fired so far — see HitTime()'s own doc
        // comment for how their timing is derived from this and duration.
        public int   hitsFired;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Spin Slash";
    [SerializeField, TextArea] private string description = "Leap forward, dealing damage in a burst around you as your weapons spin.";
    public float cooldown = 10f;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full
    // value throughout the leap) — the cooldown reflects "time since the ability ended". true: cooldown
    // starts ticking immediately at cast, running concurrently with the active window — see Simulate().
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability1;
    [SerializeField] private string[] tags;
    // true (default) — replaces this ability's own former hand-rolled ForceEndSurge() call (Surge starting
    // a NEW one was already separately blocked via IsWeaponsDisabled, but nothing previously stopped an
    // in-progress one from being force-ended AND immediately re-blocked at the counter level — see
    // IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract this now goes through instead).
    [SerializeField] private bool activeSurgeBlock = true;

    [Header("Movement")]
    // Overall active window for the whole ability — movement, invincibility, weapons-disabled, damage
    // ticking, all of it. Also the distance-vs-speed pairing for the leap-dash itself (see
    // PlayerControllerPD.StartSpecialMovement's own comment): distance = dashSpeed * duration.
    [SerializeField] private float duration = 0.5f;
    // Small extra invincibility window tacked on AFTER the active window itself ends — landing in the middle
    // of a mob is the whole point of this ability, so this gives a beat to react (e.g. dash away, which is
    // already unlocked the instant the active window ends) without being punished by a stray hit landing
    // right as the leap itself ends. Folded into the SAME GrantInvincibility(duration + grace) call at cast
    // time (see Activate()) rather than a second call at end-of-active — two separate calls each round their
    // own `duration / _currentDelta` tick count independently, and the first one's rounding could hit 0 a
    // tick before activeTimer itself did, leaving a one-tick gap in the invincibility shell before the second
    // call re-armed it. One combined grant has no seam to gap at. Deliberately NOT added to `duration` itself/
    // currentState.activeTimer — AbilityIsActive/IsActive (and therefore the lockout, cooldown-start, etc.)
    // still flip the instant the real active window ends; only the invincibility timer covers the extra beat.
    [SerializeField] private float postActiveInvincibilityGrace = 0.1f;
    // Flat units/sec speed for the leap-dash itself — NOT derived from the player's own MoveSpeed (same
    // "flat, not scaling with MoveSpeed buffs" reasoning as PlayerControllerPD.DashSpeed), owned/tunable
    // here rather than hardcoded inside PlayerControllerPD.StartSpecialMovement (that method only knows HOW
    // to move the player, never the actual tuned value). 50 matches DashSpeed's own default, so the leap
    // covers the same ground a real dash of the same duration would.
    [SerializeField] private float dashSpeed = 50f;

    [Header("Visuals")]
    // The whole-body child raised in Y for the leap arc — same sine-arc formula as LeaperGloomPD's own
    // leap (arcY = sin(t*PI) * height), just driven by this ability's own activeTimer/duration instead of
    // a leap-specific state timer.
    [SerializeField] private Transform visuals;
    [SerializeField] private float leapHeight = 1f;

    [Header("Damage")]
    // Capsule radius = this * the player's own Size (bonusSizePercent) — see DealDamage()'s own doc comment
    // for the capsule's length (reaches out to the current weapon's real range, not tuned here).
    [SerializeField] private float damageRadiusSizeMultiplier = 1.25f;
    // Flat degrees/sec weapons spin at for the whole ability, overriding the normal AttackSpeed-driven rate —
    // see CharacterStatsPD.specialSpinRate's own doc comment. Purely visual flare (weapons are already
    // disabled for hit purposes) — unrelated to damage timing below, which is scheduled by hitCountPerWeapon
    // instead. Default 1080 = 3 full spins/sec (1080 / 360).
    [SerializeField] private float spinRate = 1080f;
    // Base hit count for a single weapon — the REAL hit count scales linearly with WeaponManagerPD.
    // WeaponCount at cast time (see _effectiveHitCount/Activate()), while each hit's own damage (BaseDamage())
    // reads the normal, already-weapon-count-and-Multishot-divided Strength stat, so total damage over the
    // whole burst stays roughly constant regardless of weapon count — only how finely it's split across hits
    // changes (10 base + 2 weapons = 20 hits at half damage each; 10 base + 4 weapons = 40 hits at quarter
    // damage each). Hits land evenly spaced across the whole duration — one at t=0 (cast), one at t=duration
    // (ability end), the rest spread between. See HitTime()'s own doc comment for the exact spacing.
    public int hitCountPerWeapon = 6;
    // Flat multiplier on each hit's damage — base damage per hit is Strength * AttackSpeed * this (see
    // BaseDamage()'s own doc comment).
    public float spinAbilityDamageMultiplier = 1f;

    // Total damage across the whole burst at current stats — hitCountPerWeapon * current TotalWeaponCount
    // (mirrors _effectiveHitCount's own Activate()-time formula, just read live off CURRENT weapon count
    // rather than a cast-time snapshot, since this previews "what would happen if cast right now") times
    // BaseDamage()'s own per-hit formula. Baseline (unrolled), same convention as every other Live*Ratio.
    public string LiveDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(hitCountPerWeapon * Mathf.Max(1, stats.TotalWeaponCount) * stats.Strength * stats.AttackSpeed * spinAbilityDamageMultiplier * stats.AbilityPower)} "
            : "";

    private CharacterStatsPD    _stats;
    private PlayerControllerPD  _controller;
    private WeaponManagerPD     _weaponManager;
    private Vector3    _visualsRestLocalPosition;
    // Captured once at cast time (same moment StartSpecialMovement itself locks in specialVelocity from the
    // same currentState.cursorDir) rather than re-read every frame — the ability's travel direction is
    // fixed for its whole duration, and re-reading live input mid-ability could disagree with the direction
    // actually being moved in if the player moved the cursor after casting.
    private float _facingYaw;
    // Farthest current weapon reach (WeaponManagerPD.TryGetFarthestWeaponReach — the exact same "real hitbox
    // edge, tip offset included" value PlayerRangeIndicatorPD's own ring already shows), captured once at
    // cast time alongside _facingYaw for the same reason: fixed for the whole active window, and re-reading
    // live mid-ability could disagree with the length the damage capsule was actually cast at. Falls back to
    // the plain WeaponRange stat if no weapon is currently registered to measure from.
    private float _damageReach;
    // CharacterStatsPD.TotalWeaponCount at cast time (post-ForceEndSurge, see Activate()'s own comment),
    // floored to 1 — captured once alongside the above (same "fixed for the whole active window" reasoning)
    // rather than read live, so a weapon count that changes mid-burst can't desync HitTime()'s schedule from
    // how many hits it was actually built for. Only drives _effectiveHitCount now — BaseDamage()/
    // OnHitPowerForEffects() read the normal Strength/OnHitPower stats directly instead, which do their own
    // (live) weapon-count/Multishot divide internally (see BaseDamage()'s own doc comment).
    private int _weaponCountAtCast;
    // hitCountPerWeapon * _weaponCountAtCast, floored to 1 — see hitCountPerWeapon's own doc comment.
    private int _effectiveHitCount;

    // Scratch buffers — shared, immediately drained within one synchronous DealDamage() call, same
    // "shared, never held across calls" convention as PhantomBladeSummonHelper/AreaEffectCastHelper's own.
    private static readonly Collider[] _damageOverlapResults = new Collider[128];
    private readonly HashSet<IEnemyPD>        _damageTouchedThisTick        = new();
    private readonly HashSet<CharacterStatsPD> _damageTouchedAlliesThisTick = new();

    protected override State GetInitialState() => default;

    public Sprite Icon => icon;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
        _controller = GetComponent<PlayerControllerPD>();
        _weaponManager = GetComponent<WeaponManagerPD>();
        if (visuals != null) _visualsRestLocalPosition = visuals.localPosition;
    }

    // _controller's own _onSpecialMovementInterrupted PredictedEvent isn't constructed until
    // PlayerControllerPD's own LateAwake() runs — but PurrDiction's Setup() calls each sibling component's
    // Awake()+LateAwake() pair sequentially (per-component, not as two separate global passes across every
    // component first), so there's no reliable ordering between two DIFFERENT components' own LateAwake()
    // calls on the same GameObject either — confirmed live (this exact NullReferenceException still hit
    // from LateAwake(), just one component earlier in the crash than the original Awake()-based attempt).
    // Subscribing lazily on first Activate() instead sidesteps the ordering question entirely: by the time a
    // player can press this ability, the whole object graph has been through at least one full simulate
    // tick, so PlayerControllerPD.LateAwake() is guaranteed to have already run regardless of component
    // order. Safe to defer this long — OnSpecialMovementInterrupted only ever matters while currentState.
    // isActive is true, which can't happen before Activate() runs anyway.
    private bool _subscribedToSpecialMovementInterrupted;
    private void EnsureSpecialMovementInterruptedSubscription()
    {
        if (_subscribedToSpecialMovementInterrupted || _controller == null) return;
        _controller.AddSpecialMovementInterruptedListener(OnSpecialMovementInterrupted);
        _subscribedToSpecialMovementInterrupted = true;
    }

    public bool IsReady => !currentState.isActive && currentState.cooldownTimer <= 0f;
    public bool IsActive => currentState.isActive;
    // Moves the player (StartSpecialMovement), disables weapons, flips the orbit — this locks dash and the
    // other ability slot out for its whole active window (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => true;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    // Same underlying window as IsActive for this ability (the whole leap) — see IAbilityPD.AbilityIsActive's
    // own doc comment for why these are separate interface members even though they agree here.
    public bool AbilityIsActive => currentState.isActive;
    public bool HasActiveWindow => true;
    public float ActiveTimeRemaining => Mathf.Max(0f, currentState.activeTimer);
    // No AbilityPower scaling on this ability's own duration (see Activate() — currentState.activeTimer is
    // set straight from duration, unlike the buff abilities' own AbilityPower-scaled snapshots), so the raw
    // field is already the correct total, no separate _appliedDuration needed.
    public float ActiveTimeDuration => duration;
    public float CooldownRemaining => Mathf.Max(0f, currentState.cooldownTimer);
    public float CooldownDuration => EffectiveCooldown;
    public float BaseCooldownDuration => cooldown;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;

    // Same sine-arc formula as LeaperGloomPD's own leap.
    private float ComputeArcY(float activeTimer) =>
        Mathf.Sin((1f - Mathf.Clamp01(activeTimer / duration)) * Mathf.PI) * leapHeight;

    // Elapsed-time (seconds since cast) a given hit index should fire at — _effectiveHitCount points evenly
    // spaced across [0, duration] inclusive, so index 0 is t=0 (cast) and index _effectiveHitCount-1 is
    // t=duration (burst end). _effectiveHitCount<=1 collapses to a single hit at t=0.
    private float HitTime(int index) =>
        _effectiveHitCount > 1 ? duration * index / (_effectiveHitCount - 1) : 0f;

    // Same formula as CharacterStatsPD.DashCooldown (baseCooldown / (1 + haste/100)) — a haste augment
    // shortens this ability's cooldown the same way it already does for dash, or lengthens it if
    // EffectiveAbilityHaste ever goes negative (a penalty).
    private float EffectiveCooldown => _stats != null ? cooldown / (1f + _stats.EffectiveAbilityHaste / 100f) : cooldown;

    // Mirrors EffectiveCooldown's own formula exactly. Outside a live match, falls back to the base field
    // itself — exact at neutral EffectiveAbilityHaste (0, i.e. cooldown / (1+0/100) == cooldown), same
    // "neutral stat value == the base field" convention as every other Live*Ratio in this file.
    public string LiveCooldownRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? cooldown / (1f + stats.EffectiveAbilityHaste / 100f)
            : cooldown;
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(value)} ";
    }

    // Mirrors Simulate()'s own tick condition exactly (see that method's own comment) — a cooldown-reduction
    // effect should only ever eat into time that's ACTUALLY counting down right now, not a cooldownTimer
    // that's merely sitting at its full pre-loaded value while the active window (cooldownStartsOnCast=false)
    // hasn't ended yet to let it start ticking.
    private bool IsCooldownActivelyTicking =>
        currentState.cooldownTimer > 0f && (cooldownStartsOnCast || !currentState.isActive);

    // See IAbilityPD's own doc comments for the exact contract of each of these three.
    public void ReduceCooldownFlat(float seconds)
    {
        if (!IsCooldownActivelyTicking) return;
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer - seconds);
    }
    public void ReduceCooldownPercentRemaining(float percent01)
    {
        if (!IsCooldownActivelyTicking) return;
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer * (1f - Mathf.Clamp01(percent01)));
    }
    public void ReduceCooldownPercentTotal(float percent01)
    {
        if (!IsCooldownActivelyTicking) return;
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer - CooldownDuration * Mathf.Clamp01(percent01));
    }

    public void Activate()
    {
        if (!IsReady || _stats == null) return;

        EnsureSpecialMovementInterruptedSubscription();

        currentState.isActive      = true;
        currentState.activeTimer   = duration;
        currentState.cooldownTimer = EffectiveCooldown;
        currentState.hitsFired     = 0;
        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cycle (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);

        // Surge force-ended (and blocked for the rest of the active window) FIRST, before any cast-time
        // snapshot below — not just requested, so it's guaranteed off the instant the ability starts even if
        // Surge only just began and would normally still be inside its own minimum-duration window. Every
        // stat this method snapshots (weapon count especially — SurgeWeaponCountAugment grants extra weapons
        // only while surging) must reflect the POST-surge-end value, or a player who happened to be surging
        // at cast time would get a permanently wrong (too-high) snapshot for the rest of the burst. See
        // IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract this is now going through
        // (RemoveSurgeBlock's matching call sits in Simulate()'s own end-of-active block below).
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }

        // Cursor direction, not held movement direction — this ability casts TOWARD wherever the cursor
        // currently is, not literally to that point (dashSpeed/duration below still solely determine how
        // far the leap actually covers, same as before; only the direction source changed).
        Vector3 dir = _controller != null ? _controller.currentState.cursorDir : Vector3.zero;
        _facingYaw = dir != Vector3.zero ? Quaternion.LookRotation(dir).eulerAngles.y : 0f;

        _damageReach = _weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float reach)
            ? reach
            : _stats.WeaponRange;

        // CharacterStatsPD.TotalWeaponCount (a pure stat, instant/deterministic) instead of
        // _weaponManager.WeaponCount (the actual spawned GameObject count) — WeaponManagerPD only syncs
        // spawned weapons to the stat once per real Unity LateUpdate(), so reading it here could still see
        // last frame's (pre-ForceEndSurge) count for up to a frame. Also folds in weaponTracks (OuterTrack's
        // second ring), which the plain spawned-weapon count on one manager wouldn't represent either.
        _weaponCountAtCast = Mathf.Max(1, _stats.TotalWeaponCount);
        _effectiveHitCount = Mathf.Max(1, hitCountPerWeapon * _weaponCountAtCast);

        // Set once here (deterministic Activate() context, called only from PlayerControllerPD's own
        // Simulate()) rather than every frame from UpdateView() — WeaponBasePD.OrbitalPosition now reads
        // this from each weapon's own deterministic LateSimulate() (to orient the flipped orbit plane to the
        // travel direction), so it needs to be just as deterministic as _facingYaw itself. Fixed for the
        // whole active window, same as _facingYaw, so setting it once here is the whole story.
        _stats.SetOrbitFacingYaw(_facingYaw);

        // Flat rate, not AttackSpeed-derived — see CharacterStatsPD.specialSpinRate's own doc comment. Set
        // once here; SetUseSpecialSpinRate(true) actually turns it on (the missing piece that made this a
        // no-op before — turning it back off in Simulate() at ability end was already there, but nothing
        // ever turned it ON).
        _stats.SetSpecialSpinRate(spinRate);
        _stats.SetUseSpecialSpinRate(true);

        // Combined grant — covers the active window AND the post-active grace in one call, see
        // postActiveInvincibilityGrace's own doc comment for why this isn't split into two calls. Committed
        // channel, not the general GrantInvincibility pool — see CharacterStatsPD.GrantCommittedInvincibility's
        // own doc comment. Lets an Interrupt() revoke exactly this grant without touching any other
        // overlapping invincibility source — see OnSpecialMovementInterrupted below.
        _stats.GrantCommittedInvincibility(duration + postActiveInvincibilityGrace);
        _stats.SetWeaponsDisabled(true);
        // Separate from SetWeaponsDisabled — this one only ever affects WeaponBasePD.OrbitalPosition's
        // orbit-plane shape (see its own doc comment), never damage. Kept distinct so a future effect can
        // disable weapon damage without also flipping the orbit, or vice versa.
        _stats.SetOrbitFlipped(true);
        _controller?.StartSpecialMovement(duration, dashSpeed, dir);
        // This leap functions like a dash — fires the same OnDashStarted listeners a real dash would
        // (DashExplosionAugment, DashHealAugment, etc.), matched by NotifyDashEnded below.
        _controller?.NotifyDashStarted();
    }

    public void NotifyActivationBlocked() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole active
        // window below) or only once that window ends (state.isActive false) — see the field's own doc
        // comment. Either way Activate() already set cooldownTimer to its full value at cast time.
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive))
            state.cooldownTimer -= delta;

        if (!state.isActive) return;

        // Death force-ends the leap immediately, skipping straight to the same end-of-active reset below —
        // PlayerControllerPD already halts the leap's own MOVEMENT the instant it sees IsDead (zeroes
        // velocity, clears isSpecialMoving), but nothing previously reset THIS ability's own side effects
        // (weapons disabled, orbit flipped, spin rate override, height offset), which would otherwise sit on
        // the corpse — visually stuck mid-air with weapons frozen sideways — until activeTimer happened to
        // run out on its own. Also stops DealDamage() from landing any further hits off a dead body.
        // _stats.IsDead is part of currentState, so every machine forces the identical end tick.
        bool forceEndOnDeath = _stats != null && _stats.IsDead;

        if (!forceEndOnDeath)
        {
            state.activeTimer -= delta;

            // Same arcY formula ComputeArcY drives visuals' own cosmetic rise with — set here (deterministic
            // Simulate context) rather than UpdateView() since WeaponBasePD.OrbitalPosition now reads this from
            // each weapon's own deterministic LateSimulate() too (see CharacterStatsPD.SpecialHeightOffset's own
            // doc comment for why weapons need this at all: the player's real transform never moves in Y here).
            _stats?.SetSpecialHeightOffset(ComputeArcY(state.activeTimer));

            // While loop, not if — guards against a single tick's delta covering more than one scheduled hit's
            // worth of time (e.g. a brief stall), so a hit can never be silently skipped.
            float elapsed = duration - state.activeTimer;
            while (state.hitsFired < _effectiveHitCount && elapsed >= HitTime(state.hitsFired))
            {
                DealDamage();
                state.hitsFired++;
            }
        }

        if (state.activeTimer <= 0f || forceEndOnDeath)
            EndActiveWindow(ref state, interrupted: false);
    }

    // Shared by the natural/death end above and OnSpecialMovementInterrupted below, so the two paths can
    // never drift apart. Mechanical side effects (weapons/orbit/spin-rate/height/isActive) always revert
    // regardless of why. interrupted additionally decides two things:
    // - Invincibility: natural/death leaves the committed grant alone (the postActiveInvincibilityGrace tail
    //   is meant to survive past the active window ending on its own — see that field's own doc comment);
    //   interrupted cancels it immediately instead.
    // - NotifyDashEnded/NotifyAbilityEnd: skipped entirely when interrupted — being interrupted is a
    //   punishment, not a "the action still counts" completion, so "reward on completion" listeners
    //   (DashExplosionAugment/DashHealAugment/PhantomBladeOnDashAugment) must not still pay out. Confirmed
    //   safe to skip NotifyAbilityEnd — nothing in this codebase currently listens for it at all. Listeners
    //   that only care about the WINDOW closing regardless of why (DashThroughInterruptAugment's own sweep)
    //   instead subscribe to PlayerControllerPD's own _onDashInterrupted/_onSpecialMovementInterrupted
    //   directly, not through this ability.
    private void EndActiveWindow(ref State state, bool interrupted)
    {
        state.activeTimer = 0f;
        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        _stats?.SetWeaponsDisabled(false);
        _stats?.SetUseSpecialSpinRate(false);
        _stats?.SetOrbitFlipped(false);
        _stats?.SetOrbitFacingYaw(0f);
        _stats?.SetSpecialHeightOffset(0f);

        if (interrupted)
        {
            _stats?.CancelCommittedInvincibility();
        }
        else
        {
            _controller?.NotifyDashEnded();
            // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc
            // comment. See the matching NotifyAbilityStart call in Activate() for why this is gated on
            // cooldown > 0 too.
            if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
        }
    }

    // Fires from PlayerControllerPD.Interrupt() — only meaningful while this ability's own leap is the thing
    // currently occupying isSpecialMoving (PredictedEvent, not a targeted call, so every ForcesSpecialAction
    // ability subscribes and each one no-ops if it wasn't the one active — see the guard below). Immediately
    // reverts every side effect Activate() applied, same shared path the natural/death end already uses, so
    // this and that can never drift apart.
    private void OnSpecialMovementInterrupted()
    {
        if (!currentState.isActive) return;
        EndActiveWindow(ref currentState, interrupted: true);
    }

    // Plain, self-contained AoE damage tick — every hit is built identically to a normal weapon hit (same
    // roll/highroll/crit math, same NotifyHit/NotifyStrike/NotifyCritStrike/knockback/hit-feedback calls, so
    // onhit/onstrike augments react exactly like they would to a real weapon), reading the normal, penalized
    // Strength/OnHitPower stats for its own damage/onHitPower (see BaseDamage()'s own doc comment for why
    // this uses the weapon-count-and-Multishot-divided stats now, not the Unpenalized variants).
    private void DealDamage()
    {
        if (_stats == null) return;

        // Capsule instead of a sphere, facing the direction cast in (same _facingYaw the leap itself moved
        // toward — fixed for the whole active window, same reasoning as _damageReach above) — p0 sits at the
        // player, p1 extends toward that facing far enough that the capsule's own rounded end (which already
        // adds `radius` past p1) lands exactly at _damageReach, so the capsule's real reach matches the
        // weapon's own real reach exactly rather than overshooting by an extra radius' worth.
        Vector3 center = _stats.transform.position;
        float   radius = damageRadiusSizeMultiplier * _stats.bonusSizePercent;
        Vector3 castDir = Quaternion.Euler(0f, _facingYaw, 0f) * Vector3.forward;
        Vector3 p0 = center;
        Vector3 p1 = center + castDir * Mathf.Max(0f, _damageReach - radius);

        _damageTouchedThisTick.Clear();
        int n = Physics.OverlapCapsuleNonAlloc(p0, p1, radius, _damageOverlapResults, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var enemy = _damageOverlapResults[i].GetComponent<IEnemyPD>();
            if (enemy != null) _damageTouchedThisTick.Add(enemy);
        }
        foreach (var enemy in _damageTouchedThisTick)
            HitEnemy(enemy);

        _damageTouchedAlliesThisTick.Clear();
        int an = Physics.OverlapCapsuleNonAlloc(p0, p1, radius, _damageOverlapResults, CombatLayerMasks.PlayerHurtboxMask);
        for (int i = 0; i < an; i++)
        {
            var ally = _damageOverlapResults[i].GetComponent<CharacterStatsPD>();
            if (ally != null) _damageTouchedAlliesThisTick.Add(ally);
        }
        foreach (var ally in _damageTouchedAlliesThisTick)
            HitAlly(ally);
    }

    // Base damage per hit, shared by both HitEnemy/HitAlly — plain (penalized) Strength, not
    // UnpenalizedStrength: this ability already scales its own HIT COUNT with weapon count
    // (hitCountPerWeapon/_effectiveHitCount), so per-hit damage should use the same weapon-count-divided
    // stat every normal weapon hit uses too, rather than manually re-deriving an equivalent division here —
    // Strength already divides by TotalWeaponCount * (1 + Multishot) internally, which is the exact
    // "penalized" formula this used to hand-roll (weapon count only, no Multishot) via UnpenalizedStrength /
    // _weaponCountAtCast. Times the LIVE AttackSpeed (unrelated to spinRate, which is a flat,
    // AttackSpeed-independent value now) times spinAbilityDamageMultiplier times the live
    // CharacterStatsPD.AbilityPower (this ability's own interpretation of that stat: scales both damage
    // sources below). Deliberately reads Strength/AttackSpeed/AbilityPower live, not snapshotted at cast
    // time — unlike _facingYaw/_damageReach/_weaponCountAtcast above, none of these need to stay fixed for
    // the whole active window.
    private float BaseDamage() => _stats.Strength * _stats.AttackSpeed * spinAbilityDamageMultiplier * _stats.AbilityPower;

    // Same shape as BaseDamage() (same reasoning), but OnHitPower instead of Strength — the onHitPower this
    // hit reports to onhit-triggered effects (via WeaponHitData/AllyStrikeData below), not the damage the
    // hit itself deals.
    private float OnHitPowerForEffects() => _stats.OnHitPower * _stats.AttackSpeed * spinAbilityDamageMultiplier * _stats.AbilityPower;

    // Mirrors WeaponMeleePD.Hit() exactly, baseDamage swapped from Strength to BaseDamage().
    private void HitEnemy(IEnemyPD enemy)
    {
        if (enemy._isDead) return;
        float baseDamage = BaseDamage();
        float damage = WeaponDamageMath.RollDamage(baseDamage, _stats.LuckChance, _stats.LuckFloor, _stats.LuckCeiling, _stats.Highroll, out bool isHighroll);
        if (isHighroll) damage *= 1f + _stats.HighrollBonus;
        bool  isCrit = GameRandom.instance.NextFloat() < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        if (isHighroll) _stats.NotifyStrikeHighroll(damage);
        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = baseDamage, onHitPower = OnHitPowerForEffects(), isCrit = isCrit };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - _stats.transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback * hitData.onHitPower);
        _stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true, source = DamageSource.Weapon });
    }

    // A touched ally only ever fires the _onAllyStrike notification now — real friendly fire has been
    // removed from the project entirely (see CharacterStatsPD.NotifyAllyStrike's own doc comment).
    private void HitAlly(CharacterStatsPD ally)
    {
        if (ally == _stats || ally.IsDead) return;
        _stats.NotifyAllyStrike(new AllyStrikeData { ally = ally, onHitPower = OnHitPowerForEffects() });
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Purely cosmetic — safe off viewState/every frame. NOT true anymore for OrbitFacingYaw specifically
        // (see Activate()/Simulate() — WeaponBasePD.OrbitalPosition now reads it from weapons' own
        // deterministic LateSimulate() too, so it's set from Simulate() instead, not here).
        float arcY = viewState.isActive ? ComputeArcY(viewState.activeTimer) : 0f;

        if (visuals != null)
            visuals.localPosition = _visualsRestLocalPosition + new Vector3(0f, arcY, 0f);

        // CharacterStatsPD owns _modelTransform itself (it's also continuously spinning it every frame for
        // the always-on idle flair), so this just flags whether that spin should currently be tipped 90
        // degrees on Z / facing the travel direction, rather than writing the transform directly, which
        // would fight that ownership.
        _stats?.SetModelSidewaysTipped(viewState.isActive);
    }
}
