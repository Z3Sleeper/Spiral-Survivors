using PurrNet;
using UnityEngine;
using System.Collections.Generic;

public class WeaponManager : NetworkBehaviour
{
    [SerializeField] private GameObject bladePrefab;
    private SphereCollider _hurtbox;

    private CharacterStats _stats;
    private List<WeaponBlade> _blades = new List<WeaponBlade>();
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
        
        SpawnBlades(_stats.weaponCount);
    }

    private void SpawnBlades(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var blade = Instantiate(bladePrefab, transform.position, Quaternion.identity);
            var wb = blade.GetComponent<WeaponBlade>();
            wb.Init(_stats);
            SetBladeRange(wb);
            _blades.Add(wb);
        }
    }

    private void DespawnBlades(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var blade = _blades[_blades.Count - 1];
            _blades.RemoveAt(_blades.Count - 1);
            Destroy(blade.gameObject);
        }
    }

    private void SetBladeRange(WeaponBlade blade)
    {
        blade.transform.localScale = new Vector3(
            blade.transform.localScale.x,
            blade.transform.localScale.y,
            _stats.weaponRange
        );
    }

    private void Update()
    {
        if (_blades == null || _blades.Count == 0) return;

        // Handle weapon count changes
        if (_stats.weaponCount != _currentCount)
        {
            int diff = _stats.weaponCount - _currentCount;
            if (diff > 0)
                SpawnBlades(diff);
            else
                DespawnBlades(-diff);
            _currentCount = _stats.weaponCount;
        }

        // Handle range changes
        if (_stats.weaponRange != _currentRange)
        {
            _currentRange = _stats.weaponRange;
            foreach (var blade in _blades)
                SetBladeRange(blade);
        }

        _currentAngle -= _stats.weaponSpeed * Time.deltaTime;

        float bladeHalfRange = _currentRange / 2f;
        float totalRadius = _stats.weaponRadius + (_hurtbox != null ? _hurtbox.radius : 0f) + bladeHalfRange;

        for (int i = 0; i < _blades.Count; i++)
        {
            float angleOffset = (360f / _blades.Count) * i;
            float angle = (_currentAngle + angleOffset) * Mathf.Deg2Rad;

            Vector3 offset = new Vector3(
                Mathf.Cos(angle) * totalRadius,
                0,
                Mathf.Sin(angle) * totalRadius
            );

            _blades[i].transform.position = transform.position + offset;

            Vector3 outwardDir = _blades[i].transform.position - transform.position;
            _blades[i].transform.rotation = Quaternion.LookRotation(outwardDir);
        }
    }
}