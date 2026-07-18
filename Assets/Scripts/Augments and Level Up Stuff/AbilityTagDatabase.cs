using System.Collections.Generic;
using UnityEngine;

// Canonical registry of ability tag names — same "single source of truth" pattern as StatAugmentDatabase.
// AugmentData.tags/requiredTags store plain strings (not object references, unlike requiredAugments),
// matched against these by exact string equality — this asset exists so the Inspector can offer a
// toggle-button picker (see AugmentDataEditor) instead of hand-typing tag strings and risking a typo that
// silently breaks a requirement check.
[CreateAssetMenu(fileName = "AbilityTagDatabase", menuName = "Spiral Survivors/Ability Tag Database")]
public class AbilityTagDatabase : ScriptableObject
{
    public List<string> tags = new();
}
