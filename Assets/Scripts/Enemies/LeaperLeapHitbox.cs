using UnityEngine;

public class LeaperLeapHitbox : MonoBehaviour, IEnemyAttack
{
    private LeaperGloom _leaper;

    private void Awake()
    {
        _leaper = GetComponentInParent<LeaperGloom>();
    }

    public float GetDamage() => _leaper.damage;
    public EnemyBase Source => _leaper;
}
