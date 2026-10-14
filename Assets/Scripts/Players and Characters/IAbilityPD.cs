using UnityEngine;

// Which action-bar slot a component occupies — a real enum rather than a bool so a third/fourth slot can be
// added later (and so two components claiming the same slot on one GameObject is a visible, named mistake
// instead of a silently-overloaded "true/false" with no room to grow). See IAbilityPD.Slot's own doc comment.
public enum AbilitySlot
{
    Ability1,
    Ability2,
}

// Closed-set identity for every concrete IAbilityPD implementer (2026-10-02) — one entry per ability class,
// used by AugmentData.requiredAbilities so an ability-specific augment can require a particular ability
// without polluting the shared tags/requiredTags pool with a one-off, never-reused-elsewhere tag (that pool
// is for shared CAPABILITIES many augments carry, not one-to-one ability identity — see AugmentData's own
// doc comment). A real enum, not a string, so authoring is a dropdown with no typo risk and the set of
// values is naturally exactly the set of abilities that actually exist. See IAbilityPD.Identity's own doc
// comment for how a required value is actually checked against a player's currently-equipped abilities.
public enum AbilityIdentity
{
    RogueSpin,
    RogueVanish,
    KnightBerserk,
    KnightBuffStun,
    ExeBigAxe,
    ExeAxeThrow,
    ArcherArrowRain,
    ArcherFocusFire,
    BWBladeDance,
    BWBlink,
}

// Shared by every class-unique ability component (mirrors IWeaponPD/IEnemyPD's role as the generic contract
// PlayerControllerPD talks to without needing to know which concrete class it's holding). IsReady is the
// gate PlayerControllerPD checks before forwarding a press into Activate() — the underlying cooldown state
// behind IsReady is entirely owned by the implementing component, not PlayerControllerPD. Covers both
// action-bar slots (Ability1 and Ability2) — every character's two active abilities are functionally
// identical at this interface's level (both cooldown-gated, distinguishing themselves only via Slot below),
// so a single interface covers both rather than needing two near-duplicate ones.
public interface IAbilityPD
{
    bool IsReady { get; }
    void Activate();

    // True for the whole window an already-activated ability is in its special/locking state (RogueSpinAbilityPD's
    // leap) — distinct from IsReady, which also reads false while merely on cooldown, and distinct from
    // AbilityIsActive below, which tracks the ability's whole active duration rather than specifically the
    // locking portion of it. Combined with ForcesSpecialAction below by PlayerControllerPD.IsLockedOut to
    // decide whether this instance being active should block dash/other abilities right now.
    bool IsActive { get; }

    // Whether this ability, while IsActive, forces the player into a special action that should mutually
    // exclude with dash and any other locking ability (e.g. RogueSpinAbilityPD's leap — moves the player,
    // disables weapons, flips the orbit). False for one that doesn't touch movement at all (e.g.
    // RogueVanishAbilityPD — just flips invisibility/movespeed) — PlayerControllerPD.IsLockedOut (and the
    // equivalent Simulate()-level gate) only locks dash/other slots out while THIS is true AND IsActive,
    // never for a merely-active non-locking ability. Constant per concrete type, not state-dependent.
    bool ForcesSpecialAction { get; }

    // Whether this ability, while active, should cancel an in-progress Surge and prevent a new one from
    // starting for its whole active window — independent of ForcesSpecialAction (an ability can block Surge
    // without forcing a special action, e.g. BWBladeDanceAbilityPD, which never touches player movement at
    // all) and independent of IsWeaponsDisabled (a separate, orthogonal toggle — see its own doc comment).
    // Constant per concrete type, not state-dependent, same as ForcesSpecialAction. For an ability with no
    // active window at all (e.g. ArcherArrowRainAbilityPD/KnightBuffStunAbilityPD — AbilityIsActive is
    // always false) this is moot in practice, same as CooldownStartsOnCast's own doc comment notes for that
    // case — still exposed uniformly so the toggle exists on every implementer, ready to become meaningful
    // if that ability ever gains a real active window later. Each concrete implementer that DOES have an
    // active window owns calling CharacterStatsPD.ForceEndSurge()/AddSurgeBlock() at the very top of its own
    // Activate() (before anything else that might read Surge-dependent stats — see RogueSpinAbilityPD's own
    // comment for why that ordering matters) and CharacterStatsPD.RemoveSurgeBlock() at its own
    // end-of-active-window point, both gated on this flag — this is intentionally NOT centralized/
    // auto-detected by PlayerControllerPD, matching the same "the caller decides, by calling explicitly
    // alongside its own Activate()/end-of-active logic" precedent as NotifyDashStarted/NotifyDashEnded.
    bool ActiveSurgeBlock { get; }

