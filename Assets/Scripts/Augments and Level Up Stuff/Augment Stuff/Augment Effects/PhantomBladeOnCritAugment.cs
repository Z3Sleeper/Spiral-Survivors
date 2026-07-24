using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/PhantomBladeOnCritAugment.cs. Old system subscribed to
// global static events (WeaponBase.OnCritStrike, PhantomBlade.OnSummon) filtered by `if (stats != _stats)
// return;` — a real cross-player-interaction risk baked into the architecture itself. Replaced with every
// weapon that lands a crit calling straight into its own owning player's CharacterStatsPD.NotifyCritStrike
// (see feedback_predictedevent_anchor_persistent_identity memory for why this is anchored on Stats rather
// than the weapon itself), and this augment subscribing once to its own Stats. Every hop is a per-owner-
// instance relationship with no shared/static channel anywhere, so cross-player interaction isn't just
// filtered against — there's no channel for it to happen through.
//
// No custom predicted-state counting needed — the crit-progress counter is a plain field, mutated only
// from inside the PredictedEvent callback (itself guaranteed to fire exactly once per logical crit,
// deterministically, on every machine — same reasoning that already makes _onHighroll/_onHeal safe
// elsewhere in this codebase).
//
// Targeting deliberately does NOT use the crit event's own target payload (which enemy actually got hit)
// — per the user's call, this targets the closest enemies instead, same convention as
// PhantomBladePassiveAugment/PhantomBladeOnDashAugment, with bladeCount exposed the same way. The crit
// event still carries a PredictedObjectID (CharacterStatsPD's own _onCritStrike) since other Phantom Blade
// augments further down the family may still want the actual hit target — this one just ignores it.
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
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    [SerializeField] private int   critsPerBlade = 3;
    [SerializeField] private int   bladeCount = 1;
    [SerializeField] private float baseDamageMultiplier = 0.75f;

    private int _critCount;

    protected override void OnApply()
    {
        // Matches old's exact size += 0.5f * phantomBladeDamageBonus ratio (OnSummon handler in both
        // PhantomBladePassiveAugment and PhantomBladeOnCritAugment).
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        Stats.AddPhantomBladeSizeBonus(0.5f * phantomBladeDamageBonus);
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

        // usePenalizedDamage: true — this augment's trigger frequency itself scales with WeaponCount (more
        // weapons means more hits means more crits), unlike Passive/OnDash which trigger at a fixed rate.
        // Using the WeaponCount-divided Strength here (instead of the family's usual
        // UnpenalizedStrength) offsets that so total blade output stays flat as weapon count grows
        // instead of ballooning — see PhantomBladeSummonHelper.SummonAtTarget's own comment.
        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, TargetSearchRadius, bladeCount, baseDamageMultiplier, usePenalizedDamage: true);
    }
}
