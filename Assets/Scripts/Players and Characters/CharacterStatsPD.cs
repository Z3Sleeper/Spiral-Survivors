using System;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction;
using PurrNet.Steam;
using UnityEngine;

// Payload for CharacterStatsPD._onDamaged. Two distinct mitigation stages, not one blended number, since
// different listeners legitimately want different ones — e.g. PhantomBladeOnDamagedAugment scales off
// preMitigationDamage (the retaliation should reflect how hard the player was actually hit, not how well
// their own defenses happened to blunt it), whereas a future "gain X per point of damage actually taken"
// effect would want postMitigationDamage instead.
public struct DamageTakenData
{
    // Raw incoming damage, before DamageReduction or shield absorption touch it at all.
    public float preMitigationDamage;
    // What's left after BOTH DamageReduction and shield absorption had their chance — the actual amount
    // that goes on to affect health (modulo the separate death-save/PreventLethalDamage clamp, which isn't
    // reflected here; this is "damage after DR+shield", not "exact health lost").
    public float postMitigationDamage;
}

// Payload for CharacterStatsPD._onStatusPopup. kind tells DamageNumberManager WHAT happened — for anything
// other than Custom, DamageNumberManager owns and resolves the actual message/color itself (see its own
// StatusPopupKind/Resolve* methods), so this class doesn't need to know or hardcode presentation details
// like Berserk's own default text/color. customMessage/colorOverride are only consulted for Custom (or as
// an explicit per-call override on a known kind, if ever needed) — DamageNumberManager's resolution always
// prefers an explicit override over its own per-kind default. Safe as a struct field here since
// PredictedEvent<T> never serializes T (see PredictedEvent.cs — it's a purely local, in-process call gate),
// no networking/codegen constraint like an IPredictedData<T> State struct would have.
public struct StatusPopupData
{
    public DamageNumberManager.StatusPopupKind kind;
    public string customMessage;
    public Color? colorOverride;
    // Multiplies DamageNumber's own shared _statusDuration for just this popup (1 = default, 2 = double
    // length, etc.) — see DamageNumberManager.SpawnStatusOnPlayer's own doc comment for the full threading.
    public float durationMultiplier;
}

// Payload for CharacterStatsPD._onPlayerDamageNumber — the actual shield/health deltas a single TakeDamage
// call produced (plain before/after diffs, see TakeDamage's own shieldBeforeHit/healthBeforeHit comment),
// NOT the same thing as DamageTakenData's pre/postMitigationDamage above (those describe the incoming hit
// before shield/health split it; these describe what each pool actually lost, correct even through the
// death-prevention clamp-to-1 case). Either field can be 0 (a hit fully absorbed by shield never touches
// health and vice versa) — OnPlayerDamageNumber only spawns a popup for whichever field is actually > 0,
// same "shield and health print two separate numbers" rule as a hit that splits across both.
public struct PlayerDamageNumberData
{
    public float shieldDamage;
    public float healthDamage;
}

// Payload for CharacterStatsPD._onHeal — amount is the actual (already-clamped-to-MaxHealth) healed delta,
// same value currentState.health was actually raised by. isCrit is only ever true from Heal() itself (see
// RollHealMultiplier's own doc comment) — HealFromAlly() and the passive regen tick (which doesn't even
// invoke this event at all) never roll a crit.
public struct HealData
{
    public float amount;
    public bool isCrit;
}

// Payload for CharacterStatsPD._onEnergySpent — amount is the raw energy deducted (SurgeCost, currently the
// only energy-spend source in this codebase).
public struct EnergySpentData
{
    public float amount;
}

// Payload for CharacterStatsPD._onAllyHealGranted — fires on the HEALER's own Stats (not the receiving
// ally's), the instant HealFromAlly() lands on someone else. amount is the real, already-clamped-to-
// MaxHealth healed delta the ally actually received (same value HealData.amount would carry on the ally's
// own _onHeal), not the raw pre-clamp amount the healer's own effect requested.
public struct AllyHealGrantedData
{
    public float amount;
}

// Payload for CharacterStatsPD._onStrikeHighroll (2026-08-19) — damage is the actual final rolled damage
// (post crit+highroll, same value passed to NotifyStrikeHighroll), NOT the same thing as WeaponHitData.
// weaponDamage (that struct's own field is the raw pre-roll base stat) — kept as its own dedicated struct
// rather than reusing WeaponHitData to avoid conflating the two different meanings. onHitPower is whatever
// this specific hit's own onHitPower value was (ranged dropoff/axe handle penalty/etc. already applied,
// same as WeaponHitData.onHitPower), added so a listener (e.g. OnStrikeHighrollReduceDashCDAugment) can
// scale its own effect by it without needing a second, separately-correlated event.
public struct StrikeHighrollData
{
    public float damage;
    public float onHitPower;
}

public class CharacterStatsPD : PredictedIdentity<CharacterStatsPD.State>
{
    public static readonly Dictionary<PlayerID, CharacterStatsPD> All = new();

    // True if at least one player is currently a legal enemy-targeting candidate — same rule
    // EnemyBasePD.FindClosestTarget/IsTargetStillValid already use (not dead, and either visible or
    // taunting). Recomputed inline at every point that can actually flip it — SetTaunting, SetInvisible,
    // the death transition in TakeDamage, Revive(), plus PlayerDataPD's own All[owner]=stats/All.Remove
    // (joining/disconnecting changes candidacy just as much as an in-place flag flip) — rather than polled;
    // cheap enough (≤4 players) to just recompute outright on each of those rare events. Lets
    // FindClosestTarget's every caller — including EnemyBasePD.Simulate()'s once-per-tick "target is null"
    // fallback, which can run for every active enemy every tick — skip its own O(players) distance-check
    // loop entirely with one O(1) bool read when nothing valid exists anywhere right now (e.g. the last
    // player standing goes invisible).
    public static bool AnyValidTargetExists { get; private set; }

    // Public, not private — see the doc comment above for every call site.
    public static void RefreshAnyValidTargetExists()
    {
        foreach (var p in All.Values)
        {
            if (!p.currentState.isDead && (!p.currentState.invisible || p.currentState.taunting))
            {
                AnyValidTargetExists = true;
                return;
            }
        }
        AnyValidTargetExists = false;
    }

    // Static, not per-instance PredictedEvent — fired from EnemyBasePD.UpdateView() off verified state with
    // its own edge-detection (isDead && !_wasDeadLastFrame), the same hand-rolled pattern already
    // established as functionally equivalent to PredictedEvent (see GameManagerPD.OnGameEnded), so it's
    // already safe to fire directly here without needing the PredictedEvent machinery itself. Fires for
    // every enemy death on every machine, regardless of killer (killer may be null if it doesn't resolve to
    // a living player) — a "my own kills only" augment filters by comparing killer against its own Stats
    // instance, which evaluates identically on every machine since killer/enemy are resolved the same way
    // everywhere. (A separate OnEnemyKill used to exist, gated to only firing on the killer's own local
    // machine — removed since it had zero subscribers and that owner-only gating would have desynced any
    // effect that needs to mutate shared/enemy state, like OnKillLaunchEnemiesAugment's corpse launch.)
    public static event Action<CharacterStatsPD, IEnemyPD> OnEnemyDeath;
    public static void RaiseOnEnemyDeath(CharacterStatsPD killer, IEnemyPD enemy) => OnEnemyDeath?.Invoke(killer, enemy);

    public enum CharacterClass { Knight, Rogue, Archer, Bladeweaver, Executioner }
    public CharacterClass characterClass;

    [SerializeField] private ClassPassiveData classPassive;
    // Read by AugmentManagerPD.RegisterClassPassiveAugments — see ClassPassiveData.passiveAugments' own
    // doc comment for why this is registered as a real pick rather than spawned directly here.
    public AugmentData[] ClassPassiveAugments => classPassive != null ? classPassive.passiveAugments : null;
    // Read by CharacterSelectUI.RefreshCharacterDetails — flavor text only, no gameplay effect.
    public string ClassDescription => classPassive != null ? classPassive.classDescription : string.Empty;

    // -1 = AugmentManagerPD.RegisterClassPassiveAugments() hasn't run for this player yet, so the real
    // count isn't known — deliberately NOT "ready" in this state (unlike 0), so PlayerDataPD's readiness
    // gate can't slip through during the one-tick lag between this player existing and that registration
    // pass actually running (see RegisterClassPassiveAugments' own doc comment on that lag). Once it calls
    // BeginWaitingForClassPassiveAugments with the real spawned count (0 if this class has no unique-effect
    // passives at all), this only ever counts down as each spawned effect confirms via NotifyAugmentApplied.
    private int _pendingClassPassiveAugments = -1;

    // True once every class-passive augment this player was granted has actually applied its OnApply()
    // effect — not just been spawned. Distinct from the plain-stat block ApplyClassPassives() already
    // applies synchronously; this covers the passiveAugments[] escape hatch specifically, whose effects
    // resolve asynchronously (spawn -> verified state -> OnApply). Used to hold PlayerDataPD's "player
    // ready" gate open long enough that the loading screen can't drop before those stats are actually live
    // (fixes a visible "stat pops in after load" flash, e.g. a class-passive max-health bonus).
    public bool ClassPassiveAugmentsReady => _pendingClassPassiveAugments == 0;

    // Called once by AugmentManagerPD.RegisterClassPassiveAugments() with the exact number of this
    // player's class-passive augment effects it actually spawned this pass — the one place that knows
    // that count for certain (SpawnAugmentEffect can silently no-op, e.g. a database-only augment with no
    // effectPrefab, so the count must come from confirmed spawns, not just array length).
    public void BeginWaitingForClassPassiveAugments(int count)
    {
        _pendingClassPassiveAugments = count;
    }

    // Called by AugmentEffectPD once its own OnApply() has actually run (confirmed off verified state) —
    // every augment effect reports here, not just class-passive ones, but the guard means a normal
    // level-up pick's effect (always well after game start, once this is already at 0) is a harmless
    // no-op.
    public void NotifyAugmentApplied()
    {
        if (_pendingClassPassiveAugments > 0) _pendingClassPassiveAugments--;
    }

    // ── Serialized base values ─────────────────────────────────────────────

    [Header("Movement")]
    public float baseMoveSpeed = 10f;

    [Header("Weapon")]
    // Moved above strengthPerLevel (2026-08-19) — [Header] attaches to the next member Unity's Inspector
    // actually draws, and strengthPerLevel is a computed property with no SerializedProperty backing, so a
    // header placed above it silently drops (same bug/fix as baseDashCooldown's own field comment describes
    // in full — see it for the complete mechanism). Confirmed live: "Weapon" was rendering as if still part
    // of "Movement".
    public float baseStrength = 10f;
    // In State, not a plain field — mutated at runtime via ApplyProgressionBonuses (see that method's own
    // ApplyStatChange lambda), same as the other 3 progression-only fields (dashCharges/levelUpOptions/
    // shieldRechargeTime, declared further down) that used to be considered safe purely because their only
    // writer runs from a verified/non-Simulate() context — migrated anyway for consistency, since nothing
    // enforces that stays true for a future writer. Not Inspector-visible, same reasoning as dashCharges.
    public float strengthPerLevel => currentState.strengthPerLevel != 0f ? currentState.strengthPerLevel : 5f;
    // Scaffolding for the unlockable-augment-sets system — see AugmentUnlockTagDatabase's own doc comment
    // for why bit position == database list index. Read by AugmentManagerPD.IsAugmentTagUnlocked at
    // eligibility-check time — has to be real replicated State (not a plain field/direct ProgressionManager
    // read) since that check runs deterministically for ANY player from ANY machine, same reasoning as every
    // other progression bonus in this class (see ComputeProgressionBonuses/ApplyProgressionBonuses).
    public long UnlockedAugmentTagMask => currentState.unlockedAugmentTagMask;
    public float baseWeaponRadius = 0f;
    public float baseAttackSpeed = 0.5f;
    public int baseWeaponCount = 1;
    // Extra weapon "tracks" (e.g. the planned OuterWeaponTrackAugment), feeding into Strength's own
    // auto-divide below. Deliberately a single plain int, not a base/flat/multiplier scaffold like
    // baseWeaponCount — this is intentionally-unused-elsewhere bookkeeping, directly += a small integer
    // whenever tracks are added or removed (see AddWeaponTracks). In State, not a plain field — has a real
    // runtime mutator (AddWeaponTracks) unlike baseWeaponCount, so same reasoning as every other mutable
    // field in this class.
    public int weaponTracks => currentState.weaponTracks != 0 ? currentState.weaponTracks : 1;
    public float baseWeaponRange = 10f;

    [Header("Crit")]
    public float baseCritChance = 0f;
    // 2f -> 3f (2026-08-19 highroll/crit rework) — see baseHighrollBonus's own doc comment for the full
    // reasoning; a crit alone now does 300% damage, matching Highroll's own new value, and the two
    // additively stack (see WeaponDamageMath.ApplyCritAndHighroll) instead of multiplying together.
    public float baseCritDamage = 3f;

    [Header("Ranged Weapons")]
    public float baseRicochet = 0f;
    public float baseMultishot = 0f;
    public float baseMultishotSpreadAngle = 75f;
    public float baseProjectileDamageDropoff = 0.2f;
    public float baseProjectileOnHitDropoff = 0.2f;
    // Neutral 1f baseline, same as every other "base" stat here — WeaponRangedPD reads FireRate/
    // ProjectileSpeed off this instead of carrying its own local fields, so a class passive (e.g. Archer's
    // fireRateFlat/projectileSpeedFlat) can tune a specific weapon's feel without a weapon-specific field.
    public float baseFireRate = 1f;
    public float baseProjectileSpeed = 1f;

    [Header("Health")]
    public float baseMaxHealth = 50f;
    public float baseHealthRegen = 0f;

    [Header("Shield")]
    public float baseMaxShieldPercent = 0.05f;
    // In State, not a plain field — mutated at runtime via ApplyProgressionBonuses, see strengthPerLevel's
    // own doc comment for why.
    public float shieldRechargeTime => currentState.shieldRechargeTime != 0f ? currentState.shieldRechargeTime : 5f;

    [Header("Death Prevention")]
    // Only ever a single banked save at a time (see HasDeathPrevention) — how long it takes to (re-)earn
    // one is baseDeathPreventionTime seconds, scaled by deathPreventionTimeMultiplier (neutral 1f, a lower
    // value grants faster), PLUS an unreducible flat floor equal to deathPreventionInvincibilityDuration —
    // see the DeathPreventionTime property below for why. See Simulate()'s own comment for the full
    // grant-timer logic.
    public float baseDeathPreventionTime = 30f;
    // In State, not a plain field — ActivateDeathPreventions() mutates this at runtime (a second augment
    // that grants the mechanic compounds it down instead of no-oping), so it needs the same rollback safety
    // as every other runtime-mutable stat modifier in this class. Neutral-1f-fallback convention, same
    // shape as totalStrengthPercent/etc.
    public float deathPreventionTimeMultiplier => currentState.deathPreventionTimeMultiplier != 0f ? currentState.deathPreventionTimeMultiplier : 1f;
    // deathPreventionInvincibilityDuration is added AFTER the multiplier, not multiplied by it — a flat,
    // unreducible floor so stacking deathPreventionTimeMultiplier down toward 0 (repeat picks of the
    // mechanic, or any future haste-style augment) can never shrink the total below the invincibility window
    // itself. Without this, a low enough multiplier could re-arm the grant timer WHILE the previous save's
    // own invincibility was still running, letting a second save land before the first one's protection even
    // ended.
    public float DeathPreventionTime => baseDeathPreventionTime * deathPreventionTimeMultiplier + deathPreventionInvincibilityDuration;
    // How long the post-save invincibility window lasts — same GrantInvincibility mechanism (and "longest
    // active window wins" stacking) as dash/DeathPreventionShieldDRAugment's own death save/Revive(), so this
    // stacks harmlessly with any of those rather than fighting over which invincibility "wins". Also the
    // unreducible flat floor DeathPreventionTime's own formula adds on top of the scaled portion — see that
    // property's own doc comment.
    public float deathPreventionInvincibilityDuration = 1f;
    // Tuning knob for ActivateDeathPreventions()'s own "already active, shrink the timer instead" rule — see
    // that method's own doc comment. Centralized here specifically so every augment that grants the
    // mechanic shares one tunable value instead of each carrying its own copy.
    public float deathPreventionRepeatReductionMultiplier = 0.75f;

    [Header("Energy")]
    public float baseMaxEnergy = 50f;
    // Fraction of MaxEnergy regenerated per second (2026-08-23 rework — was a flat units/sec value; a flat
    // rate made a higher MaxEnergy investment effectively SLOWER to refill, the opposite of the intent, so
    // this and bonusEnergyRegenFlat below are both now fractions of MaxEnergy instead — see EnergyRegen's
    // own doc comment for the full formula). 0.10f = 10% of MaxEnergy per second at base.
    public float baseEnergyRegen = 0.10f;

    [Header("Surge")]
    public float baseSurgeCost = 2f;
    public float surgeMoveSpeedMulti = 1f;
    public float surgeAttackSpeedMulti = 2f;

    [Header("Dash")]
    // [Header] attaches to the next member Unity's Inspector actually draws — placed directly above this
    // real serialized field (2026-08-19, moved above dashCharges/levelUpOptions) so it renders correctly.
    // Putting it above a computed property (as dashCharges/levelUpOptions below both are) gives Unity
    // nothing to hang the header on, since those have no SerializedProperty backing them at all — the
    // header attribute silently drops, and this field renders with no header above it, visually reading as
    // still part of whichever PREVIOUS header happened to be last (confirmed live: was rendering as if still
    // under "Surge").
    public float baseDashCooldown = 5f;
    // In State, not plain fields — mutated at runtime via ApplyProgressionBonuses, see strengthPerLevel's
    // own doc comment for why. Not Inspector-visible (computed properties, no SerializedProperty) — kept
    // here purely for source-level grouping alongside baseDashCooldown, not because the Inspector shows them.
    public int dashCharges => currentState.dashCharges != 0 ? currentState.dashCharges : 1;
    public int levelUpOptions => currentState.levelUpOptions != 0 ? currentState.levelUpOptions : 3;

    [Header("Luck")]
    public float baseLuck = 0f;
    // Worst/best-case roll multipliers for anything that wants a "luck-influenced random roll" (weapon
    // damage, future augments, etc.) — roll against these shared stats instead of hardcoding a variance
    // constant, so a single augment can move the floor or ceiling and affect every roller at once. Split
    // into two independent multipliers (not a single symmetric +-variance) since a floor that can be
    // pushed past 100% would allow zero/negative rolls; the ceiling has no such risk, so it isn't capped.
    public float baseLuckFloor = 0.5f;
    public float baseLuckCeiling = 1.5f;
    // The "floor" of a highroll — a roll landing in the top (1 - Highroll) fraction of its own window
    // snaps up to the max. Currently only ever notified via NotifyStrikeHighroll (see _onStrikeHighroll's
    // own doc comment) — every weapon/strike damage source calls it; heals never highroll at all (heals
    // stopped rolling luck variance entirely — see CharacterStatsPD.Heal's own doc comment — so there's no
    // roll for a heal to ever land in a highroll window). Same base+bonus+penalty convention as every other
    // stat here, so a future augment/passive can push it (e.g. toward 1.0 = never highrolls, or lower =
    // highrolls more often) without touching WeaponDamageMath itself.
    public float baseHighroll = 0.95f;
    // The FULL multiplier a roll gets when it highrolls and nothing else procs (2026-08-19 rework, buffed
    // 0.5f -> 3f: a highroll-only hit now does 300% damage, up from 150%) — no longer a "+X bonus added on
    // top of 1" the way the old value/name implied. Highroll and Crit now ADDITIVELY stack instead of
    // multiplying together (e.g. a highroll AND crit on the same hit sums to HighrollBonus + CritDamage =
    // 600% damage at base values, not (1+HighrollBonus)*CritDamage) — see
    // WeaponDamageMath.ApplyCritAndHighroll for the actual combination formula every damage-roll call site
    // now routes through instead of two separate `rolled *=` lines.
    public float baseHighrollBonus = 3f;

    [Header("Knockback")]
    public float baseKnockback = 5f;

    [Header("Misc")]
    public float baseOnHitPower = 1f;
    public float baseXPRadius = 5f;
    [SerializeField] private PlayerColorPalette _colorPalette;

    [Header("Audio")]
    [SerializeField] private AudioClip _damagedSfx;
    public AudioClip hitSfx;

    [Header("Visuals")]
    [SerializeField] private GameObject _invincibilityShell;
    // Dedicated to the model only — deliberately NOT routed through PlayerColorApplicator, which also
    // drives the minimap icon and the ground circle indicator, neither of which should fade with invisibility.
    [SerializeField] private Renderer _modelRenderer;
    [SerializeField, Range(0f, 1f)] private float invisibleModelAlpha = 0.3f;
    // Continuously spun in UpdateView() at the same rate weapons orbit (WeaponOrbitAngle) — purely cosmetic
    // flair, always on for every character, not gated to any ability. A burst/effect that also wants the
    // model tipped sideways (e.g. the burst leap's "somersault" look) calls SetModelSidewaysTipped rather
    // than writing this transform directly — with the continuous Y-axis spin already running, tipping the
    // whole thing 90 degrees on Z turns that spin into a forward-tumbling motion for free, and keeps this
    // the single owner of the transform instead of two systems fighting over it each frame.
    [SerializeField] private Transform _modelTransform;
    // In State, not a plain field — only ever read from UpdateView() (purely cosmetic model-tip rendering,
    // never gameplay math), so this was never a resimulation-safety risk on a machine already tracking this
    // identity. But PurrDiction's replication to OTHER clients piggybacks on the same State-snapshot system
    // rollback uses — a plain field's value never reaches a client that wasn't already simulating at the
    // exact tick it changed (a late joiner, a reconnect), so a non-owner catching up after the fact would
    // never see this player's model tip at all. Migrated for that reason, not rollback safety.
    private bool modelSidewaysTipped { get => currentState.modelSidewaysTipped; set => currentState.modelSidewaysTipped = value; }
    // Sign must always match WeaponBasePD.OrbitFlipTip/ModelTipRotation's own sign — see the latter's doc
    // comment for why this is negated rather than reversing weaponOrbitAngle's own rotation direction
    // (shared with ordinary idle spin, so flipping that would also reverse normal orbiting).
    private static readonly Quaternion ModelSidewaysTipRotation = Quaternion.Euler(0f, 0f, -90f);
    public void SetModelSidewaysTipped(bool value) => modelSidewaysTipped = value;

    // World-space yaw (degrees) this class's own model spin/tip is additionally rotated by — lets an effect
    // like the spin ability's leap make the body face a travel direction while it tumbles, instead of always
    // facing a fixed world +Z. 0 = no extra rotation. Weapons don't read this at all (never have) — this only
    // ever drives the player's own model tip/spin. In State — see modelSidewaysTipped's own doc comment for
    // why (same late-joiner visibility reasoning, not rollback safety).
    public float OrbitFacingYaw => currentState.orbitFacingYaw;
    public void SetOrbitFacingYaw(float yawDegrees) => currentState.orbitFacingYaw = yawDegrees;

    [Header("Revive")]
    public float reviveTime = 3f;

    // ── Augment/bonus modifiers ────────────────────────────────────────────

    // bonusMoveSpeedFlat/Percent/moveSpeedPenalty are the "Bonus" tier — class passives
    // (ClassPassiveData/ApplyClassPassives) and meta-progression are the only writers, so bonusMoveSpeedPercent
    // only ever scales bonusMoveSpeedFlat (see MoveSpeed below), never base directly. totalMoveSpeedPercent is
    // the NEW "Total" tier — the one in-run stat augments (MoveSpeedAugment and friends) grant, scaling the
    // whole (base + bonus) sum uniformly regardless of how big base itself is. See
    // project_bonus_total_stat_tiers memory for the full reasoning/formula.
    // ── All fields in this "Augment/bonus modifiers" section (down to damageReductionPenalty below) live in
    // State, not as plain fields — see State struct's own doc comment. This used to be a mix (only 10 fields
    // that a temporary ability buff happened to already touch were migrated), but that meant any field NOT
    // yet targeted by a temporary add/subtract pair looked safe while actually just being an unexercised
    // instance of the same bug, waiting for the next ability/augment that reuses its setter temporarily
    // (exactly how the original 10 were found — reusing a "permanent" setter for a temporary buff). Every
    // field here is publicly mutable via an Add*/Set*/Multiply* method that nothing stops a future caller
    // from using temporarily, so every one of them needs to be rollback-safe unconditionally, not just the
    // ones with a currently-known temporary use. Read access is unchanged (same names, now properties reading
    // currentState) so no external call site needed touching. 1f-neutral fields fall back to 1f on an exact
    // 0 read — currentState is a raw zero-filled default(State) for a brief window on a fresh spawn, BEFORE
    // Setup() applies GetInitialState()'s real defaults (struct field initializers don't run for default(T)
    // — only a real constructor call triggers them; WeaponManagerPD.Init(), called from
    // CharacterStatsPD.LateAwake() itself, is a confirmed reader inside that exact window, see
    // bonusSizePercent's own note for the concrete bug this caused). 0f-neutral fields need no such guard —
    // 0 already IS their correct neutral value, so the zero-filled window reads correctly by coincidence.
    public float bonusMoveSpeedFlat => currentState.bonusMoveSpeedFlat;
    public float bonusMoveSpeedPercent => currentState.bonusMoveSpeedPercent != 0f ? currentState.bonusMoveSpeedPercent : 1f;
    public float moveSpeedPenalty => currentState.moveSpeedPenalty != 0f ? currentState.moveSpeedPenalty : 1f;
    public float totalMoveSpeedPercent => currentState.totalMoveSpeedPercent != 0f ? currentState.totalMoveSpeedPercent : 1f;
    public float totalMoveSpeedPenalty => currentState.totalMoveSpeedPenalty != 0f ? currentState.totalMoveSpeedPenalty : 1f;

    // bonusStrengthPercent/bonusStrengthPenalty are Bonus tier (class passives only).
    // totalStrengthPercent/totalStrengthPenalty are the Total tier — what the weapon-count/multishot/track
    // "compensate with a damage bonus" augments (BladeweaviestBladeweaverAugment, MultiShotAugment,
    // OuterTrackAugment, SurgeWeaponCountAugment, WeaponCountAugment) grant, AND what the meta-progression
    // "Total Strength %" shop stat (StatType.TotalStrengthPercent) feeds via ApplyProgressionBonuses — it's
    // a flat multiplier meant to apply after those same weapon-count/Multishot penalties, not before.
    public float bonusStrengthFlat => currentState.bonusStrengthFlat;
    public float bonusStrengthPercent => currentState.bonusStrengthPercent != 0f ? currentState.bonusStrengthPercent : 1f;
    public float bonusStrengthPenalty => currentState.bonusStrengthPenalty != 0f ? currentState.bonusStrengthPenalty : 1f;
    public float totalStrengthPercent => currentState.totalStrengthPercent != 0f ? currentState.totalStrengthPercent : 1f;
    public float totalStrengthPenalty => currentState.totalStrengthPenalty != 0f ? currentState.totalStrengthPenalty : 1f;

    public float bonusWeaponRadiusFlat => currentState.bonusWeaponRadiusFlat;
    public float bonusWeaponRadiusPercent => currentState.bonusWeaponRadiusPercent != 0f ? currentState.bonusWeaponRadiusPercent : 1f;
    public float totalWeaponRadiusPercent => currentState.totalWeaponRadiusPercent != 0f ? currentState.totalWeaponRadiusPercent : 1f;
    public float totalWeaponRadiusPenalty => currentState.totalWeaponRadiusPenalty != 0f ? currentState.totalWeaponRadiusPenalty : 1f;

    // Despite the old name, this is a flat, in-units addition to baseAttackSpeed, not a genuine percent
    // multiplier — see AddAttackSpeedFlat's own comment. Renamed from attackSpeedPercent.
    public float attackSpeedFlat => currentState.attackSpeedFlat;
    public float attackSpeedTotalPercent => currentState.attackSpeedTotalPercent != 0f ? currentState.attackSpeedTotalPercent : 1f;
    public float attackSpeedPenalty => currentState.attackSpeedPenalty != 0f ? currentState.attackSpeedPenalty : 1f;

    public int bonusWeaponCountFlat => currentState.bonusWeaponCountFlat;
    public int weaponCountMultiplier => currentState.weaponCountMultiplier != 0 ? currentState.weaponCountMultiplier : 1;

    public float bonusWeaponRangeFlat => currentState.bonusWeaponRangeFlat;
    public float bonusWeaponRangePercent => currentState.bonusWeaponRangePercent != 0f ? currentState.bonusWeaponRangePercent : 1f;
    public float weaponRangePenalty => currentState.weaponRangePenalty != 0f ? currentState.weaponRangePenalty : 1f;
    public float totalWeaponRangePercent => currentState.totalWeaponRangePercent != 0f ? currentState.totalWeaponRangePercent : 1f;
    public float totalWeaponRangePenalty => currentState.totalWeaponRangePenalty != 0f ? currentState.totalWeaponRangePenalty : 1f;

    // Character-size augments (Titan, and any future ones) add their delta here rather than each
    // multiplying transform.localScale directly — additive, so multiple size augments stack (+25% +
    // +30% = +55%) instead of compounding multiplicatively (1.25 * 1.30 = 1.625x). Every weapon's own
    // root localScale reads this directly too (see WeaponManagerPD's own RegisterWeapon/LateUpdate), so
    // the weapon grows with the character without a separate per-augment multiply call. WeaponRadius
    // (orbit distance) deliberately does not — see its own comment for why.
    // In State, not a plain field — see State struct's own doc comment (KnightBigBuffAbilityPD's Size buff
    // needs this to roll back correctly). Falls back to 1f (neutral) on an exact 0 — see
    // totalMoveSpeedPercent's own doc comment for why (this specific field is the one that surfaced the
    // issue live: WeaponManagerPD.Init(), called from CharacterStatsPD.LateAwake() itself, read this before
    // GetInitialState() had applied, producing a 0-scale weapon root and NaN/Infinity downstream).
    public float bonusSizePercent => currentState.bonusSizePercent != 0f ? currentState.bonusSizePercent : 1f;

    // In State, not a plain field — see State struct's own doc comment (ExeBigAxeAbilityPD's crit buff
    // needs this to roll back correctly). No zero-guard needed here unlike the 1f-neutral fields (see
    // totalMoveSpeedPercent's own doc comment) — 0f already IS this field's correct neutral value, so the
    // raw zero-filled default(State) window before GetInitialState() applies reads correctly by coincidence.
    public float bonusCritChanceFlat => currentState.bonusCritChanceFlat;
    public float bonusCritChancePercent => currentState.bonusCritChancePercent != 0f ? currentState.bonusCritChancePercent : 1f;
    public float totalCritChancePercent => currentState.totalCritChancePercent != 0f ? currentState.totalCritChancePercent : 1f;
    public float totalCritChancePenalty => currentState.totalCritChancePenalty != 0f ? currentState.totalCritChancePenalty : 1f;

    public float bonusCritDamageFlat => currentState.bonusCritDamageFlat;
    public float bonusCritDamagePercent => currentState.bonusCritDamagePercent != 0f ? currentState.bonusCritDamagePercent : 1f;

    public float bonusRicochetFlat => currentState.bonusRicochetFlat;
    public float bonusRicochetPercent => currentState.bonusRicochetPercent != 0f ? currentState.bonusRicochetPercent : 1f;
    public float totalRicochetPercent => currentState.totalRicochetPercent != 0f ? currentState.totalRicochetPercent : 1f;
    public float totalRicochetPenalty => currentState.totalRicochetPenalty != 0f ? currentState.totalRicochetPenalty : 1f;

    public float bonusMultishotFlat => currentState.bonusMultishotFlat;
    public float bonusMultishotPercent => currentState.bonusMultishotPercent != 0f ? currentState.bonusMultishotPercent : 1f;
    public float bonusMultishotSpreadPercent => currentState.bonusMultishotSpreadPercent != 0f ? currentState.bonusMultishotSpreadPercent : 1f;
    public float totalMultishotPercent => currentState.totalMultishotPercent != 0f ? currentState.totalMultishotPercent : 1f;
    public float totalMultishotPenalty => currentState.totalMultishotPenalty != 0f ? currentState.totalMultishotPenalty : 1f;

