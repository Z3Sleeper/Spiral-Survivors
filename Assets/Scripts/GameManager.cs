using PurrNet;
using System;
using System.Collections.Generic;
using UnityEngine;

public class GameManager : NetworkBehaviour
{
    public static GameManager instance;
    public CharacterSpawner characterSpawner;

    [NonSerialized] public SyncVar<int> _currentXP = new(0);
    public int CurrentXP
    {
        get => _currentXP.value;
        set => _currentXP.value = value;
    }

    [NonSerialized] public SyncVar<int> _level = new(1);
    public int Level
    {
        get => _level.value;
        set => _level.value = value;
    }

    [NonSerialized] public SyncVar<int> _xpToNextLevel = new(20);
    public int XPToNextLevel
    {
        get => _xpToNextLevel.value;
        set => _xpToNextLevel.value = value;
    }

    private Dictionary<PlayerID, float> _playerXPMods = new();
    private float _avgXPMod = 1f;

    public static event Action OnLevelUp;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }


    [ServerRpc(requireOwnership: false)]
    public void UpdateXPModRpc(PlayerID player, float mod)
    {
        _playerXPMods[player] = mod;
        float total = 0f;
        foreach (var m in _playerXPMods.Values)
            total += m;
        _avgXPMod = total / _playerXPMods.Count;
    }

    [ServerRpc(requireOwnership: false)]
    public void AddXPRpc(int value)
    {
        int xpGained = Mathf.RoundToInt(value * _avgXPMod);
        CurrentXP += xpGained;
        if (CurrentXP >= XPToNextLevel)
            LevelUp();
    }

    private void LevelUp()
    {
        Debug.Log("Level Up!");
        Level++;
        CurrentXP -= XPToNextLevel;
        XPToNextLevel = Mathf.RoundToInt(XPToNextLevel * 1.25f); // 25% more XP each level
        OnLevelUp?.Invoke();
    }
}