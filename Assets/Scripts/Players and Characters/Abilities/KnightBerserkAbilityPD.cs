using PurrNet.Prediction;
using UnityEngine;

// Knight's second class ability, built on the IAbilityPD architecture. Reworked (2026-08-23) from a
// fixed-duration self-buff into a Berserk Surge trigger: casting starts a Berserk Surge
// (CharacterStatsPD.StartBerserkSurge — see its own doc comment for the full contract) and Taunts every
// enemy (CharacterStatsPD.SetTaunting) for the same window — EnemyBasePD/EnemySpawnerPD already react to
// SetTaunting immediately (wired ahead of this ability existing, see OnPlayerTauntChanged's own doc
// comment), so this only needs to flip the flag on and back off. No other stat grants — the Size/Weapon
// Range buffs this ability used to also grant moved over to KnightBuffStunAbilityPD instead (2026-08-23,
// same day, per explicit user request — that ability owns every STAT buff between the two, this one is the
// Berserk Surge trigger plus Taunt). Taunt itself was dropped in that same pass and re-added 2026-08-24 (the
// user forgot it was never restored) — per explicit instruction it stays active for the SURGE's entire
// duration, same event-driven window as everything else here, not a fixed timer of its own. All AbilityPower
// scaling was already removed earlier the same day and never came back.
//
// Energy snapshot-and-refund (2026-09-17) — the whole reason this ability got any real use before was Surge
// being a "free" resource dump on Knight (energy usually refilled to max well before this came off cooldown,
// even sooner with ability haste), making Surge as a universal mechanic something you never had to think
// about on this class. Explicit fix, per design: Activate() snapshots CurrentEnergy the instant the ability
// starts; EndBuff() (every end path funnels through it — natural Surge drain-out, a hard Interrupt, or the
// dead-caster backstop below) refunds that EXACT snapshotted amount via CharacterStatsPD.SetEnergyRaw, a
// direct write immune to bonusEnergyGainPercent or any other energy-gain modifier (per explicit design — this
// is returning energy the player already had, not a new "gain" event that anything should be able to
// influence). Net effect: casting this now genuinely spends your current energy pool for its duration (Surge
// still drains it live the whole time, same as any Surge) and hands the ORIGINAL amount straight back the
// moment it ends, rather than being a functionally-free trigger.
//
// Duration is event-driven, not timed: the buff ends the instant the resulting Berserk Surge itself ends
// (_onSurgeEnded), whether that's the Surge naturally draining dry or a hard interrupt cutting it short
// (PlayerControllerPD.Interrupt() -> CharacterStatsPD.ForceEndSurge(), see both of their own doc comments —
// Berserk Surges are deliberately interruptible even though a normal player-requested Surge end is not).
//
// The _onSurgeEnded subscription is deliberately deferred to the FIRST Activate() call, not Awake() —
// CharacterStatsPD.LateAwake() is what actually assigns its _onSurgeEnded field (a fresh PredictedEvent
// instance), and that runs strictly AFTER Unity's own Awake() on every component (LateAwake is PurrDiction's
// own post-spawn hook, invoked once Instantiate() and all native Awake() calls have already completed — see
// PredictedIdentity.Setup()). Subscribing from THIS component's own Awake() would either NRE outright (if
// _onSurgeEnded is still null at that point) or, if it happened to already exist, get silently wiped moments
// later when LateAwake() overwrites it with a brand new instance — either way, the listener never actually
// ends up attached. Activate() can only ever be reached via live gameplay input long after LateAwake() has
// run, so subscribing there sidesteps the ordering question entirely rather than depending on component
// order between this ability and CharacterStatsPD on the same GameObject.
//
// The ability-bar duration UI (LocalUIManagerPD) only ever renders ActiveTimeRemaining / ActiveTimeDuration
// as a 0-1 fill ratio — never a raw "X seconds" number — so both properties are repurposed here to report
// ENERGY instead of time (CurrentEnergy / MaxEnergy). This makes the bar track "how much energy is left"
// live, refilling in place if energy refills mid-buff (e.g. via OnStrikeEnergyRegenAugment), with zero
// separate UI code needed.
//
// A dead caster still needs an explicit check to force the buff off (Simulate()'s own death check below) —
// CharacterStatsPD.TakeDamage's death branch never touches isSurging/forcedSurge itself, so death alone would
// otherwise never fire _onSurgeEnded and leave these stats stuck on a corpse (same reasoning as the OLD
// version of this file's own forceEndOnDeath, itself citing ExeBigAxeAbilityPD.Simulate()'s identical comment
// for the full background).
public class KnightBerserkAbilityPD : PredictedIdentity<KnightBerserkAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public bool  isActive;
        public float cooldownTimer;
        // Snapshotted in Activate(), restored (via CharacterStatsPD.SetEnergyRaw — bypasses every energy-gain
        // modifier, see that method's own doc comment) in EndBuff() regardless of which end path got there.
        // See class doc comment for the full 2026-09-17 redesign this belongs to.
        public float energySnapshot;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment.
        public int   charges;
        public int   knownMaxCharges;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Resolve";
    [SerializeField, TextArea] private string description = "Start a Berserk Surge and taunt all enemies for its duration.";
    public float cooldown = 20f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility1Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full value
    // throughout the buff window) — see Simulate(). true: cooldown starts ticking immediately at cast,
    // running concurrently with the active window. Same convention/default as RogueVanishAbilityPD's own.
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability1;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract.
    [SerializeField] private bool activeSurgeBlock = false;

    // 0f (no-op) until an augment (e.g. KnightBerserkMoveSpeedAugment) bumps it — Total-tier percent, via
    // AddMoveSpeedTotalPercent, applied only while this ability's own Berserk window is active (Activate()/
    // EndBuff() below), same Add/Remove-at-start/end pairing RogueVanishAbilityPD/BWBlinkAbilityPD already use
    // for their own temporary move-speed buffs. Kept as a plain field here rather than a new CharacterStatsPD
    // stat specifically because this bonus only exists while Berserk is actually running — CharacterStatsPD's
    // own stat formulas never need to know about it.
    // [HideInInspector] — same reasoning as lowHealthCooldownBonusMultiplier/lowHealthThreshold below:
    // KnightBerserkMoveSpeedAugment owns the initial value and all tuning, nothing on this ability's own
    // prefab should need hand-authoring it.
    [HideInInspector] public float moveSpeedPercent = 0f;

    // 0f (no-op) until KnightBerserkLowHealthCDHastenAugment sets it — extra multiplier added on top of the
    // normal 1x cooldown-tick rate while CurrentHealth/MaxHealth is below lowHealthThreshold (1f = twice as
    // fast overall). Checked directly in Simulate()'s own cooldown tick below — a plain cross-identity READ
    // of _stats.CurrentHealth/MaxHealth (same safety class as the _stats.IsDead read already in this file's
    // Simulate()), so no deferred/UpdateView dance is needed the way a cross-identity WRITE would require.
    // [HideInInspector] — the augment owns BOTH the initial value and all tuning for these two; nothing on
    // this ability's own prefab should ever need hand-authoring them (that would just be a second, easy-to-
    // forget-about place to look alongside the augment's own copy).
    [HideInInspector] public float lowHealthCooldownBonusMultiplier = 0f;
    [HideInInspector] public float lowHealthThreshold = 0.5f;

    // 0f (no-op) until KnightBerserkEnergyGainAugment sets it — additive against CharacterStatsPD.
    // bonusEnergyGainPercent's own 1f baseline (AddEnergyGainPercent, same mutator EnergyGainAugment's own
    // permanent +100% uses), applied only while Berserk is active — same Add-at-Activate/Remove-at-EndBuff
    // pairing as moveSpeedPercent above. Deliberately routes through the SAME mutator/field EnergyGainAugment
    // does (not a separate Berserk-only stat) so the two stack additively if the player has both — see that
    // augment's own doc comment.
    // [HideInInspector] — same reasoning as moveSpeedPercent above.
    [HideInInspector] public float energyGainPercent = 0f;

    private CharacterStatsPD _stats;
    // Snapshot of moveSpeedPercent at Activate() time, same reasoning as RogueVanishAbilityPD's own
    // _appliedMoveSpeedPercent — EndBuff() must subtract back exactly what was added, even if moveSpeedPercent
    // somehow changed mid-buff.
    private float _appliedMoveSpeedPercent;
    // Snapshot of energyGainPercent at Activate() time, same reasoning as _appliedMoveSpeedPercent above.
    private float _appliedEnergyGainPercent;

    // Guards the deferred _onSurgeEnded subscription (see class doc comment for why it can't happen in
    // Awake()) so a second Activate() doesn't stack a duplicate listener, and so OnDestroy() only ever
    // unsubscribes something it actually subscribed.
    private bool _subscribedToSurgeEnded;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_subscribedToSurgeEnded && _stats != null) _stats.RemoveSurgeEndedListener(OnSurgeEnded);
    }

    public bool IsReady => _stats != null && !currentState.isActive && currentState.charges > 0;
    public bool IsActive => currentState.isActive;
    // Only ever flips stat totals — never touches movement, so it never locks dash or the other
    // ability slot out while active (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    // Same underlying window as IsActive for this ability (the whole buff) — see IAbilityPD.
    // AbilityIsActive's own doc comment for why these are separate interface members even though they
    // agree here.
    public bool AbilityIsActive => currentState.isActive;
    public bool HasActiveWindow => true;
    // Repurposed to report energy, not time — see class doc comment.
    public float ActiveTimeRemaining => _stats != null ? _stats.CurrentEnergy : 0f;
    public float ActiveTimeDuration  => _stats != null ? _stats.MaxEnergy : 0f;
    public float CooldownRemaining => Mathf.Max(0f, currentState.cooldownTimer);
    public float CooldownDuration => EffectiveCooldown;
    public float BaseCooldownDuration => cooldown;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
    public Sprite Icon => icon;
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;
    public AbilityIdentity Identity => AbilityIdentity.KnightBerserk;
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
    // hasn't ended yet to let it start ticking.
    // MaxCharges > 1 (2026-10-02) — see Simulate()'s own identical addition for why a multi-charge ability
    // always ticks its regen timer regardless of isActive.
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

        // Deferred from Awake() — see class doc comment for why. Safe here: Activate() is never reachable
        // until well after CharacterStatsPD.LateAwake() has already assigned its real _onSurgeEnded.
        if (!_subscribedToSurgeEnded)
        {
            _stats.AddSurgeEndedListener(OnSurgeEnded);
            _subscribedToSurgeEnded = true;
        }

        currentState.isActive      = true;
        // Charge system — spend 1, and only (re)arm the regen timer if it wasn't already running — see
        // IAbilityPD.MaxCharges's own doc comment.
        currentState.charges--;
        if (currentState.cooldownTimer <= 0f) currentState.cooldownTimer = EffectiveCooldown;
        // Snapshot BEFORE StartBerserkSurge()/SetTaunting() below touch anything else — see State.
        // energySnapshot's own doc comment. Restored verbatim in EndBuff() via SetEnergyRaw, regardless of
        // how much (if any) the Surge itself drains this down to in the meantime.
        currentState.energySnapshot = _stats.CurrentEnergy;

        // Direct cross-identity mutation, same pattern as RogueVanishAbilityPD.Activate() — safe since
        // Activate() is only ever called from PlayerControllerPD's own Simulate(), deterministically,
        // identically on every machine.
        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in EndBuff() below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }
        // StartBerserkSurge() safely piggybacks onto an already-active Surge (normal or Berserk) rather than
        // double-firing anything — see its own doc comment. The whole point of this ability now.
        _stats.StartBerserkSurge();
        _stats.SetTaunting(true);

        // 0f (no-op) until an augment bumps moveSpeedPercent — see that field's own doc comment.
        _appliedMoveSpeedPercent = moveSpeedPercent;
        if (_appliedMoveSpeedPercent != 0f) _stats.AddMoveSpeedTotalPercent(_appliedMoveSpeedPercent);
        // 0f (no-op) until KnightBerserkEnergyGainAugment bumps energyGainPercent — see that field's own
        // doc comment.
        _appliedEnergyGainPercent = energyGainPercent;
        if (_appliedEnergyGainPercent != 0f) _stats.AddEnergyGainPercent(_appliedEnergyGainPercent);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cycle (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment. (Also
    // moot for a different reason: this ability's whole active window IS a Berserk Surge, so
    // PlayerControllerPD.CancelSurgeBlockingAbility() only ever runs on the tick a Surge STARTS, before this
    // could ever be the ability already occupying that Surge.)
    public void CancelActiveWindow() { }

    // _onSurgeEnded fires for ANY Surge ending, not just one this ability itself started (e.g. a plain Surge
    // that was never Berserk at all) — the isActive guard is what makes reacting to it unconditionally safe.
    // Named method, not a lambda: currentState is a ref-returning property, and a ref access on it can't be
    // captured inside a closure (same reasoning as KingOrbiterPD.OnAoEInterrupt).
    private void OnSurgeEnded()
    {
        if (!currentState.isActive) return;
        EndBuff(ref currentState);
    }

    private void EndBuff(ref State state)
    {
        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        _stats?.SetTaunting(false);
        if (_appliedMoveSpeedPercent != 0f) _stats?.AddMoveSpeedTotalPercent(-_appliedMoveSpeedPercent);
        if (_appliedEnergyGainPercent != 0f) _stats?.AddEnergyGainPercent(-_appliedEnergyGainPercent);
        // Refunds the energy snapshotted at Activate() — every end path (natural Surge drain-out, a hard
        // Interrupt, or the dead-caster backstop in Simulate()) routes through this one method, so this one
        // call covers all of them. SetEnergyRaw is a direct, unmodified write — see its own doc comment for
        // why this is deliberately immune to bonusEnergyGainPercent/forcedSurge, unlike GainEnergy().
        _stats?.SetEnergyRaw(state.energySnapshot);
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        // See the matching NotifyAbilityStart call in Activate() for why this is gated on cooldown > 0 too.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

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
            // Low-health cooldown haste (2026-10-02) — 0f/no-op until KnightBerserkLowHealthCDHastenAugment
            // sets lowHealthCooldownBonusMultiplier — see that field's own doc comment.
            float cooldownRate = 1f;
            if (lowHealthCooldownBonusMultiplier != 0f && _stats != null && _stats.MaxHealth > 0f &&
                _stats.CurrentHealth / _stats.MaxHealth < lowHealthThreshold)
                cooldownRate += lowHealthCooldownBonusMultiplier;

            // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole
            // active window below) or only once that window ends (state.isActive false) — see the field's
            // own doc comment. Same shape as RogueVanishAbilityPD.Simulate().
            // effectiveMax > 1 (2026-10-02) — see RogueVanishAbilityPD.Simulate()'s own identical addition.
            if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive || effectiveMax > 1))
                state.cooldownTimer -= delta * cooldownRate;

            if (state.cooldownTimer <= 0f)
            {
                state.charges++;
                state.cooldownTimer = state.charges < effectiveMax ? EffectiveCooldown : 0f;
            }
        }

        if (!state.isActive) return;

        // The buff's own end is normally entirely event-driven (OnSurgeEnded, above) — this is only a
        // backstop for a dead caster, since death never fires _onSurgeEnded on its own (see class doc
        // comment).
        if (_stats != null && _stats.IsDead) EndBuff(ref state);
    }
}
