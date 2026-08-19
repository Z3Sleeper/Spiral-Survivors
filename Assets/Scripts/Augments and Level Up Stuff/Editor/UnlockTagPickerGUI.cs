using UnityEditor;
using UnityEngine;

// Single-select counterpart to TagPickerGUI (see its own doc comment) — for AugmentData.unlockTag, a single
// string field rather than a string[] array, drawn against AugmentUnlockTagDatabase instead of
// AbilityTagDatabase. Clicking an already-active tag's button clears the field back to "no unlock required"
// rather than leaving it stuck active with no way to unset from the button row.
public static class UnlockTagPickerGUI
{
    public static AugmentUnlockTagDatabase FindTagDatabase()
    {
        var guids = AssetDatabase.FindAssets("t:AugmentUnlockTagDatabase");
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadAssetAtPath<AugmentUnlockTagDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    public static void Draw(SerializedObject serializedObject, string label, SerializedProperty tagProp, AugmentUnlockTagDatabase tagDatabase)
    {
        EditorGUILayout.PropertyField(tagProp, new GUIContent(label));

        if (tagDatabase == null)
        {
            EditorGUILayout.HelpBox(
                "No AugmentUnlockTagDatabase asset found. Create one via Assets > Create > Spiral Survivors > " +
                "Augment Unlock Tag Database, register your tags on it, and the toggle buttons below will appear.",
                MessageType.Info);
            return;
        }
        if (tagDatabase.tags == null || tagDatabase.tags.Count == 0) return;

        float maxWidth = EditorGUIUtility.currentViewWidth - 40f;
        float rowWidth = 0f;
        EditorGUILayout.BeginHorizontal();
        foreach (var tag in tagDatabase.tags)
        {
            bool isActive = tagProp.stringValue == tag;

            float btnWidth = GUI.skin.button.CalcSize(new GUIContent(tag)).x + 10f;
            if (rowWidth > 0f && rowWidth + btnWidth > maxWidth)
            {
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                rowWidth = 0f;
            }

            var prevColor = GUI.backgroundColor;
            if (isActive) GUI.backgroundColor = Color.green;
            if (GUILayout.Button(tag, GUILayout.Width(btnWidth), GUILayout.Height(20)))
            {
                tagProp.stringValue = isActive ? "" : tag;
                serializedObject.ApplyModifiedProperties();
            }
            GUI.backgroundColor = prevColor;

            rowWidth += btnWidth + 2f;
        }
        EditorGUILayout.EndHorizontal();
    }
}
