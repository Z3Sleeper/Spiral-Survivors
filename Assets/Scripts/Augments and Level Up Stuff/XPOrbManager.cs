using UnityEngine;

public class XPOrbManager : MonoBehaviour
{
    public static XPOrbManager instance;

    [System.Serializable]
    private struct OrbTier
    {
        public int value;
        public GameObject prefab;
    }

    [SerializeField] private OrbTier[] _tiers;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    public void SpawnOrbs(Vector3 position, float xpValue)
    {
        int remaining = Mathf.RoundToInt(xpValue);

        for (int i = _tiers.Length - 1; i >= 0; i--)
        {
            if (_tiers[i].value <= 0) continue;
            int count = remaining / _tiers[i].value;
            remaining %= _tiers[i].value;
            for (int j = 0; j < count; j++)
                SpawnOrb(_tiers[i].prefab, position, _tiers[i].value);
        }
    }

    private void SpawnOrb(GameObject prefab, Vector3 position, int value)
    {
        if (prefab == null) return;
        var obj = Instantiate(prefab, position, Quaternion.identity);
        obj.GetComponent<XPOrb>().Init(value);
    }
}
