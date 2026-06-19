using UnityEngine;

public class PierceToPercentDamageAugment : MonoBehaviour
{
    [SerializeField] private float _damagePerPierce = 3f;
    [SerializeField] private float _pierceFlat = 3f;

    private CharacterStats _stats;
    private float _contribution = 0f;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusPierceFlat += _pierceFlat;
    }

    private void Update()
    {
        if (!_stats.isSpawned) return;
        float newContribution = _stats.Pierce * _damagePerPierce;
        if (newContribution == _contribution) return;

        _stats.bonusWeaponDamageFlat += newContribution - _contribution;
        _contribution = newContribution;
    }

    private void OnDestroy()
    {
        if (_stats != null && _stats.isSpawned)
        {
            _stats.bonusPierceFlat -= _pierceFlat;
            _stats.bonusWeaponDamageFlat -= _contribution;
        }
    }
}
