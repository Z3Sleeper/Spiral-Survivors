using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Adds a "select text, click a registered color" workflow to displayName/description so you don't have
// to hand-type <color=#hex> tags or remember hex codes — see RichTextColorPalette for where the named
// colors themselves get registered.
[CustomEditor(typeof(AugmentData))]
public class AugmentDataEditor : Editor
{
    private RichTextColorPalette _palette;
    private AbilityTagDatabase _tagDatabase;
    private SerializedProperty _displayNameProp;
    private SerializedProperty _descriptionProp;
    private GUIStyle _previewStyle;
    private GUIStyle _textAreaStyle;

    private const string DisplayNameControl = "AugmentDataEditor_DisplayName";
    private const string DescriptionControl = "AugmentDataEditor_Description";

    private void OnEnable()
    {
        _displayNameProp = serializedObject.FindProperty("displayName");
        _descriptionProp = serializedObject.FindProperty("description");
        _palette = FindPalette();
        _tagDatabase = FindTagDatabase();
    }

    private static RichTextColorPalette FindPalette()
    {
        var guids = AssetDatabase.FindAssets("t:RichTextColorPalette");
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadAssetAtPath<RichTextColorPalette>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    private static AbilityTagDatabase FindTagDatabase()
    {
        var guids = AssetDatabase.FindAssets("t:AbilityTagDatabase");
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadAssetAtPath<AbilityTagDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

        if (_palette == null)
        {
            EditorGUILayout.HelpBox(
                "No RichTextColorPalette asset found. Create one via Assets > Create > Spiral Survivors > " +
                "Rich Text Color Palette, register your colors on it, and the buttons below will appear.",
                MessageType.Info);
        }

        DrawColoredTextField("Display Name", _displayNameProp, DisplayNameControl, 40f);
        EditorGUILayout.Space();
        DrawColoredTextField("Description", _descriptionProp, DescriptionControl, 160f);
        EditorGUILayout.Space();

        // requiredCount/requiredAugments/tags/requiredTags excluded here and drawn manually right after, so
        // the clamped count field lands directly above the list it's clamped against, and the tag pickers
        // get their toggle-button rows, instead of wherever the default draw order would otherwise put them.
        DrawPropertiesExcluding(serializedObject, "m_Script", "displayName", "description",
            "requiredCount", "requiredAugments", "tags", "requiredTags");

        var requiredAugmentsProp = serializedObject.FindProperty("requiredAugments");
        DrawRequiredCount(serializedObject.FindProperty("requiredCount"), requiredAugmentsProp);
        EditorGUILayout.PropertyField(requiredAugmentsProp, true);

        EditorGUILayout.Space();
        if (_tagDatabase == null)
        {
            EditorGUILayout.HelpBox(
                "No AbilityTagDatabase asset found. Create one via Assets > Create > Spiral Survivors > " +
                "Ability Tag Database, register your tags on it, and the toggle buttons below will appear.",
                MessageType.Info);
        }
        DrawTagPicker("Tags", serializedObject.FindProperty("tags"));
        EditorGUILayout.Space();
        DrawTagPicker("Required Tags", serializedObject.FindProperty("requiredTags"));

        serializedObject.ApplyModifiedProperties();
    }

    // Draws the raw string array (so entries can still be manually reordered/removed) plus a row of
    // toggle buttons — one per AbilityTagDatabase entry — that add/remove that tag from the array on
    // click. Same "find registered asset, render a button per entry" shape as DrawPalette below, but
    // toggling array membership instead of inserting a color tag into text.
    private void DrawTagPicker(string label, SerializedProperty tagsProp)
    {
        EditorGUILayout.PropertyField(tagsProp, new GUIContent(label), true);

        if (_tagDatabase == null || _tagDatabase.tags == null || _tagDatabase.tags.Count == 0) return;

        float maxWidth = EditorGUIUtility.currentViewWidth - 40f;
        float rowWidth = 0f;
        EditorGUILayout.BeginHorizontal();
        foreach (var tag in _tagDatabase.tags)
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
                ToggleTag(tagsProp, tag, existingIndex);
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

    private void ToggleTag(SerializedProperty tagsProp, string tag, int existingIndex)
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

    private void DrawColoredTextField(string label, SerializedProperty prop, string controlName, float minHeight)
    {
        EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

        // Explicit wordWrap — EditorGUILayout.TextArea's default style isn't guaranteed to wrap, which
        // made a single long sentence run off-screen horizontally instead of wrapping to new lines.
        _textAreaStyle ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true };

        GUI.SetNextControlName(controlName);
        EditorGUI.BeginChangeCheck();
        string newValue = EditorGUILayout.TextArea(prop.stringValue, _textAreaStyle, GUILayout.MinHeight(minHeight));
        if (EditorGUI.EndChangeCheck())
            prop.stringValue = newValue;

        DrawPalette(prop, controlName);

        if (!string.IsNullOrEmpty(prop.stringValue))
        {
            _previewStyle ??= new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };
            EditorGUILayout.LabelField("Preview:", EditorStyles.miniLabel);
            // ~18px per line at default label size — sized for ~4 lines.
            EditorGUILayout.LabelField(prop.stringValue, _previewStyle, GUILayout.MinHeight(72));
        }
    }

