using UnityEngine;

public class SpearHitbox : MonoBehaviour, IEnemyAttack
{
    private SpearEnemy _spear;

    private void Awake()
    {
        _spear = GetComponentInParent<SpearEnemy>();
    }

    public float GetDamage() => _spear.damage * _spear.stabDamageMultiplier;
    public EnemyBase Source => _spear;
}
