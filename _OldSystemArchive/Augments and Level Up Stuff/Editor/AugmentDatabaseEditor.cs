#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(AugmentDatabase))]
public class AugmentDatabaseEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        if (GUILayout.Button("Clear Nulls"))
        {
            var list = serializedObject.FindProperty("augments");
            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == null)
                    list.DeleteArrayElementAtIndex(i);
            }
            serializedObject.ApplyModifiedProperties();
        }

        if (GUILayout.Button("Clear All"))
        {
            var list = serializedObject.FindProperty("augments");
            for (int i = list.arraySize - 1; i >= 0; i--)
                list.DeleteArrayElementAtIndex(i);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
