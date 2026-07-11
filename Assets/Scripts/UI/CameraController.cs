using UnityEngine;

public class CameraController : MonoBehaviour
{
    private Transform target;
    [SerializeField] private Vector3 offset = new Vector3(0, 15, -10);
    [SerializeField] private float smoothSpeed = 8f;

    private void Start()
    {
        transform.rotation = Quaternion.LookRotation(-offset.normalized);
    }

    private void LateUpdate()
    {
        if (target == null) return;
        
        Vector3 desiredPosition = target.position + offset;
        transform.position = Vector3.Lerp(transform.position, desiredPosition, smoothSpeed * Time.deltaTime);
    }

    public void SetTarget(Transform t) => target = t;
}