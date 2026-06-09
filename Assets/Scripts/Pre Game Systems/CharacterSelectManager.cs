using PurrNet;
using UnityEngine;

public class CharacterSelectManager : MonoBehaviour
{
    public static CharacterSelectManager instance;

    private int _loadedCount;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    public void PlayerLoaded(PlayerID player)
    {
        _loadedCount++;
        if (_loadedCount >= NetworkManager.main.playerCount)
        {
            _loadedCount = 0;
            EnemySpawner.instance.StartSpawning();
        }
    }

    public void ResetLobby()
    {
        _loadedCount = 0;
    }
}
