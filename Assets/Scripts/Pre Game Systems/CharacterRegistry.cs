using UnityEngine;

public class CharacterRegistry : MonoBehaviour
{
    public static CharacterRegistry instance;
    
    [SerializeField] private GameObject[] characterPrefabs;
    [SerializeField] private GameObject[] predictedCharacterPrefabs;

    public int PrefabCount() => characterPrefabs.Length;
    public GameObject GetPrefab(int index) => characterPrefabs[index];
    public GameObject GetPredictedPrefab(int index) => predictedCharacterPrefabs[index];

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }
}