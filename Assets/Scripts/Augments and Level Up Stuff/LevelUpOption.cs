public class LevelUpOption
{
    public enum OptionType { Augment, StatAugment }

    public OptionType type;
    public AugmentData augmentData;
    public StatAugmentPair statPair;

    public static LevelUpOption FromAugment(AugmentData data) =>
        new() { type = OptionType.Augment, augmentData = data };

    public static LevelUpOption FromStatAugment(StatAugmentPair pair) =>
        new() { type = OptionType.StatAugment, statPair = pair };
}
