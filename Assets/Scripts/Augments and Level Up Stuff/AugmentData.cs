using UnityEngine;

[CreateAssetMenu(fileName = "NewAugment", menuName = "Spiral Survivors/Augment")]
public class AugmentData : ScriptableObject
{
    public enum ClassRestrictionMode { RestrictedTo, Excluded }

    [TextArea] public string displayName;
    [TextArea(4, 8)] public string description;
    public Sprite icon;
    public GameObject effectPrefab;
    public bool multiplayerOnly = false;

    [Header("Class Restrictions")]
    // classRestrictionMode decides how characterClasses is interpreted: RestrictedTo means ONLY those
    // classes can see this augment (empty = available to everyone); Excluded means every class EXCEPT
    // those listed can see it (empty = no exclusions, also available to everyone).
    public ClassRestrictionMode classRestrictionMode = ClassRestrictionMode.RestrictedTo;
    public CharacterClassID[] characterClasses;

    [Header("Augment Restrictions")]
    public AugmentData[] incompatibleAugments;

    // How many of requiredAugments must already be picked for this to become available — e.g. 2 means
    // any 2 of the listed augments satisfy the requirement, not necessarily all of them. Defaults to
    // int.MaxValue so an augment with requiredAugments entries but an unset count still requires ALL of
    // them (clamped down to requiredAugments.Length wherever this is evaluated), rather than silently
    // requiring none.
    public int requiredCount = int.MaxValue;
    public AugmentData[] requiredAugments;

    [Header("Tags")]
    // Tags this augment itself carries (e.g. "OnHit", "PhantomBlade") — matched against by exact string
    // equality against AbilityTagDatabase entries. Other augments require any already-picked augment to
    // carry a given tag via requiredTags below, instead of needing to individually list every augment that
    // might ever carry it (which doesn't scale — a new OnHit-effect augment added later would silently
    // fail to satisfy an existing requiredAugments-based requirement unless someone remembered to add it).
    public string[] tags;
    // Every tag listed here must have at least one already-picked augment carrying it (AND across all
    // listed tags) — e.g. requiring ["OnHit", "PhantomBlade"] is satisfied once the player has picked any
    // one augment tagged "OnHit" AND any one augment tagged "PhantomBlade", regardless of which specific
    // augments those are or how many more get added to either tag later.
    public string[] requiredTags;
    // Requires the player to currently have ALL of these specific abilities equipped (AND across all listed
    // values, same convention as requiredTags above) — for an augment built around one exact ability (e.g.
    // "RogueSpin hits twice") rather than a shared capability many augments/abilities might carry. Checked
    // live against GetComponents<IAbilityPD>() at offer time (AugmentManagerPD.HasRequiredAbilities), same
    // "whatever's actually equipped right now" safety requiredTags' own ability-matching already has — stays
    // correct even if ability loadouts ever become swappable. Deliberately a SEPARATE field from
    // tags/requiredTags above, not a tag — ability identity is a closed, one-to-one set (exactly as many
    // values as abilities that exist), not a shared many-to-few capability pool, so mixing the two would
    // clutter that pool with entries nothing else would ever reuse. Empty/null = no restriction, same as
    // every other restriction field on this class.
    public AbilityIdentity[] requiredAbilities;
    // Tag-based counterpart to incompatibleAugments above — excludes (and is excluded by) any augment
    // carrying one of these tags via its own "tags" list above, without needing to name every such augment
    // individually (same non-enumeration rationale as requiredTags). Checked symmetrically, same as
    // incompatibleAugments: it doesn't matter which side (this augment's incompatibleTags, or an already-
    // picked augment's incompatibleTags) declares the link.
    public string[] incompatibleTags;

    [Header("Unlock")]
    // Gates whether this augment can ever appear in a level-up pool at all — empty/null means always
    // available (the default, and what every existing augment keeps). SINGULAR, unlike tags/requiredTags/
    // incompatibleTags above, and drawn from a completely separate AugmentUnlockTagDatabase — see that
    // class's own doc comment for why it's a different pool from AbilityTagDatabase, and
    // CharacterStatsPD.UnlockedAugmentTagMask for how a player's own unlocked set is checked against this
    // at pick time (AugmentManagerPD.IsAugmentTagUnlocked). Not yet wired to any actual unlock TRIGGER as of
    // this scaffolding pass — nothing grants a tag yet, so any augment given one here is effectively unpickable
    // by anyone until a future system starts calling ProgressionManager.UnlockAugmentTag.
    public string unlockTag;

    public bool IsAvailableForClass(CharacterClassID characterClass)
    {
        bool inList = characterClasses != null && System.Array.IndexOf(characterClasses, characterClass) >= 0;
        return classRestrictionMode == ClassRestrictionMode.RestrictedTo
            ? (characterClasses == null || characterClasses.Length == 0 || inList)
            : !inList;
    }
}
