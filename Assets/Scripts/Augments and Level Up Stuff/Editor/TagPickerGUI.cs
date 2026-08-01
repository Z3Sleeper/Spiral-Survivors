using UnityEditor;
using UnityEngine;

// Shared "find registered AbilityTagDatabase, render a toggle-button row for it" drawer — originally lived
// as private methods on AugmentDataEditor, extracted here so any other inspector (e.g. an IAbilityPD
// implementation's own custom editor) can offer the exact same tag-picking UX against a string[] field
// without duplicating the toggle-button logic.
public static class TagPickerGUI
{
    public static AbilityTagDatabase FindTagDatabase()
    {
        var guids = AssetDatabase.FindAssets("t:AbilityTagDatabase");
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadAssetAtPath<AbilityTagDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    // Draws the raw string array (so entries can still be manually reordered/removed) plus a row of
    // toggle buttons — one per AbilityTagDatabase entry — that add/remove that tag from the array on click.
    public static void Draw(SerializedObject serializedObject, string label, SerializedProperty tagsProp, AbilityTagDatabase tagDatabase)
    {
        EditorGUILayout.PropertyField(tagsProp, new GUIContent(label), true);

        if (tagDatabase == null)
        {
            EditorGUILayout.HelpBox(
                "No AbilityTagDatabase asset found. Create one via Assets > Create > Spiral Survivors > " +
                "Ability Tag Database, register your tags on it, and the toggle buttons below will appear.",
                MessageType.Info);
            return;
        }
        if (tagDatabase.tags == null || tagDatabase.tags.Count == 0) return;

        float maxWidth = EditorGUIUtility.currentViewWidth - 40f;
        float rowWidth = 0f;
        EditorGUILayout.BeginHorizontal();
        foreach (var tag in tagDatabase.tags)
        {
            int existingIndex = FindTagIndex(tagsProp, tag);
            bool isActive = existingIndex >= 0;

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
                ToggleTag(serializedObject, tagsProp, tag, existingIndex);
            GUI.backgroundColor = prevColor;

            rowWidth += btnWidth + 2f;
        }
        EditorGUILayout.EndHorizontal();
    }

    private static int FindTagIndex(SerializedProperty tagsProp, string tag)
    {
        for (int i = 0; i < tagsProp.arraySize; i++)
            if (tagsProp.GetArrayElementAtIndex(i).stringValue == tag) return i;
        return -1;
    }

    private static void ToggleTag(SerializedObject serializedObject, SerializedProperty tagsProp, string tag, int existingIndex)
    {
        if (existingIndex >= 0)
        {
            tagsProp.DeleteArrayElementAtIndex(existingIndex);
        }
        else
        {
            tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
            tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tag;
        }
        serializedObject.ApplyModifiedProperties();
    }
}
