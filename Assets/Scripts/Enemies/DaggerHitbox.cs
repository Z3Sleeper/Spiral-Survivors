using UnityEngine;

public class DaggerHitbox : MonoBehaviour, IEnemyAttack
{
    private DaggerGloom _dagger;

    private void Awake()
    {
        _dagger = GetComponentInParent<DaggerGloom>();
    }

    public float GetDamage() => _dagger.damage * _dagger.spinDamageMultiplier;
    public EnemyBase Source => _dagger;
}
