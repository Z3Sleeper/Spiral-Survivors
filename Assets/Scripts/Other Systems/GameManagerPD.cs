using PurrNet;
using PurrNet.Modules;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManagerPD : NetworkIdentity
{
    public static GameManagerPD instance;

    [SerializeField] private EnemySpawnerPD _enemySpawner;

    private bool _gameStarted;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    private void Update()
    {
        if (!_gameStarted)
        {
            var nm = NetworkManager.main;
            if (nm != null && AllPlayersInitialized(nm))
                OnGameStarted(nm);
        }
    }

    // ── Game start ──────────────────────────────────────────────────────────

    private static bool AllPlayersInitialized(NetworkManager nm)
    {
        if (nm.playerCount == 0) return false;
        foreach (var player in nm.players)
            if (!CharacterStatsPD.All.ContainsKey(player)) return false;
        return true;
    }

    private void OnGameStarted(NetworkManager nm)
    {
        _gameStarted = true;
        if (LoadingScreen.instance != null) LoadingScreen.instance.Hide();
        if (MusicPlayer.instance != null) MusicPlayer.instance.Play();
        if (nm.isServer && _enemySpawner != null)
            _enemySpawner.StartSpawning();
    }

    // ── Return to lobby ─────────────────────────────────────────────────────

    public void ReturnToLobby()
    {
        if (!isServer) return;
        Time.timeScale = 1f;
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
}
