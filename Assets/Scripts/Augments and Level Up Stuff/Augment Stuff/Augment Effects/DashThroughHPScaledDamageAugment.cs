using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Same sweep-detection shape as DashThroughInterruptAugment — see that class's own doc comment for the full
// mechanism (swept sphere-cast following the player each tick while dashing, MovementStarted/MovementEnded-
// driven, touchedEnemies dedupes repeat hits on the same enemy across multiple ticks of one dash). This
// augment's own per-enemy payload, once per enemy touched during a dash:
//
// Deals (strengthPercent * Stats.Strength + bonusHealthPercent * Stats.BonusHealth) damage — ONE combined
// luck roll on the summed total, same "roll the whole pre-roll value once" convention every other damage
// source in this codebase uses (not two separate rolls per term). Rolls its own independent crit per enemy
// (Stats.NextFloat() < Stats.CritChance, off this player's own dedicated RNG stream — was GameRandom.instance
// until 2026-10-16) — same "direct damage source" category as
// DashExplosionAugment/PhantomBladePD, not an onhit-reactive effect, so it deliberately does NOT call
// Stats.NotifyCritStrike (would feed back into CritTriggerOnHitAugment's own "crit triggers another onhit"
// chain) — matches OnHitDamageAugment's identical reasoning. isPriority: true on the damage number (full-
// size text, not onhit-noise-sized) — this is a primary damage source, not an onhit-triggered proc.
//
// No longer calls Stats.NotifyHit (removed 2026-10-18, per explicit instruction) — this augment no longer
// triggers onhit-reactive augments (OnHitDamageAugment, CritTriggerOnHitAugment, etc.) at all, matching
// ShieldBreakBurstAugment's own "no onhit trigger" spec.
public class DashThroughHPScaledDamageAugment : AugmentEffectPD<DashThroughHPScaledDamageAugment.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        // True between OnMovementStarted and OnMovementEnded — the active sweep window.
        public bool isSweeping;
        public Vector3 sweepPrevPos;
        // Dedupes repeat damage on the same enemy across multiple ticks of one dash — same convention as
        // DashThroughInterruptAugment/EnemyBaseState.killLaunchHitIds.
        public DisposableList<PredictedObjectID> touchedEnemies;
        public void Dispose() { touchedEnemies.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.touchedEnemies = touchedEnemies.Duplicate();
            return copy;
        }
    }

    // 1f = 100% (this codebase's percent fields store the fraction). Public (not [SerializeField] private)
    // so AugmentData.description can reference {strengthPercent}/{bonusHealthPercent} directly — see
    // AugmentDescriptionFormatter's own doc comment for the reflection-token contract.
    public float strengthPercent    = 1f;
    public float bonusHealthPercent = 1f;

    // "Dash Through"/contact-on-dash sweep radius convention: 1.5f — same as DashThroughInterruptAugment's
    // own sweepRadius, see its field comment for why this family diverges from the general 1.25f Player
    // radius-effect convention.
    [SerializeField] private float sweepRadius = 1.5f;

    // Baseline damage this would currently deal, at 100% (no luck roll — baseline preview, not a randomly-
    // sampled one, same convention every other LiveXRatio token uses) for AugmentDescriptionFormatter's
    // {LiveDamageRatio} token.
    public string LiveDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * strengthPercent + stats.BonusHealth * bonusHealthPercent)} "
            : "";

    private PlayerControllerPD _controller;
    // Matches DashThroughInterruptAugment's own _sweepResults buffer size — this is the exact same kind of
    // short-lived, per-tick swept-hitbox query.
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];

    protected override State GetInitialState()
    {
        var s = default(State);
        s.touchedEnemies = DisposableList<PredictedObjectID>.Create(8);
        return s;
    }

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null)
        {
            _controller.AddDashMovementStartedListener(OnMovementStarted);
            _controller.AddDashMovementEndedListener(OnMovementEnded);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null)
        {
            _controller.RemoveDashMovementStartedListener(OnMovementStarted);
            _controller.RemoveDashMovementEndedListener(OnMovementEnded);
        }
    }

    private void OnMovementStarted()
    {
        currentState.isSweeping = true;
        currentState.sweepPrevPos = Stats.transform.position;
        currentState.touchedEnemies.Clear();
    }

    // interrupted (2026-10-10, PlayerControllerPD._onDashMovementEnded payload addition) — unused here,
    // this sweep only cares that the window closed, not why, same as before that payload existed.
    private void OnMovementEnded(bool interrupted)
    {
        currentState.isSweeping = false;
        if (Stats == null || _controller == null) return;

        // Catch-up sweep — see DashThroughInterruptAugment.OnMovementEnded's own doc comment for why this is
        // needed (BWBlinkAbilityPD's instant, same-tick teleport) and why it's a harmless no-op for a real
        // multi-tick dash.
        Vector3 currentPos = Stats.transform.position;
        SweepSegment(ref currentState, currentState.sweepPrevPos, currentPos);
        currentState.sweepPrevPos = currentPos;
    }

    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (!state.isSweeping) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || _controller == null) return;

        Vector3 currentPos = Stats.transform.position;
        Vector3 prevPos = state.sweepPrevPos;
        state.sweepPrevPos = currentPos;

        SweepSegment(ref state, prevPos, currentPos);
    }

    // Shared by LateSimulate()'s own per-tick sweep and OnMovementEnded()'s catch-up sweep.
    private void SweepSegment(ref State state, Vector3 fromPos, Vector3 toPos)
    {
        Vector3 segment = toPos - fromPos;
        float dist = segment.magnitude;
        if (dist < 0.0001f) return;
        Vector3 dir = segment / dist;
        float radius = sweepRadius * Stats.Size;

        int n = Physics.SphereCastNonAlloc(fromPos, radius, dir, _sweepResults, dist, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            if (!_sweepResults[i].collider.TryGetComponent(out IEnemyPD enemy)) continue;
            if (enemy._isDead) continue;
            if (state.touchedEnemies.Contains(enemy.ObjectId)) continue;
            state.touchedEnemies.Add(enemy.ObjectId);

            DealDamage(enemy);
        }
    }

    private void DealDamage(IEnemyPD enemy)
    {
        bool isCrit = Stats.NextFloat() < Stats.CritChance;

        float baseDamage = Stats.Strength * strengthPercent + Stats.BonusHealth * bonusHealthPercent;
        float rolled = WeaponDamageMath.RollDamage(baseDamage, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, Stats.NextFloat, out bool isHighroll);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, isHighroll, isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, isCrit, Stats.owner ?? default, isHighroll, Stats.executeThreshold, Stats.critExecuteThreshold);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = isCrit, isHighroll = isHighroll, attacker = Stats, isPriority = true });
    }
}
