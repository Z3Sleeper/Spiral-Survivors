using PurrNet.Prediction;
using UnityEngine;

// While Shielded, take 30% less damage (Stats.AddDamageReductionPercent — see that method's own doc comment
// for the inverted-sign reasoning). Removed the tick shield breaks, reapplied the tick it recharges
// (whether naturally or via this same augment's own death-prevention refresh below), via CharacterStatsPD's
// _onShieldBreak/_onShieldRecharge events.
//
// Death save (reworked): this augment no longer owns any death-save logic itself — it just calls
// Stats.ActivateDeathPreventions() once, which activates CharacterStatsPD's own built-in Death Prevention
// mechanic (grant timer, banked save, invincibility-on-spend — all handled there now, see its own
// Simulate()/TakeDamage() comments). If some OTHER augment already activated it first, that same call
// instead shrinks the grant timer by CharacterStatsPD's own centralized reduction knob — see
// ActivateDeathPreventions's own doc comment. This augment's only remaining job on a save landing is
// refreshing shield, via the _onDeathPrevented event (fires AFTER TakeDamage has already set health to 1
// and granted invincibility — this augment never touches either of those anymore).
public class DeathPreventionShieldDRAugment : AugmentEffectPD<DeathPreventionShieldDRAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float damageReductionPercent = 0.5f;

    // Plain, not State — mutated only from PredictedEvent listener callbacks (OnShieldBreak/OnShieldRecharge
    // themselves guarantee exactly-once-per-real-occurrence, see CharacterStatsPD._onShieldBreak's own doc
    // comment), same toggle-tracking shape as ShieldBreakFrenzyAugment's own _grantedView.
    private bool _reductionActive;

    protected override void OnApply()
    {
        Stats.AddShieldBreakListener(OnShieldBreak);
        Stats.AddShieldRechargeListener(OnShieldRecharge);
        Stats.AddDeathPreventedListener(OnDeathPrevented);
        Stats.ActivateDeathPreventions();

        // Granted immediately if already shielded at pick time — otherwise this player would go unprotected
        // until their NEXT shield break+recharge cycle, rather than the very first one they already have.
        if (Stats.CurrentShield > 0f)
        {
            _reductionActive = true;
            Stats.AddDamageReductionPercent(damageReductionPercent);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveShieldBreakListener(OnShieldBreak);
        Stats.RemoveShieldRechargeListener(OnShieldRecharge);
        Stats.RemoveDeathPreventedListener(OnDeathPrevented);
    }

    private void OnShieldBreak()
    {
        if (!_reductionActive) return;
        _reductionActive = false;
        Stats.RemoveDamageReductionPercent(damageReductionPercent);
    }

    private void OnShieldRecharge()
    {
        if (_reductionActive) return;
        _reductionActive = true;
        Stats.AddDamageReductionPercent(damageReductionPercent);
    }

    private void OnDeathPrevented() => Stats.RechargeShield();
}
