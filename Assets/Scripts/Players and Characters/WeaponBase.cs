using PurrNet;

public class WeaponBase : NetworkIdentity
{
    protected CharacterStats _stats;
    protected PlayerController _controller;
    public virtual bool IsMelee => true;

    public static event System.Action<CharacterStats, EnemyBase> OnHit;
    protected void InvokeOnHit(EnemyBase enemy) => OnHit?.Invoke(_stats, enemy);
    public static void RaiseOnHit(CharacterStats stats, EnemyBase enemy) => OnHit?.Invoke(stats, enemy);

    public virtual void Init(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
    }
}