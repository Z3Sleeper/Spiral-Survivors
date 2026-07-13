using PurrNet;
using PurrNet.Modules;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ReturnToLobbyManager : NetworkIdentity
{
    public static ReturnToLobbyManager instance;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    public void ReturnToLobby()
    {
        if (!isServer) return;

        // Server commits its own points directly rather than through the RPC below — every machine
        // already independently knows the correct total via deterministic simulation, so there's no
        // value earned over the network here, just avoiding a double-commit on the server's own machine.
        GameManagerPD.instance?.FlushUpgradePoints();

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
        if (!isServer) GameManagerPD.instance?.FlushUpgradePoints();
    }
}
