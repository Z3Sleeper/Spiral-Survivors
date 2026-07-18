using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/PhantomBladeOnCritAugment.cs. Old system subscribed to
// global static events (WeaponBase.OnCritStrike, PhantomBlade.OnSummon) filtered by `if (stats != _stats)
// return;` — a real cross-player-interaction risk baked into the architecture itself. Replaced with a
// per-player event chain: each weapon fires its own crit event -> WeaponManagerPD relays every one of this
// player's own weapons up to this player's own CharacterStatsPD -> this augment subscribes once to its own
// Stats. Every hop is a per-owner-instance relationship with no shared/static channel anywhere, so
// cross-player interaction isn't just filtered against — there's no channel for it to happen through.
//
// No custom predicted-state counting needed — the crit-progress counter is a plain field, mutated only
// from inside the PredictedEvent callback (itself guaranteed to fire exactly once per logical crit,
// deterministically, on every machine — same reasoning that already makes _onHighroll/_onHeal safe
// elsewhere in this codebase).
//
// Targeting deliberately does NOT use the crit event's own target payload (which enemy actually got hit)
// — per the user's call, this targets the closest enemies instead, same convention as
// PhantomBladePassiveAugment/PhantomBladeOnDashAugment, with bladeCount exposed the same way. The crit
// event still carries a PredictedObjectID (WeaponBasePD/CharacterStatsPD's _onCritStrike) since other
// Phantom Blade augments further down the family may still want the actual hit target — this one just
// ignores it.
public class PhantomBladeOnCritAugment : AugmentEffectPD<PhantomBladeOnCritAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // Large enough to reliably reach enemies out at EnemySpawnerPD's own max spawn distance (40) with room
    // to spare — not exposed as its own tunable, matching PhantomBladePassiveAugment/PhantomBladeOnDashAugment.
    private const float TargetSearchRadius = 50f;

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float bonusCritChance = 0.1f;
    [SerializeField] private float bladeDamageBonus = 0.1f;
    [SerializeField] private int   critsPerBlade = 3;
    [SerializeField] private int   bladeCount = 1;
    [SerializeField] private float baseDamageMultiplier = 0.75f;

    private int _critCount;

    protected override void OnApply()
    {
        Stats.AddCritChanceFlat(bonusCritChance);
        // Matches old's exact size += 0.5f * bladeDamageBonus ratio (OnSummon handler in both
        // PhantomBladePassiveAugment and PhantomBladeOnCritAugment).
        Stats.AddPhantomBladeDamageBonus(bladeDamageBonus);
        Stats.AddPhantomBladeSizeBonus(0.5f * bladeDamageBonus);
        Stats.AddCritStrikeListener(OnCritStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveCritStrikeListener(OnCritStrike);
    }

    private void OnCritStrike(WeaponHitData data)
    {
        if (++_critCount < critsPerBlade) return;
        _critCount = 0;

        if (phantomBladePrefab == null) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, TargetSearchRadius, bladeCount, baseDamageMultiplier);
    }
}
