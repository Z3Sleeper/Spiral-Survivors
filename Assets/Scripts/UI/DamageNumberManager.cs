using System;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

// Stacking rework (see feedback thread this was designed from): the moment a hit lands, its damage number
// prints IMMEDIATELY and locks to a fixed position relative to the enemy — no arc animation, no lifetime
// countdown yet. Any further hit from the SAME group (see below) landing within _stackWindow of the last one
// ADDS onto that same number (sliding window: each addition also resets the countdown) instead of printing
// its own. Once the window elapses with no further same-group hit, the number is "released" into its normal
// arc/fade/despawn animation from wherever it's currently sitting.
//
// Grouping is two-tiered:
// - "Stack" = (enemy, attacking player) — the vertical arrangement the group(s) below land in on the same
//   enemy from the same attacker.
// - "Group" = (stack, isPriority) — the actual accumulator every hit within the window feeds into. Enemy
//   damage numbers collapse down to just two possible groups per stack (2026-08-21, replacing the old
//   per-DamageSource bucketing): a "priority" bucket for real weapon-shaped hits (Weapon, PhantomBlade, AoE
//   Effects casts, etc.) and a "nonpriority" bucket for everything else (onhit-effect noise, KillLaunch) —
//   there's no futureproof way to keep every distinct source its own ever-taller stack once a fight has
//   enough concurrent effects going, so priority/nonpriority is the whole taxonomy now, not a per-source one.
// Every additional group's number stacks ON TOP of the previous ones within its stack, at _stackHeightStep
// world-Y per slot. Priority groups always claim the bottom slot (height 0): if a fresh priority group is
// created while a nonpriority group is still locked on the same stack, that nonpriority group is bumped up
// by one slot to make room. A released group's own slot is simply freed (not renumbered/backfilled) — see
// DamageNumber.Release's own doc comment for why only the still-locked, pre-animation stack ever reorders.
//
// Player Damage/Heal (2026-08-18/19, priority/nonpriority grouping added 2026-08-21) reuse this same
// stack-height machinery (see SpawnPlayerDamage/SpawnPlayerHeal) but diverge from the ENEMY behavior above in
// three ways: release is pop-instantly-then-throttle-HIDDEN, not sliding-while-visible — a group that hasn't
// popped in at least the window prints and releases IMMEDIATELY (no accumulation phase at all); a group
// popping again sooner instead accumulates into a completely invisible pending record (no DamageNumber exists
// yet at all) and only actually spawns — showing the combined total and immediately releasing in the same
// moment — once the window has passed since that group's own last pop, no matter how many more hits silently
// merge in before then (see _playerLastPopTime's own doc comment, and PlayerPendingGroup's for why nothing is
// ever shown mid-accumulation, unlike enemy Spawn()'s own always-visible locked number); the release
// animation itself is status-popup-style (straight-up drift + tilt, not an arc — numbers on players were
// reported hard to read with the lateral drift); and Player Damage additionally splits by PlayerDamagePool
// (Shield/Health never merge into one number, even both being priority) — see each method's own doc comment
// for specifics.
public class DamageNumberManager : MonoBehaviour
{
    public static DamageNumberManager instance;

    // Known "default" status kinds — this class owns and resolves the actual display text (and, where
    // relevant, color) for anything other than Custom, so a caller (e.g. CharacterStatsPD.StartBerserkSurge)
    // just says WHAT happened via CharacterStatsPD.NotifyStatusPopup rather than hardcoding presentation
    // details itself. Custom is the escape hatch for a one-off message that doesn't need a repeatable
    // default — the caller supplies the literal string (and optionally a color override) itself in that
    // case. See ResolveStatusMessage/ResolveStatusColor.
    public enum StatusPopupKind
    {
        Custom,
        Berserk,
        Death,
        DeathPrevented,
        InvisibleStarted,
        InvisibleEnded,
        Taunt,
        Stun,
        Interrupted,
        Slowed,
        // Enemy-only (2026-08-19) — an execute kill (EnemyBasePD.TakeDamage's own executeThreshold branch),
        // not a normal kill. See EnemyBasePD.TakeDamage's own NotifyStatusPopup call for where this fires.
        Executed,
        // Player-only (2026-08-21) — an enemy hit landed while dash-movement invincibility blocked it. See
        // CharacterStatsPD.NotifyDodge's own doc comment for the exact 3-condition contract.
        Dodged,
    }

    // The two pools a player can take damage to — Shield always purple, Health always white (see
    // _shieldDamageColor/_healthDamageColor). Orthogonal to isPriority (see class doc comment) — Shield and
    // Health never merge into one number even when both are priority. See SpawnPlayerDamage.
    public enum PlayerDamagePool
    {
        Shield,
        Health,
    }

    // Shared by every popup type — one pool of pre-warmed DamageNumber instances, drawn from regardless of
    // what a future status-popup spawn path ends up printing on them.
    [SerializeField] private DamageNumber _prefab;
    [SerializeField] private int _poolSize = 30;

    [Header("Size Scaling")]
    // Shared rate every size-scaled spawn-Y-offset site (enemy DN, player DN, enemy status, player status)
    // applies a target's own size bonus at — see SizeOffsetMultiplier's own doc comment for the full
    // formula. 0.5 = half rate (the current tuned default); 0 = no size scaling at all (offset pinned to
    // the base spawnYOffset regardless of size); 1 = the old straight-multiply behavior (full rate, what
    // put numbers too high on bosses before this was split out).
    [SerializeField] private float _sizeScalingRate = 0.5f;

    [Header("Damage")]
    // Enemy-only (2026-08-19) — Player Damage/Heal have their own dedicated _playerSpawnYOffset below now,
    // no longer sharing this one (they used to, deliberately, back when this field was introduced — see
    // _playerSpawnYOffset's own doc comment for why that changed). Straight-multiplied by source.Size at
    // the actual spawn site (Spawn()) — replaces the old per-enemy manual sizeYOffset Inspector field
    // (removed 2026-08-19, see EnemyBasePD's own doc comment on the removal) with automatic scaling, same
    // convention Player Damage/Heal already established via bonusSizePercent.
    [SerializeField] private float _spawnYOffset = 1.5f;
    // Not applied at spawn — handed to DamageNumber.InitLocked()/Release() as the X distance to arc toward
    // once released (see DamageNumber.Animate()). Magnitude is randomized per NEW group (Random.Range(min,
    // max)); the SIGN alternates per stack (see _goRight) so consecutive groups on the same stack visibly
    // separate instead of risking landing on the same side twice in a row.
    [SerializeField] private float _minSpawnXJitter = 1f;
    [SerializeField] private float _maxSpawnXJitter = 1.5f;
    // The tight same-source stacking window — see class doc comment. Sliding: every additional same-source
    // hit resets this back to the full value, so a same-source burst only starts animating once hits
    // actually stop arriving.
    [SerializeField] private float _stackWindow = 0.05f;
    // World-Y spacing between simultaneously-locked groups on the same stack — see class doc comment.
    [SerializeField] private float _stackHeightStep = 1f;
    // Colors live here (not on DamageNumber) so every damage-number spawn site just reports what happened
    // (isCrit/isHighroll) and this manager owns how that's presented — same "manager resolves kind/state
    // into presentation, DamageNumber just plays it back" convention every other popup type (Player Damage/
    // Heal/Status) already follows. See ResolveEnemyDamageColor.
    [SerializeField] private Color _normalDamageColor = Color.white;
    [SerializeField] private Color _critDamageColor = Color.red;
    [SerializeField] private Color _highrollDamageColor = new(0.2f, 0.9f, 0.85f);
    [SerializeField] private Color _critHighrollDamageColor = new(1f, 0.4f, 0.85f);

