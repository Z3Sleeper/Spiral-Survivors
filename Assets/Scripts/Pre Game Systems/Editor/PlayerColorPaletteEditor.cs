#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(PlayerColorPalette))]
public class PlayerColorPaletteEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        if (GUILayout.Button("Clear Defaults"))
        {
            var list = serializedObject.FindProperty("colors");
            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                var c = list.GetArrayElementAtIndex(i).colorValue;
                if (c.a == 0f && c.r == 0f && c.g == 0f && c.b == 0f)
                    list.DeleteArrayElementAtIndex(i);
            }
            serializedObject.ApplyModifiedProperties();
        }

        if (GUILayout.Button("Clear All"))
        {
            var list = serializedObject.FindProperty("colors");
            for (int i = list.arraySize - 1; i >= 0; i--)
                list.DeleteArrayElementAtIndex(i);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
