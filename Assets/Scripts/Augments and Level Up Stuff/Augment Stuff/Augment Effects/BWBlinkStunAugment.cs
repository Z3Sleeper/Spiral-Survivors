using PurrNet.Prediction;
using UnityEngine;

// Bladeweaver BWBlink-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [BWBlink] — scripts-only convention, that part is Editor-side, not here.
//
// After the blink lands, releases an instant AoE (no damage) at the caster's own position that stuns
// nearby enemies for stunDuration, knocks them back (Stats.Knockback * knockbackMultiplierPercent, 3x
// default — "300% knockback"), and can also interrupt them — same combined Stun+Interrupt (hard CC) shape
// KnightBuffStunAbilityPD's own TriggerAoE uses: interrupt: true feeds the generic SI/FSI event pair via
// CharacterStatsPD.NotifyInterrupted (passing stunDuration as the paired-stun amount, which grants a
// CC-resist-immune InterruptTrueStun bonus only to enemies actually interrupted), while the plain
// onEnemyHit Stun() lands on every enemy hit regardless — ApplyStun's own Mathf.Max semantics mean the two
// can never fight. Uses the shared "AoE Effects" AreaEffectPD chain (same prefab family
// DashExplosionAugment/ShieldBreakBurstAugment/KnightBuffStunAbilityPD all use) with the same
// VFXManager.PlayShockwaveVfx opt-in ShieldBreakBurstAugment's own doc comment describes — called directly
// from the event handler, not deferred to UpdateView(), since _onAbilityStart (AddAbilityStartListener) is
// a genuine PredictedEvent, guaranteed exactly-once-per-real-occurrence (same safety class as
// ShieldBreakBurstAugment's own _onShieldBreak listener; KnightBuffStunAbilityPD's OWN deferred-VFX dance
// was only needed there because its trigger path ALSO included a raw, non-PredictedEvent-gated Simulate()-
// context call from Activate()'s own recast branch — not a concern here).
//
// Triggered by CharacterStatsPD._onAbilityStart filtered to AbilityIdentity.BWBlink, not a BWBlinkAbilityPD
// code change — NotifyAbilityStart already fires from ApplyBuffAndCooldown() (the ability's own single
// shared grant method, reached from BOTH a real Activate() cast and the Blade-Dance-return auto-cast),
// AFTER the teleport/position writes in the real-cast path, so Stats.transform.position is already the
// post-blink landing spot by the time this fires in either case — "after the blink" is correct for the
// auto-cast too, even though nothing physically teleported there (the blades already arrived under their
// own power, same position either way).
public class BWBlinkStunAugment : AugmentEffectPD<BWBlinkStunAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Guards FirstSuccessfulInterrupt (FSI) — reset right before every cast (this is a single instant
        // AoE, not a multi-tick chain, so there's no separate "per activation" reset point needed beyond
        // that), so a whole burst fires FSI at most once, the first time any enemy is actually interrupted.
        // Same shape KnightBuffStunAbilityPD.State.interruptedThisCast uses.
        public bool interruptedThisCast;
        public void Dispose() { }
    }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {stunDuration}/{knockbackMultiplierPercent}/{radiusMultiplier} directly as description tokens; only
    // public fields/properties/methods are checked. "Percent" suffix at the END on knockbackMultiplierPercent
    // so its auto-*100 formatting fires (EndsWith("Percent") check) — 3f displays as "300%".
    public float stunDuration = 1f;
    public float knockbackMultiplierPercent = 3f;
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — see project_aoe_radius_formula_convention.
    public float radiusMultiplier = 1.25f;

    // Must be an AreaEffectPD-carrying prefab registered in PredictedPrefabs.asset — can reuse the exact
    // same prefab DashExplosionAugment/ShieldBreakBurstAugment/KnightBuffStunAbilityPD already spawn, since
    // the class itself isn't tied to any one of them.
    [Header("Chain (spawned on every blink)")]
    [SerializeField] private GameObject chainPrefab;

    protected override void OnApply()
    {
        Stats.AddAbilityStartListener(OnAbilityStart);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (ability.Identity != AbilityIdentity.BWBlink) return;
        if (Stats == null || Stats.IsDead || chainPrefab == null) return;

        currentState.interruptedThisCast = false;

        Vector3 castPosition = Stats.transform.position;
        float radius = Stats.RangeScalingAoEBase * radiusMultiplier;
        float knockback = Stats.Knockback * knockbackMultiplierPercent;

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        // dealDamage: false (default) — no damage, per spec. interrupt: true applied BEFORE the plain
        // onEnemyHit Stun() below (same ordering AreaEffectCastHelper.Cast's own interrupt param always
        // uses) — see class doc comment for why the two combine rather than compete.
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, radius, null,
                hasKnockback: true, knockback: knockback, interrupt: true,
                onEnemyHit: enemy => enemy.Stun(stunDuration),
                onSuccessfulInterrupt: OnEnemyInterrupted,
                vfxPerCast: true);

        // Shockwave VFX opt-in — safe unconditionally here, see class doc comment on why this call site
        // (a genuine PredictedEvent listener) doesn't need KnightBuffStunAbilityPD's own deferred-to-
        // UpdateView dedup dance.
        VFXManager.instance?.PlayShockwaveVfx(castPosition, Stats.Size, Stats.PlayerColor, Stats.PlayerColor, radius);
    }

    // Fired by AreaEffectCastHelper.Cast for every enemy this cast actually interrupted — feeds the generic
    // SI/FSI events, gated to fire FSI at most once per burst via currentState.interruptedThisCast. Passes
    // stunDuration as the paired-stun amount, added to InterruptTrueStun's own flat portion for every enemy
    // this cast successfully interrupts.
    private void OnEnemyInterrupted(IEnemyPD enemy) =>
        Stats.NotifyInterrupted(enemy, ref currentState.interruptedThisCast, stunDuration);
}
