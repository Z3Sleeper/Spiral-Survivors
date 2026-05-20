using PurrNet;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Analytics;

public class WeaponManager : NetworkBehaviour
{
    [SerializeField] private GameObject weaponPrefab;
    private SphereCollider _hurtbox;

    private CharacterStats _stats;
    private List<WeaponBase> _weapons = new List<WeaponBase>();
    private float _currentAngle;

    private int _currentCount;
    private float _currentRange;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
        if (!isOwner) return;

        _stats = GetComponent<CharacterStats>();
        _hurtbox = GetComponentInChildren<SphereCollider>();

        _currentCount = _stats.weaponCount;
        _currentRange = _stats.weaponRange;

        SpawnWeapons(_stats.weaponCount);
    }

    private void SpawnWeapons(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var obj = Instantiate(weaponPrefab, transform.position, Quaternion.identity);
            var weapon = obj.GetComponent<WeaponBase>();
            weapon.Init(_stats);
            SetWeaponRange(weapon);
            _weapons.Add(weapon);
        }
    }

    private void DespawnWeapons(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var weapon = _weapons[_weapons.Count - 1];
            _weapons.RemoveAt(_weapons.Count - 1);
            Destroy(weapon.gameObject);
        }
    }

    private void SetWeaponRange(WeaponBase weapon)
    {
        if (!weapon.IsMelee) return;
        weapon.transform.localScale = new Vector3(
            weapon.transform.localScale.x,
            weapon.transform.localScale.y,
            _stats.weaponRange
        );
    }

    private void Update()
    {
        if (_weapons == null || _weapons.Count == 0 || _stats.isDead) return;

        if (_stats.weaponCount != _currentCount)
        {
            int diff = _stats.weaponCount - _currentCount;
            if (diff > 0) SpawnWeapons(diff);
            else DespawnWeapons(-diff);
            _currentCount = _stats.weaponCount;
        }

        if (_stats.weaponRange != _currentRange)
        {
            _currentRange = _stats.weaponRange;
            foreach (var weapon in _weapons)
                SetWeaponRange(weapon);
        }

        _currentAngle -= _stats.weaponSpeed * Time.deltaTime;

        for (int i = 0; i < _weapons.Count; i++)
        {
            float angleOffset = (360f / _weapons.Count) * i;
            float angle = (_currentAngle + angleOffset) * Mathf.Deg2Rad;

            float halfRange = _weapons[i].IsMelee ? _currentRange / 2f : 0.5f;
            float totalRadius = _stats.weaponRadius + (_hurtbox != null ? _hurtbox.radius : 0f) + halfRange;

            Vector3 offset = new Vector3(
                Mathf.Cos(angle) * totalRadius,
                0,
                Mathf.Sin(angle) * totalRadius
            );

            _weapons[i].transform.position = transform.position + offset;

            Vector3 outwardDir = _weapons[i].transform.position - transform.position;
            _weapons[i].transform.rotation = Quaternion.LookRotation(outwardDir);
        }
    }
}