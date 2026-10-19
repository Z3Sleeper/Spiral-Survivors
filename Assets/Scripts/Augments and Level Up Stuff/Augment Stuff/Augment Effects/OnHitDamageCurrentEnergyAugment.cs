using PurrNet.Prediction;
using UnityEngine;

// Same family/shape as OnHitDamageAugment/OnHitDamageHealthAugment (same _onHit trigger, same
// Strength*onHitPower-scaled damage, same crit/highroll inheritance via WeaponDamageMath.RollDamageInherited
// — see OnHitDamageAugment's own doc comment for the shared reasoning this doesn't repeat), but scales off
// Stats.CurrentEnergy — the player's LIVE, fluctuating energy pool at the moment of the hit — rather than a
// permanent Bonus-tier investment stat like every sibling in this family (BonusHealth/BonusHealthRegen) uses.
// Deliberate design (confirmed explicitly, 2026-09-16): this rewards HOLDING energy in reserve rather than
// investing in Max Energy — a player who just Surged (spending most of their energy) gets little/nothing from
// this augment until it regens back up, creating real tension against Surge/energy-cost-ability spam. Named
// "CurrentEnergy" specifically (not "MaxEnergy" or "Energy" alone) to make that live-value distinction
// unambiguous at a glance.
public class OnHitDamageCurrentEnergyAugment : AugmentEffectPD<OnHitDamageCurrentEnergyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float onHitStrengthPercent = 0.05f;
    // 0.001f = 0.1% Strength per 1 point of Stats.CurrentEnergy.
    public float perEnergyPercent = 0.001f;
    // Generic +10 Max Energy side-bonus (2026-10-07), same flat grant most other energy/Surge-themed
    // augments in this codebase carry — doesn't touch this augment's own CurrentEnergy-scaling design at
    // all, just a permanent stat floor on top.
    public float bonusMaxEnergyFlat = 10f;

    // Baseline damage at 100% OnHitPower (the per-hit multiplier itself isn't included since it varies
    // hit-to-hit) for AugmentDescriptionFormatter's {LiveStrengthRatio} token — reads stats.CurrentEnergy
    // live, same as every other Live*Ratio in this codebase that previews off a live (not baked-in) value.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * (onHitStrengthPercent + perEnergyPercent * stats.CurrentEnergy))} "
            : "";

    protected override void OnApply()
    {
        Stats.AddHitListener(OnHit);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveHitListener(OnHit);
    }

    private void OnHit(WeaponHitData data)
    {
        if (!data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        // data.onHitPower already divides by WeaponCount — Stats.Strength stays undivided so onHitPower is
        // the only WeaponCount divisor, same convention OnHitDamageAugment's own preRoll comment explains.
        // Anything that deals damage rolls luck variance — no exceptions. Inherits crit/highroll from the
        // triggering hit (data.isCrit/data.isHighroll) rather than rolling independently — see
        // WeaponDamageMath.RollDamageInherited's own doc comment.
        float preRoll = Stats.Strength * (onHitStrengthPercent + perEnergyPercent * Stats.CurrentEnergy) * data.onHitPower;
        float rolled = WeaponDamageMath.RollDamageInherited(preRoll, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, data.isHighroll, Stats.NextFloat);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, data.isHighroll, data.isCrit, Stats.HighrollBonus, Stats.CritDamage);

        enemy.TakeDamage(rolled, data.isCrit, Stats.owner ?? default, data.isHighroll, Stats.executeThreshold, Stats.critExecuteThreshold);
        Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = data.isCrit, isHighroll = data.isHighroll, attacker = Stats, isPriority = false });
    }
}
