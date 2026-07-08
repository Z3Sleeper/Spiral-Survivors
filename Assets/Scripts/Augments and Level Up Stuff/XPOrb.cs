using PurrNet;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class XPOrb : NetworkIdentity
{
    private int _value;
    private Rigidbody _rb;
    private bool _collected = false;
    private bool _settled = false;

    [SerializeField] private float scatterForce = 3f;
    [SerializeField] private float attractSpeed = 8f;
    [SerializeField] private float collectRadius = 0.5f;
    [SerializeField] private AudioClip collectSound;

    private Transform _attractTarget;
    private PlayerID _collectorId;
    private bool _attractQueued;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    protected override void OnDestroy() { CancelInvoke(); base.OnDestroy(); }

    protected override void OnPoolReset()
    {
        base.OnPoolReset();
        _collected = false;
        _settled = false;
        _attractQueued = false;
        _attractTarget = null;
        _collectorId = default;
        CancelInvoke();
        if (_rb != null)
        {
            _rb.isKinematic = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
        foreach (var col in GetComponentsInChildren<Collider>())
            col.enabled = true;
        foreach (var rend in GetComponentsInChildren<Renderer>())
            rend.enabled = true;
    }

    public void Init(int value)
    {
        _value = value;
        Scatter();
    }

    private void Scatter()
    {
        Vector3 randomDir = new Vector3(
            Random.Range(-1f, 1f),
            0,
            Random.Range(-1f, 1f)
        ).normalized;
        _rb.AddForce(randomDir * scatterForce, ForceMode.Impulse);
        Invoke(nameof(Settle), 0.5f);
    }

    private void Settle()
    {
        _settled = true;
        _rb.linearVelocity = Vector3.zero;
        _rb.isKinematic = true;
    }

    public void TryStartAttracting(Transform target, PlayerID collectorId)
    {
        if (_attractQueued || _attractTarget != null) return;
        _attractQueued = true;
        StartAttractingRPC(target, collectorId);
    }

    [ServerRpc(requireOwnership: false)]
    private void StartAttractingRPC(Transform target, PlayerID collectorId)
    {
        if (_attractTarget != null) return;
        _attractTarget = target;
        _collectorId = collectorId;
    }

    private void FixedUpdate()
    {
        if (!_settled || _collected || _attractTarget == null) return;

        transform.position = Vector3.MoveTowards(
            transform.position,
            _attractTarget.position,
            attractSpeed * Time.deltaTime
        );

        if (Vector3.Distance(transform.position, _attractTarget.position) < collectRadius)
            Collect();
    }

    private void OnTriggerStay(Collider other)
    {
        if (!_settled || _collected) return;

        if (other.gameObject.layer == LayerMask.NameToLayer("Bounds"))
        {
            transform.position = Vector3.MoveTowards(
                transform.position,
                new Vector3(0f, transform.position.y, 0f),
                attractSpeed * Time.deltaTime
            );
        }
        else if (other.gameObject.layer == LayerMask.NameToLayer("Obstacle"))
        {
            Vector3 obstacleCenter = other.bounds.center;
            Vector3 dir = new Vector3(transform.position.x - obstacleCenter.x, 0f, transform.position.z - obstacleCenter.z);
            if (dir == Vector3.zero) dir = Vector3.right;
            transform.position += dir.normalized * attractSpeed * Time.deltaTime;
        }
    }

    private void Collect()
    {
        if (_collected) return;
        _collected = true;
        GameManager.instance.AddXPRpc(_value);
        HideOnCollectRpc(_collectorId);
        Destroy(gameObject, 0.5f);
    }

    [ObserversRpc]
    private void HideOnCollectRpc(PlayerID collectorId)
    {
        _collected = true;
        foreach (var col in GetComponentsInChildren<Collider>())
            col.enabled = false;
        foreach (var rend in GetComponentsInChildren<Renderer>())
            rend.enabled = false;
        SFXManager.instance?.Play(collectSound, transform.position);
    }
}
