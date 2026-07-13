using PurrNet.Prediction;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class PredictedTransformDefaultsPatch
{
    static PredictedTransformDefaultsPatch()
    {
        ObjectFactory.componentWasAdded += OnComponentAdded;
    }

    private static void OnComponentAdded(Component component)
    {
        var predictedTransform = component as PredictedTransform;
        if (predictedTransform == null) return;

        var so = new SerializedObject(predictedTransform);
        var prop = so.FindProperty("_characterControllerPatch");
        if (prop == null || !prop.boolValue) return;

        prop.boolValue = false;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