    private void DrawPalette(SerializedProperty prop, string controlName)
    {
        if (_palette == null || _palette.colors.Count == 0) return;

        float maxWidth = EditorGUIUtility.currentViewWidth - 40f;
        float rowWidth = 0f;
        EditorGUILayout.BeginHorizontal();
        foreach (var entry in _palette.colors)
        {
            float btnWidth = GUI.skin.button.CalcSize(new GUIContent(entry.name)).x + 10f;
            if (rowWidth > 0f && rowWidth + btnWidth > maxWidth)
            {
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                rowWidth = 0f;
            }

            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = entry.color;
            if (GUILayout.Button(entry.name, GUILayout.Width(btnWidth), GUILayout.Height(20)))
                ApplyColor(prop, controlName, entry.color);
            GUI.backgroundColor = prevColor;

            rowWidth += btnWidth + 2f;
        }
        EditorGUILayout.EndHorizontal();
    }

    private void ApplyColor(SerializedProperty prop, string controlName, Color color)
    {
        string hex = ColorUtility.ToHtmlStringRGB(color);
        string current = prop.stringValue ?? string.Empty;

        var editor = GUI.GetNameOfFocusedControl() == controlName ? GetActiveTextEditor() : null;

        string result;
        // Sanity-check the recycled editor is actually backing *this* field's current text before
        // trusting its selection indices — it's a single shared instance reused across every EditorGUI
        // text field, so a mismatch here means we grabbed it at the wrong moment.
        if (editor != null && editor.text == current)
        {
            int start = Mathf.Clamp(Mathf.Min(editor.cursorIndex, editor.selectIndex), 0, current.Length);
            int end = Mathf.Clamp(Mathf.Max(editor.cursorIndex, editor.selectIndex), 0, current.Length);

            result = end > start
                ? current.Substring(0, start) + $"<color=#{hex}>" + current.Substring(start, end - start) + "</color>" + current.Substring(end)
                : $"<color=#{hex}>{current}</color>";
        }
        else
        {
            // No live selection to read (field never focused this session, or Unity's internal text
            // editor state wasn't reachable) — fall back to wrapping the whole field.
            result = $"<color=#{hex}>{current}</color>";
        }

        prop.stringValue = result;
        serializedObject.ApplyModifiedProperties();

        // The recycled TextEditor caches its own copy of the string until refocused, so without this the
        // field would keep displaying the pre-wrap text until you clicked elsewhere and back.
        GUIUtility.keyboardControl = 0;
        GUI.FocusControl(null);
    }

    private static FieldInfo _recycledEditorField;
    private static bool _recycledEditorFieldSearched;

    // UnityEditor.EditorGUI's own text fields (TextArea/TextField) track cursor/selection through a
    // single shared "recycled" TextEditor instance private to EditorGUI — a completely different
    // mechanism from UnityEngine.GUIUtility's per-control-ID state dictionary (which backs runtime IMGUI
    // controls instead, and returns an unrelated/empty object here). Found by searching for a static
    // field whose type is TextEditor (or a subclass) rather than hardcoding a field name, since that
    // name has changed across Unity versions but the shared-instance pattern hasn't.
    private static TextEditor GetActiveTextEditor()
    {
        if (!_recycledEditorFieldSearched)
        {
            _recycledEditorFieldSearched = true;
            _recycledEditorField = typeof(EditorGUI)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(f => typeof(TextEditor).IsAssignableFrom(f.FieldType));
        }

        try
        {
            return _recycledEditorField?.GetValue(null) as TextEditor;
        }
        catch
        {
            return null;
        }
    }
}