    [Header("Player Damage")]
    // Own dedicated spawn height (2026-08-19) — used by BOTH Player Damage and Player Heal (see
    // SpawnAndReleasePlayerNumber, the single spawn choke point both funnel through), no longer sharing
    // Damage's own _spawnYOffset above. Deliberately higher than the enemy default — player numbers sit
    // above the player's own health bar now, per explicit request, rather than roughly chest-height like an
    // enemy hit. _playerStatusSpawnYOffset below is tuned a step higher still, so a player's status popups
    // (Stun/Slowed/etc.) keep reading above their damage/heal numbers instead of colliding with them. This
    // base value is straight-multiplied by the target's own bonusSizePercent at each actual spawn site
    // (SpawnAndReleasePlayerNumber) — a bigger player needs a taller offset to clear their own bigger model/
    // health bar, same reasoning _playerStatusSpawnYOffset's own spawn site applies it for.
    [SerializeField] private float _playerSpawnYOffset = 3f;
    // Fixed per pool rather than rolled per-hit like Damage's own crit/highroll colors — a player only ever
    // takes Shield or Health damage, no larger taxonomy to color-code. Stack window/stack height step are
    // still shared with Damage above (SAME _stackWindow, no multiplier — see SpawnPlayerDamage's own doc
    // comment for the "every current enemy hit is the same default source, futureproofed for a distinct one
    // later" rule), but spawn jitter/rotation and the release animation itself are NOT (2026-08-18) — see
    // SpawnPlayerDamage/DamageNumber.InitLockedPlayer for the status-popup-style presentation this now uses.
    [SerializeField] private Color _shieldDamageColor = new(0.6f, 0.3f, 0.9f);
    [SerializeField] private Color _healthDamageColor = Color.white;

    [Header("Player Heal")]
    // Single color regardless of source (Heal()/HealFromAlly() both route through the same _onHeal relay —
    // see CharacterStatsPD.OnHealNumber's own doc comment). Source-separated stacking (2026-08-18, see
    // _healStackWindowMultiplier and SpawnPlayerHeal's own doc comment) — no longer "just one accumulator
    // per player" the way it used to be.
    [SerializeField] private Color _healColor = new(0.3f, 0.9f, 0.3f);
    // Healing gets a MUCH longer stack window than damage — healing numbers merging aggressively (many small
    // ticks from the same source folding into one slowly-growing number) reads far better than damage's own
    // tight 0.05s window, which is tuned for "distinct hits landing close together," not "a steady trickle
    // from one ongoing effect." Multiplies _stackWindow rather than a separate flat duration, so retuning
    // Damage's own window still keeps Heal's proportionally in sync.
    [SerializeField] private float _healStackWindowMultiplier = 5f;
    private float HealStackWindow => _stackWindow * _healStackWindowMultiplier;

    [Header("Status")]
    // Enemy/location status only now (2026-08-19) — see SpawnStatusOnEnemy/SpawnStatusAtLocation and
    // DamageNumber.InitStatus's own doc comment for the drift/fade shape built on top of this. Player-
    // targeted status (SpawnStatusOnPlayer) uses _playerStatusSpawnYOffset below instead — see its own doc
    // comment for why the two split apart.
    [SerializeField] private float _statusSpawnYOffset = 2f;
    // Player-targeted status only (SpawnStatusOnPlayer) — split out from _statusSpawnYOffset above
    // (2026-08-19) specifically to stay a step above _playerSpawnYOffset (Player Damage/Heal's own new
    // height, see its own doc comment) now that the two are no longer both anchored to the old shared
    // 1.5f/2f pair — without this, a player's Stun/Slowed/etc. popup would spawn BELOW their own damage/heal
    // numbers instead of above them. Enemy status and location-based status are unaffected, still using
    // _statusSpawnYOffset. Straight-multiplied by the target's own bonusSizePercent at the actual spawn site
    // (ExecuteStatusPopup's playerTarget branch) — same "a bigger player needs a taller offset" reasoning as
    // _playerSpawnYOffset's own doc comment.
    [SerializeField] private float _playerStatusSpawnYOffset = 4f;
    // Lateral spawn jitter — magnitude randomized per popup (Random.Range(min, max)), sign random left/right
    // (no alternating-per-stack concept here, unlike Damage's _goRight, since status popups don't share a
    // stack). See RollStatusXOffset.
    [SerializeField] private float _statusMinSpawnXJitter = 0f;
    [SerializeField] private float _statusMaxSpawnXJitter = 1f;
    // The Z-axis tilt applied when a popup rolls the maximum possible X jitter — scales down proportionally
    // for a smaller rolled magnitude. See RollStatusXOffset.
    [SerializeField] private float _statusMaxSpawnZRot = 25f;
    // Minimum time a status popup gets to itself before ANOTHER popup of ANY kind is allowed to show on the
    // SAME target — see RequestStatusPopup's own doc comment for the exact showtime/dedupe/queue rule.
    [SerializeField] private float _statusShowtimeDuration = 0.05f;

    [Header("Berserk")]
    [SerializeField] private string _berserkStatusMessage = "Berserk!";
    [SerializeField] private Color _berserkStatusColor = new(1f, 0.85f, 0.1f);

    [Header("Death")]
    // One entry picked at random per popup — see ResolveStatusMessage. Empty/unconfigured is handled
    // safely (resolves to "", which the empty-message guard in SpawnStatusOnPlayer/SpawnStatusAtLocation
    // then no-ops on) rather than throwing.
    [SerializeField] private string[] _deathStatusMessages;
    [SerializeField] private Color _deathStatusColor = Color.white;

    [Header("Death Prevented")]
    [SerializeField] private string _deathPreventedStatusMessage = "Death Prevented!";
    [SerializeField] private Color _deathPreventedStatusColor = Color.white;

    [Header("Invisible Started")]
    [SerializeField] private string _invisibleStartedStatusMessage = "Invisible";
    [SerializeField] private Color _invisibleStartedStatusColor = Color.white;

    [Header("Invisible Ended")]
    [SerializeField] private string _invisibleEndedStatusMessage = "Revealed";
    [SerializeField] private Color _invisibleEndedStatusColor = Color.white;

    [Header("Taunt")]
    [SerializeField] private string _tauntStatusMessage = "Taunting";
    [SerializeField] private Color _tauntStatusColor = Color.white;

    [Header("Stun")]
    [SerializeField] private string _stunStatusMessage = "Stunned";
    [SerializeField] private Color _stunStatusColor = Color.yellow;

