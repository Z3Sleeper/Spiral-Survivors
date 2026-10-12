using PurrNet.Prediction;
using UnityEngine;

// Bladeweaver's first class ability, built on the IAbilityPD architecture — gated by a cooldown, same shape
// as every other ability here. Unlike RogueVanishAbilityPD's own single-phase active window, this is a
// three-phase sequence (Outbound -> Hold -> Return), all still counted as one continuous AbilityIsActive
// window (see IAbilityPD.AbilityIsActive's own doc comment for why that's fine for a multi-stage effect).
//
// Mechanically this doesn't touch damage/movement/hitboxes at all — it relocates WHERE every weapon orbits.
// CharacterStatsPD.EffectiveOrbitOrigin (new) is what WeaponMeleePD/WeaponAxePD/WeaponRangedPD's own
// LateSimulate now reads instead of transform.position directly; this ability drives that value every tick
// via SetOrbitOriginOverride while active, so the blades keep orbiting exactly as normal (same
// WeaponOrbitAngle/EffectiveWeaponSpeed, untouched), just around a moving/held point instead of the player:
// - Outbound: currentOrigin steps from the player's cast-time position toward the fixed targetPosition at
//   travelSpeed units/sec.
// - Hold: currentOrigin sits at targetPosition for holdDuration seconds — blades keep orbiting that point.
// - Return: currentOrigin steps toward the player's LIVE position (not a snapshot — the player is free to
//   keep moving) at travelSpeed units/sec, until it catches up, at which point the override is cleared
//   entirely and weapons seamlessly resume reading transform.position directly.
// ForcesSpecialAction is false — never touches player movement itself, matching RogueVanishAbilityPD's own
// reasoning (only Rogue's Spin leap is a real "special action").
//
// targetPosition is clamped to castRangeMultiplier * true weapon reach (TryGetFarthestWeaponReach, which
// already folds in each weapon's own orbit radius plus its blade length past that — see its own doc
// comment), same clamp-to-max-range shape as ArcherArrowRainAbilityPD's own Activate().
public class BWBladeDanceAbilityPD : PredictedIdentity<BWBladeDanceAbilityPD.State>, IAbilityPD
{
    public enum Phase { Idle, Outbound, Hold, Return }

    public struct State : IPredictedData<State>
    {
        public int     phase;
        // Only meaningful during Hold — how much longer the blades sit at targetPosition.
        public float   phaseTimer;
        // Captured once at cast, fixed for the whole sequence.
        public Vector3 targetPosition;
        // The live orbit-origin-override value, stepped every tick through all three phases.
        public Vector3 currentOrigin;
        public float   cooldownTimer;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment. Does NOT gate
        // the Hold-phase recast branch below (that's a free early-return on an already-paid-for cast, not a
        // new one) — only the Idle-phase (fresh cast) branch of IsReady.
        public int     charges;
        public int     knownMaxCharges;
        public void Dispose() { }
    }

    private Phase GetPhase(ref State s) => (Phase)s.phase;
    private void SetPhase(ref State s, Phase p) => s.phase = (int)p;

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Blade Dance";
    [SerializeField, TextArea] private string description =
        "Send your blades to the target location. They stay there for a few seconds before returning.";
    public float cooldown = 10f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility1Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
    // false (default): cooldown only starts ticking once the whole Outbound->Hold->Return sequence ends
    // (phase returns to Idle) — see Simulate(). true: starts ticking immediately at cast instead.
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability1;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract.
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    // Cast range = true weapon reach (orbit radius + blade length, already included — see
    // WeaponBasePD.GetOuterReach's own doc comment) * this.
    [SerializeField] private float castRangeMultiplier = 2f;
    [SerializeField] private float travelSpeed = 25f;
    public float holdDuration = 5f;

    // Live duration multiplier (2026-10-06, BWBladeDanceDurationAugment) — 1f (neutral) default,
    // [HideInInspector] since it's augment-owned, never hand-tuned here. Augments ADD to this rather than
    // destructively multiplying holdDuration directly — same "live multiplier field, base value never
    // mutated" shape ArcherArrowRainAbilityPD.durationMultiplier established, per Stuart's own standing
    // preference against multiplicative stacking for this kind of "+X% to Y" augment effect. EffectiveHoldDuration
    // (not the raw holdDuration field) is what Simulate()'s Outbound->Hold transition and
    // ActiveTimeDuration/ActiveTimeRemaining actually read.
    [HideInInspector] public float durationMultiplier = 1f;
    private float EffectiveHoldDuration => holdDuration * durationMultiplier;

