using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — enables allyStrikeHitEnabled on CharacterStatsPD, which each weapon type
// (WeaponMeleePD/WeaponAxePD/WeaponRangedProjectilePD/WeaponRangedPD) checks to decide whether it's worth
// running an extra overlap query against the Player hurtbox layer alongside its normal enemy detection. A
// "hit" here is a plain touch, not damage — allies are never passed through TakeDamage this way (a
// separate allyStrikeDamageEnabled gate handles real friendly fire, unused by this augment).
// allyStrikeHitEnabled and the _onAllyStrike event it gates are both generic (own no heal-specific state)
// so future "hit ally, do something" augments can reuse the exact same detection plumbing by just calling
// EnableAllyStrikeHit() and subscribing their own listener — this augment is simply the first consumer of it.
public class HealAllyOnStrikeAugment : AugmentEffectPD<HealAllyOnStrikeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float healthRegenBonusFlat = 0.5f;
    [SerializeField] private float allyHealPercent       = 2f; // 200%

    protected override void OnApply()
    {
        Stats.AddHealthRegenFlat(healthRegenBonusFlat);
        Stats.EnableAllyStrikeHit();
        Stats.AddAllyStrikeListener(OnAllyStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAllyStrikeListener(OnAllyStrike);
    }

    // Single roll shared by both heals (not rolled twice) — the caster heals for the exact same amount the
    // ally received, per the augment's own spec.
    private void OnAllyStrike(AllyStrikeData data)
    {
        float baseHeal = Stats.HealthRegen * allyHealPercent * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamage(baseHeal, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        if (isHighroll) rolled *= 1f + Stats.HighrollBonus;
        data.ally.Heal(rolled);
        Stats.Heal(rolled);
    }
}
