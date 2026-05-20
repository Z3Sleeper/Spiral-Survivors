using System;
using System.ComponentModel;
using PurrNet;
using UnityEngine;

public class CharacterStats : NetworkBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;

    [Header("Weapon")]
    public float weaponDamage = 10f;
    public float weaponRadius = 0f;
    public float weaponSpeed = 180f; // degrees per second
    public int weaponCount = 1;
    public float weaponRange = 1f;
    public float attackSpeed = 1f;
    public bool continuousFire = false;

    [Header("Health")]
    public float maxHealth = 100f;
    public SyncVar<float> _currentHealth = new SyncVar<float>(0);
    public float currentHealth
    {
        get => _currentHealth.value;
        set => _currentHealth.value = value;
    }

    [HideInInspector] public SyncVar<bool> _isDeadSync = new SyncVar<bool>(false);
    public bool isDead
    {
        get => _isDeadSync.value;
        set => _isDeadSync.value = value;
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();

        _isDeadSync.onChanged += OnDeathStateChanged;

        if (!isServer) return;

        currentHealth = maxHealth;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
            Revive();
    }

    private void OnDeathStateChanged(bool dead)
    {
        GetComponent<PlayerMovement>().enabled = !dead;
        transform.Find("PhysicsCollider").GetComponent<Collider>().enabled = !dead;
    }

    public void Revive(float healthPercent = 0.5f)
    {
        if (!isServer) return;
        isDead = false;
        currentHealth = maxHealth * healthPercent;
    }

    [ServerRpc(requireOwnership: false)]
    public void TakeDamageRpc(float damage)
    {
        TakeDamage(damage);
    }

    private void TakeDamage(float damage)
    {
        if (!isServer) return;
        if (isDead) return;
        currentHealth -= damage;
        if (currentHealth <= 0)
            Die();
    }

    private void Die()
    {
        isDead = true;
    }
}