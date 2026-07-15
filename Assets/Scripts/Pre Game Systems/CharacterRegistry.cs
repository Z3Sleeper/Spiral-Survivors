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

    // Lives here (rather than on a scene-scoped manager) because this is the one object guaranteed to
    // still be alive at quit time regardless of which scene (lobby or game) the player was in — see
    // EnemyBasePD.ResetActiveCounts() for why this reset exists at all. Practically low-value (the process
    // is ending anyway) but kept for symmetry with the other exit paths.
    private void OnApplicationQuit() => EnemyBasePD.ResetActiveCounts();
}