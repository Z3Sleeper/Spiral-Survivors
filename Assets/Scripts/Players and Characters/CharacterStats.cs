using System;
using System.Collections;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class CharacterStats : NetworkBehaviour
{
    public static readonly Dictionary<PlayerID, CharacterStats> All = new();
    public static CharacterStats Local
    {
        get
        {
            var lp = NetworkManager.main?.localPlayer;
            return lp.HasValue && All.TryGetValue(lp.Value, out var s) ? s : null;
        }
    }

    public enum CharacterClass {Knight, Rogue, Archer, Bladeweaver, Executioner}
    public CharacterClass characterClass;

    public bool outerXScalesWithRange = false;

    [SerializeField] private PlayerController _controller;
    [SerializeField] private Collider _hurtbox;
    [SerializeField] private Collider _physicsCollider;

    [Header("Audio")]
    public AudioClip hitSound;
    [SerializeField] private AudioClip damagedSound;

    [Header("Visual")]
    [SerializeField] private Renderer[] _renderers;
    private Color[] _originalColors;
    [SerializeField] private GameObject _invincibilityShell;

    [Header("Movement")]
    public float baseMoveSpeed = 1f;
    public float bonusMoveSpeedFlat = 0f;
    public float bonusMoveSpeedPercent = 1f;
    public float moveSpeedPenalty = 1f;
    public float MoveSpeed => (baseMoveSpeed + bonusMoveSpeedFlat) * bonusMoveSpeedPercent * moveSpeedPenalty * (isSurging ? SurgeMoveSpeedMulti : 1f);

    [Header("Weapon")]
    public float damagePerLevel = 2.5f;
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

    public float baseAttackSpeed = 0.5f;
    public float attackSpeedPercent = 0f;
    public float attackSpeedTotalPercent = 1f;
    public float attackSpeedPenalty = 1f;
    public float AttackSpeed => (baseAttackSpeed + attackSpeedPercent) * attackSpeedTotalPercent * attackSpeedPenalty * (isSurging ? SurgeAttackSpeedMultiplier : 1f);
    public float WeaponSpeed => AttackSpeed * 360f;
    public float AttackSpeedPercent => attackSpeedTotalPercent * attackSpeedPenalty;

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
    public SyncVar<Color> _playerColorSync = new SyncVar<Color>(Color.white);
    public Color playerColor
    {
        get => _playerColorSync.value;
        set => _playerColorSync.value = value;
    }
    [NonSerialized] public float baseCritChance = 0f;
    public float bonusCritChanceFlat = 0f;
    public float bonusCritChancePercent = 1f;
    public float CritChance => (baseCritChance + bonusCritChanceFlat) * bonusCritChancePercent;
    [NonSerialized] public float baseCritDamage = 2f;
    public float bonusCritDamageFlat = 0f;
    public float bonusCritDamagePercent = 1f;
    public float CritDamage => ((baseCritDamage + bonusCritDamageFlat) * bonusCritDamagePercent) + Mathf.Max(0f, CritChance - 1f) * 0.5f;

    [Header("Ranged Weapons")]
    public float baseRicochet = 0f;
    public float bonusRicochetFlat = 0f;
    public float bonusRicochetPercent = 1f;
    public float Ricochet => (baseRicochet + bonusRicochetFlat) * bonusRicochetPercent;

    public float baseMultishot = 0f;
    public float bonusMultishotFlat = 0f;
    public float bonusMultishotPercent = 1f;
    public float Multishot => (baseMultishot + bonusMultishotFlat) * bonusMultishotPercent;
    public float multishotSpreadAngle = 75f;

    [NonSerialized] public float baseProjectileDamageDropoff = 0.1f;
    [NonSerialized] public float projectileDamageDropoffReduction = 1f;
    public float ProjectileDamageDropoff => baseProjectileDamageDropoff * projectileDamageDropoffReduction;

    [NonSerialized] public float baseProjectileOnHitDropoff = 0.1f;
    [NonSerialized] public float projectileOnHitDropoffReduction = 1f;
    public float ProjectileOnHitDropoff => baseProjectileOnHitDropoff * projectileOnHitDropoffReduction;

    [Header("Health")]
    public SyncVar<float> _baseMaxHealth = new(20f);
    [HideInInspector] public float BaseMaxHealth
    {
        get => _baseMaxHealth.value;
        set => _baseMaxHealth.value = value;
    }
    public SyncVar<float> _bonusMaxHealthFlat = new(0f, ownerAuth: true);
    [HideInInspector] public float BonusMaxHealthFlat
    {
        get => _bonusMaxHealthFlat.value;
        set => _bonusMaxHealthFlat.value = value;
    }
    public SyncVar<float> _bonusMaxHealthPercent = new(1f, ownerAuth: true);
    [HideInInspector] public float BonusMaxHealthPercent
    {
        get => _bonusMaxHealthPercent.value;
        set => _bonusMaxHealthPercent.value = value;
    }

    public float MaxHealth => (BaseMaxHealth + BonusMaxHealthFlat) * BonusMaxHealthPercent;
    public SyncVar<float> _currentHealth = new(0, ownerAuth: true);
    [HideInInspector] public float CurrentHealth
    {
        get => _currentHealth.value;
        set => _currentHealth.value = value;
    }
    public float baseHealthRegen = 0.5f;
    public SyncVar<float> _bonusHealthRegenFlat = new(0f, ownerAuth: true);
    public float BonusHealthRegenFlat
    {
        get => _bonusHealthRegenFlat.value;
        set => _bonusHealthRegenFlat.value = value;
    }
    public SyncVar<float> _bonusHealthRegenPercent = new(1f, ownerAuth: true);
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
    public SyncVar<float> _bonusMaxShieldFlat = new(0f, ownerAuth: true);
    public float BonusMaxShieldFlat
    {
        get => _bonusMaxShieldFlat.value;
        set => _bonusMaxShieldFlat.value = value;
    }
    public SyncVar<float> _bonusMaxShieldPercent = new(1f, ownerAuth: true);
    public float BonusMaxShieldPercent
    {
        get => _bonusMaxShieldPercent.value;
        set => _bonusMaxShieldPercent.value = value;
    }
    public float MaxShieldPercentOfMaxHealth => (baseMaxShield + BonusMaxShieldFlat) * BonusMaxShieldPercent;
    public float MaxShield => MaxHealth * MaxShieldPercentOfMaxHealth;
    public SyncVar<float> _currentShield = new(0, ownerAuth: true);
    [HideInInspector] public float CurrentShield
    {
        get => _currentShield.value;
        set => _currentShield.value = value;
    }
    [NonSerialized] public float shieldRechargeTime = 5f;
    /// <summary>
    /// This is a percentage multiplier to baseShieldRechargeTime.
    /// </summary>
    [NonSerialized] public float shieldRechargeTimeReduction = 1f;
    public float ShieldRegenTime => shieldRechargeTime * shieldRechargeTimeReduction;
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
    public SyncVar<float> _currentEnergy = new(0, ownerAuth: true);
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
    public float SurgeActivationThreshhold => SurgeCost * 5f;
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
    [NonSerialized] public bool isSurging = false;
    [NonSerialized] public float surgeAttackSpeedMulti = 2f;
    [NonSerialized] public float surgeAttackSpeedPenalty = 1f;
    public float SurgeAttackSpeedMultiplier => surgeAttackSpeedMulti * surgeAttackSpeedPenalty * SurgeEffectiveness;

    [Header("Dash")]
    public int dashCharges = 3;
    [NonSerialized] public int levelUpOptions = 3;
    public float baseDashCooldown = 5f;
    public float dashHaste = 0f;
    public float DashCooldown => baseDashCooldown / (1f + dashHaste / 100f);

    [Header("Burst + SP")]
    public float baseMaxSP = 100f;
    [NonSerialized] public float bonusMaxSPFlat = 0f;
    [NonSerialized] public float bonusMaxSPPercent = 1f;
    public float MaxSP => (baseMaxSP + bonusMaxSPFlat) * bonusMaxSPPercent;
    public SyncVar<float> _currentSP = new(0, ownerAuth: true);
    public float CurrentSP
    {
        get => _currentSP.value;
        set => _currentSP.value = value;
    }

    [Header("Luck")]
    public float baseLuck = 0f;
    [NonSerialized] public float bonusLuckFlat = 0f;
    [NonSerialized] public float bonusLuckPercent = 1f;
    [NonSerialized] public float luckPenalty = 1f;
    public float Luck => (baseLuck + bonusLuckFlat) * bonusLuckPercent * luckPenalty;
    public float LuckChance => Luck / (Luck + 100f);

    [Header("Knockback")]
    public float baseKnockback = 10f;
    [NonSerialized] public float knockbackMultiplier = 1f;
    [NonSerialized] public float knockbackPenalty = 1f;
    public float Knockback => baseKnockback * knockbackMultiplier * knockbackPenalty;

    [Header("Misc")]
    public float baseOnHitPower = 1f;
    public float bonusOnHitPowerMultiplier = 1f;
    public float bonusOnHitPowerPenalty = 1f;
    public float OnHitPower => baseOnHitPower * bonusOnHitPowerMultiplier * bonusOnHitPowerPenalty;
    [NonSerialized] public float stackPowerPercent = 1f;
    [NonSerialized] public float stackPowerPenalty = 1f;
    public float StackPower => stackPowerPercent * stackPowerPenalty;
    [NonSerialized] public float xpMultiplier = 1f;
    [NonSerialized] public float xpPenalty    = 1f;
    public float XPModifier => xpMultiplier * xpPenalty;

    [NonSerialized] public float baseXPRadius = 5f;
    [NonSerialized] public float bonusXPRadiusFlat = 0f;
    [NonSerialized] public float bonusXPRadiusPercent = 1f;
    public float XPRadius => (baseXPRadius + bonusXPRadiusFlat) * bonusXPRadiusPercent;

    public SyncVar<float> _reviveTimeSync = new(3f);
    public float reviveTime
    {
        get => _reviveTimeSync.value;
        set => _reviveTimeSync.value = value;
    }
    [NonSerialized] public float reviveTimePenalty = 1f;
    [NonSerialized] public float reviveTimePenaltyReduction = 1f;
    public float ReviveTimePenalty => reviveTimePenalty * reviveTimePenaltyReduction;
    [NonSerialized] public float revivePower = 1f;


    public SyncVar<bool> _isDeadSync = new(false, ownerAuth: true);
    public bool IsDead
    {
        get => _isDeadSync.value;
        set => _isDeadSync.value = value;
    }

    [NonSerialized] public bool invincible = false;
    private Coroutine _invincibleCoroutine;
    public bool IsInvincible => invincible;

    public void SetInvincible(float duration)
    {
        if (_invincibleCoroutine != null) StopCoroutine(_invincibleCoroutine);
        if (duration <= 0f) { invincible = false; _invincibleCoroutine = null; return; }
        _invincibleCoroutine = StartCoroutine(InvincibleCoroutine(duration));
    }

    private IEnumerator InvincibleCoroutine(float duration)
    {
        invincible = true;
        yield return new WaitForSeconds(duration);
        invincible = false;
        _invincibleCoroutine = null;
    }

    [NonSerialized] public float damageReductionPercent = 1f;
    [NonSerialized] public float damageReductionPenalty = 1f;
    public float DamageReduction => damageReductionPercent * damageReductionPenalty;

    private void ApplyPlayerColor(Color color)
    {
        foreach (var applicator in GetComponentsInChildren<PlayerColorApplicator>(true))
            applicator.Apply(color);
        GetComponent<WeaponManager>()?.ReapplyColor(color);
        if (_originalColors != null)
            for (int i = 0; i < _renderers.Length; i++)
                _originalColors[i] = _renderers[i].material.color;
    }

    protected override void OnDestroy()
    {
        GameManager.OnLevelUp -= OnLevelUp;
        if (owner.HasValue) All.Remove(owner.Value);
        base.OnDestroy();
    }

    private void OnLevelUp() => bonusWeaponDamageFlat += damagePerLevel;

    private bool _lateOwnerInitDone;
    private bool _onSpawnedFired;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _onSpawnedFired = true;
        if (owner.HasValue)
            All[owner.Value] = this;

        _isDeadSync.onChanged += OnDeathStateChanged;
        _playerColorSync.onChanged += ApplyPlayerColor;
        ApplyPlayerColor(_playerColorSync.value);

        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
            _originalColors[i] = _renderers[i].material.color;

        if (isOwner)
            LateOwnerInit();

        if (!isServer) return;
        RecalculateXPMod();
    }

    protected override void OnOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner, bool asServer)
    {
        base.OnOwnerChanged(oldOwner, newOwner, asServer);
        Debug.Log($"[CharacterStats.OnOwnerChanged] asServer={asServer}, isOwner={isOwner}, newOwner={newOwner}");
        if (newOwner.HasValue)
            All[newOwner.Value] = this;
        if (!asServer && isOwner)
            StartCoroutine(LateOwnerInitDeferred());
    }

    private IEnumerator LateOwnerInitDeferred()
    {
        yield return new WaitUntil(() => _onSpawnedFired);
        if (isOwner)
            LateOwnerInit();
    }

    private void LateOwnerInit()
    {
        if (_lateOwnerInitDone) return;
        _lateOwnerInitDone = true;
        Debug.Log($"[LateOwnerInit] isSpawned={isSpawned} isOwner={isOwner}");
        GameManager.OnLevelUp += OnLevelUp;
        StartCoroutine(ApplyColorNextFrame());
        if (LocalUIManager.instance != null)
            LocalUIManager.instance.SetPlayer(this);
        ApplyBaseStats();
        CurrentHealth = MaxHealth;
        CurrentShield = MaxShield;
        CurrentEnergy = MaxEnergy;
        CurrentSP = MaxSP;
        _controller.InitializeForOwner();
        GetComponent<WeaponManager>().InitializeForOwner();
    }

    private IEnumerator ApplyColorNextFrame()
    {
        yield return null;
        playerColor = GameStartData.LocalColor;
    }

    private void ApplyBaseStats()
    {
        bonusWeaponDamageFlat += ProgressionManager.GetBaseStatBonus(StatType.WeaponDamage);
        bonusMoveSpeedFlat    += ProgressionManager.GetBaseStatBonus(StatType.MoveSpeed) * 0.1f;
        attackSpeedPercent    += ProgressionManager.GetBaseStatBonus(StatType.AttackSpeed) * 0.01f * baseAttackSpeed;
        bonusEnergyRegenFlat  += ProgressionManager.GetBaseStatBonus(StatType.EnergyRegen);
        dashHaste             += ProgressionManager.GetBaseStatBonus(StatType.DashHaste);
        bonusCritChanceFlat   += ProgressionManager.GetBaseStatBonus(StatType.CritChance) * 0.01f;
        bonusXPRadiusFlat     += ProgressionManager.GetBaseStatBonus(StatType.CollectionRadius);

        bonusWeaponRangeFlat  += ProgressionManager.GetBaseStatBonus(StatType.WeaponRange);
        bonusMaxEnergyFlat    += ProgressionManager.GetBaseStatBonus(StatType.MaxEnergy);
        dashCharges           += (int)ProgressionManager.GetBaseStatBonus(StatType.DashCharges);
        levelUpOptions        += (int)ProgressionManager.GetBaseStatBonus(StatType.LevelUpOptions);
        revivePower           += ProgressionManager.GetBaseStatBonus(StatType.RevivePower);
        reviveTimePenaltyReduction *= Mathf.Pow(0.75f, ProgressionManager.GetBaseStatTier(StatType.ReviveTimePenalty));
        xpMultiplier          += ProgressionManager.GetBaseStatBonus(StatType.XPMultiplier) * 0.01f;

        BonusMaxHealthFlat   += ProgressionManager.GetBaseStatBonus(StatType.MaxHealth);
        BonusHealthRegenFlat += ProgressionManager.GetBaseStatBonus(StatType.HealthRegen);
        bonusWeaponDamagePercent += ProgressionManager.GetBaseStatBonus(StatType.WeaponDamagePercent);
        bonusLuckFlat            += ProgressionManager.GetBaseStatBonus(StatType.Luck);
        damagePerLevel           += ProgressionManager.GetBaseStatBonus(StatType.DamagePerLevel);
        BonusMaxShieldFlat       += ProgressionManager.GetBaseStatBonus(StatType.MaxShield);
        shieldRechargeTime       += ProgressionManager.GetBaseStatBonus(StatType.ShieldRechargeTime);
    }

    private void Update()
    {
        if (_invincibilityShell != null)
            _invincibilityShell.SetActive(IsInvincible);

        if (!isOwner) return;
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
        if (!isOwner) return;
        float actual = Mathf.Min(healAmount, MaxHealth - CurrentHealth);
        CurrentHealth += actual;
        if (actual > 0f) BroadcastHealNotifyRpc(actual);
    }

    public void HealSilent(float healAmount)
    {
        if (!isOwner) return;
        CurrentHealth = Mathf.Min(CurrentHealth + healAmount, MaxHealth);
    }

    [ServerRpc(requireOwnership: true)]
    private void BroadcastHealNotifyRpc(float amount) => NotifyHealRpc(amount);

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
        ReviveOwnerRpc(owner.Value, healthPercent);
    }

    [TargetRpc]
    private void ReviveOwnerRpc(PlayerID target, float healthPercent)
    {
        IsDead = false;
        CurrentHealth = MaxHealth * healthPercent;
        CurrentShield = MaxShield;
        CurrentEnergy = MaxEnergy;
        SetInvincible(2f);
    }

    public void RecalculateXPMod()
    {
        GameManager.instance.UpdateXPModRpc(owner.Value, XPModifier);
    }

    [ObserversRpc]
    private void NotifyHealRpc(float amount) => OnHeal?.Invoke(this, amount);

    [ServerRpc(requireOwnership: false)]
    public void ReceiveHealRpc(float amount) => HealTargetRpc(owner.Value, amount);

    [TargetRpc]
    private void HealTargetRpc(PlayerID target, float amount)
    {
        float actual = Mathf.Min(amount, MaxHealth - CurrentHealth);
        CurrentHealth += actual;
        if (actual > 0f) BroadcastHealNotifyRpc(actual);
    }

    public void AddMaxHealthPercent(float bonus)
    {
        if (!isOwner) return;
        float prevMax = MaxHealth;
        BonusMaxHealthPercent += bonus;
        if (!IsDead)
            CurrentHealth = Mathf.Min(CurrentHealth + (MaxHealth - prevMax), MaxHealth);
    }

    public void AddMaxHealthFlat(float bonus)
    {
        if (!isOwner) return;
        BonusMaxHealthFlat += bonus;
        if (!IsDead)
            CurrentHealth = Mathf.Min(CurrentHealth + bonus, MaxHealth);
    }

    public void AddMaxShieldPercent(float bonus)
    {
        if (!isOwner) return;
        BonusMaxShieldPercent += bonus;
    }

    public void AddHealthRegenFlat(float bonus)
    {
        if (!isOwner) return;
        BonusHealthRegenFlat += bonus;
    }

    public void AddHealthRegenPercent(float bonus)
    {
        if (!isOwner) return;
        BonusHealthRegenPercent += bonus;
    }

    public static event Action<CharacterStats, float> OnHeal;

    public static event Action<CharacterStats, EnemyBase> OnEnemyKill;
    public static event Action<CharacterStats, EnemyBase> OnEnemyDeath;

    public static void RaiseOnEnemyKill(CharacterStats killer, EnemyBase enemy) => OnEnemyKill?.Invoke(killer, enemy);
    public static void RaiseOnEnemyDeath(CharacterStats killer, EnemyBase enemy) => OnEnemyDeath?.Invoke(killer, enemy);

    public static event Action<CharacterStats, Vector3, float, float, string> OnVFXBroadcast;

    [ServerRpc(requireOwnership: true)]
    public void BroadcastVFXRpc(Vector3 position, float size, float duration, string vfxTag)
        => ShowVFXRpc(position, size, duration, vfxTag);

    [ObserversRpc]
    private void ShowVFXRpc(Vector3 position, float size, float duration, string vfxTag)
        => OnVFXBroadcast?.Invoke(this, position, size, duration, vfxTag);

    [ServerRpc(requireOwnership: false)]
    public void TakeDamageRpc(float damage) => TakeDamage(damage);

    [ObserversRpc]
    private void FlashHitRpc()
    {
        if (isOwner)
            SFXManager.instance?.Play(damagedSound, transform.position);
        StartCoroutine(FlashCoroutine());
    }

    [ServerRpc(requireOwnership: true)]
    private void BroadcastHitFlashRpc() => FlashHitRpc();

    private IEnumerator FlashCoroutine()
    {
        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].material.color = Color.red;
        yield return new WaitForSeconds(0.08f);
        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].material.color = _originalColors[i];
    }

    public void TakeDamage(float damage)
    {
        if (!isOwner) return;
        if (IsDead) return;
        if (IsInvincible) return;

        _shieldRechargeTimer = ShieldRegenTime;
        BroadcastHitFlashRpc();

        damage *= DamageReduction;

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