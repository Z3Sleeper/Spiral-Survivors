using PurrNet;
using UnityEngine;

public class ShooterEnemyProjectile : NetworkIdentity, IEnemyAttack
{
    private float _speed;
    private float _maxRange;
    private float _damage;
    public float GetDamage() => _damage;
    public EnemyBase Source { get; private set; }
    private Vector3 _direction;
    private float _distanceTravelled;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isServer;
    }

    public void Init(float damage, Vector3 direction, float speed, float maxRange, EnemyBase source)
    {
        _damage = damage;
        Source = source;
        _direction = direction.normalized;
        _speed = speed;
        _maxRange = maxRange;
    }

    private void Update()
    {
        float step = _speed * Time.deltaTime;
        transform.position += _direction * step;
        _distanceTravelled += step;

        if (_distanceTravelled >= _maxRange)
            Destroy(gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!isServer) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("PlayerHurtbox")) return;
        Destroy(gameObject);
    }
}