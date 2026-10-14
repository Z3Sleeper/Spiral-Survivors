using PurrNet.Prediction;
using UnityEngine;

// Reworked (2026-08-24) from an independent periodic CritChance roll (ticked every baseTimer seconds,
// unrelated to actually landing a real crit) to a self-owned cooldown: baseTimer now ticks down over real
// time same as before (still no AttackSpeed scaling), but a real Strike Crit ALSO shaves cooldownReductionOnCrit
// off it directly — same "cooldown ticks naturally, a trigger event shaves more off it" shape every
// ability/dash cooldown-reduction augment in this codebase already uses (OnDodgeReduceAbilityCDAugment/
// OnHitReduceAbilityCDAugment/etc.), just applied to this augment's own internal timer instead of an
// ability's or dash's. Explicit design goal (user's own words): the old
// independent-roll trigger "worked as intended" but felt disconnected from actually landing crits — this
// version summons faster the more you're actually critting, while still guaranteeing a baseline proc even
// without one (the natural baseTimer decay), rather than being purely luck-of-the-tick. baseTimer bumped
// 1s -> 5s in the same pass (set directly on the prefab, not the C# default — see baseTimer's own doc
// comment for why the field itself wasn't renamed).
//
// The reduction can OVERFLOW (explicit user request) — same "grant it now, carry the remainder into a fresh
// cycle, repeat" shape PlayerControllerPD.ReduceDashCooldown already uses for dash charges: if
// cooldownReductionOnCrit exceeds what's left on the timer, the summon fires immediately right there in the
// listener instead of waiting for Simulate() to catch up next tick, and the LEFTOVER deficit carries into a
// fresh baseTimer countdown rather than being discarded or left to sit negative (e.g. timer at 0.1s, a 0.5s
// reduction lands: fires now, then the remaining 0.4s comes off the next baseTimer, landing at 4.6s, not 5s
// or -0.4s). See ReduceCooldown's own doc comment for the loop.
public class PhantomBladeOnCritAugment : AugmentEffectPD<PhantomBladeOnCritAugment.State>
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
    // Scaled by the crit's own onHitPower — onHitPower is usually a net PENALTY in practice, since
    // WeaponCount/WeaponTracks/Multishot picks each halve it (see OnHitPower's own doc comment), so this
    // scaling keeps the reduction proportional rather than a flat amount regardless of investment.
    public float cooldownReductionOnCrit = 0.5f;
    public int bladeCount = 3;
    public float strengthMultiplierPercent = 0.6f;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, strengthMultiplierPercent))} "
            : "";

    protected override State GetInitialState() => new() { timer = baseTimer };

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction — this used to be half the damage bonus, bumped to match it exactly). Only
        // PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.AddCritStrikeListener(OnCritStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveCritStrikeListener(OnCritStrike);
    }

    // Fires only on an actual real Strike Crit — every NotifyCritStrike call site is already isCrit-gated
    // (see CharacterStatsPD.NotifyCritStrike's own callers), so no filtering needed here. PredictedEvent<T>
    // listener — safe to mutate currentState directly (guaranteed exactly once per real crit, regardless of
    // resimulation).
    private void OnCritStrike(WeaponHitData data)
    {
        if (Stats == null || Stats.IsDead) return;
        ReduceCooldown(cooldownReductionOnCrit * data.onHitPower);
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
            Stats.transform.position, bladeCount, strengthMultiplierPercent);
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
