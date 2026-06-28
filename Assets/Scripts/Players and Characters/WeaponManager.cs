using PurrNet;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Analytics;

public class WeaponManager : NetworkBehaviour
{
    [SerializeField] private GameObject weaponPrefab;
    public GameObject WeaponPrefab => weaponPrefab;
    public bool IsRanged => !weaponPrefab.GetComponent<WeaponBase>().IsMelee;
    private CharacterStats _stats;
    private List<WeaponBase> _weapons = new List<WeaponBase>();
    private float _currentAngle;

    private int _currentCount;
    private float _currentRange;
    private float _currentSize;
    private Vector3 _weaponPrefabScale;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
        if (!isOwner) return;
        InitializeForOwner();
    }

    public void InitializeForOwner()
    {
        if (_stats != null) return;
        _stats = GetComponent<CharacterStats>();
        _currentCount = _stats.WeaponCount;
        _currentRange = _stats.WeaponRange;
        _currentSize = _stats.WeaponSize;
        _weaponPrefabScale = weaponPrefab.transform.localScale;
        SpawnWeapons(_stats.WeaponCount);
        _stats._isDeadSync.onChanged += OnDeathStateChanged;
        enabled = true;
    }

    private void OnDeathStateChanged(bool dead)
    {
        foreach (var weapon in _weapons)
            foreach (var rend in weapon.GetComponentsInChildren<Renderer>())
                rend.enabled = !dead;
    }

    private void SpawnWeapons(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var obj = Instantiate(weaponPrefab, transform.position, Quaternion.identity);
            var weapon = obj.GetComponent<WeaponBase>();
            weapon.Init(_stats);
            weapon.InitModelVisuals();
            weapon.transform.localScale = _weaponPrefabScale * _currentSize;
            SetWeaponRange(weapon);
            foreach (var applicator in obj.GetComponentsInChildren<PlayerColorApplicator>(true))
                applicator.Apply(_stats.playerColor);
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
        if (_weapons == null || _weapons.Count == 0 || _stats.IsDead) return;

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

        _currentAngle -= _stats.WeaponSpeed * Time.deltaTime;

        for (int i = 0; i < _weapons.Count; i++)
        {
            float angleOffset = 360f / _weapons.Count * i;
            float angle = (_currentAngle + angleOffset) * Mathf.Deg2Rad;

            float halfRange = _weapons[i].rangeScaleTarget != null
                ? _weapons[i].designOrbit * _weapons[i].transform.localScale.z
                : (_weapons[i].IsMelee ? _currentRange / 2f : 0.25f);
            float totalRadius = _stats.WeaponRadius + transform.lossyScale.x * 0.5f + halfRange;

            Vector3 offset = new(
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