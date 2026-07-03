using PurrNet;
using UnityEngine;

public class BossBase : EnemyBase
{
    public static BossBase Current { get; private set; }

    private const float EnrageDuration = 120f;
    public SyncVar<float> _enrageTimer = new(EnrageDuration);
    private bool _isEnraged = false;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        Current = this;
        _enrageTimer.value = EnrageDuration;
    }

    protected override void Die()
    {
        Current = null;
        base.Die();
    }

    protected override void Update()
    {
        base.Update();
        if (!isServer || _isDead || _isEnraged || IsStunned) return;

        _enrageTimer.value -= Time.deltaTime;
        if (_enrageTimer.value <= 0f)
        {
            _isEnraged = true;
            OnEnrage();
        }
    }

    public float EnrageProgress => _isEnraged ? 0f : Mathf.Clamp01(_enrageTimer.value / EnrageDuration);

    protected virtual void OnEnrage() { }
}
