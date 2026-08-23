using PurrNet.Prediction;
using UnityEngine;

// Reworked (2026-08-24) from an independent periodic Luck/Highroll roll (ticked every baseTimer seconds,
// unrelated to actually landing a real highroll) to a self-owned cooldown — same rework/reasoning as
// PhantomBladeOnCritAugment's own identical change, see that file's own doc comment for the full "cooldown
// ticks naturally, a trigger event shaves more off it" shape. baseTimer now ticks down over real time same
// as before (still no AttackSpeed scaling), but a real Strike Highroll ALSO shaves
// cooldownReductionOnHighroll off it directly, scaled by that hit's own onHitPower (same convention
// OnStrikeHighrollReduceDashCDAugment's identical reduction already uses). No longer reacts to actual
// weapon hits any other way. baseDamageMultiplier lowered 6f/600% -> 0.6f/60% in the same pass (set directly
// on the prefab, matching every other Phantom Blade augment's own per-blade damage standard — Highroll no
// longer gets an outsized per-blade payout, its own edge is now purely "the bigger cooldown reduction on a
// rarer trigger" instead) — bladeCount (3) unchanged, already matched PhantomBladeOnCritAugment's own.
//
// The reduction can OVERFLOW (explicit user request) — same "grant it now, carry the remainder into a fresh
// cycle, repeat" shape PlayerControllerPD.ReduceDashCooldown already uses for dash charges: if
// cooldownReductionOnHighroll exceeds what's left on the timer, the summon fires immediately right there in
// the listener instead of waiting for Simulate() to catch up next tick, and the LEFTOVER deficit carries
// into a fresh baseTimer countdown rather than being discarded or left to sit negative (e.g. timer at 0.1s,
// a 2.5s reduction lands: fires now, then the remaining 2.4s comes off the next baseTimer, landing at 2.6s,
// not 5s or -2.4s). See ReduceCooldown's own doc comment for the loop.
public class PhantomBladeOnHighrollAugment : AugmentEffectPD<PhantomBladeOnHighrollAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    // Now a real cooldown duration (5s, set on the prefab) rather than a rapid re-roll interval (1s) — kept
    // this exact field name rather than renaming to baseCooldown, since the prefab's own serialized value
    // was already retuned directly under this name and a rename would silently reset it back to whatever new
    // C# default replaced it.
    [SerializeField] private float baseTimer = 1f;
    // Scaled by the highrolling hit's own onHitPower, same convention OnStrikeHighrollReduceDashCDAugment's
    // identical reduction already uses — see its own doc comment for the full reasoning.
    public float cooldownReductionOnHighroll = 2.5f;
    public int bladeCount = 3;
    public float baseDamageMultiplier = 6f;

    // baseDamageMultiplier itself doesn't end in "Percent", so AugmentDescriptionFormatter's *100
    // convention wouldn't apply to it directly — this alias exists purely so a description can reference
    // {BaseDamageMultiplierPercent} and get the correctly-scaled static number, as a fallback for whenever
    // {LiveBladeDamageRatio} itself is blank (out of a live match — see ShowLiveValues's own doc comment).
    public float BaseDamageMultiplierPercent => baseDamageMultiplier;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, baseDamageMultiplier))} "
            : "";

    protected override State GetInitialState() => new() { timer = baseTimer };

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.AddStrikeHighrollListener(OnStrikeHighroll);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStrikeHighrollListener(OnStrikeHighroll);
    }

    // Fires only on an actual real Strike Highroll — _onStrikeHighroll is only ever invoked from a genuine
    // isHighroll hit (see NotifyStrikeHighroll's own callers). PredictedEvent<T> listener — safe to mutate
    // currentState directly (guaranteed exactly once per real highroll, regardless of resimulation).
    private void OnStrikeHighroll(StrikeHighrollData data)
    {
        if (Stats == null || Stats.IsDead) return;
        ReduceCooldown(cooldownReductionOnHighroll * data.onHitPower);
    }

    // Overflow-safe reduction — see class doc comment. Loops rather than a single subtract-and-clamp so a
    // reduction big enough to span more than one full cycle (extreme onHitPower stacking) still summons once
    // per cycle it actually crosses, not just once. safety bounds the loop defensively against a degenerate
    // baseTimer <= 0 configuration — never expected to matter at this augment's own tuned values.
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
            Stats.transform.position, bladeCount, baseDamageMultiplier);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;

        // Plain delta, no AttackSpeed scaling — see class doc comment.
        state.timer -= delta;
        if (state.timer > 0f) return;

        state.timer = baseTimer;
        Summon();
    }
}
