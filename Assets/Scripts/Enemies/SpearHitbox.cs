using UnityEngine;

public class SpearHitbox : MonoBehaviour, IEnemyAttack
{
    private SpearGloom _spear;

    private void Awake()
    {
        _spear = GetComponentInParent<SpearGloom>();
    }

    public float GetDamage() => _spear.damage * _spear.stabDamageMultiplier;
    public EnemyBase Source => _spear;
}
