using System.Collections;
using TMPro;
using UnityEngine;

public class DamageNumber : MonoBehaviour
{
    [SerializeField] private TextMeshPro _text;
    // Total +Z drift accumulated linearly over the full lifetime — pushes each number slightly "backward"
    // over time so newer numbers spawning at the same spot render on top of older, already-drifted ones
    // instead of z-fighting/overlapping at identical depth. Shared by every popup type (the arc/fade
    // animation isn't damage-specific), so it stays outside the Damage section below.
    [SerializeField] private float _zDrift = 0.05f;
    // Fixed one-time nudge along world -Z applied to every popup's spawn position, every popup type
    // (2026-08-19) — keeps every number sitting slightly toward the camera relative to overlapping UI like a
    // health bar, so it never z-fights with or renders behind one. Bigger in magnitude than _zDrift (which
    // only ever accumulates gradually over an enemy number's own arc lifetime) so it's never cancelled out
    // by that drift running the opposite way. Applied once, directly to worldPos, at the very top of every
    // Init* method below — folding it into worldPos there (rather than as a separate offset kept alongside)
    // means it flows through into _followOffset for free everywhere a number re-derives its position off a
    // live target every frame, with no extra bookkeeping needed.
    [SerializeField] private float _spawnZOffset = -0.1f;

    [Header("Damage")]
    // Enemy-only (2026-08-18) — player numbers no longer arc at all, they release into
    // AnimatePlayerNumber() instead, which reuses _statusMaxHeight/_statusDuration below (see
    // InitLockedPlayer's own doc comment for why). How high the arc rises above its start position at its
    // peak (t=0.5) — it always returns to exactly 0 extra height by the end of _duration, landing back at
    // its original spawn Y.
    [SerializeField] private float _maxHeight = 1f;
    [SerializeField] private float _duration = 1f;
    // Priority (real weapon hits, Phantom Blade direct damage) prints at full size; everything else
    // (onhit-effect procs, etc.) prints at half size, so the "real" damage numbers visually stand out from
    // the onhit noise they trigger. Enemy-only — player damage numbers (SetPlayerAmount) are always
    // "priority" (no onhit-noise concept exists for damage a player receives) and use _playerFontSize
    // instead, kept as its own separate field since the two want different sizes.
    [SerializeField] private float _enemyFontSize = 8f;
    [SerializeField] private float _nonPrioritySize = 4f;
    [SerializeField] private float _playerFontSize = 8f;
    // Player Heal-only override of _playerFontSize (2026-08-21) — separate field/tunable, not a fraction of
    // _playerFontSize, same "own dedicated field, not derived" convention _playerFontSize itself already
    // uses relative to _enemyFontSize. Player Damage keeps using _playerFontSize unchanged.
    [SerializeField] private float _playerHealFontSize = 3f;
    // Colors themselves are NOT here (2026-08-16) — moved to DamageNumberManager.ResolveEnemyDamageColor,
    // matching every other popup type (Player Damage/Heal/Status all resolve their own color in the manager
    // and pass the result in) instead of this prefab resolving isCrit/isHighroll into a color itself. Spawn()/
    // the merge branch in DamageNumberManager now pass the already-resolved Color straight into
    // InitLocked/SetAmount below.

    [Header("Status")]
    // Deliberately separate fields from Damage's own _maxHeight/_duration/_enemyFontSize/_normalColor above — a
    // status popup has its own pace/size/color, not tied to a damage number's. See AnimateStatus for the
    // drift/fade shape. _statusMaxHeight/_statusDuration are ALSO what AnimatePlayerNumber() uses for Player
    // Damage/Heal (2026-08-18, deliberately shared rather than duplicated — see InitLockedPlayer's own doc
    // comment) — _statusFontSize/_statusColor stay status-popup-exclusive (Player Damage/Heal keep their own
    // _playerFontSize/per-pool colors).
    [SerializeField] private float _statusMaxHeight = 2f;
    [SerializeField] private float _statusDuration = 1f;
    [SerializeField] private float _statusFontSize = 6f;
    [SerializeField] private Color _statusColor = Color.white;

