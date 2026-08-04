using UnityEngine;

// Local-only cosmetic ground ring showing the outer edge of this player's current weapon reach. Reuses the
// same flat-cylinder + IndicatorMath.ApplyRadialTransform convention every enemy attack telegraph already
// uses (Leaper/Dagger/KingSpike's windup indicators, RemoteSpikeBurstPD) rather than introducing a separate
// sprite-based indicator type just for players — one indicator implementation project-wide. Plain
// MonoBehaviour, not a PredictedIdentity: purely a read-only reflection of live predicted stats every
// frame, same "local, non-predicted, non-networked" pattern as BuffBarPD/WindupFillPD — nothing here
// mutates gameplay state, so there's nothing to desync.
public class PlayerRangeIndicatorPD : MonoBehaviour
{
    [SerializeField] private CharacterStatsPD stats;
    [SerializeField] private WeaponManagerPD weaponManager;
    [SerializeField] private Renderer indicatorRenderer;

    private void Awake()
    {
        if (stats == null) stats = GetComponentInParent<CharacterStatsPD>();
        if (weaponManager == null) weaponManager = GetComponentInParent<WeaponManagerPD>();
    }

    private void Update()
    {
        if (stats == null || weaponManager == null) return;

        bool shouldShow = stats.isOwner && !stats.IsDead;
        if (indicatorRenderer != null) indicatorRenderer.enabled = shouldShow;
        if (!shouldShow) return;

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

        IndicatorMath.ApplyRadialTransform(transform, radius / parentScale);
    }
}
