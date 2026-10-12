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
        _currentRange      = stats.EffectiveWeaponRange;
        _currentSize       = stats.Size;
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
            // Explicit false, not just relying on a fresh instance's own default (2026-08-31 fix) — weapon
            // instances go through pooled reuse here (see InitModelVisuals's own doc comment: RegisterWeapon
            // runs "on every logical spawn, including pooled reuse"), so an instance that was an outer-track
            // weapon in its PREVIOUS life would otherwise keep IsOuterTrack stuck true on this one, making
            // OrbitalPosition (WeaponBasePD.cs) read the wrong radius offset AND the wrong track's slot
            // count/spacing for it — landing this main-track weapon directly overlapping an outer-track one
            // instead of on its own ring. Confirmed live: Knight with OuterTrack + SurgeWeaponCount, the
            // Surge-spawned main-track weapon rendered on top of an outer-track weapon instead of opposite
            // the original main-track one.
            weapon.IsOuterTrack = false;
            weapon.SlotIndex = _weapons.Count;
            _weapons.Add(weapon);
        }

        weapon.Init(_stats);
        weapon.InitModelVisuals();
        // No Size multiply here anymore (2026-09-30, was _weaponPrefabScale * _currentSize) — Size no longer
        // scales any weapon's model/hitbox geometry at all, only the clearance-offset terms that already read
        // _stats.Size directly (NominalOrbitRadius/ApplyHeightLift, and SetupWeaponRange's own hitboxTarget
        // formula below). This was previously undone every call anyway by WeaponBasePD.ScaleModel's
        // own bandaid reset — see project_weapon_size_decoupling_bandaid memory (now resolved/removed) for the
        // full history of why that stopgap existed and why it's no longer needed once this line changed.
        weapon.transform.localScale = _weaponPrefabScale;
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

    // EffectiveWeaponRange, not the raw WeaponRange stat, throughout this whole method (2026-09-14) — this
    // sets the WEAPON'S OWN actual physical geometry (hitbox depth, model scale, RangeValue), so it needs to
    // track whatever a class's own weaponRangeScalingPercent trade-off actually leaves the weapon reaching —
    // see CharacterStatsPD.EffectiveWeaponRange's own doc comment for the full "why" this stays a separate
    // value from the raw stat any augment might read as its own scaling input.
    public void SetupWeaponRange(IWeaponPD weapon)
    {
        if (!weapon.IsMelee) return;
        if (weapon.hitboxTarget != null)
        {
            float rootZ = weapon.transform.localScale.z;
            float r     = _stats.EffectiveWeaponRange;
            var s = weapon.hitboxTarget.localScale;
            weapon.hitboxTarget.localScale = new Vector3(s.x, s.y, r / rootZ);
            if (weapon.rangeScaleTarget != null)
            {
                // Both terms divided by rootZ uniformly, not just the r term — the character-radius
                // clearance is a WORLD-space quantity (_stats.Size already IS the character's current world
                // scale — see ExeAxeThrowAbilityPD.spawnOffsetBase's own doc comment for why this reads the
                // stat directly rather than transform.lossyScale.x, numerically identical here too since this
                // component lives on the same player root CharacterStatsPD does), but this whole expression
                // is written in the weapon's own LOCAL space, which Unity implicitly re-multiplies by rootZ
                // (the weapon's own Size-driven scale) when resolving hitboxTarget's world position from its
                // local one. Previously only r was divided by rootZ before that implicit multiply, so it came
                // back out as exactly r (correct, scale-invariant) — but the character-radius term came back
                // out as Size*0.5f*rootZ instead of the intended Size*0.5f, compounding with the weapon's OWN
                // Size-driven scale on top of already including the character's Size once directly. That's
                // what pulled the hitbox's near edge back past the handle at higher Size (confirmed against a
                // screenshot where it visibly intruded into the hurtbox) while staying correct at Size 1
                // (rootZ's multiplier was 1, so the bug was invisible there) — dividing it too keeps the
                // clearance scaling linearly with just the character's own Size, not the weapon's Size on top
                // of that.
                var p = weapon.hitboxTarget.localPosition;
                weapon.hitboxTarget.localPosition = new Vector3(p.x, p.y, (r * 0.5f - _stats.Size * 0.5f) / rootZ);
            }
        }
        else
        {
            weapon.transform.localScale = new Vector3(
                weapon.transform.localScale.x,
                weapon.transform.localScale.y,
                _stats.EffectiveWeaponRange);
        }
        weapon.ScaleModel(_stats.EffectiveWeaponRange);
        weapon.RootScale  = weapon.transform.localScale;
        weapon.RangeValue = _stats.EffectiveWeaponRange;
    }

    // Farthest current outer hit-detection edge across every registered weapon (both rings) — used by
    // PlayerRangeIndicatorPD so the ring always reflects whichever weapon actually reaches furthest right
    // now, not a guessed stat formula. See IWeaponPD.GetOuterReach for the per-weapon-type math — purely
    // design/stat-derived (no origin needed here at all), so this stays stable regardless of where any
    // weapon's orbit is actually currently centered.
    public bool TryGetFarthestWeaponReach(out float reach)
    {
        reach = 0f;
        bool found = false;

        foreach (var weapon in _weapons)
        {
            float d = weapon.GetOuterReach();
            if (!found || d > reach) { reach = d; found = true; }
        }
        foreach (var weapon in _outerWeapons)
        {
            float d = weapon.GetOuterReach();
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

    // Every owned weapon, both rings — see IWeaponPD.SetRenderersEnabled's own doc comment. Driven
    // generically off death (see LateUpdate's own edge detection below) rather than any specific ability, so
    // every character's weapons hide/show the same way regardless of class.
    public void SetWeaponsVisible(bool visible)
    {
        foreach (var weapon in _weapons) weapon.SetRenderersEnabled(visible);
        foreach (var weapon in _outerWeapons) weapon.SetRenderersEnabled(visible);
    }

    // Edge-detected (not "while dead"), same _wasDeadLastFrame pattern PlayerControllerPD's own
    // physics-collider toggle already uses — fires SetWeaponsVisible exactly once per death/revive
    // transition instead of every frame.
    private bool _wasDeadLastFrame;

    // ── LateUpdate: stat monitoring only ─────────────────────────────────────

    private void LateUpdate()
    {
        if (_stats == null) return;

        bool isDead = _stats.IsDead;
        if (isDead != _wasDeadLastFrame)
        {
            _wasDeadLastFrame = isDead;
            SetWeaponsVisible(!isDead);
        }

        if (isDead) return;

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

        // EffectiveWeaponRange, not the raw WeaponRange stat (2026-09-14) — this is genuine weapon geometry
        // (actual model/hitbox size), so it needs to track whatever a class's own weaponRangeScalingPercent
        // trade-off actually leaves the weapon reaching — see CharacterStatsPD.EffectiveWeaponRange's own doc
        // comment, and SetupWeaponRange's own identical change below.
        if (_stats.EffectiveWeaponRange != _currentRange)
        {
            _currentRange = _stats.EffectiveWeaponRange;
            foreach (var weapon in _weapons) SetupWeaponRange(weapon);
            foreach (var weapon in _outerWeapons) SetupWeaponRange(weapon);
        }

        // _currentSize is now purely a change-detector (2026-09-30) — Size no longer scales any weapon's own
        // transform (see RegisterWeapon's own comment), but SetupWeaponRange still needs re-running on a Size
        // change: its own hitboxTarget formula bakes _stats.Size into a LOCAL position at call time (unlike
        // NominalOrbitRadius/ApplyHeightLift, which read Size live every tick and need no such trigger).
        if (_stats.Size != _currentSize)
        {
            _currentSize = _stats.Size;
            foreach (var weapon in _weapons) SetupWeaponRange(weapon);
            foreach (var weapon in _outerWeapons) SetupWeaponRange(weapon);
        }
    }
}