    public float projectileDamageDropoffReduction => currentState.projectileDamageDropoffReduction != 0f ? currentState.projectileDamageDropoffReduction : 1f;
    public float projectileOnHitDropoffReduction => currentState.projectileOnHitDropoffReduction != 0f ? currentState.projectileOnHitDropoffReduction : 1f;

    // Same (base + flat) * totalPercent * totalPenalty shape as AttackSpeed — moved off WeaponRangedPD's own
    // local fields so any future ranged weapon can share the same class-passive-tunable stats.
    public float fireRateFlat => currentState.fireRateFlat;
    public float fireRateTotalPercent => currentState.fireRateTotalPercent != 0f ? currentState.fireRateTotalPercent : 1f;
    public float fireRateTotalPenalty => currentState.fireRateTotalPenalty != 0f ? currentState.fireRateTotalPenalty : 1f;

    public float projectileSpeedFlat => currentState.projectileSpeedFlat;
    public float projectileSpeedTotalPercent => currentState.projectileSpeedTotalPercent != 0f ? currentState.projectileSpeedTotalPercent : 1f;
    public float projectileSpeedTotalPenalty => currentState.projectileSpeedTotalPenalty != 0f ? currentState.projectileSpeedTotalPenalty : 1f;

    public float bonusMaxHealthFlat => currentState.bonusMaxHealthFlat;
    public float bonusMaxHealthPercent => currentState.bonusMaxHealthPercent != 0f ? currentState.bonusMaxHealthPercent : 1f;
    public float totalMaxHealthPercent => currentState.totalMaxHealthPercent != 0f ? currentState.totalMaxHealthPercent : 1f;
    public float totalMaxHealthPenalty => currentState.totalMaxHealthPenalty != 0f ? currentState.totalMaxHealthPenalty : 1f;
    // Same tier as bonusMaxHealthFlat (both feed MaxHealth's own bonusMaxHealthFlat*bonusMaxHealthPercent
    // term) — see ConvertedHealth's own doc comment for why this needs to sit at the exact same tier.
    public float convertedHealth => currentState.convertedHealth;

    public float bonusHealthRegenFlat => currentState.bonusHealthRegenFlat;
    public float bonusHealthRegenPercent => currentState.bonusHealthRegenPercent != 0f ? currentState.bonusHealthRegenPercent : 1f;
    public float totalHealthRegenPercent => currentState.totalHealthRegenPercent != 0f ? currentState.totalHealthRegenPercent : 1f;
    public float totalHealthRegenPenalty => currentState.totalHealthRegenPenalty != 0f ? currentState.totalHealthRegenPenalty : 1f;
    // Same tier as bonusHealthRegenFlat — see ConvertedHealthRegen's own doc comment.
    public float convertedHealthRegenFlat => currentState.convertedHealthRegenFlat;

    // Additive against baseMaxShieldPercent (both in the same "percent of Max Health" units, e.g. +0.05 =
    // +5% Max Health as shield) — no separate multiplier tier between base and bonus, unlike most other
    // stats' bonusFlat*bonusPercent shape (see MaxShield's own comment for why Shield's shape is simpler).
    // Deliberately NOT named bonusMaxShieldPercent — that name used to belong to the OLD multiplier tier
    // (default 1f) this replaced, and 0BaseCharacter.prefab still had a stale `bonusMaxShieldPercent: 1`
    // serialized under the old meaning; reusing the same name would have silently loaded that stale 1 into
    // this new additive-pool field instead of the intended 0 default (exactly the bug that shipped once,
    // inflating shield by a stray +100% of Max Health) — a fresh name sidesteps the collision entirely.
    public float bonusMaxShieldAdditive => currentState.bonusMaxShieldAdditive;
    public float totalMaxShieldPercent => currentState.totalMaxShieldPercent != 0f ? currentState.totalMaxShieldPercent : 1f;
    public float totalMaxShieldPenalty => currentState.totalMaxShieldPenalty != 0f ? currentState.totalMaxShieldPenalty : 1f;

    // Only ever one banked at a time (not a stacking count) — whether an augment has made this character
    // eligible to earn one at all, and whether one is currently banked and ready to spend. See Simulate()'s
    // own grant-timer comment for the full tick logic and TakeDamage()'s own comment for how a banked save
    // gets spent.
    public bool GainsDeathPreventions => currentState.gainsDeathPreventions;
    public bool HasDeathPrevention => currentState.hasDeathPrevention;
    // The ONLY entry point augments should use to grant this mechanic — never mutate gainsDeathPreventions
    // directly, so the "already active" stacking rule below stays centralized in one place instead of
    // needing to be duplicated in every augment that wants to grant it. First activator turns the mechanic
    // on; every subsequent one additionally compounds deathPreventionTimeMultiplier down by
    // deathPreventionRepeatReductionMultiplier (25% off by default) — a redundant "already have this" pick
    // tightens the grant cycle instead of doing nothing. EVERY call (first or repeat) instantly grants a
    // ready-to-use save — deliberate QoL: picking up a DP-granting source should feel like gaining
    // protection right now, not starting on cooldown. Also unconditionally resets the grant timer to a
    // fresh full cycle at whatever DeathPreventionTime now is (not just clamping it down) — otherwise the
    // NEXT recharge (once this instantly-granted save gets spent) could inherit whatever tiny amount was
    // left on the timer at the moment of this call, making it suspiciously fast instead of a full cycle.
    public void ActivateDeathPreventions()
    {
        if (!currentState.gainsDeathPreventions)
            currentState.gainsDeathPreventions = true;
        else
            currentState.deathPreventionTimeMultiplier = deathPreventionTimeMultiplier * deathPreventionRepeatReductionMultiplier;

        currentState.hasDeathPrevention = true;
        currentState.deathPreventionTimer = Mathf.Max(1, Mathf.RoundToInt(DeathPreventionTime / _currentDelta));
    }

    public float bonusMaxEnergyFlat => currentState.bonusMaxEnergyFlat;
    public float bonusMaxEnergyPercent => currentState.bonusMaxEnergyPercent != 0f ? currentState.bonusMaxEnergyPercent : 1f;

    public float bonusEnergyRegenFlat => currentState.bonusEnergyRegenFlat;
    public float bonusEnergyRegenPercent => currentState.bonusEnergyRegenPercent != 0f ? currentState.bonusEnergyRegenPercent : 1f;
    public float energyRegenPenalty => currentState.energyRegenPenalty != 0f ? currentState.energyRegenPenalty : 1f;
    public float totalEnergyRegenPercent => currentState.totalEnergyRegenPercent != 0f ? currentState.totalEnergyRegenPercent : 1f;
    public float totalEnergyRegenPenalty => currentState.totalEnergyRegenPenalty != 0f ? currentState.totalEnergyRegenPenalty : 1f;
    public float bonusSurgeCostFlat => currentState.bonusSurgeCostFlat;
    // Reduction-only tier (2026-08-19, renamed from bonusSurgeCostPercent) — multiplicative diminishing-
    // returns, never mathematically reaches 0 — see AddSurgeCostReductionPercent's own doc comment.
    public float surgeCostReductionPercent => currentState.surgeCostReductionPercent != 0f ? currentState.surgeCostReductionPercent : 1f;
    // Increase-only tier (2026-08-19, new; switched from multiplicative to additive 2026-08-21, kept additive
    // even after the SECOND caller that originally motivated the switch — an early cut of
    // SurgeInterruptAoEAugment — was reworked away from taxing Surge Cost at all, since it's the safer
    // default for whenever a future "increase Surge Cost" source shows up) — a separate linearly-summing
    // accumulator, structurally split from surgeCostReductionPercent above so an "increase Surge Cost"
    // source can't be cancelled back down through the same "never reach 0" floor a reduction source alone
    // already guarantees. Additive rather than multiplicative specifically so two "+100%" sources would land
    // at +200% (3x total) combined, not +300% (4x total) from multiplying two independent
    // x2 factors together — see AddSurgeCostIncreasePercent's own doc comment.
    public float surgeCostIncreasePercent => currentState.surgeCostIncreasePercent;
    public float surgeAttackSpeedPenalty => currentState.surgeAttackSpeedPenalty != 0f ? currentState.surgeAttackSpeedPenalty : 1f;

    public float dashHaste => currentState.dashHaste;
    // Multiplicative on top of dashHaste (see DashCooldown) — same "additive against a 1f baseline"
    // pattern as bonusWeaponRangePercent/bonusCritChancePercent, added alongside dashHaste's existing flat
    // grant so DashHaste-family augments can have the same flat+percent dual structure Range/CritChance do.
    // dashHaste has no separate serialized "base" field (it starts at 0 and is purely additive, unlike
    // every other stat here), so there's no distinct base/bonus split to make for this one — totalDashHastePercent
    // is simply appended as one more multiplier. Nothing but DashHasteAugment ever wrote bonusDashHastePercent
    // anyway (ClassPassiveData/progression only ever add to dashHaste itself), so redirecting it to Total is
    // a clean, zero-risk swap.
    public float bonusDashHastePercent => currentState.bonusDashHastePercent != 0f ? currentState.bonusDashHastePercent : 1f;
    public float totalDashHastePercent => currentState.totalDashHastePercent != 0f ? currentState.totalDashHastePercent : 1f;
    public float totalDashHastePenalty => currentState.totalDashHastePenalty != 0f ? currentState.totalDashHastePenalty : 1f;
    // See DashCooldown's own doc comment — a separate direct-percent tier layered on top of the Haste
    // curve, not part of it.
    public float dashCooldownReductionPercent => currentState.dashCooldownReductionPercent != 0f ? currentState.dashCooldownReductionPercent : 1f;
    public float dashCooldownIncreaseMultiplier => currentState.dashCooldownIncreaseMultiplier != 0f ? currentState.dashCooldownIncreaseMultiplier : 1f;

    // Same shape as dashHaste immediately above (see its own comment for the full reasoning) — for a
    // future ability-cooldown-haste formula, not yet consumed by any ability. No separate base field,
    // purely additive, bonusAbilityHastePercent/totalAbilityHastePercent both multiply on top the same way.
    public float abilityHaste => currentState.abilityHaste;
    public float bonusAbilityHastePercent => currentState.bonusAbilityHastePercent != 0f ? currentState.bonusAbilityHastePercent : 1f;
    public float totalAbilityHastePercent => currentState.totalAbilityHastePercent != 0f ? currentState.totalAbilityHastePercent : 1f;
    public float totalAbilityHastePenalty => currentState.totalAbilityHastePenalty != 0f ? currentState.totalAbilityHastePenalty : 1f;

    public float bonusLuckFlat => currentState.bonusLuckFlat;
    public float bonusLuckPercent => currentState.bonusLuckPercent != 0f ? currentState.bonusLuckPercent : 1f;
    public float luckPenalty => currentState.luckPenalty != 0f ? currentState.luckPenalty : 1f;
    public float totalLuckPercent => currentState.totalLuckPercent != 0f ? currentState.totalLuckPercent : 1f;
    public float totalLuckPenalty => currentState.totalLuckPenalty != 0f ? currentState.totalLuckPenalty : 1f;
    public float bonusLuckFloorPercent => currentState.bonusLuckFloorPercent != 0f ? currentState.bonusLuckFloorPercent : 1f;
    public float luckFloorPenalty => currentState.luckFloorPenalty != 0f ? currentState.luckFloorPenalty : 1f;
    public float bonusLuckCeilingFlat => currentState.bonusLuckCeilingFlat;
    public float bonusLuckCeilingPercent => currentState.bonusLuckCeilingPercent != 0f ? currentState.bonusLuckCeilingPercent : 1f;
    public float luckCeilingPenalty => currentState.luckCeilingPenalty != 0f ? currentState.luckCeilingPenalty : 1f;
    public float bonusHighrollPercent => currentState.bonusHighrollPercent != 0f ? currentState.bonusHighrollPercent : 1f;
    public float highrollPenalty => currentState.highrollPenalty != 0f ? currentState.highrollPenalty : 1f;
    public float bonusHighrollBonusPercent => currentState.bonusHighrollBonusPercent != 0f ? currentState.bonusHighrollBonusPercent : 1f;
    public float highrollBonusPenalty => currentState.highrollBonusPenalty != 0f ? currentState.highrollBonusPenalty : 1f;

    public float knockbackMultiplier => currentState.knockbackMultiplier != 0f ? currentState.knockbackMultiplier : 1f;
    public float knockbackPenalty => currentState.knockbackPenalty != 0f ? currentState.knockbackPenalty : 1f;

    public float bonusOnHitPowerMultiplier => currentState.bonusOnHitPowerMultiplier != 0f ? currentState.bonusOnHitPowerMultiplier : 1f;
    public float bonusOnHitPowerPenalty => currentState.bonusOnHitPowerPenalty != 0f ? currentState.bonusOnHitPowerPenalty : 1f;
    // Total tier — bonusOnHitPowerMultiplier is already a genuine multiplier (no base/flat addition to
    // split here), written only by ClassPassiveData/meta-progression now (Bonus tier, same as every other
    // stat's bonus term). No current caller of AddOnHitPowerTotalPercent (see its own doc comment) — kept
    // available for a future genuine "+X% OnHitPower" effect.
    public float totalOnHitPowerPercent => currentState.totalOnHitPowerPercent != 0f ? currentState.totalOnHitPowerPercent : 1f;
    public float totalOnHitPowerPenalty => currentState.totalOnHitPowerPenalty != 0f ? currentState.totalOnHitPowerPenalty : 1f;

    public float stackPowerPercent => currentState.stackPowerPercent != 0f ? currentState.stackPowerPercent : 1f;
    public float stackPowerPenalty => currentState.stackPowerPenalty != 0f ? currentState.stackPowerPenalty : 1f;

    // Same Percent*Penalty shape — HealPower scales every heal this character receives (see Heal()'s own
    // formula), regardless of source (regen, DashHealAugment, KnightAreaStunHealAbilityPD, etc. — anything
    // that funnels through the shared Heal() choke point gets this for free). CritHealPower ONLY applies on
    // top of it when a given heal happens to roll a "crit heal" (against CritChance, same roll every heal
    // always makes — see Heal()'s own comment for why no separate enable flag is needed): additively
    // combined as HealPower + CritHealPower - 1f, same "1 + sum of bonuses" stacking convention
    // AreaEffectPower already uses elsewhere (see AreaEffectCastHelper.Cast()'s own comment) —
    // at CritHealPower's neutral 1f baseline this reduces to plain HealPower, so an un-augmented character
    // sees zero effect from the always-on crit-heal roll until something actually grants bonus CritHealPower.
    // critHealPowerMultiplier is reversed by CriticalHealingAugment — another concrete example (alongside
    // totalOnHitPowerPercent above) of a field outside the original 10 that already had a temporary-use
    // pattern nobody had connected to the rollback-safety issue yet.
    public float healPowerMultiplier => currentState.healPowerMultiplier != 0f ? currentState.healPowerMultiplier : 1f;
    public float healPowerPenalty => currentState.healPowerPenalty != 0f ? currentState.healPowerPenalty : 1f;
    public float critHealPowerMultiplier => currentState.critHealPowerMultiplier != 0f ? currentState.critHealPowerMultiplier : 1f;
    public float critHealPowerPenalty => currentState.critHealPowerPenalty != 0f ? currentState.critHealPowerPenalty : 1f;

    // "AoE Effects" tag — modifiers for any augment tagged as an area effect (DashExplosionAugment,
    // DashHealAugment, etc.). Percent+Penalty pair for the two floats, same shape as StackPower
    // (AreaEffectSize/AreaEffectPower below = Percent * Penalty). Count mirrors WeaponCount's own int
    // Flat*Multiplier shape instead of a float Percent/Penalty pair, since it's a discrete repeat count, not
    // a continuous scale.
    public float areaEffectSizePercent => currentState.areaEffectSizePercent != 0f ? currentState.areaEffectSizePercent : 1f;
    public float areaEffectSizePenalty => currentState.areaEffectSizePenalty != 0f ? currentState.areaEffectSizePenalty : 1f;
    public float areaEffectPowerPercent => currentState.areaEffectPowerPercent != 0f ? currentState.areaEffectPowerPercent : 1f;
    public float areaEffectPowerPenalty => currentState.areaEffectPowerPenalty != 0f ? currentState.areaEffectPowerPenalty : 1f;
    public int   areaEffectCountFlat    => currentState.areaEffectCountFlat != 0 ? currentState.areaEffectCountFlat : 1;
    public int   areaEffectCountPenalty => currentState.areaEffectCountPenalty != 0 ? currentState.areaEffectCountPenalty : 1;

    public float AreaEffectSize  => areaEffectSizePercent * areaEffectSizePenalty;
    // Auto-divided by AreaEffectCount, same reasoning as Strength's auto-divide by WeaponCount — picking up
    // more casts per trigger shouldn't multiply total output for free. AreaEffectCountAugment compensates
    // with its own flat AreaEffectPower grant, same "extra copies, compensate with a straight bonus" pattern
    // WeaponCountAugment already uses for Strength.
    public float AreaEffectPower => areaEffectPowerPercent * areaEffectPowerPenalty / AreaEffectCount;
    public int   AreaEffectCount => areaEffectCountFlat * areaEffectCountPenalty;

    // Character-level multiplier on top of whatever a weapon's own outer-zone extent is authored at
    // (currently only WeaponAxePD.outerXZExtent — the outer/blade capsule's reach in the X/Z plane, i.e.
    // everything except the up/down axis; see its own ComputeZones/UpdateOuterBladeVisual for where this
    // actually gets read). Named XZ, not X — it scales the capsule's side-to-side spread AND its
    // forward/back+sideways radius together (a proportionally bigger axehead), deliberately excluding
    // height/thickness (Y). Single-tier, not a Percent+Penalty pair like AreaEffectPower — no known debuff
    // source for this exists yet, so no Penalty term to multiply against; add one later if something
    // concretely needs it. Generic here on CharacterStatsPD (not baked into WeaponAxePD itself) so any
    // future weapon type that grows its own "outer zone" concept can read the same stat for free, same
    // reasoning AreaEffectPower/AreaEffectSize already follow.
    public float outerXZMultiplierPercent => currentState.outerXZMultiplierPercent != 0f ? currentState.outerXZMultiplierPercent : 1f;
    public float OuterXZMultiplier => outerXZMultiplierPercent;

    public float xpMultiplier => currentState.xpMultiplier != 0f ? currentState.xpMultiplier : 1f;
    public float xpPenalty => currentState.xpPenalty != 0f ? currentState.xpPenalty : 1f;

    public float bonusXPRadiusFlat => currentState.bonusXPRadiusFlat;
    public float bonusXPRadiusPercent => currentState.bonusXPRadiusPercent != 0f ? currentState.bonusXPRadiusPercent : 1f;

    public float reviveTimePenalty => currentState.reviveTimePenalty != 0f ? currentState.reviveTimePenalty : 1f;
    public float reviveTimePenaltyReduction => currentState.reviveTimePenaltyReduction != 0f ? currentState.reviveTimePenaltyReduction : 1f;
    public float revivePower => currentState.revivePower != 0f ? currentState.revivePower : 1f;

    public float damageReductionPercent => currentState.damageReductionPercent != 0f ? currentState.damageReductionPercent : 1f;
    public float damageReductionPenalty => currentState.damageReductionPenalty != 0f ? currentState.damageReductionPenalty : 1f;

    // ── Computed properties ────────────────────────────────────────────────

    // Bonus/Total split: bonusMoveSpeedPercent only scales bonusMoveSpeedFlat (the "Bonus" sub-term, still
    // written only by ClassPassiveData/meta-progression), then totalMoveSpeedPercent scales the WHOLE
    // (base + bonus) sum — so an in-run "+X% total move speed" augment is worth the same relative bump
    // regardless of how big base/bonus already are, instead of being multiplied only against whatever tiny
    // bonus pool happens to exist. See project_bonus_total_stat_tiers memory for the full formula.
    // Slow (see Slow()'s own doc comment) is the one exception to the Bonus/Total shape — a straight
    // multiplier on top, same "just cuts the final result" role EnemyBasePD.EffectiveMoveSpeed's own
    // slowMultiplier plays, but move-speed-only here (AttackSpeed deliberately does NOT read this).
    public float MoveSpeed => (baseMoveSpeed + bonusMoveSpeedFlat * bonusMoveSpeedPercent * moveSpeedPenalty) * totalMoveSpeedPercent * totalMoveSpeedPenalty * (currentState.isSurging ? SurgeMoveSpeedMulti : 1f) * (currentState.slowTimer > 0 ? currentState.slowMultiplier : 1f);

    // Same Bonus/Total split as MoveSpeed — bonusStrengthPenalty is folded into the Bonus sub-term
    // (only ever written by ClassPassiveData/meta-progression, same as every other stat's bonus penalty),
    // totalStrengthPenalty is the Total-tier counterpart. No weapon-count/Multishot divide baked in here at
    // all (2026-08-19 rework, see WeaponStrength below) — this is the general-purpose Strength every non-
    // weapon-strike consumer (Phantom Blade, AoE Effects, on-hit/on-strike procs, most abilities that
    // "cosplay" a weapon strike like ExeAxeThrowProjectilePD) reads directly.
    public float Strength => (baseStrength + bonusStrengthFlat * bonusStrengthPercent * bonusStrengthPenalty) * totalStrengthPercent * totalStrengthPenalty;
    // Softened-divide (2026-08-19) — was a straight divide by total simultaneous damage instances, replaced
    // with a diminishing-returns curve so every weapon-count/Multishot/track augment gets a "net positive"
    // feel for free, with no per-augment compensating bonus needed at all (those all got removed the same
    // day, see this stat's own history). effectiveDivisor = 1 + (D - 1) * WeaponInstanceSoftenFactor, where
    // D = TotalWeaponCount * (1f + Multishot) is the RAW (unsoftened) instance count and
    // WeaponInstanceSoftenFactor (0.5) is how much of the "extra" divide beyond the first instance actually
    // applies — at D=2 (e.g. going from 1 to 2 simultaneous weapons), effectiveDivisor = 1.5, so two copies
    // net ~+33% combined instead of breaking exactly even. 0 would mean no divide at all; 1 would mean the
    // old exact straight divide. A single global constant can't reproduce every individual augment's own old
    // hand-tuned bonus (those ranged ~20%-60%) exactly — it trades that precision for one consistent curve
    // instead, by explicit design.
    private const float WeaponInstanceSoftenFactor = 0.5f;
    // (1f + Multishot) because Multishot itself is "bonus shots beyond the first" (base 0), matching the
    // same "+1" WeaponRangedPD.Fire() already applies when turning it into an actual shot count — dividing
    // by raw Multishot would divide by zero for anyone without it. Shared by WeaponStrength/WeaponOnHitPower
    // below, and by nothing else — this is the RAW (unsoftened) instance count, not what either of them
    // actually divides by.
    private float WeaponInstanceCount => TotalWeaponCount * (1f + Multishot);
    private float WeaponInstanceDivisor => 1f + (WeaponInstanceCount - 1f) * WeaponInstanceSoftenFactor;
    // ONLY WeaponMeleePD/WeaponRangedPD/WeaponRangedProjectilePD/WeaponAxePD — the four literal weapon
    // classes — should ever read this, with ONE deliberate exception: RogueSpinAbilityPD.BaseDamage(),
    // reviewed and re-approved to keep reading this (see its own doc comment) since that ability is meant
    // to feel like it's actually striking with weapons. (2026-08-19 rework — this used to be named plain
    // Strength and read far more broadly, which is exactly what let WeaponCountAugment/MultiShotAugment/
    // OuterTrackAugment's own compensating +Strength/+OnHitPower bonuses silently over-buff every non-weapon
    // consumer that was never being divided in the first place — those compensating grants are gone now
    // too, since the divide living ONLY here is itself the whole counterbalance.) Everything else —
    // including any other augment/ability that merely "cosplays" a weapon strike (ExeAxeThrowProjectilePD,
    // Phantom Blade, AoE Effects) — reads plain Strength above instead, undivided, by explicit design.
    public float WeaponStrength => Strength / WeaponInstanceDivisor;
    // Deliberately doesn't factor in bonusSizePercent, unlike a weapon's own hitbox/visual scale —
    // Bladeweaver is the only class that uses this for actual orbit distance (hovering blades), and a size
    // augment pushing that distance out further makes it harder for them to hit anything nearby, which
    // isn't the intent of a size augment. The weapon's own root scale still scales with size; this
    // doesn't need to.
    // totalWeaponRadiusPercent: no in-run augment grants this yet (only ClassPassiveData touches
    // bonusWeaponRadiusPercent) — added for future use per the same Bonus/Total split as every other stat.
    public float WeaponRadius => (baseWeaponRadius + bonusWeaponRadiusFlat * bonusWeaponRadiusPercent) * totalWeaponRadiusPercent * totalWeaponRadiusPenalty;

    // Already implements the target Bonus/Total shape and needs no change: attackSpeedFlat IS this stat's
    // "bonus" term already — added directly to base in absolute units, same role bonusMoveSpeedFlat plays
    // elsewhere — and attackSpeedTotalPercent already is the genuine Total-tier multiplier AttackSpeedAugment
    // grants. attackSpeedPenalty deliberately keeps its own "multiplies everything, outside both tiers"
    // position — unlike Strength's bonusStrengthPenalty, no in-run system currently needs this reconsidered.
    public float AttackSpeed => (baseAttackSpeed + attackSpeedFlat) * attackSpeedTotalPercent * attackSpeedPenalty * (currentState.isSurging ? SurgeAttackSpeedMultiplier : 1f);
    public float WeaponSpeed => AttackSpeed * 360f;
    // Overrides WeaponSpeed for the shared weaponOrbitAngle tick below when set — lets a special action
    // (e.g. the burst) spin weapons at a fixed, snapshotted rate instead of the live AttackSpeed-driven one,
    // without touching AttackSpeed itself (which would also affect FireRate and anything else derived from
    // it). Same degrees/sec units as WeaponSpeed. In State, not plain fields — set/cleared from whichever
    // effect's own Simulate() is driving them; PREVIOUSLY reasoned as safe as plain fields ("fully re-derived
    // every resimulation pass from whatever set it") since the driving effect re-asserts the value every
    // tick it's active — but on reflection that assumption is exactly the kind of thing that already broke
    // once for the original 10 fields, and every one of these is still mutable from Simulate()-reachable
    // code with no compiler-enforced guarantee it'll always be reasserted every tick by every future caller.
    // Migrated for full consistency with the rest of this class rather than carrying a second exception
    // category.
    public float specialSpinRate  { get => currentState.specialSpinRate;  private set => currentState.specialSpinRate = value; }
    public bool  useSpecialSpinRate { get => currentState.useSpecialSpinRate; private set => currentState.useSpecialSpinRate = value; }
    public void SetSpecialSpinRate(float degreesPerSecond) => specialSpinRate = degreesPerSecond;
    public void SetUseSpecialSpinRate(bool value) => useSpecialSpinRate = value;
    public float EffectiveWeaponSpeed => useSpecialSpinRate ? specialSpinRate : WeaponSpeed;
    // Whether WeaponBasePD.OrbitalPosition should sweep a vertical (tipped) plane instead of the normal
    // horizontal one — purely a VISUAL orbit-shape switch, deliberately separate from IsWeaponsDisabled
    // (which only ever gates damage application, nothing else — see its own doc comment). Kept as its own
    // flag rather than folded into IsWeaponsDisabled specifically so a future effect can disable weapon
    // damage without also flipping the orbit, or vice versa. In State — see specialSpinRate's own doc
    // comment for why.
    public bool weaponOrbitFlipped { get => currentState.weaponOrbitFlipped; private set => currentState.weaponOrbitFlipped = value; }
    public bool IsOrbitFlipped => weaponOrbitFlipped;
    public void SetOrbitFlipped(bool value) => weaponOrbitFlipped = value;
    // Set by ArcherFocusFireAbilityPD for its own active window — read by WeaponRangedPD.LateSimulate() to
    // switch its firing behavior: normally a shot requires FindTarget() to first confirm something's
    // actually in the raycast's path before firing; while true, it skips that requirement entirely and just
    // fires straight forward (wherever the weapon is currently aimed) every time its normal fire-rate timer
    // allows — gated by ContinuousFireHasTarget below, not truly "target or not" anymore. In State — see
    // specialSpinRate's own doc comment for why.
    public bool continuousFire { get => currentState.continuousFire; private set => currentState.continuousFire = value; }
    public bool ContinuousFire => continuousFire;
    public void SetContinuousFire(bool value) => continuousFire = value;
    // The shared OrbitalPosition baseAngle every weapon on this character should use INSTEAD of the normal,
    // ever-incrementing WeaponOrbitAngle while ContinuousFire is active — same Atan2-based convention
    // OrbitalPosition's own flatOffset math uses (X = cos(rad), Z = sin(rad)), so setting this to
    // Atan2(aimDir.z, aimDir.x) * Rad2Deg makes the SlotIndex-0 weapon point exactly at the aim direction
    // (the closest enemy — see ArcherFocusFireAbilityPD.UpdateAutoAimAngle's own doc comment, this used to
    // be cursor-derived before the auto-aim rework). Every other weapon still applies its own normal
    // 360/total*SlotIndex offset on top of this shared base exactly as it always does during ordinary
    // orbiting — only the shared base itself changes source, so the whole ring freezes and re-aims as one
    // rigid formation instead of each weapon trying to aim independently. In State — see specialSpinRate's
    // own doc comment for why.
    public float focusFireAimAngle { get => currentState.focusFireAimAngle; private set => currentState.focusFireAimAngle = value; }
    public float FocusFireAimAngle => focusFireAimAngle;
    public void SetFocusFireAimAngle(float degrees) => focusFireAimAngle = degrees;
    // Set alongside focusFireAimAngle by ArcherFocusFireAbilityPD.UpdateAutoAimAngle — whether that same
    // auto-aim search actually found a living enemy within range this tick. WeaponRangedPD.LateSimulate()
    // checks this before actually firing during ContinuousFire, so the weapon no longer shoots blindly into
    // empty space just because the fire-rate timer allows it — it still skips FindTarget()/line-of-sight
    // entirely (fires straight at the aimed direction, not along a raycast), it just also requires SOMETHING
    // to be in range at all. In State — see specialSpinRate's own doc comment for why.
    public bool continuousFireHasTarget { get => currentState.continuousFireHasTarget; private set => currentState.continuousFireHasTarget = value; }
    public bool ContinuousFireHasTarget => continuousFireHasTarget;
    public void SetContinuousFireHasTarget(bool value) => continuousFireHasTarget = value;
    // What fraction of normal fire rate remains while ContinuousFire is active (e.g. 0.2 for an 80%
    // reduction) — 2026-08-21 fix, see FireRate's own read site. Deliberately a STANDALONE multiplicative
    // term, not folded into fireRateTotalPercent/fireRateTotalPenalty (both additive-around-1 accumulators
    // that stack with every OTHER fire-rate bonus/penalty a player has — a flat subtraction into either one
    // gets proportionally swallowed the higher a player's other fire-rate investment already is, so an
    // intended "80% slower" read as barely noticeable once other bonuses pushed the baseline well above 1).
    // Same "separate, always-applies-in-full multiplicative term at the very end of the formula" shape
    // MoveSpeed's own slowMultiplier/slowTimer already establishes for Slow(). Gated on continuousFire
    // itself (already State-backed, already correctly toggled by ArcherFocusFireAbilityPD's own Activate()/
    // EndFocusFire()) rather than a separate timer — no need to also "revert" this value when the window
    // ends, since FireRate simply stops reading it the instant continuousFire goes false.
    public float focusFireRateMultiplier { get => currentState.focusFireRateMultiplier; private set => currentState.focusFireRateMultiplier = value; }
    public void SetFocusFireRateMultiplier(float multiplier) => focusFireRateMultiplier = multiplier;
    // Overrides the point every weapon orbits (WeaponMeleePD/WeaponAxePD/WeaponRangedPD each read this
    // instead of transform.position directly for their own LateSimulate origin) — lets a special action
    // relocate the whole orbit somewhere other than the player (e.g. BWBladeDanceAbilityPD sending its
    // blades out to a target point, still orbiting normally, just around that point instead of the player)
    // without touching WeaponOrbitAngle/EffectiveWeaponSpeed at all — weapons keep spinning at their normal
    // rate, just around a different center. In State — see specialSpinRate's own doc comment for why.
    public bool    weaponOrbitOriginOverridden { get => currentState.weaponOrbitOriginOverridden; private set => currentState.weaponOrbitOriginOverridden = value; }
    public Vector3 weaponOrbitOriginOverride   { get => currentState.weaponOrbitOriginOverride;   private set => currentState.weaponOrbitOriginOverride = value; }
    public Vector3 EffectiveOrbitOrigin => weaponOrbitOriginOverridden ? weaponOrbitOriginOverride : transform.position;
    // null clears the override (weapons revert to orbiting transform.position directly next tick).
    public void SetOrbitOriginOverride(Vector3? origin)
    {
        weaponOrbitOriginOverridden = origin.HasValue;
        if (origin.HasValue) weaponOrbitOriginOverride = origin.Value;
    }
    // Extra world Y added to WeaponBasePD.OrbitalPosition's result — lets a special action's own vertical
    // arc (e.g. the spin ability's leap height) carry weapons along with it, since the player's REAL
    // transform never actually moves in Y for that leap (only a cosmetic Visuals-child offset does — see
    // RogueSpinAbilityPD.UpdateView's own arcY). In State — see specialSpinRate's own doc comment for why.
    public float specialHeightOffset { get => currentState.specialHeightOffset; private set => currentState.specialHeightOffset = value; }
    public float SpecialHeightOffset => specialHeightOffset;
    public void SetSpecialHeightOffset(float value) => specialHeightOffset = value;
    // Public read access to the shared orbit angle (see State.weaponOrbitAngle's own doc comment) — every
    // weapon (WeaponMeleePD/WeaponAxePD/WeaponRangedPD) reads this instead of ticking its own copy.
    public float WeaponOrbitAngle => currentState.weaponOrbitAngle;
    public float AttackSpeedPercent => attackSpeedTotalPercent * attackSpeedPenalty;

