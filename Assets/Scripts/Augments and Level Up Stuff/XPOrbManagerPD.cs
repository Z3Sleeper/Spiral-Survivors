using PurrNet.Prediction;
using UnityEngine;

public class XPOrbManagerPD : MonoBehaviour
{
    public static XPOrbManagerPD instance;

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

        System.Array.Sort(_tiers, (a, b) => b.value.CompareTo(a.value));
    }

    public void SpawnOrbs(PredictedHierarchy hierarchy, Vector3 position, float xpValue)
    {
        if (_tiers.Length == 0) return;

        // Tiers are sorted descending in Awake(), so index 0 is the highest tier. Past 5x that value,
        // per-tier splitting would balloon the orb count unboundedly (no tier above it to carry into),
        // so just represent the whole amount as a single orb instead of spawning dozens/hundreds.
        var highestTier = _tiers[0];
        if (highestTier.value > 0 && xpValue > highestTier.value * 5)
        {
            SpawnOrb(hierarchy, highestTier.prefab, position, Mathf.RoundToInt(xpValue));
            return;
        }

        int remaining = Mathf.RoundToInt(xpValue);

        for (int i = 0; i < _tiers.Length; i++)
        {
            if (_tiers[i].value <= 0) continue;
            int count = remaining / _tiers[i].value;
            remaining %= _tiers[i].value;
            for (int j = 0; j < count; j++)
                SpawnOrb(hierarchy, _tiers[i].prefab, position, _tiers[i].value);
        }
    }

    private void SpawnOrb(PredictedHierarchy hierarchy, GameObject prefab, Vector3 position, int value)
    {
        if (prefab == null) return;
        var id = hierarchy.Create(prefab, position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out XPOrbPD orb))
            orb.SetValue(value);
    }
}
