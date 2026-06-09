using System;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class CharacterStats : NetworkBehaviour
{
    public static readonly List<CharacterStats> All = new();

    public enum CharacterClass {Knight, Rogue, Archer, Bladeweaver}
    public CharacterClass characterClass;

    [SerializeField] private PlayerController _controller;
    [SerializeField] private Collider _hurtbox;
    [SerializeField] private Collider _physicsCollider;

    [Header("Movement")]
    public float baseMoveSpeed = 1f;
    [NonSerialized] public float bonusMoveSpeedFlat = 0f;
    [NonSerialized] public float bonusMoveSpeedPercent = 1f;
    public float MoveSpeed => (baseMoveSpeed + bonusMoveSpeedFlat) * bonusMoveSpeedPercent;

    [Header("Weapon")]
    public float baseWeaponDamage = 10f;
    [NonSerialized] public float bonusWeaponDamageFlat = 0f;
    public float bonusWeaponDamagePercent = 1f;
    public float WeaponDamage => (baseWeaponDamage + bonusWeaponDamageFlat) * bonusWeaponDamagePercent;

    public float baseWeaponRadius = 0f;
    [NonSerialized] public float bonusWeaponRadiusFlat = 0f;
    [NonSerialized] public float bonusWeaponRadiusPercent = 1f;
    public float WeaponRadius => (baseWeaponRadius + bonusWeaponRadiusFlat) * bonusWeaponRadiusPercent;

    public float baseWeaponSpeed = 180f;
    [NonSerialized] public float bonusWeaponSpeedFlat = 0f;
    [NonSerialized] public float bonusWeaponSpeedPercent = 1f;
    public float WeaponSpeed => (baseWeaponSpeed + bonusWeaponSpeedFlat) * bonusWeaponSpeedPercent;

    public int baseWeaponCount = 1;
    [NonSerialized] public int bonusWeaponCountFlat = 0;
    [NonSerialized] public int bonusWeaponCountMultiplier = 1;
    public int WeaponCount => (baseWeaponCount + bonusWeaponCountFlat) * bonusWeaponCountMultiplier;

    public float baseWeaponRange = 1f;
    [NonSerialized] public float bonusWeaponRangeFlat = 0f;
    [NonSerialized] public float bonusWeaponRangePercent = 1f;
    public float WeaponRange => (baseWeaponRange + bonusWeaponRangeFlat) * bonusWeaponRangePercent;

    public float AttackSpeed => WeaponSpeed / 360f;

    public bool continuousFire = false;
    public float baseCritChance = 0.05f;
    [NonSerialized] public float bonusCritChanceFlat = 0f;
    [NonSerialized] public float bonusCritChancePercent = 1f;
    public float CritChance => (baseCritChance + bonusCritChanceFlat) * bonusCritChancePercent;
    public float baseCritDamage = 3f;
    [NonSerialized] public float bonusCritDamageFlat = 0f;
    [NonSerialized] public float bonusCritDamagePercent = 1f;
    public float CritDamage => (baseCritDamage + bonusCritDamageFlat) * bonusCritDamagePercent;

    [Header("Health")]
    public SyncVar<float> _baseMaxHealth = new(50f);
    [HideInInspector] public float BaseMaxHealth
    {
        get => _baseMaxHealth.value;
        set => _baseMaxHealth.value = value;
    }
    [HideInInspector] public SyncVar<float> _bonusMaxHealthFlat = new(0f);
    [HideInInspector] public float BonusMaxHealthFlat
    {
        get => _bonusMaxHealthFlat.value;
        set => _bonusMaxHealthFlat.value = value;
    }
    [HideInInspector] public SyncVar<float> _bonusMaxHealthPercent = new(1f);
    [HideInInspector] public float BonusMaxHealthPercent
    {
        get => _bonusMaxHealthPercent.value;
        set => _bonusMaxHealthPercent.value = value;
    }

    public float MaxHealth => (BaseMaxHealth + BonusMaxHealthFlat) * BonusMaxHealthPercent;
    [HideInInspector] public SyncVar<float> _currentHealth = new(0);
    [HideInInspector] public float CurrentHealth
    {
        get => _currentHealth.value;
        set => _currentHealth.value = value;
    }
    public float baseHealthRegen = 0.5f;
    [NonSerialized] public float bonusHealthRegenFlat = 0f;
    [NonSerialized] public float bonusHealthRegenPercent = 1f;
    public float HealthRegen => (baseHealthRegen + bonusHealthRegenFlat) * bonusHealthRegenPercent;
    [NonSerialized] public float _healthRegenTimer = 0f;
    [NonSerialized] public float _healthRegenInterval = 1f;

    /// <summary>
    /// Percentage of max health. Ex: 0.1f = max shield equal to 10% max health.
    /// </summary>
    [Header("Shield")]
    public float baseMaxShield = 0.1f;
    [NonSerialized] public float bonusMaxShieldFlat = 0f;
    [NonSerialized] public float bonusMaxShieldPercent = 1f;
    public float MaxShieldPercentOfMaxHealth => (baseMaxShield + bonusMaxShieldFlat) * bonusMaxShieldPercent;
    public float MaxShield => MaxHealth * MaxShieldPercentOfMaxHealth;
    [HideInInspector] public SyncVar<float> _currentShield = new(0);
    [HideInInspector] public float CurrentShield
    {
        get => _currentShield.value;
        set => _currentShield.value = value;
    }
    [NonSerialized] public float shieldRechargeTime = 5f;
    /// <summary>
    /// This is a percentage multiplier to baseShieldRechargeTime.
    /// </summary>
    [NonSerialized] public float shieldRechargeTimeMultiplier = 1f;
    public float ShieldRegenTime => shieldRechargeTime * shieldRechargeTimeMultiplier;
    /// <summary>
    /// This is for tracking the time until shield recharge. Do not alter this value. 
    /// For adjusting shield recharge time, use shieldRechargeTime.
    /// </summary>
    [NonSerialized] public float _shieldRechargeTimer = 0f;

    [Header("Surge + Energy")]
    public float baseMaxEnergy = 100f;
    [NonSerialized] public float bonusMaxEnergyFlat = 0f;
    [NonSerialized] public float bonusMaxEnergyPercent = 1f;
    public float MaxEnergy => (baseMaxEnergy + bonusMaxEnergyFlat) * bonusMaxEnergyPercent;
    [HideInInspector] public SyncVar<float> _currentEnergy = new(0, ownerAuth: true);
    public float CurrentEnergy
    {
        get => _currentEnergy.value;
        set => _currentEnergy.value = value;
    }
    public float baseEnergyRegen = 5f;
    [NonSerialized] public float bonusEnergyRegenFlat = 0f;
    [NonSerialized] public float bonusEnergyRegenPercent = 1f;
    public float EnergyRegen => (baseEnergyRegen + bonusEnergyRegenFlat) * bonusEnergyRegenPercent;
    /// <summary>
    /// Value limits the activation of surge to itself. To adjust the energy expended during surge, use surgeCost.
    /// </summary>
    public float SurgeActivationThreshhold => (baseMaxEnergy + bonusMaxEnergyFlat) * bonusMaxEnergyPercent * 0.2f;
    [NonSerialized] public float baseSurgeCost = 2f;
    [NonSerialized] public float bonusSurgeCostFlat = 0f;
    [NonSerialized] public float bonusSurgeCostPercent = 1f;
    public float SurgeCost => (baseSurgeCost + bonusSurgeCostFlat) * bonusSurgeCostPercent;
    [NonSerialized] public float baseSurgeEffectiveness = 1f;
    [NonSerialized] public float bonusSurgeEffectivenessFlat = 0f;
    [NonSerialized] public float bonusSurgeEffectivenessPercent = 1f;
    public float SurgeEffectiveness => (baseSurgeEffectiveness + bonusSurgeEffectivenessFlat) * bonusSurgeEffectivenessPercent;
    [NonSerialized] public float baseSurgeMoveSpeedMulti = 0.5f;
    [NonSerialized] public float bonusSurgeMoveSpeedMultiFlat = 0f;
    [NonSerialized] public float bonusSurgeMoveSpeedMultiPercent = 1f;
    public float SurgeMoveSpeedMulti => (baseSurgeMoveSpeedMulti + bonusSurgeMoveSpeedMultiFlat) * bonusSurgeMoveSpeedMultiPercent;
    [NonSerialized] public float baseSurgeWeaponSpeedMulti = 3f;
    [NonSerialized] public float bonusSurgeWeaponSpeedMultiFlat = 0f;
    [NonSerialized] public float bonusSurgeWeaponSpeedMultiPercent = 1f;
    public float SurgeWeaponSpeedMultiplier => (baseSurgeWeaponSpeedMulti + bonusSurgeWeaponSpeedMultiFlat) * bonusSurgeWeaponSpeedMultiPercent;
    [NonSerialized] public float baseSurgeAttackSpeedMulti = 3f;
    [NonSerialized] public float bonusSurgeAttackSpeedMultiFlat = 0f;
    [NonSerialized] public float bonusSurgeAttackSpeedMultiPercent = 1f;
    public float SurgeAttackSpeedMultiplier => (baseSurgeAttackSpeedMulti + bonusSurgeAttackSpeedMultiFlat) * bonusSurgeAttackSpeedMultiPercent;

    [Header("Dash")]
    public int dashCharges = 3;
    public float baseDashCooldown = 5f;
    public float dashHaste = 0f;
    public float DashCooldown => baseDashCooldown / (1f + dashHaste / 100f);

    [Header("Misc")]
    private float _baseXPMod = 1f;
    public float BaseXPMod
    {
        get => _baseXPMod;
        set { _baseXPMod = value; RecalculateXPMod(); }
    }

    private float _bonusXPModFlat = 0f;
    public float BonusXPModFlat
    {
        get => _bonusXPModFlat;
        set { _bonusXPModFlat = value; RecalculateXPMod(); }
    }

    private float _bonusXPModPercent = 1f;
    public float BonusXPModPercent
    {
        get => _bonusXPModPercent;
        set { _bonusXPModPercent = value; RecalculateXPMod(); }
    }
    public float XPModifier => (BaseXPMod + BonusXPModFlat) * BonusXPModPercent;

    [NonSerialized] public float baseXPRadius = 5f;
    [NonSerialized] public float bonusXPRadiusFlat = 0f;
    [NonSerialized] public float bonusXPRadiusPercent = 1f;
    public float XPRadius => (baseXPRadius + bonusXPRadiusFlat) * bonusXPRadiusPercent;

    [NonSerialized] public float reviveTime = 3f;
    [NonSerialized] public float revivePower = 1f;

    [HideInInspector] public SyncVar<bool> _isDeadSync = new(false);
    public bool IsDead
    {
        get => _isDeadSync.value;
        set => _isDeadSync.value = value;
    }

    public bool invincible = false;

    protected override void OnDestroy() { All.Remove(this); base.OnDestroy(); }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        All.Add(this);

        _isDeadSync.onChanged += OnDeathStateChanged;

        if (isOwner && LocalUIManager.instance != null)
            LocalUIManager.instance.SetPlayer(this);

        if (isOwner)
            CurrentEnergy = MaxEnergy;

        if (!isServer) return;
        CurrentHealth = MaxHealth;
        CurrentShield = MaxShield;
        RecalculateXPMod();
    }

    private void Update()
    {
        if (!isServer) return;
        if (IsDead) return;

        // Health regen | RegenTimer and HealthRegen x3 is what we need
        if (CurrentHealth < MaxHealth)
        {
            _healthRegenTimer -= Time.deltaTime;
            if (_healthRegenTimer <= 0f)
            {
                Heal(HealthRegen);
                _healthRegenTimer = _healthRegenInterval;
            }
        }


        // Shield regen
        if (CurrentShield < MaxShield)
        {
            _shieldRechargeTimer -= Time.deltaTime;
            if (_shieldRechargeTimer <= 0f)
                CurrentShield = MaxShield;
        }
    }

    public void Heal(float healAmount)
    {
        if (!isServer) return;

        CurrentHealth += HealthRegen;
        CurrentHealth = Mathf.Min(CurrentHealth, MaxHealth);
    }

    private void OnDeathStateChanged(bool dead)
    {
        _controller.enabled = !dead;
        _physicsCollider.enabled = !dead;
        _hurtbox.enabled = !dead;
        if (dead)
            GameManager.instance.CheckAllPlayersDead();
    }

    public void Revive(float healthPercent = 0.5f)
    {
        if (!isServer) return;
        IsDead = false;
        CurrentHealth = MaxHealth * healthPercent;
        CurrentShield = MaxShield;
    }

    private void RecalculateXPMod()
    {
        GameManager.instance.UpdateXPModRpc(owner.Value, XPModifier);
    }

    [ServerRpc(requireOwnership: false)]
    public void TakeDamageRpc(float damage)
    {
        TakeDamage(damage);
    }

    private void TakeDamage(float damage)
    {
        if (!isServer) return;
        if (IsDead) return;
        if (invincible) return;

        _shieldRechargeTimer = ShieldRegenTime;

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
        IsDead = true;
    }
}