using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/DashHealAugment.cs — On Dash End, heals self and all nearby
// allies for a percent of HealthRegen. Rolls luck variance per ally independently (the "*" annotation),
// same as every other healing/damage effect (see OnStrikeHealAllyAugment's own RollDamage call). No longer
// grants a flat dashHaste bonus (removed per the "no training wheels" balance pass — augments granting
// unique effects shouldn't hand out the stats needed to build around them). Subscribes directly to this
// player's own PlayerControllerPD._onDashEnded, same per-owner-instance cross-player-safety reasoning as
// PhantomBladeOnDashAugment (no shared/static channel for another player's dash to leak through).
public class DashHealAugment : AugmentEffectPD<DashHealAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float healthRegenMultiplier = 5f;
    [SerializeField] private float healRadius   = 10f;

    [Header("VFX")]
    [SerializeField] private GameObject radiusVFXPrefab;
    [SerializeField] private float      vfxDuration = 0.3f;

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashEndedListener(OnDashEnded);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashEndedListener(OnDashEnded);
    }

    private void OnDashEnded()
    {
        if (Stats == null || Stats.IsDead) return;

        // CharacterStatsPD.All naturally includes this player themselves (distance 0 from their own
        // position is always <= healRadius), so self-heal falls out of the same loop without a special case
        // — matches old's exact behavior.
        foreach (var ally in CharacterStatsPD.All.Values)
        {
            if (ally.IsDead) continue;
            if (Vector3.Distance(ally.transform.position, Stats.transform.position) > healRadius) continue;

            float baseHeal = Stats.HealthRegen * healthRegenMultiplier;
            float rolled = WeaponDamageMath.RollDamage(baseHeal, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
            if (isHighroll) rolled *= 1f + Stats.HighrollBonus;

            ally.Heal(rolled);
        }

        SpawnRadiusVFX();
    }

    private void SpawnRadiusVFX()
    {
        if (radiusVFXPrefab == null) return;

        var id = hierarchy.Create(radiusVFXPrefab, Stats.transform.position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out RadiusIndicatorVFXPD vfx))
            vfx.Launch(healRadius, vfxDuration);
    }
}
