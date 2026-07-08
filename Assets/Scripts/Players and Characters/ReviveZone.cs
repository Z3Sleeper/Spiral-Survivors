using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class ReviveZone : NetworkIdentity
{
    [SerializeField] private GameObject reviveArea;
    [SerializeField] private Collider reviveAreaCollider;
    [SerializeField] private GameObject reviveProgressArea;
    private CharacterStats _stats;
    private HashSet<CharacterStats> _alliesInZone = new HashSet<CharacterStats>();
    public SyncVar<float> _chargeTimerSync = new SyncVar<float>(0f);
    private float ChargeTimer
    {
        get => _chargeTimerSync.value;
        set => _chargeTimerSync.value = value;
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _stats = GetComponentInParent<CharacterStats>();
        _stats._isDeadSync.onChanged += OnDeathStateChanged;
        reviveAreaCollider.enabled = false;
        reviveProgressArea.transform.localScale = new Vector3(0f, reviveProgressArea.transform.localScale.y, 0f);
        reviveProgressArea.SetActive(false);
        reviveArea.SetActive(false);
    }

    private void OnDeathStateChanged(bool dead)
    {
        reviveAreaCollider.enabled = dead;
        reviveProgressArea.SetActive(dead);
        reviveArea.SetActive(dead);
        if (isServer) ChargeTimer = 0f;
        _alliesInZone.Clear();
        if (dead)
            reviveProgressArea.transform.localScale = new Vector3(0f, reviveProgressArea.transform.localScale.y, 0f);
    }

    private void Update()
    {
        if (_stats == null) return;
        if (!_stats.IsDead) return;

        // Scale on all clients from synced timer
        float t = Mathf.Clamp01(ChargeTimer / _stats.reviveTime);
        reviveProgressArea.transform.localScale = new Vector3(
            t * 10f,
            reviveProgressArea.transform.localScale.y,
            t * 10f
        );

        if (!isServer) return;

        float chargeRate = 0f;
        foreach (var ally in _alliesInZone)
            if (!ally.IsDead) chargeRate += ally.revivePower;

        if (chargeRate <= 0f) return;

        ChargeTimer += Time.deltaTime * chargeRate * _stats.revivePower;

        if (ChargeTimer >= _stats.reviveTime)
        {
            _stats.Revive();
            ChargeTimer = 0f;
            _stats.reviveTime += _stats.ReviveTimePenalty;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!isServer) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("PlayerHurtbox")) return;
        var ally = other.GetComponentInParent<CharacterStats>();
        if (ally == null || ally == _stats) return;
        _alliesInZone.Add(ally);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!isServer) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("PlayerHurtbox")) return;
        var ally = other.GetComponentInParent<CharacterStats>();
        if (ally == null || ally == _stats) return;
        _alliesInZone.Remove(ally);
    }
}