    // Augment-owned (2026-10-06, BWBladeDanceAbilityHasteAttackSpeedAugment) — [HideInInspector]/0f (true
    // no-op) default, same Pattern B shape as every other ability-owned augment hook in this codebase (e.g.
    // KnightBerserkAbilityPD.moveSpeedPercent). Used to be a hardcoded always-on base-form mechanic
    // (AbilityHaste converting directly into bonus Attack Speed, since AbilityHaste normally only ever
    // shortens THIS ability's own cooldown — barely meaningful against its own low cooldown) — Stuart's own
    // explicit call to pull it out into an augment instead, since Bladeweaver's abilities otherwise "don't
    // do too much" on their own.
    //
    // 1 point of EffectiveAbilityHaste = attackSpeedPercentPerHaste Attack Speed, ADDED via AddAttackSpeedFlat
    // (a plain additive percent, same tier every other "+X% Attack Speed" grant in this codebase uses).
    // Granted once at cast (the fresh-cast branch only — a Hold recast doesn't re-grant, the buff already
    // spans the whole sequence) and reversed with the identical snapshotted value in EndDance(), same
    // cast-time-snapshot convention as RogueVanishAbilityPD/KnightBerserkAbilityPD's own applied-percent
    // fields (a live AbilityHaste change mid-dance must not desync the add/subtract pair). This also means
    // blade orbit speed itself (EffectiveWeaponSpeed = AttackSpeed * 360) speeds up too, along with anything
    // else this character's Attack Speed drives (FireRate, etc.) — not scoped to "just the blades."
    //
    // See BWBladeDanceAbilityHasteAttackSpeedAugment for the live-value description token/{AttackSpeedPerHastePercent}
    // alias — moved there entirely (same "ability owns the no-op field, augment owns the description-token
    // helpers" split every other Pattern B hook in this codebase uses), since this is always 0 on the base
    // ability now and has nothing of its own worth describing.
    [HideInInspector] public float attackSpeedPercentPerHaste = 0f;

    // Augment-owned (2026-10-10, BWBladeDanceDurationAugment) — [HideInInspector]/0f (true no-op) default,
    // same Pattern B shape as attackSpeedPercentPerHaste above. Additive onto CharacterStatsPD's own
    // bonusEnergyGainPercent (AddEnergyGainPercent) for the whole Outbound/Hold/Return window — granted once
    // at cast (fresh-cast branch only, same as the attack-speed grant) and reversed with the identical
    // snapshotted value in EndDance(). Doubles as this family's own "is the Duration augment also picked"
    // signal for BWBladeDanceStunAugment's synergy point — only ever set away from 0 by that one augment, so
    // reading it directly is a cross-augment-via-the-ability check, same "shared knob lives on the ability,
    // not a GetComponent<OtherAugment> reference" convention this codebase already uses for other
    // within-one-ability-family synergies (e.g. ExeBigAxeAbilityPD.buffStrengthMultiplier).
    [HideInInspector] public float energyGenerationBonusPercent = 0f;

    private CharacterStatsPD   _stats;
    private WeaponManagerPD    _weaponManager;
    private PlayerControllerPD _controller;
    // Auto-cast target — see the Return case in Simulate() for why this is only ever poked on a NATURAL
    // arrival, never the death-forced end.
    private BWBlinkAbilityPD   _blink;

