using PurrNet.Prediction;
using UnityEngine;

// Fanfare-scatter model (replaced the old "currency change" denomination system 2026-08-16): every kill
// spawns a FIXED count of orbs for its category (1 for Pleb/Normal, eliteOrbCount for Elite, bossOrbCount
// for Boss), each carrying an even split of the kill's total xpValue — no remainder/leftover handling
// needed at all (a plain float division, unlike the old fixed-denomination prefabs which had to fold
// leftovers into an extra orb). WHICH prefab spawns is looked up per (tier, category) from tier1-4 below,
// mirroring EnemySpawnerPD's own TierEnemies/tier1-4 shape exactly. Scatter itself (each orb's own little
// random-direction impulse on spawn) already lives on XPOrbPD — this class only decides count/value/prefab.
public class XPOrbManagerPD : MonoBehaviour
{
    public static XPOrbManagerPD instance;

    [System.Serializable]
    private struct TierOrbs
    {
        public GameObject pleb;
        public GameObject normal;
        public GameObject elite;
        public GameObject boss;
    }

    [SerializeField] private TierOrbs tier1;
    [SerializeField] private TierOrbs tier2;
    [SerializeField] private TierOrbs tier3;
    [SerializeField] private TierOrbs tier4;

    // How many orbs an Elite/Boss kill's xpValue gets split evenly across — Pleb/Normal are always exactly
    // 1 (the enemy's full xpValue, no split needed), not separately tunable fields since neither category
    // has ever needed one.
    [SerializeField] private int eliteOrbCount = 5;
    [SerializeField] private int bossOrbCount = 20;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    // tier is the enemy's own EnemyBasePD.spawnTier (1-based) — mirrors EnemySpawnerPD.GetCurrentTier's own
    // bossesDefeated->tier mapping (tier1=1, tier2=2, tier3=3, tier4=4+), clamping anything beyond 4 down
    // to tier4 the same way.
    public void SpawnOrbs(PredictedHierarchy hierarchy, Vector3 position, float xpValue, EnemySpawnCategory category, int tier)
    {
        if (xpValue <= 0f) return;

        var tierOrbs = tier switch
        {
            1 => tier1,
            2 => tier2,
            3 => tier3,
            _ => tier4,
        };

        GameObject prefab = category switch
        {
            EnemySpawnCategory.Pleb => tierOrbs.pleb,
            EnemySpawnCategory.Elite => tierOrbs.elite,
            EnemySpawnCategory.Boss => tierOrbs.boss,
            _ => tierOrbs.normal,
        };
        if (prefab == null) return;

        int count = category switch
        {
            EnemySpawnCategory.Elite => Mathf.Max(1, eliteOrbCount),
            EnemySpawnCategory.Boss => Mathf.Max(1, bossOrbCount),
            _ => 1,
        };

        float perOrb = xpValue / count;
        for (int i = 0; i < count; i++)
            SpawnOrb(hierarchy, prefab, position, perOrb);
    }

    private void SpawnOrb(PredictedHierarchy hierarchy, GameObject prefab, Vector3 position, float value)
    {
        var id = hierarchy.Create(prefab, position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out XPOrbPD orb))
            orb.SetValue(value);
    }
}
