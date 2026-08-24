using PurrNet.Prediction;
using UnityEngine;

// Knight's second class ability, built on the IAbilityPD architecture. Reworked (2026-08-23) from a
// fixed-duration self-buff into a Berserk Surge trigger: casting starts a Berserk Surge
// (CharacterStatsPD.StartBerserkSurge — see its own doc comment for the full contract) and Taunts every
// enemy (CharacterStatsPD.SetTaunting) for the same window — EnemyBasePD/EnemySpawnerPD already react to
// SetTaunting immediately (wired ahead of this ability existing, see OnPlayerTauntChanged's own doc
// comment), so this only needs to flip the flag on and back off. No other stat grants — the Size/Weapon
// Range buffs this ability used to also grant moved over to KnightAreaStunHealAbilityPD instead (2026-08-23,
// same day, per explicit user request — that ability owns every STAT buff between the two, this one is the
// Berserk Surge trigger plus Taunt). Taunt itself was dropped in that same pass and re-added 2026-08-24 (the
// user forgot it was never restored) — per explicit instruction it stays active for the SURGE's entire
// duration, same event-driven window as everything else here, not a fixed timer of its own. All AbilityPower
// scaling was already removed earlier the same day and never came back.
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
public class KnightBigBuffAbilityPD : PredictedIdentity<KnightBigBuffAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public bool  isActive;
        public float cooldownTimer;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Resolve";
    [SerializeField, TextArea] private string description = "Start a Berserk Surge and taunt all enemies for its duration.";
    public float cooldown = 20f;
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
    // No stat changes granted (see class doc comment) — Taunt is a flag, not a stat. isActive tracks whether
    // the resulting Berserk Surge is currently active, for cooldown/UI/Taunt-window purposes. Exposed for
    // live playtesting — see IAbilityPD.IsActionAbility's own doc comment.
    [SerializeField] private bool isActionAbility = false;

    private CharacterStatsPD _stats;

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

    public bool IsReady => _stats != null && !currentState.isActive && currentState.cooldownTimer <= 0f;
    public bool IsActive => currentState.isActive;
    // Only ever flips stat totals — never touches movement, so it never locks dash or the other
    // ability slot out while active (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    public bool IsActionAbility => isActionAbility;
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

        // Deferred from Awake() — see class doc comment for why. Safe here: Activate() is never reachable
        // until well after CharacterStatsPD.LateAwake() has already assigned its real _onSurgeEnded.
        if (!_subscribedToSurgeEnded)
        {
            _stats.AddSurgeEndedListener(OnSurgeEnded);
            _subscribedToSurgeEnded = true;
        }

        currentState.isActive      = true;
        currentState.cooldownTimer = EffectiveCooldown;

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

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cycle (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }

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
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        // See the matching NotifyAbilityStart call in Activate() for why this is gated on cooldown > 0 too.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole active
        // window below) or only once that window ends (state.isActive false) — see the field's own doc
        // comment. Either way Activate() already set cooldownTimer to its full value at cast time. Same
        // shape as RogueVanishAbilityPD.Simulate().
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive))
            state.cooldownTimer -= delta;

        if (!state.isActive) return;

        // The buff's own end is normally entirely event-driven (OnSurgeEnded, above) — this is only a
        // backstop for a dead caster, since death never fires _onSurgeEnded on its own (see class doc
        // comment).
        if (_stats != null && _stats.IsDead) EndBuff(ref state);
    }
}