    // Retired (2026-08-31) — "action vs buff" used to gate whether dashing or a forced Surge blocked an
    // ability's activation. Dash no longer blocks any ability at all, and Surge-blocking now uses
    // ActiveSurgeBlock directly (the same flag an ability already uses to block Surge in the other
    // direction), making this categorization fully redundant. See PlayerControllerPD.Simulate()'s own
    // ability-activation block and CancelSurgeBlockingAbility for the current rules.

    // "End my own active window right now, as a clean-but-forced cancel" (2026-08-31) — called by
    // PlayerControllerPD.CancelSurgeBlockingAbility() the instant a Berserk Surge starts, on whichever
    // ability slot is currently IsActive with its own ActiveSurgeBlock true (the reverse direction of
    // ActiveSurgeBlock — an ability already force-ends Surge when IT activates; this is what makes Surge
    // activating first cancel the ability instead). Distinct from NotifyActivationBlocked (a no-op-attempt
    // notification, never actually active) and from PlayerControllerPD.Interrupt()'s own
    // _onSpecialMovementInterrupted event (ForcesSpecialAction-scoped, not ActiveSurgeBlock-scoped — the two
    // sets overlap today only by coincidence, since RogueSpinAbilityPD is currently the sole implementer of
    // both). No-op for every implementer that never sets its own ActiveSurgeBlock true — only meaningful for
    // one that does (currently only RogueSpinAbilityPD, which routes this into its own existing
    // EndActiveWindow(interrupted: true) — see its own doc comment for why this uses interrupted: true,
    // unlike PlayerControllerPD's own dash-cancel case).
    void CancelActiveWindow();

    // True for the whole window this ability is "doing its thing" — RogueSpinAbilityPD's whole leap,
    // RogueVanishAbilityPD's whole invisibility/movespeed buff. Distinct from IsActive above: IsActive is
    // specifically the special/locking-movement signal PlayerControllerPD.IsLockedOut cares about, while
    // this is the generic "ability's active duration" signal — used to gate when the cooldown timer starts
    // ticking (see the concrete implementer's own cooldownStartsOnCast field) and as a general hook point
    // for any future "do something while this ability is active" effect. Today's two abilities happen to
    // have IsActive and AbilityIsActive span the exact same window (both driven by the same underlying
    // active-timer), but a future ability could decouple them — e.g. a brief locking dash at the start of a
    // longer, non-locking buff.
    bool AbilityIsActive { get; }

    // Whether this ability has a real active window AT ALL — false for an instantaneous ability
    // (ArcherArrowRainAbilityPD/KnightBuffStunAbilityPD/ExeAxeThrowAbilityPD — AbilityIsActive is always
    // false for these), true for anything with a timed buff/locking window (RogueSpinAbilityPD's leap,
    // RogueVanishAbilityPD's buff, etc.). Constant per concrete type, not state-dependent, same as
    // ForcesSpecialAction/ActiveSurgeBlock — a plain hardcoded property, NOT a serialized Inspector field
    // (nothing to tune per-instance here). Exists so generic UI (the ability duration bar) can tell "this
    // ability will never have an active window to show progress for" apart from "it just isn't active RIGHT
    // NOW" without needing per-concrete-type special-casing.
    bool HasActiveWindow { get; }

