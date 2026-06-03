using PurrNet;
using UnityEngine;

public class ShooterEnemyProjectile : NetworkIdentity
{
    private float _speed;
    private float _maxRange;
    private float _damage;
    private Vector3 _direction;
    private float _distanceTravelled;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isServer;
    }

    public void Init(float damage, Vector3 direction, float speed, float maxRange)
    {
        _damage = damage;
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
        var player = other.GetComponentInParent<CharacterStats>();
        if (player == null || player.IsDead) return;

        player.TakeDamageRpc(_damage);
        Destroy(gameObject);
    }
}