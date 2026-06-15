using UnityEngine;

public class BossBase : EnemyBase
{
    private const float EnrageDuration = 180f;
    private float _enrageTimer = EnrageDuration;
    private bool _isEnraged = false;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _enrageTimer = EnrageDuration;
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