    // Per-call override of _statusDuration for THIS status popup only (set once in InitStatus from its own
    // durationMultiplier param) — never written back to _statusDuration itself, since that field is also read
    // by AnimatePlayerNumber() for unrelated Player Damage/Heal numbers (see comment block above). AnimateStatus
    // reads this instead of _statusDuration directly.
    private float _effectiveStatusDuration;

    // Shared by every popup type.
    private DamageNumberManager _manager;
    private Camera _cam;

    // Damage — this number's own tracked enemy + stacking state.
    private IEnemyPD _target;
    // Captured at InitLocked() time — a raw IEnemyPD reference alone can't tell a still-alive enemy apart
    // from this same pooled GameObject having since been recycled for a completely different spawn, so
    // tracking is only trusted while _target.SpawnGeneration still matches this. See DamageNumberManager's
    // own IsStillTheSameEnemy for the manager-side equivalent of this same check.
    private int _targetGeneration;
    // World-space offset between where this number actually spawned (worldPos, already including the
    // size-scaled _spawnYOffset/etc., but NOT the stack height — see _lockedHeight) and the
    // target's position at that moment — re-added to the target's LIVE position every frame (both while
    // locked and while animating) so the number keeps the same relative spawn offset while tracking a
    // moving enemy instead of drifting behind it. Release() bakes the current _lockedHeight into this once,
    // permanently, so Animate()'s own basePos formula doesn't need to know about stacking at all.
    private Vector3 _followOffset;
    // Current stack slot height (world +Y, on top of _followOffset) — live-adjustable via SetHeight() while
    // still locked (e.g. a later Weapon hit bumping this group up the stack), frozen the moment Release()
    // bakes it into _followOffset.
    private float _lockedHeight;
    // Rolled once by DamageNumberManager when this group was first created — the total X distance Animate()
    // arcs toward once released. Enemy-only (2026-08-18) — player numbers no longer arc at all (see
    // ReleasePlayer/AnimatePlayerNumber), their own X jitter is baked directly into the initial spawn
    // position instead, same as a status popup's own X jitter already works.
    private float _xOffset;
    private bool _isPriority;
    // Set instead of _target for a player-damage number (see DamageNumberManager.SpawnPlayerDamage) —
    // mutually exclusive with _target (InitLocked/InitLockedPlayer each null out the other), both checked
    // in FollowLocked/Animate to decide which live position to re-derive from each frame. No SpawnGeneration-
    // style staleness tracking needed — CharacterStatsPD instances are never pooled/reused for a different
    // player mid-match (see AllyStrikeData's own doc comment in WeaponBasePD.cs), so a plain null check
    // (Unity's real destroyed-check, since CharacterStatsPD is a MonoBehaviour) is enough.
    private CharacterStatsPD _playerTarget;
    // Rolled once by DamageNumberManager (RollStatusXOffset, the same roll a status popup's own _statusZRot
    // uses) when this group was first created, held fixed for its whole lifetime — see AnimatePlayerNumber
    // for how this gets applied as a camera-relative roll, same shape as Status's own _statusZRot.
    private float _playerZRot;

    // Status — this popup's own tracked player (null for a fixed-location popup) + the Z-tilt rolled for
    // it. No stacking/locking state needed here — see InitStatus's own doc comment.
    private CharacterStatsPD _statusTarget;
    // Same idea as Damage's _followOffset above, just re-derived off CharacterStatsPD.transform instead of
    // an IEnemyPD's Visuals — kept as its own field rather than reusing _followOffset since the two run
    // through completely separate coroutines (FollowLocked/Animate vs AnimateStatus) that could otherwise
    // stomp each other if this same pooled instance is reused across popup types back to back.
    private Vector3 _statusFollowOffset;
    // Rolled once by DamageNumberManager when this popup was first spawned, held fixed for its whole
    // lifetime — see AnimateStatus for how this gets applied as a camera-relative roll.
    private float _statusZRot;
    // Set instead of _statusTarget for an enemy status popup (see DamageNumberManager.SpawnStatusOnEnemy) —
    // mutually exclusive with _statusTarget, both null-checked in AnimateStatus to decide which live position
    // to re-derive from. Unlike a player, enemies ARE pooled/reused, so this needs the same SpawnGeneration
    // staleness guard Damage's own _target/_targetGeneration already uses — see IsStillTheSameStatusEnemy.
    private IEnemyPD _statusEnemyTarget;
    private int _statusEnemyTargetGeneration;

