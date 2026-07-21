using PurrNet.Prediction;
using UnityEngine;

// Gains energy on every real weapon Strike — deliberately the separate OnStrike chain (AddStrikeListener),
// not OnHit. Strike fires exactly once per genuine weapon connection with an enemy and is never touched by
// anything that re-invokes OnHit (e.g. CritTriggerOnHitAugment calling Stats.NotifyHit again from its own
// crit listener) — Strike and OnHit are two separate conventions that both happen to use onHitPower for
// scaling triggered effects, not the same event under two names. Scaled by data.onHitPower (not a flat
// MaxEnergy% grant) so this still scales down with WeaponCount the same way OnHit-triggered effects do —
// see OnHitDamageAugment's own doc comment for the WeaponCount-divide reasoning. No flat stat grant. Doesn't
// scale with data.isCrit — matches OnStrikeHealAllyAugment's own precedent that a restore triggered off a
// strike event doesn't crit-scale, unlike the damage-dealing OnHit augments.
public class OnStrikeEnergyRegenAugment : AugmentEffectPD<OnStrikeEnergyRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.1f = 10% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    [SerializeField] private float onStrikeEnergyPercent = 0.1f;

    protected override void OnApply()
    {
        Stats.AddStrikeListener(OnStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStrikeListener(OnStrike);
    }

    private void OnStrike(WeaponHitData data)
    {
        // Anything that deals/restores an amount rolls luck variance — no exceptions.
        float baseGain = Stats.MaxEnergy * onStrikeEnergyPercent * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamage(baseGain, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        if (isHighroll) rolled *= 1f + Stats.HighrollBonus;

        Stats.GainEnergy(rolled);
    }
}
