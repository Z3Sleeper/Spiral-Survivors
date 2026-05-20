using PurrNet;
using UnityEngine;

public class WeaponArrow : NetworkIdentity
{
    [SerializeField] private float travelSpeed = 20f;

    private float _damage;
    private float _maxRange;
    private Vector3 _direction;
    private float _distanceTravelled;
    private bool _hit;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
    }

    public void Init(float damage, float maxRange, Vector3 direction)
    {
        _damage = damage;
        _maxRange = maxRange;
        _direction = direction.normalized;
    }

    private void Update()
    {
        if (_hit) return;

        float step = travelSpeed * Time.deltaTime;
        transform.position += _direction * step;
        _distanceTravelled += step;

        if (_distanceTravelled >= _maxRange)
            Destroy(gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (_hit) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("EnemyHurtbox")) return;

        var enemy = other.GetComponentInParent<EnemyBase>();
        if (enemy == null) return;

        _hit = true;
        enemy.TakeDamageRpc(_damage);
        Destroy(gameObject);
    }
}