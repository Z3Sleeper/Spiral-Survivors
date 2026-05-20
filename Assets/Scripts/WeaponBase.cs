using PurrNet;

public class WeaponBase : NetworkIdentity
{
    protected CharacterStats _stats;
    public virtual bool IsMelee => true;

    public virtual void Init(CharacterStats stats)
    {
        _stats = stats;
    }
}