using PurrNet.Prediction;
using UnityEngine;

// Rogue's second class ability, built on the IAbilityPD architecture — drops enemy aggro by going invisible
// (CharacterStatsPD.GrantInvisibility, a duration-max contribution, not a raw on/off — see its own doc
// comment) and grants +30% Total-tier move speed (AddMoveSpeedTotalPercent, 2026-09-30 — was Bonus-tier via
// AddMoveSpeedPercent, migrated the same day that whole Bonus-tier percent pair was removed from
// CharacterStatsPD, see bonusMoveSpeedFlat's own doc comment there), both for the same fixed duration. Gated by a cooldown, same
// shape as RogueSpinAbilityPD (see that file for the template this mirrors) — the two slots are otherwise
// functionally identical now, this one just doesn't force a special action while active. EnemyBasePD/
// EnemySpawnerPD already react to invisibility changing immediately (the taunt/invisible enemy-targeting
// work) — this ability only needs to grant its own duration at cast, never explicitly turning it back off
// (GrantInvisibility's own shared timer handles that), all the actual retargeting behavior already exists.
// The visual fade (CharacterStatsPD's own model-renderer alpha) is likewise driven generically off the
// invisible flag itself, not by this ability directly.
public class RogueVanishAbilityPD : PredictedIdentity<RogueVanishAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        // Running total of extension-seconds ACTUALLY applied to activeTimer so far this cast (via
        // ExtendActiveTime below) — not the sum of every `seconds` ever passed in, only the portion that
        // genuinely landed. Reset to 0 at Activate(). See ExtendActiveTime's own doc comment for why this
        // exists (2026-10-14 rework, explicit request — distinct from the OLD "clamp activeTimer itself"
        // approach, which capped the instantaneous stored value but let the ability's total real-world
        // uptime run unbounded as long as hits kept refilling it before it decayed).
        public float onHitExtensionApplied;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment for the full
        // mechanism. knownMaxCharges tracks the last-computed MaxCharges so Simulate() can detect a mid-run
        // increase (a bonusAbility2Charges augment) and grant the newly-unlocked charge(s) immediately.
        public int   charges;
        public int   knownMaxCharges;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Vanish";
    [SerializeField, TextArea] private string description = "Become invisible and gain +30% Move Speed for 3 seconds.";
    public float cooldown = 20f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility2Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full
    // value throughout the invisibility window) — see Simulate(). true: cooldown starts ticking
    // immediately at cast, running concurrently with the active window.
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability2;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract.
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    public float duration = 3f;
    // Total-tier percent, via AddMoveSpeedTotalPercent (2026-09-30 — was Bonus-tier via AddMoveSpeedPercent,
    // migrated the same day that whole Bonus-tier percent pair was removed from CharacterStatsPD, see
    // bonusMoveSpeedFlat's own doc comment there).
    public float moveSpeedPercent = 0.30f;

    // 0f (no-op) until RogueVanishBlinkAugment sets it — see Activate()'s own use for the full teleport
    // contract. [HideInInspector] — the augment owns this switch, nothing on this ability's own prefab
    // should need hand-authoring it (same reasoning as KnightBerserkAbilityPD's own augment-owned fields).
    [HideInInspector] public bool blinkOnCast = false;
    // 0f (no-op) until RogueVanishBlinkAugment/RogueVanishHighrollAugment/RogueVanishHealAugment sets it —
    // additive against 1f (all three stack additively against this one shared field). Renamed from
    // invisibilityDurationBonusPercent (2026-10-14 rework, explicit request) — used to scale ONLY the
    // GrantInvisibility call, leaving the Move Speed buff/active window running at the shorter raw duration;
    // now folded directly into _appliedDuration itself (see Activate()'s own use), so it's a real, permanent
    // part of the whole ability's duration — a buff you've picked, not a per-cast recomputation layered onto
    // one specific side effect. Knock-on effect worth knowing: RogueVanishHealAugment's own heal-over-time
    // (scoped to "while this ability is active") now runs for this same longer duration too, not just the
    // original 3s baseline — explicit, intended consequence of unifying the number, not a bug.
    [HideInInspector] public float durationBonusPercent = 0f;
    // 0f (no-op) until RogueVanishHighrollAugment sets it — additive against CharacterStatsPD.
    // totalHighrollChancePercent's own 1f baseline (AddTotalHighrollChancePercent — see that property's
    // own doc comment for why +1f genuinely doubles HighrollChance), active only for the duration of this
    // ability's own invisibility window — same Add-at-Activate/Remove-at-end pairing moveSpeedPercent above
    // already uses.
    [HideInInspector] public float highrollChanceBonusPercent = 0f;
    // 0f (default, off) until RogueVanishBlinkAugment sets it — see EffectiveCooldown's own doc comment for
    // the full contract. [HideInInspector] — the augment owns this switch, same reasoning as the other
    // augment-owned fields above. Doubles as both "is this on" AND "how much" (0 means off, same "0 means
    // neutral/off" convention this codebase uses elsewhere) — nerfed from a plain bool (2026-10-08, was a
    // full 1:1 pass-through of EffectiveDashHaste) once Dash Haste's full value folded in felt too strong;
    // the augment now sets this to 0.5 (50% of Dash Haste) instead of toggling a bool.
    [HideInInspector] public float dashHasteContributionPercent = 0f;

    private CharacterStatsPD _stats;
    // Only resolved/used when blinkOnCast is on — see Activate()'s own teleport block, same fields
    // BWBlinkAbilityPD already keeps for its own teleport.
    private PlayerControllerPD _controller;
    private PredictedRigidbody _rigidbody;
    private PredictedTransform _predictedTransform;
    // Not scaled by anything (see Activate()'s own comment), so this is just a plain snapshot of the field
    // at cast time rather than recomputed live at both Activate() and the end-of-active reversal — same
    // "fixed for the whole active window" reasoning as RogueSpinAbilityPD's own cast-time snapshots
    // (_weaponCountAtCast etc).
    private float _appliedMoveSpeedPercent;
    // Snapshotted alongside it — see IAbilityPD.ActiveTimeDuration's own doc comment for why the UI needs
    // this fixed rather than reading the raw duration field live, in case it changes mid-buff.
    private float _appliedDuration;
    // Snapshot of highrollChanceBonusPercent at Activate() time, same reasoning as _appliedMoveSpeedPercent
    // above — the end-of-active revert must subtract back exactly what was added.
    private float _appliedHighrollChanceBonusPercent;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
        _controller = GetComponent<PlayerControllerPD>();
        _rigidbody = GetComponent<PredictedRigidbody>();
        if (_rigidbody != null) _predictedTransform = _rigidbody.GetComponent<PredictedTransform>();
    }

    public bool IsReady => _stats != null && !currentState.isActive && currentState.charges > 0;
    public bool IsActive => currentState.isActive;
    // Only ever flips invisibility/movespeed — never touches movement, so it never locks dash or the
    // other ability slot out while active (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    // Same underlying window as IsActive for this ability (the whole invisibility buff) — see
    // IAbilityPD.AbilityIsActive's own doc comment for why these are separate interface members even
    // though they agree here.
    public bool AbilityIsActive => currentState.isActive;
    public bool HasActiveWindow => true;
    public float ActiveTimeRemaining => Mathf.Max(0f, currentState.activeTimer);
    public float ActiveTimeDuration => _appliedDuration;
    public float CooldownRemaining => Mathf.Max(0f, currentState.cooldownTimer);
    public float CooldownDuration => EffectiveCooldown;
    public float BaseCooldownDuration => cooldown;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
    public Sprite Icon => icon;
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;
    public AbilityIdentity Identity => AbilityIdentity.RogueVanish;
    public int CurrentCharges => currentState.charges;
    public int MaxCharges => EffectiveMaxCharges;
    // Live, not cached — see IAbilityPD.MaxCharges's own doc comment for why a mid-run bonus must be
    // reflected immediately. Reads whichever bonus matches this instance's own (Inspector-configurable)
    // Slot, not a hardcoded one. Falls back to just authoredChargeCount if _stats isn't resolved yet
    // (shouldn't happen in practice, Awake() runs well before Simulate()/any UI read).
    private int EffectiveMaxCharges => authoredChargeCount + (_stats != null
        ? (slot == AbilitySlot.Ability1 ? _stats.bonusAbility1Charges : _stats.bonusAbility2Charges)
        : 0);

    // Same formula as CharacterStatsPD.DashCooldown (baseCooldown / (1 + haste/100)) — a haste augment
    // shortens this ability's cooldown the same way it already does for dash, or lengthens it if
    // EffectiveAbilityHaste ever goes negative (a penalty). dashHasteContributionPercent (2026-10-05,
    // RogueVanishBlinkAugment — "the ability's cooldown should ALSO get reduced by Dash Haste, same way
    // Ability Haste already affects it"; nerfed 2026-10-08 from a full 1:1 fold-in to a 0.5 multiplier,
    // i.e. only 50% of Dash Haste counts) folds EffectiveDashHaste * dashHasteContributionPercent into the
    // SAME divide, additively with EffectiveAbilityHaste — same "two haste sources combining additively in
    // one denominator" shape a single haste stat's own internal Bonus/Total tiers already use, rather than
    // two separate divides multiplying together (which would double-dip on already-hasted time, same
    // reasoning every other "combine two percent sources additively, not multiplicatively" convention in
    // this codebase follows). 0f by default, so this is a pure no-op without the augment (multiplying by 0
    // naturally zeroes out the Dash Haste term, no separate on/off branch needed).
    private float EffectiveCooldown => _stats != null
        ? cooldown / (1f + (_stats.EffectiveAbilityHaste + _stats.EffectiveDashHaste * dashHasteContributionPercent) / 100f)
        : cooldown;

    // Mirrors EffectiveCooldown's own formula exactly, including the dashHasteContributionPercent fold-in —
    // see that property's own doc comment. Outside a live match, falls back to the base field itself —
    // exact at neutral haste (0, i.e. cooldown / (1+0/100) == cooldown), same "neutral stat value == the
    // base field" convention as every other Live*Ratio in this file.
    public string LiveCooldownRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? cooldown / (1f + (stats.EffectiveAbilityHaste + stats.EffectiveDashHaste * dashHasteContributionPercent) / 100f)
            : cooldown;
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(value)} ";
    }

    // Mirrors Simulate()'s own tick condition exactly (see that method's own comment) — a cooldown-reduction
    // effect should only ever eat into time that's ACTUALLY counting down right now, not a cooldownTimer
    // that's merely sitting at its full pre-loaded value while the active window (cooldownStartsOnCast=false)
    // hasn't ended yet to let it start ticking. MaxCharges > 1 (2026-10-02) — see Simulate()'s own identical
    // addition for why a multi-charge ability always ticks its regen timer regardless of isActive.
    private bool IsCooldownActivelyTicking =>
        currentState.cooldownTimer > 0f && (cooldownStartsOnCast || !currentState.isActive || EffectiveMaxCharges > 1);

    // See IAbilityPD's own doc comments for the exact contract of each of these three.
    // Shared by all three methods below — a reduction that would drop cooldownTimer below 0 now
    // "overflows" into granting the next charge (looping further if the reduction is large enough to cross
    // multiple charges at once), rather than being clamped away and wasted the instant cooldownTimer first
    // hits 0 (2026-10-03, explicit request — multi-charge abilities should let a big cooldown-reduction
    // effect spill into progress toward the NEXT charge, same as the ordinary passive regen tick already
    // does over many real ticks, just resolved atomically in one call). Caps at EffectiveMaxCharges — once
    // fully charged, any further excess is genuinely discarded, nothing left to speed up. The while loop's
    // own `cooldownTimer > 0f` check (re-tested every iteration) also makes this safe against a degenerate
    // EffectiveCooldown of 0 — the loop exits immediately rather than spinning.
    private void ApplyCooldownReduction(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);
        int effectiveMax = EffectiveMaxCharges;
        while (seconds > 0f && currentState.charges < effectiveMax && currentState.cooldownTimer > 0f)
        {
            if (seconds < currentState.cooldownTimer)
            {
                currentState.cooldownTimer -= seconds;
                seconds = 0f;
            }
            else
            {
                seconds -= currentState.cooldownTimer;
                currentState.charges++;
                currentState.cooldownTimer = currentState.charges < effectiveMax ? EffectiveCooldown : 0f;
            }
        }
    }

    public void ReduceCooldownFlat(float seconds)
    {
        if (!IsCooldownActivelyTicking) return;
        ApplyCooldownReduction(seconds);
    }
    public void ReduceCooldownPercentRemaining(float percent01)
    {
        if (!IsCooldownActivelyTicking) return;
        ApplyCooldownReduction(currentState.cooldownTimer * Mathf.Clamp01(percent01));
    }
    public void ReduceCooldownPercentTotal(float percent01)
    {
        if (!IsCooldownActivelyTicking) return;
        ApplyCooldownReduction(CooldownDuration * Mathf.Clamp01(percent01));
    }

    public void Activate()
    {
        if (!IsReady || _stats == null) return;

        // RogueVanishBlinkAugment's switch (2026-10-04) — 0f-equivalent/false by default, see blinkOnCast's
        // own doc comment. Mirrors BWBlinkAbilityPD's own instant-teleport block almost exactly (see that
        // file's own extensive comments for the full reasoning behind each piece, only summarized here):
        // target is wherever the cursor currently points (PlayerControllerPD.currentState.cursorDir/
        // cursorDistance — already ground-plane, unclamped by any particular ability's own range), clamped
        // to PlayerControllerPD.DashRange (same real-world distance a normal dash covers), then resolved
        // through the shared PlayerControllerPD.ResolveSafeTeleportTarget so it can never land inside/past
        // Bounds/Obstacle geometry or outside the playable area. Fires the same OnDashStarted/OnDashEnded
        // pair (bracketing the teleport, Started at the OLD position/Ended at the NEW one) every other
        // teleport-style ability in this codebase fires, so DashExplosionAugment/DashHealAugment/etc. still
        // trigger off it.
        if (blinkOnCast && _controller != null && _rigidbody != null)
        {
            // Cleanly ends a real dash still in progress at blink time (2026-10-18 fix) — this ability
            // deliberately doesn't set ForcesSpecialAction (see that property's own doc comment), so the
            // ability-activation gate's own "end the dash first" step never runs for Vanish the way it does
            // for RogueSpin/BWBlink. Without this, an active dash's state.isDashing/dashVelocity survived
            // completely untouched through the teleport below and kept being applied from the NEW
            // post-teleport position on every later tick, never re-validated against it — confirmed live as
            // the cause of briefly leaping out of the map when chaining a dash into this blink near a
            // boundary wall. Called FIRST, before fromPosition is read, so any correction EndDashIfActive
            // itself applies (if the dash's current mid-flight position had already drifted somewhere
            // invalid) is reflected in the blink's own starting point too.
            _controller.EndDashIfActive();

            // Also what DashEffectDoubleTriggerAugment listens to now (2026-10-10, replaces the removed
            // NotifyRealDashStarted/RealDashEnded pair) — see PlayerControllerPD._onDashMovementEnded's own
            // field comment.
            _controller.NotifyDashStarted();
            _controller.NotifyDashMovementStarted();

            Vector3 fromPosition = _stats.transform.position;
            float blinkDistance = Mathf.Min(_controller.currentState.cursorDistance, _controller.DashRange);
            Vector3 rawBlinkTarget = fromPosition + _controller.currentState.cursorDir.normalized * blinkDistance;
            Vector3 blinkTarget = _controller.ResolveSafeTeleportTarget(rawBlinkTarget, fromPosition);

            _rigidbody.position = blinkTarget;
            // Same staleness fix BWBlinkAbilityPD's own identical line addresses — see its own doc comment:
            // a Rigidbody.position write from PurrDiction's own custom tick isn't guaranteed reflected in
            // transform.position within this same synchronous continuation otherwise.
            _stats.transform.position = blinkTarget;
            _predictedTransform?.ResetInterpolation();

            // interrupted: false — an instant teleport has nothing mid-flight to interrupt.
            _controller.NotifyDashEnded();
            _controller.NotifyDashMovementEnded(false);
        }

        // No AbilityPower scaling (removed 2026-08-23) — duration and moveSpeedPercent are both plain,
        // fixed values now. durationBonusPercent folded directly in (2026-10-14 rework, was invisibility-
        // only before — see durationBonusPercent's own doc comment) — a real, permanent part of this
        // ability's own duration now, not a one-off recomputed-every-cast add-on layered just onto the
        // invisibility floor contribution.
        _appliedMoveSpeedPercent = moveSpeedPercent;
        _appliedDuration      = duration * (1f + durationBonusPercent);

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        // Fresh per-cast budget — see ExtendActiveTime's own doc comment. Nothing carries over from a
        // previous instance, same "resets fresh on the NEXT Activate()" convention activeTimer itself uses.
        currentState.onHitExtensionApplied = 0f;
        // Charge system — spend 1, and only (re)arm the regen timer if it wasn't already running (i.e. this
        // cast came off a fully-topped-off state). A timer already mid-regen toward another charge keeps
        // ticking on its own schedule, untouched by this cast — see IAbilityPD.MaxCharges's own doc comment.
        currentState.charges--;
        if (currentState.cooldownTimer <= 0f) currentState.cooldownTimer = EffectiveCooldown;
        // Direct cross-identity mutation, same pattern as PausePD.SetPlayerReady()/CharacterStatsPD.
        // StartSurge() — safe since Activate() is only ever called from PlayerControllerPD's own Simulate(),
        // deterministically, identically on every machine.
        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in Simulate()'s own end-of-active block below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }
        // GrantInvisibility, not a raw on/off — contributes this ability's own duration as a floor rather
        // than unconditionally claiming the flag, so a DIFFERENT overlapping invisibility source (e.g.
        // DeathPreventionInvisibleAugment) never gets cut short by this ability's own end-of-buff turning it
        // off early. See CharacterStatsPD.GrantInvisibility's own doc comment. _appliedDuration already has
        // durationBonusPercent folded in above — no separate multiply needed here anymore (2026-10-14
        // rework — used to be invisibility-only, now a real part of the whole ability's duration, so the
        // Move Speed buff/active window below share the exact same boosted number instead of silently
        // running shorter).
        _stats.GrantInvisibility(_appliedDuration);
        _stats.AddMoveSpeedTotalPercent(_appliedMoveSpeedPercent);
        // 0f (no-op) until RogueVanishHighrollAugment bumps highrollChanceBonusPercent — see that field's
        // own doc comment.
        _appliedHighrollChanceBonusPercent = highrollChanceBonusPercent;
        if (_appliedHighrollChanceBonusPercent != 0f) _stats.AddTotalHighrollChancePercent(_appliedHighrollChanceBonusPercent);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cycle (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

    // RogueVanishOnHitDurationAugment's own hook (2026-10-05) — extends THIS cast's own active window by
    // `seconds` (a no-op, never called at all, without that augment). Gated on currentState.isActive so it
    // genuinely only ever takes effect during an ongoing Vanish, and only extends that one instance —
    // activeTimer is reset fresh to _appliedDuration on the NEXT Activate() regardless of how much a
    // previous instance got extended, so nothing carries over.
    //
    // Total extension across the WHOLE cast is capped at 100% of _appliedDuration (2026-10-14 rework,
    // explicit request — replaces an earlier version that only clamped the instantaneous activeTimer value
    // to _appliedDuration * 2f. That approach bounded the STORED value at any one moment, but not the
    // ability's total real-world uptime: activeTimer decays every tick like any other countdown, so as long
    // as hits kept landing fast enough to top it back toward that same ceiling before it ran out, Vanish
    // could be kept alive far longer than +100%, in principle indefinitely). currentState.onHitExtensionApplied
    // now tracks the running total of extension-seconds ACTUALLY applied so far this cast, reset fresh at
    // Activate() — once it reaches _appliedDuration (100%), no further call can add anything more, no matter
    // how much activeTimer has since decayed. `seconds` is clamped to whatever budget remains BEFORE being
    // added to either activeTimer or the running total — the portion that would have overflowed the budget
    // is discarded outright, same "tossed away, never counted" contract RogueSpinInterruptAugment-family
    // once-per-activation guards use for their own excess (explicit request: a hit that lands while the
    // budget is already exhausted must not inflate onHitExtensionApplied for the part that didn't actually
    // land). activeTimer can therefore never exceed _appliedDuration + _appliedDuration (2x) by construction
    // — no separate ceiling clamp needed on it directly.
    //
    // _appliedDuration already has durationBonusPercent folded in (2026-10-14 rework — see that field's own
    // doc comment), so the 100% budget is measured against this ability's OWN already-applied, already-
    // boosted cast-time snapshot (e.g. 6s baseline with Blink+Highroll -> up to 12s hard ceiling), the same
    // number the Move Speed buff/active window/invisibility floor all share.
    //
    // Also re-asserts GrantInvisibility(currentState.activeTimer) right alongside the extension — the
    // invisibility flag itself is a SEPARATE "longest contribution wins" floor-timer (see class doc
    // comment), set ONCE at Activate() time to a FIXED total, so it does NOT automatically track a later
    // extension to activeTimer on its own. Without this re-assertion, a long enough chain of on-hit
    // extensions would keep the Move Speed buff/AbilityIsActive window running past the ORIGINAL
    // invisibility timer, leaving the player genuinely visible again while Vanish still reads as "active"
    // — re-syncing here keeps both timers aligned at every extension instead. Always contributes the
    // FULL current activeTimer (not just the delta), which is exactly what GrantInvisibility's own
    // max-of-contributions semantics need to actually take effect (a tiny +0.05f contribution alone would
    // never beat whatever's already running).
    public void ExtendActiveTime(float seconds)
    {
        if (!currentState.isActive) return;

        float remainingBudget = _appliedDuration - currentState.onHitExtensionApplied;
        if (remainingBudget <= 0f) return;

        float actualSeconds = Mathf.Min(seconds, remainingBudget);
        currentState.onHitExtensionApplied += actualSeconds;
        currentState.activeTimer += actualSeconds;
        _stats?.GrantInvisibility(currentState.activeTimer);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Charge system (2026-10-02) — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment. A mid-run
        // MaxCharges increase (bonus charges augment) grants the newly-unlocked charge(s) immediately rather
        // than making the player wait out a regen cycle; a decrease (shouldn't normally happen) clamps down.
        int effectiveMax = EffectiveMaxCharges;
        if (effectiveMax > state.knownMaxCharges)
        {
            state.charges += effectiveMax - state.knownMaxCharges;
            state.knownMaxCharges = effectiveMax;
        }
        else if (effectiveMax < state.knownMaxCharges)
        {
            state.charges = Mathf.Min(state.charges, effectiveMax);
            state.knownMaxCharges = effectiveMax;
        }

        if (state.charges < effectiveMax)
        {
            // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole
            // active window below) or only once that window ends (state.isActive false) — see the field's
            // own doc comment. Same shape as RogueSpinAbilityPD.Simulate(). effectiveMax > 1 (2026-10-02) —
            // a multi-charge ability always regens concurrently with its own active window regardless of
            // cooldownStartsOnCast, since the whole point of charges is to keep stacking up for later, not
            // stall behind whatever's currently active — see IAbilityPD.MaxCharges's own doc comment.
            if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive || effectiveMax > 1))
                state.cooldownTimer -= delta;

            if (state.cooldownTimer <= 0f)
            {
                state.charges++;
                state.cooldownTimer = state.charges < effectiveMax ? EffectiveCooldown : 0f;
            }
        }

        if (!state.isActive) return;

        state.activeTimer -= delta;
        // Death force-ends the buff early — see ExeBigAxeAbilityPD.Simulate()'s own identical comment for
        // the full reasoning (same shape here: a dead caster gets nothing from Invisible/MoveSpeed, and the
        // cooldown otherwise couldn't start ticking until this window ends anyway).
        bool forceEndOnDeath = _stats != null && _stats.IsDead;
        if (state.activeTimer > 0f && !forceEndOnDeath) return;

        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        // Invisibility itself is deliberately NOT turned off here — it's left to CharacterStatsPD's own
        // shared invisibleTimer to expire naturally (see GrantInvisibility's own doc comment), so it isn't
        // cut short if some other source is still relying on it. Only this ability's own Move Speed grant
        // reverts here, since that's exclusively this ability's to give and take back. The screen tint/model
        // fade are no longer this ability's own concern either (2026-08-31) — see CharacterStatsPD.
        // UpdateView's own invisible-flag-driven block, which now handles both generically for any source.
        _stats?.AddMoveSpeedTotalPercent(-_appliedMoveSpeedPercent);
        if (_appliedHighrollChanceBonusPercent != 0f) _stats?.AddTotalHighrollChancePercent(-_appliedHighrollChanceBonusPercent);
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        // See the matching NotifyAbilityStart call in Activate() for why this is gated on cooldown > 0 too.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }
}
