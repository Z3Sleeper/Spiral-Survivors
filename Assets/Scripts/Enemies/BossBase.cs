using UnityEngine;

public class BossBase : EnemyBase
{
    public static BossBase Current { get; private set; }

    private const float EnrageDuration = 120f;
    private float _enrageTimer = EnrageDuration;
    private bool _isEnraged = false;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        Current = this;
        _enrageTimer = EnrageDuration;
    }

    protected override void Die()
    {
        Current = null;
        base.Die();
    }

    protected override void Update()
    {
        base.Update();
        if (!isServer || _isDead || _isEnraged) return;

        _enrageTimer -= Time.deltaTime;
        if (_enrageTimer <= 0f)
        {
            _isEnraged = true;
            OnEnrage();
        }
    }

    protected virtual void OnEnrage() { }
}
