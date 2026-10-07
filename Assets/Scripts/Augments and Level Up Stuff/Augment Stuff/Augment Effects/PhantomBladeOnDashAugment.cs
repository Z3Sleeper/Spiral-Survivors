using PurrNet.Prediction;
using UnityEngine;

// Not a port — the user's current intended design supersedes whatever the old system's
// PhantomBladeOnDashAugment implied. Literally the same shape as PhantomBladePassiveAugment except the
// trigger (dash start, not a periodic timer) and the pair of bonus stats it grants (Phantom Blade damage +
// Dash Haste, not Phantom Blade damage + Attack Speed). Subscribes directly to this player's own
// PlayerControllerPD._onDashStarted (2026-09-14, was _onDashEnded — switched to trigger the instant a dash
// begins rather than waiting for it to complete naturally; per-owner-instance, same cross-player-safety
// reasoning as PhantomBladeOnCritAugment's crit-event chain — PlayerControllerPD lives on the same GameObject
// as this player's own CharacterStatsPD, so there's no shared/static channel for another player's dash to
// leak through). Also fires on any dash-like ability movement that calls
// PlayerControllerPD.NotifyDashStarted() (RogueSpinAbilityPD's leap, BWBlinkAbilityPD) — same convention
// DashExplosionAugment/DashHealAugment/OnDashStartedGainEnergyAugment already ride.
public class PhantomBladeOnDashAugment : AugmentEffectPD<PhantomBladeOnDashAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    public int bladeCount = 3;
    public float strengthMultiplierPercent = 0.6f;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token — see
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, strengthMultiplierPercent))} "
            : "";

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);

        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashStartedListener(OnDashStarted);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashStartedListener(OnDashStarted);
    }

    private void OnDashStarted()
    {
        if (Stats == null || Stats.IsDead) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, strengthMultiplierPercent);
    }
}