    // Only meaningful while AbilityIsActive AND HasActiveWindow are both true — how much of the active
    // window is left, and how long the window was in total (haste-scaled, matching whatever was
    // actually granted at cast time, same "Effective/Applied" convention CooldownRemaining/CooldownDuration
    // already use). ActiveTimeRemaining / ActiveTimeDuration gives a UI fill ratio that counts DOWN from 1 to
    // 0 over the window, same direction CooldownRemaining/CooldownDuration's own fill already counts. Return
    // 0 for both when HasActiveWindow is false — nothing should ever read them in that case, but they still
    // need a defined value.
    float ActiveTimeRemaining { get; }
    float ActiveTimeDuration { get; }

    // Called by PlayerControllerPD when a press was seen but IsReady was false, so the component itself can
    // report/log its own "still on cooldown" feedback — PlayerControllerPD never invokes Activate() in that
    // case, so this is the only hook the component gets for that outcome.
    void NotifyActivationBlocked();

    // Generic, regardless of concrete ability, so LocalUIManagerPD's ability UI (cooldown fill/text) never
    // needs to know which class it's reading — same reason every member above already exists. Every
    // implementer has a real cooldown now (no more resource-gated exception), so these are always
    // meaningful.
    float CooldownRemaining { get; }
    float CooldownDuration { get; }
    // The raw, un-hasted cooldown value (the concrete implementer's own tunable `cooldown` field) — distinct
    // from CooldownDuration above, which is scaled by CharacterStatsPD.EffectiveAbilityHaste for the actual
    // countdown/UI fill. For effects that scale off "how much cooldown this ability is designed around"
    // (e.g. PhantomBladeOnAbilityStartAugment, a 6s-base-cooldown ability dealing more than a 2s one)
    // rather than the player's current haste, which would make the effect's own power swing with a stat
    // that has nothing to do with the design intent.
    float BaseCooldownDuration { get; }

    // Charge system (2026-10-02) — "off cooldown" no longer directly means "castable"; IsReady now gates on
    // CurrentCharges > 0 instead (see each concrete implementer's own authoredChargeCount field). Every
    // cast spends exactly 1 charge; CooldownRemaining/CooldownDuration are REPURPOSED, not replaced — they
    // now represent progress toward the NEXT charge (ticking only while CurrentCharges < MaxCharges), so a
    // UI can still use the exact same fill-ratio formula it always has, just gated on a different visibility
    // condition (see LocalUIManagerPD/PlayerUIWorldSpace's own ChargeFill vs. the ability-icon cooldown
    // sweep). MaxCharges is authoredChargeCount PLUS whatever CharacterStatsPD.bonusAbility1Charges/
    // bonusAbility2Charges currently grants for this ability's own Slot — a live value, not fixed at spawn,
    // so a mid-run augment can raise it; each concrete implementer grants the newly-unlocked charge(s)
    // immediately the tick MaxCharges increases, rather than making the player wait out a full regen cycle
    // to benefit. For an ability authored with authoredChargeCount = 1 (the default) and no bonus, MaxCharges
    // is always 1 and CurrentCharges is always 0 or 1 — behaviorally identical to the old off-cooldown model.
    int CurrentCharges { get; }
    int MaxCharges { get; }

    // false (the default on every implementer): CooldownRemaining only starts ticking down once
    // AbilityIsActive goes false — the cooldown reflects "time since the ability ended". true: it starts
    // ticking immediately at cast, running concurrently with the active window instead. Each concrete
    // implementer owns its own `cooldownStartsOnCast` Inspector field and the Simulate()-level gating that
    // reads it (see e.g. RogueSpinAbilityPD.Simulate()'s own comment) — this is just the read-only surface
    // PlayerControllerPD/UI could query generically. For an ability with no active window at all (e.g.
    // KnightBuffStunAbilityPD, which is instantaneous — AbilityIsActive is always false) the two
    // settings are indistinguishable in practice: the cooldown always starts immediately at cast either
    // way, since there's no active window to wait out. The field is still exposed there so the toggle
    // exists uniformly across every ability, ready to become meaningful if that ability ever gains a real
    // active window later.
    bool CooldownStartsOnCast { get; }
    Sprite Icon { get; }

