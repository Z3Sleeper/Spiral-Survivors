using UnityEditor;
using UnityEngine;

// Adds a "select text, click a registered color" workflow to displayName/description (via the shared
// ColoredTextFieldGUI, see its own doc comment) so you don't have to hand-type <color=#hex> tags or
// remember hex codes.
[CustomEditor(typeof(AugmentData))]
public class AugmentDataEditor : Editor
{
    private RichTextColorPalette _palette;
    private AbilityTagDatabase _tagDatabase;
    private SerializedProperty _displayNameProp;
    private SerializedProperty _descriptionProp;

    private const string DisplayNameControl = "AugmentDataEditor_DisplayName";
    private const string DescriptionControl = "AugmentDataEditor_Description";

    private void OnEnable()
    {
        _displayNameProp = serializedObject.FindProperty("displayName");
        _descriptionProp = serializedObject.FindProperty("description");
        _palette = ColoredTextFieldGUI.FindPalette();
        _tagDatabase = TagPickerGUI.FindTagDatabase();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

        ColoredTextFieldGUI.Draw(serializedObject, _palette, "Display Name", _displayNameProp, DisplayNameControl, 40f);
        EditorGUILayout.Space();
        ColoredTextFieldGUI.Draw(serializedObject, _palette, "Description", _descriptionProp, DescriptionControl, 200f);
        EditorGUILayout.Space();

        // requiredCount/requiredAugments/tags/requiredTags excluded here and drawn manually right after, so
        // the clamped count field lands directly above the list it's clamped against, and the tag pickers
        // get their toggle-button rows, instead of wherever the default draw order would otherwise put them.
        DrawPropertiesExcluding(serializedObject, "m_Script", "displayName", "description",
            "requiredCount", "requiredAugments", "tags", "requiredTags", "incompatibleTags");

        var requiredAugmentsProp = serializedObject.FindProperty("requiredAugments");
        DrawRequiredCount(serializedObject.FindProperty("requiredCount"), requiredAugmentsProp);
        EditorGUILayout.PropertyField(requiredAugmentsProp, true);

        EditorGUILayout.Space();
        TagPickerGUI.Draw(serializedObject, "Tags", serializedObject.FindProperty("tags"), _tagDatabase);
        EditorGUILayout.Space();
        TagPickerGUI.Draw(serializedObject, "Required Tags", serializedObject.FindProperty("requiredTags"), _tagDatabase);
        EditorGUILayout.Space();
        TagPickerGUI.Draw(serializedObject, "Incompatible Tags", serializedObject.FindProperty("incompatibleTags"), _tagDatabase);

        serializedObject.ApplyModifiedProperties();
    }

    // requiredCount defaults to int.MaxValue (see AugmentData) so an unset count naturally means "all
    // required" without a separate sentinel check anywhere else. Shown here as the resolved value
    // (clamped to the current requiredAugments length) rather than the raw int.MaxValue, so it always
    // reads as a sensible number — but only written back to the underlying property if the user actually
    // edits it, so the "auto-tracks all" behavior survives untouched as requiredAugments grows or shrinks.
    private void DrawRequiredCount(SerializedProperty countProp, SerializedProperty augmentsProp)
    {
        int max = Mathf.Max(0, augmentsProp.arraySize);
        int displayValue = Mathf.Clamp(countProp.intValue, 0, max);

        EditorGUI.BeginChangeCheck();
        int newValue = EditorGUILayout.IntField("Required Count", displayValue);
        if (EditorGUI.EndChangeCheck())
            countProp.intValue = Mathf.Clamp(newValue, 0, max);
    }
}
