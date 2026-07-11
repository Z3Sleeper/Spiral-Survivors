using System.Collections.Generic;
using UnityEngine;

public class WeaponManagerPD : MonoBehaviour
{
    [SerializeField] private GameObject weaponPrefab;
    [SerializeField] private Transform  _visualsRoot;

    public GameObject WeaponPrefab      => weaponPrefab;
    public bool       IsRanged          => !weaponPrefab.GetComponent<WeaponBladePD>().IsMelee;
    public int        WeaponCount       => _weapons.Count;
    public Transform  VisualsRoot       => _visualsRoot;
    public Vector3    WeaponPrefabScale => _weaponPrefabScale;

    private CharacterStatsPD             _stats;
    private readonly List<WeaponBladePD> _weapons = new();
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
            var blade = _weapons[_weapons.Count - 1];
            _weapons.RemoveAt(_weapons.Count - 1);
            _stats.hierarchy.Delete(blade.gameObject);
        }
    }

    // ── Blade registration ────────────────────────────────────────────────────

    public void RegisterBlade(WeaponBladePD blade)
    {
        if (_weapons.Contains(blade)) return;
        blade.SlotIndex = _weapons.Count;
        _weapons.Add(blade);
        blade.Init(_stats);
        blade.InitModelVisuals();
        blade.transform.localScale = _weaponPrefabScale * _currentSize;
        SetupBladeRange(blade);
        foreach (var a in blade.GetComponentsInChildren<PlayerColorApplicator>(true))
            a.Apply(_stats.PlayerColor);
    }

    public void UnregisterBlade(WeaponBladePD blade)
    {
        if (!_weapons.Remove(blade)) return;
        for (int i = 0; i < _weapons.Count; i++)
            _weapons[i].SlotIndex = i;
    }

    // ── Range / size helpers ──────────────────────────────────────────────────

    public void SetupBladeRange(WeaponBladePD blade)
    {
        if (!blade.IsMelee) return;
        if (blade.hitboxTarget != null)
        {
            float rootZ = blade.transform.localScale.z;
            float r     = _stats.WeaponRange;
            var s = blade.hitboxTarget.localScale;
            blade.hitboxTarget.localScale = new Vector3(s.x, s.y, r / rootZ);
            if (blade.rangeScaleTarget != null)
            {
                var p = blade.hitboxTarget.localPosition;
                blade.hitboxTarget.localPosition = new Vector3(p.x, p.y, r / (2f * rootZ) - transform.lossyScale.x * 0.5f);
            }
        }
        else
        {
            blade.transform.localScale = new Vector3(
                blade.transform.localScale.x,
                blade.transform.localScale.y,
                _stats.WeaponRange);
        }
        blade.ScaleModel(_stats.WeaponRange);
        blade._rootScale = blade.transform.localScale;
        blade._range     = _stats.WeaponRange;
    }

    public void ApplyColor(Color c)
    {
        foreach (var blade in _weapons)
            foreach (var a in blade.GetComponentsInChildren<PlayerColorApplicator>(true))
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
            foreach (var blade in _weapons) SetupBladeRange(blade);
        }

        if (_stats.WeaponSize != _currentSize)
        {
            _currentSize = _stats.WeaponSize;
            foreach (var blade in _weapons)
            {
                blade.transform.localScale = _weaponPrefabScale * _currentSize;
                SetupBladeRange(blade);
            }
        }
    }
}
