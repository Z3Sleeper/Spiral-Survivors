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

    private Transform _attractTarget;

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
    public void StartAttractingRPC(Transform target)
    {
        if (_attractTarget != null) return;
        _attractTarget = target;
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

    private void Collect()
    {
        if (_collected) return;
        _collected = true;
        GameManager.instance.AddXPRpc(_value);
        Destroy(gameObject);
    }
}