using UnityEngine;

public class CharacterRegistry : MonoBehaviour
{
    public static CharacterRegistry instance;
    
    [SerializeField] private GameObject[] characterPrefabs;
    
    public int PrefabCount() => characterPrefabs.Length;

    public GameObject GetPrefab(int index) => characterPrefabs[index];

    private void Awake() => instance = this;
}