using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class PausePD : PredictedIdentity<PausePD.State>
{
    public static PausePD instance;
    public static event System.Action<bool> OnPauseChanged;
    public static event System.Action<int, int> OnReadyCountChanged;
    public static event System.Action<int> OnCountdownChanged;
    public static event System.Action<int, bool> OnFallbackTimerChanged;

    // Exposed (not const) so these can be tuned down in the Inspector for local multiplayer testing
    // (fast-forwarding through the countdowns instead of waiting out 3s/30s every time).
    [SerializeField] private float countdownDuration = 3f;
    // Once at least one player has readied up (but not everyone), this counts down so a single AFK/
    // stuck player can't hold the whole group paused indefinitely — force-unpauses when it expires.
    [SerializeField] private float readyFallbackDuration = 30f;

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public bool isPaused;
        public float pauseCountdown;
        public float readyFallbackTimer;
        public DisposableList<PlayerID> readyPlayers;
        public void Dispose() => readyPlayers.Dispose();

        // Was missing entirely — every rollback snapshot fell back to the slow serialize/deserialize
        // round-trip instead of this cheap direct field-copy. See EnemyBaseState.Duplicate's own doc
        // comment for the full reasoning.
        public State Duplicate()
        {
            var copy = this;
            copy.readyPlayers = readyPlayers.Duplicate();
            return copy;
        }
    }

    // Also true while a level-up needs picks, or the game has ended (won/lost) — folding both in here
    // means every existing IsPaused check across the codebase (enemies, weapons, orbs, the spawner, etc.)
    // automatically halts gameplay for those too, without touching each of those systems individually.
    public bool IsPaused => currentState.isPaused
        || (AugmentManagerPD.instance != null && AugmentManagerPD.instance.IsPresenting)
        || (GameManagerPD.instance != null && (GameManagerPD.instance.HasWon || GameManagerPD.instance.IsGameOver));

    private bool _pendingToggle;

    private bool _prevIsPaused;
    private int _prevReadyCount = -1;
    private int _prevCountdownSecs = -1;
    private int _prevFallbackSecs = -1;
    private bool _prevFallbackVisible;

    protected override void LateAwake() => instance = this;

    protected override State GetInitialState() => new()
    {
        readyFallbackTimer = readyFallbackDuration,
        readyPlayers = DisposableList<PlayerID>.Create(4)
    };

    public void RequestToggle() => _pendingToggle = true;

    // Called directly from PlayerControllerPD.Simulate() — mutates our own predicted State immediately,
    // same cross-identity pattern as CharacterStatsPD.StartSurge()/AugmentManagerPD.SubmitPick(). The
    // previous version staged deltas in a plain (non-State) Dictionary and drained it here, which only
    // produced correct results if every player's Simulate() ran before this one in the same tick — an
    // ordering assumption, not a guarantee, and it didn't roll back cleanly on replay since the
    // dictionary wasn't part of State. That caused a brief, client-only mispredicted unpause.
    public void SetPlayerReady(PlayerID player, bool ready)
    {
        for (int i = 0; i < currentState.readyPlayers.Count; i++)
        {
            if (currentState.readyPlayers[i] != player) continue;
            if (!ready) currentState.readyPlayers.RemoveAt(i);
            return;
        }
        if (ready) currentState.readyPlayers.Add(player);
    }

    protected override void Simulate(ref State state, float delta)
    {
        // ── Level-up / win / loss screens override any manual pause entirely ────────────────────────────
        // Gameplay stays frozen either way — IsPaused folds these in independently of state.isPaused — but
        // the separate PAUSED overlay (LocalUIManagerPD listens for OnPauseChanged, which fires off
        // state.isPaused specifically) would otherwise linger on top of the level-up screen, unresumable,
        // if the player happened to pause manually right as a level-up (or the game ending) landed. Also
        // discards any pending manual-pause toggle/countdown so a NEW pause can't engage while one of these
        // is up either — same "the more important thing wins" reasoning IsPaused itself already uses.
        if ((AugmentManagerPD.instance != null && AugmentManagerPD.instance.IsPresenting)
            || (GameManagerPD.instance != null && (GameManagerPD.instance.HasWon || GameManagerPD.instance.IsGameOver)))
        {
            state.isPaused = false;
            state.pauseCountdown = 0f;
            state.readyPlayers.Clear();
            _pendingToggle = false;
            return;
        }

        // ── Countdown ──────────────────────────────────────────────────────
        if (state.pauseCountdown > 0f)
        {
            state.pauseCountdown -= delta;
            if (state.pauseCountdown <= 0f)
            {
                state.pauseCountdown = 0f;
                state.isPaused = true;
                state.readyPlayers.Clear();
            }
            return;
        }

        // ── Ready-up (multiplayer unpause) ─────────────────────────────────
        if (state.isPaused)
        {
            int playerCount = NetworkManager.main != null ? NetworkManager.main.playerCount : 1;
            if (state.readyPlayers.Count >= playerCount)
            {
                state.isPaused = false;
                state.readyPlayers.Clear();
                state.readyFallbackTimer = readyFallbackDuration;
                return;
            }

            if (state.readyPlayers.Count > 0)
            {
                state.readyFallbackTimer -= delta;
                if (state.readyFallbackTimer <= 0f)
                {
                    state.isPaused = false;
                    state.readyPlayers.Clear();
                    state.readyFallbackTimer = readyFallbackDuration;
                    return;
                }
            }
            else
            {
                // Nobody's readied up yet — keep the fallback window fresh until someone actually starts it.
                state.readyFallbackTimer = readyFallbackDuration;
            }
        }

        // ── Toggle ─────────────────────────────────────────────────────────
        if (!_pendingToggle) return;
        _pendingToggle = false;

        bool isSinglePlayer = NetworkManager.main == null || NetworkManager.main.playerCount <= 1;

        if (state.isPaused)
        {
            if (!isSinglePlayer) return;
            state.isPaused = false;
        }
        else
        {
            if (isSinglePlayer)
            {
                state.isPaused = true;
                state.readyPlayers.Clear();
            }
            else
            {
                state.pauseCountdown = countdownDuration;
            }
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        var s = verified ?? viewState;
        int playerCount = NetworkManager.main != null ? NetworkManager.main.playerCount : 1;

        int countdownSecs = s.pauseCountdown > 0f ? Mathf.CeilToInt(s.pauseCountdown) : 0;
        if (countdownSecs != _prevCountdownSecs)
        {
            _prevCountdownSecs = countdownSecs;
            OnCountdownChanged?.Invoke(countdownSecs);
        }

        // Ready count / pause-screen visibility react to the local player's own button press, so they're
        // read off currentState (the raw latest predicted tick) instead — verified lags a full network
        // round trip behind the press, and viewState is smoothed/interpolated and lags too. Safe to read
        // raw here now that readyPlayers is real predicted State mutated idempotently through
        // SetPlayerReady() (the old ordering-dependent delta dictionary was what caused a genuine
        // misprediction flash; this isn't that).
        int readyCount = currentState.readyPlayers.Count;
        if (readyCount != _prevReadyCount)
        {
            _prevReadyCount = readyCount;
            OnReadyCountChanged?.Invoke(readyCount, playerCount);
        }

        // Same instant-feedback reasoning as ready count — this is a shared countdown visible to
        // everyone, but reading currentState keeps it visually consistent with the ready count it's
        // displayed alongside instead of lagging a network round trip behind it.
        bool fallbackVisible = currentState.isPaused && currentState.readyPlayers.Count > 0;
        int fallbackSecs = fallbackVisible ? Mathf.CeilToInt(currentState.readyFallbackTimer) : 0;
        if (fallbackVisible != _prevFallbackVisible || fallbackSecs != _prevFallbackSecs)
        {
            _prevFallbackVisible = fallbackVisible;
            _prevFallbackSecs = fallbackSecs;
            OnFallbackTimerChanged?.Invoke(fallbackSecs, fallbackVisible);
        }

        if (currentState.isPaused == _prevIsPaused) return;
        _prevIsPaused = currentState.isPaused;
        OnPauseChanged?.Invoke(currentState.isPaused);
    }
}
