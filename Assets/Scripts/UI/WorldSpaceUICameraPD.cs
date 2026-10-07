using UnityEngine;

// Plain scene singleton (not a PredictedIdentity — this is a real Unity Camera, not a networked/spawned
// object) for the single local Overlay camera dedicated to rendering World Space UI (health bars, ability
// icons) on top of everything else via URP's Camera Stack. Every player's own PlayerUIWorldSpace Canvas
// needs its worldCamera set to THIS SAME camera (there's only one per local client, shared across every
// spawned player's own floating UI, not one per player) — see PlayerUIWorldSpace.Start()'s own read site.
// Attach this to the UI Camera GameObject in the scene (the Overlay camera parented under Main Camera).
public class WorldSpaceUICameraPD : MonoBehaviour
{
    public static Camera instance;

    private Camera _camera;

    private void Awake()
    {
        _camera = GetComponent<Camera>();
        instance = _camera;
    }

    // Not DontDestroyOnLoad — see LocalUIManagerPD.OnDestroy's own doc comment for the full reasoning
    // (guarded against nulling out a newer instance; without this, `instance` was left dangling on a
    // destroyed object across a scene return to lobby). Compares against the cached _camera, not `this`,
    // since `instance` holds the Camera component, not this script.
    private void OnDestroy()
    {
        if (instance == _camera) instance = null;
    }
}
