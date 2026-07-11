using PurrNet.Prediction;

public class GameRandom : PredictedRandomSystem
{
    public static GameRandom instance { get; private set; }

    protected override void LateAwake()
    {
        base.LateAwake();
        instance = this;
    }
}
