using System;
using PurrNet;
using UnityEngine;

public class CharacterStats : NetworkBehaviour
{
    [Header("Movement")]
    public float baseMoveSpeed = 1f;
    [NonSerialized] public float bonusMoveSpeed = 0f;
    public float MoveSpeed => baseMoveSpeed + bonusMoveSpeed;

    [Header("Weapon")]
    public float baseWeaponDamage = 10f;
    [NonSerialized] public float bonusWeaponDamage = 0f;
    public float WeaponDamage => baseWeaponDamage + bonusWeaponDamage;

    public float baseWeaponRadius = 0f;
    [NonSerialized] public float bonusWeaponRadius = 0f;
    public float WeaponRadius => baseWeaponRadius + bonusWeaponRadius;

    public float baseWeaponSpeed = 180f;
    [NonSerialized] public float bonusWeaponSpeed = 0f;
    public float WeaponSpeed => baseWeaponSpeed + bonusWeaponSpeed;

    public int baseWeaponCount = 1;
    [NonSerialized] public int bonusWeaponCount = 0;
    public int WeaponCount => baseWeaponCount + bonusWeaponCount;

    public float baseWeaponRange = 1f;
    [NonSerialized] public float bonusWeaponRange = 0f;
    public float WeaponRange => baseWeaponRange + bonusWeaponRange;

    public float baseAttackSpeed = 1f;
    [NonSerialized] public float bonusAttackSpeed = 0f;
    public float AttackSpeed => baseAttackSpeed + bonusAttackSpeed;

    public bool continuousFire = false;

    [Header("Health")]
    public SyncVar<float> _baseMaxHealth = new(100f);
    [HideInInspector] public float BaseMaxHealth
    {
        get => _baseMaxHealth.value;
        set => _baseMaxHealth.value = value;
    }

    [HideInInspector] public SyncVar<float> _bonusMaxHealth = new(0f);
    [HideInInspector] public float BonusMaxHealth
    {
        get => _bonusMaxHealth.value;
        set => _bonusMaxHealth.value = value;
    }
    public float MaxHealth => BaseMaxHealth + BonusMaxHealth;
    [HideInInspector] public SyncVar<float> _currentHealth = new(0);
    [HideInInspector] public float CurrentHealth
    {
        get => _currentHealth.value;
        set => _currentHealth.value = value;
    }

    [Header("Shield")]
    public float baseMaxShieldPercent = 0.1f;
    [NonSerialized] public float bonusMaxShieldPercent = 0f;
    public float MaxShieldPercent => baseMaxShieldPercent + bonusMaxShieldPercent;
    [NonSerialized] public float baseShieldRegenTime = 5f;
    [NonSerialized] public float shieldRegenTimeDivisor = 1f;
    public float ShieldRegenTime => baseShieldRegenTime / shieldRegenTimeDivisor;
    public float MaxShield => MaxHealth * MaxShieldPercent;
    [HideInInspector] public SyncVar<float> _currentShield = new(0);
    [HideInInspector] public float CurrentShield
    {
        get => _currentShield.value;
        set => _currentShield.value = value;
    }
    [NonSerialized] public float _shieldRegenTimer = 0f;

    [Header("Surge")]
    public float baseMaxEnergy = 50f;
    [NonSerialized] public float bonusMaxEnergy = 0f;
    public float MaxEnergy => baseMaxEnergy + bonusMaxEnergy;
    public float energyRegen = 10f;
    [NonSerialized] public float surgeActivationThreshhold = 20f;
    [NonSerialized] public float surgeUpKeepCost = 2f;
    [NonSerialized] public float surgeEffectiveness = 1f;
    [NonSerialized] public float surgeMoveSpeedMultiplier = 1f;
    [NonSerialized] public float surgeWeaponSpeedMultiplier = 3f;
    [NonSerialized] public float surgeAttackSpeedMultiplier = 3f;
    [HideInInspector] public SyncVar<float> _currentEnergy = new(0, ownerAuth: true);
    public float CurrentEnergy
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
            CurrentEnergy = MaxEnergy;

        if (!isServer) return;
        CurrentHealth = MaxHealth;
        CurrentShield = MaxShield;
    }

    private void Update()
    {
        if (!isServer) return;
        if (isDead) return;

        // Shield regen
        if (CurrentShield < MaxShield)
        {
            _shieldRegenTimer -= Time.deltaTime;
            if (_shieldRegenTimer <= 0f)
                CurrentShield = MaxShield;
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
        CurrentHealth = MaxHealth * healthPercent;
        CurrentShield = MaxShield * healthPercent;
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

        _shieldRegenTimer = ShieldRegenTime;

        if (CurrentShield > 0)
        {
            float shieldDamage = Mathf.Min(damage, CurrentShield);
            CurrentShield -= shieldDamage;
            damage -= shieldDamage;
        }

        if (damage > 0)
            CurrentHealth -= damage;

        if (CurrentHealth <= 0)
        {
            CurrentHealth = 0f;
            Die();
        }
    }

    private void Die()
    {
        isDead = true;
    }
}