using System;
using PurrNet;
using UnityEngine;

public class CharacterStats : NetworkBehaviour
{
    [Header("Movement")]
    public float baseMoveSpeed = 1f;
    public float bonusMoveSpeed = 0f;
    public float moveSpeed => baseMoveSpeed + bonusMoveSpeed;

    [Header("Weapon")]
    public float baseWeaponDamage = 10f;
    public float bonusWeaponDamage = 0f;
    public float weaponDamage => baseWeaponDamage + bonusWeaponDamage;

    public float baseWeaponRadius = 0f;
    public float bonusWeaponRadius = 0f;
    public float weaponRadius => baseWeaponRadius + bonusWeaponRadius;

    public float baseWeaponSpeed = 180f;
    public float bonusWeaponSpeed = 0f;
    public float weaponSpeed => baseWeaponSpeed + bonusWeaponSpeed;

    public int baseWeaponCount = 1;
    public int bonusWeaponCount = 0;
    public int weaponCount => baseWeaponCount + bonusWeaponCount;

    public float baseWeaponRange = 1f;
    public float bonusWeaponRange = 0f;
    public float weaponRange => baseWeaponRange + bonusWeaponRange;

    public float baseAttackSpeed = 1f;
    public float bonusAttackSpeed = 0f;
    public float attackSpeed => baseAttackSpeed + bonusAttackSpeed;

    public bool continuousFire = false;

    [Header("Health")]
    public SyncVar<float> _baseMaxHealth = new SyncVar<float>(100f);
    public float baseMaxHealth
    {
        get => _baseMaxHealth.value;
        set => _baseMaxHealth.value = value;
    }

    public SyncVar<float> _bonusMaxHealth = new SyncVar<float>(0f);
    public float bonusMaxHealth
    {
        get => _bonusMaxHealth.value;
        set => _bonusMaxHealth.value = value;
    }
    public float maxHealth => baseMaxHealth + bonusMaxHealth;
    public SyncVar<float> _currentHealth = new SyncVar<float>(0);
    public float currentHealth
    {
        get => _currentHealth.value;
        set => _currentHealth.value = value;
    }

    [Header("Shield")]
    public float baseMaxShieldPercent = 0.1f;
    public float bonusMaxShieldPercent = 0f;
    public float maxShieldPercent => baseMaxShieldPercent + bonusMaxShieldPercent;
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
    public float baseMaxEnergy = 50f;
    public float bonusMaxEnergy = 0f;
    public float maxEnergy => baseMaxEnergy + bonusMaxEnergy;
    public float energyRegen = 10f;
    public float surgeCost = 10f;
    public float surgeEffectiveness = 1f;
    public float surgeMoveSpeedMultiplier = 1.5f;
    public float surgeWeaponSpeedMultiplier = 1.5f;
    public float surgeAttackSpeedMultiplier = 1.5f;
    public SyncVar<float> _currentEnergy = new SyncVar<float>(0, ownerAuth: true);
    public float currentEnergy
    {
        get => _currentEnergy.value;
        set => _currentEnergy.value = value;
    }

    [Header("Dash")]
    public int dashCharges = 3;
    public float dashCooldown = 5f;

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

        if (isOwner && UIManager.instance != null)
            UIManager.instance.SetPlayer(this);

        if (isOwner)
            currentEnergy = maxEnergy;

        if (!isServer) return;
        currentHealth = maxHealth;
        currentShield = maxShield;
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

        _shieldRegenTimer = shieldRegenTime;

        if (currentShield > 0)
        {
            float shieldDamage = Mathf.Min(damage, currentShield);
            currentShield -= shieldDamage;
            damage -= shieldDamage;
        }

        if (damage > 0)
            currentHealth -= damage;

        if (currentHealth <= 0)
            Die();
    }

    private void Die()
    {
        isDead = true;
    }
}