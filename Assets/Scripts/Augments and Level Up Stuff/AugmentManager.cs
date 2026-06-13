using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class AugmentManager : NetworkBehaviour
{
    public static AugmentManager instance;

    [SerializeField] private StatAugmentDatabase statDatabase;

    // Each player's current 3 card options (pairs of stats)
    private Dictionary<PlayerID, List<StatAugmentPair>> _playerOptions = new();

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    public void GenerateStatOptions(PlayerID player)
    {
        var options = new List<StatAugmentPair>();
        var usedPairs = new HashSet<string>();

        bool isRanged = false;
        foreach (var s in FindObjectsByType<CharacterStats>(FindObjectsSortMode.None))
        {
            if (s.owner == player) { isRanged = s.isRanged; break; }
        }

        var stats = new List<StatAugmentEntry>();
        foreach (var entry in statDatabase.stats)
            if (!entry.rangedOnly || isRanged) stats.Add(entry);

        int attempts = 0;
        while (options.Count < 3 && attempts < 50)
        {
            attempts++;
            int idxA = Random.Range(0, stats.Count);
            int idxB = Random.Range(0, stats.Count);

            if (idxA == idxB) continue;

            // Create a unique key for this pair regardless of order
            string key = idxA < idxB ? $"{idxA}_{idxB}" : $"{idxB}_{idxA}";
            if (usedPairs.Contains(key)) continue;

            usedPairs.Add(key);
            options.Add(new StatAugmentPair(stats[idxA], stats[idxB]));
        }

        _playerOptions[player] = options;

        var indicesA = new int[options.Count];
        var indicesB = new int[options.Count];
        for (int i = 0; i < options.Count; i++)
        {
            indicesA[i] = statDatabase.stats.IndexOf(options[i].statA);
            indicesB[i] = statDatabase.stats.IndexOf(options[i].statB);
        }
        SendOptionsToPlayerRpc(player, indicesA, indicesB);
    }

    public List<StatAugmentPair> GetOptionsForPlayer(PlayerID player)
    {
        return _playerOptions.TryGetValue(player, out var options) ? options : null;
    }

    [ServerRpc(requireOwnership: false)]
    public void PickAugmentRpc(PlayerID player, int cardIndex)
    {
        if (!_playerOptions.TryGetValue(player, out var options)) return;
        if (cardIndex < 0 || cardIndex >= options.Count) return;

        ApplyStatAugment(player, options[cardIndex]);
        _playerOptions.Remove(player);

        GameManager.instance.ConfirmReadyRpc(player);
    }

    private void ApplyStatAugment(PlayerID player, StatAugmentPair pair)
    {
        var players = FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
        foreach (var stats in players)
        {
            if (stats.owner != player) continue;
            ApplyStat(stats, pair.statA);
            ApplyStat(stats, pair.statB);
            break;
        }
    }

    private void ApplyStat(CharacterStats stats, StatAugmentEntry entry)
    {
        switch (entry.statType)
        {
            case StatType.MaxHealth:
                stats.BonusMaxHealthFlat += entry.value;
                stats.CurrentHealth += entry.value;
                break;
            case StatType.WeaponDamage:
                stats.bonusWeaponDamageFlat += entry.value;
                break;
            case StatType.MoveSpeed:
                stats.bonusMoveSpeedFlat += entry.value * 0.1f;
                break;
            case StatType.WeaponRange:
                stats.bonusWeaponRangeFlat += entry.value * 0.2f * stats.baseWeaponRange;
                break;
            case StatType.AttackSpeed:
                stats.bonusWeaponSpeedFlat += entry.value * 0.01f * stats.baseWeaponSpeed;
                break;
            case StatType.HealthRegen:
                stats.bonusHealthRegenFlat += entry.value;
                break;
            case StatType.MaxShield:
                stats.bonusMaxShieldFlat += entry.value * 0.01f;
                break;
            case StatType.EnergyRegen:
                stats.bonusEnergyRegenFlat += entry.value;
                break;
            case StatType.DashCooldown:
                stats.dashHaste += entry.value;
                break;
            case StatType.CritChance:
                stats.bonusCritChanceFlat += entry.value * 0.01f;
                break;
            case StatType.Pierce:
                stats.bonusPierceFlat += entry.value;
                break;
            case StatType.Ricochet:
                stats.bonusRicochetFlat += entry.value;
                break;
        }
    }

    [TargetRpc]
    private void SendOptionsToPlayerRpc(PlayerID target, int[] statIndicesA, int[] statIndicesB)
    {
        var options = new List<StatAugmentPair>();
        for (int i = 0; i < statIndicesA.Length; i++)
            options.Add(new StatAugmentPair(statDatabase.stats[statIndicesA[i]], statDatabase.stats[statIndicesB[i]]));
        
        LocalUIManager.instance.SetUpLevelUpScreen(options, target);
    }

    public void ApplyRandomAugment(PlayerID player)
    {
        if (!_playerOptions.TryGetValue(player, out var options)) return;
        if (options.Count == 0) return;
        int randomIndex = UnityEngine.Random.Range(0, options.Count);
        ApplyStatAugment(player, options[randomIndex]);
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