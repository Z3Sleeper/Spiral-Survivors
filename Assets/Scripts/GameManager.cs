using PurrNet;
using System;
using System.Collections;
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

    [NonSerialized] public SyncVar<bool> _isPaused = new(false);
    public bool IsPaused
    {
        get => _isPaused.value;
        set => _isPaused.value = value;
    }

    private Dictionary<PlayerID, float> _playerXPMods = new();
    private float _avgXPMod = 1f;

    private HashSet<PlayerID> _readyPlayers = new();
    private Coroutine _levelUpTimerCoroutine;

    private const float LevelUpTimerDuration = 30f;

    public static event Action OnLevelUp;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _isPaused.onChanged += OnPausedChanged;
    }

    private void OnPausedChanged(bool newVal)
    {
        Time.timeScale = newVal ? 0f : 1f;
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
        Level++;
        CurrentXP -= XPToNextLevel;
        XPToNextLevel = Mathf.RoundToInt(XPToNextLevel * 1.25f);

        _readyPlayers.Clear();
        IsPaused = true;

        OnLevelUp?.Invoke();
        ShowLevelUpScreenRpc();

        bool isMultiplayer = networkManager != null && networkManager.playerCount >= 2;
        if (isMultiplayer)
        {
            if (_levelUpTimerCoroutine != null)
                StopCoroutine(_levelUpTimerCoroutine);
            _levelUpTimerCoroutine = StartCoroutine(LevelUpTimerCoroutine());
        }
    }

    [ServerRpc(requireOwnership: false)]
    public void ConfirmReadyRpc(PlayerID player)
    {
        if (!IsPaused) return;

        _readyPlayers.Add(player);

        int playerCount = networkManager != null ? networkManager.playerCount : 1;
        if (_readyPlayers.Count >= playerCount)
            ResolveLevelUp();
    }

    private IEnumerator LevelUpTimerCoroutine()
    {
        float remaining = LevelUpTimerDuration;

        while (remaining > 0f)
        {
            // Use unscaled time since we're paused
            yield return new WaitForSecondsRealtime(1f);
            remaining -= 1f;
            UpdateTimerRpc(Mathf.CeilToInt(remaining));
        }

        ResolveLevelUp();
    }

    private void ResolveLevelUp()
    {
        if (_levelUpTimerCoroutine != null)
        {
            StopCoroutine(_levelUpTimerCoroutine);
            _levelUpTimerCoroutine = null;
        }

        IsPaused = false;
        HideLevelUpScreenRpc();
    }

    [ObserversRpc]
    private void ShowLevelUpScreenRpc()
    {
        bool isMultiplayer = networkManager != null && networkManager.playerCount >= 2;
        NetworkedUIManager.instance?.ShowLevelUpScreen(isMultiplayer);
    }

    [ObserversRpc]
    private void HideLevelUpScreenRpc()
    {
        NetworkedUIManager.instance?.HideLevelUpScreen();
    }

    [ObserversRpc]
    private void UpdateTimerRpc(int secondsRemaining)
    {
        NetworkedUIManager.instance?.UpdateTimer(secondsRemaining);
    }
}