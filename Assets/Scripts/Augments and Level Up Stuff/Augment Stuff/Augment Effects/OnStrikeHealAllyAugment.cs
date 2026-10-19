using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — subscribes to CharacterStatsPD's _onAllyStrike, which every weapon type
// (WeaponMeleePD/WeaponAxePD/WeaponRangedProjectilePD) always fires now when it touches an ally (ally-hit
// detection is unconditional, "canContactAllies" permanently on — see CharacterStatsPD.NotifyAllyStrike's
// own doc comment). A "hit" here is a plain touch, not damage — real friendly fire has been removed from
// the project entirely, allies are never passed through TakeDamage this way. _onAllyStrike is generic (owns
// no heal-specific state) so future "hit ally, do something" augments can reuse the exact same notification
// by just subscribing their own listener — this augment is simply the first consumer of it.
public class OnStrikeHealAllyAugment : AugmentEffectPD<OnStrikeHealAllyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 2026-09-14: scales off Stats.BonusHealthRegen (was Stats.HealthRegen, the full/total stat) — see that
    // property's own doc comment. Renamed from healthRegenMultiplier to bonusHealthRegenPercent (now ends in
    // "Percent", so AugmentDescriptionFormatter's *100 convention applies directly) — the old
    // HealthRegenMultiplierPercent alias property this needed is gone, no longer necessary.
    public float bonusHealthRegenPercent = 1f; // 100%
    // Flat floor added to the scaled portion below (2026-08-12, playtesting: this augment did literally
    // nothing for a build with zero HealthRegen investment) — added BEFORE the onHitPower multiply (see
    // OnAllyStrike()), so it still scales with onHitPower same as the rest of the heal, just guarantees a
    // non-zero base even at zero HealthRegen investment.
    public float baseHeal = 2f;

    // Baseline heal at 100% OnHitPower (varies hit-to-hit, so not baked into the preview — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention) for
    // AugmentDescriptionFormatter's {LiveHealthRegenRatio} token. Includes baseHeal so the preview matches
    // what OnAllyStrike actually heals.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseHeal + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";

    protected override void OnApply()
    {
        Stats.AddAllyStrikeListener(OnAllyStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAllyStrikeListener(OnAllyStrike);
    }

    // baseHeal is passed RAW to both (no luck roll on either side anymore — 2026-08-12, see
    // CharacterStatsPD.Heal's own doc comment). The two sides still scale differently: the caster's own
    // portion goes through the normal Heal() (scales with the CASTER's own HealPower/CritHealPower, same as
    // any other self-heal), the ally's portion goes through HealFromAlly() instead (NOT scaled by the ally's
    // own HealPower — see that method's own doc comment for why: letting a receiving ally's stacked
    // HealPower also amplify every ally-sourced heal they receive, on top of their own self-heals, risks
    // compounding well past intended value once multiple players are all stacking HealPower and running
    // heal-ally effects at once).
    private void OnAllyStrike(AllyStrikeData data)
    {
        // baseHeal joins the scaled portion INSIDE the onHitPower multiply — see its own field comment.
        float totalHeal = (baseHeal + Stats.BonusHealthRegen * bonusHealthRegenPercent) * data.onHitPower;
        // Stub (2026-08-30) — HealFromAlly is CharacterStatsPD-specific, not on IFriendlyPD yet (data.ally
        // was widened to IFriendlyPD as part of the combat-targeting migration). Non-player ally healing
        // shape isn't designed yet, same "stub for now" call as DamageNumberManager's own IFriendlyPD
        // overloads — a future non-player ally just doesn't get healed by this augment until that's decided.
        if (data.ally is CharacterStatsPD player)
        {
            player.HealFromAlly(totalHeal, Stats);
            // Heal VFX opt-in (2026-09-26, user-curated per-augment, non-persistent heals only) — safe
            // unconditionally: this callback only ever runs once per real ally strike, off
            // CharacterStatsPD._onAllyStrike, a genuine PredictedEvent<AllyStrikeData> (see
            // VFXManager.PlayHealVfx's own per-target cooldown for the anti-spam side of this).
            VFXManager.instance?.PlayHealVfx(player.transform);
        }
        Stats.Heal(totalHeal);
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
