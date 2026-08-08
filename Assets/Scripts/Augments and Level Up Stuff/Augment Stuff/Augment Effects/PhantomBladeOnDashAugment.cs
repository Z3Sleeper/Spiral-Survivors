using PurrNet.Prediction;
using UnityEngine;

// Not a port — the user's current intended design supersedes whatever the old system's
// PhantomBladeOnDashAugment implied. Literally the same shape as PhantomBladePassiveAugment except the
// trigger (dash end, not a periodic timer) and the pair of bonus stats it grants (Phantom Blade damage +
// Dash Haste, not Phantom Blade damage + Attack Speed). Subscribes directly to this player's own
// PlayerControllerPD._onDashEnded (per-owner-instance, same cross-player-safety reasoning as
// PhantomBladeOnCritAugment's crit-event chain — PlayerControllerPD lives on the same GameObject as this
// player's own CharacterStatsPD, so there's no shared/static channel for another player's dash to leak
// through).
public class PhantomBladeOnDashAugment : AugmentEffectPD<PhantomBladeOnDashAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    public int bladeCount = 3;
    public float damageMultiplier = 1f;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;

    // damageMultiplier itself doesn't end in "Percent", so AugmentDescriptionFormatter's *100 convention
    // wouldn't apply to it directly — this alias exists purely so a description can reference
    // {DamageMultiplierPercent} and get the correctly-scaled static number, as a fallback for whenever
    // {LiveBladeDamageRatio} itself is blank (out of a live match — see ShowLiveValues's own doc comment).
    public float DamageMultiplierPercent => damageMultiplier;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token — see
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, damageMultiplier))} "
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
        if (_controller != null) _controller.AddDashEndedListener(OnDashEnded);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashEndedListener(OnDashEnded);
    }

    private void OnDashEnded()
    {
        if (Stats == null || Stats.IsDead) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, damageMultiplier);
    }
}
