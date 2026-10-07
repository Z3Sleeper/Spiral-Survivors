using PurrNet.Prediction;
using UnityEngine;

// The "Range" member of the per-core-stat Phantom Blade summon family (alongside Passive/AttackSpeed,
// OnDash/DashHaste, OnAbilityStart/AbilityHaste, OnHeal/HealthRegen, OnDamaged/Health, OnCrit/Crit,
// OnHighroll/Luck, OnMovement/MoveSpeed, OnEnergySpent/EnergyRegen) — NOT a blanket buff like
// PhantomBladeSizeAugment/PhantomBladeCarnivalAugment.
//
// REWORKED (2026-09-13) — the original design (accumulate WeaponHitData.hitDistance * onHitPower toward a
// flat threshold) turned out to reward attack SPEED far more than Range investment in practice (more hits per
// second simply filled the tracker faster, regardless of how far those hits actually reached), making this
// functionally an attack-speed augment wearing a Range costume. Now matches PhantomBladeOnCritAugment/
// PhantomBladeOnHighrollAugment's own shape instead: baseTimer (5s) ticks down naturally in Simulate() and
// summons on its own when it runs out, but a real weapon-strike TIP hit (WeaponHitData.isTip — see that
// field's own doc comment; "snipe" for ranged weapons, same flag) ALSO shaves cooldownReductionPerRangeOnTip *
// Stats.BonusWeaponRange * that hit's own onHitPower off the timer directly, same "grant it now, carry the
// overflow into a fresh cycle" shape OnCrit/OnHighroll already use for their own reduction. Scaling the
// reduction by Range directly (not just gating on isTip alone) is the deliberate fix for the original
// problem: investing in Range now makes EVERY tip hit worth more cooldown reduction, rather than the old
// perverse incentive to keep a weapon small so tip hits were merely easier to land.
//
// REWORKED AGAIN (2026-09-14) — switched from Stats.WeaponRange (the character's full, current Range stat) to
// Stats.BonusWeaponRange (see that property's own doc comment): scales purely off Range INVESTED beyond the
// character's own raw baseline, doubling the per-point coefficient (0.05 -> 0.1) to compensate. A character
// with zero bonus Range investment now gets no cooldown reduction from tip hits at all (a genuine no-op, not
// just a small baseline number) — steeper reward for Range-focused builds, explicit nerf to the baseline case.
public class PhantomBladeRangeAugment : AugmentEffectPD<PhantomBladeRangeAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    // Real cooldown duration, same convention as OnCrit/OnHighroll's own baseTimer (their comments explain
    // why this is a genuine cooldown, not a rapid re-roll interval).
    [SerializeField] private float baseTimer = 5f;
    // Per-tip-hit cooldown reduction, PER POINT OF BonusWeaponRange — 0.1 (2026-09-14, was 0.05 scaled off
    // full WeaponRange) means 0.1s off the timer per point of Range INVESTED beyond baseline, per tip hit,
    // before onHitPower scaling; 0 bonus Range investment means 0 reduction. This is the field that actually
    // makes Range investment matter: more invested Range means every tip hit is worth more cooldown
    // reduction, not just that tip hits happen to land sometimes.
    public float cooldownReductionPerRangeOnTip = 0.1f;
    public int bladeCount = 3;
    public float strengthMultiplierPercent = 0.6f;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, strengthMultiplierPercent))} "
            : "";

    // Mirrors ReduceCooldown's own per-tip-hit formula exactly (cooldownReductionPerRangeOnTip *
    // BonusWeaponRange), minus the onHitPower factor (varies hit-to-hit, not baked into a static preview —
    // same "baseline coefficient" convention as OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc
    // comment). Outside a live match, falls back to "" via stats == null short-circuiting below, same as
    // every other Live*Ratio in this family.
    public string LiveTipReductionRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(cooldownReductionPerRangeOnTip * stats.BonusWeaponRange)} "
            : "";

    protected override State GetInitialState() => new() { timer = baseTimer };

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction), matching Passive/OnCrit/etc.'s own convention. Only PhantomBladeSizeAugment itself
        // touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.AddStrikeListener(OnStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStrikeListener(OnStrike);
    }

    // _onStrike, not _onHit — fires exactly once per real strike, never doubled by CritTriggerOnHitAugment
    // re-invoking _onHit, and never fired at all by PhantomBladePD's own hits (PhantomBladeOnHitAugment only
    // ever calls NotifyHit, never NotifyStrike — see PhantomBladePD.ProcessHit) — so this can never feed
    // itself into a runaway summon loop even if PhantomBladeOnHitAugment is also active. isTip-gated —
    // WeaponHitData.isTip is scoped to genuine weapon strikes only (WeaponMeleePD/WeaponAxePD/
    // WeaponRangedProjectilePD, "snipe" for the ranged case) and is false by default everywhere else, so a
    // non-tip strike simply no-ops here rather than needing its own separate check.
    private void OnStrike(WeaponHitData data)
    {
        if (Stats == null || Stats.IsDead) return;
        if (!data.isTip) return;
        ReduceCooldown(cooldownReductionPerRangeOnTip * Stats.BonusWeaponRange * data.onHitPower);
    }

    // Overflow-safe reduction — same "grant it now, carry the remainder into a fresh cycle, repeat" shape
    // PhantomBladeOnCritAugment/PhantomBladeOnHighrollAugment's own identical ReduceCooldown already uses.
    // Loops rather than a single subtract-and-clamp so a reduction big enough to span more than one full
    // cycle (extreme onHitPower/WeaponRange stacking) still summons once per cycle it actually crosses, not
    // just once. safety bounds the loop defensively against a degenerate baseTimer <= 0 configuration — never
    // expected to matter at this augment's own tuned values.
    private void ReduceCooldown(float amount)
    {
        int safety = 1000;
        while (amount > 0f && safety-- > 0)
        {
            if (amount < currentState.timer)
            {
                currentState.timer -= amount;
                return;
            }

            amount -= currentState.timer;
            currentState.timer = baseTimer;
            Summon();
        }
    }

    private void Summon()
    {
        if (phantomBladePrefab == null) return;
        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, strengthMultiplierPercent);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;

        state.timer -= delta;
        if (state.timer > 0f) return;

        state.timer = baseTimer;
        Summon();
    }
}
