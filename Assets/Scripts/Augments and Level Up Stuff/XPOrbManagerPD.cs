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
        if (_tiers.Length == 0 || xpValue <= 0f) return;

        // Tiers are sorted descending in Awake(), so index 0 is the highest tier. Past 5x that value,
        // per-tier splitting would balloon the orb count unboundedly (no tier above it to carry into),
        // so just represent the whole amount as a single orb instead of spawning dozens/hundreds. Passed
        // through as the raw float (not rounded) — an orb's value was always a float (XPOrbPD.SetValue),
        // only this call site was throwing precision away.
        var highestTier = _tiers[0];
        if (highestTier.value > 0 && xpValue > highestTier.value * 5)
        {
            SpawnOrb(hierarchy, highestTier.prefab, position, xpValue);
            return;
        }

        float remaining = xpValue;

        for (int i = 0; i < _tiers.Length; i++)
        {
            if (_tiers[i].value <= 0) continue;
            int count = Mathf.FloorToInt(remaining / _tiers[i].value);
            remaining -= count * _tiers[i].value;
            for (int j = 0; j < count; j++)
                SpawnOrb(hierarchy, _tiers[i].prefab, position, _tiers[i].value);
        }

        // Whatever's left is smaller than the lowest tier's value (e.g. a co-op/elite xpMultiplier didn't
        // land on a clean multiple of every denomination) — rather than silently drop it, fold it into one
        // extra orb using the lowest tier's prefab but the leftover's exact (fractional) value, so no XP
        // is ever lost to rounding. Epsilon guards against spawning a near-zero orb from float error.
        if (remaining > 0.001f)
        {
            for (int i = _tiers.Length - 1; i >= 0; i--)
            {
                if (_tiers[i].value <= 0) continue;
                SpawnOrb(hierarchy, _tiers[i].prefab, position, remaining);
                break;
            }
        }
    }

    private void SpawnOrb(PredictedHierarchy hierarchy, GameObject prefab, Vector3 position, float value)
    {
        if (prefab == null) return;
        var id = hierarchy.Create(prefab, position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out XPOrbPD orb))
            orb.SetValue(value);
    }
}
