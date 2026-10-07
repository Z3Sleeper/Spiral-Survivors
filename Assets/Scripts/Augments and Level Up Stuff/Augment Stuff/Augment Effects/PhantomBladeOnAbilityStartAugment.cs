using PurrNet.Prediction;
using UnityEngine;

// Not a port — new family member reacting to CharacterStatsPD._onAbilityStart (fires the instant any
// ability's own AbilityIsActive flips true, see IAbilityPD.AbilityIsActive's own doc comment) instead of a
// periodic timer/dash/crit. Damage scales with which ability was cast: 100% Unpenalized Strength (rolled
// through luck variance, same as every other family member — see PhantomBladeSummonHelper.SummonAtTarget)
// times that ability's own BaseCooldownDuration (the raw, un-hasted cooldown — see IAbilityPD.
// BaseCooldownDuration's own doc comment for why this and not the haste-scaled CooldownDuration). A
// 6-second-base-cooldown ability yields 600% damage, a 2-second one yields 200%, etc. — a bigger cooldown
// investment hits harder. The payload IS the ability that started (see CharacterStatsPD._onAbilityStart's
// own doc comment for why that's safe to pass directly), so this reads BaseCooldownDuration straight off
// it — no GetComponents<IAbilityPD>()/Slot caching needed at all.
public class PhantomBladeOnAbilityStartAugment : AugmentEffectPD<PhantomBladeOnAbilityStartAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    public int bladeCount = 3;
    public float strengthMultiplierPercent = 0.12f;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;

    // No single number to preview (the actual blade damage depends on which of this character's two
    // abilities was cast) — shows both class abilities' own actual values instead, "Ability1/Ability2", for
    // AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the shared per-blade formula.
    public string LiveBladeDamageRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            float value = PhantomBladeSummonHelper.PreviewDamage(stats, strengthMultiplierPercent * ability.BaseCooldownDuration);
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = value;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = value;
        }
        // "|" not "/" — see OnAbilityStartHealHealthRegenAugment's identical LiveAbilityHealAmount for why.
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(ability1Value)}|{AugmentDescriptionFormatter.FormatLiveNumber(ability2Value)} ";
    }

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.AddAbilityStartListener(OnAbilityStart);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (Stats == null || Stats.IsDead || phantomBladePrefab == null) return;

        float baseCooldown = ability.BaseCooldownDuration;
        if (baseCooldown <= 0f) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, strengthMultiplierPercent * baseCooldown);
    }
}
