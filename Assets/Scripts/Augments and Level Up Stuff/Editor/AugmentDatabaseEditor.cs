using UnityEditor;
using UnityEngine;

// Ported from _OldSystemArchive/Augments and Level Up Stuff/Editor/AugmentDatabaseEditor.cs — augments is
// just a List<AugmentData> of references, so both buttons only ever clear list entries, never touch the
// underlying AugmentData assets themselves.
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
