using System.Collections.Generic;
using UnityEngine;

// Same "single source of truth for a curated string list" role as AbilityTagDatabase, but a SEPARATE
// database/class deliberately — these tags gate whether an augment appears in the level-up pool at all
// (see AugmentData.unlockTag), a completely different concern from AbilityTagDatabase's compatibility/
// requirement tags (tags/requiredTags/incompatibleTags), so mixing the two pools in one picker would be
// confusing (and would let an unlock-set tag accidentally get used as a requiredTags/incompatibleTags entry
// or vice versa).
//
// List INDEX doubles as this tag's bit position in CharacterStatsPD.UnlockedAugmentTagMask (see
// ProgressionManager.UnlockedAugmentTags/CharacterStatsPD.ComputeUnlockedAugmentTagMask) — entries must only
// ever be APPENDED, never reordered or removed, or every player's already-granted unlock bits silently
// repoint at a different tag. Capacity is 64 (a single long's worth of bits) — comfortably more than this
// system is ever expected to need for "augment sets."
[CreateAssetMenu(fileName = "AugmentUnlockTagDatabase", menuName = "Spiral Survivors/Augment Unlock Tag Database")]
public class AugmentUnlockTagDatabase : ScriptableObject
{
    public List<string> tags = new();

    // -1 if not registered (including null/empty). See class doc comment for why index == bit position.
    public int GetTagBit(string tag) => string.IsNullOrEmpty(tag) ? -1 : tags.IndexOf(tag);

    // Add a new case here (plus a corresponding field on AugmentUnlockCondition below, if the condition needs
    // its own parameter) for each new kind of unlock trigger — see GrantQualifyingUnlocks' own doc comment.
    public enum AugmentUnlockConditionType
    {
        ThreatLevelUnlocked, // requiredThreatLevel: ProgressionManager.HighestUnlockedDifficulty must be >= this.
    }

    // Deliberately NOT parallel-indexed to tags above (which must stay strictly append-only/bit-position-
    // stable forever, per the class doc comment) — keyed by its own tag string instead, so entries here can be
    // freely added/reordered/removed without any risk to an already-registered tag's bit position.
    [System.Serializable]
    public struct AugmentUnlockCondition
    {
        public string tag;
        public AugmentUnlockConditionType conditionType;
        public int requiredThreatLevel; // only meaningful for ThreatLevelUnlocked
        // Shown once, via UnlockManager's existing announcement popup (same pipeline the Threat-Level-unlock
        // announcement already uses), the first time this tag is actually granted — see
        // ProgressionManager.UnlockAugmentTag's own doc comment for why no further dedup bookkeeping is
        // needed beyond that single grant. Leave blank for a silent unlock (no popup).
        public string unlockMessage;
    }

    public List<AugmentUnlockCondition> unlockConditions = new();

    // Called once per player spawn from CharacterStatsPD.ComputeUnlockedAugmentTagMask(), right before that
    // resolves this player's own unlock bitmask — so a tag granted here is reflected in the SAME spawn's
    // eligible-augment pool immediately, whether the condition was just newly satisfied this run or was
    // already satisfied from a past session before this trigger system existed (both cases are handled
    // identically, since this just re-checks fresh every call rather than reacting to a specific "crossed
    // the threshold" event). Idempotent and cheap to call redundantly — ProgressionManager.UnlockAugmentTag()
    // only actually writes to disk the first time a tag crosses from locked to unlocked; every call after
    // that is a no-op HashSet check, matching how ComputeUnlockedAugmentTagMask() itself is already safe to
    // recompute every tick (this call site also runs from PlayerDataPD.GetFinalInput(), not just once).
    public void GrantQualifyingUnlocks()
    {
        foreach (var condition in unlockConditions)
        {
            bool qualifies = condition.conditionType switch
            {
                AugmentUnlockConditionType.ThreatLevelUnlocked =>
                    ProgressionManager.HighestUnlockedDifficulty >= condition.requiredThreatLevel,
                _ => false,
            };
            if (qualifies) ProgressionManager.UnlockAugmentTag(condition.tag, condition.unlockMessage);
        }
    }
}
