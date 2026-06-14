using UnityEngine;

public class LeaperLeapHitbox : MonoBehaviour, IEnemyAttack
{
    private LeaperEnemy _leaper;

    private void Awake()
    {
        _leaper = GetComponentInParent<LeaperEnemy>();
    }

    public float GetDamage() => _leaper.damage;
}
