using System;
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

        // Sorts by each AugmentData ASSET's own name (a.name — the file/object name, e.g.
        // "KnightBuffStunBuffStrengthAugment"), NOT the player-facing displayName field — asset names are
        // deliberately authored in an organized scheme (ability name + effect + "Augment") distinct from
        // whatever the player-facing displayName reads, and it's the asset name this button should sort by.
        // Case-insensitive, nulls last. Direct List<AugmentData>.Sort on the target itself rather than a
        // manual SerializedProperty.MoveArrayElement shuffle (no convenient "sort" API on SerializedProperty)
        // — Undo.RecordObject/EditorUtility.SetDirty cover the same Undo/save-dirty guarantee the other two
        // buttons get from ApplyModifiedProperties, then serializedObject.Update() re-syncs the serialized
        // view against the now-reordered list so DrawDefaultInspector reflects it immediately.
        if (GUILayout.Button("Alphabetize"))
        {
            var db = (AugmentDatabase)target;
            Undo.RecordObject(db, "Alphabetize Augments");
            db.augments.Sort((a, b) =>
            {
                if (a == null && b == null) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });
            EditorUtility.SetDirty(db);
            serializedObject.Update();
        }
    }
}