    public int WeaponCount => (baseWeaponCount + bonusWeaponCountFlat) * weaponCountMultiplier;
    // Total simultaneous weapon-equivalent count across every track — weaponTracks >= 2 (OuterTrackAugment)
    // mirrors WeaponCount onto a second ring (see WeaponManagerPD.weaponTracks' own doc comment), so the
    // real total a per-weapon effect should scale by is this product, not WeaponCount alone. Extracted here
    // since Strength/OnHitPower's own auto-divide below already needed this exact product — any future
    // per-weapon-count scaling (e.g. RogueSpinAbilityPD's hit-count-per-weapon) should read this instead of
    // reimplementing the same multiply.
    public int TotalWeaponCount => weaponTracks * WeaponCount;
    // Same Bonus/Total split as MoveSpeed — totalWeaponRangePercent is the new Total tier RangeAugment (and
    // its outlier siblings OuterTrackAugment/TitanAugment) grant; weaponRangePenalty is folded into the
    // Bonus sub-term (ClassPassiveData is its only writer, same as moveSpeedPenalty/energyRegenPenalty).
    public float WeaponRange => (baseWeaponRange + bonusWeaponRangeFlat * bonusWeaponRangePercent * weaponRangePenalty) * totalWeaponRangePercent * totalWeaponRangePenalty * 0.2f;

    // Shared base radius for the "AoE Effects" family (KnightAreaStunHealAbilityPD, DashExplosionAugment,
    // DashHealAugment, ShieldBreakBurstAugment, SurgeInterruptAoEAugment — see project_area_effect_system) —
    // single source of truth for the part of the radius formula that's supposed to be identical across every
    // family member. Each caller still multiplies this by its own per-augment rangeMultiplier/radiusMultiplier
    // on top (that scalar is caller-specific tuning, not part of the shared formula, so it stays local to
    // each site). Coefficient retuned 0.5 -> 1 on 2026-08-12; extracted here the same day so a future retune
    // only ever touches one place instead of every family member + their doc comments.
    //
    // The flat 0.5 offset is meant to represent the same "near-body reach" a weapon's own hitbox has even at
    // zero WeaponRange — 2026-08-23 fix: now scaled by bonusSizePercent (the player's actual physical Size
    // stat, e.g. from KnightBigBuffAbilityPD's own buff), same as a weapon's own root transform already
    // scales with Size (see bonusSizePercent's own doc comment: "Every weapon's own root localScale reads
    // this directly too"). Before this fix the offset only scaled with AreaEffectSize, so a caster who got
    // physically bigger/smaller (Size, not AreaEffectSize) saw their weapon hitboxes resize but not this
    // shared near-body AoE offset — inconsistent with what a real weapon does. Scales symmetrically both
    // directions (shrinks for Size < 1, grows for Size > 1) since bonusSizePercent is a plain multiplier.
    // WeaponRange itself is untouched by this — only the flat offset term gets the extra Size factor.
    public float RangeScalingAoEBase => (0.5f * bonusSizePercent + WeaponRange) * AreaEffectSize;

    // Same Bonus/Total split — totalCritChancePercent is the new Total tier CritChanceAugment grants.
    public float CritChance => (baseCritChance + bonusCritChanceFlat * bonusCritChancePercent) * totalCritChancePercent * totalCritChancePenalty;
    public float CritDamage => ((baseCritDamage + bonusCritDamageFlat) * bonusCritDamagePercent) + Mathf.Max(0f, CritChance - 1f) * 0.5f;

    // totalRicochetPercent/totalMultishotPercent: no in-run augment grants these yet (RicochetAugment/
    // MultiShotAugment only ever touch the Flat side) — added for future use per the same Bonus/Total split.
    public float Ricochet => (baseRicochet + bonusRicochetFlat * bonusRicochetPercent) * totalRicochetPercent * totalRicochetPenalty;
    public float Multishot => (baseMultishot + bonusMultishotFlat * bonusMultishotPercent) * totalMultishotPercent * totalMultishotPenalty;
    public float MultishotSpreadAngle => baseMultishotSpreadAngle * bonusMultishotSpreadPercent;
    public float ProjectileDamageDropoff => baseProjectileDamageDropoff * projectileDamageDropoffReduction;
    public float ProjectileOnHitDropoff => baseProjectileOnHitDropoff * projectileOnHitDropoffReduction;
    public float FireRate => (baseFireRate + fireRateFlat) * fireRateTotalPercent * fireRateTotalPenalty * (continuousFire ? focusFireRateMultiplier : 1f);
    public float ProjectileSpeed => (baseProjectileSpeed + projectileSpeedFlat) * projectileSpeedTotalPercent * projectileSpeedTotalPenalty;

    // Same Bonus/Total split — totalMaxHealthPercent is the new Total tier HealthAugment (and outlier
    // TitanAugment) grant.
    public float MaxHealth => (baseMaxHealth + bonusMaxHealthFlat * bonusMaxHealthPercent) * totalMaxHealthPercent * totalMaxHealthPenalty;
    // How much of MaxHealth currently comes from bonuses (augments, meta-progression, class passives, etc.)
    // rather than the character's raw baseMaxHealth — e.g. OnHitDamageHealthAugment scales its own damage
    // off this instead of the full MaxHealth, so a character who hasn't picked up any health-boosting
    // bonuses doesn't deal damage off their unmodified starting health. Floored at 0 in case a future
    // penalty tier ever pushes MaxHealth below baseMaxHealth.
    public float BonusHealth => Mathf.Max(0f, MaxHealth - baseMaxHealth);
    // How much of BonusHealth currently came from a stat-conversion augment (e.g. a hypothetical
    // HealthRegenToHealthAugment), rather than gear/other augments/class passives — a conversion augment
    // reading Health as ITS OWN source (e.g. HealthToHealthRegenAugment) subtracts this from BonusHealth
    // before converting, so it never "sees" — and therefore can never loop back through — another
    // augment's own converted output. Mirrors BonusHealth's exact derivation (convertedHealth sits at the
    // same tier as bonusMaxHealthFlat, run through the identical bonusMaxHealthPercent/totalMaxHealthPercent/
    // totalMaxHealthPenalty chain) so the subtraction cancels cleanly instead of comparing apples to
    // oranges. A conversion augment that GRANTS Health should add its own delta here (AddConvertedHealth)
    // in addition to the real grant (AddMaxHealthFlat), using the identical delta both times — this stays
    // at 0 (a complete no-op) until some augment actually does that; nothing does yet.
    public float ConvertedHealth => convertedHealth * bonusMaxHealthPercent * totalMaxHealthPercent * totalMaxHealthPenalty;
    // Same split — totalHealthRegenPercent is the new Total tier HealthRegenAugment grants.
    public float HealthRegen => (baseHealthRegen + bonusHealthRegenFlat * bonusHealthRegenPercent) * totalHealthRegenPercent * totalHealthRegenPenalty;
    // HealthRegen's own equivalent of ConvertedHealth above — see its doc comment for the full reasoning.
    // No base term (base is never conversion-derived), otherwise the exact same
    // bonusHealthRegenPercent/totalHealthRegenPercent/totalHealthRegenPenalty chain as HealthRegen itself,
    // so HealthRegen - ConvertedHealthRegen exactly isolates the non-conversion-derived portion.
    public float ConvertedHealthRegen => convertedHealthRegenFlat * bonusHealthRegenPercent * totalHealthRegenPercent * totalHealthRegenPenalty;
    // Same "current minus base" shape as BonusHealth — see its own doc comment. A conversion augment sourcing
    // off HealthRegen should read this (minus ConvertedHealthRegen), not raw HealthRegen, so someone with
    // zero HealthRegen investment beyond base doesn't get a "free" conversion off their own baseline.
    public float BonusHealthRegen => Mathf.Max(0f, HealthRegen - baseHealthRegen);

    // Simpler shape than most other stats — just base+bonus (both already "percent of Max Health" units)
    // then the usual Total tier, no separate bonusFlat*bonusPercent multiplier step in between. ShieldAugment
    // is the first in-run augment to grant bonusMaxShieldAdditive; totalMaxShieldPercent/totalMaxShieldPenalty
    // still have no augment-facing caller (AddMaxShieldTotalPercent exists for a future outlier effect).
    public float MaxShield => (baseMaxShieldPercent + bonusMaxShieldAdditive) * totalMaxShieldPercent * totalMaxShieldPenalty * MaxHealth;

    public float MaxEnergy => (baseMaxEnergy + bonusMaxEnergyFlat) * bonusMaxEnergyPercent;
    // Returns an actual per-second AMOUNT (same as before this rework — Simulate()'s own regen tick still
    // just does energy += EnergyRegen * delta, no change needed there), but baseEnergyRegen/bonusEnergyRegenFlat
    // are now both FRACTIONS of MaxEnergy rather than flat units (2026-08-23 rework — see baseEnergyRegen's
    // own doc comment for why), so the whole bracketed term is multiplied by MaxEnergy once at the end,
    // same "multiply the percent-tier result by the stat it scales" shape MaxShield already uses for
    // MaxHealth just above. Same Bonus/Total split as MoveSpeed/WeaponRange otherwise — energyRegenPenalty
    // folded into the Bonus sub-term (ClassPassiveData-only writer), totalEnergyRegenPercent is the Total
    // tier EnergyRegenAugment grants (now a straight doubling, +100% — see that augment's own doc comment).
    public float EnergyRegen => (baseEnergyRegen + bonusEnergyRegenFlat * bonusEnergyRegenPercent * energyRegenPenalty) * totalEnergyRegenPercent * totalEnergyRegenPenalty * MaxEnergy;

    // surgeCostReductionPercent (never mathematically reaches 0, multiplicative diminishing returns) *
    // (1 + surgeCostIncreasePercent) (linearly-summing additive bonus) — see each field's own doc comment
    // for why they're structurally separate tiers, not one shared accumulator. Defensive floor clamp
    // (2026-08-19, matches DamageReduction's own identical reasoning) — SurgeCost reaching 0 or negative
    // would mean free or literally energy-GAINING Surging, a severe exploit, so this stays strictly positive
    // regardless of any edge case in either tier above.
    public float SurgeCost => Mathf.Max(0.01f, (baseSurgeCost + bonusSurgeCostFlat) * surgeCostReductionPercent * (1f + surgeCostIncreasePercent));
    public float SurgeActivationThreshhold => SurgeCost * 5f;
    // In State, not a plain field — the only one of the three Surge-header fields with an actual runtime
    // setter (SetSurgeMoveSpeedPenalty, called by SurgeNoMoveSpeedPenaltyAugment/
    // SurgeHalfMoveSpeedPenaltyAugment); surgeMoveSpeedMulti/surgeAttackSpeedMulti have no mutator anywhere
    // and stay plain Inspector-authored constants, same category as baseMoveSpeed/baseStrength.
    public float surgeMoveSpeedPenalty => currentState.surgeMoveSpeedPenalty != 0f ? currentState.surgeMoveSpeedPenalty : 0.5f;
    public float SurgeMoveSpeedMulti => surgeMoveSpeedMulti * surgeMoveSpeedPenalty;
    public float SurgeAttackSpeedMultiplier => surgeAttackSpeedMulti * surgeAttackSpeedPenalty;

    // The actual effective dash haste value DashCooldown's formula uses — exposed separately so UI (or
    // anything else) can display the real number instead of the pre-multiplier raw dashHaste field.
    // dashHaste has no separate base field (see its own declaration comment) — totalDashHastePercent (the
    // new Total tier DashHasteAugment grants) is simply appended as one more multiplier alongside the
    // existing bonusDashHastePercent, rather than restructured into a base/bonus split.
    public float EffectiveDashHaste => dashHaste * bonusDashHastePercent * totalDashHastePercent * totalDashHastePenalty;
    // dashCooldownReductionPercent/dashCooldownIncreaseMultiplier (2026-08-19) are a SEPARATE direct-percent
    // tier layered on top of the Haste-derived cooldown above, not folded into dashHaste itself — a
    // "capstone"-style augment (DashCooldownReductionAugment) that wants to synergize WITH Dash Haste
    // (multiply on top of whatever the haste curve already produced) rather than compete with it inside the
    // same diminishing-returns curve. Same split-tier shape as CharacterStatsPD.SurgeCost (see its own doc
    // comment for the full "why structurally separate" reasoning) — dashCooldownReductionPercent uses
    // multiplicative diminishing-returns (never mathematically reaches 0, see
    // AddDashCooldownReductionPercent), dashCooldownIncreaseMultiplier freely multiplies up. Defensive floor
    // clamp (matches DamageReduction/SurgeCost's own identical reasoning) — a 0-or-negative cooldown would
    // mean free-spam or instantly-refilling dash charges, a severe exploit.
    public float DashCooldown => Mathf.Max(0.01f, (baseDashCooldown / (1f + EffectiveDashHaste / 100f)) * dashCooldownReductionPercent * dashCooldownIncreaseMultiplier);

    // Same shape as EffectiveDashHaste — whatever future Bursts/Abilities cooldown formula gets built will
    // divide its own base cooldown by (1f + EffectiveAbilityHaste / 100f), same as DashCooldown does.
    public float EffectiveAbilityHaste => abilityHaste * bonusAbilityHastePercent * totalAbilityHastePercent * totalAbilityHastePenalty;

    // Same Bonus/Total split as MoveSpeed/WeaponRange — luckPenalty folded into the Bonus sub-term
    // (ClassPassiveData-only writer), totalLuckPercent is the new Total tier LuckAugment grants.
    public float Luck => (baseLuck + bonusLuckFlat * bonusLuckPercent * luckPenalty) * totalLuckPercent * totalLuckPenalty;
    public float LuckChance => Luck / (Luck + 100f);
    // Both clamped to a minimum of 0 here — the one source every consumer reads from — so no
    // debuff/penalty stacking can ever push either one negative and cause a roll below zero. No upper
    // clamp on the ceiling; an arbitrarily high one is just a bigger reward, not a bug.
    public float LuckFloor   => Mathf.Max(0f, baseLuckFloor * bonusLuckFloorPercent * luckFloorPenalty);
    public float LuckCeiling => Mathf.Max(0f, (baseLuckCeiling + bonusLuckCeilingFlat) * bonusLuckCeilingPercent * luckCeilingPenalty);
    // The floor a roll is actually bounded by once LuckChance has raised it off the raw LuckFloor — same
    // Lerp(rawMin, max, LuckChance) WeaponDamageMath.RollDamage itself uses, just expressed in the
    // [LuckFloor, LuckCeiling] multiplier space instead of actual damage numbers so UI can display it
    // without a damage value on hand. LuckCeiling is untouched here — LuckChance only raises the floor
    // toward the ceiling, it never moves the ceiling itself.
    public float EffectiveLuckFloor => Mathf.Lerp(LuckFloor, LuckCeiling, LuckChance);
    // Clamped to [0,1] here — it's a Mathf.Lerp t-value downstream (WeaponDamageMath.RollDamage), which
    // would clamp it internally anyway, but keeping the source itself sane means anything else that reads
    // Highroll directly later doesn't have to know that.
    public float Highroll => Mathf.Clamp01(baseHighroll * bonusHighrollPercent * highrollPenalty);
    // Derived from WeaponDamageMath.RollDamage's own uniform-roll math: a roll is uniform across
    // [min, max] where min = Lerp(rawMin, max, LuckChance) and the highroll threshold =
    // Lerp(rawMin, max, Highroll) — both lerps share the same [rawMin, max] window, so the "chance the
    // roll lands at/above the threshold" reduces to (1 - Highroll) / (1 - LuckChance), independent of the
    // actual damage/rawMin/max values. Once LuckChance has caught up to (or passed) Highroll, min is
    // already at or above the threshold, so every roll highrolls (100%) — matches RollDamage's own
    // "as luckChance climbs... every roll highrolls" comment. Clamped defensively in case LuckChance ever
    // gets close enough to 1 to make the division blow up past 100%.
    public float HighrollChance => LuckChance >= Highroll ? 1f : Mathf.Clamp01((1f - Highroll) / (1f - LuckChance));
    // Floored at 1f, NOT 0f (2026-08-19 rework) — this is now applied as a DIRECT replacement multiplier
    // when a highroll is the only thing that procs (see WeaponDamageMath.ApplyCritAndHighroll), not "+X on
    // top of a 1 baseline" the way the old "+bonus" semantics worked. A 0f floor would let a heavy enough
    // highrollBonusPenalty make a highroll multiply damage by ZERO — actively worse than not highrolling at
    // all, which defeats the entire "a highroll should never actively reduce damage below its already-rolled
    // value" invariant this clamp exists for. 1f preserves that invariant instead: worst case, a highroll is
    // exactly as good as a normal roll, never worse.
    public float HighrollBonus => Mathf.Max(1f, baseHighrollBonus * bonusHighrollBonusPercent * highrollBonusPenalty);

    public float Knockback => baseKnockback * knockbackMultiplier * knockbackPenalty;

    // bonusOnHitPowerMultiplier is already a genuine multiplier (no base/flat addition to split, unlike
    // most other stats here) — totalOnHitPowerPercent is simply appended as one more independent multiplier
    // per the same Bonus/Total convention. No weapon-count/Multishot divide here — see WeaponOnHitPower
    // below and Strength/WeaponStrength's own doc comments for the full 2026-08-19 split reasoning. onHitPower
    // feeds every onhit-triggered effect (OnHitDamageAugment, PhantomBladeOnHitAugment,
    // OnStrikeHealAllyAugment, etc.) — none of those are literal weapon strikes, so they read this
    // undivided value directly now, same as Strength.
    public float OnHitPower => baseOnHitPower * bonusOnHitPowerMultiplier * bonusOnHitPowerPenalty * totalOnHitPowerPercent * totalOnHitPowerPenalty;
    // Same auto-divide as WeaponStrength, same restriction (including the same ONE deliberate
    // RogueSpinAbilityPD.OnHitPowerForEffects() exception — see WeaponStrength's own doc comment) — ONLY
    // WeaponMeleePD/WeaponRangedPD/WeaponRangedProjectilePD/WeaponAxePD should otherwise ever read this. A
    // weapon's own hit reports THIS (the per-instance divided value) into WeaponHitData.onHitPower, so any
    // reactive on-hit effect scaling off "how powerful was this particular hit" (data.onHitPower) still
    // correctly reflects the actual divided
    // hit that landed — that's a different, still-correct use from a non-weapon effect reading OnHitPower
    // directly for its own scaling, which should stay undivided.
    public float WeaponOnHitPower => (baseOnHitPower * bonusOnHitPowerMultiplier * bonusOnHitPowerPenalty * totalOnHitPowerPercent * totalOnHitPowerPenalty)
        / WeaponInstanceDivisor;
    public float StackPower => stackPowerPercent * stackPowerPenalty;
    public float HealPower => healPowerMultiplier * healPowerPenalty;
    public float CritHealPower => critHealPowerMultiplier * critHealPowerPenalty;
    public float XPModifier => xpMultiplier * xpPenalty;
    public float XPRadius => (baseXPRadius + bonusXPRadiusFlat) * bonusXPRadiusPercent;

    public float ReviveTimePenalty => reviveTimePenalty * reviveTimePenaltyReduction;
    // Floor-clamped to 0f — damageReductionPercent itself should never mathematically reach 0 or go negative
    // now that every contributor stacks multiplicatively (see AddDamageReductionPercent's own doc comment),
    // but this is the one place actually gating TakeDamage's `damage *= DamageReduction`, so it stays as a
    // defensive final clamp regardless: negative here would mean MORE than 100% reduction, i.e. healing on
    // a hit that should merely be mitigated.
    public float DamageReduction => Mathf.Max(0f, damageReductionPercent * damageReductionPenalty);

    // Simulation state reads (use in Simulate / gameplay logic only)
    public float CurrentHealth => currentState.health;
    public float CurrentEnergy => currentState.energy;
    public float CurrentShield => currentState.shield;
    public bool  IsDead        => currentState.isDead;
    public bool  IsSurging     => currentState.isSurging;
    // True only during a Berserk-Surge-style forced Surge (StartBerserkSurge) — grants invincibility for
    // the surge's own duration (see IsInvincible/TakeDamage's own OR against this) without needing a
    // separate, hand-computed invincibility timer that could drift out of sync with however long the surge
    // actually ends up lasting.
    public bool  IsForcedSurging => currentState.forcedSurge;
    // ORs in IsForcedSurging — see its own doc comment for why a forced Surge grants invincibility this way
    // instead of a fixed-duration GrantInvincibility() call. TakeDamage's own guard mirrors this same OR.
    // Also ORs in the separate committed-action invincibility pool — see GrantCommittedInvincibility's own
    // doc comment for why that's a distinct channel from invincibleTimer.
    public bool  IsInvincible  => currentState.invincible || currentState.forcedSurge || currentState.committedInvincibleTimer > 0;
    public bool  IsTaunting    => currentState.taunting;
    public bool  IsInvisible   => currentState.invisible;
    // Gates weapon hit/damage application only — weapons keep orbiting/spinning visually regardless (see
    // WeaponMeleePD/WeaponAxePD/WeaponRangedPD's own read sites). Currently only set by the spin ability's
    // leap (RogueSpinAbilityPD), but generic/persistent so any future effect can reuse it.
    public bool  IsWeaponsDisabled => currentState.weaponsDisabled;
    // Counter, not a bool — more than one active ability could plausibly hold a block at once (unlike
    // IsWeaponsDisabled, ActiveSurgeBlock isn't gated behind ForcesSpecialAction's single-active-special-
    // action mutex, so e.g. a non-locking ability could theoretically overlap with another). Each holder
    // increments on its own Activate() and decrements on its own end-of-active — see IAbilityPD.
    // ActiveSurgeBlock's own doc comment for the full contract. Surge is still allowed to be already-running
    // and get force-ended by whichever ability actually calls ForceEndSurge() itself; this counter only
    // gates STARTING a new one (see PlayerControllerPD.Simulate()'s own surge-start check).
    public bool  IsSurgeBlocked => currentState.surgeBlockCount > 0;
    public float CurrentReviveTime => currentState.currentReviveTime;

    // UI-facing state (set in UpdateView — safe to read from MonoBehaviour.Update)
    // Health/shield use verified state to avoid prediction-rollback jumps.
    // Energy/surge use viewState since the owner predicts these accurately.
    public float UIHealth              { get; private set; }
    public float UIShield              { get; private set; }
    public float UIShieldRechargeTimer { get; private set; }
    public float UIDeathPreventionTimer { get; private set; }
    public float UIEnergy              { get; private set; }
    public bool  UIIsSurging           { get; private set; }
    // Mirrors IsInvincible's own OR exactly, just off viewState instead of raw currentState — same
    // "UI-facing snapshot, not a direct predicted-state read" convention as UIIsSurging (a boolean flag has
    // no visible-jump-on-correction concern the way a drifting numeric value does, so viewState — not
    // verified — is fine here, same reasoning as UIIsSurging's own). Drives the health bar's white
    // invincibility tint in LocalUIManagerPD/PlayerUIWorldSpace.
    public bool  UIIsInvincible        { get; private set; }

