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
        ChargeTimer = 0f;
        _alliesInZone.Clear();
        if (dead)
            reviveProgressArea.transform.localScale = new Vector3(0f, reviveProgressArea.transform.localScale.y, 0f);
    }

    private void Update()
    {
        if (_stats == null) return;
        if (!_stats.IsDead) return;

        // Scale on all clients from synced timer
        reviveProgressArea.transform.localScale = new Vector3(
            ChargeTimer / _stats.reviveTime * 5f,
            reviveProgressArea.transform.localScale.y,
            ChargeTimer / _stats.reviveTime * 5f
        );

        if (!isServer) return;

        int aliveAllies = 0;
        foreach (var ally in _alliesInZone)
            if (!ally.IsDead) aliveAllies++;

        if (aliveAllies <= 0) return;

        ChargeTimer += Time.deltaTime * aliveAllies;

        if (ChargeTimer >= _stats.reviveTime)
        {
            _stats.Revive();
            ChargeTimer = 0f;
            _stats.reviveTime += 2f;
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