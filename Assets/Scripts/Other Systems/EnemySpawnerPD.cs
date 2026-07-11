using PurrNet.Prediction;
using UnityEngine;

public class EnemySpawnerPD : PredictedIdentity<EnemySpawnerPD.State>
{
    public struct State : IPredictedData<State>
    {
        public bool isActive;
        public float spawnTimer;
        public void Dispose() { }
    }

    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] public float spawnInterval = 3f;

    public static EnemySpawnerPD instance;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    protected override State GetInitialState() => default;

    public void StartSpawning()
    {
        currentState.isActive = true;
        currentState.spawnTimer = spawnInterval;
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (!state.isActive) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        state.spawnTimer -= delta;
        if (state.spawnTimer <= 0f)
        {
            state.spawnTimer += spawnInterval;
            hierarchy.Create(enemyPrefab, new Vector3(0f, 0f, 20f), Quaternion.identity);
        }
    }
}
