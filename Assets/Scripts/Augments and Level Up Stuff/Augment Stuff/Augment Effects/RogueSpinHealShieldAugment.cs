using PurrNet.Prediction;
using UnityEngine;

// Rogue Spin-specific augment (2026-10-04). This augment's own .asset must set requiredAbilities =
// [RogueSpin] — scripts-only convention, that part is Editor-side, not here.
//
// On cast (ability start, not end — unlike RogueSpinAttackSpeedAugment's own post-ability trigger),
// instantly refreshes Shield to full (Stats.RechargeShield — same instant-refill-and-fire-the-recharge-
// edge-event entry point DeathPreventionShieldDRAugment's own death save uses) and heals
// healFlat + Stats.BonusHealthRegen * bonusHealthRegenPercent (5 + 500% default) — same
// "healBase + BonusHealthRegen * percent" formula every other Health-Regen-scaled heal in this codebase
// uses (SurgeHealAugment/DashHealAugment/OnStrikeHealAllyAugment), routed through the shared Heal() choke
// point so it correctly picks up HealPower/CritHealPower/the always-on crit roll for free.
//
// Pattern C (see KnightBerserkMoveSpeedAugment's own doc comment for Patterns A/B, RogueSpinAttackSpeedAugment
// for the post-ability variant) — subscribes to the EXISTING shared CharacterStatsPD._onAbilityStart event
// (AddAbilityStartListener, same event PhantomBladeOnAbilityStartAugment already consumes) filtered by
// ability.Identity == RogueSpin, zero changes to RogueSpinAbilityPD itself. Both effects are plain one-shot
// grants, not an ongoing accumulator — direct Stats.RechargeShield()/Stats.Heal() calls from the event
// handler are safe here for the same reason PhantomBladeOnAbilityStartAugment's own direct SummonAtClosestEnemies
// call is: PredictedEvent handlers already guarantee exactly-once-per-real-event firing, no deferred-delta/
// UpdateView() dance needed for a true one-shot effect.
public class RogueSpinHealShieldAugment : AugmentEffectPD<RogueSpinHealShieldAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {healFlat}/{bonusHealthRegenPercent} directly as description tokens; only public fields/properties/
    // methods are checked. Still Inspector-serializable either way. 5f = 500% (this codebase's percent
    // fields store the fraction, e.g. SurgeHealAugment's own bonusHealthRegenPercent = 2f for 200%).
    public float healFlat = 5f;
    public float bonusHealthRegenPercent = 5f;

    // Baseline heal-per-cast at 100% HealPower (the crit/HealPower multiply itself isn't included since it
    // varies per-heal, not a stat to preview) for AugmentDescriptionFormatter's {LiveHealRatio} token — same
    // shape as SurgeHealAugment's own LiveHealPerSecondRatio, just without "PerSecond" since this fires once
    // per cast, not on an ongoing tick.
    public string LiveHealRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healFlat + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";

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
        if (Stats == null || Stats.IsDead || ability.Identity != AbilityIdentity.RogueSpin) return;
        Stats.RechargeShield();
        Stats.Heal(healFlat + Stats.BonusHealthRegen * bonusHealthRegenPercent);
        // Heal VFX (2026-10-07, caught missing during a playtest pass) — safe to call directly here, same
        // as RogueVanishHealAugment/ExeAxeThrowProjectilePD's own identical calls: PlayHealVfx has its own
        // per-target cooldown dedup built in, so it tolerates a direct call from this same discrete,
        // input/edge-triggered call site fine.
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
