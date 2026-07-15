using UnityEngine;

// No behavioral difference from the plain EnemyBasePD — Spike Gloom is spawned/killed in far greater
// numbers than other enemy types, and FindTarget() is an O(players) scan over CharacterStatsPD.All run
// independently by every single enemy instance on its own timer, so at swarm scale it's worth letting this
// type re-target less often than the 1s default.
public class SpikeGloomPD : EnemyBasePD
{
    [SerializeField] private float findTargetInterval = 3f;
    protected override float FindTargetInterval => findTargetInterval;
}
