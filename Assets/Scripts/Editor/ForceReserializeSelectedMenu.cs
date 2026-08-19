using UnityEditor;
using UnityEngine;

// Utility for dropping orphaned serialized data left behind on a prefab/asset after a field is removed from
// its script — Unity's YAML serializer only writes exactly what the current component declares once an
// asset is fully rewritten, but simply deleting a [SerializeField] doesn't trigger that rewrite on its own,
// so the old value silently lingers in the file forever otherwise. Select the affected assets in the Project
// window and run this to force a clean rewrite.
public static class ForceReserializeSelectedMenu
{
    [MenuItem("Assets/Force Reserialize Selected")]
    private static void ForceReserializeSelected()
    {
        string[] paths = Selection.assetGUIDs.Length > 0
            ? System.Array.ConvertAll(Selection.assetGUIDs, AssetDatabase.GUIDToAssetPath)
            : System.Array.Empty<string>();

        if (paths.Length == 0)
        {
            Debug.LogWarning("Force Reserialize Selected: nothing selected in the Project window.");
            return;
        }

        AssetDatabase.ForceReserializeAssets(paths, ForceReserializeAssetsOptions.ReserializeAssetsAndMetadata);
        Debug.Log($"Force Reserialize Selected: reserialized {paths.Length} asset(s).");
    }
}
