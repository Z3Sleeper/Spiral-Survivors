using System.Collections;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class EnemySpawner : NetworkIdentity
{
    public static EnemySpawner instance;

    [SerializeField] private List<GameObject> enemyPrefabs;
    [SerializeField] private float spawnMinDistance = 5f;
    [SerializeField] private float spawnMaxDistance = 10f;
    [SerializeField] private float spawnInterval = 1f;
    [SerializeField] private bool waitForPlayersLoaded = true;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        if (!isServer) return;

        if (!waitForPlayersLoaded)
            StartSpawning();
    }

    public void StartSpawning()
    {
        StartCoroutine(SpawnLoop());
    }

    private IEnumerator SpawnLoop()
    {
        while (true)
        {
            SpawnEnemy(enemyPrefabs[Random.Range(0, enemyPrefabs.Count)]);
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    public void SpawnEnemy(GameObject enemy)
    {
        Vector3 randomPos = new Vector3(
            Random.Range(-spawnMaxDistance, spawnMaxDistance),
            0,
            Random.Range(-spawnMaxDistance, spawnMaxDistance)
        );

        if (Mathf.Abs(randomPos.x) < spawnMinDistance)
            randomPos.x = spawnMinDistance * Mathf.Sign(randomPos.x);
        if (Mathf.Abs(randomPos.z) < spawnMinDistance)
            randomPos.z = spawnMinDistance * Mathf.Sign(randomPos.z);

        Instantiate(enemy, randomPos, Quaternion.identity);
    }
}