    private void Awake() => _cam = Camera.main;

    // Spawns immediately, frozen at its stack position — no arc, no fade, no lifetime countdown yet — and
    // keeps tracking the target's live position (spawn offset + current stack height) every frame until
    // Release() is called. This is the "print instantly, lock position, wait for the stacking window"
    // phase of DamageNumberManager's stacking rework — see that class's own doc comment for the full rule.
    // color is already resolved by the caller (DamageNumberManager.ResolveEnemyDamageColor) — see the
    // Damage header's own comment for why this prefab no longer resolves isCrit/isHighroll into a color
    // itself, matching every other popup type.
    public void InitLocked(float amount, Vector3 worldPos, float height, float xOffset, IEnemyPD target, DamageNumberManager manager, bool isCrit, bool isHighroll, bool isPriority, Color color)
    {
        worldPos += new Vector3(0f, 0f, _spawnZOffset);
        _manager = manager;
        _target = target;
        _playerTarget = null;
        bool targetAlive = !DamageNumberManager.IsDestroyed(target);
        _targetGeneration = targetAlive ? target.SpawnGeneration : 0;
        _followOffset = targetAlive ? worldPos - target.Visuals.position : Vector3.zero;
        _lockedHeight = height;
        _xOffset = xOffset;
        _isPriority = isPriority;
        transform.position = worldPos + Vector3.up * height;
        SetAmount(amount, isCrit, isHighroll, color);
        _text.alpha = 1f;
        gameObject.SetActive(true);
        StopAllCoroutines();
        StartCoroutine(FollowLocked());
    }

    // Player Damage/Heal — same locked-then-released two-step/FollowLocked machinery as InitLocked above
    // (stack window/height step, unchanged), but the RELEASE half diverges (2026-08-18): a player number now
    // releases into ReleasePlayer/AnimatePlayerNumber (straight-up drift + camera-relative tilt, same
    // presentation a status popup uses) instead of Release/Animate's arc — see each one's own doc comment.
    // xOffset is no longer a parameter here — a player number's own X jitter is baked directly into worldPos
    // by the caller now (same "spawn-position-only" jitter a status popup's own worldPos already uses),
    // not deferred to animate-time. zRot is rolled once by the caller (DamageNumberManager.RollStatusXOffset,
    // the exact same roll a status popup's own zRot uses) and held fixed for the whole lifetime. color is
    // fixed per pool/kind (see DamageNumberManager.ResolvePlayerDamageColor/_healColor), not rolled per-hit,
    // and always full-size (SetPlayerAmount always uses its own _playerFontSize) — no onhit-noise concept
    // exists for damage/healing a player receives. isHeal picks the leading "+"/"-"; isCrit only ever comes
    // from a Heal()-rolled crit (see SetPlayerAmount's own doc comment) — Player Damage never sets it.
    public void InitLockedPlayer(float amount, Vector3 worldPos, float height, float zRot, CharacterStatsPD target, DamageNumberManager manager, Color color, bool isHeal, bool isCrit)
    {
        worldPos += new Vector3(0f, 0f, _spawnZOffset);
        _manager = manager;
        _target = null;
        _playerTarget = target;
        // YAxisMovers, not transform (2026-08-17) — root/rigidbody position updates are physics-driven and
        // visibly jittery; see CharacterStatsPD.YAxisMovers's own doc comment.
        _followOffset = target != null ? worldPos - target.YAxisMovers.position : Vector3.zero;
        _lockedHeight = height;
        _playerZRot = zRot;
        _isPriority = true;
        transform.position = worldPos + Vector3.up * height;
        SetPlayerAmount(amount, color, isHeal, isCrit);
        _text.alpha = 1f;
        gameObject.SetActive(true);
        StopAllCoroutines();
        StartCoroutine(FollowLocked());
    }

