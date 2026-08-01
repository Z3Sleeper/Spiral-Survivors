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

        // Scale itself is no longer set here — reviveArea/reviveProgressArea are parented under Visuals,
        // which scales normally with the character's own Size stat, so a one-time Awake() value would go
        // stale the instant a Size augment is picked afterward. Both now recompute live every UpdateView()
        // tick instead (see its own comment) — this just handles the one-time initial hide.
        if (reviveArea != null) reviveArea.SetActive(false);
        if (reviveProgressArea != null) reviveProgressArea.SetActive(false);
    }

    // reviveRadius is a fixed WORLD-unit gameplay value (see Simulate()'s own distance check) — dividing by
    // the object's live parent lossyScale.x cancels out whatever the character's current Size scaling has
    // accumulated up to that point, so the ring's actual world-space size always matches the real revive
    // radius exactly, same "divide out the live parent scale" technique PlayerRangeIndicatorPD already uses
    // for its own range ring and for the identical reason (Size augments shouldn't balloon/shrink something
    // that represents a fixed gameplay radius).
    private static float GetParentScale(GameObject go)
    {
        float scale = go.transform.parent != null ? go.transform.parent.lossyScale.x : 1f;
        return scale > 0f ? scale : 1f;
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

        // Recomputed every tick (not just on the isDead transition above) so a Size change mid-run — or
        // even mid-revive — never leaves either ring showing a stale radius. See GetParentScale's own
        // comment for why the division is needed at all.
        if (reviveArea != null)
        {
            float diameterScale = (reviveRadius / CylinderBaseRadius) / GetParentScale(reviveArea);
            var s = reviveArea.transform.localScale;
            reviveArea.transform.localScale = new Vector3(diameterScale, s.y, diameterScale);
        }

        if (reviveProgressArea != null && _stats.CurrentReviveTime > 0f)
        {
            float t = Mathf.Clamp01(viewState.chargeTimer / _stats.CurrentReviveTime);
            float diameterScale = (reviveRadius / CylinderBaseRadius) * t / GetParentScale(reviveProgressArea);
            var s = reviveProgressArea.transform.localScale;
            reviveProgressArea.transform.localScale = new Vector3(diameterScale, s.y, diameterScale);
        }
    }
}
