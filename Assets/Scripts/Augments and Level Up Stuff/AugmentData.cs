using UnityEngine;

[CreateAssetMenu(fileName = "NewAugment", menuName = "Spiral Survivors/Augment")]
public class AugmentData : ScriptableObject
{
    [TextArea] public string displayName;
    [TextArea] public string description;
    public Sprite icon;
    public CharacterStats.CharacterClass[] restrictedToClasses;
    public CharacterStats.CharacterClass[] excludedClasses;
    public GameObject effectPrefab;
    public bool multiplayerOnly = false;
    public AugmentData[] incompatibleAugments;
    public AugmentData[] requiredAugments;
    public AugmentData[] requiredOneOfAugments;

    public bool IsAvailableForClass(CharacterStats.CharacterClass characterClass)
    {
        if (excludedClasses != null && System.Array.IndexOf(excludedClasses, characterClass) >= 0)
            return false;
        return restrictedToClasses == null || restrictedToClasses.Length == 0
            || System.Array.IndexOf(restrictedToClasses, characterClass) >= 0;
    }
}