    // Status — prints immediately and starts animating right away, no stacking/locking phase (unlike
    // Damage's InitLocked/Release two-step above, status popups never merge/accumulate). Drifts straight up
    // +Y to _statusMaxHeight over _statusDuration, staying at full alpha for the first half of that time
    // then fading linearly to 0 over the second half — see AnimateStatus. target null means a fixed-location
    // popup (worldPos never moves again); non-null continuously re-follows that player's live position the
    // same way a damage number follows its enemy, preserving the same spawn-relative offset. zRot is rolled
    // once by the caller (DamageNumberManager.RollStatusXOffset) and held fixed for the whole lifetime.
    // color overrides _statusColor for just this popup when provided (null uses _statusColor as-is) — see
    // CharacterStatsPD.NotifyStatusPopup's own doc comment for where a per-popup override originates.
    public void InitStatus(string message, Vector3 worldPos, CharacterStatsPD target, float zRot, DamageNumberManager manager, Color? color = null, float durationMultiplier = 1f)
    {
        worldPos += new Vector3(0f, 0f, _spawnZOffset);
        _manager = manager;
        _effectiveStatusDuration = _statusDuration * durationMultiplier;
        _statusTarget = target;
        _statusEnemyTarget = null;
        // YAxisMovers, not transform (2026-08-17, for consistency with Player Damage/Heal — see
        // CharacterStatsPD.YAxisMovers's own doc comment) — root/rigidbody jitter was never visible here
        // before only because a status popup has no lateral arc to make it visible, not because it wasn't
        // there.
        _statusFollowOffset = target != null ? worldPos - target.YAxisMovers.position : Vector3.zero;
        _statusZRot = zRot;
        InitStatusText(message, color);
        transform.position = worldPos;
        gameObject.SetActive(true);
        StopAllCoroutines();
        StartCoroutine(AnimateStatus(worldPos));
    }

    // Enemy overload — mirrors the CharacterStatsPD one above exactly, just tracking an IEnemyPD/Visuals
    // instead (with the extra SpawnGeneration staleness guard enemies need — see _statusEnemyTarget's own
    // doc comment). Called by DamageNumberManager.SpawnStatusOnEnemy.
    public void InitStatus(string message, Vector3 worldPos, IEnemyPD target, float zRot, DamageNumberManager manager, Color? color = null, float durationMultiplier = 1f)
    {
        worldPos += new Vector3(0f, 0f, _spawnZOffset);
        _manager = manager;
        _effectiveStatusDuration = _statusDuration * durationMultiplier;
        _statusTarget = null;
        _statusEnemyTarget = target;
        bool targetAlive = !DamageNumberManager.IsDestroyed(target);
        _statusEnemyTargetGeneration = targetAlive ? target.SpawnGeneration : 0;
        _statusFollowOffset = targetAlive ? worldPos - target.Visuals.position : Vector3.zero;
        _statusZRot = zRot;
        InitStatusText(message, color);
        transform.position = worldPos;
        gameObject.SetActive(true);
        StopAllCoroutines();
        StartCoroutine(AnimateStatus(worldPos));
    }

    // Shared text/style reset between both InitStatus overloads above.
    private void InitStatusText(string message, Color? color)
    {
        // Explicitly reset (not left as whatever this pooled instance last had) — this same TextMeshPro
        // could have just come from a Damage popup with a crit/highroll color and Bold/Italic style still
        // applied, which would otherwise bleed into this status popup's own presentation.
        _text.text = message;
        _text.color = color ?? _statusColor;
        _text.fontStyle = FontStyles.Normal;
        _text.fontSize = _statusFontSize;
        _text.alpha = 1f;
    }

