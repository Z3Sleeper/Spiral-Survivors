using PurrNet;

public class WeaponBase : NetworkIdentity
{
    protected CharacterStats _stats;
    protected PlayerController _controller;
    public virtual bool IsMelee => true;

    public virtual void Init(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
    }
}