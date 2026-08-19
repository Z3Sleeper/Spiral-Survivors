using PurrNet.Prediction;
using UnityEngine;

// Same family shape as PhantomBladeOnCritAugment (see its own doc comment for the full architecture
// reasoning) — summons at the closest enemies for unpenalized Strength damage, grants the usual +Phantom
// Blade Damage/Size pair. Triggered off BOTH CharacterStatsPD._onHeal (heals THIS player received — self-
// heals and ally-sourced heals landing on them) AND _onAllyHealGranted (heals THIS player granted to someone
// ELSE, via their own heal-ally effects, e.g. OnStrikeHealAllyAugment/DashHealAugment) — both accumulate
// toward the same flat healthNeeded threshold, so "how much healing this player is responsible for" is what
// counts, not just healing they personally received. No double-counting across two different players: a
// heal-ally effect's self-heal branch fires _onHeal on the caster (once), the genuinely-other-ally branch
// fires _onHeal on the ALLY's own separate Stats/augment instance (irrelevant here) and _onAllyHealGranted on
// the caster's own Stats (once) — each real heal only ever increments exactly one of this augment's own
// instances. No WeaponCount-divide needed here (unlike the crit version) since healing has no relationship to
// weapon count at all. Same "plain field, mutated only inside the PredictedEvent callback" safety as
// PhantomBladeOnCritAugment's own _critCount — _healAccumulator only ever changes from inside OnHeal/
// OnAllyHealGranted, which PredictedEvent guarantees fires exactly once per real occurrence on every machine.
// Passive HealthRegen deliberately does NOT invoke _onHeal at all (see its own doc comment in
// CharacterStatsPD.Simulate — exactly the "future effect that scales off heal size" case it was written to
// exclude), so this never counts passive trickle, only genuine discrete heals.
public class PhantomBladeOnHealAugment : AugmentEffectPD<PhantomBladeOnHealAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    public float healthNeeded = 50f;
    public int bladeCount = 3;
    public float baseDamageMultiplier = 0.6f;

    // baseDamageMultiplier itself doesn't end in "Percent", so AugmentDescriptionFormatter's *100
    // convention wouldn't apply to it directly — this alias exists purely so a description can reference
    // {DamageMultiplierPercent} and get the correctly-scaled static number, as a fallback for whenever
    // {LiveBladeDamageRatio} itself is blank (out of a live match — see ShowLiveValues's own doc comment).
    public float DamageMultiplierPercent => baseDamageMultiplier;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, baseDamageMultiplier))} "
            : "";

    private float _healAccumulator;

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.AddHealListener(OnHeal);
        Stats.AddAllyHealGrantedListener(OnAllyHealGranted);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveHealListener(OnHeal);
        Stats.RemoveAllyHealGrantedListener(OnAllyHealGranted);
    }

    private void OnHeal(HealData data) => Accumulate(data.amount);
    private void OnAllyHealGranted(AllyHealGrantedData data) => Accumulate(data.amount);

    // While, not if (2026-08-21) — a single heal can be large enough to clear healthNeeded more than once in
    // one call (e.g. a big burst heal landing while this was already partway accumulated); an `if` would only
    // ever summon once and silently strand the rest of that overshoot until the NEXT heal event happened to
    // come in, instead of correctly summoning immediately for every threshold that heal actually cleared.
    // Same "while, not if" guard this codebase already uses wherever a single event's own magnitude can cross
    // more than one scheduled threshold at once (e.g. KingOrbiterPD's own dispatch loops).
    private void Accumulate(float amount)
    {
        _healAccumulator += amount;
        while (_healAccumulator >= healthNeeded)
        {
            _healAccumulator -= healthNeeded;
            if (phantomBladePrefab == null) continue;

            PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
                Stats.transform.position, bladeCount, baseDamageMultiplier);
        }
    }
}
