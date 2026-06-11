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

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
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

    [ServerRpc(requireOwnership:false)]
    public void StartAttractingRPC(Transform target, PlayerID collectorId)
    {
        if (_attractTarget != null) return;
        _attractTarget = target;
        _collectorId = collectorId;
    }

    private void Update()
    {
        if (!_settled || _collected) return;

        if (_attractTarget != null)
        {
            transform.position = Vector3.MoveTowards(
                transform.position,
                _attractTarget.position,
                attractSpeed * Time.deltaTime
            );

            if (Vector3.Distance(transform.position, _attractTarget.position) < collectRadius)
                Collect();
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (!_settled || _collected) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("Bounds")) return;
        transform.position = Vector3.MoveTowards(
            transform.position,
            new Vector3(0f, transform.position.y, 0f),
            attractSpeed * Time.deltaTime
        );
    }

    private void Collect()
    {
        if (_collected) return;
        _collected = true;
        PlayCollectSoundRpc(_collectorId);
        GameManager.instance.AddXPRpc(_value);
        Destroy(gameObject);
    }

    [TargetRpc]
    private void PlayCollectSoundRpc(PlayerID target)
    {
        SFXManager.instance?.Play(collectSound, transform.position);
    }
}