    // Called by DamageNumberManager whenever another same-source hit lands while this number is still
    // locked/accumulating — updates the displayed total and crit/highroll styling in place, no new number
    // spawned. isCrit/isHighroll are OR-combined by the caller across every hit folded into this group
    // (showing crit/highroll styling if ANY hit in the accumulated total qualified, not just the latest one).
    // AugmentDescriptionFormatter.FormatLiveNumber — same precision convention already used for every
    // ability/augment description's live-value tokens (<1 shows 2 decimals, >=1 shows 1, >=10 shows a whole
    // number) — reused here so world-space number popups read the exact same way. DamageNumberManager's own
    // amount<=0 guards (Spawn/SpawnPlayerDamage/SpawnPlayerHeal) already keep a 0-or-below amount from ever
    // reaching this in the first place. color is already resolved by the caller — see InitLocked's own
    // comment.
    public void SetAmount(float amount, bool isCrit, bool isHighroll, Color color)
    {
        _text.text = AugmentDescriptionFormatter.FormatLiveNumber(amount) + ResolveSuffix(isCrit, isHighroll);
        _text.color = color;
        _text.fontStyle = ResolveStyle(isCrit, isHighroll);
        _text.fontSize = _isPriority ? _enemyFontSize : _nonPrioritySize;
    }

    // Player Damage/Heal equivalent of SetAmount above — called by DamageNumberManager.SpawnPlayerDamage/
    // SpawnPlayerHeal whenever another same-group hit/heal lands while this number is still locked/
    // accumulating. No highroll/style concept (unlike enemy SetAmount) and always full-size (see
    // InitLockedPlayer), via its own _playerFontSize rather than the enemy side's _enemyFontSize. Leading
    // "-"/"+" distinguishes damage (loss) from healing (gain) at a glance — an enemy damage number has
    // neither sign, since it's read unambiguously as "damage dealt" on its own. isCrit appends the same
    // cosmetic "!" ResolveSuffix gives an enemy crit — only ever true for a Player Heal group (Heal()
    // rolling CriticalHealingAugment's own CritHealPower — see CharacterStatsPD.RollHealMultiplier), Player
    // Damage always passes false (no crit concept exists for damage a player receives).
    public void SetPlayerAmount(float amount, Color color, bool isHeal, bool isCrit)
    {
        _text.text = (isHeal ? "+" : "-") + AugmentDescriptionFormatter.FormatLiveNumber(amount) + (isCrit ? "!" : "");
        _text.color = color;
        _text.fontStyle = FontStyles.Normal;
        _text.fontSize = isHeal ? _playerHealFontSize : _playerFontSize;
    }

    // Called by DamageNumberManager when a fresh Weapon-source group needs to bump every other currently-
    // locked group on the same stack up by one slot — repositions on the next FollowLocked frame, no
    // animation needed (this number hasn't started its real arc yet, so a hard reposition here is
    // unnoticeable, same as any other locked-phase update).
    public void SetHeight(float height) => _lockedHeight = height;

    private IEnumerator FollowLocked()
    {
        while (true)
        {
            if (!DamageNumberManager.IsDestroyed(_target) && _target.SpawnGeneration == _targetGeneration)
                transform.position = _target.Visuals.position + _followOffset + Vector3.up * _lockedHeight;
            // Player Damage — _target/_playerTarget are mutually exclusive (see _playerTarget's own doc
            // comment), so this only ever runs for a player-damage number.
            else if (_playerTarget != null)
                transform.position = _playerTarget.YAxisMovers.position + _followOffset + Vector3.up * _lockedHeight;
            if (_cam != null) transform.forward = _cam.transform.forward;
            yield return null;
        }
    }

