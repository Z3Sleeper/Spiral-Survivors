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
    private float _chargeTimer = 0f;

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
        _chargeTimer = 0f;
        _alliesInZone.Clear();
        if (dead)
            reviveProgressArea.transform.localScale = new Vector3(0f, reviveProgressArea.transform.localScale.y, 0f);
    }

    private void Update()
    {
        if (_stats == null) return;
        if (!isServer) return;
        if (!_stats.IsDead) return;

        int aliveAllies = 0;
        foreach (var ally in _alliesInZone)
            if (!ally.IsDead) aliveAllies++;

        if (aliveAllies <= 0) return;

        _chargeTimer += Time.deltaTime * aliveAllies;

        // Scale the progress zone
        reviveProgressArea.transform.localScale = new Vector3(
            _chargeTimer/_stats.reviveTime * 5f,
            reviveProgressArea.transform.localScale.y,
            _chargeTimer/_stats.reviveTime * 5f
        );

        if (_chargeTimer >= _stats.reviveTime)
        {
            _stats.Revive();
            reviveProgressArea.transform.localScale = new Vector3(0f, reviveProgressArea.transform.localScale.y, 0f);
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