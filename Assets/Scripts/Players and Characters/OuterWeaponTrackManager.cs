using System.Collections.Generic;
using UnityEngine;

public class OuterWeaponTrackManager : MonoBehaviour
{
    private CharacterStats _stats;
    private List<WeaponBase> _weapons = new();
    private float _currentAngle;
    private int _currentCount;
    private float _currentRange;
    private float _currentSize;
    private Vector3 _weaponPrefabScale;
    private GameObject _weaponPrefab;

    public void Initialize(CharacterStats stats, GameObject weaponPrefab)
    {
        _stats = stats;
        _weaponPrefab = weaponPrefab;
        _currentCount = stats.WeaponCount;
        _currentRange = stats.WeaponRange;
        _currentSize = stats.WeaponSize;
        _weaponPrefabScale = weaponPrefab.transform.localScale;
        SpawnWeapons(stats.WeaponCount);
    }

    private void SpawnWeapons(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var obj = Instantiate(_weaponPrefab, transform.position, Quaternion.identity);
            var weapon = obj.GetComponent<WeaponBase>();
            weapon.Init(_stats);
            weapon.InitModelVisuals();
            weapon.transform.localScale = _weaponPrefabScale * _currentSize;
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
        if (weapon.hitboxTarget != null)
        {
            float rootZ = weapon.transform.localScale.z;
            float r     = _stats.WeaponRange;
            var s = weapon.hitboxTarget.localScale;
            weapon.hitboxTarget.localScale = new Vector3(s.x, s.y, r / rootZ);
            if (weapon.rangeScaleTarget != null)
            {
                var p = weapon.hitboxTarget.localPosition;
                weapon.hitboxTarget.localPosition = new Vector3(p.x, p.y, r / (2f * rootZ) - transform.lossyScale.x * 0.5f);
            }
        }
        else
        {
            weapon.transform.localScale = new Vector3(
                weapon.transform.localScale.x,
                weapon.transform.localScale.y,
                _stats.WeaponRange
            );
        }
        weapon.ScaleModel(_stats.WeaponRange);
    }

    private void Update()
    {
        if (_stats == null || _weapons.Count == 0 || _stats.IsDead) return;

        if (_stats.WeaponCount != _currentCount)
        {
            int diff = _stats.WeaponCount - _currentCount;
            if (diff > 0) SpawnWeapons(diff);
            else DespawnWeapons(-diff);
            _currentCount = _stats.WeaponCount;
        }

        if (_stats.WeaponRange != _currentRange)
        {
            _currentRange = _stats.WeaponRange;
            foreach (var weapon in _weapons)
                SetWeaponRange(weapon);
        }

        if (_stats.WeaponSize != _currentSize)
        {
            _currentSize = _stats.WeaponSize;
            foreach (var weapon in _weapons)
            {
                weapon.transform.localScale = _weaponPrefabScale * _currentSize;
                SetWeaponRange(weapon);
            }
        }

        _currentAngle += _stats.WeaponSpeed * Time.deltaTime;

        for (int i = 0; i < _weapons.Count; i++)
        {
            float angleOffset = 360f / _weapons.Count * i;
            float angle = (_currentAngle + angleOffset) * Mathf.Deg2Rad;

            float halfRange = _weapons[i].rangeScaleTarget != null
                ? _weapons[i].designOrbit * _weapons[i].transform.localScale.z
                : (_weapons[i].IsMelee ? _currentRange / 2f : 0.25f);
            float totalRadius = _stats.WeaponRadius + transform.lossyScale.x * 0.5f + halfRange + _stats.WeaponRange;

            _weapons[i].transform.position = transform.position + new Vector3(
                Mathf.Cos(angle) * totalRadius,
                0,
                Mathf.Sin(angle) * totalRadius
            );

            _weapons[i].transform.rotation = Quaternion.LookRotation(_weapons[i].transform.position - transform.position);
        }
    }

    private void OnDestroy()
    {
        foreach (var weapon in _weapons)
            if (weapon != null) Destroy(weapon.gameObject);
    }
}
