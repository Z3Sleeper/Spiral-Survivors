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
        public void Dispose() { }
    }

    private Phase GetPhase(ref State s) => (Phase)s.phase;
    private void SetPhase(ref State s, Phase p) => s.phase = (int)p;

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Blade Dance";
    [SerializeField, TextArea] private string description =
        "Send your blades to the target location. They stay there for a few seconds before returning.";
    [SerializeField] private float cooldown = 10f;
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
    [SerializeField] private float holdDuration = 5f;

    [Header("Ability Haste -> Attack Speed")]
    // AbilityHaste normally only ever shortens THIS ability's own cooldown (EffectiveCooldown below) — moot
    // at cooldown = 0, where dividing by (1 + haste/100) still always comes out to 0. So instead, for the
    // whole Outbound->Hold->Return window, AbilityHaste converts directly into bonus Attack Speed instead:
    // 1 point of EffectiveAbilityHaste = attackSpeedPercentPerHaste (1% by default) Total Attack Speed,
    // ADDED via AddAttackSpeedTotalPercent — then that haste-derived amount (not the player's whole Attack
    // Speed, just this bonus slice of it) is further multiplied by AbilityPower on top. Granted once at cast
    // (the fresh-cast branch only — a Hold recast doesn't re-grant, the buff already spans the whole
    // sequence) and reversed with the identical snapshotted value in EndDance(), same cast-time-snapshot
    // convention as RogueVanishAbilityPD/KnightBigBuffAbilityPD's own applied-percent fields (a live AbilityPower/
    // AbilityHaste change mid-dance must not desync the add/subtract pair). This also means blade orbit
    // speed itself (EffectiveWeaponSpeed = AttackSpeed * 360) speeds up too, along with anything else this
    // character's Attack Speed drives (FireRate, etc.) — not scoped to "just the blades," matching the
    // user's own framing of "give Bladeweaver attack speed."
    [SerializeField] private float attackSpeedPercentPerHaste = 0.01f;

    private CharacterStatsPD   _stats;
    private WeaponManagerPD    _weaponManager;
    private PlayerControllerPD _controller;

    // Snapshotted at cast time — see attackSpeedPercentPerHaste's own doc comment for why.
    private float _appliedAttackSpeedPercent;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats         = GetComponent<CharacterStatsPD>();
        _weaponManager = GetComponent<WeaponManagerPD>();
        _controller    = GetComponent<PlayerControllerPD>();
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
            if (phase == Phase.Idle) return currentState.cooldownTimer <= 0f;
            return phase == Phase.Hold;
        }
    }
    public bool IsActive => GetPhase(ref currentState) != Phase.Idle;
    // Never touches player movement itself — only ever repositions where weapons orbit (see class doc
    // comment), so it never locks dash or the other ability slot out while active.
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    public bool AbilityIsActive => GetPhase(ref currentState) != Phase.Idle;
    public float CooldownRemaining => Mathf.Max(0f, currentState.cooldownTimer);
    public float CooldownDuration => EffectiveCooldown;
    public float BaseCooldownDuration => cooldown;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;

    // Same formula as CharacterStatsPD.DashCooldown (baseCooldown / (1 + haste/100)) — a haste augment
    // shortens this ability's cooldown the same way it already does for dash, or lengthens it if
    // EffectiveAbilityHaste ever goes negative (a penalty).
    private float EffectiveCooldown => _stats != null ? cooldown / (1f + _stats.EffectiveAbilityHaste / 100f) : cooldown;

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

        currentState.cooldownTimer = EffectiveCooldown;

        Vector3 origin = _stats.transform.position;

        float trueReach = (_weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float r)
            ? r
            : _stats.WeaponRange) * castRangeMultiplier;

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

        // See attackSpeedPercentPerHaste's own doc comment for the full reasoning — AbilityHaste converts
        // straight into bonus Attack Speed for this ability's whole active window, further multiplied by
        // AbilityPower, since a 0-cooldown ability has no cooldown left for AbilityHaste to meaningfully act on.
        _appliedAttackSpeedPercent = _stats.EffectiveAbilityHaste * attackSpeedPercentPerHaste * _stats.AbilityPower;
        _stats.AddAttackSpeedTotalPercent(_appliedAttackSpeedPercent);

        currentState.targetPosition = origin + cursorDir * castDistance;
        currentState.currentOrigin  = origin;
        SetPhase(ref currentState, Phase.Outbound);

        // Set immediately (not left to wait for this identity's own Simulate() later this same tick) —
        // harmless no-op visually right now since currentOrigin still equals the player's own position, but
        // guarantees the override is already in place from the very first tick regardless of Simulate()
        // ordering between identities.
        _stats.SetOrbitOriginOverride(origin);

        // Skipped entirely at 0 (raw, un-hasted) cooldown — this is exactly the case that matters most for
        // this ability specifically, since it's the one designed to be recastable with no downtime at all
        // (see class doc comment's own "recall early" section) — firing "a real ability cast" notification
        // on every rapid cycle would spam anything listening for one (PhantomBladeOnAbilityStartAugment and
        // any future listener) far more often than intended.
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        var phase = GetPhase(ref state);

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole
        // Outbound->Hold->Return window) or only once that window ends (phase back to Idle) — see the
        // field's own doc comment. Same shape as RogueVanishAbilityPD.Simulate().
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || phase == Phase.Idle))
            state.cooldownTimer -= delta;

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
                    state.phaseTimer    = holdDuration;
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
                    EndDance(ref state);
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
        _stats?.AddAttackSpeedTotalPercent(-_appliedAttackSpeedPercent);
        _stats?.SetOrbitOriginOverride(null);
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }

    // Public entry point for BWBlinkAbilityPD (called right after it teleports the caster to the blades'
    // live position) — ends the dance from WHATEVER phase it's currently in (no travel, same instant-recall
    // shape as the death-forced end above), snapping every weapon straight back to normal orbit around the
    // player. Net effect of the two calls together: the player jumps to the blades, then the blades jump
    // back to the player, so both end up coinciding at the same point instead of the blades trailing behind
    // on their own Return trip afterward. No-op if the dance isn't running (Idle) — nothing to recall.
    public void RecallInstantly()
    {
        if (GetPhase(ref currentState) == Phase.Idle) return;
        EndDance(ref currentState);
    }
}
