using PurrNet.Prediction;
using UnityEngine;

public class ReviveZonePD : PredictedIdentity<ReviveZonePD.State>
{
    public struct State : IPredictedData<State>
    {
        public float chargeTimer;
        public void Dispose() { }
    }

    [SerializeField] private GameObject reviveArea;
    [SerializeField] private GameObject reviveProgressArea;
    [SerializeField] private float reviveRadius = 5f;
    [SerializeField] private float baseChargeRate = 1f;

    // Unity's built-in Cylinder primitive has a radius of 0.5 (diameter 1) at localScale 1.
    private const float CylinderBaseRadius = 0.5f;

    private CharacterStatsPD _stats;
    private bool _wasDeadLastFrame;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStatsPD>();

        // Scale itself is no longer set here — reviveArea/reviveProgressArea are parented under a dedicated
        // anchor object registered in CharacterStatsPD.unscaledSizeChildren (2026-09-19), which keeps that
        // anchor's own WORLD scale pinned to its originally-authored value regardless of the character's
        // current Size — so these two children's own local scale already IS true world units directly, no
        // live per-frame counter-scaling needed here at all (see UpdateView()'s own comment for the formula
        // this replaced). This just handles the one-time initial hide.
        if (reviveArea != null) reviveArea.SetActive(false);
        if (reviveProgressArea != null) reviveProgressArea.SetActive(false);
    }

    // ── Simulation ─────────────────────────────────────────────────────────
    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        if (_stats == null || !_stats.IsDead)
        {
            state.chargeTimer = 0f;
            return;
        }

        // The downed player's own revive power is a flat bonus (e.g. +25% => 0.25) applied to every ally's own rate individually.
        float downedPlayerBonus = _stats.revivePower - 1f;

        float chargeRate = 0f;
        foreach (var ally in CharacterStatsPD.All.Values)
        {
            if (ally == _stats || ally.IsDead) continue;
            if (Vector3.Distance(ally.transform.position, transform.position) > reviveRadius) continue;
            chargeRate += baseChargeRate * (ally.revivePower + downedPlayerBonus);
        }

        if (chargeRate <= 0f) return;

        state.chargeTimer += delta * chargeRate;

        if (state.chargeTimer >= _stats.CurrentReviveTime)
        {
            _stats.Revive();
            state.chargeTimer = 0f;
        }
    }

    // ── View ───────────────────────────────────────────────────────────────
    protected override void UpdateView(State viewState, State? verified)
    {
        if (_stats == null) return;

        bool isDead = _stats.verifiedState?.isDead ?? false;
        if (isDead != _wasDeadLastFrame)
        {
            if (reviveArea != null) reviveArea.SetActive(isDead);
            if (reviveProgressArea != null) reviveProgressArea.SetActive(isDead);
            _wasDeadLastFrame = isDead;
        }

        // No parent-scale division needed anymore (2026-09-19) — reviveArea/reviveProgressArea now sit under
        // an anchor object whose own world scale CharacterStatsPD.unscaledSizeChildren already keeps pinned
        // to its authored value regardless of the character's current Size, so reviveRadius (a fixed
        // WORLD-unit gameplay value — see Simulate()'s own distance check) converts directly to a local
        // scale with no extra correction. Still recomputed every tick (not just the isDead transition above)
        // so a Size change mid-run never leaves either ring showing a stale radius.
        if (reviveArea != null)
        {
            float diameterScale = reviveRadius / CylinderBaseRadius;
            var s = reviveArea.transform.localScale;
            reviveArea.transform.localScale = new Vector3(diameterScale, s.y, diameterScale);
        }

        if (reviveProgressArea != null && _stats.CurrentReviveTime > 0f)
        {
            float t = Mathf.Clamp01(viewState.chargeTimer / _stats.CurrentReviveTime);
            float diameterScale = (reviveRadius / CylinderBaseRadius) * t;
            var s = reviveProgressArea.transform.localScale;
            reviveProgressArea.transform.localScale = new Vector3(diameterScale, s.y, diameterScale);
        }
    }
}
