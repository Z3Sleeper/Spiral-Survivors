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

    [Header("Shield")]
    public float maxShieldPercent = 0.1f;
    public float shieldRegenTime = 5f;
    public float maxShield => maxHealth * maxShieldPercent;
    public SyncVar<float> _currentShield = new SyncVar<float>(0);
    public float currentShield
    {
        get => _currentShield.value;
        set => _currentShield.value = value;
    }
    [NonSerialized] public float _shieldRegenTimer = 0f;

    [Header("Surge")]
    public float maxEnergy = 50f;
    public float energyRegen = 10f;
    public float energyOnHit = 2f;
    public float energyOnDamaged = 0.05f; // percent gain
    public float surgeCost = 10f;
    public float surgeUpkeep = 5f;
    public float surgeEffectiveness = 1f;
    public float surgeMoveSpeedMultiplier = 1.5f;
    public float surgeWeaponSpeedMultiplier = 1.5f;
    public float surgeAttackSpeedMultiplier = 1.5f;
    public SyncVar<float> _currentEnergy = new SyncVar<float>(0);
    public float currentEnergy
    {
        get => _currentEnergy.value;
        set => _currentEnergy.value = value;
    }

    [Header("Dash")]
    public int dashCharges = 3;
    public float dashCooldown = 5f;
    public float dashDistance = 10f;
    public float dashSpeed = 100f;

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
        if (isOwner)
            UIManager.instance?.SetPlayer(this);
        currentHealth = maxHealth;
        currentHealth = maxHealth;
        currentShield = maxShield;
        currentEnergy = maxEnergy;
    }

    private void Update()
    {
        if (!isServer) return;
        if (isDead) return;

        // Shield regen
        if (currentShield < maxShield)
        {
            _shieldRegenTimer -= Time.deltaTime;
            if (_shieldRegenTimer <= 0f)
                currentShield = maxShield;
        }

        if (Input.GetKeyDown(KeyCode.R))
            Revive();
    }

    private void OnDeathStateChanged(bool dead)
    {
        GetComponent<PlayerController>().enabled = !dead;
        transform.Find("PhysicsCollider").GetComponent<Collider>().enabled = !dead;
    }

    public void Revive(float healthPercent = 0.5f)
    {
        if (!isServer) return;
        isDead = false;
        currentHealth = maxHealth * healthPercent;
        currentShield = maxShield * healthPercent;
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
        /*
        currentHealth -= damage;
        if (currentHealth <= 0)
            Die();
        */
        _shieldRegenTimer = shieldRegenTime;

        if (currentShield > 0)
        {
            float shieldDamage = Mathf.Min(damage, currentShield);
            currentShield -= shieldDamage;
            damage -= shieldDamage;
        }

        if (damage > 0)
            currentHealth -= damage;

        currentEnergy += maxEnergy * energyOnDamaged;
        currentEnergy = Mathf.Min(currentEnergy, maxEnergy);

        if (currentHealth <= 0)
            Die();
    }

    private void Die()
    {
        isDead = true;
    }
}