    // ── State ──────────────────────────────────────────────────────────────
    public struct State : IPredictedData<State>
    {
        public float health;
        public float energy;
        public float shield;
        // Grant-timer tick count toward the next banked Death Prevention — 0 means "dormant, ready to
        // (re-)arm", same convention as every other countdown-tick field in this class (see Simulate()'s
        // own comment for the full arm/tick/grant cycle).
        public int deathPreventionTimer;
        // Whether an augment has made this character eligible to earn a Death Prevention at all.
        public bool gainsDeathPreventions;
        // Whether one is currently banked and ready to spend — only ever one at a time, never a stacking
        // count.
        public bool hasDeathPrevention;
        public bool isDead;
        public bool isSurging;
        // True only during a Berserk-Surge-style forced Surge (see StartBerserkSurge/IsForcedSurging's own
        // doc comments) — never true at the same time isSurging is false.
        public bool forcedSurge;
        public bool invincible;
        // Enemy-targeting only (2026-07-26, ahead of the Bursts/Abilities Taunt/Invisibility abilities that
        // will actually set these) — see EnemyBasePD.FindClosestTarget for how these are consumed.
        public bool taunting;
        public bool invisible;
        // Weapon hits become purely visual while true — see IsWeaponsDisabled's own doc comment.
        public bool weaponsDisabled;
        // How many currently-active abilities are holding a Surge block right now — see IsSurgeBlocked's
        // own doc comment.
        public int surgeBlockCount;
        // These four are ticks, not seconds — see EnemyBaseState.ContactCooldown.remaining's own doc comment for why
        // (PurrNet's delta-packer costs a full 33 bits for any float that changed since last snapshot; a
        // changed int only costs a cheap zigzag-varint diff). healthRegenTimer is seeded to -1 by
        // GetInitialState() as a "not yet converted" sentinel — see Simulate()'s own comment on it, since
        // GetInitialState() has no delta available yet to convert its seconds-based initial value.
        public int invincibleTimer;
        // Separate pool from invincibleTimer above — see GrantCommittedInvincibility's own doc comment for why.
        public int committedInvincibleTimer;
        // True between PlayerControllerPD's own DashMovementStarted/Ended pair — mirrored here (rather than
        // read live off PlayerControllerPD each TakeDamage() call) since it needs to live in real State to
        // be resimulation-safe. Drives NotifyDodge's own "are we currently in a dash movement" condition —
        // see its own doc comment.
        public bool inDashMovement;
        // Guards FirstDodge (FD) per dash-movement activation — reset the instant inDashMovement flips true.
        // Same "caller owns the guard, reset at the start of their own activation window" shape SI/FSI's own
        // alreadyFiredThisActivation convention uses, just owned directly here since the "activation" in
        // question is this dash-movement window itself. See NotifyDodge's own doc comment.
        public bool dodgedThisMovement;
        // Same duration-max shape as invincibleTimer — see GrantInvisibility's own doc comment.
        public int invisibleTimer;
        public int surgeTickTimer;
        public int surgeMinDurationTimer;
        // Mirrors EnemyBaseState.slowTimer/slowMultiplier — see CharacterStatsPD.Slow()'s own doc comment.
        // Move-speed only (unlike the enemy side, which also slows attack speed) — see MoveSpeed's own read
        // site, AttackSpeed deliberately does NOT read this.
        public int slowTimer;
        public float slowMultiplier;
        public int colorIndex;
        public int healthRegenTimer;
        public int shieldRechargeTimer;
        public uint damageEventId;
        public float currentReviveTime;
        // Single shared orbit angle for EVERY weapon this player owns (melee, axe, ranged alike) — ticked
        // once here instead of each weapon independently decrementing its own copy. A weapon's own SlotIndex
        // * (360/WeaponCount) offset (see WeaponBasePD.OrbitalPosition) only produces evenly-spaced
        // positions if every weapon reads the exact same base angle; when each weapon tracked its own
        // independently-ticking copy, a newly spawned weapon started fresh at 0 while existing weapons had
        // already drifted from spawn, so the two were never actually 360/count degrees apart — this fixes
        // that by construction, since there's only one angle value for every weapon to ever read.
        // CompressedFloat (PurrNet built-in, 0.001-precision quantized float with implicit float
        // conversions both ways) instead of a plain float — this field changes almost every tick for
        // every living player, so it never earns PurrNet's cheap "unchanged" 1-bit case; CompressedFloat's
        // delta-packer costs an integer diff of its internal rounded value instead of a full 33-bit float
        // rewrite. Wrapped to 0-360 every tick in Simulate() (see that comment) both to keep this tidy and
        // to stop CompressedFloat's internal int accumulator from eventually overflowing over a very long
        // session (int overflows past ~2.1 million accumulated degrees at this precision).
        public CompressedFloat weaponOrbitAngle;
        // EVERY stat modifier field in the whole "Augment/bonus modifiers" section of this class lives here,
        // not as a plain field. Started as just 10 fields — the ones RogueVanishAbilityPD/BWBlinkAbilityPD/
        // BWBladeDanceAbilityPD/ExeBigAxeAbilityPD/KnightBigBuffAbilityPD/ArcherFocusFireAbilityPD happened to
        // already add a temporary amount to on Activate() and subtract back off later inside Simulate() — a
        // plain field doesn't roll back on resimulation (only STATE does), so a mispredicted tick that gets
        // replayed across either half of an add/subtract pair would double-apply it, permanently drifting the
        // stat every time. Expanded to cover every remaining bonus/total/penalty field once it became clear
        // the same pattern already existed elsewhere untouched (SurgeWeaponCountAugment reversing
        // totalOnHitPowerPercent, CriticalHealingAugment reversing critHealPowerMultiplier — neither in the
        // original 10) and, more importantly, that NOTHING stops any future ability/augment from reusing any
        // OTHER field here the same way — the field being "only ever written once so far" was never a
        // property of the field itself, just of what happened to call it to date. Every writer (permanent
        // augments, ClassPassiveData, meta-progression, temporary ability buffs alike) goes through the same
        // Add*/Set*/Multiply* methods below either way, so moving the backing storage here is strictly safer
        // for all of them, never a behavior change. See feedback_ability_temp_buff_state_safety memory for
        // the full incident history.
        public float totalMoveSpeedPercent;
        public float attackSpeedTotalPercent;
        public float totalWeaponRangePercent;
        public float executeThreshold;
        public float bonusCritChanceFlat;
        public float bonusSizePercent;
        public float totalMaxHealthPercent;
        public float totalStrengthPercent;
        public float damageReductionPercent;
        public float fireRateTotalPercent;
        public float bonusMoveSpeedFlat;
        public float bonusMoveSpeedPercent;
        public float moveSpeedPenalty;
        public float totalMoveSpeedPenalty;
        public float bonusStrengthFlat;
        public float bonusStrengthPercent;
        public float bonusStrengthPenalty;
        public float totalStrengthPenalty;
        public float bonusWeaponRadiusFlat;
        public float bonusWeaponRadiusPercent;
        public float totalWeaponRadiusPercent;
        public float totalWeaponRadiusPenalty;
        public float attackSpeedFlat;
        public float attackSpeedPenalty;
        public int   bonusWeaponCountFlat;
        public int   weaponCountMultiplier;
        public float bonusWeaponRangeFlat;
        public float bonusWeaponRangePercent;
        public float weaponRangePenalty;
        public float totalWeaponRangePenalty;
        public float bonusCritChancePercent;
        public float totalCritChancePercent;
        public float totalCritChancePenalty;
        public float bonusCritDamageFlat;
        public float bonusCritDamagePercent;
        public float bonusRicochetFlat;
        public float bonusRicochetPercent;
        public float totalRicochetPercent;
        public float totalRicochetPenalty;
        public float bonusMultishotFlat;
        public float bonusMultishotPercent;
        public float bonusMultishotSpreadPercent;
        public float totalMultishotPercent;
        public float totalMultishotPenalty;
        public float projectileDamageDropoffReduction;
        public float projectileOnHitDropoffReduction;
        public float fireRateFlat;
        public float fireRateTotalPenalty;
        public float projectileSpeedFlat;
        public float projectileSpeedTotalPercent;
        public float projectileSpeedTotalPenalty;
        public float bonusMaxHealthFlat;
        public float bonusMaxHealthPercent;
        public float totalMaxHealthPenalty;
        // Conversion-source bookkeeping (2026-08-19) — see ConvertedHealth's own doc comment.
        public float convertedHealth;
        public float bonusHealthRegenFlat;
        public float bonusHealthRegenPercent;
        public float totalHealthRegenPercent;
        public float totalHealthRegenPenalty;
        // Conversion-source bookkeeping (2026-08-19) — see ConvertedHealthRegen's own doc comment.
        public float convertedHealthRegenFlat;
        public float bonusMaxShieldAdditive;
        public float totalMaxShieldPercent;
        public float totalMaxShieldPenalty;
        public float deathPreventionTimeMultiplier;
        public float bonusMaxEnergyFlat;
        public float bonusMaxEnergyPercent;
        public float bonusEnergyRegenFlat;
        public float bonusEnergyRegenPercent;
        public float energyRegenPenalty;
        public float totalEnergyRegenPercent;
        public float totalEnergyRegenPenalty;
        public float bonusSurgeCostFlat;
        public float surgeCostReductionPercent;
        public float surgeCostIncreasePercent;
        public float surgeAttackSpeedPenalty;
        public float dashHaste;
        public float bonusDashHastePercent;
        public float totalDashHastePercent;
        public float totalDashHastePenalty;
        public float dashCooldownReductionPercent;
        public float dashCooldownIncreaseMultiplier;
        public float abilityHaste;
        public float bonusAbilityHastePercent;
        public float totalAbilityHastePercent;
        public float totalAbilityHastePenalty;
        public float bonusLuckFlat;
        public float bonusLuckPercent;
        public float luckPenalty;
        public float totalLuckPercent;
        public float totalLuckPenalty;
        public float bonusLuckFloorPercent;
        public float luckFloorPenalty;
        public float bonusLuckCeilingFlat;
        public float bonusLuckCeilingPercent;
        public float luckCeilingPenalty;
        public float bonusHighrollPercent;
        public float highrollPenalty;
        public float bonusHighrollBonusPercent;
        public float highrollBonusPenalty;
        public float knockbackMultiplier;
        public float knockbackPenalty;
        public float bonusOnHitPowerMultiplier;
        public float bonusOnHitPowerPenalty;
        public float totalOnHitPowerPercent;
        public float totalOnHitPowerPenalty;
        public float stackPowerPercent;
        public float stackPowerPenalty;
        public float healPowerMultiplier;
        public float healPowerPenalty;
        public float critHealPowerMultiplier;
        public float critHealPowerPenalty;
        public float areaEffectSizePercent;
        public float areaEffectSizePenalty;
        public float areaEffectPowerPercent;
        public float areaEffectPowerPenalty;
        public int   areaEffectCountFlat;
        public int   areaEffectCountPenalty;
        public float outerXZMultiplierPercent;
        public float xpMultiplier;
        public float xpPenalty;
        public float bonusXPRadiusFlat;
        public float bonusXPRadiusPercent;
        public float reviveTimePenalty;
        public float reviveTimePenaltyReduction;
        public float revivePower;
        public float damageReductionPenalty;
        public float phantomBladeDamageBonus;
        public float phantomBladeSizeBonus;
        public float phantomBladeDamageBonusPercent;
        public float phantomBladeSizeBonusPercent;
        public float phantomBladeDamagePenalty;
        public float phantomBladeSizePenalty;
        public float phantomBladeOnHitPowerPercent;
        public int   deathEventCount;
        public int   killEventCount;
        public float energyRegenWhileSurgingPercent;
        public float critExecuteThreshold;
        // "Special action" overrides (specialSpinRate through specialHeightOffset) — set/cleared from
        // whichever effect's own Simulate() is currently driving them (RogueSpinAbilityPD,
        // ArcherFocusFireAbilityPD, BWBladeDanceAbilityPD). Previously plain fields on the theory that the
        // driving effect reasserts the correct value every tick it's active, so a rollback not resetting
        // them in between couldn't cause drift — migrated anyway for consistency, since that reasoning
        // depends on every future caller actually reasserting every tick, which nothing enforces.
        public float   specialSpinRate;
        public bool    useSpecialSpinRate;
        public bool    weaponOrbitFlipped;
        public bool    continuousFire;
        public float   focusFireAimAngle;
        public bool    continuousFireHasTarget;
        public float   focusFireRateMultiplier;
        public bool    weaponOrbitOriginOverridden;
        public Vector3 weaponOrbitOriginOverride;
        public float   specialHeightOffset;
        // Neutral default 0.5f, not 1f or 0f — see this field's own doc comment further down for why it's
        // the one Surge-header field that needed migrating.
        public float   surgeMoveSpeedPenalty;
        // Progression-only fields, mutated once by ApplyProgressionBonuses — see strengthPerLevel's own doc
        // comment for why these are here despite that.
        public float strengthPerLevel;
        public int   weaponTracks;
        public float shieldRechargeTime;
        // Which AugmentUnlockTagDatabase tags this player currently has unlocked, as a bitmask (bit N = tag
        // at database index N — see that class's own doc comment). Same "progression-only, mutated once by
        // ApplyProgressionBonuses" shape as strengthPerLevel above — see UnlockedAugmentTagMask's own doc
        // comment for why this needs to be real replicated State rather than a plain local field.
        public long  unlockedAugmentTagMask;
        public int   dashCharges;
        public int   levelUpOptions;
        public bool  phantomBladeOnHitEnabled;
        // Read by AugmentCardUI/PlayerControllerPD (local-UI-only, never a Simulate()-context gameplay
        // decision) to decide whether this player's own level-up stat-pick cards should roll luck variance
        // + highroll — see LuckRolledLevelUpStatsAugment's own doc comment for the full mechanism. Still
        // lives in State per this section's own blanket policy, even though nothing currently needs it
        // predicted/rollback-safe.
        public bool  luckRolledLevelUpStats;
        // Scaled-down fraction of Stats.HighrollBonus applied to a luck-rolled level-up stat pick — see
        // LuckRolledLevelUpStatsAugment's own highrollBonusScale doc comment for why this can't just use the
        // full (damage-scope) HighrollBonus value directly.
        public float luckRolledLevelUpStatsHighrollScale;
        public bool  modelSidewaysTipped;
        public float orbitFacingYaw;
        public bool  lethalDamagePrevented;
        public void Dispose() { }
    }

    protected override State GetInitialState() => new()
    {
        health = baseMaxHealth,
        energy = baseMaxEnergy,
        shield = 0f,
        // Sentinel meaning "not yet converted from seconds to ticks" — GetInitialState() has no delta
        // available yet. Simulate() converts this to the real tick count exactly once, the first time it
        // ever runs for this character — see its own comment. -1 is safe/unambiguous since the normal
        // countdown logic always reassigns to a valid positive count before a Simulate() call ends, so a
        // legitimately-running countdown can never be negative when read back.
        healthRegenTimer = -1,
        shieldRechargeTimer = 0,
        currentReviveTime = reviveTime,
        // 1f (neutral multiplier) baseline — see the State struct's own doc comment on this whole block of
        // fields. Every "Flat"-suffixed field (and a handful of others whose neutral is genuinely 0, like
        // dashHaste/abilityHaste/executeThreshold/critExecuteThreshold/phantomBladeDamageBonus) is
        // deliberately absent here — their neutral value IS 0f, already the struct's own zero-init default.
        totalMoveSpeedPercent = 1f,
        attackSpeedTotalPercent = 1f,
        totalWeaponRangePercent = 1f,
        bonusSizePercent = 1f,
        totalMaxHealthPercent = 1f,
        totalStrengthPercent = 1f,
        damageReductionPercent = 1f,
        fireRateTotalPercent = 1f,
        bonusMoveSpeedPercent = 1f,
        moveSpeedPenalty = 1f,
        totalMoveSpeedPenalty = 1f,
        bonusStrengthPercent = 1f,
        bonusStrengthPenalty = 1f,
        totalStrengthPenalty = 1f,
        bonusWeaponRadiusPercent = 1f,
        totalWeaponRadiusPercent = 1f,
        totalWeaponRadiusPenalty = 1f,
        attackSpeedPenalty = 1f,
        weaponCountMultiplier = 1,
        bonusWeaponRangePercent = 1f,
        weaponRangePenalty = 1f,
        totalWeaponRangePenalty = 1f,
        bonusCritChancePercent = 1f,
        totalCritChancePercent = 1f,
        totalCritChancePenalty = 1f,
        bonusCritDamagePercent = 1f,
        bonusRicochetPercent = 1f,
        totalRicochetPercent = 1f,
        totalRicochetPenalty = 1f,
        bonusMultishotPercent = 1f,
        bonusMultishotSpreadPercent = 1f,
        totalMultishotPercent = 1f,
        totalMultishotPenalty = 1f,
        projectileDamageDropoffReduction = 1f,
        projectileOnHitDropoffReduction = 1f,
        fireRateTotalPenalty = 1f,
        projectileSpeedTotalPercent = 1f,
        projectileSpeedTotalPenalty = 1f,
        bonusMaxHealthPercent = 1f,
        totalMaxHealthPenalty = 1f,
        bonusHealthRegenPercent = 1f,
        totalHealthRegenPercent = 1f,
        totalHealthRegenPenalty = 1f,
        totalMaxShieldPercent = 1f,
        totalMaxShieldPenalty = 1f,
        bonusMaxEnergyPercent = 1f,
        bonusEnergyRegenPercent = 1f,
        energyRegenPenalty = 1f,
        totalEnergyRegenPercent = 1f,
        totalEnergyRegenPenalty = 1f,
        surgeCostReductionPercent = 1f,
        surgeAttackSpeedPenalty = 1f,
        bonusDashHastePercent = 1f,
        totalDashHastePercent = 1f,
        totalDashHastePenalty = 1f,
        dashCooldownReductionPercent = 1f,
        dashCooldownIncreaseMultiplier = 1f,
        bonusAbilityHastePercent = 1f,
        totalAbilityHastePercent = 1f,
        totalAbilityHastePenalty = 1f,
        bonusLuckPercent = 1f,
        luckPenalty = 1f,
        totalLuckPercent = 1f,
        totalLuckPenalty = 1f,
        bonusLuckFloorPercent = 1f,
        luckFloorPenalty = 1f,
        bonusLuckCeilingPercent = 1f,
        luckCeilingPenalty = 1f,
        bonusHighrollPercent = 1f,
        highrollPenalty = 1f,
        bonusHighrollBonusPercent = 1f,
        highrollBonusPenalty = 1f,
        knockbackMultiplier = 1f,
        knockbackPenalty = 1f,
        bonusOnHitPowerMultiplier = 1f,
        bonusOnHitPowerPenalty = 1f,
        totalOnHitPowerPercent = 1f,
        totalOnHitPowerPenalty = 1f,
        stackPowerPercent = 1f,
        stackPowerPenalty = 1f,
        healPowerMultiplier = 1f,
        healPowerPenalty = 1f,
        critHealPowerMultiplier = 1f,
        critHealPowerPenalty = 1f,
        areaEffectSizePercent = 1f,
        areaEffectSizePenalty = 1f,
        areaEffectPowerPercent = 1f,
        areaEffectPowerPenalty = 1f,
        areaEffectCountFlat = 1,
        areaEffectCountPenalty = 1,
        outerXZMultiplierPercent = 1f,
        xpMultiplier = 1f,
        xpPenalty = 1f,
        bonusXPRadiusPercent = 1f,
        reviveTimePenalty = 1f,
        reviveTimePenaltyReduction = 1f,
        revivePower = 1f,
        damageReductionPenalty = 1f,
        phantomBladeDamageBonusPercent = 1f,
        phantomBladeSizeBonusPercent = 1f,
        phantomBladeDamagePenalty = 1f,
        phantomBladeSizePenalty = 1f,
        surgeMoveSpeedPenalty = 0.5f,
        strengthPerLevel = 5f,
        weaponTracks = 1,
        shieldRechargeTime = 5f,
        dashCharges = 1,
        levelUpOptions = 3
    };

    // ── Events ─────────────────────────────────────────────────────────────
    // Per-instance (not static): each character's events are its own, not a shared global channel.
    // PredictedEvent handles view-safety itself — server always fires, the owner fires once on its
    // own live (non-replay) pass, and non-owners fire once the tick is verified — so callers don't
    // need to reason about replay/ownership at the invoke site.
    private PredictedEvent<HealData> _onHeal;
    // Fires whenever any hit this player lands highrolls — moved here from being owned by whichever weapon
    // actually landed the hit (see feedback_predictedevent_anchor_persistent_identity memory / the hit-
    // feedback despawn-race fix this mirrors): a weapon can despawn (WeaponManagerPD.DespawnWeapons, e.g.
    // SurgeWeaponCountAugment ending) shortly after landing a hit, and a non-owner client only ever fires a
    // PredictedEvent once its tick verifies, which requires the OWNING identity to still exist to be
    // resimulated — a despawned weapon can never satisfy that. First real subscriber (2026-08-19):
    // OnStrikeHighrollReduceDashCDAugment — see StrikeHighrollData's own doc comment for the payload.
    //
    // Named Strike specifically (not a generic _onHighroll) — every current caller is a weapon/ability
    // STRIKE damage source (WeaponMeleePD/WeaponAxePD/WeaponRangedProjectilePD/RogueSpinAbilityPD/
    // ExeAxeThrowProjectilePD). Heals do NOT highroll at all (2026-08-12: heals stopped rolling luck variance
    // entirely, so there's no roll for a heal to ever land in a highroll window — see CharacterStatsPD.Heal's
    // own doc comment), so this only ever needs to cover Strike sources.
    private PredictedEvent<StrikeHighrollData> _onStrikeHighroll;
    // Fires whenever energy is actually spent — currently only Surge's own cost (StartSurge()'s upfront
    // SurgeCost, and the periodic SurgeTickInterval drain in Simulate()), the only place energy decreases
    // anywhere in this codebase today. No current subscriber (2026-08-23 — PhantomBladeEnergyRegenAugment,
    // the original/only one, was retired) — still a valid generic hook for a future augment.
    private PredictedEvent<EnergySpentData> _onEnergySpent;
    // Fires on the HEALER's own Stats whenever HealFromAlly() lands on someone else — see
    // AllyHealGrantedData's own doc comment. First subscriber: PhantomBladeOnHealAugment (wants heals GRANTED
    // to allies to also count toward its own threshold, not just self-heals).
    private PredictedEvent<AllyHealGrantedData> _onAllyHealGranted;
    private PredictedEvent _onSurgeStarted;
    private PredictedEvent _onSurgeEnded;
    // Fires the tick shield crosses from >0 to <=0 inside TakeDamage — see that method's own 0-crossing
    // check. No payload (matches _onSurgeStarted/_onSurgeEnded) since "which player" is already implicit
    // per-instance and there's no other useful data to carry (overflow damage already went to health in
    // the same call, before this fires).
    private PredictedEvent _onShieldBreak;
    // Fires the tick shield crosses from <=0 back up to >0 — the natural Simulate()-driven regen (see that
    // method's own shield-regen block) and the instant RechargeShield() both funnel through this same
    // detection so "reapply while shielded"-style effects (e.g. DeathPreventionShieldDRAugment) never need to
    // care which path actually refilled the shield.
    private PredictedEvent _onShieldRecharge;
    // Fires from inside TakeDamage the moment it determines the incoming hit WOULD reduce health to <=0,
    // before that reduction is actually applied — gives a death-prevention effect a chance to call
    // PreventLethalDamage() synchronously (see that method's own doc comment) before TakeDamage finalizes
    // the outcome. No payload — a listener that wants to react conditionally (e.g. a cooldown) tracks that
    // itself; the incoming damage amount isn't needed to decide whether to intervene. No subscribers as of
    // the deathPreventions rework (DeathPreventionShieldDRAugment moved to _onDeathPrevented below) — kept for
    // any future listener-based save that wants the SAME "veto before it happens" hook this already provides.
    private PredictedEvent _onLethalDamageTaken;
    // Fires from inside TakeDamage the moment a banked Death Prevention actually gets spent (health set to
    // 1, invincibility already granted by TakeDamage itself before this fires) — for effects that want to
    // react to the save landing, not decide whether it happens (that's what gainsDeathPreventions/
    // hasDeathPrevention already gate). e.g. DeathPreventionShieldDRAugment refreshing shield on this.
    private PredictedEvent _onDeathPrevented;
    // Fires from inside TakeDamage whenever incoming damage actually reduces shield and/or health by a
    // positive amount (gate itself unchanged from before DamageTakenData existed — post-DamageReduction,
    // before the shield/health split) — e.g. PhantomBladeOnDamagedAugment. Excludes a killing blow, same
    // reasoning as _onShieldBreak: a "reacted to taking damage" effect firing on a player who's already dead
    // doesn't make sense.
    private PredictedEvent<DamageTakenData> _onDamaged;
    // Called directly by whichever weapon/projectile actually landed the crit (WeaponMeleePD.Hit(),
    // WeaponAxePD.Hit(), WeaponRangedProjectilePD.ProcessHit()) via NotifyCritStrike — not relayed through
    // WeaponManagerPD or any per-weapon event (see feedback_predictedevent_anchor_persistent_identity memory
    // for why: a weapon can despawn before its own event's verification window closes, silently dropping the
    // notification on other clients). The one channel downstream per-player systems like
    // PhantomBladeOnCritAugment subscribe to, instead of each augment needing to know about individual
    // weapons at all. Deliberately per-instance like every other event here, not static — this is what
    // makes cross-player interaction structurally impossible rather than just filtered against.
    private PredictedEvent<WeaponHitData> _onCritStrike;
    // Same direct-call shape as _onCritStrike, but fires for every weapon hit (crit or not) — e.g.
    // CritTriggerOnHitAugment subscribes to both and calls NotifyHit again from its own crit listener to
    // make a crit count as two hits.
    private PredictedEvent<WeaponHitData> _onHit;
    // Fires once per REAL weapon connection with an enemy — called directly alongside _onHit/_onCritStrike
    // exactly like them, but deliberately never touched by anything that re-invokes _onHit/NotifyHit
    // (e.g. CritTriggerOnHitAugment calling Stats.NotifyHit again from its own crit listener). Effects that
    // must fire exactly once per genuine strike, not once-or-twice depending on other augments (e.g.
    // OnStrikeEnergyRegenAugment), subscribe here instead of to _onHit.
    private PredictedEvent<WeaponHitData> _onStrike;
    // Fires whenever any of this player's weapons touches (not damages) an ally — called directly by each
    // weapon (same direct-call convention as _onHit/_onCritStrike/_onStrike, since "touched an ally"
    // isn't a per-weapon-type concept anything else needs to distinguish). Generic on purpose: multiple
    // "hit ally, do something" augments (OnStrikeHealAllyAugment and future family members) can each
    // subscribe independently and decide what to do with the touch themselves, rather than this class
    // baking in any one specific effect.
    private PredictedEvent<AllyStrikeData> _onAllyStrike;
    // Fires once per "primary" Phantom Blade summon (every SummonAtTarget call whose notifySummon param is
    // left true — i.e. every real trigger: crit, passive tick, dash, onhit), carrying the already-fully-
    // resolved damage/size so a listener like PhantomBladeCarnivalAugment can spawn an exact duplicate
    // without re-rolling luck variance. Never fires for a copy spawned in reaction to this same event —
    // SummonCopyAtTarget doesn't call SummonAtTarget at all, so there's no path back into this event from
    // the copy itself, structurally ruling out recursion rather than needing a guard flag to prevent it.
    private PredictedEvent<PhantomBladeSummonData> _onPhantomBladeSummon;
    // Fire whenever SetTaunting/SetInvisible actually flips the flag (never on a no-op call — see those
    // methods' own guard) — the enemy-targeting hub (EnemySpawnerPD) subscribes to these once per player and
    // fans out to every active enemy, so a taunt/invisibility change reacts immediately instead of enemies
    // needing to poll for it. bool payload is the new value, matching the "which player" being implicit
    // per-instance convention every other event here already uses.
    private PredictedEvent<bool> _onTauntChanged;
    private PredictedEvent<bool> _onInvisibleChanged;
    // Fires the instant an ability's own AbilityIsActive flips true/false (see IAbilityPD.AbilityIsActive's
    // own doc comment) — generic across both slots, for augments that want to react to "an ability started/
    // ended" regardless of which one, same "owned by the persistent CharacterStatsPD, not the (non-transient
    // but still not the natural augment-facing hub) ability component itself" convention as every other
    // augment-facing event here. Payload is the live IAbilityPD reference itself (not just its AbilitySlot)
    // — safe to pass directly, unlike a pooled/transient IEnemyPD, since every ability component is a
    // permanent one-per-character fixture for the whole match (same "pass the live reference" precedent as
    // HitFeedbackData.attacker carrying a live CharacterStatsPD). Lets a listener read Slot, Name,
    // BaseCooldownDuration, or anything else IAbilityPD exposes directly off the payload instead of caching
    // its own GetComponents<IAbilityPD>() lookup just to resolve which instance fired.
    private PredictedEvent<IAbilityPD> _onAbilityStart;
    private PredictedEvent<IAbilityPD> _onAbilityEnd;

    // Generic "something interrupted an enemy" channel — ANY IEnemyPD.Interrupt() caller (dash sweep,
    // AxeThrow, Knight StunHeal, future sources) funnels through here instead of each interrupt source
    // needing its own bespoke event, same "owned by the persistent CharacterStatsPD, not whichever
    // transient weapon/ability/augment-effect landed it" reasoning as _onHitFeedback above. No payload —
    // "which player" is already implicit per-instance, and no listener so far needs to know which enemy or
    // which source. _onSuccessfulInterrupt (SI) fires once per successful interrupt (Interrupt() returned
    // true); _onFirstSuccessfulInterrupt (FSI) fires once per "activation" (one dash, one ability cast, one
    // AxeThrow leg — see NotifyInterrupted's own doc comment) — callers should call NotifyInterrupted below
    // rather than invoking these two directly, so the once-per-activation gating logic lives in one place.
    private PredictedEvent _onSuccessfulInterrupt;
    private PredictedEvent _onFirstSuccessfulInterrupt;

    // Same SI/FSI shape, "hit while dash-movement-invincible" instead of "successfully interrupted an
    // enemy" — see NotifyDodge's own doc comment. No external caller (unlike NotifyInterrupted, called by
    // every interrupt source) — TakeDamage is the only place this can ever fire, so both are invoked
    // entirely internally.
    private PredictedEvent _onDodge;
    private PredictedEvent _onFirstDodge;

    // Fires exactly once, server-side only, the moment the host resolves this player's authoritative color
    // (see GameStartData.HostColorAssignments' own doc comment) — colorIndex is only ever written here,
    // never by client input, so unlike most other events in this file there's no owner-side "predict it
    // early" branch to reconcile: a client's own pre-match color guess used to be the actual bug (see
    // PlayerDataPD's git history), so nothing but the server's own resolution is ever trusted or applied.
    // Self-subscribed to ApplyColor below rather than something PlayerDataPD (or anything else) polls every
    // frame — PredictedEvent already guarantees the listener fires once per real occurrence regardless of
    // resimulation, same as _onShieldRecharge/_onSurgeEnded above.
    private PredictedEvent<int> _onColorChanged;

    // Owned here rather than by whichever weapon/projectile/blade/augment-effect actually landed the hit —
    // every one of those emitters (WeaponBasePD, WeaponRangedProjectilePD, PhantomBladePD, AugmentEffectPD)
    // is transient (despawns/gets deleted on its own schedule, independent of the hit it just landed),
    // whereas this player's own CharacterStatsPD is persistent for the whole match. PredictedEvent<T> only
    // actually fires for a non-owner, non-server client once the tick becomes verified, which requires the
    // OWNING identity to still exist to be resimulated when that confirmation lands — a hit fired from a
    // since-despawned weapon (e.g. SurgeWeaponCountAugment's temporary extra weapon, or any short-lived
    // projectile/blade) could permanently lose its feedback on every other client if it depended on the
    // weapon's own event instead of this one. Every emitter already carries `attacker` (this identity) in
    // its HitFeedbackData payload, so routing the dispatch itself through here instead costs nothing.
    private PredictedEvent<HitFeedbackData> _onHitFeedback;

    // Self-subscribed relay to this player's own local status-popup UI, same "no external Add/Remove
    // wrappers, nothing outside this class needs to subscribe" shape as _onHitFeedback just above.
    // DamageNumberManager is a per-client singleton with no prediction-awareness of its own, so any write
    // into it needs to originate from an already resimulation-safe context — PredictedEvent<string>
    // guarantees that here (fires once per real occurrence: always on the server, once per real tick for
    // the owner even across rollback replays, only once verified for every other client watching this
    // player), so any caller of NotifyStatusPopup below can fire it straight from Simulate()-context code
    // (e.g. StartBerserkSurge) without worrying about duplicate popups during resimulation.
    private PredictedEvent<StatusPopupData> _onStatusPopup;

    // Dedicated event for player-received damage numbers, deliberately NOT reusing _onDamaged (that one
    // skips the killing blow — see its own "not on the killing blow" exclusion — but a damage number should
    // still print on the hit that kills the player, same as an enemy's own killing hit still shows one via
    // EnemyBasePD.TriggerHitFeedback). Same PredictedEvent<T> resimulation-safety reasoning as
    // _onStatusPopup above — lets TakeDamage's own Simulate()-context call at the end of this method Invoke
    // this directly without risking duplicate popups during rollback.
    private PredictedEvent<PlayerDamageNumberData> _onPlayerDamageNumber;

    public void AddHealListener(Action<HealData> listener) => _onHeal.AddListener(listener);
    public void RemoveHealListener(Action<HealData> listener) => _onHeal.RemoveListener(listener);
    public void AddStrikeHighrollListener(Action<StrikeHighrollData> listener) => _onStrikeHighroll.AddListener(listener);
    public void RemoveStrikeHighrollListener(Action<StrikeHighrollData> listener) => _onStrikeHighroll.RemoveListener(listener);
    public void AddEnergySpentListener(Action<EnergySpentData> listener) => _onEnergySpent.AddListener(listener);
    public void RemoveEnergySpentListener(Action<EnergySpentData> listener) => _onEnergySpent.RemoveListener(listener);
    public void AddAllyHealGrantedListener(Action<AllyHealGrantedData> listener) => _onAllyHealGranted.AddListener(listener);
    public void RemoveAllyHealGrantedListener(Action<AllyHealGrantedData> listener) => _onAllyHealGranted.RemoveListener(listener);
    public void NotifyStrikeHighroll(float damage, float onHitPower) => _onStrikeHighroll.Invoke(new StrikeHighrollData { damage = damage, onHitPower = onHitPower });
    public void AddSurgeStartedListener(Action listener) => _onSurgeStarted.AddListener(listener);
    public void RemoveSurgeStartedListener(Action listener) => _onSurgeStarted.RemoveListener(listener);
    public void AddSurgeEndedListener(Action listener) => _onSurgeEnded.AddListener(listener);
    public void RemoveSurgeEndedListener(Action listener) => _onSurgeEnded.RemoveListener(listener);
    public void AddShieldBreakListener(Action listener) => _onShieldBreak.AddListener(listener);
    public void RemoveShieldBreakListener(Action listener) => _onShieldBreak.RemoveListener(listener);
    public void AddShieldRechargeListener(Action listener) => _onShieldRecharge.AddListener(listener);
    public void RemoveShieldRechargeListener(Action listener) => _onShieldRecharge.RemoveListener(listener);
    public void AddLethalDamageTakenListener(Action listener) => _onLethalDamageTaken.AddListener(listener);
    public void RemoveLethalDamageTakenListener(Action listener) => _onLethalDamageTaken.RemoveListener(listener);
    public void AddDeathPreventedListener(Action listener) => _onDeathPrevented.AddListener(listener);
    public void RemoveDeathPreventedListener(Action listener) => _onDeathPrevented.RemoveListener(listener);
    public void AddDamagedListener(Action<DamageTakenData> listener) => _onDamaged.AddListener(listener);
    public void RemoveDamagedListener(Action<DamageTakenData> listener) => _onDamaged.RemoveListener(listener);
    public void AddCritStrikeListener(Action<WeaponHitData> listener) => _onCritStrike.AddListener(listener);
    public void RemoveCritStrikeListener(Action<WeaponHitData> listener) => _onCritStrike.RemoveListener(listener);
    public void AddTauntChangedListener(Action<bool> listener) => _onTauntChanged.AddListener(listener);
    public void RemoveTauntChangedListener(Action<bool> listener) => _onTauntChanged.RemoveListener(listener);
    public void AddInvisibleChangedListener(Action<bool> listener) => _onInvisibleChanged.AddListener(listener);
    public void RemoveInvisibleChangedListener(Action<bool> listener) => _onInvisibleChanged.RemoveListener(listener);
    public void AddAbilityStartListener(Action<IAbilityPD> listener) => _onAbilityStart.AddListener(listener);
    public void RemoveAbilityStartListener(Action<IAbilityPD> listener) => _onAbilityStart.RemoveListener(listener);
    public void NotifyAbilityStart(IAbilityPD ability) => _onAbilityStart.Invoke(ability);
    public void AddAbilityEndListener(Action<IAbilityPD> listener) => _onAbilityEnd.AddListener(listener);
    public void RemoveAbilityEndListener(Action<IAbilityPD> listener) => _onAbilityEnd.RemoveListener(listener);
    public void AddSuccessfulInterruptListener(Action listener) => _onSuccessfulInterrupt.AddListener(listener);
    public void RemoveSuccessfulInterruptListener(Action listener) => _onSuccessfulInterrupt.RemoveListener(listener);
    public void NotifySuccessfulInterrupt() => _onSuccessfulInterrupt.Invoke();
    public void AddFirstSuccessfulInterruptListener(Action listener) => _onFirstSuccessfulInterrupt.AddListener(listener);
    public void RemoveFirstSuccessfulInterruptListener(Action listener) => _onFirstSuccessfulInterrupt.RemoveListener(listener);
    public void NotifyFirstSuccessfulInterrupt() => _onFirstSuccessfulInterrupt.Invoke();

    // Fires SuccessfulInterrupt (SI) unconditionally, and FirstSuccessfulInterrupt (FSI) only if this is
    // the first successful interrupt the caller has seen this "activation" (one dash, one ability cast, one
    // AxeThrow leg, etc.) — every interrupt source should call this instead of invoking
    // NotifySuccessfulInterrupt/NotifyFirstSuccessfulInterrupt directly, so the once-per-activation gating
    // logic lives in exactly one place. Caller owns alreadyFiredThisActivation and resets it themselves at
    // the start of their own activation window.
    //
    // Also owns granting IEnemyPD.InterruptTrueStun on the interrupted enemy — fires on EVERY successful
    // interrupt (SI), not just FSI, per explicit design: every enemy an interrupt-capable effect lands on
    // this activation gets the bonus true-stun, not just the first. pairedStunDuration is 0 (default) for an
    // interrupt with no paired stun (DashThroughInterruptAugment, ExeAxeThrowProjectilePD) — a caller that
    // ALSO explicitly stuns whatever it interrupts on the same hit (KnightAreaStunHealAbilityPD) passes that
    // same raw duration here instead of relying on its own separate Stun() call to cover it — see
    // InterruptTrueStun's own doc comment for how the two portions combine.
    public void NotifyInterrupted(IEnemyPD enemy, ref bool alreadyFiredThisActivation, float pairedStunDuration = 0f)
    {
        NotifySuccessfulInterrupt();
        enemy?.InterruptTrueStun(pairedStunDuration);
        if (alreadyFiredThisActivation) return;
        alreadyFiredThisActivation = true;
        NotifyFirstSuccessfulInterrupt();
    }

