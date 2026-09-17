using PurrNet.Prediction;
using UnityEngine;

// Same family/shape as OnHitDamageAugment (same _onHit trigger, same Strength*onHitPower-scaled damage, same
// "inherit isCrit AND isHighroll from the trigger via WeaponDamageMath.RollDamageInherited, never
// NotifyCritStrike on its own" convention) — see that file's own doc comment for the shared reasoning this
// doesn't repeat. Adds a second, distance-scaled term on top: onHitStrengthPercent plus perUnitDistancePercent
// times the hit's own normalized distance from the player — every point of Range invested raises the CEILING
// on this augment's own damage for free, no tip/snipe-style all-or-nothing gate needed.
//
// RETUNED (2026-09-16, post-WeaponRange-rework) — using OnHitDamageAugment's own flat 25% Strength as "how
// much a generic onhit effect should deal" for scale, this is deliberately WEAKER than that at zero Range
// investment (7.5% base, 15% on a Tip Strike) in exchange for real upside a flat augment can't offer: target
// ~40%+ Strength on Tip Strikes by a fully-invested late run. Calibration point: ~30 bonus Range investment by
// run's end (base speed of Range investment is 1 per stat-augment pick at max upgrades, ~2/3 of ~45 total
// levels are augment picks, so ~30 is the realistic ceiling even though not every pick will be Range) — total
// WeaponRange stat 40 (10 base + 30 bonus) at that point.
//
// Both fields end in "Percent" (perUnitDistancePercent specifically renamed, 2026-09-13, from
// distancePercentPerUnit — the old name ended in "PerUnit," not "Percent," so AugmentDescriptionFormatter's
// *100 auto-scale silently wouldn't have applied to it) so both can be referenced directly as description
// tokens with no separate alias property, same convention OnHitDamageAugment's own onHitDamagePercent uses.
//
// perUnitDistancePercent's own derivation (2026-09-16): hitDistance (and therefore normalizedDistance below)
// is a REAL WORLD UNIT distance, not the player-facing "Range" stat number — see CharacterStatsPD.WeaponRange's
// own doc comment for the full 0.2f-compression history this has to account for. 40 total Range = 8 real-world
// units (40 * 0.2). Target: the distance-scaling term alone should contribute 25 percentage points (of the
// 40%+ Tip Strike total) at that 8-unit ceiling, AFTER tip-doubling — so pre-doubling that's 12.5 percentage
// points at 8 units, i.e. 1.5625% per unit, rounded up to a clean 1.6%.
//
// Normalized against ALL THREE of data.weaponRadius/weaponOffset/weaponRangeMultiplier (2026-09-14, was
// weaponRangeMultiplier alone) — factoring out weaponRadius/weaponOffset (both flat, subtracted) on top of
// weaponRangeMultiplier (a scale, divided) is what makes this scale off genuine WeaponRange investment alone,
// regardless of build: without weaponRadius, Bladeweaver (this project's only character with WeaponRadius > 0)
// or anyone with OuterTrackAugment's own outer ring would read every hit as farther-reaching than an
// equivalent WeaponRange investment on any other class/track, purely because their weapons orbit farther from
// the player before even reaching outward at all — the exact same "bait option, quietly worse/better for one
// specific character/pick" problem weaponRangeMultiplier already existed to prevent for Archer's own bow
// boost, just for two different stats. weaponOffset alone (the universal bonusSizePercent*0.5f clearance
// every weapon's orbit gets) is a flat floor every hit already carries regardless of build, so it's always
// worth subtracting even with no Radius/OuterTrack investment at all. hitDistance/weaponRadius/weaponOffset/
// weaponRangeMultiplier all default to 0 on any onhit trigger that isn't a real weapon strike (AoE effects,
// PhantomBladePD's own onhit chain) — this augment's distance term naturally reads as 0 for those instead of
// needing a separate filter, same "0 means unset" convention every consumer of these fields already follows.
//
// Tip hits (WeaponHitData.isTip, "snipe" for ranged) double this augment's own damage outright — see OnHit's
// own comment for why this needs no separate "was this a real weapon strike" filter. LiveStrengthRatio's own
// preview deliberately does NOT include this doubling (same "don't bake a conditional, not-every-hit bonus
// into the baseline preview" convention crit/onHitPower already follow) — it's a real, not-guaranteed-every-
// hit bonus on top, not part of this augment's own baseline.
public class OnHitDamageDistanceAugment : AugmentEffectPD<OnHitDamageDistanceAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 0.075 = 7.5% (2026-09-16, was 0.05) — 15% on a Tip Strike once doubled. See class doc comment for the
    // full retune derivation.
    public float onHitStrengthPercent = 0.075f;
    // 0.016 = 1.6% per real-world-unit of normalized distance (2026-09-16, was 0.05) — see class doc comment
    // for the full derivation against the ~8-real-world-unit (40 total Range) calibration point.
    public float perUnitDistancePercent = 0.016f;

    // onHitStrengthPercent ALONE — the only piece of this formula that doesn't vary hit-to-hit (onHitPower and
    // the distance term both do, same "baseline coefficient, not a live roll" convention every other
    // Live*Ratio in this codebase uses; the tip-doubling is likewise left out, same as crit/onHitPower always
    // are — see class doc comment). The description states the doubling and per-unit scaling separately in
    // plain text instead of folding them into this live number.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * onHitStrengthPercent)} "
            : "";

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
        if (!data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        // Factors out weaponRadius/weaponOffset (flat) and weaponRangeMultiplier (scale) — see class doc
        // comment for the full reasoning. rangeMultiplier: 0 (unset, every non-weapon-strike onhit trigger)
        // reads as neutral 1, same convention CharacterStatsPD's own *Percent tiers use. Clamped to 0 (not
        // left to go negative) — guards the theoretical case of a hit landing closer than the weapon's own
        // orbit+clearance would normally allow (e.g. an enemy that moved inside the orbit radius mid-hitbox-
        // overlap); Radius/Offset themselves are never negative on any real construction site.
        float rangeMultiplier = data.weaponRangeMultiplier > 0f ? data.weaponRangeMultiplier : 1f;
        float normalizedDistance = Mathf.Max(0f, data.hitDistance - data.weaponRadius - data.weaponOffset) / rangeMultiplier;

        // Tip hits (WeaponHitData.isTip — "snipe" for ranged) double this augment's own damage outright.
        // Naturally scoped to genuine weapon strikes only, with no separate filter needed: isTip is false by
        // design on every onhit trigger that isn't one (AoE effects, PhantomBladePD's own onhit chain — see
        // that field's own doc comment), so this just multiplies by 1 for those instead of requiring a check.
        float tipMultiplier = data.isTip ? 2f : 1f;

        // data.onHitPower already divides by WeaponCount (see OnHitDamageAugment's own preRoll comment for
        // the full reasoning) — Stats.Strength stays undivided so onHitPower is the only WeaponCount divisor.
        // Anything that deals damage rolls luck variance — no exceptions. Mathf.Max(0f, ...) on ONLY the
        // distance-scaling bonus (2026-09-16, explicit guard, corrected — was wrongly clamping the COMBINED
        // sum, which could floor the guaranteed onHitStrengthPercent base down to 0 too instead of just
        // preventing the distance bonus from ever being negative). onHitStrengthPercent itself is never
        // touched by this clamp — the base always applies in full regardless of what the distance term does.
        float distanceBonus = Mathf.Max(0f, perUnitDistancePercent * normalizedDistance);
        float strengthPercent = onHitStrengthPercent + distanceBonus;
        float preRoll = Stats.Strength * strengthPercent * data.onHitPower * tipMultiplier;
        float rolled = WeaponDamageMath.RollDamageInherited(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, data.isHighroll);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, data.isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default, data.isHighroll);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = data.isHighroll, attacker = Stats, isPriority = false });
    }
}