    [Header("Interrupted")]
    [SerializeField] private string _interruptedStatusMessage = "Interrupted";
    [SerializeField] private Color _interruptedStatusColor = Color.white;

    [Header("Slowed")]
    [SerializeField] private string _slowedStatusMessage = "Slowed";
    [SerializeField] private Color _slowedStatusColor = Color.cyan;

    [Header("Executed")]
    [SerializeField] private string _executedStatusMessage = "Executed!";
    [SerializeField] private Color _executedStatusColor = new(1f, 0.5f, 0f);

    [Header("Dodged")]
    [SerializeField] private string _dodgedStatusMessage = "Dodged!";
    [SerializeField] private Color _dodgedStatusColor = Color.white;

    // Shared — the pool itself holds pooled DamageNumber instances regardless of popup type.
    private readonly Queue<DamageNumber> _pool = new();

    // Damage — the stacking/grouping accumulation machinery described in the class doc comment up top.
    private class LockedGroup
    {
        public DamageNumber number;
        public IEnemyPD enemy;
        public int enemyGeneration;
        public float amount;
        public bool isCrit;
        public bool isHighroll;
        public float windowRemaining;
        public int stackKey;
        public int height;
    }

    // Keyed by CombineGroupKey(stackKey, source) — the accumulator every same-source hit within _stackWindow
    // feeds into. See class doc comment.
    private readonly Dictionary<int, LockedGroup> _lockedGroups = new();
    // Keyed by stackKey (enemy + attacking player) — the ordered set of groupKeys currently locked on that
    // stack, bottom to top, used purely to assign each new group's height. A released group is removed from
    // here but the remaining entries are NOT renumbered/compacted — only inserting a fresh Weapon group
    // forces a reflow (see class doc comment).
    private readonly Dictionary<int, List<int>> _stackOrder = new();
    // One alternating left/right toggle per stack, flipped every time a NEW group is created on it.
    private readonly Dictionary<int, bool> _goRight = new();
    // Reused every Update() to snapshot which groupKeys expired this frame — can't remove from _lockedGroups
    // while enumerating it directly.
    private readonly List<int> _expiredScratch = new();

    // Player Damage/Heal — a PENDING accumulator, not a visible number (2026-08-19 rework — a throttled
    // group used to create+show its DamageNumber immediately and just update its displayed total in place
    // while waiting out the window, which read as "the number is visibly growing before it pops," not what
    // was wanted). No DamageNumber reference here at all anymore — nothing is created/shown for a group
    // still accumulating. See SpawnPlayerDamage/SpawnPlayerHeal's own doc comments for the instant-vs-
    // throttled decision, and PopPendingGroup for where the actual spawn+immediate-release finally happens
    // once a throttled group's window elapses. isHeal/pool carry enough of the original call's own context
    // to resolve the right color/sign at that later pop time, since nothing was resolved/baked in up front.
    // No generation tracking — CharacterStatsPD instances are never pooled/reused for a different player
    // mid-match (see AllyStrikeData's own doc comment in WeaponBasePD.cs for the same reasoning already
    // established there), so a plain null check is enough to detect a gone target. No isHighroll (unlike
    // enemy LockedGroup) — color is fixed per pool/kind, not rolled per-hit; isCrit is only ever meaningful/
    // set for Player Heal groups (a cosmetic "!" suffix — see SpawnPlayerHeal), left false and unused for
    // Player Damage groups. No priority-bottom-slot reflow either — plain insertion-order heights are enough
    // (at most 2 Heal groups (priority/nonpriority) plus up to 2 Damage groups per pool (Shield/Health) ever
    // concurrent on one player — still just a flat list, no special-cased slot). Kept as a fully separate set
    // of dictionaries (rather
    // than shoehorned into the enemy ones above) specifically because of that IEnemyPD/CharacterStatsPD type
    // mismatch — see DamageNumber's own _target/_playerTarget split for the matching reasoning on the
    // presentation side.
    private class PlayerPendingGroup
    {
        public CharacterStatsPD player;
        public float amount;
        public bool isHeal;
        public PlayerDamagePool pool;
        public bool isCrit;
        public float windowRemaining;
        public int stackKey;
        public int height;
    }

    private readonly Dictionary<int, PlayerPendingGroup> _playerPendingGroups = new();
    private readonly Dictionary<int, List<int>> _playerStackOrder = new();
    private readonly List<int> _playerExpiredScratch = new();
    // Real Time.time of this groupKey's own last pop — persists even after the group itself is gone
    // from _playerPendingGroups (2026-08-19, see SpawnPlayerDamage/SpawnPlayerHeal's own doc comments for
    // the pop-instantly-then-throttle rule this drives). Never pruned — bounded by however many distinct
    // (player, pool/source) combos have ever fired this session, same non-pruning already accepted
    // elsewhere (e.g. _lockedGroups never removes a stale enemy's own past entries either).
    private readonly Dictionary<int, float> _playerLastPopTime = new();

    // Status — the showtime/queue gate (see RequestStatusPopup's own doc comment for the full rule). Keyed
    // by targetKey (player GetInstanceID() or enemy ObjectId.GetHashCode()) — one state per target, shared
    // across every StatusPopupKind rather than a separate window per kind.
    private class StatusShowtimeState
    {
        // >0 while some popup is "showing" (blocking any other popup on this target) — ticked down and
        // pruned in Update() below.
        public float windowRemaining;
        // Which kind currently owns the active window — dedupe target for an incoming same-kind request.
        public StatusPopupKind showingKind;
        // FIFO — dequeued one at a time as each window expires. Dedupe (see RequestStatusPopup) already
        // guarantees no two queued entries (or the queue vs. the currently-showing kind) ever share a kind,
        // Custom excepted, so this never grows past roughly "how many distinct kinds got requested during
        // one blocked window" (+ arbitrarily many Customs, which are never deduped against each other).
        public readonly Queue<PendingStatusPopup> queue = new();
    }

    // Everything SpawnStatusOnPlayer/SpawnStatusOnEnemy need to actually execute a popup later, once its
    // turn comes up out of the queue — playerTarget/enemyTarget are mutually exclusive (exactly one is set,
    // matching which of the two public methods originally received this request).
    private class PendingStatusPopup
    {
        public StatusPopupKind kind;
        public CharacterStatsPD playerTarget;
        public IEnemyPD enemyTarget;
        public string customMessage;
        public Color? colorOverride;
        public float durationMultiplier;
    }

    private readonly Dictionary<int, StatusShowtimeState> _statusShowtimeStates = new();
    private readonly List<int> _statusExpiredScratch = new();

    private void Awake()
    {
        instance = this;
        for (int i = 0; i < _poolSize; i++)
        {
            var num = Instantiate(_prefab, transform);
            num.gameObject.SetActive(false);
            _pool.Enqueue(num);
        }
    }

