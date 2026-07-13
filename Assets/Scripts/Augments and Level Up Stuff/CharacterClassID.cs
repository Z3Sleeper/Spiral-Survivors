// Shared class identifier used by data assets (StatAugmentEntry) that both the old CharacterStats
// system and the new CharacterStatsPD system need to read — neither system's own CharacterClass enum
// can be the shared type without dragging that whole system in as a compile dependency of the other.
public enum CharacterClassID { Knight, Rogue, Archer, Bladeweaver, Executioner }
