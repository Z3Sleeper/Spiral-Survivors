using PurrNet;
using UnityEngine;

public class XPOrbManager : MonoBehaviour
{
    public static XPOrbManager instance;

    [SerializeField] private GameObject orbPrefab1;
    [SerializeField] private GameObject orbPrefab10;
    [SerializeField] private GameObject orbPrefab100;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    public void SpawnOrbs(Vector3 position, float xpValue)
    {
        int remaining = Mathf.RoundToInt(xpValue);

        int count100 = remaining / 100;
        remaining %= 100;
        int count10 = remaining / 10;
        remaining %= 10;
        int count1 = remaining;

        for (int i = 0; i < count100; i++) SpawnOrb(orbPrefab100, position, 100);
        for (int i = 0; i < count10; i++) SpawnOrb(orbPrefab10, position, 10);
        for (int i = 0; i < count1; i++) SpawnOrb(orbPrefab1, position, 1);
    }

    private void SpawnOrb(GameObject prefab, Vector3 position, int value)
    {
        var obj = Instantiate(prefab, position, Quaternion.identity);
        var orb = obj.GetComponent<XPOrb>();
        orb.Init(value);
    }
}