    private void Update()
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        if (_lockedGroups.Count > 0)
        {
            _expiredScratch.Clear();
            foreach (var kvp in _lockedGroups)
            {
                var group = kvp.Value;
                // Target gone (destroyed or pooled/reused for a different enemy) — force-release immediately
                // rather than waiting out the rest of the window on an enemy that no longer exists.
                if (!IsStillTheSameEnemy(group.enemy, group.enemyGeneration))
                {
                    _expiredScratch.Add(kvp.Key);
                    continue;
                }
                group.windowRemaining -= Time.deltaTime;
                if (group.windowRemaining <= 0f) _expiredScratch.Add(kvp.Key);
            }
            for (int i = 0; i < _expiredScratch.Count; i++)
                ReleaseGroup(_expiredScratch[i]);
        }

        // Player Damage/Heal — same tick/expire shape as the enemy loop above, see PlayerPendingGroup's own
        // doc comment for why this is a separate dictionary/loop rather than folded into the one above.
        if (_playerPendingGroups.Count > 0)
        {
            _playerExpiredScratch.Clear();
            foreach (var kvp in _playerPendingGroups)
            {
                var group = kvp.Value;
                // Plain null check (not IsStillTheSameEnemy) — see PlayerPendingGroup's own doc comment for
                // why CharacterStatsPD doesn't need the enemy's generation-tracking dance.
                if (group.player == null)
                {
                    _playerExpiredScratch.Add(kvp.Key);
                    continue;
                }
                group.windowRemaining -= Time.deltaTime;
                if (group.windowRemaining <= 0f) _playerExpiredScratch.Add(kvp.Key);
            }
            for (int i = 0; i < _playerExpiredScratch.Count; i++)
                PopPendingGroup(_playerExpiredScratch[i]);
        }

