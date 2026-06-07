using PurrNet;
using PurrNet.Modules;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.PlasticSCM.Editor.WebApi;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : NetworkIdentity
{
    public static GameManager instance;
    public CharacterSpawner characterSpawner;

    [NonSerialized] public SyncVar<float> _currentXP = new(0);
    public float CurrentXP
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

    [NonSerialized] public SyncVar<float> _xpToNextLevel = new(20);
    public float XPToNextLevel
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
    private int _levelsToResolve = 0;

    private HashSet<PlayerID> _readyPlayers = new();
    private HashSet<PlayerID> _gameOverReadyPlayers = new();
    private HashSet<PlayerID> _pauseReadyPlayers = new();
    private Coroutine _levelUpTimerCoroutine;

    private const float LevelUpTimerDuration = 30f;

    public static event Action OnLevelUp;
    public static event Action<bool> OnPauseChanged;

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
        OnPauseChanged?.Invoke(newVal);
    }

    [ServerRpc(requireOwnership: false)]
    public void ReadyToLevelRpc(PlayerID player)
    {
        ShowLevelUpScreenRpc();
        if (!_pauseReadyPlayers.Contains(player))
            _pauseReadyPlayers.Add(player);
        if (_pauseReadyPlayers.Count >= networkManager.playerCount)
        {
            _pauseReadyPlayers.Clear();
            IsPaused = true;
        }
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
        float coopXPPenalty = 1f + (networkManager.playerCount - 1) * EnemySpawner.instance.coopSpawnTimeScalar;
        float xpGained = value * _avgXPMod / coopXPPenalty;
        CurrentXP += xpGained;

        if (CurrentXP >= XPToNextLevel && !IsPaused && _levelsToResolve == 0)
        {
            while (CurrentXP >= XPToNextLevel)
            {
                _levelsToResolve++;
                Level++;
                CurrentXP -= XPToNextLevel;
                XPToNextLevel *= 1.25f;
            }
            LevelUp();
        }
    }

    private void LevelUp()
    {
        _readyPlayers.Clear();
        OnLevelUp?.Invoke();

        foreach (var player in networkManager.players)
            AugmentManager.instance.GenerateStatOptions(player);

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
        _readyPlayers.Add(player);

        if (_readyPlayers.Count >= networkManager.playerCount)
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

        foreach (var player in networkManager.players)
        {
            if (_readyPlayers.Contains(player)) continue;
            AugmentManager.instance.ApplyRandomAugment(player);
        }

        _levelsToResolve--;

        if (_levelsToResolve == 0)
        {
            while (CurrentXP >= XPToNextLevel)
            {
                _levelsToResolve++;
                Level++;
                CurrentXP -= XPToNextLevel;
                XPToNextLevel *= 1.25f;
            }
        }

        if (_levelsToResolve > 0)
        {
            LevelUp();
            return;
        }

        IsPaused = false;
        HideLevelUpScreenRpc();
    }
    
    [ObserversRpc]
    private void ShowLevelUpScreenRpc()
    {
        LocalUIManager.instance.ShowLevelUpScreen();
    }

    [ObserversRpc]
    private void HideLevelUpScreenRpc()
    {
        LocalUIManager.instance.HideLevelUpScreen();
    }

    [ObserversRpc]
    private void UpdateTimerRpc(int secondsRemaining)
    {
        LocalUIManager.instance.UpdateTimer(secondsRemaining);
    }

    [ServerRpc(requireOwnership: false)]
    public void CheckAllPlayersDead()
    {
        var players = FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
        foreach (var player in players)
            if (!player.IsDead) return;
        ShowGameOverRPC();
    }

    [ObserversRpc]
    private void ShowGameOverRPC()
    {
        _gameOverReadyPlayers.Clear();
        NetworkedUIManager.instance.ShowGameOverScreen();
    }

    [ServerRpc(requireOwnership: false)]
    public void ConfirmReturnToLobbyRpc(PlayerID player)
    {
        _gameOverReadyPlayers.Add(player);
        if (_gameOverReadyPlayers.Count >= networkManager.playerCount)
            ReturnToLobby();
    }

    private void ReturnToLobby()
    {
        if (CharacterSelectManager.instance != null)
            CharacterSelectManager.instance.ResetLobby();
        NetworkManager.main.sceneModule.LoadSceneAsync("Lobby Scene", new PurrSceneSettings
        {
            mode = LoadSceneMode.Single,
            isPublic = true
        });
    }
}