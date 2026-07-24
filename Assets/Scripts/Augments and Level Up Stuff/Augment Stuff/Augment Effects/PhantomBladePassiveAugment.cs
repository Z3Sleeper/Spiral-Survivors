using PurrNet.Prediction;
using UnityEngine;

// Not a port — the user's current intended design supersedes whatever the old system's
// PhantomBladePassiveAugment implied. Periodically summons Phantom Blades at the closest enemies to the
// player, entirely independent of combat events (unlike PhantomBladeOnCritAugment, which reacts to
// crits). The periodic timer lives in predicted State and ticks in Simulate(), same as
// CharacterStatsPD's own health/shield regen timers — and per AugmentEffectPD's own doc comment, calling
// PhantomBladeSummonHelper's hierarchy.Create() directly from Simulate() is safe here (Create() is
// deterministic/idempotent under replay, unlike a plain stat += would be).
public class PhantomBladePassiveAugment : AugmentEffectPD<PhantomBladePassiveAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        public void Dispose() { }
    }

    // Large enough to reliably reach enemies out at EnemySpawnerPD's own max spawn distance (40) with room
    // to spare — not exposed as its own tunable since the user's spec doesn't call it out as one.
    private const float TargetSearchRadius = 50f;

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float baseTimer = 3f;
    [SerializeField] private int   bladeCount = 1;
    [SerializeField] private float damageMultiplier = 0.5f;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;

    protected override State GetInitialState() => new() { timer = baseTimer };

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;

        // Stats.AttackSpeedPercent excludes both the flat-style attackSpeedFlat stacking AND Surge (only
        // Stats.AttackSpeed itself carries the Surge multiplier) — dividing it out of AttackSpeed against
        // baseAttackSpeed gives the true current speed multiplier including Surge, recomputed every tick so
        // a Surge starting/ending mid-countdown speeds this up/down immediately instead of only affecting
        // the next reset. Same base-divided-by-multiplier convention as CharacterStatsPD.DashCooldown.
        float speedMultiplier = Stats.AttackSpeed / Stats.baseAttackSpeed;
        state.timer -= delta * speedMultiplier;
        if (state.timer > 0f) return;

        state.timer = baseTimer / speedMultiplier;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, TargetSearchRadius, bladeCount, damageMultiplier);
    }
}
