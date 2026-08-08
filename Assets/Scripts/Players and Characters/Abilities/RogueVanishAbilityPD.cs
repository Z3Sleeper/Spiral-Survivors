using PurrNet.Prediction;
using UnityEngine;

// Rogue's second class ability, built on the IAbilityPD architecture — drops enemy aggro by going invisible
// (CharacterStatsPD.GrantInvisibility, a duration-max contribution, not a raw on/off — see its own doc
// comment) and grants +50% total move speed, both for the same fixed duration. Gated by a cooldown, same
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
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Vanish";
    [SerializeField, TextArea] private string description = "Become invisible and gain +50% Total Move Speed for 3 seconds.";
    public float cooldown = 20f;
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
    public float moveSpeedTotalPercent = 0.5f;

    // Mirrors Activate()'s own _appliedDuration formula exactly. Outside a live match, falls back to the
    // base field itself — exact at neutral AbilityPower (see ArcherFocusFireAbilityPD.LiveDurationRatio's
    // own doc comment for why), so one token covers both.
    public string LiveDurationRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? duration * stats.AbilityPower
            : duration;
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(value)} ";
    }

    private CharacterStatsPD _stats;
    // Flat — moveSpeedTotalPercent is not AbilityPower-scaled (see Activate()'s own comment), so this is
    // just a plain snapshot of the field at cast time rather than recomputed live at both Activate() and the
    // end-of-active reversal — same "fixed for the whole active window" reasoning as RogueSpinAbilityPD's own
    // cast-time snapshots (_weaponCountAtCast etc), just without any AbilityPower math involved here.
    private float _appliedMoveSpeedTotalPercent;
    // Snapshotted alongside it — see IAbilityPD.ActiveTimeDuration's own doc comment for why the UI needs
    // this fixed rather than recomputing duration * AbilityPower live off a since-changed AbilityPower.
    private float _appliedDuration;

    // The local-only screen tint toggle routes through PredictedEvent, not a raw call directly in
    // Simulate()/Activate() — same reasoning as every other one-shot side effect in this codebase: a
    // resimulated tick could otherwise retrigger it multiple times.
    private PredictedEvent _onActiveStarted;
    private PredictedEvent _onActiveEnded;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        _onActiveStarted = new PredictedEvent(predictionManager, this);
        _onActiveStarted.AddListener(() =>
        {
            // Only the local owning client's own screen shows the tint — LocalUIManagerPD is already
            // scoped to whichever player this machine owns, so gating on isOwner here is sufficient.
            if (isOwner) LocalUIManagerPD.instance?.SetInvisibleTintVisible(true);
        });

        _onActiveEnded = new PredictedEvent(predictionManager, this);
        _onActiveEnded.AddListener(() =>
        {
            if (isOwner) LocalUIManagerPD.instance?.SetInvisibleTintVisible(false);
        });
    }

    public bool IsReady => _stats != null && !currentState.isActive && currentState.cooldownTimer <= 0f;
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

    // See IAbilityPD's own doc comments for the exact contract of each of these three.
    public void ReduceCooldownFlat(float seconds) =>
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer - seconds);
    public void ReduceCooldownPercentRemaining(float percent01) =>
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer * (1f - Mathf.Clamp01(percent01)));
    public void ReduceCooldownPercentTotal(float percent01) =>
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer - CooldownDuration * Mathf.Clamp01(percent01));

    public void Activate()
    {
        if (!IsReady || _stats == null) return;

        // AbilityPower's interpretation for this ability: grows only the buff's own duration, same uniform
        // convention every buff ability in this codebase now follows (ExeBigAxeAbilityPD/
        // KnightBigBuffAbilityPD/BWBlinkAbilityPD). moveSpeedTotalPercent itself is flat, not AbilityPower-
        // scaled — a buff's own strength and its duration both scaling off the same stat on the same cast
        // would double-dip.
        float abilityPower = _stats.AbilityPower;
        _appliedMoveSpeedTotalPercent = moveSpeedTotalPercent;
        _appliedDuration              = duration * abilityPower;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        currentState.cooldownTimer = EffectiveCooldown;
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
        // off early. See CharacterStatsPD.GrantInvisibility's own doc comment.
        _stats.GrantInvisibility(_appliedDuration);
        _stats.AddMoveSpeedTotalPercent(_appliedMoveSpeedTotalPercent);

        _onActiveStarted.Invoke();
        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cycle (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole active
        // window below) or only once that window ends (state.isActive false) — see the field's own doc
        // comment. Either way Activate() already set cooldownTimer to its full value at cast time. Same
        // shape as RogueSpinAbilityPD.Simulate().
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive))
            state.cooldownTimer -= delta;

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
        // reverts here, since that's exclusively this ability's to give and take back.
        _stats?.AddMoveSpeedTotalPercent(-_appliedMoveSpeedTotalPercent);
        _onActiveEnded.Invoke();
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        // See the matching NotifyAbilityStart call in Activate() for why this is gated on cooldown > 0 too.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }
}
