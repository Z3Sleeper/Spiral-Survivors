using UnityEditor;
using UnityEngine;

// Common inspector for any IAbilityPD MonoBehaviour: the same "select text, click a registered color"
// workflow AugmentDataEditor uses for its displayName/description (via the shared ColoredTextFieldGUI)
// against this ability's own "abilityName"/"description" fields, everything else drawn normally, then the
// same toggle-button tag picker AugmentDataEditor uses (via TagPickerGUI) against this ability's own
// "tags" field. Unity's [CustomEditor] attribute only resolves against a concrete closed type — not an
// interface, and not an open generic — so it can't be attached to IAbilityPD directly; each concrete
// ability still needs its own one-line subclass (see RogueVanishAbilityPDEditor/
// RogueSpinAbilityPDEditor), but all the actual drawing logic lives here once, not duplicated per ability.
public abstract class AbilityPDEditorBase<T> : Editor where T : MonoBehaviour, IAbilityPD
{
    private RichTextColorPalette _palette;
    private AbilityTagDatabase _tagDatabase;
    private SerializedProperty _nameProp;
    private SerializedProperty _descriptionProp;
    private SerializedProperty _tagsProp;

    private const string NameControl = "AbilityPDEditor_Name";
    private const string DescriptionControl = "AbilityPDEditor_Description";

    protected virtual void OnEnable()
    {
        _palette = ColoredTextFieldGUI.FindPalette();
        _tagDatabase = TagPickerGUI.FindTagDatabase();
        _nameProp = serializedObject.FindProperty("abilityName");
        _descriptionProp = serializedObject.FindProperty("description");
        _tagsProp = serializedObject.FindProperty("tags");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

        ColoredTextFieldGUI.Draw(serializedObject, _palette, "Name", _nameProp, NameControl, 40f);
        EditorGUILayout.Space();
        ColoredTextFieldGUI.Draw(serializedObject, _palette, "Description", _descriptionProp, DescriptionControl, 200f);
        EditorGUILayout.Space();

        DrawPropertiesExcluding(serializedObject, "m_Script", "abilityName", "description", "tags");

        EditorGUILayout.Space();
        TagPickerGUI.Draw(serializedObject, "Tags", _tagsProp, _tagDatabase);

        serializedObject.ApplyModifiedProperties();
    }
}
