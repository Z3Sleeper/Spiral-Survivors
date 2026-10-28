using PurrNet.Prediction;

// Executioner BigAxe-specific augment (2026-10-07). This augment's own .asset must set requiredAbilities =
// [ExeBigAxe] — scripts-only convention, that part is Editor-side, not here.
//
// While ExeBigAxeAbilityPD's own active window is running, every hit (any source — BigAxe itself deals no
// damage, it's a self-buff, so this is "while wielding the massive axe," not "struck BY the axe
// specifically," same "while the buff is up" reasoning BWBladeDanceSlowAugment/BWBladeDanceStunAugment use
// for their own ability) inflicts a stacking bleed: bleedPerSecondPercent (10% Strength per second default)
// for bleedDuration (3s), scaled by onHitPower (damage only — duration is never touched by onHitPower).
//
// Listens to _onHit, not the exactly-once _onStrike (reworked 2026-10-08, was _onStrike —
// original framing was "dealt on-strike," but switching to _onHit lets this synergize with anything else
// that routes through _onHit specifically: CritTriggerOnHitAugment re-invoking NotifyHit on a crit now also
// re-triggers this (a crit applies bleed twice while BigAxe is up), and PhantomBladePD/AreaEffectCastHelper
// hits (which only ever call NotifyHit, never NotifyStrike) now apply bleed too, not just genuine weapon
// swings. Every real weapon (Melee/Axe/RangedProjectile/ExeAxeThrowProjectile/RogueSpin) still fires both
// events together, so this doesn't lose any coverage it already had, only gains the extra sources.
//
// Rolled through the FULL standard damage pipeline, same shape OnHitDamageAugment's own doc comment
// describes for an on-hit secondary effect: WeaponDamageMath.RollDamageInherited for luck variance, then
// ApplyCritAndHighroll for the real CritDamage/HighrollBonus multipliers — both INHERITING data.isCrit/
// data.isHighroll from the triggering strike rather than independently re-rolling ("if the trigger
// crits/highrolls, this does too"). On top of that standard crit scaling, a crit ALSO doubles the whole
// result again (2026-10-07, explicit request — "in addition to crit damage, double the total damage," so
// a separate flat critBleedPerSecondPercent variable is no longer needed at all; crit scaling now comes
// from the player's real CritDamage stat via the normal pipeline, with this extra doubling layered on top).
//
// Each stack's fully-resolved per-second rate (already luck-rolled, crit-scaled, and extra-doubled if
// applicable) is handed to IEnemyPD.ApplyBleed — see EnemyBasePD.ApplyBleed's own doc comment for the
// shared-timer/additive-magnitude bucket mechanism (accepted as a stopgap per
// [[project_bleed_bucket_status_system]] until/unless a second DoT status ever justifies a real generic
// status-effect system).
public class ExeBigAxeBleedAugment : AugmentEffectPD<ExeBigAxeBleedAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {bleedPerSecondPercent}/{bleedDuration} directly as description tokens; only public fields/
    // properties/methods are checked. "Percent" suffix at the END on the rate field so its auto-*100
    // formatting fires. bleedDuration is a raw seconds value, no suffix needed.
    public float bleedPerSecondPercent = 0.1f;
    public float bleedDuration = 3f;

    // Baseline per-stack rate at 100% HealPower-equivalent (onHitPower/luck/crit variance aren't included
    // since all three vary per-hit, not a stat to preview) for AugmentDescriptionFormatter's
    // {LiveBleedPerSecondRatio} token — same shape every other rate-based augment's own Live*Ratio uses.
    // Reads _bigAxe directly (a field on this same instance, cached in LateAwake()) rather than taking it as
    // a param — Live*Ratio methods are reflection-invoked with a fixed (CharacterStatsPD) signature, so
    // anything else this preview needs has to come from the instance itself.
    public string LiveBleedPerSecondRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * bleedPerSecondPercent * (_bigAxe != null ? _bigAxe.buffStrengthMultiplier : 1f))} "
            : "";

    // Cached once — see KnightBerserkHealAugment._berserk's own identical caching pattern, avoids a fresh
    // GetComponent call on every single strike (this event can fire very often in a busy fight).
    private ExeBigAxeAbilityPD _bigAxe;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _bigAxe = Stats.GetComponent<ExeBigAxeAbilityPD>();
    }

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
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeBigAxeAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (_bigAxe == null || !_bigAxe.IsActive) return;
        if (!data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        // Anything that deals damage rolls luck variance — no exceptions. onHitPower scales damage only —
        // bleedDuration below is never touched by it. _bigAxe.buffStrengthMultiplier (2026-10-08) folds in
        // ExeBigAxeBuffAugment's own shared amplifier — same multiplier that already scales Range/Execute
        // Threshold/Crit Chance/Knockback/Attack Speed on the ability itself — so picking Buff now also
        // amps this augment's bleed rate, adding cross-augment synergy within the BigAxe family. 1f neutral
        // default (every BigAxe throw without the Buff augment), so this is a true no-op without it.
        float preRoll = Stats.Strength * bleedPerSecondPercent * data.onHitPower * _bigAxe.buffStrengthMultiplier;
        float rolledPerSecond = WeaponDamageMath.RollDamageInherited(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, data.isHighroll, Stats.NextFloat);
        rolledPerSecond = WeaponDamageMath.ApplyCritAndHighroll(rolledPerSecond, data.isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);
        // Extra flat doubling on top of the normal crit multiplier above — explicit design spec.
        if (data.isCrit) rolledPerSecond *= 2f;

        // executeThresholdPercent/critExecuteThresholdPercent (2026-10-08 perf fix) — captured directly off
        // Stats here, while the live reference is already in hand, rather than making every future bleed
        // tick re-look it up via CharacterStatsPD.All — see IEnemyPD.TakeDamage's own doc comment.
        enemy.ApplyBleed(rolledPerSecond, bleedDuration, Stats.owner ?? default, Stats.executeThreshold, Stats.critExecuteThreshold);
    }
}
