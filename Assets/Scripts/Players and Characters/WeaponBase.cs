using PurrNet;

public class WeaponBase : NetworkIdentity
{
    protected CharacterStats _stats;
    protected PlayerController _controller;
    public virtual bool IsMelee => true;

    public static event System.Action<CharacterStats, EnemyBase, float> OnStrike;
    protected void InvokeOnStrike(EnemyBase enemy)
    {
        OnStrike?.Invoke(_stats, enemy, 1f);
        RaiseOnHit(_stats, enemy);
    }
    public static void RaiseOnStrike(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f)
    {
        OnStrike?.Invoke(stats, enemy, effectiveness);
        RaiseOnHit(stats, enemy, effectiveness);
    }

    public static event System.Action<CharacterStats, EnemyBase, float> OnHit;
    public static void RaiseOnHit(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f) => OnHit?.Invoke(stats, enemy, effectiveness);

    public static event System.Action<CharacterStats, EnemyBase, float> OnCritStrike;
    public static void RaiseOnCritStrike(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f) => OnCritStrike?.Invoke(stats, enemy, effectiveness);

    public virtual void Init(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
    }
}
