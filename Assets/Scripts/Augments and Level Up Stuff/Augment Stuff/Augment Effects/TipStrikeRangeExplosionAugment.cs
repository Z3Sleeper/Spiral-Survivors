using PurrNet.Prediction;
using UnityEngine;

// On a Tip Strike (WeaponHitData.isTip — "snipe" for ranged, see that field's own doc comment) from a REAL
// weapon strike (WeaponMeleePD/WeaponAxePD/WeaponRangedProjectilePD only — isTip is false by design on every
// other onhit trigger), detonates an AoE centered ON THE STRUCK ENEMY (not the player), dealing
// damagePercent * onHitPower Strength damage to it and every other enemy caught in the blast. Hooked to
// _onHit (AddHitListener), same family as OnHitDamageAugment/OnHitDamageDistanceAugment — a real damage
// source deliberately uses _onHit rather than _onStrike so CritTriggerOnHitAugment's own re-invocation on
// crit doubles this too, matching that family's established convention (see OnHitDamageDistanceAugment's own
// doc comment for the "_onStrike vs _onHit" reasoning).
//
// "AoE Effects" tag (2026-09-14) — spawns through the same shared AreaEffectPD chain/AreaEffectCastHelper
// every other AoE Effects family member uses (DashExplosionAugment/ShieldBreakBurstAugment/DashHealAugment/
// KnightAreaStunHealAbilityPD); chainPrefab can reuse the exact same "AreaChain" prefab those already
// reference (Assets/Scripts/Augments and Level Up Stuff/Augment Stuff/Augment Objects/Helper Objects/
// AreaChain.prefab) — AreaEffectPD isn't tied to any one augment. notifyOnHit: false (matches
// ShieldBreakBurstAugment's own choice) — this augment is itself an _onHit listener, so re-notifying _onHit
// for every enemy this AoE damages would recursively re-trigger every onhit augment (including, in principle,
// this one again) off a hit that was never a real weapon strike; every real weapon-strike-only field
// (isTip/weaponRadius/weaponOffset/weaponRangeMultiplier) would read as unset/false on such a re-notify
// anyway, but skipping notifyOnHit entirely avoids relying on that as a safety net.
//
// Radius = Stats.RangeWorldUnits * radiusMultiplier(0.75) * Stats.AreaEffectSize — deliberately NOT
// Stats.RangeScalingAoEBase (the (0.5*Size+RangeWorldUnits)*AreaEffectSize shape DashExplosionAugment/
// ShieldBreakBurstAugment/DashHealAugment share): this augment's own spec is a plain Range-only scale, no
// Size-based floor term. Stats.RangeWorldUnits (2026-09-16, was Stats.WeaponRange directly, back when that
// property itself carried the real-world-distance compression — see RangeWorldUnits' own doc comment) — the
// TRUE, un-Archer/Bladeweaver-scaled Range stat, not EffectiveWeaponRange, same "AoE Effects reads the real
// stat, not the caster's true weapon extent" convention KnightAreaStunHealAbilityPD/RangeScalingAoEBase's own
// doc comments already establish — just expressed as a genuine world-space distance now instead of the raw
// player-facing stat number.
//
// damagePercent * data.onHitPower (THIS specific hit's own already-WeaponCount-divided, already-ricochet-
// decayed onHitPower — same convention OnHitDamageDistanceAugment's own preRoll uses, not Stats.OnHitPower)
// is passed straight through as AreaEffectPD's own damagePercent parameter — Cast()'s internal damage formula
// is just owner.Strength * damagePercent, so pre-multiplying damagePercent by data.onHitPower here reproduces
// "Strength * damagePercent * onHitPower" exactly without needing any change to the shared helper itself.
//
// forcedCrit/forcedHighroll: data.isCrit/data.isHighroll (2026-09-14) — the whole blast crits/highrolls in
// lockstep with the triggering Tip Strike itself rather than each enemy in range rolling its own independent
// chance, same "onhit/onstrike effects inherit rather than roll" convention every other onhit-triggered damage
// effect in this codebase now follows (see WeaponDamageMath.RollDamageInherited's own doc comment).
public class TipStrikeRangeExplosionAugment : AugmentEffectPD<TipStrikeRangeExplosionAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float damagePercent = 0.2f;
    // Radius = Stats.RangeWorldUnits * radiusMultiplier * Stats.AreaEffectSize — see class doc comment. 0.75
    // (2026-09-14, was 0.5) is this augment's own authored spec; tune feel here without touching the
    // Range/AreaEffectSize scaling itself. Public, not [SerializeField] private (2026-09-17 fix) — so
    // AugmentDescriptionFormatter's reflection can resolve {radiusMultiplier} directly as a description
    // token; only public fields/properties/methods are checked. Still Inspector-serializable either way.
    public float radiusMultiplier = 0.75f;

    // Baseline preview at 100% onHitPower (the per-hit onHitPower factor varies hit-to-hit, so isn't baked in
    // here — same "baseline coefficient, not a live roll" convention every other Live*Ratio in this codebase
    // uses) for AugmentDescriptionFormatter's {LiveStrengthRatio} token.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * damagePercent)} "
            : "";

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Can reuse the exact same "AreaChain" prefab DashExplosionAugment/ShieldBreakBurstAugment already
    // reference — pooled, so this is cheap even on frequent Tip Strike procs. Must be registered in
    // PredictedPrefabs.asset like any other spawned effect (already true if reusing that same prefab).
    [Header("Chain (spawned on every Tip Strike)")]
    [SerializeField] private GameObject chainPrefab;

    protected override void OnApply()
    {
        Stats.AddHitListener(OnHit);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveHitListener(OnHit);
    }

    private void OnHit(WeaponHitData data)
    {
        if (Stats == null || Stats.IsDead) return;
        if (!data.isTip) return;
        if (chainPrefab == null) return;
        if (!data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        Vector3 castPosition = enemy.transform.position;
        float radius = Stats.RangeWorldUnits * radiusMultiplier * Stats.AreaEffectSize;
        float effectiveDamagePercent = damagePercent * data.onHitPower;

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, radius, radiusVFXPrefab,
                damagePercent: effectiveDamagePercent, notifyOnHit: false, vfxPerCast: true,
                forcedCrit: data.isCrit, forcedHighroll: data.isHighroll);
    }
}