    public void AddDodgeListener(Action listener) => _onDodge.AddListener(listener);
    public void RemoveDodgeListener(Action listener) => _onDodge.RemoveListener(listener);
    public void AddFirstDodgeListener(Action listener) => _onFirstDodge.AddListener(listener);
    public void RemoveFirstDodgeListener(Action listener) => _onFirstDodge.RemoveListener(listener);

    // A "Dodge" is exactly 3 conditions, all satisfied simultaneously (2026-08-21, explicit design):
    // 1. Currently inside a dash-movement window (currentState.inDashMovement — mirrors PlayerControllerPD's
    //    own DashMovementStarted/Ended pair, see OnDashMovementStarted/Ended below).
    // 2. Invincible (already guaranteed true here — this is only ever called from inside TakeDamage's own
    //    invincibility-blocked branch, which covers EVERY invincibility source, not just the dash's own
    //    committedInvincibleTimer specifically; deliberately not narrowed to "invincible BECAUSE of the
    //    dash" — the three conditions are independently checked, not causally linked).
    // 3. Actually hit — TakeDamage() was called at all. Every player-facing TakeDamage() call in this
    //    codebase originates from an enemy source (contact damage, projectiles, hazards — confirmed no
    //    self-damage/environmental caller exists), so reaching this point already satisfies this condition.
    //    EXCLUDES Pleb-tier attackers (2026-08-21) and purely passive/ambient contact damage (2026-08-21,
    //    later same day — see EnemyBasePD.ContactDamageIsPassive's own doc comment) — TakeDamage's own
    //    isPleb/isPassiveContact parameters gate the call to this method entirely, not a condition checked
    //    in here.
    //
    // Fires Dodge (D) unconditionally, FirstDodge (FD) only the first time within this dash-movement's own
    // activation window — same SI/FSI shape NotifyInterrupted uses above, just owned entirely internally
    // here since TakeDamage is the only possible caller (no ref bool to thread through from outside).
    private void NotifyDodge()
    {
        _onDodge.Invoke();
        if (currentState.dodgedThisMovement) return;
        currentState.dodgedThisMovement = true;
        _onFirstDodge.Invoke();
    }

