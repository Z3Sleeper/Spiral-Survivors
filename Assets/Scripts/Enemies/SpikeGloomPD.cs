// No behavioral difference from the plain EnemyBasePD — Spike Gloom is spawned/killed in far greater
// numbers than other enemy types, and having no attack-routine state machine of its own, it never calls
// FindClosestTarget() beyond EnemyBasePD's own base cases (spawn, target null/dead). This is exactly the
// "biggest win" case the acquire-once-then-keep-until-gone model was built for: at swarm scale, this type
// never re-scans CharacterStatsPD.All at all once it has a living target.
public class SpikeGloomPD : EnemyBasePD
{
}
