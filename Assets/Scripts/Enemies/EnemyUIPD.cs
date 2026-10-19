using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class EnemyUIPD : MonoBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private TextMeshProUGUI healthText;

    private IEnemyPD _enemy;
    private Camera _camera;
    // Last values actually displayed — with up to ~500 concurrent enemies (see EnemySpawnerPD's own caps),
    // rebuilding healthText's string every frame regardless of whether health actually moved was a real,
    // chronic per-frame GC source (confirmed via Profiler: EnemyUIPD showing up as a top GC-allocation
    // contributor across several consecutive frames). Skipping the Slider/text write entirely when nothing
    // changed since last frame removes both the allocation and the redundant TMP rebuild for every enemy
    // that isn't currently taking damage, which is the overwhelming majority on any given frame.
    private float _lastCurrentHealth = -1f;
    private float _lastMaxHealth = -1f;

    // Whether this enemy's category should currently show a worldspace bar at all — see Update()'s own
    // comment for why this is tracked/re-evaluated every frame instead of decided once.
    private bool _lastShouldShow = true;

    private void Awake()
    {
        _enemy = GetComponentInParent<IEnemyPD>();
        _camera = Camera.main;
        if (healthBar != null) healthBar.value = 1f;
    }

    private void Update()
    {
        // No _isDead early-return — a kill-launch corpse stays visible (and its healthbar with it) for a
        // while after death, so the bar needs to keep updating to actually show 0/max instead of freezing
        // at whatever it last displayed the instant before the killing blow landed.
        if (_enemy == null) return;

        // Bosses already have a dedicated screenspace health bar UI (see BossHealthBarUI.cs) — this
        // worldspace one is redundant and, on something King Spike-sized, sits awkwardly high above the
        // model. Plebs are numerous/small enough that a per-instance floating bar is just visual clutter at
        // swarm scale. Both are a generic, category-based rule, checked every frame here (not once in
        // Awake()) for two reasons: spawnCategory isn't actually set yet at Awake()-time — ConfigureSpawn
        // only runs once hierarchy.Create() returns to the spawner, strictly AFTER Awake()/LateAwake() have
        // already run — and a pooled enemy can be a DIFFERENT category on its next life, so the decision has
        // to be able to flip back. Hides/shows this object's own CHILDREN (Background/Fill/Text), never
        // this GameObject itself — this script and its Slider both live directly on the same "HealthBar"
        // object (confirmed in the prefab), so disabling IT would also stop this very Update() from ever
        // running again, permanently stuck hidden even once the pooled slot is reused as a category that
        // should show a bar.
        // IsHiddenOnDeath added (2026-10-16) — the category check alone never hid this on death at all;
        // the bar previously stayed visible until the whole enemy GameObject finally got pooled/deleted
        // (verified-gated, much later), floating over an already-invisible corpse the whole time in between.
        // Deliberately checked here, not via an _isDead early-return above — IsHiddenOnDeath stays false for
        // the whole duration of a kill-launch (or any future death-hold effect), matching the model's own
        // timing exactly: the bar keeps updating/showing (correctly settling to 0/max, per this method's own
        // top comment) for as long as the corpse itself is still visibly held, then hides in the same frame
        // the model does, not before and not after.
        bool shouldShow = _enemy.SpawnCategory != EnemySpawnCategory.Boss && _enemy.SpawnCategory != EnemySpawnCategory.Pleb
            && !_enemy.IsHiddenOnDeath;
        if (shouldShow != _lastShouldShow)
        {
            _lastShouldShow = shouldShow;
            for (int i = 0; i < transform.childCount; i++)
                transform.GetChild(i).gameObject.SetActive(shouldShow);
        }
        if (!shouldShow) return;

        if (_enemy.MaxHealth <= 0f) return;

        float current = _enemy.CurrentHealth;
        float max = _enemy.MaxHealth;
        if (current == _lastCurrentHealth && max == _lastMaxHealth) return;
        _lastCurrentHealth = current;
        _lastMaxHealth = max;

        healthBar.value = current / max;
        healthText.text = $"<b>{FormatStat(current)}/{FormatStat(max)}</b>";
    }

    private void LateUpdate()
    {
        if (_camera == null) _camera = Camera.main;
        if (_camera != null) transform.rotation = _camera.transform.rotation;
    }

    private static string FormatStat(float value)
    {
        if (value >= 1_000_000_000_000f) return $"{value / 1_000_000_000_000f:F2}tril";
        if (value >= 1_000_000_000f)     return $"{value / 1_000_000_000f:F2}bil";
        if (value >= 1_000_000f)         return $"{value / 1_000_000f:F2}mil";
        if (value >= 1_000f)             return $"{value / 1_000f:F2}k";
        return $"{Mathf.RoundToInt(value)}";
    }
}
