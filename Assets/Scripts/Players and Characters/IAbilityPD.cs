using UnityEngine;

// Which action-bar slot a component occupies — a real enum rather than a bool so a third/fourth slot can be
// added later (and so two components claiming the same slot on one GameObject is a visible, named mistake
// instead of a silently-overloaded "true/false" with no room to grow). See IAbilityPD.Slot's own doc comment.
public enum AbilitySlot
{
    Ability1,
    Ability2,
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
    // active window at all (e.g. ArcherArrowRainAbilityPD/KnightAreaStunHealAbilityPD — AbilityIsActive is
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

    // "Action" (true) vs "buff" (false) — a plain content categorization. Exposed as a tunable Inspector
    // field on every concrete implementer (not hardcoded) specifically so it can be flipped live during
    // playtesting to decide what should/shouldn't be allowed during a forced Surge, rather than needing a
    // recompile per change — see each implementer's own isActionAbility field. Not otherwise state-dependent
    // (never changes mid-match on its own). An action ability DOES something beyond granting stats/
    // buff-states — a committed movement (RogueSpinAbilityPD, BWBlinkAbilityPD), spawning a projectile/
    // hazard (ExeAxeThrowAbilityPD), or otherwise directly acting on the world. A buff ability only grants
    // stat changes or a temporary buff state on the caster (ArcherFocusFireAbilityPD, KnightBigBuffAbilityPD,
    // RogueVanishAbilityPD, ExeBigAxeAbilityPD) — nothing else happens. KnightAreaStunHealAbilityPD is a
    // buff by explicit exception (does act on the world, but the user's own call is that it's fine to allow
    // during a forced Surge anyway).
    //
    // Distinct from ActiveSurgeBlock just above (a completely different concern — "does this ability's own
    // active window fight an in-progress Surge" — several buff abilities are also ActiveSurgeBlock, e.g.
    // BWBladeDanceAbilityPD is an action AND blocks Surge; the two flags answer unrelated questions and
    // deliberately don't derive from each other). PlayerControllerPD.Simulate() reads this alone (not
    // ActiveSurgeBlock) to decide whether a Berserk-Surge-style forced Surge (CharacterStatsPD.
    // IsForcedSurging) blocks this specific ability — buff abilities stay usable through a forced Surge,
    // only action abilities are blocked. Dash itself is a deliberate exception — always allowed through a
    // forced Surge regardless of this flag, the one action the player keeps the whole time (see
    // PlayerControllerPD.Simulate()'s own dash-input comment). LocalUIManagerPD/PlayerUIWorldSpace read
    // the identical condition to show the on-cooldown background while blocked this way, even though the
    // ability's own CooldownRemaining/IsReady have nothing to do with why it's currently unusable.
    bool IsActionAbility { get; }

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
    // (ArcherArrowRainAbilityPD/KnightAreaStunHealAbilityPD/ExeAxeThrowAbilityPD — AbilityIsActive is always
    // false for these), true for anything with a timed buff/locking window (RogueSpinAbilityPD's leap,
    // RogueVanishAbilityPD's buff, etc.). Constant per concrete type, not state-dependent, same as
    // ForcesSpecialAction/ActiveSurgeBlock — a plain hardcoded property, NOT a serialized Inspector field
    // (nothing to tune per-instance here). Exists so generic UI (the ability duration bar) can tell "this
    // ability will never have an active window to show progress for" apart from "it just isn't active RIGHT
    // NOW" without needing per-concrete-type special-casing.
    bool HasActiveWindow { get; }

    // Only meaningful while AbilityIsActive AND HasActiveWindow are both true — how much of the active
    // window is left, and how long the window was in total (AbilityPower/haste-scaled, matching whatever was
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

    // false (the default on every implementer): CooldownRemaining only starts ticking down once
    // AbilityIsActive goes false — the cooldown reflects "time since the ability ended". true: it starts
    // ticking immediately at cast, running concurrently with the active window instead. Each concrete
    // implementer owns its own `cooldownStartsOnCast` Inspector field and the Simulate()-level gating that
    // reads it (see e.g. RogueSpinAbilityPD.Simulate()'s own comment) — this is just the read-only surface
    // PlayerControllerPD/UI could query generically. For an ability with no active window at all (e.g.
    // KnightAreaStunHealAbilityPD, which is instantaneous — AbilityIsActive is always false) the two
    // settings are indistinguishable in practice: the cooldown always starts immediately at cast either
    // way, since there's no active window to wait out. The field is still exposed there so the toggle
    // exists uniformly across every ability, ready to become meaningful if that ability ever gains a real
    // active window later.
    bool CooldownStartsOnCast { get; }
    Sprite Icon { get; }

    // Cooldown-reduction hooks — for augments that want to shave time off whatever's left on an ability's
    // cooldown (e.g. OnStrikeReduceAbilityCDAugment) rather than granting a stat that only ever affects
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

    // For display-only contexts (character select's detail panel) that want to show the ability in an
    // augment-card-style layout alongside the class's passive augments.
    string Name { get; }
    string Description { get; }

    // Matched against AugmentData.requiredTags by AugmentManagerPD.HasPickedAugmentForEachTag, same
    // AbilityTagDatabase strings augments themselves use — an ability is "always picked" from match start,
    // so it can satisfy an augment's tag requirement without the player needing to have picked anything yet.
    string[] Tags { get; }
}