    // Self-subscribed relay straight to the shared status-popup pipeline — same "no external Add/Remove
    // wrappers needed, nothing outside this class needs to subscribe" shape OnDeathPreventedStatusPopup
    // already uses for _onDeathPrevented.
    private void OnDodgeStatusPopup() => NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Dodged);

    private void OnDashMovementStarted()
    {
        currentState.inDashMovement = true;
        currentState.dodgedThisMovement = false;
    }

    private void OnDashMovementEnded() => currentState.inDashMovement = false;
    public void NotifyAbilityEnd(IAbilityPD ability) => _onAbilityEnd.Invoke(ability);
    public void AddColorChangedListener(Action<int> listener) => _onColorChanged.AddListener(listener);
    public void RemoveColorChangedListener(Action<int> listener) => _onColorChanged.RemoveListener(listener);
    public void NotifyCritStrike(WeaponHitData data) => _onCritStrike.Invoke(data);
    public void AddHitListener(Action<WeaponHitData> listener) => _onHit.AddListener(listener);
    public void RemoveHitListener(Action<WeaponHitData> listener) => _onHit.RemoveListener(listener);
    public void NotifyHit(WeaponHitData data) => _onHit.Invoke(data);
    public void AddStrikeListener(Action<WeaponHitData> listener) => _onStrike.AddListener(listener);
    public void RemoveStrikeListener(Action<WeaponHitData> listener) => _onStrike.RemoveListener(listener);
    // _onStrike itself fires exactly once per real strike (never doubled by CritTriggerOnHitAugment
    // re-invoking _onHit) — NotifyStrike is always called synchronously from within the striking weapon's
    // own LateSimulate(), a deterministic context, same as every other direct currentState-mutating call
    // in this class.
    public void NotifyStrike(WeaponHitData data)
    {
        _onStrike.Invoke(data);
    }
    public void AddAllyStrikeListener(Action<AllyStrikeData> listener) => _onAllyStrike.AddListener(listener);
    public void RemoveAllyStrikeListener(Action<AllyStrikeData> listener) => _onAllyStrike.RemoveListener(listener);
    public void AddPhantomBladeSummonListener(Action<PhantomBladeSummonData> listener) => _onPhantomBladeSummon.AddListener(listener);
    public void RemovePhantomBladeSummonListener(Action<PhantomBladeSummonData> listener) => _onPhantomBladeSummon.RemoveListener(listener);
    public void NotifyPhantomBladeSummon(PhantomBladeSummonData data) => _onPhantomBladeSummon.Invoke(data);

    // No Add/RemoveListener wrappers — nothing outside this class needs to subscribe, it's purely a
    // despawn-safe relay from "whatever landed the hit" to this identity's own TriggerHitFeedback dispatch.
    public void NotifyHitFeedback(HitFeedbackData data) => _onHitFeedback.Invoke(data);

    private void OnHitFeedback(HitFeedbackData data)
    {
        if (data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy))
            enemy.TriggerHitFeedback(data.damage, data.isCrit, data.isHighroll, data.attacker, data.isPriority, data.overrideSfx, data.sfx);
    }

    // No Add/RemoveListener wrappers — nothing outside this class needs to subscribe, same shape as
    // OnHitFeedback above. Splits into up to two separate calls (Shield/Health) — see PlayerDamageNumberData
    // and DamageNumberManager.SpawnPlayerDamage's own doc comments for the "two separate numbers" rule.
    private void OnPlayerDamageNumber(PlayerDamageNumberData data)
    {
        var dnm = DamageNumberManager.instance;
        if (dnm == null) return;
        if (data.shieldDamage > 0f) dnm.SpawnPlayerDamage(data.shieldDamage, this, DamageNumberManager.PlayerDamagePool.Shield);
        if (data.healthDamage > 0f) dnm.SpawnPlayerDamage(data.healthDamage, this, DamageNumberManager.PlayerDamagePool.Health);
    }

    private void OnStatusPopup(StatusPopupData data) =>
        DamageNumberManager.instance?.SpawnStatusOnPlayer(data.kind, this, data.customMessage, data.colorOverride,
            data.durationMultiplier > 0f ? data.durationMultiplier : 1f);

    // Self-subscribed to _onHeal below (same "no external Add/Remove wrappers needed, this class reacts to
    // its own already-existing event" shape as OnStatusPopup above) — _onHeal only ever fires from Heal()/
    // HealFromAlly(), NEVER the passive per-tick health regen in Simulate() (see that block's own doc
    // comment for why regen deliberately skips it), so this is already exactly "every heal EXCEPT natural
    // regen" for free, no extra filtering needed here.
    //
    // TODO: see DamageNumberManager.SpawnPlayerHeal's own TODO — an ally-sourced heal (HealFromAlly) should
    // always be visible to everyone. HealData carries no "was this HealFromAlly at all" bool, so this relay
    // still can't distinguish an ally heal from a self-heal to pass that along.
    private void OnHealNumber(HealData data) => DamageNumberManager.instance?.SpawnPlayerHeal(data.amount, this, data.isCrit);

    // Self-subscribed to _onInvisibleChanged/_onTauntChanged below (same "no external Add/Remove wrappers
    // needed, this class reacts to its own already-existing change event" shape as OnStatusPopup/
    // OnHitFeedback above) — routes the two flags' own real edges (never a no-op call, see
    // SetTaunting/GrantInvisibility's own change-guards) straight into a status popup, no separate trigger
    // logic needed here.
    private void OnInvisibleChangedStatusPopup(bool visible) =>
        NotifyStatusPopup(visible ? DamageNumberManager.StatusPopupKind.InvisibleStarted : DamageNumberManager.StatusPopupKind.InvisibleEnded);

    // Taunt only pops on the rising edge (starting to taunt) — no "TauntEnded" kind exists yet, so the
    // falling edge is a deliberate no-op here rather than reusing/misusing another kind for it.
    private void OnTauntChangedStatusPopup(bool taunting)
    {
        if (taunting) NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Taunt);
    }

    // Self-subscribed to _onDeathPrevented below — same shape as OnTauntChangedStatusPopup/
    // OnInvisibleChangedStatusPopup above. _onDeathPrevented already only fires on an actual save landing
    // (see TakeDamage's own PreventLethalDamage/hasDeathPrevention resolution), never speculatively.
    private void OnDeathPreventedStatusPopup() => NotifyStatusPopup(DamageNumberManager.StatusPopupKind.DeathPrevented);

    // Prints an ad-hoc, one-off message on THIS player (kind defaults to Custom — DamageNumberManager just
    // passes it straight through instead of resolving one of its own known defaults). The general escape
    // hatch for a message that doesn't need a repeatable DamageNumberManager-owned default — see the
    // kind-based overload below for a known kind (Berserk, Death, ...) whose message/color
    // DamageNumberManager already owns and resolves itself.
    public void NotifyStatusPopup(string customMessage, Color? colorOverride = null, float durationMultiplier = 1f) =>
        NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Custom, customMessage, colorOverride, durationMultiplier);

    // The generic "print a status popup on THIS player" hook — call this whenever some notable, at-a-glance
    // state change happens to a player that's worth calling out visually (e.g. StartBerserkSurge's own
    // Berserk call below). Visible to every client watching this player, not just its own owner — unlike a
    // purely local screen effect (RogueVanishAbilityPD's own invisible-tint, for comparison), a status popup
    // is world-space feedback everyone in the party should see. customMessage/colorOverride are only
    // consulted by DamageNumberManager for Custom (or as an explicit override on top of a known kind's own
    // default, if ever needed) — omit them for a known kind and DamageNumberManager supplies both itself.
    public void NotifyStatusPopup(DamageNumberManager.StatusPopupKind kind, string customMessage = null, Color? colorOverride = null, float durationMultiplier = 1f) =>
        _onStatusPopup.Invoke(new StatusPopupData { kind = kind, customMessage = customMessage, colorOverride = colorOverride, durationMultiplier = durationMultiplier });

    // ── Lifecycle ──────────────────────────────────────────────────────────
    protected override void LateAwake()
    {
        _onHeal          = new PredictedEvent<HealData>(predictionManager, this);
        _onHeal.AddListener(OnHealNumber);
        _onStrikeHighroll = new PredictedEvent<StrikeHighrollData>(predictionManager, this);
        _onEnergySpent = new PredictedEvent<EnergySpentData>(predictionManager, this);
        _onAllyHealGranted = new PredictedEvent<AllyHealGrantedData>(predictionManager, this);
        _onSurgeStarted  = new PredictedEvent(predictionManager, this);
        _onSurgeEnded    = new PredictedEvent(predictionManager, this);
        _onShieldBreak   = new PredictedEvent(predictionManager, this);
        _onShieldRecharge = new PredictedEvent(predictionManager, this);
        _onLethalDamageTaken = new PredictedEvent(predictionManager, this);
        _onDeathPrevented = new PredictedEvent(predictionManager, this);
        _onDeathPrevented.AddListener(OnDeathPreventedStatusPopup);
        _onDamaged       = new PredictedEvent<DamageTakenData>(predictionManager, this);
        _onCritStrike    = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onHit           = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onStrike        = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onAllyStrike    = new PredictedEvent<AllyStrikeData>(predictionManager, this);
        _onPhantomBladeSummon = new PredictedEvent<PhantomBladeSummonData>(predictionManager, this);
        _onHitFeedback   = new PredictedEvent<HitFeedbackData>(predictionManager, this);
        _onHitFeedback.AddListener(OnHitFeedback);
        _onStatusPopup   = new PredictedEvent<StatusPopupData>(predictionManager, this);
        _onStatusPopup.AddListener(OnStatusPopup);
        _onPlayerDamageNumber = new PredictedEvent<PlayerDamageNumberData>(predictionManager, this);
        _onPlayerDamageNumber.AddListener(OnPlayerDamageNumber);
        _onTauntChanged     = new PredictedEvent<bool>(predictionManager, this);
        _onTauntChanged.AddListener(OnTauntChangedStatusPopup);
        _onInvisibleChanged = new PredictedEvent<bool>(predictionManager, this);
        _onInvisibleChanged.AddListener(OnInvisibleChangedStatusPopup);
        _onAbilityStart     = new PredictedEvent<IAbilityPD>(predictionManager, this);
        _onAbilityEnd       = new PredictedEvent<IAbilityPD>(predictionManager, this);
        _onSuccessfulInterrupt      = new PredictedEvent(predictionManager, this);
        _onFirstSuccessfulInterrupt = new PredictedEvent(predictionManager, this);
        _onDodge      = new PredictedEvent(predictionManager, this);
        _onDodge.AddListener(OnDodgeStatusPopup);
        _onFirstDodge = new PredictedEvent(predictionManager, this);
        _onColorChanged     = new PredictedEvent<int>(predictionManager, this);
        _onColorChanged.AddListener(ApplyColor);

        // bonuses applied by PlayerDataPD.Simulate after spawn
        GetComponent<WeaponManagerPD>()?.Init(this);
        // TODO: GetComponent<RogueClassPassive>()?.Init(this);

        UIHealth = baseMaxHealth;
        UIEnergy = baseMaxEnergy;

        if (isOwner && LocalUIManagerPD.instance != null)
            LocalUIManagerPD.instance.SetPlayer(this);
    }

    private const float SurgeTickInterval    = 0.1f;
    private const float SurgeMinDuration     = 0.5f;
    // Extremely short grace window granted the instant a Berserk Surge (forcedSurge) naturally ends (see
    // the surge-drain block below) — routed through the general invincible/invincibleTimer channel so
    // IsInvincible/UIIsInvincible/the invincibility shell all read continuously true across the handoff,
    // no flicker-off gap between forcedSurge dropping and this picking up.
    private const float BerserkSurgeEndGrace = 0.05f;
    private const float HealthRegenInterval  = 1f;

    // Cached every tick (before the pause early-return, so it's always fresh) — GrantInvincibility(),
    // StartSurge(), and TakeDamage() all set tick-based countdown fields from seconds-based durations, but
    // are called from OTHER identities' Simulate()-context (dash input, weapon Hit(), contact damage),
    // not this one's own, so they don't receive this tick's delta as a parameter directly. Safe to read:
    // delta is a constant tick duration now that nothing manipulates Time.timeScale, so it's identical
    // regardless of exactly when within a tick it's read.
    private float _currentDelta;

    // Resolved lazily here (not LateAwake) rather than GetComponent<PlayerControllerPD>() up front — sibling
    // PredictedIdentity components on the same prefab have no guaranteed relative Awake/LateAwake order, so
    // PlayerControllerPD's own DashMovementStarted/Ended PredictedEvent objects aren't guaranteed constructed
    // yet by the time THIS identity's own LateAwake() would run. Retried every tick until it succeeds (the
    // null check below then short-circuits every tick after); see OnDashMovementStarted/Ended and
    // NotifyDodge's own doc comments for what this subscription drives.
    private PlayerControllerPD _controller;

    protected override void Simulate(ref State state, float delta)
    {
        _currentDelta = delta;

        if (_controller == null)
        {
            _controller = GetComponent<PlayerControllerPD>();
            if (_controller != null)
            {
                _controller.AddDashMovementStartedListener(OnDashMovementStarted);
                _controller.AddDashMovementEndedListener(OnDashMovementEnded);
            }
        }

        // Server-authoritative color assignment — see GameStartData.HostColorAssignments' own doc comment.
        // RESOLUTION (deciding + writing the actual value into state) is server-only and happens exactly
        // once, same as before. But the _onColorChanged.Invoke() call below is UNCONDITIONAL, every tick, on
        // every machine — PredictedEvent<T>.Invoke() (see its own source, Library/PackageCache/
        // dev.purrnet.purrdiction/Runtime/Core/PredictedEvent.cs) only ever actually notifies listeners if
        // Invoke() is CALLED on that machine at all: server always delivers immediately, the owner delivers
        // immediately unless mid-replay, and a third-party observer delivers ONLY once _world.isVerified is
        // true for that tick. Gating the call itself behind isServer (the original version of this block)
        // meant Invoke() was NEVER called on any non-server machine — including the owner's own client for
        // its OWN color — so the listener never fired there at all, no matter how long you waited. That's
        // what caused every character to render as default/white on a client build: not a timing race, a
        // genuinely uncalled event. ApplyColor's own change-guard (see its doc comment) is what keeps this
        // cheap despite firing every tick.
        if (isServer && !_serverColorResolved && owner.HasValue)
        {
            _serverColorResolved = TryResolveServerColor(owner.Value, out int resolvedColor);
            if (_serverColorResolved)
                state.colorIndex = resolvedColor;
        }
        _onColorChanged.Invoke(state.colorIndex);

        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Energy recharges even while dead so the player has energy on revive. Fully suppressed while
        // Surging (intentional, not a bug) — energyRegenWhileSurgingPercent exists as a reusable hook for a
        // future augment to punch a partial-rate hole in that (0 = fully suppressed, the current always-true
        // case since nothing grants it right now — the augment that originally did, EnergyRegenWhileSurgingAugment,
        // has since been removed).
        if (!state.isSurging)
            state.energy = Mathf.Min(state.energy + EnergyRegen * delta, MaxEnergy);
        else if (energyRegenWhileSurgingPercent > 0f)
            state.energy = Mathf.Min(state.energy + EnergyRegen * energyRegenWhileSurgingPercent * delta, MaxEnergy);

        if (state.isDead) return;

        // Weapon orbit — same "don't rotate while dead" gating the individual weapons used to apply
        // themselves before this became a shared value. EffectiveWeaponSpeed instead of WeaponSpeed
        // directly so a special action (useSpecialSpinRate) can override the spin rate — see its own doc
        // comment.
        state.weaponOrbitAngle -= EffectiveWeaponSpeed * delta;

        // Wrapped to 0-360 every tick — keeps CompressedFloat's internal int accumulator from eventually
        // overflowing over a very long session, and is safe regardless of magnitude (including a single
        // tick's rotation exceeding a full 360° lap): OrbitalPosition() only ever reads this through
        // Cos/Sin (exactly periodic every 360°), and WeaponMeleePD/WeaponAxePD's own sub-arc sweep
        // reconstruction (startAngle = currentAngle + angleDelta) is recomputed fresh every tick from
        // whatever this wrapped value currently is, so a wrap boundary crossed mid-tick can't corrupt it —
        // confirmed by SweepMath.ComputeSubArcCount() already explicitly capping its effective sweep
        // coverage at 360° for exactly this "more than a full lap in one tick" case.
        float wrappedAngle = state.weaponOrbitAngle % 360f;
        if (wrappedAngle < 0f) wrappedAngle += 360f;
        state.weaponOrbitAngle = wrappedAngle;

        // Invincibility timer
        if (state.invincible && state.invincibleTimer > 0)
        {
            state.invincibleTimer--;
            if (state.invincibleTimer <= 0)
                state.invincible = false;
        }

        // Committed-action invincibility timer — separate pool, see GrantCommittedInvincibility's own doc
        // comment. No falling-edge event needed; nothing currently reacts to this specific channel expiring.
        if (state.committedInvincibleTimer > 0)
            state.committedInvincibleTimer--;

        // Slow timer — same tick-down shape as the others above. No falling-edge event needed; MoveSpeed
        // reads state.slowTimer > 0 directly (see its own doc comment), so expiry just self-resolves.
        if (state.slowTimer > 0)
            state.slowTimer--;

        // Invisibility timer — same shape as the invincibility timer above, but invisibility also has a
        // change event (_onInvisibleChanged) enemy targeting reacts to, so the natural expiry here has to
        // fire the falling edge itself, same as GrantInvisibility fires the rising edge. Safe to invoke
        // directly from Simulate() — PredictedEvent fires exactly once per real occurrence regardless of
        // resimulation, same reasoning _onShieldRecharge's own Simulate()-driven regen block already relies on.
        if (state.invisible && state.invisibleTimer > 0)
        {
            state.invisibleTimer--;
            if (state.invisibleTimer <= 0)
            {
                state.invisible = false;
                RefreshAnyValidTargetExists();
                _onInvisibleChanged.Invoke(false);
            }
        }

        // Death Prevention grant timer — only ticks while eligible (gainsDeathPreventions) AND not already
        // holding a banked save (see "stop ticking, it only ticks when we DON'T have one" above), AND not
        // paused by a forced Surge (Berserk Surge already grants full invincibility for its whole duration
        // — see IsInvincible's own doc comment — so there's nothing for a Death Prevention to protect
        // against right now anyway). 0 means "dormant, ready to (re-)arm" — same self-arming shape as
        // healthRegenTimer's own sentinel-conversion below, just using 0 as the sentinel instead of -1 since
        // arming always happens right here in Simulate() (delta is always available), never at spawn.
        // Grants and immediately re-parks at 0 the instant the timer bottoms out, so the very next tick
        // after the granted save gets spent (hasDeathPrevention flips back false in TakeDamage) starts a
        // fresh full-length cycle automatically.
        if (state.gainsDeathPreventions && !state.hasDeathPrevention && !state.forcedSurge)
        {
            if (state.deathPreventionTimer <= 0)
                state.deathPreventionTimer = Mathf.Max(1, Mathf.RoundToInt(DeathPreventionTime / delta));
            state.deathPreventionTimer--;
            if (state.deathPreventionTimer <= 0)
                state.hasDeathPrevention = true;
        }

        // One-time sentinel conversion — see GetInitialState()'s own comment. Only ever true on the very
        // first Simulate() call this character's whole life; healthRegenTimer is never negative again
        // after this, since the block below always reassigns it to a valid positive count before ending.
        if (state.healthRegenTimer < 0)
            state.healthRegenTimer = Mathf.Max(1, Mathf.RoundToInt(HealthRegenInterval / delta));

        // Health regen — scales by HealPower same as any other heal, but deliberately skips luck variance,
        // Highroll, and CritHealPower (a passive per-tick trickle isn't a "roll" in the way a deliberate heal
        // is — those are reserved for discrete heal sources) and does NOT go through Heal() or invoke
        // _onHeal: a passive per-second tick firing the same event a deliberate heal does would spam anything
        // listening for "the player was just healed" (e.g. a future effect that scales off heal size) far
        // more often than intended, and for a source that isn't really a discrete "heal" in the way regen's
        // own per-tick amount usually reads as trivially small besides.
        if (state.health < MaxHealth)
        {
            state.healthRegenTimer--;
            if (state.healthRegenTimer <= 0)
            {
                float healed = Mathf.Min(HealthRegen * HealPower, MaxHealth - state.health);
                state.health += healed;
                state.healthRegenTimer = Mathf.Max(1, Mathf.RoundToInt(HealthRegenInterval / delta));
            }
        }

        // Shield regen — paused during a forced Surge (Berserk Surge), per its own design: the shield stays
        // exactly as broken/depleted as it was when the surge started for the whole forced-invincibility
        // window, rather than quietly regenerating underneath it.
        if (state.shield < MaxShield && !state.forcedSurge)
        {
            state.shieldRechargeTimer--;
            if (state.shieldRechargeTimer <= 0)
            {
                bool wasBroken = state.shield <= 0f;
                state.shield = MaxShield;
                // Safe to invoke directly from Simulate() — PredictedEvent fires exactly once per real
                // occurrence regardless of resimulation, same reasoning as _onStrikeHighroll being invoked
                // directly from weapon Hit() methods.
                if (wasBroken && state.shield > 0f)
                    _onShieldRecharge.Invoke();
            }
        }

        // Surge energy drain (can't be surging while dead, so this block is alive-only)
        if (state.isSurging)
        {
            if (state.surgeMinDurationTimer > 0)
                state.surgeMinDurationTimer--;

            state.surgeTickTimer--;
            if (state.surgeTickTimer <= 0)
            {
                state.surgeTickTimer = Mathf.Max(1, Mathf.RoundToInt(SurgeTickInterval / delta));
                state.energy -= SurgeCost;
                // Safe to invoke directly from Simulate() — see _onShieldRecharge's own identical call above
                // for the reasoning.
                _onEnergySpent.Invoke(new EnergySpentData { amount = SurgeCost });
                if (state.energy <= 0f)
                {
                    state.energy = 0f;
                    state.isSurging = false;
                    // Cleared unconditionally — a no-op false=false assignment for a normal, non-forced
                    // Surge. This is the NATURAL end (energy drained to 0); a Berserk Surge can also now end
                    // early via a hard interrupt (PlayerControllerPD.Interrupt() -> ForceEndSurge(), see its
                    // own doc comment for the identical forcedSurge/grace-period cleanup it mirrors) —
                    // forcedSurge itself still blocks the ordinary TryEndSurge() path regardless.
                    bool wasForcedSurge = state.forcedSurge;
                    state.forcedSurge = false;
                    // BerserkSurgeEndGrace hand-off — see its own doc comment. Only for a genuinely forced
                    // (Berserk) Surge ending, not a normal player-ended one, which was never invincible to
                    // begin with. This runs AFTER the invincibleTimer countdown block earlier in this same
                    // method already ticked for this tick, so the freshly granted timer starts counting down
                    // cleanly next tick, not double-processed this one.
                    if (wasForcedSurge)
                    {
                        state.invincible = true;
                        state.invincibleTimer = Mathf.Max(state.invincibleTimer, Mathf.RoundToInt(BerserkSurgeEndGrace / delta));
                    }
                    _onSurgeEnded.Invoke();
                }
            }
        }
    }

    private uint _lastDamageEventId;

    protected override void UpdateView(State viewState, State? verified)
    {
        // Health/shield use verified state, not viewState: viewState is the predicted/interpolated
        // value, which visibly snaps every time a rollback correction lands. Verified state only
        // moves forward once confirmed, so displaying it skips the correction jitter entirely.
        if (verified.HasValue)
        {
            UIHealth              = verified.Value.health;
            UIShield              = verified.Value.shield;
            // Converted back to seconds — UIShieldRechargeTimer is a UI-facing float consumed by
            // LocalUIManagerPD's fill-ratio math alongside shieldRechargeTime (also seconds), so it needs
            // to stay in seconds even though the underlying predicted field is ticks now.
            UIShieldRechargeTimer = verified.Value.shieldRechargeTimer * _currentDelta;
            // Same tick-to-seconds conversion, same verified-only gating — deathPreventionTimer is just as
            // subject to prediction-rollback correction as shieldRechargeTimer, so this avoids the same
            // visible-jump problem the comment above already covers.
            UIDeathPreventionTimer = verified.Value.deathPreventionTimer * _currentDelta;
        }
        UIEnergy              = viewState.energy;
        UIIsSurging           = viewState.isSurging;
        // See IsInvincible's own doc comment for why forcedSurge/committedInvincibleTimer are ORed in too.
        UIIsInvincible        = viewState.invincible || viewState.forcedSurge || viewState.committedInvincibleTimer > 0;

        if (isOwner && viewState.damageEventId != _lastDamageEventId)
        {
            _lastDamageEventId = viewState.damageEventId;
            if (SFXManager.instance != null) SFXManager.instance.Play(_damagedSfx, transform.position);
        }

        // Any invincibility source shows this shell, no exceptions — reuses UIIsInvincible (computed just
        // above) rather than duplicating its OR here, so this can never silently drift out of sync with
        // IsInvincible/TakeDamage's own guard/the health bar's own white-tint (LocalUIManagerPD/
        // PlayerUIWorldSpace) if a future invincibility channel is ever added.
        if (_invincibilityShell != null)
            _invincibilityShell.SetActive(UIIsInvincible);

        // Driven generically off the invisible flag itself, not any specific ability — any current or
        // future invisibility source gets this same visual fade for free. Guarded on an actual change so
        // this doesn't touch the renderer every single frame. A real material INSTANCE, not a
        // MaterialPropertyBlock — every player's model renderer is wired to the same shared Material asset
        // (inherited from the BaseCharacter prefab), and a property-block override on a shared material
        // bled across every player using it (Rogue's Vanish was making all 4 players go invisible at once
        // in multiplayer). `.material` auto-clones a unique instance for this renderer the first time it's
        // read, so writes here only ever affect this one player. The base material is already Transparent
        // surface type (see PlayerColorApplicator-adjacent art setup) so alpha actually has a visual effect.
        // RGB comes from PlayerColor, not a hardcoded white — PlayerColorApplicator tints this same
        // renderer's material directly, so overriding _BaseColor here must keep that tint's RGB and only
        // change alpha, or invisibility would blank the model back to white.
        if (_modelRenderer != null && viewState.invisible != _lastAppliedInvisible)
        {
            _lastAppliedInvisible = viewState.invisible;
            if (_modelMaterialInstance == null) _modelMaterialInstance = _modelRenderer.material;
            Color c = PlayerColor;
            c.a = viewState.invisible ? invisibleModelAlpha : 1f;
            _modelMaterialInstance.SetColor(BaseColorId, c);
        }

        // Continuous idle spin, always on — see _modelTransform's own doc comment for why the sideways tip
        // (tip * spin, not spin * tip) turns this same Y-axis spin into a tumble once tipped, instead of
        // needing separate spin logic for the burst case.
        if (_modelTransform != null)
        {
            Quaternion spin   = Quaternion.Euler(0f, -viewState.weaponOrbitAngle, 0f);
            Quaternion tip    = modelSidewaysTipped ? ModelSidewaysTipRotation : Quaternion.identity;
            // Outermost — the body faces OrbitFacingYaw (e.g. the burst's travel direction) while the
            // tip/spin tumble happens relative to that facing, not a fixed world orientation.
            Quaternion facing = Quaternion.Euler(0f, OrbitFacingYaw, 0f);
            _modelTransform.localRotation = facing * tip * spin;
        }
    }

    private bool _lastAppliedInvisible;
    // Lazily cached the first time it's read — see the invisibility-fade block above for why this needs to
    // be a real per-renderer instance rather than a MaterialPropertyBlock.
    private Material _modelMaterialInstance;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    protected override void Destroyed()
    {
        if (owner.HasValue)
        {
            All.Remove(owner.Value);
            RefreshAnyValidTargetExists();
        }
    }

    // ── Init ──────────────────────────────────────────────────────────────
    public struct ProgressionBonuses
    {
        public float strengthFlat, moveSpeedFlat, attackSpeedPct, energyRegenFlat;
        public float dashHaste, critChanceFlat, xpRadiusFlat, weaponRangeFlat, maxEnergyFlat;
        public float abilityHaste;
        public int   dashChargesBonus, levelUpOptionsBonus;
        public float revivePower, reviveTimePenaltyMult, xpMultiplier;
        public float maxHealthFlat, healthRegenFlat, strengthTotalPercent;
        public float luckFlat, strengthPerLevel, maxShieldFlat, shieldRechargeTime;
        // StatType.MoveSpeedPercent shop stat — Bonus tier (see bonusMoveSpeedPercent's own doc comment for
        // why meta-progression writes Bonus here, same as every other flat progression stat, not Total).
        public float moveSpeedBonusPercent;
        // Not an amount to add — a one-time unlock flag (StatType.DeathPrevention is a single-tier shop
        // item, GetBaseStatBonus reads > 0 once bought). See ApplyProgressionBonuses' own handling below.
        public bool  deathPreventionUnlocked;
        // Scaffolding for the unlockable-augment-sets system — see UnlockedAugmentTagMask's own doc comment.
        public long  unlockedAugmentTagMask;
    }

    public static ProgressionBonuses ComputeProgressionBonuses() => new()
    {
        strengthFlat            = ProgressionManager.GetBaseStatBonus(StatType.Strength),
        moveSpeedFlat           = ProgressionManager.GetBaseStatBonus(StatType.MoveSpeed),
        moveSpeedBonusPercent   = ProgressionManager.GetBaseStatBonus(StatType.MoveSpeedPercent),
        attackSpeedPct          = ProgressionManager.GetBaseStatBonus(StatType.AttackSpeed) * 0.01f,
        energyRegenFlat         = ProgressionManager.GetBaseStatBonus(StatType.EnergyRegen),
        dashHaste               = ProgressionManager.GetBaseStatBonus(StatType.DashHaste),
        abilityHaste            = ProgressionManager.GetBaseStatBonus(StatType.AbilityHaste),
        critChanceFlat          = ProgressionManager.GetBaseStatBonus(StatType.CritChance) * 0.01f,
        xpRadiusFlat            = ProgressionManager.GetBaseStatBonus(StatType.CollectionRadius),
        weaponRangeFlat         = ProgressionManager.GetBaseStatBonus(StatType.WeaponRange),
        maxEnergyFlat           = ProgressionManager.GetBaseStatBonus(StatType.MaxEnergy),
        dashChargesBonus        = (int)ProgressionManager.GetBaseStatBonus(StatType.DashCharges),
        levelUpOptionsBonus     = (int)ProgressionManager.GetBaseStatBonus(StatType.LevelUpOptions),
        revivePower             = ProgressionManager.GetBaseStatBonus(StatType.RevivePower),
        reviveTimePenaltyMult   = Mathf.Pow(0.75f, ProgressionManager.GetBaseStatTier(StatType.ReviveTimePenalty)),
        xpMultiplier            = ProgressionManager.GetBaseStatBonus(StatType.XPMultiplier) * 0.01f,
        maxHealthFlat           = ProgressionManager.GetBaseStatBonus(StatType.MaxHealth),
        healthRegenFlat         = ProgressionManager.GetBaseStatBonus(StatType.HealthRegen),
        strengthTotalPercent    = ProgressionManager.GetBaseStatBonus(StatType.TotalStrengthPercent),
        luckFlat                = ProgressionManager.GetBaseStatBonus(StatType.Luck),
        strengthPerLevel        = ProgressionManager.GetBaseStatBonus(StatType.StrengthPerLevel),
        maxShieldFlat           = ProgressionManager.GetBaseStatBonus(StatType.MaxShield),
        shieldRechargeTime      = ProgressionManager.GetBaseStatBonus(StatType.ShieldRechargeTime),
        deathPreventionUnlocked = ProgressionManager.GetBaseStatBonus(StatType.DeathPrevention) > 0f,
        unlockedAugmentTagMask  = ComputeUnlockedAugmentTagMask(),
    };

    // Scaffolding for the unlockable-augment-sets system — see UnlockedAugmentTagMask's own doc comment.
    // AugmentManagerPD.instance may not exist yet (isolated test scene, or this running before that
    // singleton's own Awake()) — falls back to 0 (no tags resolved) rather than throwing, same "missing
    // optional cross-scene singleton is a harmless no-op" precedent EnemySpawnerPD's own TierAccentMaterial
    // null-check already establishes. Likewise a tag string with no matching database entry (unregistered,
    // or database not wired up yet) just contributes no bit rather than erroring.
    private static long ComputeUnlockedAugmentTagMask()
    {
        var db = AugmentManagerPD.instance != null ? AugmentManagerPD.instance.UnlockTagDatabase : null;
        if (db == null) return 0L;

        // Lazily grants anything this player now qualifies for (e.g. a Threat Level just crossed, or already
        // crossed in a past session before this trigger system existed) before resolving the mask below —
        // see GrantQualifyingUnlocks' own doc comment for why this single call site is sufficient.
        db.GrantQualifyingUnlocks();

        long mask = 0L;
        foreach (var tag in ProgressionManager.UnlockedAugmentTags)
        {
            int bit = db.GetTagBit(tag);
            if (bit >= 0 && bit < 64) mask |= 1L << bit;
        }
        return mask;
    }

    // Wraps any stat mutation that might raise MaxHealth/MaxEnergy, and carries current resources up
    // by the same delta so the ceiling never rises while current stays pinned below it.
    // Shield is deliberately excluded from that same delta-catch-up treatment — it doesn't gradually regen
    // like health/energy, it snaps to full after a no-damage timer, so a max-shield INCREASE should only
    // raise that ceiling and let the existing regen naturally catch current shield up to it. A DECREASE is
    // different though: since MaxShield is itself derived from MaxHealth (see its own comment), any effect
    // that lowers MaxHealth (or directly lowers a Shield stat) can leave current shield sitting above the
    // new, lower ceiling — clamped down below, same "pull current down to the new max, never below it" intent
    // the health delta below now uses too.
    // Every future stat-granting effect (augments, etc.) should route through this rather than
    // mutating bonus fields directly, so refill-on-max-increase stays a single, shared behavior.
    private void ApplyStatChange(Action mutate)
    {
        float maxHealthBefore = MaxHealth;
        float maxEnergyBefore = MaxEnergy;

        mutate();

        // A ceiling INCREASE carries current up by the same delta (a free "heal"/refill to match the new
        // max). A ceiling DECREASE never reduces current on its own — it only pulls current down if it's
        // now sitting ABOVE the new (lower) ceiling (Mathf.Min just leaves it untouched otherwise), clamping
        // to exactly that ceiling rather than subtracting the raw delta — a temporary Max Health buff ending
        // (e.g. KnightBigBuffAbilityPD) must never punish a player who took damage mid-buff and was already
        // below the new max before the ceiling dropped back down.
        // Floored rather than left to go negative — health floors at 1 so a max decrease alone can never be
        // a kill; energy can bottom out at 0.
        float maxHealthDelta = MaxHealth - maxHealthBefore;
        currentState.health = maxHealthDelta > 0f
            ? Mathf.Max(1f, currentState.health + maxHealthDelta)
            : Mathf.Max(1f, Mathf.Min(currentState.health, MaxHealth));

        float maxEnergyDelta = MaxEnergy - maxEnergyBefore;
        currentState.energy = maxEnergyDelta > 0f
            ? Mathf.Max(0f, currentState.energy + maxEnergyDelta)
            : Mathf.Max(0f, Mathf.Min(currentState.energy, MaxEnergy));

        if (currentState.shield > MaxShield) currentState.shield = MaxShield;
    }

    // Public entry point for augment effect components (e.g. HealthAugment) to grant a flat max health
    // bonus — routes through ApplyStatChange so current health rises with the new ceiling instead of
    // staying pinned below it. Augment effects apply identically on every machine (unlike the old
    // owner-only model), so this needs no isOwner gating here.
    public void AddMaxHealthFlat(float amount) => ApplyStatChange(() => currentState.bonusMaxHealthFlat += amount);
    // Conversion-source bookkeeping, not a real stat grant — see ConvertedHealth's own doc comment. A
    // conversion augment that grants Health should call this alongside AddMaxHealthFlat, with the identical
    // delta both times. Plain +=, not ApplyStatChange — doesn't touch MaxHealth/MaxEnergy itself, only the
    // separate tracker other conversion augments read, so the before/after clamp recompute would be pure
    // overhead (same reasoning as AddPhantomBladeDamageBonusPercent).
    public void AddConvertedHealth(float amount) => currentState.convertedHealth += amount;

    // Same pattern as AddMaxHealthFlat, for augments that grant a percentage bonus instead (additive
    // against the 1f baseline, e.g. +0.2 = +20% total). Bonus tier — now only scales bonusMaxHealthFlat
    // (see MaxHealth), not the whole stat; kept for ClassPassiveData/meta-progression and any future
    // "outlier" augment, but HealthAugment itself now calls AddMaxHealthTotalPercent below instead.
    public void AddMaxHealthPercent(float amount) => ApplyStatChange(() => currentState.bonusMaxHealthPercent += amount);
    // NEW Total tier — scales the whole (base + bonus) MaxHealth sum. What HealthAugment (and TitanAugment,
    // redirected alongside it since both grant MaxHealth) actually calls now.
    public void AddMaxHealthTotalPercent(float amount) => ApplyStatChange(() => currentState.totalMaxHealthPercent += amount);
    public void AddWeaponRangeFlat(float amount) => ApplyStatChange(() => currentState.bonusWeaponRangeFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddWeaponRangePercent(float amount) => ApplyStatChange(() => currentState.bonusWeaponRangePercent += amount);
    // NEW Total tier — what RangeAugment/OuterTrackAugment/TitanAugment all call now (every existing
    // WeaponRange-percent-granting augment, redirected together since they all target the same stat).
    public void AddWeaponRangeTotalPercent(float amount) => ApplyStatChange(() => currentState.totalWeaponRangePercent += amount);
    public void AddAttackSpeedTotalPercent(float amount) => ApplyStatChange(() => currentState.attackSpeedTotalPercent += amount);
    // attackSpeedFlat (renamed from attackSpeedPercent) is added directly in the same units as
    // baseAttackSpeed, not a raw fraction — matches the progression system's own conversion
    // (entry.value * 0.01f * baseAttackSpeed) so a caller here can pass an intuitive fraction (e.g. 0.25 for
    // +25%) same as every other percent Add, even though what it grants under the hood is flat.
    public void AddAttackSpeedFlat(float amount) => ApplyStatChange(() => currentState.attackSpeedFlat += amount * baseAttackSpeed);
    public void AddMoveSpeedFlat(float amount) => ApplyStatChange(() => currentState.bonusMoveSpeedFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddMoveSpeedPercent(float amount) => ApplyStatChange(() => currentState.bonusMoveSpeedPercent += amount);
    // NEW Total tier — what MoveSpeedAugment/GremlinAugment/ShieldBreakFrenzyAugment all call now (every
    // remaining MoveSpeed-percent-granting augment). SurgeEndMoveSpeedAugment used to as well, before it
    // switched to a flat AddMoveSpeedFlat grant instead (see its own doc comment).
    public void AddMoveSpeedTotalPercent(float amount) => ApplyStatChange(() => currentState.totalMoveSpeedPercent += amount);
    // Direct override, not additive — surgeMoveSpeedPenalty (default 0.5f) is the actual reduction
    // multiplier itself (SurgeMoveSpeedMulti = surgeMoveSpeedMulti * surgeMoveSpeedPenalty * ...), not a
    // bonus-stacking pool, so "half the penalty"/"no penalty" augments each set it to their own fixed
    // absolute target (0.75f/1f) rather than accumulating a delta off the current value. This also makes
    // picking both of a mutually-exclusive pair harmless (whichever applies last simply wins) rather than
    // compounding into a nonsensical value.
    public void SetSurgeMoveSpeedPenalty(float value) => ApplyStatChange(() => currentState.surgeMoveSpeedPenalty = value);
    // "Additive against the 1f baseline" pattern, same as AddCritChancePercent — stackPowerPercent starts at
    // 1f (StackPower = stackPowerPercent * stackPowerPenalty), just had no augment-facing wrapper until now.
    public void AddStackPowerPercent(float amount) => ApplyStatChange(() => currentState.stackPowerPercent += amount);
    // Same "additive against the 1f baseline" convention as AddStackPowerPercent — plain +=, not
    // ApplyStatChange, since HealPower/CritHealPower don't affect Max Health/Energy (no before/after clamp
    // recompute needed, same reasoning as AddAreaEffectSizePercent).
    public void AddHealPowerPercent(float amount) => currentState.healPowerMultiplier += amount;
    public void MultiplyHealPowerPenalty(float factor) => currentState.healPowerPenalty *= factor;
    public void AddCritHealPowerPercent(float amount) => currentState.critHealPowerMultiplier += amount;
    public void MultiplyCritHealPowerPenalty(float factor) => currentState.critHealPowerPenalty *= factor;
    // Plain +=, not ApplyStatChange — same reasoning as AddPhantomBladeDamageBonusPercent: doesn't affect
    // Max Health/Energy, so the before/after clamp recompute ApplyStatChange does would be pure overhead.
    public void AddAreaEffectSizePercent(float amount) => currentState.areaEffectSizePercent += amount;
    public void AddAreaEffectPowerPercent(float amount) => currentState.areaEffectPowerPercent += amount;
    // Additive-into-1f, same best-guess convention ClassPassiveData's own doc comment uses for every other
    // Penalty field (none have an established combination rule yet — this is consistent with that, not a
    // confirmed pattern).
    public void AddAreaEffectSizePenalty(float amount) => currentState.areaEffectSizePenalty += amount;
    public void AddAreaEffectPowerPenalty(float amount) => currentState.areaEffectPowerPenalty += amount;
    public void AddAreaEffectCount(int amount) => currentState.areaEffectCountFlat += amount;
    // Multiply/Divide pair, not additive — matches bonusWeaponCountMultiplier's own int-multiplier shape
    // (WeaponCount => (base + flat) * multiplier), the closest existing precedent for a discrete-count
    // penalty rather than a continuous float one.
    public void MultiplyAreaEffectCountPenalty(int factor) => currentState.areaEffectCountPenalty *= factor;
    public void DivideAreaEffectCountPenalty(int factor) => currentState.areaEffectCountPenalty /= factor;
    // Plain +=, not ApplyStatChange — same reasoning as AddAreaEffectSizePercent: doesn't affect Max
    // Health/Energy, so the before/after clamp recompute ApplyStatChange does would be pure overhead.
    public void AddOuterXZMultiplierPercent(float amount) => currentState.outerXZMultiplierPercent += amount;
    public void AddHealthRegenFlat(float amount) => ApplyStatChange(() => currentState.bonusHealthRegenFlat += amount);
    // Conversion-source bookkeeping, not a real stat grant — see ConvertedHealthRegen's own doc comment. A
    // conversion augment that grants HealthRegen should call this alongside AddHealthRegenFlat, with the
    // identical delta both times. Plain +=, same "doesn't touch Max Health/Energy" reasoning as
    // AddConvertedHealth above.
    public void AddConvertedHealthRegen(float amount) => currentState.convertedHealthRegenFlat += amount;
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddHealthRegenPercent(float amount) => ApplyStatChange(() => currentState.bonusHealthRegenPercent += amount);
    // NEW Total tier — what HealthRegenAugment calls now.
    public void AddHealthRegenTotalPercent(float amount) => ApplyStatChange(() => currentState.totalHealthRegenPercent += amount);
    // No longer "flat" despite the name (2026-08-23 — bonusEnergyRegenFlat is now a fraction of MaxEnergy,
    // see baseEnergyRegen's own doc comment) — kept for ClassPassiveData/shop symmetry with the other Add*Flat
    // wrappers, no current caller.
    public void AddEnergyRegenFlat(float amount) => ApplyStatChange(() => currentState.bonusEnergyRegenFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddEnergyRegenPercent(float amount) => ApplyStatChange(() => currentState.bonusEnergyRegenPercent += amount);
    // NEW Total tier — what EnergyRegenAugment calls now.
    public void AddEnergyRegenTotalPercent(float amount) => ApplyStatChange(() => currentState.totalEnergyRegenPercent += amount);
    public void AddLuckFlat(float amount) => ApplyStatChange(() => currentState.bonusLuckFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddLuckPercent(float amount) => ApplyStatChange(() => currentState.bonusLuckPercent += amount);
    // NEW Total tier — what LuckAugment calls now.
    public void AddLuckTotalPercent(float amount) => ApplyStatChange(() => currentState.totalLuckPercent += amount);
    // Directly additive against the LuckCeiling number itself (base 1.5 + 0.25 = 1.75), same
    // (base + Flat) * Percent * Penalty shape as Strength — NOT AddLuckCeilingPercent below, which
    // instead scales bonusLuckCeilingPercent's own 1f-baseline multiplier and would compound against
    // baseLuckCeiling (1.5 * 1.25 = 1.875) rather than adding to it directly.
    public void AddLuckCeilingFlat(float amount) => ApplyStatChange(() => currentState.bonusLuckCeilingFlat += amount);
    // "Additive against the 1f baseline" pattern, same as AddCritChancePercent — bonusLuckCeilingPercent
    // starts at 1f (LuckCeiling = (baseLuckCeiling + bonusLuckCeilingFlat) * bonusLuckCeilingPercent *
    // luckCeilingPenalty), just had no augment-facing wrapper until now.
    public void AddLuckCeilingPercent(float amount) => ApplyStatChange(() => currentState.bonusLuckCeilingPercent += amount);
    // Inverted, same reasoning as AddDamageReductionPercent: Highroll = baseHighroll * bonusHighrollPercent
    // * highrollPenalty, and a LOWER Highroll threshold means MORE of a roll's window counts as a highroll
    // (see WeaponDamageMath.RollDamage's own doc comment) — so a lower highrollPenalty is better for the
    // player here. The parameter keeps this class's usual "positive = better for the player" convention
    // (e.g. 0.05 for a 5% easier highroll threshold); it's SUBTRACTED under the hood.
    public void AddHighrollPenalty(float amount) => ApplyStatChange(() => currentState.highrollPenalty -= amount);
    public void AddCritChanceFlat(float amount) => ApplyStatChange(() => currentState.bonusCritChanceFlat += amount);
    // Same "additive against the 1f baseline" pattern as AddMaxHealthPercent, for CritChance's own percent
    // multiplier. Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment
    // calls this anymore (CritChanceAugment, née CritAugment, now calls AddCritChanceTotalPercent below).
    public void AddCritChancePercent(float amount) => ApplyStatChange(() => currentState.bonusCritChancePercent += amount);
    // NEW Total tier — what CritChanceAugment calls now.
    public void AddCritChanceTotalPercent(float amount) => ApplyStatChange(() => currentState.totalCritChancePercent += amount);
    // Flat addition to CritDamage's own baseCritDamage term (CritDamage = (baseCritDamage +
    // bonusCritDamageFlat) * bonusCritDamagePercent + ...), e.g. +0.1 for +10% crit damage.
    public void AddCritDamageFlat(float amount) => ApplyStatChange(() => currentState.bonusCritDamageFlat += amount);
    // Inverted from every other "...Percent" wrapper above: damageReductionPercent starts at 1f and
    // multiplies DIRECTLY into incoming damage (TakeDamage's own `damage *= DamageReduction`), so a LOWER
    // value is better for the player here. The parameter still follows this class's usual "positive =
    // better for the player" convention (e.g. 0.4 for -40% damage taken).
    //
    // Multiplicative, NOT additive — multiplies damageReductionPercent by (1 - amount), so every
    // simultaneously-active source compounds against the REMAINING damage fraction (two 40% reductions:
    // (1-0.4)*(1-0.4) = 0.36 remaining = 64% total reduction) rather than being subtracted from a shared 1f
    // baseline. The old subtractive model let enough stacked sources drive damageReductionPercent to 0 or
    // negative — 100%+ reduction, i.e. taking damage would have HEALED the player — which multiplicative
    // stacking makes structurally impossible (a product of positive fractions can't cross 0). DamageReduction
    // itself still floor-clamps to 0f as a final defensive backstop regardless (see its own doc comment).
    //
    // Unlike the old model, passing a negative amount here does NOT cleanly revert a previous grant anymore
    // (multiplying by (1 - -x) = (1 + x) is not the inverse of multiplying by (1 - x)) — use
    // RemoveDamageReductionPercent(amount) instead, with the SAME amount originally granted.
    public void AddDamageReductionPercent(float amount) => ApplyStatChange(() => currentState.damageReductionPercent *= (1f - amount));

    // Reverts a specific earlier AddDamageReductionPercent(amount) grant — divides back out the exact factor
    // that grant multiplied in. amount must match the value originally passed to AddDamageReductionPercent;
    // order relative to other simultaneously-active sources doesn't matter (multiplication/division are
    // commutative), only that this exact amount is the one being undone.
    public void RemoveDamageReductionPercent(float amount) => ApplyStatChange(() => currentState.damageReductionPercent /= (1f - amount));
    public void AddDashHaste(float amount) => ApplyStatChange(() => currentState.dashHaste += amount);
    // Same "additive against the 1f baseline" pattern as AddMaxHealthPercent/AddCritChancePercent, for
    // DashHaste's own percent multiplier (bonusDashHastePercent starts at 1f, e.g. +0.15 = +15% total).
    // No other writer ever touched this field (see its own declaration comment) — DashHasteAugment now
    // calls AddDashHasteTotalPercent below instead; kept here only in case something else wants the
    // (functionally identical, since dashHaste has no separate base) Bonus-tier slot later.
    public void AddDashHastePercent(float amount) => ApplyStatChange(() => currentState.bonusDashHastePercent += amount);
    // NEW Total tier — what DashHasteAugment calls now.
    public void AddDashHasteTotalPercent(float amount) => ApplyStatChange(() => currentState.totalDashHastePercent += amount);
    // Multiplicative diminishing-returns stacking, same "never reach 0" shape/calling convention as
    // AddDamageReductionPercent/AddSurgeCostReductionPercent (see either's own doc comment) — a direct
    // percent cut layered ON TOP of the Haste-derived DashCooldown, not part of dashHaste's own curve, see
    // DashCooldown's own doc comment for why this is a structurally separate tier.
    public void AddDashCooldownReductionPercent(float amount) => ApplyStatChange(() => currentState.dashCooldownReductionPercent *= (1f - amount));
    // Reverts a specific earlier AddDashCooldownReductionPercent(amount) grant — same shape as
    // RemoveDamageReductionPercent/RemoveSurgeCostReductionPercent.
    public void RemoveDashCooldownReductionPercent(float amount) => ApplyStatChange(() => currentState.dashCooldownReductionPercent /= (1f - amount));
    // Separate, freely-multiplying tier for a future "increase Dash Cooldown" augment — same reasoning as
    // MultiplySurgeCostIncrease's own doc comment for why this can't share the reduction tier's own
    // accumulator. No paired Divide/Remove — nothing currently needs to revert a permanent pick's own grant.
    public void MultiplyDashCooldownIncrease(float factor) => ApplyStatChange(() => currentState.dashCooldownIncreaseMultiplier *= factor);
    // Same shape as the three DashHaste wrappers immediately above.
    public void AddAbilityHaste(float amount) => ApplyStatChange(() => currentState.abilityHaste += amount);
    public void AddAbilityHastePercent(float amount) => ApplyStatChange(() => currentState.bonusAbilityHastePercent += amount);
    public void AddAbilityHasteTotalPercent(float amount) => ApplyStatChange(() => currentState.totalAbilityHastePercent += amount);
    // Multiplicative diminishing-returns stacking (2026-08-19, replacing the old flat-additive
    // bonusSurgeCostPercent) — same "never reach 0" reasoning as AddDamageReductionPercent (see its own doc
    // comment): SurgeCost is a pure multiplier chain, and letting several reduction sources sum linearly
    // against a 1f baseline could drive it to 0 or negative (a literal free/energy-gaining Surge exploit)
    // once combined with a large enough reduction. amount is "how much to reduce," same calling convention
    // as AddDamageReductionPercent (e.g. 0.5 = -50%, not -0.5).
    public void AddSurgeCostReductionPercent(float amount) => ApplyStatChange(() => currentState.surgeCostReductionPercent *= (1f - amount));
    // Reverts a specific earlier AddSurgeCostReductionPercent(amount) grant — divides back out the exact
    // factor that grant multiplied in, same shape as RemoveDamageReductionPercent. amount must match the
    // value originally passed to AddSurgeCostReductionPercent.
    public void RemoveSurgeCostReductionPercent(float amount) => ApplyStatChange(() => currentState.surgeCostReductionPercent /= (1f - amount));
    // Separate, linearly-summing tier (2026-08-19, new; switched multiplicative->additive 2026-08-21) for
    // "increase Surge Cost" sources — currently only SurgeCostIncreaseAugment, kept additive regardless (see
    // this tier's own accessor property doc comment for why) so any future second source stacks correctly
    // without needing this reworked again. Deliberately NOT folded into the reduction tier's own
    // diminishing-returns accumulator above: mixing an increase into that same pool would let enough
    // reduction sources eventually cancel the increase back down toward (or arbitrarily close to) the same
    // floor the reduction tier alone already guarantees, instead of the two combining straightforwardly
    // against each other the way an "increase this, reduce that" pair of stats normally should. Additive
    // (not multiplicative) specifically so two "+100%" sources stack to +200% (3x total), not +300% (4x
    // total from 2x*2x) — the whole reason this stopped being a straight multiplier once a second caller
    // showed up. No paired Remove — nothing currently needs to revert a permanent pick's own grant.
    public void AddSurgeCostIncreasePercent(float amount) => ApplyStatChange(() => currentState.surgeCostIncreasePercent += amount);
    // No augment touches this yet (2026-08-19, first writer is SurgeCostIncreaseAugment) — same
    // multiplicative-compounding shape as MultiplyStrengthPenalty/MultiplyOnHitPowerPenalty, plain +=
    // reasoning doesn't apply since this is a *Penalty-style field, not a bonusXPercent one.
    public void MultiplySurgeAttackSpeedPenalty(float factor) => currentState.surgeAttackSpeedPenalty *= factor;
    public void AddWeaponCountFlat(int amount) => ApplyStatChange(() => currentState.bonusWeaponCountFlat += amount);
    // Multiplicative compounding, same convention as MultiplyStrengthPenalty/MultiplyOnHitPowerMultiplier
    // — bonusWeaponCountMultiplier already sits in the WeaponCount formula (previously only ever touched by
    // ClassPassiveData aggregation), just had no augment-facing wrapper until now. factor is an exact
    // integer multiplier (e.g. 2 for SurgeWeaponCountAugment's x2), inverted with the same factor to revert
    // (integer *=/ /= pairs cleanly as long as they're applied/removed in matched pairs).
    public void MultiplyWeaponCountMultiplier(int factor) => ApplyStatChange(() => currentState.weaponCountMultiplier *= factor);
    // Paired revert for the above — an int can't be un-multiplied via 1f/factor the way the float
    // Multiply___ methods revert, so this divides back by the same exact factor instead. Only safe when
    // applied/removed in matched pairs with no other multiplicative change interleaved in between.
    public void DivideWeaponCountMultiplier(int factor) => ApplyStatChange(() => currentState.weaponCountMultiplier /= factor);
    // Unused for now — the planned OuterWeaponTrackAugment will call this. amount can be negative to
    // remove tracks (nothing currently does, but the direct += supports it symmetrically for free).
    public void AddWeaponTracks(int amount) => ApplyStatChange(() => currentState.weaponTracks += amount);
    // Multiplicative, not additive — bonusStrengthPenalty/bonusOnHitPowerPenalty both start at 1f and
    // apply as straight multipliers (Strength/OnHitPower), so stacking two -40% penalties correctly
    // compounds to 0.6 * 0.6 = 0.36 rather than clamping at 1 - 0.8 = 0.2. factor is what the penalty gets
    // multiplied by (e.g. 1f - 0.4f for a -40% penalty), matching the old system's identical
    // `bonusStrengthPenalty *= 1f - damagePenalty` call.
    public void MultiplyStrengthPenalty(float factor) => ApplyStatChange(() => currentState.bonusStrengthPenalty *= factor);
    public void MultiplyOnHitPowerPenalty(float factor) => ApplyStatChange(() => currentState.bonusOnHitPowerPenalty *= factor);
    // "Additive against the 1f baseline" pattern, same as AddCritChancePercent — bonusStrengthPercent
    // already sits in the Strength formula. Bonus tier — now only scales bonusStrengthFlat (see
    // Strength), not the whole stat; kept for ClassPassiveData/meta-progression.
    public void AddStrengthPercent(float amount) => ApplyStatChange(() => currentState.bonusStrengthPercent += amount);
    // Total tier — e.g. KnightBigBuffAbilityPD's own temporary self-buff. Was ALSO used by
    // BladeweaviestBladeweaverAugment/MultiShotAugment/OuterTrackAugment/SurgeWeaponCountAugment/
    // WeaponCountAugment/BladeweaverStartingAugment as a "extra weapon copies, compensate with a damage
    // bonus" grant — removed from all of them (2026-08-19, see CharacterStatsPD.WeaponStrength's own doc
    // comment) since that compensation only needed to offset WeaponStrength's own divide, but granting it
    // here silently over-buffed every non-weapon-strike consumer of the general Strength stat too.
    public void AddStrengthTotalPercent(float amount) => ApplyStatChange(() => currentState.totalStrengthPercent += amount);
    // Multiplicative compounding, same convention as MultiplyStrengthPenalty/MultiplyOnHitPowerPenalty
    // — bonusOnHitPowerMultiplier already sits in the OnHitPower formula (previously only ever touched by
    // ClassPassiveData aggregation), just had no augment-facing wrapper until now. Same
    // penalty-to-bonus swap as AddStrengthPercent above, for the same reason.
    public void MultiplyOnHitPowerMultiplier(float factor) => ApplyStatChange(() => currentState.bonusOnHitPowerMultiplier *= factor);
    public void AddRicochetFlat(float amount) => ApplyStatChange(() => currentState.bonusRicochetFlat += amount);
    // NEW Total tier — no augment grants this yet (RicochetAugment only ever grants Flat), reserved for a
    // future in-run Ricochet augment.
    public void AddRicochetTotalPercent(float amount) => ApplyStatChange(() => currentState.totalRicochetPercent += amount);
    public void AddMultishotFlat(float amount) => ApplyStatChange(() => currentState.bonusMultishotFlat += amount);
    // NEW Total tier — no augment grants this yet (MultiShotAugment only ever grants Flat), reserved for a
    // future in-run Multishot augment.
    public void AddMultishotTotalPercent(float amount) => ApplyStatChange(() => currentState.totalMultishotPercent += amount);
    public void AddFireRateFlat(float amount) => ApplyStatChange(() => currentState.fireRateFlat += amount);
    public void AddFireRateTotalPercent(float amount) => ApplyStatChange(() => currentState.fireRateTotalPercent += amount);
    public void AddProjectileSpeedFlat(float amount) => ApplyStatChange(() => currentState.projectileSpeedFlat += amount);
    public void AddProjectileSpeedTotalPercent(float amount) => ApplyStatChange(() => currentState.projectileSpeedTotalPercent += amount);
    public void AddWeaponRadiusFlat(float amount) => ApplyStatChange(() => currentState.bonusWeaponRadiusFlat += amount);
    // NEW Total tier — no augment grants WeaponRadius percent yet (only ClassPassiveData touches
    // bonusWeaponRadiusPercent) — reserved for a future in-run augment on this stat.
    public void AddWeaponRadiusTotalPercent(float amount) => ApplyStatChange(() => currentState.totalWeaponRadiusPercent += amount);
    // Total tier — no in-run augment grants this yet (ShieldAugment grants the Bonus tier below instead),
    // reserved for a future outlier effect.
    public void AddMaxShieldTotalPercent(float amount) => ApplyStatChange(() => currentState.totalMaxShieldPercent += amount);
    // Bonus tier — first augment-facing entry point for MaxShield (ShieldAugment). Adds directly against
    // baseMaxShieldPercent, both in the same "percent of Max Health" units, e.g. +0.2 = +20% Max Health as
    // shield — see MaxShield's own comment for why there's no separate bonusFlat*bonusPercent multiplier
    // step here, unlike most other stats. Routes through ApplyStatChange per that method's own "every future
    // stat-granting effect should route through this" convention, even though shield isn't part of the
    // health/energy catch-up logic it performs (see ApplyStatChange's own comment) — the ceiling still rises
    // immediately, current shield just naturally catches up via its own regen/no-damage-timer snap instead
    // of being bumped here.
    public void AddMaxShieldPercent(float amount) => ApplyStatChange(() => currentState.bonusMaxShieldAdditive += amount);
    // Total tier. Was used by the weapon-count/multishot/track "compensate" augments
    // (BladeweaviestBladeweaverAugment, MultiShotAugment, OuterTrackAugment, SurgeWeaponCountAugment,
    // WeaponCountAugment) — removed from all of them (2026-08-19, see AddStrengthTotalPercent's own doc
    // comment and CharacterStatsPD.WeaponStrength's for the full reasoning). No current caller; kept
    // available for a future genuine "+X% OnHitPower" effect, same role AddStrengthTotalPercent plays for
    // KnightBigBuffAbilityPD.
    public void AddOnHitPowerTotalPercent(float amount) => ApplyStatChange(() => currentState.totalOnHitPowerPercent += amount);
    // Same MultiplyStrengthPenalty-style compounding convention — multiple spread-reducing sources
    // multiply together rather than summing (two -80% reductions compound to 0.2 * 0.2 = 0.04, not clamp
    // at 1 - 1.6). factor is what bonusMultishotSpreadPercent gets multiplied by (e.g. 1f - 0.8f for
    // MultiShotSpreadAugment's -80% reduction).
    public void MultiplyMultishotSpreadPercent(float factor) => ApplyStatChange(() => currentState.bonusMultishotSpreadPercent *= factor);
    // "Additive against the 1f baseline" pattern, same as AddCritChancePercent — knockbackMultiplier starts
    // at 1f (also aggregated multiplicatively by ClassPassiveData separately), e.g. +0.5 = +50% knockback.
    public void AddKnockbackPercent(float amount) => ApplyStatChange(() => currentState.knockbackMultiplier += amount);

    // Shared "stacking bonus" scaffold for the whole Phantom Blade augment family — each Phantom Blade
    // augment the player has picked contributes its own delta here (matching old system's per-augment
    // OnSummon handlers, e.g. PhantomBladeOnCritAugment/PhantomBladePassiveAugment both doing
    // `blade.damage += blade.baseDamage * bladeDamageBonus`), and every blade reads the combined total
    // once at spawn time rather than each blade needing to know which augments exist. Plain += (not
    // ApplyStatChange) — doesn't affect Max Health/Energy, and nothing else reads these live the way
    // Strength/MoveSpeed etc. do.
    // In State, not plain fields, same as every other bonus/total/penalty field in this class now — see
    // that section's own doc comment for why (any Add*/Multiply* exposed here could be reused temporarily
    // by a future Phantom Blade ability/augment, same as the original 10 discovered this the hard way).
    public float phantomBladeDamageBonus => currentState.phantomBladeDamageBonus;
    public float phantomBladeSizeBonus => currentState.phantomBladeSizeBonus;
    // Second, separate multiplicative layer for each of the two above — PhantomBladeSizeAugment's own
    // damage/size bonuses are meant to scale the blade's ALREADY-combined additive total (every other
    // Phantom Blade augment's own +damage%/+size% summed together), not just join those same additive
    // pools. Both start at 1f and are added to directly, same "additive against the 1f baseline"
    // convention as bonusMaxHealthPercent/bonusCritChancePercent (+0.5 = +50% on top of the additive
    // total). PhantomBladeSummonHelper applies each pair as
    // `(1 + phantomBladeDamageBonus) * phantomBladeDamageBonusPercent` and
    // `(1 + phantomBladeSizeBonus) * phantomBladeSizeBonusPercent`.
    public float phantomBladeDamageBonusPercent => currentState.phantomBladeDamageBonusPercent != 0f ? currentState.phantomBladeDamageBonusPercent : 1f;
    public float phantomBladeSizeBonusPercent => currentState.phantomBladeSizeBonusPercent != 0f ? currentState.phantomBladeSizeBonusPercent : 1f;
    // Third layer, multiplicative like the "Percent" pair above but for PENALTIES rather than bonuses —
    // e.g. PhantomBladeCarnivalAugment's own -35% total damage/size tradeoff for its extra-blade-per-summon
    // effect. Same MultiplyStrengthPenalty-style compounding convention as bonusStrengthPenalty
    // (multiple penalty sources compound multiplicatively rather than summing, so two -35% penalties would
    // compound to 0.65 * 0.65 = 0.4225, not clamp at 1 - 0.7 = 0.3). Plain *= (not ApplyStatChange), same
    // reasoning as the rest of this scaffold.
    public float phantomBladeDamagePenalty => currentState.phantomBladeDamagePenalty != 0f ? currentState.phantomBladeDamagePenalty : 1f;
    public float phantomBladeSizePenalty => currentState.phantomBladeSizePenalty != 0f ? currentState.phantomBladeSizePenalty : 1f;
    // False by default — Phantom Blades don't trigger OnHit at all unless PhantomBladeOnHitAugment (or a
    // future family member) turns it on. Kept as its own bool rather than gating off
    // phantomBladeOnHitPowerPercent > 0 — that would conflate "how strong is the onhit trigger" (a
    // magnitude) with "does this blade attempt to trigger onhit at all" (a binary capability), which
    // would silently break if anything ever reduced the percent without meaning to disable the feature.
    // Both are read once at Summon() time like the other two, baked into that blade's lifetime rather
    // than reacting live to later picks.
    // In State, not a plain field — see the "Augment/bonus modifiers" section's own doc comment for why
    // every mutable field here lives in State now, even a one-directional enable flag with no exposed
    // "disable" path today.
    public bool phantomBladeOnHitEnabled => currentState.phantomBladeOnHitEnabled;
    public bool luckRolledLevelUpStats => currentState.luckRolledLevelUpStats;
    public float luckRolledLevelUpStatsHighrollScale => currentState.luckRolledLevelUpStatsHighrollScale;
    public float phantomBladeOnHitPowerPercent => currentState.phantomBladeOnHitPowerPercent;
    public void AddPhantomBladeDamageBonus(float amount) => currentState.phantomBladeDamageBonus += amount;
    public void AddPhantomBladeSizeBonus(float amount) => currentState.phantomBladeSizeBonus += amount;
    public void AddPhantomBladeDamageBonusPercent(float amount) => currentState.phantomBladeDamageBonusPercent += amount;
    public void AddPhantomBladeSizeBonusPercent(float amount) => currentState.phantomBladeSizeBonusPercent += amount;
    public void MultiplyPhantomBladeDamagePenalty(float factor) => currentState.phantomBladeDamagePenalty *= factor;
    public void MultiplyPhantomBladeSizePenalty(float factor) => currentState.phantomBladeSizePenalty *= factor;
    public void EnablePhantomBladeOnHit() => currentState.phantomBladeOnHitEnabled = true;
    public void EnableLuckRolledLevelUpStats(float highrollBonusScale)
    {
        currentState.luckRolledLevelUpStats = true;
        currentState.luckRolledLevelUpStatsHighrollScale = highrollBonusScale;
    }
    public void AddPhantomBladeOnHitPowerPercent(float amount) => currentState.phantomBladeOnHitPowerPercent += amount;

    // Read directly by EnemyBasePD.Die() (Simulate-context, safe pure reads of counts only ever incremented
    // once each, at OnApply()) to reserve exactly enough death-holds up front, atomically — no dependence
    // on OnEnemyDeath listener dispatch order/timing the way a single "did anyone enable this" bool would
    // have (that was the previous killEffectHoldEnabled design; a listener requesting its own hold DURING
    // dispatch could lose the race against deletion checking the count that same tick). deathEventCount is
    // summed across every player's Stats since a death-reactive effect (e.g. a future "on any enemy death"
    // augment) doesn't care who the killer was; killEventCount is read only off the killer's own Stats since
    // kill-credited effects (e.g. OnKillLaunchEnemiesAugment) only ever react to their own kills. Each
    // augment that needs a hold increments the matching count once in OnApply() — multiple such augments on
    // the same player just add up, same idempotent shape as every other augment count in this file — and
    // releases its own hold via the enemy's ReleaseDeathHold() once its individual effect concludes.
    public int deathEventCount => currentState.deathEventCount;
    public int killEventCount => currentState.killEventCount;
    public void AddDeathEventCount(int amount = 1) => currentState.deathEventCount += amount;
    public void AddKillEventCount(int amount = 1) => currentState.killEventCount += amount;

    // Punches a partial-rate hole in Simulate()'s own !state.isSurging gate on the passive EnergyRegen tick
    // — 0 (default) means fully suppressed while Surging, which is the current always-true case: the
    // augment that used to call this (EnergyRegenWhileSurgingAugment) has been removed, so nothing grants
    // this right now. Left in place as a reusable hook for a future augment in the same spirit. Plain +=
    // (not ApplyStatChange) since it doesn't touch Max Health/Energy, same shape as
    // phantomBladeDamageBonusPercent; multiple copies of a future augment here would just stack additively.
    public float energyRegenWhileSurgingPercent => currentState.energyRegenWhileSurgingPercent;
    public void AddEnergyRegenWhileSurgingPercent(float amount) => currentState.energyRegenWhileSurgingPercent += amount;

    // Called by a weapon (melee/axe/ranged) once it's confirmed touching an ally's hurtbox — every weapon
    // always runs this detection now (no more enable gate; "canContactAllies" is permanently on). A plain
    // touch, never damage — real friendly fire (the old allyStrikeDamageEnabled/canDamageAllies gate) has
    // been removed entirely from the project, so this is the ONLY thing an ally hit can ever do: notify
    // whatever "hit ally, do something" augment (OnStrikeHealAllyAugment, future family members) is
    // listening. Self- and dead-ally exclusion live here (not in each weapon's detection loop) so every
    // current and future _onAllyStrike listener gets that guard for free rather than each needing to
    // re-check it.
    public void NotifyAllyStrike(AllyStrikeData data)
    {
        if (data.ally == null || data.ally == this || data.ally.IsDead) return;
        _onAllyStrike.Invoke(data);
    }

    // Called by HealFromAlly() on the HEALER's own Stats (not the receiving ally's) — see
    // AllyHealGrantedData's own doc comment. amount <= 0 guard mirrors HealFromAlly's own early-out (a fully
    // overhealed/dead target never reaches this call at all, but kept here too for any future direct caller).
    public void NotifyAllyHealGranted(float amount)
    {
        if (amount <= 0f) return;
        _onAllyHealGranted.Invoke(new AllyHealGrantedData { amount = amount });
    }

    // ── Execute ────────────────────────────────────────────────────────────
    // Checked by EnemyBasePD.TakeDamage — see that method's own doc comment for the full condition. Two
    // independent tracks, each gated purely on its own threshold being above 0 — no separate enable bool:
    // a plain "any hit" execute (executeThreshold — ExeBigAxeAbilityPD's own temporary buff is the current
    // consumer) and a crit-gated one (critExecuteThreshold, what CritExecuteAugment actually turns on by
    // adding to it). Execute is just a cheap "does this attacker have EITHER threshold above 0 at all" gate,
    // checked before either specific comparison runs.
    // In State, not plain fields — see the "Augment/bonus modifiers" section's own doc comment. No
    // zero-guard needed on either — 0f already IS both fields' correct neutral/"off" value.
    public float executeThreshold => currentState.executeThreshold;
    public float critExecuteThreshold => currentState.critExecuteThreshold;
    public bool Execute => executeThreshold > 0f || critExecuteThreshold > 0f;

    // Bosses resist execute effects by 80% — a threshold tuned for normal/elite enemies would otherwise
    // insta-finish a boss the instant one crit landed while it was already low, which is a very different
    // balance question than "does this run-of-the-mill enemy die a hit early." One shared multiplier
    // (0.2 = keep 20%, i.e. -80%) rather than a separate boss-specific stat, so tuning the player-facing
    // threshold and the boss reduction can never drift out of the intended ratio relative to each other.
    public float BossExecuteThreshold => executeThreshold * 0.2f;
    public float BossCritExecuteThreshold => critExecuteThreshold * 0.2f;

    // Multiplicative diminishing-returns stacking (2026-08-14) — same "never reach 100%" reasoning as
    // AddDamageReductionPercent (see its own doc comment): executeThreshold/critExecuteThreshold are both
    // % of max health, and letting several sources sum linearly would eventually GUARANTEE an execute
    // (>=100% health) the instant a hit lands, regardless of how healthy the target actually is. Combining
    // via 1 - (1-old)*(1-amount) instead means each additional source shrinks the REMAINING "safe" health
    // margin multiplicatively — asymptotically approaches but can never exceed 100% from stacking alone (a
    // single 100% source still hits the cap outright, same as DR's own single-source edge case).
    public void AddExecuteThresholdPercent(float amount) => ApplyStatChange(() => currentState.executeThreshold = 1f - (1f - currentState.executeThreshold) * (1f - amount));
    // Reverts a specific earlier AddExecuteThresholdPercent(amount) grant — divides back out the exact
    // factor that grant multiplied in, same shape as RemoveDamageReductionPercent. amount must match the
    // value originally passed to AddExecuteThresholdPercent (e.g. ExeBigAxeAbilityPD's own temporary buff
    // un-applying itself on deactivate) — a straight AddExecuteThresholdPercent(-amount) does NOT correctly
    // invert this now that stacking is multiplicative, same reasoning DR's own Remove method exists for.
    public void RemoveExecuteThresholdPercent(float amount) => ApplyStatChange(() => currentState.executeThreshold = 1f - (1f - currentState.executeThreshold) / (1f - amount));

    public void AddCritExecuteThresholdPercent(float amount) => ApplyStatChange(() => currentState.critExecuteThreshold = 1f - (1f - currentState.critExecuteThreshold) * (1f - amount));

    // These children stay visually the same size regardless of the character's own scale — a revive
    // trigger volume and worldspace/minimap UI markers shouldn't grow along with the player. Height/position
    // is deliberately left alone — WorldspaceUI in particular sits under its own "YAxisMovers" parent
    // specifically so its Y offset keeps scaling up with the character (so the UI doesn't clip into a bigger
    // character), while only ITS OWN scale (set below) gets counter-scaled back to constant. Direct
    // Inspector references, not a name-string lookup — a plain transform.Find(name) only searches direct
    // children, so it silently found nothing (and left the UI scaling with the character) once WorldspaceUI/
    // Minimap Icon ended up nested under an intermediate parent instead of directly under this transform;
    // wiring the actual Transform here fails loudly (an empty slot, visible in the Inspector) instead of
    // silently no-opping the next time the hierarchy gets restructured.
    [SerializeField] private Transform[] unscaledSizeChildren;

    // Smoothed visual anchor for world-space popups (damage/heal numbers) that need to follow this player
    // without inheriting root/rigidbody jitter — physics-driven position updates are choppy at render
    // framerate, same "track the smoothly-interpolated child, not the physics root" reasoning as
    // IEnemyPD.Visuals, just this hierarchy's own "YAxisMovers" child (see unscaledSizeChildren's own
    // comment above for context on that name) instead of a dedicated "Visuals" child. Confirmed live
    // (2026-08-17): player damage numbers tracking transform.position directly were visibly jittery;
    // status popups never showed it only because they have no lateral arc to make the jitter visible.
    // Direct Inspector reference, not a name-string lookup — same reasoning as unscaledSizeChildren above.
    [SerializeField] private Transform yAxisMovers;
    public Transform YAxisMovers => yAxisMovers != null ? yAxisMovers : transform;

    // Additive against the 1f baseline (amount is a delta, e.g. +0.25 = +25%), same convention as every
    // other bonus here — unlike a direct transform.localScale *= multiplier, which would compound
    // multiplicatively across multiple size augments instead of stacking their percentages. Re-derives
    // the full scale from bonusSizePercent every call rather than incrementally adjusting the transform,
    // so this stays correct no matter how many size augments have been applied. Each weapon's own root
    // localScale already reads bonusSizePercent directly too (see WeaponManagerPD's own RegisterWeapon/
    // LateUpdate — WeaponRadius itself deliberately doesn't, see its own comment for why), so no separate
    // call is needed here to scale the weapon.
    public void AddSizePercent(float amount)
    {
        currentState.bonusSizePercent += amount;
        transform.localScale = Vector3.one * currentState.bonusSizePercent;

        foreach (var child in unscaledSizeChildren)
            if (child != null)
                child.localScale = Vector3.one / currentState.bonusSizePercent;
    }

    private bool _bonusesApplied;
    public void ApplyProgressionBonuses(ProgressionBonuses b)
    {
        if (_bonusesApplied) return;
        _bonusesApplied = true;

        ApplyStatChange(() =>
        {
            currentState.bonusStrengthFlat          += b.strengthFlat;
            currentState.bonusMoveSpeedFlat         += b.moveSpeedFlat;
            currentState.attackSpeedFlat            += b.attackSpeedPct * baseAttackSpeed;
            currentState.bonusEnergyRegenFlat       += b.energyRegenFlat;
            currentState.dashHaste                  += b.dashHaste;
            currentState.abilityHaste                += b.abilityHaste;
            currentState.bonusCritChanceFlat += b.critChanceFlat;
            currentState.bonusXPRadiusFlat          += b.xpRadiusFlat;
            currentState.bonusWeaponRangeFlat       += b.weaponRangeFlat;
            currentState.bonusMaxEnergyFlat         += b.maxEnergyFlat;
            currentState.dashCharges                += b.dashChargesBonus;
            currentState.levelUpOptions             += b.levelUpOptionsBonus;
            currentState.revivePower                += b.revivePower;
            currentState.reviveTimePenaltyReduction *= b.reviveTimePenaltyMult;
            currentState.xpMultiplier               += b.xpMultiplier;
            currentState.bonusMaxHealthFlat         += b.maxHealthFlat;
            currentState.bonusHealthRegenFlat       += b.healthRegenFlat;
            currentState.totalStrengthPercent       += b.strengthTotalPercent;
            currentState.bonusLuckFlat              += b.luckFlat;
            currentState.strengthPerLevel           += b.strengthPerLevel;
            currentState.bonusMaxShieldAdditive     += b.maxShieldFlat;
            currentState.shieldRechargeTime         += b.shieldRechargeTime;
            currentState.bonusMoveSpeedPercent      += b.moveSpeedBonusPercent;
        });

        // Not a stat add — activates the Death Prevention mechanic itself, same centralized entry point any
        // augment that grants it also calls (see ActivateDeathPreventions' own doc comment for the
        // "already active elsewhere, shrink the grant timer instead" stacking rule). Outside the
        // ApplyStatChange block above since it doesn't touch MaxHealth/MaxEnergy/Shield. This call always
        // runs before any augment could possibly have activated it already (ApplyProgressionBonuses fires at
        // spawn, augments only apply mid-run), so it's always the "first activator" branch in practice.
        if (b.deathPreventionUnlocked) ActivateDeathPreventions();

        // Not a stat add either — a full snapshot assignment (=, not +=), same reasoning as
        // deathPreventionUnlocked above (outside ApplyStatChange, doesn't touch MaxHealth/MaxEnergy/Shield).
        // See UnlockedAugmentTagMask's own doc comment.
        currentState.unlockedAugmentTagMask = b.unlockedAugmentTagMask;
    }

    // Called from GameManagerPD.UpdateView() once per level gained, off verified state only — never
    // from Simulate(), since bonus fields aren't part of predicted State and can't roll back on replay.
    public void OnLevelUp() => ApplyStatChange(() => currentState.bonusStrengthFlat += strengthPerLevel);

    // Whole-run bookkeeping only, one field per StatType ApplySingleStatEntry below actually handles — the
    // literal amount ever added to that field via a stat-augment pick, nothing more (not the individual
    // picks/options themselves, which AugmentManagerPD.State.picks already tracks whole-run, tagged per
    // round). Returned by ApplyStatAugmentPick so AugmentManagerPD can accumulate it into its own per-player
    // running total —
    // kept here, not duplicated as a second switch statement elsewhere, so there's exactly one place that
    // defines "what a stat pick actually grants."
    public struct StatBoostTotals
    {
        public float maxHealth;
        public float strength;
        public float moveSpeed;
        public float weaponRange;
        public float attackSpeed;
        public float healthRegen;
        public float dashHaste;
        public float critChance;
        public float luck;
        public float abilityHaste;

        public static StatBoostTotals operator +(StatBoostTotals a, StatBoostTotals b) => new()
        {
            maxHealth    = a.maxHealth    + b.maxHealth,
            strength     = a.strength     + b.strength,
            moveSpeed    = a.moveSpeed    + b.moveSpeed,
            weaponRange  = a.weaponRange  + b.weaponRange,
            attackSpeed  = a.attackSpeed  + b.attackSpeed,
            healthRegen  = a.healthRegen  + b.healthRegen,
            dashHaste    = a.dashHaste    + b.dashHaste,
            critChance   = a.critChance   + b.critChance,
            luck         = a.luck         + b.luck,
            abilityHaste = a.abilityHaste + b.abilityHaste,
        };
    }

    // Called from AugmentManagerPD.UpdateView() once per verified pick, same reasoning as OnLevelUp.
    // multiplier is the picking player's own meta-progression multiplier (ProgressionManager.GetStatMultiplier),
    // captured into PlayerControllerPD.Input by the OWNING client — never recomputed locally here, since
    // a remote machine's own ProgressionManager save data would give a different (wrong) answer.
    // luckMultiplierA/B (2026-08-19) — same "captured client-side, never recomputed here" trust as
    // multiplier, but for LuckRolledLevelUpStatsAugment's own roll: AugmentCardUI rolls each stat
    // independently (against UnityEngine.Random, not the shared GameRandom stream — see its own doc
    // comment for why), caches the result, and resubmits the SAME rolled value at pick time so what's
    // applied here always matches what the card displayed. Both default to 1f (neutral) for a player
    // without the augment, or for an augment-card pick (never read in that branch).
    public StatBoostTotals ApplyStatAugmentPick(StatAugmentEntry entryA, StatAugmentEntry entryB, float multiplier, float luckMultiplierA = 1f, float luckMultiplierB = 1f)
    {
        StatBoostTotals delta = default;
        ApplyStatChange(() =>
        {
            ApplySingleStatEntry(entryA, multiplier, luckMultiplierA, ref delta);
            ApplySingleStatEntry(entryB, multiplier, luckMultiplierB, ref delta);
        });
        return delta;
    }

    // Called from PlayerDataPD.UpdateView() once per character spawn, on every machine (not owner-gated
    // like ApplyProgressionBonuses — classPassive is a shared asset reference baked into the prefab, not
    // per-machine save data, so every observer computes the identical result). No meta-progression
    // multiplier applied here, unlike picked stat augments — a class passive is a fixed identity bonus,
    // not something ProgressionManager's LevelUpStats upgrade is meant to scale.
    private bool _classPassivesApplied;
    public void ApplyClassPassives()
    {
        if (_classPassivesApplied) return;
        _classPassivesApplied = true;

        // "No passive assigned" resolves as "applied" too — future code checking this flag directly
        // shouldn't need to separately handle the no-passive case as a distinct not-yet-resolved state.
        if (classPassive == null) return;

        var p = classPassive;
        ApplyStatChange(() =>
        {
            currentState.bonusMoveSpeedFlat              += p.bonusMoveSpeedFlat;
            currentState.bonusMoveSpeedPercent           += p.bonusMoveSpeedPercent;
            currentState.moveSpeedPenalty                += p.moveSpeedPenalty;
            currentState.totalMoveSpeedPercent += p.totalMoveSpeedPercent;
            currentState.totalMoveSpeedPenalty           += p.totalMoveSpeedPenalty;

            currentState.bonusStrengthFlat           += p.bonusStrengthFlat;
            currentState.bonusStrengthPercent        += p.bonusStrengthPercent;
            currentState.bonusStrengthPenalty        += p.bonusStrengthPenalty;
            currentState.totalStrengthPercent += p.totalStrengthPercent;
            currentState.totalStrengthPenalty        += p.totalStrengthPenalty;

            currentState.bonusWeaponRadiusFlat           += p.bonusWeaponRadiusFlat;
            currentState.bonusWeaponRadiusPercent        += p.bonusWeaponRadiusPercent;
            currentState.totalWeaponRadiusPercent        += p.totalWeaponRadiusPercent;
            currentState.totalWeaponRadiusPenalty        += p.totalWeaponRadiusPenalty;

            currentState.attackSpeedFlat                 += p.attackSpeedFlat;
            currentState.attackSpeedTotalPercent += p.attackSpeedTotalPercent;
            currentState.attackSpeedPenalty              += p.attackSpeedPenalty;

            currentState.bonusWeaponCountFlat            += p.bonusWeaponCountFlat;
            currentState.weaponCountMultiplier      *= p.bonusWeaponCountMultiplier;

            currentState.bonusWeaponRangeFlat            += p.bonusWeaponRangeFlat;
            currentState.bonusWeaponRangePercent         += p.bonusWeaponRangePercent;
            currentState.weaponRangePenalty              += p.weaponRangePenalty;
            currentState.totalWeaponRangePercent += p.totalWeaponRangePercent;
            currentState.totalWeaponRangePenalty         += p.totalWeaponRangePenalty;

            if (p.bonusSizePercent != 0f) AddSizePercent(p.bonusSizePercent);

            currentState.bonusCritChanceFlat += p.bonusCritChanceFlat;
            currentState.bonusCritChancePercent          += p.bonusCritChancePercent;
            currentState.totalCritChancePercent          += p.totalCritChancePercent;
            currentState.totalCritChancePenalty          += p.totalCritChancePenalty;

            currentState.bonusCritDamageFlat             += p.bonusCritDamageFlat;
            currentState.bonusCritDamagePercent          += p.bonusCritDamagePercent;

            currentState.bonusRicochetFlat               += p.bonusRicochetFlat;
            currentState.bonusRicochetPercent            += p.bonusRicochetPercent;
            currentState.totalRicochetPercent            += p.totalRicochetPercent;
            currentState.totalRicochetPenalty            += p.totalRicochetPenalty;

            currentState.bonusMultishotFlat              += p.bonusMultishotFlat;
            currentState.bonusMultishotPercent           += p.bonusMultishotPercent;
            currentState.totalMultishotPercent           += p.totalMultishotPercent;
            currentState.totalMultishotPenalty           += p.totalMultishotPenalty;

            currentState.projectileDamageDropoffReduction += p.projectileDamageDropoffReduction;
            currentState.projectileOnHitDropoffReduction  += p.projectileOnHitDropoffReduction;

            currentState.fireRateFlat                    += p.fireRateFlat;
            currentState.fireRateTotalPercent += p.fireRateTotalPercent;
            currentState.fireRateTotalPenalty            += p.fireRateTotalPenalty;

            currentState.projectileSpeedFlat             += p.projectileSpeedFlat;
            currentState.projectileSpeedTotalPercent     += p.projectileSpeedTotalPercent;
            currentState.projectileSpeedTotalPenalty     += p.projectileSpeedTotalPenalty;

            currentState.bonusMaxHealthFlat              += p.bonusMaxHealthFlat;
            currentState.bonusMaxHealthPercent           += p.bonusMaxHealthPercent;
            currentState.totalMaxHealthPercent += p.totalMaxHealthPercent;
            currentState.totalMaxHealthPenalty           += p.totalMaxHealthPenalty;

            currentState.bonusHealthRegenFlat            += p.bonusHealthRegenFlat;
            currentState.bonusHealthRegenPercent         += p.bonusHealthRegenPercent;
            currentState.totalHealthRegenPercent         += p.totalHealthRegenPercent;
            currentState.totalHealthRegenPenalty         += p.totalHealthRegenPenalty;

            currentState.bonusMaxShieldAdditive          += p.bonusMaxShieldAdditive;
            currentState.totalMaxShieldPercent           += p.totalMaxShieldPercent;
            currentState.totalMaxShieldPenalty           += p.totalMaxShieldPenalty;

            currentState.bonusMaxEnergyFlat              += p.bonusMaxEnergyFlat;
            currentState.bonusMaxEnergyPercent           += p.bonusMaxEnergyPercent;

            currentState.bonusEnergyRegenFlat            += p.bonusEnergyRegenFlat;
            currentState.bonusEnergyRegenPercent         += p.bonusEnergyRegenPercent;
            currentState.energyRegenPenalty              += p.energyRegenPenalty;
            currentState.totalEnergyRegenPercent         += p.totalEnergyRegenPercent;
            currentState.totalEnergyRegenPenalty         += p.totalEnergyRegenPenalty;

            currentState.bonusSurgeCostFlat              += p.bonusSurgeCostFlat;
            // Multiplicative, not additive — same reasoning as damageReductionPercent's own treatment just
            // below (each contributing class-passive tier compounds against the REMAINING cost fraction, so
            // 0 cost can never be reached purely from stacking many small per-level grants). surgeAttackSpeedPenalty
            // stays additive, matching every other *Penalty field's own convention in this sweep.
            currentState.surgeCostReductionPercent       *= (1f - p.surgeCostReductionPercent);
            currentState.surgeAttackSpeedPenalty         += p.surgeAttackSpeedPenalty;

            currentState.dashHaste                       += p.dashHaste;
            currentState.bonusDashHastePercent           += p.bonusDashHastePercent;
            currentState.totalDashHastePercent           += p.totalDashHastePercent;
            currentState.totalDashHastePenalty           += p.totalDashHastePenalty;

            currentState.abilityHaste                    += p.abilityHaste;
            currentState.bonusAbilityHastePercent        += p.bonusAbilityHastePercent;
            currentState.totalAbilityHastePercent        += p.totalAbilityHastePercent;
            currentState.totalAbilityHastePenalty        += p.totalAbilityHastePenalty;

            currentState.bonusLuckFlat                   += p.bonusLuckFlat;
            currentState.bonusLuckPercent                += p.bonusLuckPercent;
            currentState.luckPenalty                     += p.luckPenalty;
            currentState.totalLuckPercent                += p.totalLuckPercent;
            currentState.totalLuckPenalty                += p.totalLuckPenalty;
            currentState.bonusLuckFloorPercent           += p.bonusLuckFloorPercent;
            currentState.luckFloorPenalty                += p.luckFloorPenalty;
            currentState.bonusLuckCeilingPercent         += p.bonusLuckCeilingPercent;
            currentState.luckCeilingPenalty              += p.luckCeilingPenalty;
            currentState.bonusHighrollPercent            += p.bonusHighrollPercent;
            currentState.highrollPenalty                 += p.highrollPenalty;
            currentState.bonusHighrollBonusPercent       += p.bonusHighrollBonusPercent;
            currentState.highrollBonusPenalty            += p.highrollBonusPenalty;

            currentState.knockbackMultiplier             *= p.knockbackMultiplier;
            currentState.knockbackPenalty                += p.knockbackPenalty;

            currentState.bonusOnHitPowerMultiplier       *= p.bonusOnHitPowerMultiplier;
            currentState.bonusOnHitPowerPenalty          += p.bonusOnHitPowerPenalty;
            currentState.totalOnHitPowerPercent          += p.totalOnHitPowerPercent;
            currentState.totalOnHitPowerPenalty          += p.totalOnHitPowerPenalty;

            currentState.stackPowerPercent               += p.stackPowerPercent;
            currentState.stackPowerPenalty               += p.stackPowerPenalty;

            currentState.xpMultiplier                    += p.xpMultiplier;
            currentState.xpPenalty                       += p.xpPenalty;

            currentState.bonusXPRadiusFlat               += p.bonusXPRadiusFlat;
            currentState.bonusXPRadiusPercent            += p.bonusXPRadiusPercent;

            currentState.reviveTimePenalty               += p.reviveTimePenalty;
            currentState.reviveTimePenaltyReduction      *= p.reviveTimePenaltyReduction;
            currentState.revivePower                     += p.revivePower;

            // Multiplicative, not additive — same reasoning as AddDamageReductionPercent's own doc comment
            // (each contributing class-passive tier compounds against the REMAINING damage fraction, so
            // 100% reduction can never be reached purely from stacking many small per-level grants).
            // damageReductionPenalty is untouched — a "Penalty" field, same additive-tier convention every
            // other *Penalty field in this loop already uses, not part of this sweep.
            currentState.damageReductionPercent *= (1f - p.damageReductionPercent);
            currentState.damageReductionPenalty          += p.damageReductionPenalty;
        });

        // classPassive.passiveAugments is no longer spawned here — AugmentManagerPD.Simulate() registers
        // them as real PlayerAugmentRecords (and spawns their effect prefabs itself via SpawnAugmentEffect)
        // the moment this player's CharacterStatsPD becomes available, since that's the only place safe to
        // mutate AugmentManagerPD's own predicted state. See RegisterClassPassiveAugments and
        // ClassPassiveData.passiveAugments' own doc comments for the full reasoning.
    }

    // Called once per spawn (see PlayerDataPD.UpdateView()) to reapply whatever stat-augment totals
    // AugmentManagerPD has on record for this player — same underlying fields ApplySingleStatEntry above
    // grants, just applied as one lump sum instead of one pick at a time. On a normal game start every
    // player's recorded totals are still all-zero (nobody's picked anything yet), so this is a genuine
    // no-op then; for a reconnecting player (AugmentManagerPD itself is never destroyed/recreated by a
    // single player dropping, so its bookkeeping survives the disconnect) it's what restores the stats
    // they'd already earned. Not owner-gated, same reasoning as ApplyClassPassives — every machine holds
    // an identically-computed copy of AugmentManagerPD's totals (accumulated off verified picks the same
    // way on every machine), so every observer applies the same thing.
    public void ApplyStatBoostTotals(StatBoostTotals totals)
    {
        ApplyStatChange(() =>
        {
            currentState.bonusMaxHealthFlat    += totals.maxHealth;
            currentState.bonusStrengthFlat     += totals.strength;
            currentState.bonusMoveSpeedFlat    += totals.moveSpeed;
            currentState.bonusWeaponRangeFlat  += totals.weaponRange;
            currentState.attackSpeedFlat       += totals.attackSpeed;
            currentState.bonusHealthRegenFlat  += totals.healthRegen;
            currentState.dashHaste             += totals.dashHaste;
            currentState.bonusCritChanceFlat += totals.critChance;
            currentState.bonusLuckFlat         += totals.luck;
            currentState.abilityHaste          += totals.abilityHaste;
        });
    }

    // luckMultiplier (2026-08-19) — see ApplyStatAugmentPick's own doc comment. Every case multiplies by
    // both multiplier and luckMultiplier now — WeaponRange used to be a lone exception that skipped
    // `multiplier` entirely (a bug, found and fixed 2026-08-19), so it's treated the same as every other
    // stat here rather than carrying its own special case forward.
    private void ApplySingleStatEntry(StatAugmentEntry entry, float multiplier, float luckMultiplier, ref StatBoostTotals delta)
    {
        switch (entry.statType)
        {
            case StatType.MaxHealth:
                { float d = entry.value * multiplier * luckMultiplier; currentState.bonusMaxHealthFlat += d; delta.maxHealth += d; break; }
            case StatType.Strength:
                { float d = entry.value * multiplier * luckMultiplier; currentState.bonusStrengthFlat += d; delta.strength += d; break; }
            case StatType.MoveSpeed:
                { float d = entry.value * multiplier * luckMultiplier; currentState.bonusMoveSpeedFlat += d; delta.moveSpeed += d; break; }
            case StatType.WeaponRange:
                { float d = entry.value * multiplier * luckMultiplier; currentState.bonusWeaponRangeFlat += d; delta.weaponRange += d; break; }
            case StatType.AttackSpeed:
                { float d = entry.value * 0.01f * baseAttackSpeed * multiplier * luckMultiplier; currentState.attackSpeedFlat += d; delta.attackSpeed += d; break; }
            case StatType.HealthRegen:
                { float d = entry.value * multiplier * luckMultiplier; currentState.bonusHealthRegenFlat += d; delta.healthRegen += d; break; }
            // StatType.EnergyRegen intentionally has no case here (2026-08-23) — Energy Regen is retired
            // from the in-run stat-augment pick pool entirely (the level-up card was already removed from
            // StatAugmentDatabase), and its shop/meta-progression contribution now flows through
            // ApplyProgressionBonuses instead, not this per-pick switch.
            case StatType.DashHaste:
                { float d = entry.value * multiplier * luckMultiplier; currentState.dashHaste += d; delta.dashHaste += d; break; }
            case StatType.CritChance:
                { float d = entry.value * 0.01f * multiplier * luckMultiplier; currentState.bonusCritChanceFlat += d; delta.critChance += d; break; }
            case StatType.Luck:
                { float d = entry.value * multiplier * luckMultiplier; currentState.bonusLuckFlat += d; delta.luck += d; break; }
            case StatType.AbilityHaste:
                { float d = entry.value * multiplier * luckMultiplier; currentState.abilityHaste += d; delta.abilityHaste += d; break; }
        }
    }

    public Color PlayerColor { get; private set; } = Color.white;

    // No longer called from outside this class — _onColorChanged (self-subscribed to this in LateAwake) is
    // the sole caller now. Invoke() fires every tick (see Simulate()'s own doc comment on why), so the
    // change-guard here — not a one-shot latch — is what keeps the actual renderer/material work cheap;
    // colorIndex itself only ever meaningfully changes once in practice (default 0 -> real resolved value),
    // but this stays correct even if that assumption ever changes.
    private int _lastAppliedColorIndex = -1;
    private void ApplyColor(int colorIndex)
    {
        if (colorIndex == _lastAppliedColorIndex) return;
        _lastAppliedColorIndex = colorIndex;

        Color c = _colorPalette != null ? _colorPalette.Get(colorIndex) : Color.white;
        PlayerColor = c;
        foreach (var a in GetComponentsInChildren<PlayerColorApplicator>(true))
            a.Apply(c);
        if (TryGetComponent(out WeaponManagerPD wm))
            wm.ApplyColor(c);
    }

    // Server-only, resolved exactly once — see Simulate()'s own doc comment on this whole mechanism.
    private bool _serverColorResolved;

    // PlayerID -> Steam ID -> GameStartData.HostColorAssignments lookup, same resolution chain proven in
    // LobbySceneController.OnPurrNetPlayerJoined's Steam-ID-snapshot rejection check. Fails (returns false)
    // rather than throwing if any step can't resolve yet — e.g. a connection that hasn't fully registered
    // with PlayersManager on the very first tick or two; Simulate() just tries again next tick since
    // _serverColorResolved stays false until this succeeds.
    private static bool TryResolveServerColor(PlayerID player, out int colorIndex)
    {
        colorIndex = 0;
        var nm = NetworkManager.main;
        if (nm == null) return false;

        // The host's own local (listen-server) player never resolves a real Connection/Steam ID the way a
        // remote player does — same carve-out as LobbySceneController.OnPurrNetPlayerJoined's rejection
        // check. Its own Steam ID is simply whatever this machine's own GameStartData.LocalUserId already is.
        if (player == nm.localPlayer)
            return GameStartData.HostColorAssignments.TryGetValue(GameStartData.LocalUserId, out colorIndex);

        if (!nm.TryGetModule<PlayersManager>(true, out var playersManager)) return false;
        if (!playersManager.TryGetConnection(player, out var conn)) return false;
        if (!(nm.transport is SteamTransport steamTransport)) return false;

        ulong steamId = steamTransport.GetSteamID(conn);
        if (steamId == 0) return false;

        return GameStartData.HostColorAssignments.TryGetValue(steamId.ToString(), out colorIndex);
    }

    // ── Public API ─────────────────────────────────────────────────────────
    public void StartSurge()
    {
        if (currentState.isDead || currentState.isSurging) return;
        currentState.isSurging = true;
        currentState.surgeTickTimer = Mathf.Max(1, Mathf.RoundToInt(SurgeTickInterval / _currentDelta));
        currentState.surgeMinDurationTimer = Mathf.Max(1, Mathf.RoundToInt(SurgeMinDuration / _currentDelta));
        currentState.energy -= SurgeCost;
        _onEnergySpent.Invoke(new EnergySpentData { amount = SurgeCost });
        _onSurgeStarted.Invoke();
    }

    public void TryEndSurge()
    {
        // forcedSurge makes this uncancellable via normal player input — a Berserk Surge only ever ends by
        // running its energy pool dry (see the natural-end block in Simulate()). Player releasing/re-
        // pressing the Surge button during one is a silent no-op, same as any other blocked-input case.
        if (!currentState.isSurging || currentState.surgeMinDurationTimer > 0 || currentState.forcedSurge) return;
        currentState.isSurging = false;
        _onSurgeEnded.Invoke();
    }

    // Forced, uncancellable Surge triggered by an effect rather than player input (e.g.
    // ShieldBreakFrenzyAugment's reworked shield-break reaction) — refills energy to full and (forces)
    // a Surge that runs until that energy pool naturally drains to 0 (see Simulate()'s own surge-drain
    // block), with TryEndSurge() blocked from ending it early. Invincible for the whole window via
    // forcedSurge itself (IsInvincible/TakeDamage OR it in — see their own doc comments), not a fixed-
    // duration GrantInvincibility() call, since exactly how long this surge lasts depends on MaxEnergy/
    // SurgeCost/SurgeTickInterval at the moment it starts and isn't precomputed here. Shield regen and the
    // Death Prevention grant timer both also pause for forcedSurge's own duration — see their respective
    // gates in Simulate().
    //
    // If a Surge is already running (player-initiated, or an earlier Berserk one), this does NOT end it and
    // start a fresh one — it piggybacks: refills energy and flips it to forced in place, extending whatever
    // was already going rather than interrupting it. _onSurgeStarted only fires when this call is what
    // ACTUALLY starts the surge (nothing was running before); an extended surge never gets a redundant
    // second start event, so every real start still pairs with exactly one later end.
    public void StartBerserkSurge()
    {
        if (currentState.isDead) return;

        bool alreadySurging = currentState.isSurging;
        bool alreadyForced = currentState.forcedSurge;

        currentState.energy = MaxEnergy;
        currentState.isSurging = true;
        currentState.forcedSurge = true;
        currentState.surgeTickTimer = Mathf.Max(1, Mathf.RoundToInt(SurgeTickInterval / _currentDelta));
        // No min-duration throttle needed — TryEndSurge() is blocked outright by forcedSurge regardless of
        // this timer's value, so there's nothing left for it to gate here.
        currentState.surgeMinDurationTimer = 0;

        if (!alreadySurging)
            _onSurgeStarted.Invoke();

        // "Berserk" status popup fires whenever forcedSurge itself actually flips on — independent of
        // _onSurgeStarted above (which only fires when nothing was surging at all yet), since piggybacking
        // onto an already-running NORMAL Surge still counts as Berserk activating for popup purposes.
        // Guarded so a redundant StartBerserkSurge() call while already forced-surging doesn't spam a
        // second popup.
        if (!alreadyForced)
            NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Berserk);
    }

    // Unconditional surge end, bypassing the surgeMinDurationTimer throttle TryEndSurge() respects — for
    // effects that need to guarantee surge is off right now (e.g. the burst), not just request an end that
    // could silently no-op if surge only just started. Now also correctly ends a Berserk (forced) Surge
    // (2026-08-23, was: only ever used/safe for a normal surge — see the forcedSurge/grace-period block
    // below, previously missing here entirely) — needed for PlayerControllerPD.Interrupt() to be able to cut
    // a Berserk Surge short on a hard interrupt landing (see its own doc comment).
    public void ForceEndSurge()
    {
        if (!currentState.isSurging) return;
        currentState.isSurging = false;
        currentState.surgeMinDurationTimer = 0;

        // Same forcedSurge reset + BerserkSurgeEndGrace invincibility hand-off the natural energy-depletion
        // end already does (see Simulate()'s own surge-drain block) — without this, force-ending a Berserk
        // Surge left forcedSurge stuck true forever, which is a PERMANENT invincibility bug (IsInvincible
        // ORs in forcedSurge) and permanently blocks anything gated on !IsForcedSurging.
        bool wasForcedSurge = currentState.forcedSurge;
        currentState.forcedSurge = false;
        if (wasForcedSurge)
        {
            currentState.invincible = true;
            currentState.invincibleTimer = Mathf.Max(currentState.invincibleTimer, Mathf.RoundToInt(BerserkSurgeEndGrace / _currentDelta));
        }

        _onSurgeEnded.Invoke();
    }

    // See IsSurgeBlocked/IAbilityPD.ActiveSurgeBlock's own doc comments for the full contract — each ability
    // holding a block calls AddSurgeBlock() once at its own Activate() (alongside ForceEndSurge()) and
    // RemoveSurgeBlock() once at its own end-of-active point. Paired 1:1 per holder, so the counter only
    // ever reaches 0 once every current holder has released its own block.
    public void AddSurgeBlock() => currentState.surgeBlockCount++;
    public void RemoveSurgeBlock() => currentState.surgeBlockCount = Mathf.Max(0, currentState.surgeBlockCount - 1);

    // Grants (or extends) invincibility for `duration` seconds. Always MAXES against whatever time is
    // already remaining rather than overwriting it, and a non-positive duration is simply a no-op — nothing
    // ever explicitly turns invincibility off "for" another source anymore. This is deliberate: dash,
    // ShieldBreakBurstAugment, DeathPreventionShieldDRAugment's death save, and Revive() all call this
    // independently, and previously (when this was a plain setter) whichever one fired LAST would win,
    // including a dash ending early and wiping out a still-active augment-granted window. Now every source
    // just contributes its own floor, the longest one currently active always wins, and each source's own
    // contribution is left to expire naturally via the invincibleTimer countdown in Simulate() — nobody
    // needs (or is able) to force it off early.
    public void GrantInvincibility(float duration)
    {
        if (duration <= 0f) return;
        currentState.invincible = true;
        currentState.invincibleTimer = Mathf.Max(currentState.invincibleTimer, Mathf.RoundToInt(duration / _currentDelta));
    }

    // Separate invincibility channel from GrantInvincibility's own shared/merged pool (see its own doc
    // comment for why that one can't support this) — used exclusively by "committed action" mechanics
    // (PlayerControllerPD's own dash, RogueSpinAbilityPD's leap) whose invincibility needs to be
    // independently revocable the instant that specific action gets cancelled (see
    // PlayerControllerPD.Interrupt()'s own doc comment), without touching any other overlapping
    // invincibility source. Same max-against-existing shape as GrantInvincibility for safety, though in
    // practice only one committed action can ever be active at a time (IAbilityPD.ForcesSpecialAction +
    // isDashing already enforce this via PlayerControllerPD.IsLockedOut), so there's never actually a
    // "multiple sources competing" scenario on this channel.
    public void GrantCommittedInvincibility(float duration)
    {
        if (duration <= 0f) return;
        currentState.committedInvincibleTimer = Mathf.Max(currentState.committedInvincibleTimer, Mathf.RoundToInt(duration / _currentDelta));
    }

    public bool IsSlowed => currentState.slowTimer > 0;

    // Player-side counterpart to EnemyBasePD.Slow()/ApplySlow() — no live source calls this yet (players
    // have no slow-inflicting enemy attacks currently), wired ahead of one existing, same reasoning as
    // PlayerControllerPD.Stun(). Move-speed only, deliberately NOT attack speed (unlike the enemy version —
    // see MoveSpeed's own read site, AttackSpeed never touches slowTimer/slowMultiplier at all). No
    // crowdControlResist scaling either — players don't have a resist stat (same reasoning as
    // PlayerControllerPD.ApplyStun's own doc comment). multiplier is how much of normal move speed remains
    // while active (e.g. 0.5 = half speed), floor-clamped to 0.1 (a 90% max slow, same safety floor
    // EnemyBasePD.ApplySlow() uses) so this can never fully freeze movement outright. A weaker slow landing
    // while a stronger one is still active doesn't water it down early — only adopts the new multiplier if
    // at least as strong (lower) as whatever's currently active, or the previous slow already expired — same
    // "never shortens what's already active" duration-extension rule as GrantInvincibility. Fires the
    // "Slowed" status popup on the rising edge only, same convention as EnemyBasePD.Slow()'s own popup gate.
    public void Slow(float multiplier, float duration)
    {
        if (duration <= 0f || currentState.isDead) return;
        bool wasSlowed = currentState.slowTimer > 0;

        multiplier = Mathf.Max(0.1f, multiplier);
        if (currentState.slowTimer <= 0 || multiplier <= currentState.slowMultiplier)
            currentState.slowMultiplier = multiplier;
        currentState.slowTimer = Mathf.Max(currentState.slowTimer, Mathf.RoundToInt(duration / _currentDelta));

        if (!wasSlowed) NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Slowed);
    }

    // Immediately zeroes ONLY the committed-action invincibility channel — safe to hard-cancel (unlike
    // GrantInvincibility's own general pool) specifically because nothing else ever writes to this channel,
    // so there's no risk of wiping out an unrelated source's contribution.
    public void CancelCommittedInvincibility() => currentState.committedInvincibleTimer = 0;

    // Direct on/off, not duration-based like GrantInvincibility — whatever Taunt/Invisibility ability ends
    // up calling these owns its own duration/timer, this is just the flag enemies actually read. Guarded on
    // an actual change so _onTauntChanged/_onInvisibleChanged only ever fire for a real flip, never a no-op
    // call — the enemy-targeting hub (EnemySpawnerPD) reacts to every firing, so a redundant call here would
    // otherwise force every active enemy to redundantly re-evaluate its target for nothing.
    public void SetTaunting(bool value)
    {
        if (currentState.taunting == value) return;
        currentState.taunting = value;
        RefreshAnyValidTargetExists();
        _onTauntChanged.Invoke(value);
    }

    // Duration-based, same "every source contributes its own floor, longest wins, natural countdown handles
    // turning it off" convention as GrantInvincibility — replaces the old direct on/off SetInvisible(bool),
    // which let one source's own end-of-buff call clobber a DIFFERENT source's still-active invisibility
    // window early (e.g. RogueVanishAbilityPD's own end-of-buff call used to unconditionally turn
    // invisibility off, even if some other overlapping effect still wanted it on). Fires _onInvisibleChanged
    // only on the rising edge here (invisible was off, this grant turns it on) — the falling edge fires from
    // Simulate()'s own natural-expiry block once the shared timer actually runs out, same change-guarded
    // contract SetInvisible used to provide.
    public void GrantInvisibility(float duration)
    {
        if (duration <= 0f) return;
        bool wasInvisible = currentState.invisible;
        currentState.invisible = true;
        currentState.invisibleTimer = Mathf.Max(currentState.invisibleTimer, Mathf.RoundToInt(duration / _currentDelta));
        if (!wasInvisible)
        {
            RefreshAnyValidTargetExists();
            _onInvisibleChanged.Invoke(true);
        }
    }

    // No change-guard/event needed (unlike SetTaunting/SetInvisible) — nothing reacts to the flip itself,
    // every weapon just reads the current value fresh each tick.
    public void SetWeaponsDisabled(bool value) => currentState.weaponsDisabled = value;

    // In State, not a plain field — reset to false right before every _onLethalDamageTaken.Invoke() call and
    // read immediately after, all within the same synchronous TakeDamage() call, so it was never a
    // resimulation-safety risk on its own (same-call communication channel, not a cross-tick accumulator).
    // Migrated anyway for consistency with the rest of this class.
    private bool lethalDamagePrevented { get => currentState.lethalDamagePrevented; set => currentState.lethalDamagePrevented = value; }

    // Called by a death-prevention listener (e.g. DeathPreventionShieldDRAugment), synchronously from inside
    // its own _onLethalDamageTaken callback, to flag that the lethal hit currently being processed by
    // TakeDamage should instead leave the player at 1 health. Calling this outside that callback (i.e. any
    // time TakeDamage isn't mid-resolution of a lethal hit) has no effect — the flag is only ever read once,
    // immediately after the event that gives it meaning.
    public void PreventLethalDamage() => lethalDamagePrevented = true;

    // Instantly refills shield to full and fires the same recharge-edge event the natural Simulate()-driven
    // regen does (only if it was actually broken) — used by effects like DeathPreventionShieldDRAugment's
    // death save, so "reapply while shielded"-style effects correctly reapply immediately rather than
    // waiting for the natural shieldRechargeTimer to also elapse.
    public void RechargeShield()
    {
        bool wasBroken = currentState.shield <= 0f;
        currentState.shield = MaxShield;
        currentState.shieldRechargeTimer = 0;
        if (wasBroken && currentState.shield > 0f)
            _onShieldRecharge.Invoke();
    }

    // 0.6 = speed reduced to 60%, i.e. a 40% slow. Buffed again 2026-08-21 (was 30%/1s, before that 20%/1s).
    private float threatLevel2SlowMultiplier = 0.6f;
    private float threatLevel2SlowDuration = 1.5f;

    // Set exactly once, from PlayerDataPD.UpdateView()'s own one-time setup sequence (verified-state only,
    // never resimulated — see ActivateDifficultyModifiers' own doc comment), never mutated again for the
    // rest of this life. Plain field, not predicted State, on purpose — same "write-once at spawn from
    // already-deterministic data, freely readable from Simulate()-context code afterward with zero rollback
    // risk" pattern EnemyBasePD.spawnTier already establishes (nothing in State tracks it, so a resimulation
    // replay never needs to restore/reconcile it — it's already permanently settled by the time any tick
    // that could read it runs).
    private bool _threatLevel2SlowOnDamageActive;

    // Threat Level 2's own unique modifier (per the character-select difficulty description) — Threat Level 1
    // has NO unique modifier of its own (explicit design, corrected 2026-08-20 — it was originally built
    // active from difficulty 1, which was wrong). Stacks with every HIGHER difficulty too (explicit design:
    // threat modifiers are cumulative, not standalone per tier), so this stays active at every difficulty
    // from 2 up. Called once, not owner-gated — see PlayerDataPD's own call site for why that's safe
    // (classPassive-style shared/deterministic setup, not per-machine save data).
    public void ActivateDifficultyModifiers(int difficulty)
    {
        _threatLevel2SlowOnDamageActive = difficulty >= 2;
    }

    // isPleb: true excludes this hit from Dodge credit (see NotifyDodge's own doc comment) — every caller
    // should pass whether ITS OWN attacker is EnemySpawnCategory.Pleb. isPassiveContact: true ALSO excludes
    // this hit from Dodge credit — for purely ambient/passive contact damage (most enemies' generic body-
    // touch) as opposed to contact damage that IS a real attack (see EnemyBasePD.ContactDamageIsPassive's
    // own doc comment) — every non-contact-damage caller can safely leave this at its default. Every other
    // TakeDamage() branch (damage/shield/health/death) is completely unaffected by either flag.
    public void TakeDamage(float damage, bool isPleb = false, bool isPassiveContact = false)
    {
        if (currentState.isDead) return;
        // See IsInvincible's own doc comment for why forcedSurge/committedInvincibleTimer are ORed in too.
        if (currentState.invincible || currentState.forcedSurge || currentState.committedInvincibleTimer > 0)
        {
            // "Dodge" — see NotifyDodge's own doc comment for the exact 3-condition contract. isPleb
            // (2026-08-21) explicitly excludes Pleb-tier attackers; isPassiveContact (2026-08-21) explicitly
            // excludes purely ambient contact damage — see this method's own doc comment for both.
            if (currentState.inDashMovement && !isPleb && !isPassiveContact) NotifyDodge();
            return;
        }

        currentState.damageEventId++;
        currentState.shieldRechargeTimer = Mathf.Max(1, Mathf.RoundToInt(shieldRechargeTime / _currentDelta));

        // Snapshotted before any mutation below — the actual shield/health deltas at the end of this method
        // (shieldBeforeHit - currentState.shield, etc.) drive the player-damage-number popup. Computed as a
        // plain before/after diff rather than tracked through each branch individually so it's automatically
        // correct for every case (partial shield absorb, the death-prevention clamp-to-1, an actual death)
        // without needing to duplicate that branching — see the Invoke call at the end of this method.
        float shieldBeforeHit = currentState.shield;
        float healthBeforeHit = currentState.health;

        // Raw incoming damage, before DR or shield touch it at all — see DamageTakenData's own doc comment.
        float preMitigationDamage = damage;

        damage *= DamageReduction;
        // Captured here, before the shield/health split below — "how much damage this hit represented" as
        // a whole, for _onDamaged's firing GATE specifically (kept exactly as it always was, post-DR,
        // pre-shield, so adding DamageTakenData's two new values below doesn't change WHEN this event
        // fires, only what it now carries).
        float totalDamageTaken = damage;

        // Break edge, not "shield is currently 0" — only true the tick it actually crosses down from >0,
        // same reasoning as every other transition-detected event in this class. Invoking is deferred to
        // the very end (see below) so a hit that also kills outright through shield+health+DamageReduction
        // doesn't fire it.
        bool shieldJustBroke = false;

        if (currentState.shield > 0f)
        {
            float shieldDamage = Mathf.Min(damage, currentState.shield);
            currentState.shield -= shieldDamage;
            damage -= shieldDamage;

            if (currentState.shield <= 0f)
                shieldJustBroke = true;
        }

        // What's actually left to affect health after BOTH DR and shield had their chance — see
        // DamageTakenData's own doc comment.
        float postMitigationDamage = damage;

        // Threat Level 2's own modifier — only unshielded (direct health) damage triggers this (2026-08-23,
        // was: every hit that landed, shield-absorbed or not) — a hit fully soaked by shield never reaches
        // postMitigationDamage > 0f, so it correctly doesn't trigger this. Refreshes the slow, not stacks it,
        // on every QUALIFYING hit — Slow()'s own "extend, don't shorten what's already active" rule means
        // sustained unshielded fire keeps the player continuously slowed rather than layering independent
        // timers, and it fades exactly threatLevel2SlowDuration seconds after the LAST such hit.
        if (_threatLevel2SlowOnDamageActive && postMitigationDamage > 0f)
            Slow(threatLevel2SlowMultiplier, threatLevel2SlowDuration);

        if (damage > 0f)
        {
            bool wouldBeLethal = currentState.health - damage <= 0f;
            if (wouldBeLethal)
            {
                // Gives any listener (e.g. a death-prevention augment) a chance to call
                // PreventLethalDamage() synchronously, right here, before this damage is actually applied —
                // see that method's own doc comment.
                lethalDamagePrevented = false;
                _onLethalDamageTaken.Invoke();

                // Built-in fallback save, checked only if nothing above already claimed this hit via
                // PreventLethalDamage() (a free, listener-based veto — none currently exist, but the hook
                // stays available for a future one) — a banked Death Prevention takes a fresh
                // DeathPreventionTime-long grant cycle to earn back (see Simulate()'s own comment), so it's
                // only spent when nothing cheaper already covered this hit.
                if (!lethalDamagePrevented && currentState.hasDeathPrevention)
                {
                    currentState.hasDeathPrevention = false;
                    lethalDamagePrevented = true;
                    GrantInvincibility(deathPreventionInvincibilityDuration);
                    // Fires AFTER invincibility is granted and hasDeathPrevention is already cleared — a
                    // listener (e.g. DeathPreventionShieldDRAugment) reacts to the save having landed, it
                    // never decides whether it happens (that's gainsDeathPreventions/hasDeathPrevention's
                    // job, resolved above).
                    _onDeathPrevented.Invoke();
                }
            }

            if (wouldBeLethal && lethalDamagePrevented)
                currentState.health = 1f;
            else
                currentState.health -= damage;
        }

        if (currentState.health <= 0f)
        {
            currentState.health = 0f;
            currentState.isDead = true;
            RefreshAnyValidTargetExists();
            // Only reachable here on an ACTUAL death — a death-prevented lethal hit takes the
            // wouldBeLethal && lethalDamagePrevented branch above instead (health clamped to 1, isDead never
            // set). DamageNumberManager owns the actual message (a random pick from its own
            // _deathStatusMessages) — see StatusPopupKind.Death's own doc comment. TakeDamage's own
            // `if (currentState.isDead) return;` guard at the top means this whole method (and therefore
            // this call) can never run again for this player, so this can't double-fire.
            NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Death);
        }

        // Only fires if this same hit didn't also kill the player — a lethal hit shouldn't trigger a
        // "shield broke" reaction (invincibility, an AOE burst, etc.) on a player who's already dead.
        if (shieldJustBroke && !currentState.isDead)
            _onShieldBreak.Invoke();

        // Same "not on the killing blow" exclusion as _onShieldBreak above.
        if (totalDamageTaken > 0f && !currentState.isDead)
            _onDamaged.Invoke(new DamageTakenData
            {
                preMitigationDamage = preMitigationDamage,
                postMitigationDamage = postMitigationDamage
            });

        // Player-damage-number popup — deliberately NOT gated on !currentState.isDead like _onShieldBreak/
        // _onDamaged above (those are gameplay-reaction events that shouldn't fire on a hit that already
        // killed the player; this is pure visual feedback, and should show on the killing blow too, exact
        // same convention EnemyBasePD.TriggerHitFeedback's own damage number already follows for enemies).
        // Plain before/after diff (see shieldBeforeHit/healthBeforeHit's own doc comment) — correct
        // regardless of which branch above actually ran.
        float shieldDamageDealt = shieldBeforeHit - currentState.shield;
        float healthDamageDealt = healthBeforeHit - currentState.health;
        if (shieldDamageDealt > 0f || healthDamageDealt > 0f)
            _onPlayerDamageNumber.Invoke(new PlayerDamageNumberData
            {
                shieldDamage = shieldDamageDealt,
                healthDamage = healthDamageDealt
            });
    }

    // Only called by Heal() below — the natural health-regen tick in Simulate() deliberately reads HealPower
    // directly instead (see its own comment for why it skips crit/luck/Highroll entirely), it does NOT go
    // through this. Every Heal()-routed heal rolls against CritChance for a "crit heal", always rolled, not
    // gated behind any enable flag, since an un-augmented CritHealPower sitting at its neutral 1f baseline
    // makes a successful roll indistinguishable from a normal heal anyway (see HealPower/CritHealPower's own
    // doc comment). isCrit is surfaced to the caller so HealData.isCrit (and therefore the healing number's
    // own cosmetic "!" — see DamageNumberManager.SpawnPlayerHeal) reflects whichever heals actually crit.
    private float RollHealMultiplier(out bool isCrit)
    {
        isCrit = GameRandom.instance.NextFloat() < CritChance;
        return isCrit ? (HealPower + CritHealPower - 1f) : HealPower;
    }

    // amount gets scaled by RollHealMultiplier() (crit-heal chance/power) BEFORE being applied/clamped to
    // MaxHealth. No luck-variance roll (2026-08-12: removed per explicit design call — heals no longer roll
    // luck at all, unlike damage; see feedback_all_damage_rolls_luck memory for the updated scope). Callers
    // should still pass the RAW amount — RollHealMultiplier() is this method's own job, not the caller's.
    public void Heal(float amount)
    {
        if (currentState.isDead) return;
        float multiplier = RollHealMultiplier(out bool isCrit);
        float healed = Mathf.Min(amount * multiplier, MaxHealth - currentState.health);
        if (healed <= 0f) return;
        currentState.health += healed;
        _onHeal.Invoke(new HealData { amount = healed, isCrit = isCrit });
    }

    // Same shape as Heal() minus RollHealMultiplier() — for heals RECEIVED from an ally's own effect (e.g.
    // OnStrikeHealAllyAugment), deliberately NOT scaled by the RECEIVER's own HealPower/CritHealPower. Heal()
    // being instance-scoped means a normal Heal() call already only ever applies whichever character it's
    // invoked on, never the giver's — the risk this avoids is a receiver's OWN stacked HealPower separately
    // amplifying every ally-sourced heal they happen to receive, on top of it already amplifying their own
    // self-heals/regen, which could compound well past intended value with 4 players all stacking HealPower
    // and all running heal-ally effects at once. Still fires _onHeal — this is still a genuine heal as far as
    // anything reacting to that event is concerned, only the HealPower/CritHealPower scaling is skipped.
    //
    // healer identifies who granted the heal so NotifyAllyHealGranted below can credit them — required, not
    // optional, an ally heal with no identifiable healer doesn't make sense.
    public void HealFromAlly(float amount, CharacterStatsPD healer)
    {
        if (currentState.isDead) return;
        float healed = Mathf.Min(amount, MaxHealth - currentState.health);
        if (healed <= 0f) return;
        currentState.health += healed;
        // Never a crit — no RollHealMultiplier() call here, see this method's own class doc comment for why.
        _onHeal.Invoke(new HealData { amount = healed, isCrit = false });
        // Notify the HEALER's own Stats too, with the real post-clamp amount — see NotifyAllyHealGranted's
        // own doc comment. Deliberately a SEPARATE event from _onHeal above (fired on a different instance):
        // _onHeal is "I was healed", _onAllyHealGranted is "I healed someone else".
        if (healer != null) healer.NotifyAllyHealGranted(healed);
    }

    // Same clamped-gain shape as Heal(), for Energy instead of Health — no _onXInvoke since nothing
    // currently needs to react to an energy gain in progress the way CriticalHealingAugment reacts to heals.
    public void GainEnergy(float amount)
    {
        if (currentState.isDead) return;
        float gained = Mathf.Min(amount, MaxEnergy - currentState.energy);
        if (gained <= 0f) return;
        currentState.energy += gained;
    }

    public void Revive(float healthPercent = 0.5f)
    {
        if (!currentState.isDead) return;

        currentState.isDead = false;
        currentState.health = MaxHealth * healthPercent;
        currentState.shield = MaxShield;
        currentState.energy = MaxEnergy;
        currentState.currentReviveTime += ReviveTimePenalty;
        RefreshAnyValidTargetExists();
        GrantInvincibility(2f);
    }
}
