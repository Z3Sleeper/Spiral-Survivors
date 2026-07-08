public class SpikyGloom : EnemyBase
{
    protected override void OnSpawned()
    {
        FindTargetInterval = 3f;
        base.OnSpawned();
    }
}