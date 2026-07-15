// Display-layer wrapper only — lets LocalUIManagerPD/AugmentCardUI handle a mixed list of real augments
// and stat-pair fallbacks without caring which is which until it's time to pick the right card setup.
public class LevelUpOption
{
    public bool isAugment;
    public AugmentData augmentData;
    public StatAugmentPair statPair;

    public static LevelUpOption FromAugment(AugmentData data) => new() { isAugment = true, augmentData = data };
    public static LevelUpOption FromStatPair(StatAugmentPair pair) => new() { isAugment = false, statPair = pair };
}
