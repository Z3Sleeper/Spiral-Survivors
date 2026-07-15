using UnityEngine;

// Upgrade points earned this session are a purely local, per-client save-file concern — nothing about
// "how many points does my save file need" benefits from being deterministic/networked/predicted, since
// every machine already verifies the exact same enemy deaths independently and would arrive at the same
// number anyway. This used to be tracked as PurrDiction predicted State (GameManagerPD.upgradePoints,
// shared across machines) plus a per-player "committed so far" predicted field on CharacterStatsPD, with
// the flush triggered by ReturnToLobbyManager — a plain NetworkIdentity reaching into a PredictedIdentity
// from outside the tick/view pipeline entirely, which was a real source of bugs. Living outside
// PurrDiction entirely as a plain, self-contained, non-networked object sidesteps that whole class of
// problem: nothing here can ever be rolled back, reconciled, or replayed out from under itself.
public class LocalUpgradePointsTracker : MonoBehaviour
{
    private static LocalUpgradePointsTracker _instance;
    public static LocalUpgradePointsTracker instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject(nameof(LocalUpgradePointsTracker));
                _instance = go.AddComponent<LocalUpgradePointsTracker>();
            }
            return _instance;
        }
    }

    [SerializeField] private float flushInterval = 5f;
    private float _flushTimer;
    private float _pendingPoints;

    // Session-total, for HUD display — never reset by a flush, only grows.
    public float TotalEarnedThisSession { get; private set; }

    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;
    }

    // Called from GameManagerPD.AwardUpgradePoints(), itself called from EnemyBasePD.UpdateView() off a
    // verified death transition — already reliably edge-triggered exactly once per kill per machine, so
    // no extra dedup is needed here.
    public void AddPoints(float amount)
    {
        if (amount <= 0f) return;
        _pendingPoints += amount;
        TotalEarnedThisSession += amount;
    }

    private void Update()
    {
        _flushTimer += Time.unscaledDeltaTime;
        if (_flushTimer < flushInterval) return;
        _flushTimer = 0f;
        Flush();
    }

    // Safe to call directly from anywhere, anytime (including a plain NetworkIdentity's RPC handler) —
    // this is plain, synchronous, non-networked code with no tick/rollback timing to worry about.
    public void Flush()
    {
        if (_pendingPoints <= 0f) return;
        ProgressionManager.CommitPoints((long)Mathf.Round(_pendingPoints));
        _pendingPoints = 0f;
    }
}