    // Called by DamageNumberManager once the stacking window elapses with no further same-source hits (or
    // the target enemy is gone) — starts the real arc/fade/lifetime animation from wherever this number is
    // currently sitting, exactly like the old system's Init() used to do immediately; the only thing that
    // changed is WHEN this fires relative to the first hit.
    public void Release()
    {
        // Bakes the current stack height into _followOffset permanently, so Animate()'s own basePos formula
        // (which knows nothing about stacking) keeps this number at the height it was sitting at when the
        // window closed, instead of snapping back down to the base spawn height the instant the real
        // animation starts. No further SetHeight() calls affect this number after Release() — matches
        // DamageNumberManager's own "only the pre-animation stack reorders" rule.
        _followOffset += Vector3.up * _lockedHeight;
        StopAllCoroutines();
        StartCoroutine(Animate(_xOffset));
    }

    // Player Damage/Heal equivalent of Release() above (2026-08-18) — same "bake the current stack height
    // in permanently" step, but starts AnimatePlayerNumber() (straight-up drift + tilt, status-popup style)
    // instead of Animate() (arc). Called by DamageNumberManager.ReleasePlayerGroup.
    public void ReleasePlayer()
    {
        _followOffset += Vector3.up * _lockedHeight;
        StopAllCoroutines();
        StartCoroutine(AnimatePlayerNumber());
    }

    private string ResolveSuffix(bool isCrit, bool isHighroll)
    {
        string suffix = "";
        if (isCrit) suffix += "!";
        if (isHighroll) suffix += "!";
        return suffix;
    }

    // FontStyles is a [Flags] enum — crit alone is Bold+Italic, highroll alone is Bold, and crit+highroll
    // composes to the same Bold+Italic (the |= is idempotent) rather than needing a distinct "both" case.
    private FontStyles ResolveStyle(bool isCrit, bool isHighroll)
    {
        FontStyles style = FontStyles.Normal;
        if (isHighroll) style |= FontStyles.Bold;
        if (isCrit) style |= FontStyles.Bold | FontStyles.Italic;
        return style;
    }

    private IEnumerator Animate(float xOffset)
    {
        float elapsed = 0f;
        // Re-derived from _target's live position every frame below when still valid — this is only the
        // fallback once the target is gone (pooled/destroyed mid-animation), freezing at wherever it was
        // last tracked rather than snapping back to the very first spawn frame.
        Vector3 basePos = transform.position;

        while (elapsed < _duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / _duration;
            // DamageNumberManager.IsDestroyed, not a plain null check — a truly-destroyed enemy's IEnemyPD
            // reference still reads as non-null through a bare `_target != null` (Unity's "fake null"
            // operator only kicks in for the UnityEngine.Object static type, not an interface), which threw
            // MissingReferenceException the instant .Visuals touched its transform. SpawnGeneration on top
            // of that catches the separate pooled-reuse case: a dead-but-pooled (not destroyed) enemy keeps
            // a perfectly valid, non-destroyed reference even after its GameObject gets recycled for a
            // completely different spawn, so without this we'd silently start tracking whatever NEW enemy
            // now occupies that slot instead of freezing where the original one died.
            if (!DamageNumberManager.IsDestroyed(_target) && _target.SpawnGeneration == _targetGeneration)
                basePos = _target.Visuals.position + _followOffset;
            // Player Damage — see FollowLocked's own identical branch/comment.
            else if (_playerTarget != null)
                basePos = _playerTarget.YAxisMovers.position + _followOffset;
            // sin(t * pi) is 0 at t=0 and t=1 and peaks at _maxHeight at t=0.5 — rises then falls back to
            // exactly the start height by the end, same arc-bob shape as LeaperGloomPD's leap. X moves
            // linearly toward xOffset over the same duration, so the combined path traces an arc/parabola
            // rather than a straight vertical float.
            float arc = Mathf.Sin(t * Mathf.PI) * _maxHeight;
            transform.position = basePos + new Vector3(xOffset * t, arc, _zDrift * t);
            _text.alpha = HalfDurationAlpha(elapsed, _duration);
            if (_cam != null) transform.forward = _cam.transform.forward;
            yield return null;
        }

        gameObject.SetActive(false);
        _manager.Return(this);
    }

