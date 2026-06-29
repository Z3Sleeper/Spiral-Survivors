using System;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class AugmentManager : NetworkBehaviour
{
    public static AugmentManager instance;

    [SerializeField] private StatAugmentDatabase statDatabase;
    [SerializeField] private AugmentDatabase augmentDatabase;

    private Dictionary<PlayerID, List<AugmentData>> _playerPools = new();
    private Dictionary<PlayerID, HashSet<AugmentData>> _playerPickedAugments = new();
    private Dictionary<PlayerID, List<LevelUpOption>> _playerOptions = new();

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    public void InitializePlayerPool(PlayerID player, CharacterStats.CharacterClass characterClass)
    {
        bool isMultiplayer = networkManager.playerCount > 1;
        var pool = new List<AugmentData>();
        foreach (var augment in augmentDatabase.augments)
        {
            if (augment == null) continue;
            if (augment.IsAvailableForClass(characterClass) && (!augment.multiplayerOnly || isMultiplayer))
                pool.Add(augment);
        }

        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        _playerPools[player] = pool;
        _playerPickedAugments[player] = new HashSet<AugmentData>();
    }

    public void GenerateOptions(PlayerID player, int level)
    {
        var options = new List<LevelUpOption>();
        bool isAugmentLevel = level % 5 == 0;

        var allStats = FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
        var playerStats = System.Array.Find(allStats, s => s.owner.HasValue && s.owner.Value == player);
        int optionCount = playerStats != null ? playerStats.levelUpOptions : 1;

        if (isAugmentLevel && _playerPools.TryGetValue(player, out var pool) && pool.Count > 0)
        {
            _playerPickedAugments.TryGetValue(player, out var picked);
            var available = pool.FindAll(a => IsPrerequisiteMet(a, picked));
            int drawCount = Mathf.Min(optionCount, available.Count);
            var drawnIndices = new HashSet<int>();
            for (int i = 0; i < drawCount; i++)
            {
                int idx, attempts = 0;
                do { idx = UnityEngine.Random.Range(0, available.Count); attempts++; }
                while (drawnIndices.Contains(idx) && attempts < 20);
                drawnIndices.Add(idx);
                options.Add(LevelUpOption.FromAugment(available[idx]));
            }
        }

        var usedPairs = new HashSet<(StatAugmentEntry, StatAugmentEntry)>();
        while (options.Count < optionCount)
        {
            var pair = GenerateStatFallback(player, usedPairs);
            if (pair != null)
            {
                usedPairs.Add((pair.statA, pair.statB));
                options.Add(LevelUpOption.FromStatAugment(pair));
            }
            else break;
        }

        _playerOptions[player] = options;

        var types = new int[options.Count];
        var primaryIndices = new int[options.Count];
        var secondaryIndices = new int[options.Count];

        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].type == LevelUpOption.OptionType.Augment)
            {
                types[i] = 0;
                primaryIndices[i] = augmentDatabase.augments.IndexOf(options[i].augmentData);
                secondaryIndices[i] = -1;
            }
            else
            {
                types[i] = 1;
                primaryIndices[i] = statDatabase.stats.IndexOf(options[i].statPair.statA);
                secondaryIndices[i] = statDatabase.stats.IndexOf(options[i].statPair.statB);
            }
        }

        SendOptionsToPlayerRpc(player, types, primaryIndices, secondaryIndices);
    }

    private bool IsPrerequisiteMet(AugmentData augment, HashSet<AugmentData> picked)
    {
        if (picked == null)
            return (augment.requiredAugments == null || augment.requiredAugments.Length == 0)
                && (augment.requiredOneOfAugments == null || augment.requiredOneOfAugments.Length == 0);

        if (augment.requiredAugments != null)
            foreach (var req in augment.requiredAugments)
                if (!picked.Contains(req)) return false;

        if (augment.requiredOneOfAugments != null && augment.requiredOneOfAugments.Length > 0)
        {
            bool anyMet = false;
            foreach (var req in augment.requiredOneOfAugments)
                if (picked.Contains(req)) { anyMet = true; break; }
            if (!anyMet) return false;
        }

        return true;
    }

    private StatAugmentPair GenerateStatFallback(PlayerID player, HashSet<(StatAugmentEntry, StatAugmentEntry)> usedPairs)
    {
        CharacterStats.CharacterClass playerClass = CharacterStats.CharacterClass.Knight;
        foreach (var s in CharacterStats.All)
            if (s.owner == player) { playerClass = s.characterClass; break; }

        var validStats = new List<StatAugmentEntry>();
        foreach (var entry in statDatabase.stats)
            if (entry.allowedClasses.Length == 0 || Array.IndexOf(entry.allowedClasses, playerClass) >= 0)
                validStats.Add(entry);

        if (validStats.Count < 2) return null;

        var available = new List<(StatAugmentEntry, StatAugmentEntry)>();
        for (int i = 0; i < validStats.Count; i++)
            for (int j = i + 1; j < validStats.Count; j++)
                if (!usedPairs.Contains((validStats[i], validStats[j])))
                    available.Add((validStats[i], validStats[j]));

        if (available.Count == 0) return null;

        var chosen = available[UnityEngine.Random.Range(0, available.Count)];
        return new StatAugmentPair(chosen.Item1, chosen.Item2);
    }

    public IReadOnlyCollection<AugmentData> GetPickedAugmentsForPlayer(PlayerID player)
    {
        return _playerPickedAugments.TryGetValue(player, out var picked) ? picked : null;
    }

    public List<LevelUpOption> GetOptionsForPlayer(PlayerID player)
    {
        return _playerOptions.TryGetValue(player, out var options) ? options : null;
    }

    [ServerRpc(requireOwnership: false)]
    public void PickAugmentRpc(PlayerID player, int cardIndex, float effectiveValueA = 1f, float effectiveValueB = 1f)
    {
        if (!_playerOptions.TryGetValue(player, out var options)) return;
        if (cardIndex < 0 || cardIndex >= options.Count) return;

        var option = options[cardIndex];
        if (option.type == LevelUpOption.OptionType.Augment)
        {
            ApplyRealAugment(player, option.augmentData);
            if (_playerPickedAugments.TryGetValue(player, out var picked))
                picked.Add(option.augmentData);
            if (_playerPools.TryGetValue(player, out var pool))
            {
                pool.Remove(option.augmentData);
                if (option.augmentData.incompatibleAugments != null)
                    foreach (var incompatible in option.augmentData.incompatibleAugments)
                        pool.Remove(incompatible);
            }
        }
        else
            ApplyStatAugment(player, option.statPair, effectiveValueA, effectiveValueB);

        _playerOptions.Remove(player);
        GameManager.instance.ConfirmReadyRpc(player);
    }

    private void ApplyRealAugment(PlayerID player, AugmentData augment)
    {
        int index = augmentDatabase.augments.IndexOf(augment);
        ApplyAugmentEffectRpc(player, index);
    }

    [ObserversRpc]
    private void ApplyAugmentEffectRpc(PlayerID player, int augmentIndex)
    {
        var augment = augmentDatabase.augments[augmentIndex];
        if (augment.effectPrefab == null) return;
        CharacterStats stats = null;
        foreach (var s in CharacterStats.All)
            if (s.owner == player) { stats = s; break; }
        if (stats == null) return;
        Instantiate(augment.effectPrefab, stats.transform);
    }

    private void ApplyStatAugment(PlayerID player, StatAugmentPair pair, float effectiveValueA = 1f, float effectiveValueB = 1f)
    {
        CharacterStats stats = null;
        foreach (var s in CharacterStats.All)
            if (s.owner == player) { stats = s; break; }
        if (stats != null)
        {
            ApplyServerStat(stats, pair.statA, effectiveValueA);
            ApplyServerStat(stats, pair.statB, effectiveValueB);
        }

        int idxA = statDatabase.stats.IndexOf(pair.statA);
        int idxB = statDatabase.stats.IndexOf(pair.statB);
        ApplyAugmentLocalRpc(player, idxA, idxB);
    }

    private void ApplyServerStat(CharacterStats stats, StatAugmentEntry entry, float effectiveValue)
    {
        switch (entry.statType)
        {
            case StatType.MaxHealth:
                stats.BonusMaxHealthFlat += effectiveValue;
                stats.CurrentHealth = Mathf.Min(stats.CurrentHealth + effectiveValue, stats.MaxHealth);
                break;
        }
    }

    [TargetRpc]
    private void ApplyAugmentLocalRpc(PlayerID target, int idxA, int idxB)
    {
        CharacterStats stats = null;
        foreach (var s in CharacterStats.All)
            if (s.isOwner) { stats = s; break; }
        if (stats == null) return;
        ApplyStatLocal(stats, statDatabase.stats[idxA]);
        ApplyStatLocal(stats, statDatabase.stats[idxB]);
    }

    private void ApplyStatLocal(CharacterStats stats, StatAugmentEntry entry)
    {
        switch (entry.statType)
        {
            case StatType.WeaponDamage:  stats.bonusWeaponDamageFlat += entry.value * ProgressionManager.GetStatMultiplier(StatType.WeaponDamage); break;
            case StatType.MoveSpeed:     stats.bonusMoveSpeedFlat    += entry.value * 0.1f * ProgressionManager.GetStatMultiplier(StatType.MoveSpeed); break;
            case StatType.WeaponRange:   stats.bonusWeaponRangeFlat  += entry.value; break;
            case StatType.AttackSpeed:   stats.attackSpeedPercent    += entry.value * 0.01f * stats.baseAttackSpeed * ProgressionManager.GetStatMultiplier(StatType.AttackSpeed); break;
            case StatType.HealthRegen:   stats.BonusHealthRegenFlat  += entry.value * ProgressionManager.GetStatMultiplier(StatType.HealthRegen); break;
            case StatType.EnergyRegen:   stats.bonusEnergyRegenFlat  += entry.value * ProgressionManager.GetStatMultiplier(StatType.EnergyRegen); break;
            case StatType.DashHaste:     stats.dashHaste             += entry.value * ProgressionManager.GetStatMultiplier(StatType.DashHaste); break;
            case StatType.CritChance:    stats.bonusCritChanceFlat   += entry.value * 0.01f * ProgressionManager.GetStatMultiplier(StatType.CritChance); break;
        }
    }

    [TargetRpc]
    private void SendOptionsToPlayerRpc(PlayerID target, int[] types, int[] primaryIndices, int[] secondaryIndices)
    {
        var options = new List<LevelUpOption>();
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] == 0)
                options.Add(LevelUpOption.FromAugment(augmentDatabase.augments[primaryIndices[i]]));
            else
                options.Add(LevelUpOption.FromStatAugment(new StatAugmentPair(
                    statDatabase.stats[primaryIndices[i]],
                    statDatabase.stats[secondaryIndices[i]])));
        }
        LocalUIManager.instance.SetUpLevelUpScreen(options, target);
    }

    public void ApplyRandomAugment(PlayerID player)
    {
        if (!_playerOptions.TryGetValue(player, out var options) || options.Count == 0) return;
        int randomIndex = UnityEngine.Random.Range(0, options.Count);
        var option = options[randomIndex];
        if (option.type == LevelUpOption.OptionType.Augment)
            ApplyRealAugment(player, option.augmentData);
        else
            ApplyStatAugment(player, option.statPair);
        _playerOptions.Remove(player);
    }

}

public class StatAugmentPair
{
    public StatAugmentEntry statA;
    public StatAugmentEntry statB;

    public StatAugmentPair(StatAugmentEntry a, StatAugmentEntry b)
    {
        statA = a;
        statB = b;
    }
}
