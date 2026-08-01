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
// deterministically, on every machine — same reasoning that already makes _onStrikeHighroll/_onHeal safe
// elsewhere in this codebase).
//
// Crit-progress divide (2026-07-26): a naive "+1 per crit, need 3" blows up under high WeaponCount/
// weaponTracks/Multishot — more simultaneous projectiles/weapons means more crit EVENTS for the same
// number of actual attacks, so blades would summon far faster than intended for anyone stacking those
// stats (Archer's multishot in particular). Each crit now increments by the SAME divisor Strength/
// OnHitPower already auto-divide by (weaponTracks * WeaponCount * (1f + Multishot)), so total blade
// output tracks actual attacks made, not raw crit event count. _critCount is a float for this reason —
// overflow past critsNeeded is preserved via subtraction (not reset to 0), so fractional progress from a
// high-division-count hit isn't discarded on the tick that finally crosses the threshold.
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

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    [SerializeField] private int   critsNeeded = 3;
    [SerializeField] private int   bladeCount = 1;
    [SerializeField] private float baseDamageMultiplier = 0.75f;

    private int _critCount;

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction — this used to be half the damage bonus, bumped to match it exactly). Only
        // PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.AddCritStrikeListener(OnCritStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveCritStrikeListener(OnCritStrike);
    }

    private void OnCritStrike(WeaponHitData data)
    {
        _critCount += 1;
        if (_critCount < critsNeeded) return;
        _critCount -= critsNeeded;

        if (phantomBladePrefab == null) return;

        // usePenalizedDamage: true, usePenalizedOnHitPower: true — this augment's trigger frequency itself
        // scales with WeaponCount (more weapons means more hits means more crits), unlike Passive/OnDash
        // which trigger at a fixed rate. Using the WeaponCount-divided Strength (blade damage) and
        // WeaponCount-divided OnHitPower (this blade's own onhit-trigger power, if PhantomBladeOnHitAugment
        // is active) instead of the family's usual Unpenalized variants offsets that so total output stays
        // flat as weapon count grows instead of ballooning — see PhantomBladeSummonHelper.SummonAtTarget's
        // own comment.
        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, baseDamageMultiplier, usePenalizedDamage: true, usePenalizedOnHitPower: true);
    }
}
