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

    [SerializeField] private float healthRegenMultiplier       = 2f; // 200%

    protected override void OnApply()
    {
        Stats.AddAllyStrikeListener(OnAllyStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAllyStrikeListener(OnAllyStrike);
    }

    // baseHeal is passed RAW (un-rolled) to both — Heal()/HealFromAlly() each roll their own luck variance
    // now (see their own doc comments), so this is two independent rolls off the same base amount rather
    // than one shared roll reused for both. Each side also scales differently from there: the caster's own
    // portion goes through the normal Heal() (scales with the CASTER's own HealPower/CritHealPower, same as
    // any other self-heal, and rolls against the CASTER's own Luck), the ally's portion goes through
    // HealFromAlly() instead (rolls against the CASTER's Luck too — "the healer rolls for the healee" — but
    // is NOT scaled by the ally's own HealPower — see that method's own doc comment for why: letting a
    // receiving ally's stacked HealPower also amplify every ally-sourced heal they receive, on top of their
    // own self-heals, risks compounding well past intended value once multiple players are all stacking
    // HealPower and running heal-ally effects at once).
    private void OnAllyStrike(AllyStrikeData data)
    {
        float baseHeal = Stats.HealthRegen * healthRegenMultiplier * data.onHitPower;
        data.ally.HealFromAlly(baseHeal, Stats);
        Stats.Heal(baseHeal);
    }
}
