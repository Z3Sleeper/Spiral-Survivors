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

    protected override State GetInitialState() => default;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        if (!state.isActive)
        {
            if (GameManagerPD.instance == null || !GameManagerPD.instance.GameStarted) return;
            state.isActive = true;
            state.spawnTimer = spawnInterval;
            return;
        }

        state.spawnTimer -= delta;
        if (state.spawnTimer <= 0f)
        {
            state.spawnTimer += spawnInterval;
            hierarchy.Create(enemyPrefab, new Vector3(0f, 0f, 20f), Quaternion.identity);
        }
    }
}
