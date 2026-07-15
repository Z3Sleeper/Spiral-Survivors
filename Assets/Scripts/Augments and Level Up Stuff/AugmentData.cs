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

    public bool IsAvailableForClass(CharacterClassID characterClass)
    {
        bool inList = characterClasses != null && System.Array.IndexOf(characterClasses, characterClass) >= 0;
        return classRestrictionMode == ClassRestrictionMode.RestrictedTo
            ? (characterClasses == null || characterClasses.Length == 0 || inList)
            : !inList;
    }
}
