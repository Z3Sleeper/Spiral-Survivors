using PurrLobby;
using PurrNet;
using PurrNet.Modules;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : NetworkIdentity
{
    public static GameManager instance;
    public CharacterSpawner characterSpawner;
    [SerializeField] private float xpLevelStep = 5f;
    [SerializeField] private AudioClip levelUpSound;

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

    public SyncVar<float> _xpToNextLevel = new(5);
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

    [NonSerialized] public SyncVar<int> _sessionPoints = new(0);
    public int SessionPoints
    {
        get => _sessionPoints.value;
        set => _sessionPoints.value = value;
    }

    [SerializeField] private float pointMultiplier = 1f;

    public void AwardPoints(int amount)
    {
        if (!isServer) return;
        SessionPoints += Mathf.RoundToInt(amount * pointMultiplier);
    }

    private Dictionary<PlayerID, float> _playerXPMods = new();
    private float _avgXPMod = 1f;
    private int _levelsToResolve = 0;

    private HashSet<PlayerID> _readyPlayers = new();
    private HashSet<PlayerID> _gameOverReadyPlayers = new();
    private HashSet<PlayerID> _pauseReadyPlayers = new();
    private Coroutine _levelUpTimerCoroutine;
    private bool _isLevelingUp;
    private int _playersLoaded;

    private HashSet<PlayerID> _manualPauseReadyPlayers = new();
    private bool _isPendingPause;
    private Coroutine _pauseCountdownCoroutine;
    private Coroutine _pauseFallbackCoroutine;

    private const float LevelUpTimerDuration = 30f;

    public static event Action OnLevelUp;
    public static event Action<bool> OnPauseChanged;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    private void OnApplicationQuit()
    {
        var lobbyDataHolder = FindFirstObjectByType<LobbyDataHolder>();
        if (lobbyDataHolder != null && lobbyDataHolder.CurrentLobby.IsValid &&
            ulong.TryParse(lobbyDataHolder.CurrentLobby.LobbyId, out var lobbyId))
            SteamMatchmaking.LeaveLobby(new CSteamID(lobbyId));
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        Time.timeScale = 1f;
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
                CurrentXP -= XPToNextLevel;
                XPToNextLevel += xpLevelStep * ((Level / 5) + 1);
                Level++;
            }
            LevelUp();
        }
    }

    private void LevelUp()
    {
        if (_pauseCountdownCoroutine != null)
        {
            StopCoroutine(_pauseCountdownCoroutine);
            _pauseCountdownCoroutine = null;
            _isPendingPause = false;
            UpdatePauseCountdownRpc(0);
        }

        _isLevelingUp = true;
        _readyPlayers.Clear();
        OnLevelUp?.Invoke();

        foreach (var player in networkManager.players)
            AugmentManager.instance.GenerateOptions(player);

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
                XPToNextLevel += xpLevelStep * ((Level / 5) + 1);
            }
        }

        if (_levelsToResolve > 0)
        {
            LevelUp();
            return;
        }

        _isLevelingUp = false;
        IsPaused = false;
        HideLevelUpScreenRpc();
    }
    
    [ObserversRpc]
    private void ShowLevelUpScreenRpc()
    {
        LocalUIManager.instance.ShowLevelUpScreen();
        SFXManager.instance?.Play(levelUpSound, Vector3.zero);
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

    public void TriggerWin()
    {
        if (!isServer) return;
        IsPaused = true;
        ShowWinScreenRpc();
    }

    [ObserversRpc]
    private void ShowWinScreenRpc()
    {
        _gameOverReadyPlayers.Clear();
        NetworkedUIManager.instance.ShowWinScreen();
    }

    [ServerRpc(requireOwnership: false)]
    public void CheckAllPlayersDead()
    {
        var players = FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
        foreach (var player in players)
            if (!player.IsDead) return;

        if (_pauseCountdownCoroutine != null)
        {
            StopCoroutine(_pauseCountdownCoroutine);
            _pauseCountdownCoroutine = null;
            _isPendingPause = false;
        }

        IsPaused = true;
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
        UpdateGameOverTextRpc(_gameOverReadyPlayers.Count, networkManager.playerCount);
        if (_gameOverReadyPlayers.Count >= networkManager.playerCount)
            ReturnToLobby();
    }

    [ObserversRpc]
    private void UpdateGameOverTextRpc(int ready, int total)
    {
        NetworkedUIManager.instance.UpdateReadyText(ready, total);
    }

    public void PlayerLoaded()
    {
        if (!isServer) return;
        _playersLoaded++;
        Debug.Log($"[PlayerLoaded] _playersLoaded={_playersLoaded}, playerCount={networkManager.playerCount}");
        if (_playersLoaded >= networkManager.playerCount)
        {
            _playersLoaded = 0;
            HideLoadingScreenRpc();
            EnemySpawner.instance.StartSpawning();
        }
    }

    [ObserversRpc]
    private void HideLoadingScreenRpc()
    {
        LoadingScreen.instance.Hide();
        MusicPlayer.instance?.Play();
    }

    private void ReturnToLobby()
    {
        ProgressionManager.CommitPoints(SessionPoints);
        CommitPointsRpc(SessionPoints);
        Time.timeScale = 1f;
        _playersLoaded = 0;
        PurrLobby.LobbyFlags.IsReturningFromGame = true;
        SetReturningFromGameRpc();
        NetworkManager.main.sceneModule.LoadSceneAsync(GameStartData.LobbySceneName, new PurrSceneSettings
        {
            mode = LoadSceneMode.Single,
            isPublic = true
        });
    }

    [ObserversRpc]
    private void SetReturningFromGameRpc()
    {
        PurrLobby.LobbyFlags.IsReturningFromGame = true;
    }

    [ObserversRpc]
    private void CommitPointsRpc(int points)
    {
        if (isServer) return;
        ProgressionManager.CommitPoints(points);
    }

    [ServerRpc(requireOwnership: false)]
    public void ReturnToLobbyFromPauseRpc()
    {
        if (!IsPaused) return;
        ReturnToLobby();
    }

    [ServerRpc(requireOwnership: false)]
    public void RequestPauseRpc()
    {
        if (networkManager.playerCount == 1 && IsPaused)
        {
            Unpause();
            return;
        }
        if (_isPendingPause || IsPaused) return;
        _isPendingPause = true;

        if (networkManager.playerCount == 1)
        {
            IsPaused = true;
            _isPendingPause = false;
            ShowPauseScreenRpc(true);
            return;
        }

        _pauseCountdownCoroutine = StartCoroutine(PauseCountdownCoroutine());
    }

    private IEnumerator PauseCountdownCoroutine()
    {
        int countdown = 3;
        ShowPauseCountdownRpc(countdown);
        while (countdown > 0)
        {
            yield return new WaitForSecondsRealtime(1f);
            countdown--;
            UpdatePauseCountdownRpc(countdown);
        }
        IsPaused = true;
        _isPendingPause = false;
        _manualPauseReadyPlayers.Clear();
        ShowPauseScreenRpc(false);
    }

    [ServerRpc(requireOwnership: false)]
    public void TogglePauseReadyRpc(PlayerID player)
    {
        if (!IsPaused) return;

        bool wasEmpty = _manualPauseReadyPlayers.Count == 0;

        if (_manualPauseReadyPlayers.Contains(player))
            _manualPauseReadyPlayers.Remove(player);
        else
            _manualPauseReadyPlayers.Add(player);

        bool nowEmpty = _manualPauseReadyPlayers.Count == 0;

        UpdatePauseReadyCountRpc(_manualPauseReadyPlayers.Count, networkManager.playerCount);

        if (nowEmpty && _pauseFallbackCoroutine != null)
        {
            StopCoroutine(_pauseFallbackCoroutine);
            _pauseFallbackCoroutine = null;
            SetFallbackTimerRpc(0, false);
            return;
        }

        if (wasEmpty && !nowEmpty)
            _pauseFallbackCoroutine = StartCoroutine(PauseFallbackCoroutine());

        if (_manualPauseReadyPlayers.Count >= networkManager.playerCount)
            Unpause();
    }

    private IEnumerator PauseFallbackCoroutine()
    {
        for (int remaining = 30; remaining > 0; remaining--)
        {
            SetFallbackTimerRpc(remaining, true);
            yield return new WaitForSecondsRealtime(1f);
        }
        Unpause();
    }

    private void Unpause()
    {
        if (_isLevelingUp) return;
        if (_pauseFallbackCoroutine != null)
        {
            StopCoroutine(_pauseFallbackCoroutine);
            _pauseFallbackCoroutine = null;
        }
        _manualPauseReadyPlayers.Clear();
        _isPendingPause = false;
        IsPaused = false;
        HidePauseScreenRpc();
    }

    [ObserversRpc]
    private void ShowPauseCountdownRpc(int seconds)
        => LocalUIManager.instance.ShowPauseCountdown(seconds);

    [ObserversRpc]
    private void UpdatePauseCountdownRpc(int seconds)
        => LocalUIManager.instance.UpdatePauseCountdown(seconds);

    [ObserversRpc]
    private void ShowPauseScreenRpc(bool isSinglePlayer)
        => LocalUIManager.instance.ShowPauseScreen(isSinglePlayer);

    [ObserversRpc]
    private void HidePauseScreenRpc()
        => LocalUIManager.instance.HidePauseScreen();

    [ObserversRpc]
    private void UpdatePauseReadyCountRpc(int ready, int total)
        => LocalUIManager.instance.UpdatePauseReadyCount(ready, total);

    [ObserversRpc]
    private void SetFallbackTimerRpc(int seconds, bool visible)
        => LocalUIManager.instance.SetFallbackTimer(seconds, visible);
}