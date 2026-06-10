using PurrNet;
using UnityEngine;

public class WeaponArrow : NetworkIdentity
{
    private float _speed;
    private float _damage;
    private float _maxRange;
    private float _critChance;
    private float _critDamage;
    private Vector3 _direction;
    private float _distanceTravelled;
    private bool _hit;
    private Vector3 _spawnPoint;
    private AudioClip _hitSound;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
    }

    public void Init(float damage, float maxRange, Vector3 direction, float speed, float critChance, float critDamage, AudioClip hitSound = null)
    {
        _damage = damage;
        _maxRange = maxRange;
        _direction = direction.normalized;
        _speed = speed;
        _critChance = critChance;
        _critDamage = critDamage;
        _spawnPoint = transform.position;
        _hitSound = hitSound;
    }

    private void Update()
    {
        if (_hit) return;

        float step = _speed * Time.deltaTime;
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

        // Calculate Crit
        float damage = _damage;
        if (Random.value < _critChance)
            damage *= _critDamage;

        enemy.TakeDamageRpc(damage);
        SFXManager.instance?.Play(_hitSound, transform.position);
        Destroy(gameObject);
    }
}