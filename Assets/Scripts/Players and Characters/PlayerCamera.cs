using PurrNet;
using UnityEngine;

public class PlayerCamera : NetworkBehaviour
{
    protected override void OnSpawned(bool asServer)
    {
        base.OnSpawned();

        enabled = isOwner;
        
        if (!isOwner) return;
        
        Camera.main.GetComponent<CameraController>().SetTarget(transform);
    }
}