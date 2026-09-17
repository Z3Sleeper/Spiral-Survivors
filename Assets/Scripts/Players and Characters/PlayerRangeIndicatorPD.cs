using UnityEngine;

// Local-only cosmetic ground ring showing the outer edge of this player's current weapon reach. Reuses
// WindupFillPD's own Radial outline (2026-09-14, replaces a scaled flat-cylinder mesh +
// IndicatorMath.ApplyRadialTransform) — one indicator implementation project-wide, matching every enemy
// attack telegraph (Leaper/Dagger/KingSpike's windup indicators, RemoteSpikeBurstPD). The switch is
// specifically to fix a real visual artifact the old scaled-mesh ring had: its outline's THICKNESS scaled
// proportionally with radius (thin-looking at long range, stubby/thick-looking at short range), since it was
// just a flat cylinder mesh being uniformly scaled. WindupFillPD's own outline is a LineRenderer, whose
// startWidth/endWidth are fixed WORLD-space units regardless of the circle's radius, so the ring now reads
// as the same true thickness at every range. rangeFill's own fillMesh is left unassigned in the Inspector —
// this indicator is a pure boundary line, never a filling shape, so WindupFillPD's fill-mesh math simply
// never runs (see its own null-guard in ApplyFill); fillType must be set to Radial on it. Plain
// MonoBehaviour, not a PredictedIdentity: purely a read-only reflection of live predicted stats every
// frame, same "local, non-predicted, non-networked" pattern as BuffBarPD/WindupFillPD — nothing here
// mutates gameplay state, so there's nothing to desync.
public class PlayerRangeIndicatorPD : MonoBehaviour
{
    [SerializeField] private CharacterStatsPD stats;
    [SerializeField] private WeaponManagerPD weaponManager;
    [SerializeField] private WindupFillPD rangeFill;

    // Change-detection (2026-09-14) — see feedback_ui_update_change_detection: every per-frame UI write
    // guarded against redundant values. WindupFillPD's own Radial outline now separately invalidates its
    // cache on an actual radius change (see RecomputeOutline's own doc comment) so this isn't needed for
    // correctness anymore, but skipping the ShowCircle/SetProgress calls entirely when nothing changed still
    // avoids doing real (if cheap) work — a LineRenderer.SetPositions call plus a material touch — every
    // single frame for a ring that, in practice, sits at the same radius the overwhelming majority of ticks.
    private float _lastShownRadius = float.NaN;

    private void Awake()
    {
        if (stats == null) stats = GetComponentInParent<CharacterStatsPD>();
        if (weaponManager == null) weaponManager = GetComponentInParent<WeaponManagerPD>();
    }

    private void Update()
    {
        if (stats == null || weaponManager == null || rangeFill == null) return;

        bool shouldShow = stats.isOwner && !stats.IsDead;
        if (!shouldShow)
        {
            rangeFill.Hide();
            // Forces a fresh ShowCircle+SetProgress the next time this becomes visible again, even if the
            // radius happens to numerically match whatever it was last shown at — Hide() itself invalidates
            // WindupFillPD's own outline cache, so skipping that first post-show call as "unchanged" would
            // leave the outline never actually redrawn.
            _lastShownRadius = float.NaN;
            return;
        }

        // Reads the actual farthest currently-registered weapon's own hit-detection edge (see
        // WeaponManagerPD.TryGetFarthestWeaponReach/IWeaponPD.GetOuterReach) instead of approximating from
        // WeaponRadius/WeaponRange directly — exact for every weapon model, including per-prefab visual
        // offsets a stat-only formula can't see. No freeze-while-disabled special case needed here anymore
        // — GetOuterReach is now purely design/stat-derived (never reads live transform position), so it no
        // longer fluctuates during a flipped orbit (RogueSpinAbilityPD's leap) or a relocated one
        // (BWBladeDanceAbilityPD) the way the old live-distance formula did.
        if (!weaponManager.TryGetFarthestWeaponReach(out float radius)) return;

        // Compensate for the player's own Size scaling (AddSizePercent scales the root transform directly,
        // and this object is parented under that root) so the ring's WORLD-space radius always matches the
        // real reach exactly, regardless of any Size augments — otherwise the ring would balloon/shrink
        // alongside unrelated Size bonuses instead of purely tracking weapon reach. Reads stats.
        // bonusSizePercent directly rather than transform.parent.lossyScale.x — numerically identical since
        // this object is a direct child of the player root (see ExeAxeThrowAbilityPD.spawnOffsetBase's own
        // doc comment for the general reasoning), and stats is already right here.
        float parentScale = stats.bonusSizePercent > 0f ? stats.bonusSizePercent : 1f;
        float shownRadius = radius / parentScale;

        // Only actually redraws when the radius has genuinely changed (a fluctuating weapon reach — augments,
        // Size — should never leave the shown ring silently stale, but re-issuing the same draw call every
        // single tick it HASN'T changed is pure waste). SetProgress(1f) only drives the outline recompute here
        // (rangeFill's own fillMesh is unassigned, see class doc comment) — the actual value passed is
        // meaningless, any call just forces RecomputeOutline to run against the freshly-set radius.
        if (shownRadius == _lastShownRadius) return;
        _lastShownRadius = shownRadius;

        rangeFill.ShowCircle(shownRadius);
        rangeFill.SetProgress(1f);
    }
}
