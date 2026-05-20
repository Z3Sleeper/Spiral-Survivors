using PurrNet;
using PurrNet.Modules;
using UnityEngine;
using UnityEngine.SceneManagement;

public class CharacterSelectManager : NetworkIdentity
{
    public static CharacterSelectManager instance;

    public SyncDictionary<PlayerID, int> playerCharacterChoices = new SyncDictionary<PlayerID, int>();
    public SyncDictionary<PlayerID, bool> _readyStates = new SyncDictionary<PlayerID, bool>();
    public SyncDictionary<PlayerID, bool> _loadedStates = new SyncDictionary<PlayerID, bool>();

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        if (!isServer) return;

        foreach (var player in NetworkManager.main.players)
        {
            playerCharacterChoices[player] = 0;
            _readyStates[player] = false;
        }

        NetworkManager.main.onPlayerJoined += OnPlayerJoined;
        NetworkManager.main.onPlayerLeft += OnPlayerLeft;
    }

    private void OnPlayerJoined(PlayerID player, bool isReconnect, bool asServer)
    {
        if (!asServer) return;
        playerCharacterChoices[player] = 0;
        _readyStates[player] = false;
    }

    private void OnPlayerLeft(PlayerID player, bool asServer)
    {
        if (!asServer) return;
        playerCharacterChoices.Remove(player);
        _readyStates.Remove(player);
        CheckAllReady();
    }

    [ServerRpc(requireOwnership: false)]
    public void SelectCharacter(int index, PlayerID player)
    {
        playerCharacterChoices[player] = index;
    }

    [ServerRpc(requireOwnership: false)]
    public void SelectRandom(PlayerID player)
    {
        int randomIndex = Random.Range(0, CharacterRegistry.instance.PrefabCount());
        playerCharacterChoices[player] = randomIndex;
    }

    [ServerRpc(requireOwnership: false)]
    public void ReadyUp(PlayerID player)
    {
        if (_readyStates[player] == false)
        {
            _readyStates[player] = true;
        }
        else
        {
            _readyStates[player] = false;
        }
        
        CheckAllReady();
    }

    private void CheckAllReady()
    {
        if (_readyStates.Count == 0) return;
        foreach (var ready in _readyStates.Values)
            if (!ready) return;
        LoadGameScene();
    }

    private void LoadGameScene()
    {
        NetworkManager.main.sceneModule.LoadSceneAsync("Networked Game Scene", new PurrSceneSettings
        {
            mode = LoadSceneMode.Single,
            isPublic = true
        });
    }

    [ServerRpc(requireOwnership: false)]
    public void PlayerLoaded(PlayerID player)
    {
        _loadedStates[player] = true;
        CheckAllLoaded();
    }

    private void CheckAllLoaded()
    {
        if (_loadedStates.Count == 0) return;
        foreach (var loaded in _loadedStates.Values)
            if (!loaded) return;
        // Everyone loaded, start the game
        EnemySpawner.instance.StartSpawning();
    }
}