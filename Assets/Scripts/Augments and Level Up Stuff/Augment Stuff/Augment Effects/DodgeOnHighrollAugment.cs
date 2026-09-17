using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. Every incoming hit that would otherwise deal real damage rolls HighrollChance before
// any shield/health is touched — a successful roll negates the hit entirely and counts as a genuine Dodge,
// firing the same _onDodge event the dash-invincibility dodge path uses (so the status popup and every
// OnDodge* augment benefit from this too), independent of whether the player is even dashing at all. The
// actual mechanic lives entirely in CharacterStatsPD.TakeDamage() itself (see that method's own doc comment)
// — this augment is just a one-way switch (CharacterStatsPD.EnableDodgeOnHighroll), same "flip a permanent
// bool, the effect lives on CharacterStatsPD" shape PhantomBladeOnHitAugment already uses for its own
// EnablePhantomBladeOnHit toggle.
//
// Also grants its own +3% Highroll Chance (Stats.AddBonusHighrollPercent — see that method's own doc comment
// for why this is the Bonus tier, not AddHighrollPenalty's Penalty tier, for an ordinary buff like this one)
// on top of the base trigger — this augment felt underpowered relying purely on whatever HighrollChance
// investment the player happened to already have, so it brings a small chunk of its own.
public class DodgeOnHighrollAugment : AugmentEffectPD<DodgeOnHighrollAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float highrollChanceBonus = 0.03f;

    protected override void OnApply()
    {
        Stats.EnableDodgeOnHighroll();
        Stats.AddBonusHighrollPercent(highrollChanceBonus);
    }
}
