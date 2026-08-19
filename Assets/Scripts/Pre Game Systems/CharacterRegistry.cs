using UnityEngine;

public class CharacterRegistry : MonoBehaviour
{
    public static CharacterRegistry instance;

    [SerializeField] private GameObject[] predictedCharacterPrefabs;

    public int PrefabCount() => predictedCharacterPrefabs.Length;
    public GameObject GetPredictedPrefab(int index) => predictedCharacterPrefabs[index];

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // OnApplicationQuit() (Steam-lobby-leak fix + SteamAPI shutdown request) moved to GamePresets (2026-08-20)
    // — this is a character-prefab registry specifically, that logic never semantically belonged here, it
    // just needed SOME DDOL object guaranteed alive at quit time. See GamePresets.OnApplicationQuit's own
    // doc comment for the full reasoning.
}