    // Snapshotted at cast time — see attackSpeedPercentPerHaste's own doc comment for why.
    private float _appliedAttackSpeedPercent;
    // Snapshotted at cast time — see energyGenerationBonusPercent's own doc comment for why.
    private float _appliedEnergyGenerationPercent;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats         = GetComponent<CharacterStatsPD>();
        _weaponManager = GetComponent<WeaponManagerPD>();
        _controller    = GetComponent<PlayerControllerPD>();
        _blink         = GetComponent<BWBlinkAbilityPD>();
    }

    public Sprite Icon => icon;

    // Ready in two different senses depending on phase — see class doc comment's own "recall early" section:
    // Idle means a normal fresh cast, gated on cooldown like any other ability; Hold means a recast is
    // available to cut the hold short and start heading back early (see Activate()'s own Hold branch).
    // Outbound/Return are never ready — the blades are already committed to that leg of the trip, so a
    // press there does nothing (PlayerControllerPD routes it to NotifyActivationBlocked() instead, itself a
    // no-op). This is also what LocalUIManagerPD/PlayerUIWorldSpace's cooldown-BG visibility is driven by
    // (both already derive it purely from !IsReady) — so the BG automatically hides during Hold (recast
    // available) and shows during Outbound/Return (does nothing right now), no UI-side changes needed.
    public bool IsReady
    {
        get
        {
            if (_stats == null) return false;
            var phase = GetPhase(ref currentState);
            if (phase == Phase.Idle) return currentState.charges > 0;
            return phase == Phase.Hold;
        }
    }
    public bool IsActive => GetPhase(ref currentState) != Phase.Idle;
    // Exposes the private phase enum publicly (2026-10-06, BWBladeDanceStunAugment) — that augment needs to
    // tell Outbound/Return apart from Hold (its stun only applies during travel, not while the blades are
    // sitting still at the destination), which IsActive alone can't distinguish.
    public Phase CurrentPhase => GetPhase(ref currentState);
    // Never touches player movement itself — only ever repositions where weapons orbit (see class doc
    // comment), so it never locks dash or the other ability slot out while active.
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    public bool AbilityIsActive => GetPhase(ref currentState) != Phase.Idle;
    // True, but ActiveTimeRemaining/ActiveTimeDuration below don't map onto a single linear countdown the
    // way every other ability's does — Outbound/Return are travel-distance-driven (no fixed length, depends
    // on castDistance/the player's live position), only Hold has a real timer (phaseTimer/holdDuration).
    // Instead each phase reports whatever ratio reads correctly for it: Outbound reports full (the bar
    // appears immediately at cast but doesn't start draining until Hold actually begins), Hold reports the
    // real phaseTimer/holdDuration countdown, and Return reports empty (recalling — whether via the natural
    // Hold-timeout-into-Return or an early recast-to-return-during-Hold — reads as "spent," and the bar
    // stays visible at 0 rather than disappearing until the blades actually finish arriving and AbilityIsActive
    // itself goes false at Idle).
    public bool HasActiveWindow => true;
    public float ActiveTimeRemaining
    {
        get
        {
            switch (GetPhase(ref currentState))
            {
                case Phase.Outbound: return EffectiveHoldDuration;
                case Phase.Hold:     return Mathf.Max(0f, currentState.phaseTimer);
                default:             return 0f; // Return (and Idle, though moot there — see HasActiveWindow).
            }
        }
    }
    public float ActiveTimeDuration => EffectiveHoldDuration;
    public float CooldownRemaining => Mathf.Max(0f, currentState.cooldownTimer);
    public float CooldownDuration => EffectiveCooldown;
    public float BaseCooldownDuration => cooldown;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;
    public AbilityIdentity Identity => AbilityIdentity.BWBladeDance;
    public int CurrentCharges => currentState.charges;
    public int MaxCharges => EffectiveMaxCharges;
    // Live, not cached — see IAbilityPD.MaxCharges's own doc comment for why a mid-run bonus must be
    // reflected immediately. Reads whichever bonus matches this instance's own (Inspector-configurable)
    // Slot, not a hardcoded one.
    private int EffectiveMaxCharges => authoredChargeCount + (_stats != null
        ? (slot == AbilitySlot.Ability1 ? _stats.bonusAbility1Charges : _stats.bonusAbility2Charges)
        : 0);

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
    // hasn't ended yet to let it start ticking. Phase-based (not a plain isActive bool) since this ability
    // tracks Idle/Outbound/Hold/Return — matches Simulate()'s own `phase == Phase.Idle` check exactly.
    // MaxCharges > 1 (2026-10-02) — see Simulate()'s own identical addition for why a multi-charge ability
    // always ticks its regen timer regardless of phase.
    private bool IsCooldownActivelyTicking =>
        currentState.cooldownTimer > 0f && (cooldownStartsOnCast || GetPhase(ref currentState) == Phase.Idle || EffectiveMaxCharges > 1);

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

        // IsReady already narrowed this to exactly Idle (fresh cast) or Hold (recast) — see its own doc
        // comment. Recast: cut the hold short and start heading back early. Nothing else about the ongoing
        // sequence needs re-triggering — cooldownTimer/targetPosition/the surge block/NotifyAbilityStart are
        // all already in effect from the original cast, and currentOrigin just keeps stepping from wherever
        // it already is (Simulate()'s own Return case reads it live), so this is purely a phase transition.
        if (GetPhase(ref currentState) == Phase.Hold)
        {
            SetPhase(ref currentState, Phase.Return);
            return;
        }

        // Charge system — spend 1, and only (re)arm the regen timer if it wasn't already running — see
        // IAbilityPD.MaxCharges's own doc comment.
        currentState.charges--;
        if (currentState.cooldownTimer <= 0f) currentState.cooldownTimer = EffectiveCooldown;

        Vector3 origin = _stats.transform.position;

        // EffectiveWeaponRange fallback (2026-09-16, was plain WeaponRange) — matches TryGetFarthestWeaponReach's
        // own EffectiveWeaponRange-based GetOuterReach() scale, same reasoning ExeAxeThrowAbilityPD's own
        // identical trueReach fallback uses.
        float trueReach = (_weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float r)
            ? r
            : _stats.EffectiveWeaponRange) * castRangeMultiplier;

        // Clamped to trueReach, not just aimed toward it — same shape as ArcherArrowRainAbilityPD's own
        // Activate(): cursor within range targets exactly the cursor's own point, cursor beyond range
        // targets at max range in that same direction. cursorDir/cursorDistance come from
        // PlayerControllerPD's own predicted state (captured once, owner-only, from real input), so every
        // machine resimulates this cast at the identical point.
        Vector3 cursorDir      = _controller != null ? _controller.currentState.cursorDir : Vector3.forward;
        float   cursorDistance = _controller != null ? _controller.currentState.cursorDistance : 0f;
        float   castDistance   = Mathf.Min(cursorDistance, trueReach);

        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in EndDance() below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }

        // 0f (no-op) until BWBladeDanceAbilityHasteAttackSpeedAugment bumps attackSpeedPercentPerHaste — see
        // that field's own doc comment for the full reasoning.
        _appliedAttackSpeedPercent = _stats.EffectiveAbilityHaste * attackSpeedPercentPerHaste;
        _stats.AddAttackSpeedFlat(_appliedAttackSpeedPercent);

        // 0f (no-op) until BWBladeDanceDurationAugment bumps energyGenerationBonusPercent — see that field's
        // own doc comment for the full reasoning.
        _appliedEnergyGenerationPercent = energyGenerationBonusPercent;
        _stats.AddEnergyGainPercent(_appliedEnergyGenerationPercent);

        currentState.targetPosition = origin + cursorDir * castDistance;
        currentState.currentOrigin  = origin;
        SetPhase(ref currentState, Phase.Outbound);

        // Set immediately (not left to wait for this identity's own Simulate() later this same tick) —
        // harmless no-op visually right now since currentOrigin still equals the player's own position, but
        // guarantees the override is already in place from the very first tick regardless of Simulate()
        // ordering between identities.
        _stats.SetOrbitOriginOverride(origin);

        // Only skipped at a literal 0 (raw, un-hasted) cooldown — see class doc comment. Fires normally now
        // that cooldown is a real, if small, value.
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        var phase = GetPhase(ref state);

        // Charge system (2026-10-02) — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment.
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
            // Outbound->Hold->Return window) or only once that window ends (phase back to Idle) — see the
            // field's own doc comment. Same shape as RogueVanishAbilityPD.Simulate().
            // effectiveMax > 1 (2026-10-02) — see RogueVanishAbilityPD.Simulate()'s own identical addition.
            if (state.cooldownTimer > 0f && (cooldownStartsOnCast || phase == Phase.Idle || effectiveMax > 1))
                state.cooldownTimer -= delta;

            if (state.cooldownTimer <= 0f)
            {
                state.charges++;
                state.cooldownTimer = state.charges < effectiveMax ? EffectiveCooldown : 0f;
            }
        }

        if (phase == Phase.Idle) return;

        // Death force-ends the dance immediately, skipping any remaining Outbound/Hold/Return travel —
        // EndDance() clears the orbit-origin override outright (see its own call), which snaps every
        // weapon's own OrbitalPosition straight back to reading _stats.transform.position (normal orbit
        // around the player) the very next LateSimulate tick, no interpolated travel back at all. The player
        // never actually SEES this snap: WeaponManagerPD hides every weapon's renderers off the same IsDead
        // flag (its own LateUpdate edge detection), same real-time frame, so from their perspective the
        // blades just vanish on death rather than visibly teleporting home first.
        if (_stats != null && _stats.IsDead)
        {
            EndDance(ref state);
            return;
        }

        switch (phase)
        {
            case Phase.Outbound:
            {
                Vector3 toTarget = state.targetPosition - state.currentOrigin;
                float   dist     = toTarget.magnitude;
                float   step     = travelSpeed * delta;
                if (dist <= step)
                {
                    state.currentOrigin = state.targetPosition;
                    state.phaseTimer    = EffectiveHoldDuration;
                    SetPhase(ref state, Phase.Hold);
                }
                else
                {
                    state.currentOrigin += toTarget * (step / dist);
                }
                break;
            }

            case Phase.Hold:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) SetPhase(ref state, Phase.Return);
                break;

            case Phase.Return:
            {
                // Chases the player's LIVE position, not a cast-time snapshot — the player is free to keep
                // moving during the return trip, same "always resolves against current reality" reasoning
                // as ArcherArrowRainAbilityPD's own SpawnChain reading Stats live on every recast.
                Vector3 livePlayerPos = _stats != null ? _stats.transform.position : state.currentOrigin;
                Vector3 toPlayer      = livePlayerPos - state.currentOrigin;
                float   dist          = toPlayer.magnitude;
                float   step          = travelSpeed * delta;
                if (dist <= step)
                {
                    EndDance(ref state);
                    // Auto-cast Blink's buff (never the teleport itself — the blades just got home under
                    // their own power) the instant the dance genuinely finishes, so a player who forgets to
                    // press Blink mid-dance doesn't lose out on its Move Speed buff or its Ability Started/
                    // Ended-triggered augment interactions just for not pressing an extra button — see
                    // BWBlinkAbilityPD.AutoCastOnDanceReturn's own doc comment. Deliberately NOT reached from
                    // the death-forced EndDance() call above — a dead caster gets nothing from this either
                    // way, and AutoCastOnDanceReturn's own _stats.IsDead guard would block it regardless.
                    _blink?.AutoCastOnDanceReturn();
                }
                else
                    state.currentOrigin += toPlayer * (step / dist);
                break;
            }
        }

        // Pushed every tick the dance is still ongoing — EndDance() above already clears the override
        // itself the instant Return actually finishes, so this only re-applies it while genuinely still
        // Outbound/Hold/Return after the switch above.
        if (GetPhase(ref state) != Phase.Idle && _stats != null)
            _stats.SetOrbitOriginOverride(state.currentOrigin);
    }

    private void EndDance(ref State state)
    {
        SetPhase(ref state, Phase.Idle);
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        _stats?.AddAttackSpeedFlat(-_appliedAttackSpeedPercent);
        _stats?.AddEnergyGainPercent(-_appliedEnergyGenerationPercent);
        _stats?.SetOrbitOriginOverride(null);
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }

    // Public entry point for BWBlinkAbilityPD (called right after it teleports the caster to the blades'
    // live position) — snaps currentOrigin to atPosition and flips into Phase.Return (instead of calling
    // EndDance() directly, unlike the death-forced end above) so weapons visually coincide with the player
    // THIS tick regardless (EffectiveOrbitOrigin reads the override, which now equals atPosition —
    // numerically identical to the override being cleared outright), while letting Simulate()'s own
    // Return-phase arrival check (dist <= step, see its own case block above) be what actually finishes the
    // dance — normally one tick later, since dist starts at (essentially) zero. This reuses the exact same
    // "distance already closed" trigger a genuine Return trip ends on rather than a separate code path, and
    // gives the duration-bar UI its normal one-tick "Return phase, 0 fill, still visible" moment instead of
    // the bar disappearing in the exact same tick the recall was requested — see ActiveTimeRemaining's own
    // doc comment on this file.
    //
    // atPosition is a PARAMETER, not read from _stats.transform.position here, deliberately — the caller
    // (BWBlinkAbilityPD) already knows the exact position it just placed the caster at (blinkTarget), and
    // that's the value that must be used. Re-reading _stats.transform.position instead was the original bug:
    // Rigidbody interpolation can leave the caster's own VISUAL transform still catching up to a
    // just-assigned Rigidbody.position for a few frames after the teleport, so a read taken immediately
    // after landing could still return a stale, pre-teleport-ish position — pinning currentOrigin to that
    // stale point while the caster's own transform kept drifting toward the true target over the next few
    // frames produced a visible "blades chasing the player" shimmy instead of the intended instant snap.
    // Passing the already-known target sidesteps the staleness question entirely.
    public void RecallInstantly(Vector3 atPosition)
    {
        if (GetPhase(ref currentState) == Phase.Idle) return;

        currentState.currentOrigin = atPosition;
        _stats?.SetOrbitOriginOverride(currentState.currentOrigin);
        SetPhase(ref currentState, Phase.Return);
    }
}