        // Status showtime windows — count down; once a window expires, either pop the next queued popup
        // (starting a fresh window for it) or, if nothing's queued, this target goes fully idle and gets
        // pruned. No target-staleness check needed the way the two loops above have (this only ever tracks
        // a duration + a small pending queue, not a live number that needs to keep following anything —
        // ExecuteStatusPopup below handles a since-destroyed enemy target on its own when it's each pending
        // item's own turn to actually spawn).
        if (_statusShowtimeStates.Count > 0)
        {
            _statusExpiredScratch.Clear();
            foreach (var kvp in _statusShowtimeStates)
            {
                var state = kvp.Value;
                state.windowRemaining -= Time.deltaTime;
                if (state.windowRemaining > 0f) continue;

                if (state.queue.Count > 0)
                {
                    var next = state.queue.Dequeue();
                    state.showingKind = next.kind;
                    state.windowRemaining = _statusShowtimeDuration;
                    ExecuteStatusPopup(next);
                }
                else
                {
                    _statusExpiredScratch.Add(kvp.Key);
                }
            }
            for (int i = 0; i < _statusExpiredScratch.Count; i++)
                _statusShowtimeStates.Remove(_statusExpiredScratch[i]);
        }
    }

    // stackKey identifies the (enemy, attacking player) vertical arrangement this hit belongs to — computed
    // by the caller (EnemyBasePD.ShowDamageNumber, same HashCode.Combine(GetInstanceID(), attackerOwner)
    // formula as before) since it's the one that already has direct access to GetInstanceID(). isPriority is
    // the actual accumulation bucket within that stack — see class doc comment for the full rule. Spawn
    // height is _spawnYOffset scaled by HALF of source.Size's own bonus over 1f (2026-08-19 fix — was
    // straight-multiplied by the full source.Size, which put numbers noticeably too high on big/boss
    // enemies; a model scales uniformly around its own center, so only half of a size bonus actually moves
    // the visual "top" upward, the other half grows downward) — replaces the old per-enemy manual
    // sizeYOffset/sizeZOffset nudge fields (see EnemyBasePD's own doc comment on their removal). Same
    // "bigger target needs a taller offset" convention Player Damage/Heal already established via
    // bonusSizePercent — this is BELOW the per-group stack height, same as before.
    // Shared by every size-scaled spawn-Y-offset site (enemy DN, player DN, enemy status, player status) — a
    // model scales uniformly around its own center, so only a FRACTION (_sizeScalingRate, tunable in the
    // Inspector — see its own doc comment) of a size value's own bonus over the 1f baseline actually moves
    // the visual "top" upward (the rest grows downward); straight-multiplying by the raw size value (the old
    // behavior, equivalent to _sizeScalingRate = 1) put numbers noticeably too high on anything bigger than
    // default, most visible on bosses. rawSize is whatever the source's own 1f-baseline size multiplier is
    // (EnemyBasePD.Size / CharacterStatsPD.bonusSizePercent) — both already use the same "1f = default"
    // convention.
    private float SizeOffsetMultiplier(float rawSize) => 1f + (rawSize - 1f) * _sizeScalingRate;

    public void Spawn(float amount, IEnemyPD source, int stackKey, bool isCrit = false, bool isHighroll = false, PlayerID attackerOwner = default, bool isPriority = false)
    {
        if (amount <= 0f || IsDestroyed(source)) return;

        if (!attackerOwner.Equals(default(PlayerID)))
        {
            bool isOwn = CharacterStatsPD.All.TryGetValue(attackerOwner, out var stats) && stats.isOwner;
            if (!isOwn && !Settings.SeeOtherPlayerDamage) return;
        }

        int groupKey = CombineGroupKey(stackKey, isPriority);

        // Same-group hit within the window — fold into the existing locked number instead of printing a
        // new one, and slide the window back out to the full value.
        if (_lockedGroups.TryGetValue(groupKey, out var existing))
        {
            existing.amount += amount;
            existing.isCrit |= isCrit;
            existing.isHighroll |= isHighroll;
            existing.windowRemaining = _stackWindow;
            existing.number.SetAmount(existing.amount, existing.isCrit, existing.isHighroll, ResolveEnemyDamageColor(existing.isCrit, existing.isHighroll));
            return;
        }

        if (!_stackOrder.TryGetValue(stackKey, out var order))
        {
            order = new List<int>();
            _stackOrder[stackKey] = order;
        }

        int height;
        if (isPriority)
        {
            // Priority always claims the bottom slot — every other currently-locked group on this stack
            // bumps up by one to make room (see class doc comment). With only two possible groups per stack
            // now, "every other" is at most the one nonpriority group.
            for (int i = 0; i < order.Count; i++)
                if (_lockedGroups.TryGetValue(order[i], out var other))
                    other.number.SetHeight(++other.height * _stackHeightStep);
            height = 0;
            order.Insert(0, groupKey);
        }
        else
        {
            height = order.Count;
            order.Add(groupKey);
        }

        bool goRight = !_goRight.TryGetValue(stackKey, out bool last) || !last;
        _goRight[stackKey] = goRight;
        float magnitude = UnityEngine.Random.Range(_minSpawnXJitter, _maxSpawnXJitter);
        float xOffset = goRight ? magnitude : -magnitude;

        var num = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(_prefab, transform);
        Vector3 pos = source.Visuals.position + new Vector3(0f, _spawnYOffset * SizeOffsetMultiplier(source.Size), 0f);
        num.InitLocked(amount, pos, height * _stackHeightStep, xOffset, source, this, isCrit, isHighroll, isPriority, ResolveEnemyDamageColor(isCrit, isHighroll));

        _lockedGroups[groupKey] = new LockedGroup
        {
            number = num,
            enemy = source,
            enemyGeneration = source.SpawnGeneration,
            amount = amount,
            isCrit = isCrit,
            isHighroll = isHighroll,
            windowRemaining = _stackWindow,
            stackKey = stackKey,
            height = height,
        };
    }

    private void ReleaseGroup(int groupKey)
    {
        if (!_lockedGroups.TryGetValue(groupKey, out var group)) return;
        _lockedGroups.Remove(groupKey);

        if (_stackOrder.TryGetValue(group.stackKey, out var order))
        {
            order.Remove(groupKey);
            // Prune fully-emptied stacks so a very long session with many distinct enemies doesn't leave
            // behind an ever-growing pile of empty entries.
            if (order.Count == 0)
            {
                _stackOrder.Remove(group.stackKey);
                _goRight.Remove(group.stackKey);
            }
        }

        group.number.Release();
    }

    private static int CombineGroupKey(int stackKey, bool isPriority) => HashCode.Combine(stackKey, isPriority);

    private Color ResolveEnemyDamageColor(bool isCrit, bool isHighroll)
    {
        if (isCrit && isHighroll) return _critHighrollDamageColor;
        if (isHighroll) return _highrollDamageColor;
        if (isCrit) return _critDamageColor;
        return _normalDamageColor;
    }

    // ── Player Damage ──────────────────────────────────────────────────────
    // Same _stackHeightStep stacking convention as Spawn() above — just keyed by CharacterStatsPD/
    // PlayerDamagePool/isPriority instead of IEnemyPD/isPriority — with a release model fully reworked
    // (2026-08-19, replacing that day's earlier attempts): a group that hasn't popped in at least
    // _stackWindow seconds (or has never popped at all) pops INSTANTLY — spawns and immediately starts its
    // release animation in the same call, nothing shown before that. A group that pops again sooner than that
    // instead starts (or adds to) a PENDING accumulator that stays completely invisible/unspawned — no
    // DamageNumber created at all — until _stackWindow seconds have passed since that group's own LAST pop,
    // at which point it finally spawns showing the combined total and immediately releases, same as an
    // instant pop. Every hit merging into a pending group before its pop just grows the total, never resets
    // the clock or shows anything early. This means an isolated, infrequent hit reads instantly (no
    // artificial delay before it even appears), while a rapid burst from one ongoing source still throttles
    // down to at most one visible pop per window instead of spamming a fresh number per tick OR visibly
    // growing in place while waiting. See _playerLastPopTime's own doc comment for the persistent per-group
    // timestamp this reads/writes, and PopPendingGroup for where a throttled group's own pop actually happens.
    //
    // The RELEASE presentation also diverges from Spawn() (2026-08-18): status-popup-style spawn jitter/
    // rotation (RollStatusXOffset, same roll a status popup uses) and a straight-up-drift release animation
    // (DamageNumber.InitLockedPlayer/ReleasePlayer) instead of Damage's own arc — numbers on players were
    // reported hard to read with the arc's lateral drift, unlike enemy numbers which keep it. Gated by
    // Settings.SeeOtherPlayerDamage exactly like Spawn() already is — these ARE damage numbers, not status
    // popups, same visibility rule applies. Always full-size/priority text (see DamageNumber.InitLockedPlayer)
    // — no crit/highroll concept exists for damage a player receives, unlike Spawn()'s own isCrit/isHighroll.
    //
    // isPriority defaults to true — every current caller (all real enemy contact/hit damage) is priority.
    // pool (Shield/Health) stays its own separate group dimension regardless of isPriority — see
    // PlayerDamagePool's own doc comment. Reserved for a future environmental/"other" damage source that
    // wants to fall into the nonpriority bucket instead of merging with ordinary enemy hits, same taxonomy
    // Spawn()'s own isPriority already uses for enemies.
    public void SpawnPlayerDamage(float amount, CharacterStatsPD target, PlayerDamagePool pool, bool isPriority = true)
    {
        if (amount <= 0f || target == null) return;

        bool isOwn = target.isOwner;
        if (!isOwn && !Settings.SeeOtherPlayerDamage) return;

        int stackKey = target.GetInstanceID();
        int groupKey = CombinePlayerGroupKey(stackKey, pool, isPriority);

        // Same-pool-and-priority hit while a group is still pending — fold into it instead of spawning
        // anything. windowRemaining is deliberately left untouched — this group already has its own pop time
        // fixed (see the throttled branch below), so a merge just grows the total without pushing that time
        // back out. No visual update either — nothing has been shown for this group yet.
        if (_playerPendingGroups.TryGetValue(groupKey, out var existing))
        {
            existing.amount += amount;
            return;
        }

        float elapsedSincePop = _playerLastPopTime.TryGetValue(groupKey, out float lastPop) ? Time.time - lastPop : float.MaxValue;
        bool popInstantly = elapsedSincePop >= _stackWindow;

        if (popInstantly)
        {
            if (!_playerStackOrder.TryGetValue(stackKey, out var order))
                order = new List<int>();
            SpawnAndReleasePlayerNumber(target, amount, ResolvePlayerDamageColor(pool), isHeal: false, isCrit: false, order.Count);
            _playerLastPopTime[groupKey] = Time.time;
            return;
        }

        if (!_playerStackOrder.TryGetValue(stackKey, out var pendingOrder))
        {
            pendingOrder = new List<int>();
            _playerStackOrder[stackKey] = pendingOrder;
        }

        // Plain insertion-order height — no Weapon-style bottom-slot reflow needed (see PlayerPendingGroup's
        // own doc comment). Shared with Player Heal's own groups on the same player (see SpawnPlayerHeal),
        // so e.g. a simultaneous heal-while-damaged stacks vertically instead of overlapping once both
        // eventually pop. Reserved now (at pending-creation time), not deferred to the eventual pop, so
        // several different sources pending/popping around the same time still end up spaced apart even
        // though none of them are visible yet.
        int height = pendingOrder.Count;
        pendingOrder.Add(groupKey);

        _playerPendingGroups[groupKey] = new PlayerPendingGroup
        {
            player = target,
            amount = amount,
            isHeal = false,
            pool = pool,
            // Pops exactly _stackWindow seconds after the LAST pop, not a fresh full window from now — see
            // this method's own doc comment.
            windowRemaining = _stackWindow - elapsedSincePop,
            stackKey = stackKey,
            height = height,
        };
    }

    // Player Heal — same _playerPendingGroups/_playerStackOrder machinery/status-style spawn jitter AND the
    // same pop-instantly-then-throttle-hidden release model as Player Damage above (see SpawnPlayerDamage's
    // own doc comment for the full rule), with two deliberate differences of its own:
    // - isPriority grouping only (2026-08-21, replacing the old per-source stacking — every heal now merges
    //   into one of at most two numbers per player, same priority/nonpriority taxonomy Spawn()/
    //   SpawnPlayerDamage use) — defaults to true, since every current heal (Heal()/HealFromAlly(), whatever
    //   augment/ability granted it) is priority. Reserved for a future environmental/"other" heal source that
    //   wants its own separate nonpriority bucket instead of merging with ordinary heals.
    // - HealStackWindow (_stackWindow * _healStackWindowMultiplier), not the plain _stackWindow Damage uses
    //   — healing is a steady trickle from one ongoing effect far more often than damage is, so a much wider
    //   throttle window reads better (see _healStackWindowMultiplier's own doc comment). E.g. a source that's
    //   never healed before pops instantly; healing again 0.05s and 0.1s later (each still within
    //   HealStackWindow of that first pop) both merge into the same pending accumulator, completely invisible
    //   until it itself pops once HealStackWindow seconds have passed since that first pop, at which point it
    //   shows the combined total and immediately releases.
    // Leading "+" via its own dedicated color (_healColor) regardless of source. Gated by
    // Settings.SeeOtherPlayerDamage exactly like Player Damage — still fundamentally a damage-number-shaped
    // popup, same visibility rule. Called from CharacterStatsPD's own _onHeal relay — see OnHealNumber's own
    // doc comment for why that's already scoped to exclude passive health regen for free.
    //
    // TODO: explicit request that an ally-sourced heal (CharacterStatsPD.HealFromAlly — one player healing
    // ANOTHER) should always be visible to everyone, bypassing Settings.SeeOtherPlayerDamage entirely rather
    // than being gated by it like a normal heal/damage number. Still not implementable — nothing passed to
    // this method carries a "was this HealFromAlly at all" bool, so it still can't tell an ally heal apart
    // from a self-heal to skip the gate specifically for that case.
    // isCrit — cosmetic "!" suffix (see DamageNumber.SetPlayerAmount) for a Heal()-rolled crit heal
    // (CriticalHealingAugment). OR-combined across every heal merged into the same pending group within the
    // throttle window, same "shows the styling if ANY hit in the accumulated total qualified" rule Spawn()'s
    // own isCrit/isHighroll merge already uses for enemies. An instantly-popped heal just uses its own isCrit
    // directly (nothing to OR against — it's the only heal in that pop).
    public void SpawnPlayerHeal(float amount, CharacterStatsPD target, bool isCrit = false, bool isPriority = true)
    {
        if (amount <= 0f || target == null) return;

        bool isOwn = target.isOwner;
        if (!isOwn && !Settings.SeeOtherPlayerDamage) return;

        int stackKey = target.GetInstanceID();
        int groupKey = CombinePlayerHealGroupKey(stackKey, isPriority);

        // windowRemaining deliberately untouched on merge — this group's own pop time is already fixed (see
        // the throttled branch below), a merge just grows the total. No visual update either — nothing has
        // been shown for this group yet.
        if (_playerPendingGroups.TryGetValue(groupKey, out var existing))
        {
            existing.amount += amount;
            existing.isCrit |= isCrit;
            return;
        }

        float healWindow = HealStackWindow;
        float elapsedSincePop = _playerLastPopTime.TryGetValue(groupKey, out float lastPop) ? Time.time - lastPop : float.MaxValue;
        bool popInstantly = elapsedSincePop >= healWindow;

        if (popInstantly)
        {
            if (!_playerStackOrder.TryGetValue(stackKey, out var order))
                order = new List<int>();
            SpawnAndReleasePlayerNumber(target, amount, _healColor, isHeal: true, isCrit: isCrit, order.Count);
            _playerLastPopTime[groupKey] = Time.time;
            return;
        }

        if (!_playerStackOrder.TryGetValue(stackKey, out var pendingOrder))
        {
            pendingOrder = new List<int>();
            _playerStackOrder[stackKey] = pendingOrder;
        }

        int height = pendingOrder.Count;
        pendingOrder.Add(groupKey);

        _playerPendingGroups[groupKey] = new PlayerPendingGroup
        {
            player = target,
            amount = amount,
            isHeal = true,
            isCrit = isCrit,
            windowRemaining = healWindow - elapsedSincePop,
            stackKey = stackKey,
            height = height,
        };
    }

    // The actual spawn — rolls jitter, pulls a pooled DamageNumber, shows the given amount, and immediately
    // starts its release (float-away) animation in the same call. Used for BOTH an instant pop (called
    // directly from SpawnPlayerDamage/SpawnPlayerHeal) and a throttled pop (called from PopPendingGroup once
    // a pending group's window elapses) — by the time either one reaches here, there's no distinction left:
    // a number is about to become visible and immediately starts floating away, full stop. height is a slot
    // index (multiplied by _stackHeightStep internally the same way every other spawn site here does).
    private void SpawnAndReleasePlayerNumber(CharacterStatsPD target, float amount, Color color, bool isHeal, bool isCrit, int height)
    {
        float xOffset = RollStatusXOffset(out float zRot);
        var num = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(_prefab, transform);
        // SizeOffsetMultiplier, not a straight bonusSizePercent multiply (2026-08-19 fix — see its own
        // doc comment) — a bigger player has a taller/wider model (and health bar) to clear, so the spawn
        // height still needs to scale up with size, just at half rate since the model scales around its own
        // center.
        Vector3 pos = target.transform.position + new Vector3(xOffset, _playerSpawnYOffset * SizeOffsetMultiplier(target.bonusSizePercent), 0f);
        num.InitLockedPlayer(amount, pos, height * _stackHeightStep, zRot, target, this, color, isHeal, isCrit);
        // ReleasePlayer, not Release (2026-08-18) — Player Damage/Heal release into a straight-up-drift
        // animation now (status-popup style), not Damage's own arc. See DamageNumber.InitLockedPlayer's own
        // doc comment.
        num.ReleasePlayer();
    }

    // Called once a pending group's window elapses (Update()'s own player loop) — resolves its color/sign
    // from the data it was carrying (isHeal/pool, never resolved up front since nothing was shown until now)
    // and hands off to SpawnAndReleasePlayerNumber for the actual spawn+release. A target that went null
    // while still pending is silently discarded instead — there's no live position left to show a number at,
    // and nothing was ever visible for the player to notice disappearing.
    private void PopPendingGroup(int groupKey)
    {
        if (!_playerPendingGroups.TryGetValue(groupKey, out var group)) return;
        _playerPendingGroups.Remove(groupKey);
        // Records the REAL pop moment (not whatever the group's original windowRemaining math scheduled it
        // for) as this source's own last pop time — see _playerLastPopTime's own doc comment for the
        // instant-pop-vs-throttle decision this feeds on this source's NEXT hit.
        _playerLastPopTime[groupKey] = Time.time;

        if (_playerStackOrder.TryGetValue(group.stackKey, out var order))
        {
            order.Remove(groupKey);
            if (order.Count == 0)
                _playerStackOrder.Remove(group.stackKey);
        }

        if (group.player == null) return;

        Color color = group.isHeal ? _healColor : ResolvePlayerDamageColor(group.pool);
        SpawnAndReleasePlayerNumber(group.player, group.amount, color, group.isHeal, group.isCrit, group.height);
    }

    private static int CombinePlayerGroupKey(int stackKey, PlayerDamagePool pool, bool isPriority) => HashCode.Combine(stackKey, (int)pool, isPriority);

    // Distinct salt ("Heal", not sharing CombinePlayerGroupKey's own hashed shape) — collision with a
    // Shield/Health groupKey on the same stackKey is effectively impossible (different hashed value
    // entirely), and even a theoretical collision would only mean an in-progress damage/heal number bumping
    // into each other's accumulation window, not crossed streams between different players.
    private static int CombinePlayerHealGroupKey(int stackKey, bool isPriority) => HashCode.Combine(stackKey, "Heal", isPriority);

    private Color ResolvePlayerDamageColor(PlayerDamagePool pool) => pool switch
    {
        PlayerDamagePool.Shield => _shieldDamageColor,
        _ => _healthDamageColor,
    };

    // Plain `source == null` on an IEnemyPD-typed reference does NOT detect a truly-destroyed Unity object
    // — Unity's overloaded "fake null" equality operator only applies when the compile-time type is
    // UnityEngine.Object (or a subclass); for an interface-typed variable, C# falls back to plain
    // reference equality, which stays "non-null" even after the underlying enemy is destroyed. Casting to
    // Object first routes the check through Unity's real destroyed-check instead. Public/static so
    // DamageNumber can use the exact same check for its own _target.
    public static bool IsDestroyed(IEnemyPD enemy) => enemy == null || (UnityEngine.Object)enemy == null;

    // source stays a valid, non-null reference even after its GameObject gets pooled/recycled for a
    // completely different enemy spawn — SpawnGeneration is the only reliable signal that it's still the
    // SAME logical enemy this entry was originally locked against.
    private static bool IsStillTheSameEnemy(IEnemyPD source, int capturedGeneration) =>
        !IsDestroyed(source) && source.SpawnGeneration == capturedGeneration;

    public void Return(DamageNumber num) => _pool.Enqueue(num);

    // ── Status popups ──────────────────────────────────────────────────────
    // Same "caller's responsibility to only fire from an already resimulation-safe context" contract Spawn()
    // above already has (verified/render-rate, or relayed through an already-safe PredictedEvent listener —
    // see CharacterStatsPD.NotifyStatusPopup for the player-targeted case). No accumulation here, unlike
    // Damage's own merge-into-one-growing-number behavior — see RequestStatusPopup's own doc comment for the
    // showtime/dedupe/queue rule this uses instead.
    //
    // TODO: no visibility gating at all yet — unlike Spawn() above (gated by Settings.SeeOtherPlayerDamage),
    // every client currently always sees every status popup. See Settings.cs's own TODO for the planned
    // separate toggle — once added, gate both methods below on it, same "isOwn || setting" shape Spawn() uses,
    // EXCEPT StatusPopupKind.Death — that one must always stay visible to everyone regardless of the toggle
    // (explicit request), so its resolution needs to skip the gate entirely rather than checking it.

    // Continuously follows target for its whole lifetime, same live-tracking idea as a damage number
    // following its enemy — see DamageNumber.InitStatus's own doc comment for the drift/fade shape.
    // customMessage/colorOverride are only consulted for StatusPopupKind.Custom (or as an explicit override
    // on top of a known kind's own default) — see ResolveStatusMessage/ResolveStatusColor. Doesn't spawn
    // directly — see RequestStatusPopup for the showtime/queue gate every request goes through first.
    public void SpawnStatusOnPlayer(StatusPopupKind kind, CharacterStatsPD target, string customMessage = null, Color? colorOverride = null, float durationMultiplier = 1f)
    {
        if (target == null) return;
        RequestStatusPopup(target.GetInstanceID(), new PendingStatusPopup
        {
            kind = kind,
            playerTarget = target,
            customMessage = customMessage,
            colorOverride = colorOverride,
            durationMultiplier = durationMultiplier,
        });
    }

    // Fixed (X, 0, Z) world location — no target to follow, e.g. a boss telegraphing a mechanic to the whole
    // party rather than calling out one specific player. kind/customMessage/colorOverride — see
    // SpawnStatusOnPlayer's own doc comment. Deliberately does NOT go through RequestStatusPopup's showtime/
    // queue gate — there's no persistent per-call target identity here to key it against (a fresh Vector3
    // every call), unlike the other two methods below.
    public void SpawnStatusAtLocation(StatusPopupKind kind, Vector3 location, string customMessage = null, Color? colorOverride = null, float durationMultiplier = 1f)
    {
        string message = ResolveStatusMessage(kind, customMessage);
        if (string.IsNullOrEmpty(message)) return;
        Color? color = ResolveStatusColor(kind, colorOverride);

        float xOffset = RollStatusXOffset(out float zRot);
        Vector3 worldPos = new Vector3(location.x, 0f, location.z) + new Vector3(xOffset, _statusSpawnYOffset, 0f);

        var num = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(_prefab, transform);
        // Explicit CharacterStatsPD cast — a bare null is ambiguous now that InitStatus has both a
        // CharacterStatsPD and an IEnemyPD overload; either resolves identically for a fixed-location popup
        // (no target either way), this just breaks the tie.
        num.InitStatus(message, worldPos, (CharacterStatsPD)null, zRot, this, color, durationMultiplier);
    }

    // Enemy counterpart to SpawnStatusOnPlayer above — same continuous-follow/kind/message/color resolution,
    // just keyed off IEnemyPD/Visuals instead of CharacterStatsPD/transform (see DamageNumber.InitStatus's
    // IEnemyPD overload for the follow-tracking difference — enemies are pooled/reused, so that overload
    // needs the same SpawnGeneration staleness guard Damage's own enemy-following already uses, unlike a
    // player). EnemyBasePD.Stun()/Slow()/Interrupt() are the only callers today. Also routed through
    // RequestStatusPopup — see its own doc comment.
    public void SpawnStatusOnEnemy(StatusPopupKind kind, IEnemyPD target, string customMessage = null, Color? colorOverride = null, float durationMultiplier = 1f)
    {
        if (IsDestroyed(target)) return;
        RequestStatusPopup(target.ObjectId.GetHashCode(), new PendingStatusPopup
        {
            kind = kind,
            enemyTarget = target,
            customMessage = customMessage,
            colorOverride = colorOverride,
            durationMultiplier = durationMultiplier,
        });
    }

    // The showtime/dedupe/queue gate every SpawnStatusOnPlayer/SpawnStatusOnEnemy request goes through — the
    // OPPOSITE of Damage's own "merge same-source hits into one growing number" behavior. A status popup
    // gets _statusShowtimeDuration entirely to itself on its target before ANY other popup (any kind) is
    // allowed to show there — nothing else prints early, even a different kind. A request that arrives
    // during that window either:
    // - gets DROPPED outright if it's the SAME kind as whatever's currently showing OR already queued (e.g.
    //   two "Stun" requests landing close together on the same enemy: only the first ever prints) — Custom
    //   is exempt from this dedupe entirely, since two Custom calls could carry completely unrelated ad-hoc
    //   messages, so "same kind" doesn't mean "same content" the way it does for every fixed-message kind;
    // - otherwise gets QUEUED (FIFO) and shown once its turn comes up, each dequeue starting a fresh
    //   _statusShowtimeDuration window of its own (see Update()'s own status-tick block).
    // targetKey is caller-supplied (player GetInstanceID() or enemy ObjectId.GetHashCode()) since this class
    // has no other reason to know how to derive an identity out of either type itself.
    private void RequestStatusPopup(int targetKey, PendingStatusPopup pending)
    {
        if (!_statusShowtimeStates.TryGetValue(targetKey, out var state))
        {
            state = new StatusShowtimeState();
            _statusShowtimeStates[targetKey] = state;
        }

        // Nothing currently blocking this target — show immediately, no queueing needed.
        if (state.windowRemaining <= 0f)
        {
            state.showingKind = pending.kind;
            state.windowRemaining = _statusShowtimeDuration;
            ExecuteStatusPopup(pending);
            return;
        }

        if (pending.kind != StatusPopupKind.Custom)
        {
            if (state.showingKind == pending.kind) return;
            foreach (var queued in state.queue)
                if (queued.kind == pending.kind) return;
        }

        state.queue.Enqueue(pending);
    }

    // The actual spawn logic — called either immediately (RequestStatusPopup, nothing blocking) or later,
    // once a queued entry's turn comes up (Update()'s own status-tick block). Branches on which of
    // playerTarget/enemyTarget is set (see PendingStatusPopup's own doc comment) — an enemy that died while
    // still queued (IsDestroyed) is silently skipped rather than spawning on a stale/gone target.
    private void ExecuteStatusPopup(PendingStatusPopup pending)
    {
        string message = ResolveStatusMessage(pending.kind, pending.customMessage);
        if (string.IsNullOrEmpty(message)) return;
        Color? color = ResolveStatusColor(pending.kind, pending.colorOverride);

        float xOffset = RollStatusXOffset(out float zRot);
        var num = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(_prefab, transform);

        if (pending.playerTarget != null)
        {
            // SizeOffsetMultiplier — see SpawnAndReleasePlayerNumber's own identical comment for why.
            Vector3 worldPos = pending.playerTarget.transform.position + new Vector3(xOffset, _playerStatusSpawnYOffset * SizeOffsetMultiplier(pending.playerTarget.bonusSizePercent), 0f);
            num.InitStatus(message, worldPos, pending.playerTarget, zRot, this, color, pending.durationMultiplier);
        }
        else if (!IsDestroyed(pending.enemyTarget))
        {
            // SizeOffsetMultiplier (2026-08-19) — enemy status popups previously had no size scaling at
            // all, unlike every other spawn-Y-offset site; a boss like King Spike (size 3) would show a
            // Stun/Slow/Interrupt popup pinned to the same height as a normal-sized enemy. Same convention as
            // everywhere else now — see SizeOffsetMultiplier's own doc comment for the rate/formula.
            Vector3 worldPos = pending.enemyTarget.Visuals.position + new Vector3(xOffset, _statusSpawnYOffset * SizeOffsetMultiplier(pending.enemyTarget.Size), 0f);
            num.InitStatus(message, worldPos, pending.enemyTarget, zRot, this, color, pending.durationMultiplier);
        }
        else
        {
            // Target gone while queued — force-inactive (a fresh Instantiate() fallback, unlike a dequeued
            // pooled instance, isn't guaranteed to start inactive) and return it unused rather than leaking
            // it or leaving a stray visible object.
            num.gameObject.SetActive(false);
            _pool.Enqueue(num);
        }
    }

    // Custom passes customMessage straight through unchanged (the caller's own literal string). Every other
    // kind resolves to its own [Header] default above. Death is the one exception with multiple entries —
    // random pick from _deathStatusMessages, or "" if that array isn't configured yet (SpawnStatusOnPlayer/
    // SpawnStatusAtLocation's own empty-message guard then silently no-ops the popup rather than printing
    // blank text).
    private string ResolveStatusMessage(StatusPopupKind kind, string customMessage) => kind switch
    {
        StatusPopupKind.Berserk => _berserkStatusMessage,
        StatusPopupKind.Death => _deathStatusMessages != null && _deathStatusMessages.Length > 0
            ? _deathStatusMessages[UnityEngine.Random.Range(0, _deathStatusMessages.Length)]
            : "",
        StatusPopupKind.DeathPrevented => _deathPreventedStatusMessage,
        StatusPopupKind.InvisibleStarted => _invisibleStartedStatusMessage,
        StatusPopupKind.InvisibleEnded => _invisibleEndedStatusMessage,
        StatusPopupKind.Taunt => _tauntStatusMessage,
        StatusPopupKind.Stun => _stunStatusMessage,
        StatusPopupKind.Interrupted => _interruptedStatusMessage,
        StatusPopupKind.Slowed => _slowedStatusMessage,
        StatusPopupKind.Executed => _executedStatusMessage,
        StatusPopupKind.Dodged => _dodgedStatusMessage,
        _ => customMessage,
    };

    // An explicit colorOverride always wins regardless of kind. Otherwise every known kind resolves to its
    // own [Header] color above (each currently white by default, but kept as its own field per-kind so any
    // one of them can be retuned independently later — not shared). Custom (no per-kind color of its own)
    // falls through to null, which DamageNumber.InitStatus then resolves to its own generic _statusColor
    // default.
    private Color? ResolveStatusColor(StatusPopupKind kind, Color? colorOverride)
    {
        if (colorOverride.HasValue) return colorOverride;
        return kind switch
        {
            StatusPopupKind.Berserk => _berserkStatusColor,
            StatusPopupKind.Death => _deathStatusColor,
            StatusPopupKind.DeathPrevented => _deathPreventedStatusColor,
            StatusPopupKind.InvisibleStarted => _invisibleStartedStatusColor,
            StatusPopupKind.InvisibleEnded => _invisibleEndedStatusColor,
            StatusPopupKind.Taunt => _tauntStatusColor,
            StatusPopupKind.Stun => _stunStatusColor,
            StatusPopupKind.Interrupted => _interruptedStatusColor,
            StatusPopupKind.Slowed => _slowedStatusColor,
            StatusPopupKind.Executed => _executedStatusColor,
            StatusPopupKind.Dodged => _dodgedStatusColor,
            _ => (Color?)null,
        };
    }

    // Rolls this popup's own lateral spawn jitter + matching Z-axis tilt together — magnitude random between
    // _statusMinSpawnXJitter/_statusMaxSpawnXJitter, sign random left/right. zRot scales against
    // _statusMaxSpawnZRot at the same fraction-of-max the rolled magnitude landed at, sign inverted from
    // xOffset's own (positive zRot for a negative xOffset, negative zRot for a positive one) so the tilt
    // visually leans away from the side the popup drifted toward.
    private float RollStatusXOffset(out float zRot)
    {
        float magnitude = UnityEngine.Random.Range(_statusMinSpawnXJitter, _statusMaxSpawnXJitter);
        bool goRight = UnityEngine.Random.value < 0.5f;
        float xOffset = goRight ? magnitude : -magnitude;

        float t = _statusMaxSpawnXJitter > 0f ? magnitude / _statusMaxSpawnXJitter : 0f;
        zRot = (xOffset >= 0f ? -1f : 1f) * t * _statusMaxSpawnZRot;
        return xOffset;
    }
}
