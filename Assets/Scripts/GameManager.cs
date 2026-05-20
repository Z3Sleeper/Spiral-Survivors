using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;
    public CharacterSpawner characterSpawner;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }
}