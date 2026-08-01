using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Shared "select text, click a registered color" workflow for a string field — originally lived as private
// methods on AugmentDataEditor, extracted here so any other inspector (e.g. AbilityPDEditorBase, for every
// IAbilityPD implementer's own Name/Description fields) can offer the exact same rich-text-color UX against
// a plain string SerializedProperty without duplicating the palette/preview/selection-wrapping logic. See
// RichTextColorPalette for where the named colors themselves get registered, and TagPickerGUI for the
// sibling extraction this mirrors (same "shared static drawer, one file, called from N editors" shape).
public static class ColoredTextFieldGUI
{
    private static GUIStyle _previewStyle;
    private static GUIStyle _textAreaStyle;

    public static RichTextColorPalette FindPalette()
    {
        var guids = AssetDatabase.FindAssets("t:RichTextColorPalette");
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadAssetAtPath<RichTextColorPalette>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    // controlName must be unique per on-screen field (across the whole inspector, not just this class) —
    // it's how GetActiveTextEditor below confirms which recycled editor instance is currently backing THIS
    // field before trusting its selection indices.
    public static void Draw(SerializedObject serializedObject, RichTextColorPalette palette, string label,
        SerializedProperty prop, string controlName, float minHeight)
    {
        EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

        if (palette == null)
        {
            EditorGUILayout.HelpBox(
                "No RichTextColorPalette asset found. Create one via Assets > Create > Spiral Survivors > " +
                "Rich Text Color Palette, register your colors on it, and the buttons below will appear.",
                MessageType.Info);
        }

        // Explicit wordWrap — EditorGUILayout.TextArea's default style isn't guaranteed to wrap, which
        // made a single long sentence run off-screen horizontally instead of wrapping to new lines.
        _textAreaStyle ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true };

        GUI.SetNextControlName(controlName);
        EditorGUI.BeginChangeCheck();
        string newValue = EditorGUILayout.TextArea(prop.stringValue, _textAreaStyle, GUILayout.MinHeight(minHeight));
        if (EditorGUI.EndChangeCheck())
            prop.stringValue = newValue;

        DrawPalette(serializedObject, palette, prop, controlName);

        if (!string.IsNullOrEmpty(prop.stringValue))
        {
            _previewStyle ??= new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };
            EditorGUILayout.LabelField("Preview:", EditorStyles.miniLabel);
            // ~18px per line at default label size — sized for ~4 lines.
            EditorGUILayout.LabelField(prop.stringValue, _previewStyle, GUILayout.MinHeight(72));
        }
    }

    private static void DrawPalette(SerializedObject serializedObject, RichTextColorPalette palette, SerializedProperty prop, string controlName)
    {
        if (palette == null || palette.colors.Count == 0) return;

        float maxWidth = EditorGUIUtility.currentViewWidth - 40f;
        float rowWidth = 0f;
        EditorGUILayout.BeginHorizontal();
        foreach (var entry in palette.colors)
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
                ApplyColor(serializedObject, prop, controlName, entry.color);
            GUI.backgroundColor = prevColor;

            rowWidth += btnWidth + 2f;
        }
        EditorGUILayout.EndHorizontal();
    }

    private static void ApplyColor(SerializedObject serializedObject, SerializedProperty prop, string controlName, Color color)
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
