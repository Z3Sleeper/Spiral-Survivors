using System.Collections.Generic;
using UnityEngine;

public class WeaponManagerPD : MonoBehaviour
{
    [SerializeField] private GameObject weaponPrefab;
    [SerializeField] private Transform  _visualsRoot;

    public GameObject WeaponPrefab      => weaponPrefab;
    public bool       IsRanged          => !weaponPrefab.GetComponent<IWeaponPD>().IsMelee;
    public int        WeaponCount       => _weapons.Count;
    public Transform  VisualsRoot       => _visualsRoot;
    public Vector3    WeaponPrefabScale => _weaponPrefabScale;

    private CharacterStatsPD          _stats;
    private readonly List<IWeaponPD> _weapons = new();
    private int     _currentCount;
    private float   _currentRange;
    private float   _currentSize;
    private Vector3 _weaponPrefabScale;
    private int     _pendingSpawn;

    // ── Init ──────────────────────────────────────────────────────────────────

    public void Init(CharacterStatsPD stats)
    {
        _stats             = stats;
        _currentCount      = stats.WeaponCount;
        _currentRange      = stats.WeaponRange;
        _currentSize       = stats.WeaponSize;
        _weaponPrefabScale = weaponPrefab.transform.localScale;
        if (stats.isServer) _pendingSpawn = stats.WeaponCount;
    }

    private void SpawnWeapons(int count)
    {
        for (int i = 0; i < count; i++)
            _stats.hierarchy.Create(weaponPrefab, transform.position, Quaternion.identity, _stats.owner);
    }

    private void DespawnWeapons(int count)
    {
        for (int i = 0; i < count && _weapons.Count > 0; i++)
        {
            var weapon = _weapons[_weapons.Count - 1];
            _weapons.RemoveAt(_weapons.Count - 1);
            _stats.hierarchy.Delete(weapon.transform.gameObject);
        }
    }

    // ── Weapon registration ───────────────────────────────────────────────────

    public void RegisterWeapon(IWeaponPD weapon)
    {
        if (_weapons.Contains(weapon)) return;
        weapon.SlotIndex = _weapons.Count;
        _weapons.Add(weapon);
        weapon.Init(_stats);
        weapon.InitModelVisuals();
        weapon.transform.localScale = _weaponPrefabScale * _currentSize;
        SetupWeaponRange(weapon);
        foreach (var a in weapon.transform.GetComponentsInChildren<PlayerColorApplicator>(true))
            a.Apply(_stats.PlayerColor);
    }

    public void UnregisterWeapon(IWeaponPD weapon)
    {
        if (!_weapons.Remove(weapon)) return;
        for (int i = 0; i < _weapons.Count; i++)
            _weapons[i].SlotIndex = i;
    }

    // ── Range / size helpers ──────────────────────────────────────────────────

    public void SetupWeaponRange(IWeaponPD weapon)
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
                _stats.WeaponRange);
        }
        weapon.ScaleModel(_stats.WeaponRange);
        weapon.RootScale  = weapon.transform.localScale;
        weapon.RangeValue = _stats.WeaponRange;
    }

    public void ApplyColor(Color c)
    {
        foreach (var weapon in _weapons)
            foreach (var a in weapon.transform.GetComponentsInChildren<PlayerColorApplicator>(true))
                a.Apply(c);
    }

    // ── LateUpdate: stat monitoring only ─────────────────────────────────────

    private void LateUpdate()
    {
        if (_stats == null || _stats.IsDead) return;

        if (_pendingSpawn > 0)
        {
            SpawnWeapons(_pendingSpawn);
            _pendingSpawn = 0;
        }

        if (_stats.isServer && _stats.WeaponCount != _currentCount)
        {
            int diff = _stats.WeaponCount - _currentCount;
            if (diff > 0) SpawnWeapons(diff);
            else DespawnWeapons(-diff);
            _currentCount = _stats.WeaponCount;
        }

        if (_stats.WeaponRange != _currentRange)
        {
            _currentRange = _stats.WeaponRange;
            foreach (var weapon in _weapons) SetupWeaponRange(weapon);
        }

        if (_stats.WeaponSize != _currentSize)
        {
            _currentSize = _stats.WeaponSize;
            foreach (var weapon in _weapons)
            {
                weapon.transform.localScale = _weaponPrefabScale * _currentSize;
                SetupWeaponRange(weapon);
            }
        }
    }
}