    // Cooldown-reduction hooks — for augments that want to shave time off whatever's left on an ability's
    // cooldown (e.g. OnHitReduceAbilityCDAugment) rather than granting a stat that only ever affects
    // FUTURE casts (EffectiveAbilityHaste). All three are safe to call on an ability that's already off
    // cooldown (CooldownRemaining == 0) — every implementer clamps its own cooldownTimer at 0, so calling
    // any of these on a ready ability is a harmless no-op rather than something callers need to guard for
    // themselves. Each concrete implementer owns an identical one-line body against its own
    // currentState.cooldownTimer field, same "duplicated small formula per class" convention
    // EffectiveCooldown's own shared shape already uses throughout this ability system.
    //
    // Flat: reduces remaining cooldown by a fixed number of seconds, unaffected by how much cooldown was
    // left or the ability's own total cooldown length.
    void ReduceCooldownFlat(float seconds);
    // Percent-of-remaining: reduces remaining cooldown by a fraction of whatever's CURRENTLY left — e.g. 0.5
    // on an ability with 4s left cuts it to 2s, but only 0.5s left on the same ability only cuts it to
    // 0.25s. percent01 is a 0..1 fraction (0.05 = 5%), clamped internally.
    void ReduceCooldownPercentRemaining(float percent01);
    // Percent-of-total: reduces remaining cooldown by a fraction of the ability's own EFFECTIVE (haste-
    // scaled) full cooldown length (CooldownDuration) — e.g. 0.5 always cuts off half of a 10s cooldown's
    // worth of time (5s) regardless of how much was actually left, capping at 0 rather than going negative
    // if less than that much remained. percent01 is a 0..1 fraction, clamped internally.
    void ReduceCooldownPercentTotal(float percent01);

    // A character can carry one of each (an Ability1-slot component and an Ability2-slot component) on the
    // same GameObject, which makes a plain GetComponent<IAbilityPD>() ambiguous — PlayerControllerPD/
    // CharacterSelectUI/AugmentManagerPD all use GetComponents<IAbilityPD>() plus this to route each
    // instance to the right slot instead of guessing from component order. Each character is only ever meant
    // to carry one component per AbilitySlot value — nothing enforces that at compile time. Concrete
    // implementers expose this as a plain [SerializeField], not a hardcoded constant, so which slot an
    // ability occupies is an Inspector/prefab-authoring choice rather than baked into the class — which also
    // means a copy-pasted prefab could end up with both components left on the same slot; whichever one
    // GetComponents finds last wins silently in that case, so it's worth checking after duplicating a
    // character prefab.
    AbilitySlot Slot { get; }

    // Fixed per concrete class (e.g. RogueSpinAbilityPD always returns AbilityIdentity.RogueSpin) — a plain
    // hardcoded property, NOT a serialized Inspector field, same "constant per concrete type, nothing to
    // tune per-instance" shape HasActiveWindow/ForcesSpecialAction already use on this interface. Checked by
    // AugmentManagerPD against AugmentData.requiredAbilities the same way Tags is checked against
    // requiredTags — a live GetComponents<IAbilityPD>() scan of the player's own CharacterStatsPD at offer
    // time, not a static class-to-ability lookup, so this stays correct even if ability loadouts ever become
    // swappable (whatever's actually equipped in a slot is what gets checked, not what a character "usually"
    // has).
    AbilityIdentity Identity { get; }

    // For display-only contexts (character select's detail panel) that want to show the ability in an
    // augment-card-style layout alongside the class's passive augments.
    string Name { get; }
    string Description { get; }

    // Matched against AugmentData.requiredTags by AugmentManagerPD.HasPickedAugmentForEachTag, same
    // AbilityTagDatabase strings augments themselves use — an ability is "always picked" from match start,
    // so it can satisfy an augment's tag requirement without the player needing to have picked anything yet.
    string[] Tags { get; }
}
