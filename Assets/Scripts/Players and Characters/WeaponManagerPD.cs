using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

public class WeaponManagerPD : MonoBehaviour
{
    [SerializeField] private GameObject weaponPrefab;
    [SerializeField] private Transform  _visualsRoot;

    public GameObject WeaponPrefab      => weaponPrefab;
    public bool       IsRanged          => !weaponPrefab.GetComponent<IWeaponPD>().IsMelee;
    public int        WeaponCount       => _weapons.Count;
    // OuterTrackAugment's second ring — see RegisterWeapon/_spawningOuter for how a freshly-spawned weapon
    // ends up in this list instead of the main one, and OrbitalPosition (WeaponBasePD.cs) for how this
    // separate count keeps the two rings' own angular spacing from interfering with each other.
    public int        OuterWeaponCount  => _outerWeapons.Count;
    public Transform  VisualsRoot       => _visualsRoot;
    public Vector3    WeaponPrefabScale => _weaponPrefabScale;

    private CharacterStatsPD          _stats;
    private readonly List<IWeaponPD> _weapons = new();
    private readonly List<IWeaponPD> _outerWeapons = new();
    private int     _currentCount;
    private int     _currentOuterCount;
    private float   _currentRange;
    private float   _currentSize;
    private Vector3 _weaponPrefabScale;
    private int     _pendingSpawn;
    // Set for the duration of a SpawnOuterWeapons() call only — hierarchy.Create() synchronously fires the
    // new weapon's LateAwake -> TryInit -> RegisterWeapon before returning, so by the time control gets
    // back here it would already be too late to tell a freshly-created instance "you're outer track" after
    // the fact. Reading this flag from inside RegisterWeapon itself is what makes that classification
    // possible instead.
    private bool    _spawningOuter;

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

    // Same weaponPrefab, same hierarchy.Create loop as SpawnWeapons — only the _spawningOuter bracketing
    // differs, which is what RegisterWeapon reads to route these specific instances into _outerWeapons
    // instead of _weapons. Safe because hierarchy.Create's LateAwake->TryInit->RegisterWeapon chain runs
    // synchronously per call, so the flag is never ambiguous about which spawn burst is "in progress".
    private void SpawnOuterWeapons(int count)
    {
        _spawningOuter = true;
        for (int i = 0; i < count; i++)
            _stats.hierarchy.Create(weaponPrefab, transform.position, Quaternion.identity, _stats.owner);
        _spawningOuter = false;
    }

    private void DespawnOuterWeapons(int count)
    {
        for (int i = 0; i < count && _outerWeapons.Count > 0; i++)
        {
            var weapon = _outerWeapons[_outerWeapons.Count - 1];
            _outerWeapons.RemoveAt(_outerWeapons.Count - 1);
            _stats.hierarchy.Delete(weapon.transform.gameObject);
        }
    }

    // ── Weapon registration ───────────────────────────────────────────────────

    public void RegisterWeapon(IWeaponPD weapon)
    {
        if (_weapons.Contains(weapon) || _outerWeapons.Contains(weapon)) return;

        if (_spawningOuter)
        {
            weapon.IsOuterTrack = true;
            weapon.SlotIndex = _outerWeapons.Count;
            _outerWeapons.Add(weapon);
        }
        else
        {
            weapon.SlotIndex = _weapons.Count;
            _weapons.Add(weapon);
        }

        weapon.Init(_stats);
        weapon.InitModelVisuals();
        weapon.transform.localScale = _weaponPrefabScale * _currentSize;
        SetupWeaponRange(weapon);
        foreach (var a in weapon.transform.GetComponentsInChildren<PlayerColorApplicator>(true))
            a.Apply(_stats.PlayerColor);
    }

    public void UnregisterWeapon(IWeaponPD weapon)
    {
        if (weapon.IsOuterTrack)
        {
            _outerWeapons.Remove(weapon);
            for (int i = 0; i < _outerWeapons.Count; i++)
                _outerWeapons[i].SlotIndex = i;
        }
        else
        {
            _weapons.Remove(weapon);
            for (int i = 0; i < _weapons.Count; i++)
                _weapons[i].SlotIndex = i;
        }
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

    // Farthest current outer hit-detection edge across every registered weapon (both rings) — used by
    // PlayerRangeIndicatorPD so the ring always reflects whichever weapon actually reaches furthest right
    // now, not a guessed stat formula. See IWeaponPD.GetOuterReach for the per-weapon-type math.
    public bool TryGetFarthestWeaponReach(out float reach)
    {
        reach = 0f;
        bool found = false;
        Vector3 origin = _stats.transform.position;

        foreach (var weapon in _weapons)
        {
            float d = weapon.GetOuterReach(origin);
            if (!found || d > reach) { reach = d; found = true; }
        }
        foreach (var weapon in _outerWeapons)
        {
            float d = weapon.GetOuterReach(origin);
            if (!found || d > reach) { reach = d; found = true; }
        }
        return found;
    }

    public void ApplyColor(Color c)
    {
        foreach (var weapon in _weapons)
            foreach (var a in weapon.transform.GetComponentsInChildren<PlayerColorApplicator>(true))
                a.Apply(c);
        foreach (var weapon in _outerWeapons)
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

        // Outer track (OuterTrackAugment): exists only while weaponTracks >= 2 — see
        // CharacterStatsPD.weaponTracks' own doc comment, OuterTrackAugment is the only thing that raises
        // it — and always mirrors the main track's own WeaponCount ("copies weaponCount" per spec), so
        // there's no separate outer-count stat to track, just this target derivation.
        int targetOuterCount = _stats.weaponTracks >= 2 ? _stats.WeaponCount : 0;
        if (_stats.isServer && targetOuterCount != _currentOuterCount)
        {
            int diff = targetOuterCount - _currentOuterCount;
            if (diff > 0) SpawnOuterWeapons(diff);
            else DespawnOuterWeapons(-diff);
            _currentOuterCount = targetOuterCount;
        }

        if (_stats.WeaponRange != _currentRange)
        {
            _currentRange = _stats.WeaponRange;
            foreach (var weapon in _weapons) SetupWeaponRange(weapon);
            foreach (var weapon in _outerWeapons) SetupWeaponRange(weapon);
        }

        if (_stats.WeaponSize != _currentSize)
        {
            _currentSize = _stats.WeaponSize;
            foreach (var weapon in _weapons)
            {
                weapon.transform.localScale = _weaponPrefabScale * _currentSize;
                SetupWeaponRange(weapon);
            }
            foreach (var weapon in _outerWeapons)
            {
                weapon.transform.localScale = _weaponPrefabScale * _currentSize;
                SetupWeaponRange(weapon);
            }
        }
    }
}
