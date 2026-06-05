using UnityEngine;

public class RangeIndicator : MonoBehaviour
{
    private CharacterStats _stats;

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
    }

    private void Update()
    {
        transform.localScale = Vector3.one * _stats.WeaponRange;
    }
}