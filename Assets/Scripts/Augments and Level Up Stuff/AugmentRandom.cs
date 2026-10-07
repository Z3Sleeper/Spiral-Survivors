using PurrNet.Prediction;

// Dedicated deterministic RNG stream for AugmentManagerPD's own augment/stat option rolls (2026-09-22) —
// same shape/precedent as GameRandom (Other Systems/GameRandom.cs), just a SEPARATE stream instance so
// nothing else in the game can ever shift AugmentManagerPD's own draw position. GameRandom is one shared,
// global, order-sensitive stream drawn from by many different systems every tick (enemy spawning, event
// selection, augment rolls, etc.) — if ANY of them draws a different number of values between a client's
// speculative prediction and the eventual server-confirmed replay of that same tick (an ordinary,
// EXPECTED occurrence in any prediction system, not a bug in GameRandom itself), every later draw that
// tick lands on a different value, including an augment roll that has nothing wrong with its own logic.
// See EventManagerPD.State.eventSequence's own doc comment for the first confirmed instance of this bug
// class (event selection); this closes the same risk for "which augments get offered"/"which one an
// auto-pick lands on" permanently, regardless of what else ever gets added to the game later, without
// needing to predetermine the whole sequence upfront the way events did (not viable here — augment offers
// depend on live, per-player, choice-dependent state that can't be known at match start).
public class AugmentRandom : PredictedRandomSystem
{
    public static AugmentRandom instance { get; private set; }

    protected override void LateAwake()
    {
        base.LateAwake();
        instance = this;
    }
}
