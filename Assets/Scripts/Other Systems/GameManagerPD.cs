using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class GameManagerPD : PredictedIdentity<GameManagerPD.State>
{
    public struct State : IPredictedData<State>
    {
        public bool gameStarted;
        public float currentXP;
        public int level;
        public float xpToNextLevel;
        public float upgradePoints;
        public void Dispose() { }
    }

    public static GameManagerPD instance;

    [Header("XP")]
    [SerializeField] private float baseXPToNextLevel = 20f;
    [SerializeField] private float xpLevelStep = 5f;
    // TODO: move onto EnemySpawnerPD once a real spawner exists — this is where the old system kept it,
    // since it's really an enemy-pacing knob that happens to also scale the XP curve.
    [SerializeField] private float coopSpawnTimeScalar = 1f;

    [Header("Upgrade Points")]
    // Lets a harder level award more points per kill without having to also inflate enemy XP values
    // (which would throw off the level-up pacing) for balance purposes.
    [SerializeField] private float upgradePointMultiplier = 1f;
    // Committed periodically rather than only at game end, so a player who disconnects mid-run keeps
    // whatever was earned up through the last commit instead of walking away with nothing. Kept fairly
    // infrequent since ProgressionManager.CommitPoints does a synchronous file write — too cheap to
    // matter every few seconds, too expensive to do on every single kill in a horde swarm.
    [SerializeField] private float pointCommitInterval = 5f;

    public bool  GameStarted   => currentState.gameStarted;
    public float CurrentXP     => currentState.currentXP;
    public int   Level         => currentState.level;
    public float XPToNextLevel => currentState.xpToNextLevel;
    public float UpgradePoints => currentState.upgradePoints;

    private bool _wasGameStartedLastFrame;
    private float _committedUpgradePoints;
    private float _pointCommitTimer;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    protected override State GetInitialState() => new()
    {
        level = 1,
        xpToNextLevel = baseXPToNextLevel,
    };

    // ── Game start ──────────────────────────────────────────────────────────

    protected override void Simulate(ref State state, float delta)
    {
        if (state.gameStarted) return;

        var nm = NetworkManager.main;
        if (nm == null || nm.playerCount == 0) return;
        if (CharacterStatsPD.All.Count < nm.playerCount) return;

        state.gameStarted = true;
    }

    // ── Upgrade points ─────────────────────────────────────────────────────

    // Called from EnemyBasePD.UpdateView() off a verified death transition — never from Simulate(),
    // same reasoning as OnLevelUp()/ApplyStatAugmentPick(): this is a += accumulation, and calling it
    // from Simulate() risks the client's routine replay pass double-firing it for the same kill.
    public void AwardUpgradePoints(float baseAmount) => currentState.upgradePoints += baseAmount * upgradePointMultiplier;

    // Commits only the delta earned since the last commit (ProgressionManager.CommitPoints is additive)
    // to this machine's own local save. Called periodically below and once more at game end
    // (ReturnToLobbyManager) as a final safety net for whatever accrued since the last periodic tick.
    public void FlushUpgradePoints()
    {
        float verifiedPoints = verifiedState?.upgradePoints ?? _committedUpgradePoints;
        float delta = verifiedPoints - _committedUpgradePoints;
        if (delta <= 0f) return;

        _committedUpgradePoints = verifiedPoints;
        ProgressionManager.CommitPoints((long)Mathf.Round(delta));
    }

    // ── XP ─────────────────────────────────────────────────────────────────

    // Called from XPOrbPD.UpdateView() off a verified collection transition — deterministic, so every
    // machine applies the exact same XP/level result.
    public void AwardXP(float value)
    {
        int playerCount = CharacterStatsPD.All.Count;
        if (playerCount <= 0) return;

        float totalMod = 0f;
        foreach (var stats in CharacterStatsPD.All.Values)
            totalMod += stats.XPModifier;
        float avgXPMod = totalMod / playerCount;

        float coopXPPenalty = 1f + (playerCount - 1) * coopSpawnTimeScalar;
        if (coopXPPenalty <= 0f) coopXPPenalty = 1f;

        currentState.currentXP += value * avgXPMod / coopXPPenalty;

        while (currentState.currentXP >= currentState.xpToNextLevel)
        {
            currentState.currentXP -= currentState.xpToNextLevel;
            currentState.xpToNextLevel += xpLevelStep * ((currentState.level / 5) + 1);
            currentState.level++;
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!verified.HasValue) return;
        var v = verified.Value;

        if (v.gameStarted && !_wasGameStartedLastFrame)
        {
            if (LoadingScreen.instance != null) LoadingScreen.instance.Hide();
            if (MusicPlayer.instance != null) MusicPlayer.instance.Play();
        }
        _wasGameStartedLastFrame = v.gameStarted;

        _pointCommitTimer += Time.deltaTime;
        if (_pointCommitTimer >= pointCommitInterval)
        {
            _pointCommitTimer = 0f;
            FlushUpgradePoints();
        }
    }
}
