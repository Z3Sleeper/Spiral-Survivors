public class ShielderGloom : EnemyBase
{
    protected override void OnPoolReset()
    {
        base.OnPoolReset();
        var shield = GetComponentInChildren<GloomShield>();
        if (shield != null) shield.ResetState();
    }
}
