using PurrNet.Prediction;
using UnityEngine;

// Heals on every ability start — CharacterStatsPD._onAbilityStart fires the instant any ability's own
// AbilityIsActive flips true (see that event's own doc comment). Heal amount scales with which ability was
// cast: healPercentMaxHealthPerCooldownSecond * MaxHealth * that ability's own BaseCooldownDuration (the
// raw, un-hasted cooldown — see IAbilityPD.BaseCooldownDuration's own doc comment for why this and not the
// haste-scaled CooldownDuration, same convention PhantomBladeOnAbilityStartAugment already established for
// this exact event). A bigger cooldown investment heals for more. Heal() itself rolls luck variance and
// HealPower/CritHealPower now — this just passes the raw, un-rolled amount straight through.
public class OnAbilityStartHealPercentHPAugment : AugmentEffectPD<OnAbilityStartHealPercentHPAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.01f = 1% Max Health per second of BaseCooldownDuration.
    [SerializeField] private float healPercentMaxHealthPerCooldownSecond = 0.01f;

    protected override void OnApply() => Stats.AddAbilityStartListener(OnAbilityStart);

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (Stats == null || Stats.IsDead) return;

        float baseCooldown = ability.BaseCooldownDuration;
        if (baseCooldown <= 0f) return;

        Stats.Heal(Stats.MaxHealth * healPercentMaxHealthPerCooldownSecond * baseCooldown);
    }
}