    // Player Damage/Heal equivalent of Animate() above (2026-08-18) — straight-up drift (no arc, no lateral
    // drift at all) to _statusMaxHeight over _statusDuration, plus the camera-relative _playerZRot tilt,
    // exactly AnimateStatus()'s own shape below (deliberately reuses Status's OWN pacing/height fields, not
    // separate Player-specific ones — see InitLockedPlayer's own doc comment for why). _followOffset already
    // has the X jitter baked in from spawn (see DamageNumberManager.SpawnPlayerDamage/SpawnPlayerHeal), so
    // there's nothing left to do laterally here, only +Y.
    private IEnumerator AnimatePlayerNumber()
    {
        float elapsed = 0f;
        Vector3 basePos = transform.position;

        while (elapsed < _statusDuration)
        {
            elapsed += Time.deltaTime;
            if (_playerTarget != null)
                basePos = _playerTarget.YAxisMovers.position + _followOffset;

            float driftT = Mathf.Clamp01(elapsed / _statusDuration);
            transform.position = basePos + Vector3.up * (_statusMaxHeight * driftT);

            _text.alpha = HalfDurationAlpha(elapsed, _statusDuration);

            // Camera-relative roll — see AnimateStatus's own identical line for the full reasoning.
            if (_cam != null) transform.rotation = Quaternion.LookRotation(_cam.transform.forward) * Quaternion.Euler(0f, 0f, _playerZRot);

            yield return null;
        }

        gameObject.SetActive(false);
        _manager.Return(this);
    }

    // Full alpha for the first HALF of duration, then a linear fade to 0 over the second half — applied to
    // every animated popup type (2026-08-18; previously Status-only, see AnimateStatus's own original
    // comment). Shared here so Animate()/AnimatePlayerNumber()/AnimateStatus() can't drift out of sync with
    // each other on this exact shape.
    private static float HalfDurationAlpha(float elapsed, float duration)
    {
        float half = duration * 0.5f;
        return elapsed <= half ? 1f : 1f - Mathf.Clamp01((elapsed - half) / Mathf.Max(half, 0.0001f));
    }

    // Status — straight-line +Y drift to _statusMaxHeight over _statusDuration (no arc, unlike Damage's
    // Animate() above), full alpha for the first half of that time then a linear fade to 0 over the second
    // half. _statusTarget is a plain CharacterStatsPD (a MonoBehaviour/UnityEngine.Object), so a bare
    // `!= null` already routes through Unity's real destroyed-check directly — no DamageNumberManager.
    // IsDestroyed-style indirection needed here the way _target (IEnemyPD, an interface) requires above.
    private IEnumerator AnimateStatus(Vector3 initialPos)
    {
        float elapsed = 0f;
        Vector3 basePos = initialPos;

        while (elapsed < _effectiveStatusDuration)
        {
            elapsed += Time.deltaTime;
            if (_statusTarget != null)
                basePos = _statusTarget.YAxisMovers.position + _statusFollowOffset;
            // Enemy status popup — see _statusEnemyTarget's own doc comment for why this needs the extra
            // SpawnGeneration check a player target doesn't (enemies are pooled/reused, players aren't).
            else if (!DamageNumberManager.IsDestroyed(_statusEnemyTarget) && _statusEnemyTarget.SpawnGeneration == _statusEnemyTargetGeneration)
                basePos = _statusEnemyTarget.Visuals.position + _statusFollowOffset;

            float driftT = Mathf.Clamp01(elapsed / _effectiveStatusDuration);
            transform.position = basePos + Vector3.up * (_statusMaxHeight * driftT);

            _text.alpha = HalfDurationAlpha(elapsed, _effectiveStatusDuration);

            // Camera-relative roll — LookRotation faces the camera same as Animate()'s own `transform.forward
            // = _cam.transform.forward` above, then the extra Euler appends _statusZRot as a LOCAL Z tilt on
            // top of that facing, so it reads as a consistent screen-plane lean regardless of view angle.
            if (_cam != null) transform.rotation = Quaternion.LookRotation(_cam.transform.forward) * Quaternion.Euler(0f, 0f, _statusZRot);

            yield return null;
        }

        gameObject.SetActive(false);
        _manager.Return(this);
    }
}
