using System;
using System.Collections;
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

    [Header("Audio")]
    public AudioClip hitSound;
    [SerializeField] private AudioClip damagedSound;

    [Header("Visual")]
    [SerializeField] private Renderer[] _renderers;
    private Color[] _originalColors;

    [Header("Movement")]
    public float baseMoveSpeed = 1f;
    public float bonusMoveSpeedFlat = 0f;
    public float bonusMoveSpeedPercent = 1f;
    public float moveSpeedPenalty = 1f;
    public float MoveSpeed => (baseMoveSpeed + bonusMoveSpeedFlat) * bonusMoveSpeedPercent * moveSpeedPenalty;

    [Header("Weapon")]
    public float baseWeaponDamage = 15f;
    public float bonusWeaponDamageFlat = 0f;
    public float bonusWeaponDamagePercent = 1f;
    public float bonusWeaponDamagePenalty = 1f;
    public float WeaponDamage => (baseWeaponDamage + bonusWeaponDamageFlat) * bonusWeaponDamagePercent * bonusWeaponDamagePenalty;
    public float UnpenalizedWeaponDamage => (baseWeaponDamage + bonusWeaponDamageFlat) * bonusWeaponDamagePercent;

    public float baseWeaponRadius = 0f;
    [NonSerialized] public float bonusWeaponRadiusFlat = 0f;
    [NonSerialized] public float bonusWeaponRadiusPercent = 1f;
    public float WeaponRadius => (baseWeaponRadius + bonusWeaponRadiusFlat) * bonusWeaponRadiusPercent;

    public float baseWeaponSpeed = 180f;
    public float bonusWeaponSpeedFlat = 0f;
    public float bonusWeaponSpeedPercent = 1f;
    public float weaponSpeedPenalty = 1f;
    public float WeaponSpeed => (baseWeaponSpeed + bonusWeaponSpeedFlat) * bonusWeaponSpeedPercent * weaponSpeedPenalty;

    public float AttackSpeed => WeaponSpeed / 360f;
    public float AttackSpeedPercent => bonusWeaponSpeedPercent * weaponSpeedPenalty;

    public int baseWeaponCount = 1;
    public int bonusWeaponCountFlat = 0;
    public int bonusWeaponCountMultiplier = 1;
    public int WeaponCount => (baseWeaponCount + bonusWeaponCountFlat) * bonusWeaponCountMultiplier;

    public float baseWeaponRange = 5f;
    public float bonusWeaponRangeFlat = 0f;
    public float bonusWeaponRangePercent = 1f;
    public float weaponRangePenalty = 1f;
    public float WeaponRange => (baseWeaponRange + bonusWeaponRangeFlat) * bonusWeaponRangePercent * weaponRangePenalty * 0.2f;

    public float baseWeaponSize = 1f;
    public float bonusWeaponSizePercent = 1f;
    public float WeaponSize => baseWeaponSize * bonusWeaponSizePercent;

    public bool continuousFire = false;
    [NonSerialized] public bool canContactAllies = false;
    [NonSerialized] public bool canDamageAllies  = false;
    [NonSerialized] public float baseCritChance = 0f;
    public float bonusCritChanceFlat = 0f;
    public float bonusCritChancePercent = 1f;
    public float CritChance => (baseCritChance + bonusCritChanceFlat) * bonusCritChancePercent;
    [NonSerialized] public float baseCritDamage = 2f;
    public float bonusCritDamageFlat = 0f;
    public float bonusCritDamagePercent = 1f;
    public float CritDamage => ((baseCritDamage + bonusCritDamageFlat) * bonusCritDamagePercent) + Mathf.Max(0f, CritChance - 1f) * 0.5f;

    [Header("Ranged Weapons")]
    public float basePierce = 0f;
    public float bonusPierceFlat = 0f;
    public float bonusPiercePercent = 1f;
    public float Pierce => (basePierce + bonusPierceFlat) * bonusPiercePercent;
    public float baseRicochet = 0f;
    public float bonusRicochetFlat = 0f;
    public float bonusRicochetPercent = 1f;
    public float Ricochet => (baseRicochet + bonusRicochetFlat) * bonusRicochetPercent;

    public float baseMultishot = 0f;
    public float bonusMultishotFlat = 0f;
    public float bonusMultishotPercent = 1f;
    public float Multishot => (baseMultishot + bonusMultishotFlat) * bonusMultishotPercent;
    public float multishotSpreadAngle = 75f;

    public float baseProjectileDamageDropoff = 0.5f;
    public float projectileDamageDropoffReduction = 1f;
    public float ProjectileDamageDropoff => baseProjectileDamageDropoff * projectileDamageDropoffReduction;

    public float baseProjectileOnHitDropoff = 0.5f;
    public float projectileOnHitDropoffReduction = 1f;
    public float ProjectileOnHitDropoff => baseProjectileOnHitDropoff * projectileOnHitDropoffReduction;

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
    [HideInInspector] public SyncVar<float> _bonusHealthRegenFlat = new(0f);
    public float BonusHealthRegenFlat
    {
        get => _bonusHealthRegenFlat.value;
        set => _bonusHealthRegenFlat.value = value;
    }
    [HideInInspector] public SyncVar<float> _bonusHealthRegenPercent = new(1f);
    public float BonusHealthRegenPercent
    {
        get => _bonusHealthRegenPercent.value;
        set => _bonusHealthRegenPercent.value = value;
    }
    public float HealthRegen => (baseHealthRegen + BonusHealthRegenFlat) * BonusHealthRegenPercent;
    [NonSerialized] public float _healthRegenTimer = 0f;
    [NonSerialized] public float _healthRegenInterval = 1f;

    /// <summary>
    /// Percentage of max health. Ex: 0.1f = max shield equal to 10% max health.
    /// </summary>
    [Header("Shield")]
    public float baseMaxShield = 0.1f;
    [HideInInspector] public SyncVar<float> _bonusMaxShieldFlat = new(0f);
    public float BonusMaxShieldFlat
    {
        get => _bonusMaxShieldFlat.value;
        set => _bonusMaxShieldFlat.value = value;
    }
    [HideInInspector] public SyncVar<float> _bonusMaxShieldPercent = new(1f);
    public float BonusMaxShieldPercent
    {
        get => _bonusMaxShieldPercent.value;
        set => _bonusMaxShieldPercent.value = value;
    }
    public float MaxShieldPercentOfMaxHealth => (baseMaxShield + BonusMaxShieldFlat) * BonusMaxShieldPercent;
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
    [NonSerialized] public float energyRegenPenalty = 1f;
    public float EnergyRegen => (baseEnergyRegen + bonusEnergyRegenFlat) * bonusEnergyRegenPercent * energyRegenPenalty;
    /// <summary>
    /// Value limits the activation of surge to itself. To adjust the energy expended during surge, use surgeCost.
    /// </summary>
    public float SurgeActivationThreshhold => (baseMaxEnergy + bonusMaxEnergyFlat) * bonusMaxEnergyPercent * 0.2f;
    [NonSerialized] public float baseSurgeCost = 2f;
    [NonSerialized] public float bonusSurgeCostFlat = 0f;
    [NonSerialized] public float bonusSurgeCostPercent = 1f;
    public float SurgeCost => (baseSurgeCost + bonusSurgeCostFlat) * bonusSurgeCostPercent;
    [NonSerialized] public float surgeEffectiveness = 1f;
    [NonSerialized] public float surgeEffectivenessPenalty = 1f;
    public float SurgeEffectiveness => surgeEffectiveness * surgeEffectivenessPenalty;
    [NonSerialized] public float surgeMoveSpeedMulti = 1f;
    [NonSerialized] public float surgeMoveSpeedPenalty = 0.5f;
    public float SurgeMoveSpeedMulti => surgeMoveSpeedMulti * surgeMoveSpeedPenalty * SurgeEffectiveness;
    [NonSerialized] public float surgeWeaponSpeedMulti = 3f;
    [NonSerialized] public float surgeWeaponSpeedPenalty = 1f;
    public float SurgeWeaponSpeedMultiplier => surgeWeaponSpeedMulti * surgeWeaponSpeedPenalty * SurgeEffectiveness;

    [Header("Dash")]
    public int dashCharges = 3;
    public float baseDashCooldown = 5f;
    public float dashHaste = 0f;
    public float DashCooldown => baseDashCooldown / (1f + dashHaste / 100f);

    [Header("Burst + SP")]
    public float baseMaxSP = 100f;
    [NonSerialized] public float bonusMaxSPFlat = 0f;
    [NonSerialized] public float bonusMaxSPPercent = 1f;
    public float MaxSP => (baseMaxSP + bonusMaxSPFlat) * bonusMaxSPPercent;
    [HideInInspector] public SyncVar<float> _currentSP = new(0, ownerAuth: true);
    public float CurrentSP
    {
        get => _currentSP.value;
        set => _currentSP.value = value;
    }

    [Header("Misc")]
    public float baseOnHitEffectiveness = 1f;
    public float bonusOnHitEffectivenessMultiplier = 1f;
    public float bonusOnHitEffectivenessPenalty = 1f;
    public float OnHitEffectiveness => baseOnHitEffectiveness * bonusOnHitEffectivenessMultiplier * bonusOnHitEffectivenessPenalty;
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

    [HideInInspector] public SyncVar<float> _reviveTimeSync = new(1f);
    public float reviveTime
    {
        get => _reviveTimeSync.value;
        set => _reviveTimeSync.value = value;
    }
    [NonSerialized] public float revivePower = 1f;

    private bool _ownerInitialized = false;

    [HideInInspector] public SyncVar<bool> _isDeadSync = new(false);
    public bool IsDead
    {
        get => _isDeadSync.value;
        set => _isDeadSync.value = value;
    }

    public bool invincible = false;
    [HideInInspector] public SyncVar<bool> _reviveInvincible = new(false);
    public bool IsInvincible => invincible || _reviveInvincible.value;

    protected override void OnDestroy() { All.Remove(this); base.OnDestroy(); }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        All.Add(this);
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
            _originalColors[i] = _renderers[i].material.color;

        _isDeadSync.onChanged += OnDeathStateChanged;

        if (isOwner)
        {
            _ownerInitialized = true;
            if (LocalUIManager.instance != null)
                LocalUIManager.instance.SetPlayer(this);
            ApplyBaseStats();
            CurrentEnergy = MaxEnergy;
            CurrentSP = MaxSP;
        }

        if (!isServer) return;
        CurrentHealth = MaxHealth;
        CurrentShield = MaxShield;
        RecalculateXPMod();
    }

    private void LateOwnerInit()
    {
        if (LocalUIManager.instance != null)
            LocalUIManager.instance.SetPlayer(this);
        ApplyBaseStats();
        CurrentEnergy = MaxEnergy;
        CurrentSP = MaxSP;
        _controller.InitializeForOwner();
        GetComponent<WeaponManager>().InitializeForOwner();
    }

    private void ApplyBaseStats()
    {
        bonusWeaponDamageFlat += ProgressionManager.GetBaseStatBonus(StatType.WeaponDamage);
        bonusMoveSpeedFlat    += ProgressionManager.GetBaseStatBonus(StatType.MoveSpeed) * 0.1f;
        bonusWeaponSpeedFlat  += ProgressionManager.GetBaseStatBonus(StatType.AttackSpeed) * 0.01f * baseWeaponSpeed;
        bonusEnergyRegenFlat  += ProgressionManager.GetBaseStatBonus(StatType.EnergyRegen);
        dashHaste             += ProgressionManager.GetBaseStatBonus(StatType.DashCooldown);
        bonusCritChanceFlat   += ProgressionManager.GetBaseStatBonus(StatType.CritChance) * 0.01f;
        bonusXPRadiusFlat     += ProgressionManager.GetBaseStatBonus(StatType.CollectionRadius);

        bool isRanged = GetComponent<WeaponManager>().IsRanged;
        if (isRanged)
            bonusPierceFlat      += ProgressionManager.GetBaseStatBonus(StatType.WeaponRange);
        else
            bonusWeaponRangeFlat += ProgressionManager.GetBaseStatBonus(StatType.WeaponRange);

        ApplyBaseStatsServerRpc(
            ProgressionManager.GetBaseStatBonus(StatType.MaxHealth),
            ProgressionManager.GetBaseStatBonus(StatType.HealthRegen)
        );
    }

    [ServerRpc(requireOwnership: false)]
    private void ApplyBaseStatsServerRpc(float maxHealthBonus, float healthRegenBonus)
    {
        BonusMaxHealthFlat   += maxHealthBonus;
        BonusHealthRegenFlat += healthRegenBonus;
        CurrentHealth = MaxHealth;
    }

    private void Update()
    {
        if (!_ownerInitialized && isOwner)
        {
            _ownerInitialized = true;
            LateOwnerInit();
        }

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
        float actual = Mathf.Min(healAmount, MaxHealth - CurrentHealth);
        CurrentHealth += actual;
        if (actual > 0f) NotifyHealRpc(actual);
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
        _reviveInvincible.value = true;
        StartCoroutine(ReviveInvincibilityCoroutine());
    }

    private static readonly WaitForSeconds ReviveInvincibilityWait = new(2f);

    private IEnumerator ReviveInvincibilityCoroutine()
    {
        yield return ReviveInvincibilityWait;
        _reviveInvincible.value = false;
    }

    private void RecalculateXPMod()
    {
        GameManager.instance.UpdateXPModRpc(owner.Value, XPModifier);
    }

    [ObserversRpc]
    private void NotifyHealRpc(float amount) => OnHeal?.Invoke(this, amount);

    [ServerRpc(requireOwnership: true)]
    public void HealRpc(float amount)
    {
        float actual = Mathf.Min(amount, MaxHealth - CurrentHealth);
        CurrentHealth += actual;
        if (actual > 0f) NotifyHealRpc(actual);
    }

    [ServerRpc(requireOwnership: true)]
    public void HealSilentRpc(float amount)
    {
        CurrentHealth = Mathf.Min(CurrentHealth + amount, MaxHealth);
    }

    [ServerRpc(requireOwnership: false)]
    public void ReceiveHealRpc(float amount)
    {
        float actual = Mathf.Min(amount, MaxHealth - CurrentHealth);
        CurrentHealth += actual;
        if (actual > 0f) NotifyHealRpc(actual);
    }

    [ServerRpc(requireOwnership: true)]
    public void AddMaxHealthPercentRpc(float bonus)
    {
        float prevMax = MaxHealth;
        BonusMaxHealthPercent += bonus;
        CurrentHealth = Mathf.Min(CurrentHealth + (MaxHealth - prevMax), MaxHealth);
    }

    [ServerRpc(requireOwnership: true)]
    public void AddMaxHealthFlatRpc(float bonus)
    {
        BonusMaxHealthFlat += bonus;
        CurrentHealth = Mathf.Min(CurrentHealth + bonus, MaxHealth);
    }

    public static event Action<CharacterStats, float> OnHeal;

    public static event Action<CharacterStats, Vector3, float, float, string> OnVFXBroadcast;

    [ServerRpc(requireOwnership: true)]
    public void BroadcastVFXRpc(Vector3 position, float size, float duration, string vfxTag)
        => ShowVFXRpc(position, size, duration, vfxTag);

    [ObserversRpc]
    private void ShowVFXRpc(Vector3 position, float size, float duration, string vfxTag)
        => OnVFXBroadcast?.Invoke(this, position, size, duration, vfxTag);

    [ServerRpc(requireOwnership: false)]
    public void TakeDamageRpc(float damage)
    {
        TakeDamage(damage);
    }

    [ObserversRpc]
    private void FlashHitRpc()
    {
        if (isOwner)
            SFXManager.instance?.Play(damagedSound, transform.position);
        StartCoroutine(FlashCoroutine());
    }

    private IEnumerator FlashCoroutine()
    {
        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].material.color = Color.red;
        yield return new WaitForSeconds(0.08f);
        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].material.color = _originalColors[i];
    }

    private void TakeDamage(float damage)
    {
        if (!isServer) return;
        if (IsDead) return;
        if (IsInvincible) return;

        _shieldRechargeTimer = ShieldRegenTime;
        FlashHitRpc();

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