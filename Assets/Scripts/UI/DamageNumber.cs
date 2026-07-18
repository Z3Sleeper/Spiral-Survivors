using System.Collections;
using TMPro;
using UnityEngine;

public class DamageNumber : MonoBehaviour
{
    [SerializeField] private TextMeshPro _text;
    // How high the arc rises above its start position at its peak (t=0.5) — it always returns to exactly
    // 0 extra height by the end of _duration, landing back at its original spawn Y.
    [SerializeField] private float _maxHeight = 1f;
    [SerializeField] private float _duration = 1f;
    // Total +Z drift accumulated linearly over the full lifetime — pushes each number slightly "backward"
    // over time so newer numbers spawning at the same spot render on top of older, already-drifted ones
    // instead of z-fighting/overlapping at identical depth.
    [SerializeField] private float _zDrift = 0.05f;

    // Priority (real weapon hits, Phantom Blade direct damage) prints at full size; everything else
    // (onhit-effect procs, etc.) prints at half size, so the "real" damage numbers visually stand out from
    // the onhit noise they trigger.
    [SerializeField] private float _fontSize = 8f;
    [SerializeField] private float _nonPrioritySize = 4f;

    // Colors live here (not passed in by callers) so every damage-number spawn site just reports what
    // happened (isCrit/isHighroll) and this prefab owns how that's presented — tweak these in the
    // Inspector without touching weapon/enemy code.
    [SerializeField] private Color _normalColor = Color.white;
    [SerializeField] private Color _critColor = Color.red;
    [SerializeField] private Color _highrollColor = new(0.2f, 0.9f, 0.85f);
    [SerializeField] private Color _critHighrollColor = new(1f, 0.4f, 0.85f);

    private DamageNumberManager _manager;
    private Camera _cam;
    private IEnemyPD _target;
    // Captured at Init() time — a raw IEnemyPD reference alone can't tell a still-alive enemy apart from
    // this same pooled GameObject having since been recycled for a completely different spawn, so tracking
    // is only trusted while _target.SpawnGeneration still matches this. See DamageNumberManager's own
    // IsStillTheSameEnemy for the queued-number equivalent of this same check.
    private int _targetGeneration;
    // World-space offset between where this number actually spawned (worldPos, already including
    // sizeYOffset/sizeZOffset/etc.) and the target's position at that moment — re-added to the target's
    // LIVE position every frame in Animate(), so the number keeps the same relative spawn offset while
    // tracking a moving enemy instead of drifting behind it.
    private Vector3 _followOffset;

    private void Awake() => _cam = Camera.main;

    // xOffset is the total X distance this number arcs toward over its lifetime (rolled by
    // DamageNumberManager as its spawn-time jitter roll) — not applied to worldPos directly, since the
    // arc itself is what moves it there over _duration rather than an instant spawn-position offset.
    public void Init(float amount, Vector3 worldPos, float xOffset, IEnemyPD target, DamageNumberManager manager, bool isCrit, bool isHighroll, bool isPriority)
    {
        _manager = manager;
        _target = target;
        bool targetAlive = !DamageNumberManager.IsDestroyed(target);
        _targetGeneration = targetAlive ? target.SpawnGeneration : 0;
        transform.position = worldPos;
        _followOffset = targetAlive ? worldPos - target.Visuals.position : Vector3.zero;
        _text.text = Mathf.RoundToInt(amount).ToString() + ResolveSuffix(isCrit, isHighroll);
        _text.color = ResolveColor(isCrit, isHighroll);
        _text.fontStyle = ResolveStyle(isCrit, isHighroll);
        _text.fontSize = isPriority ? _fontSize : _nonPrioritySize;
        _text.alpha = 1f;
        gameObject.SetActive(true);
        StartCoroutine(Animate(xOffset));
    }

    private Color ResolveColor(bool isCrit, bool isHighroll)
    {
        if (isCrit && isHighroll) return _critHighrollColor;
        if (isHighroll) return _highrollColor;
        if (isCrit) return _critColor;
        return _normalColor;
    }

    private string ResolveSuffix(bool isCrit, bool isHighroll)
    {
        string suffix = "";
        if (isCrit) suffix += "!";
        if (isHighroll) suffix += "!";
        return suffix;
    }

    // FontStyles is a [Flags] enum — crit alone is Bold+Italic, highroll alone is Bold, and crit+highroll
    // composes to the same Bold+Italic (the |= is idempotent) rather than needing a distinct "both" case.
    private FontStyles ResolveStyle(bool isCrit, bool isHighroll)
    {
        FontStyles style = FontStyles.Normal;
        if (isHighroll) style |= FontStyles.Bold;
        if (isCrit) style |= FontStyles.Bold | FontStyles.Italic;
        return style;
    }

    private IEnumerator Animate(float xOffset)
    {
        float elapsed = 0f;
        // Re-derived from _target's live position every frame below when still valid — this is only the
        // fallback once the target is gone (pooled/destroyed mid-animation), freezing at wherever it was
        // last tracked rather than snapping back to the very first spawn frame.
        Vector3 basePos = transform.position;

        while (elapsed < _duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / _duration;
            // DamageNumberManager.IsDestroyed, not a plain null check — a truly-destroyed enemy's IEnemyPD
            // reference still reads as non-null through a bare `_target != null` (Unity's "fake null"
            // operator only kicks in for the UnityEngine.Object static type, not an interface), which threw
            // MissingReferenceException the instant .Visuals touched its transform. SpawnGeneration on top
            // of that catches the separate pooled-reuse case: a dead-but-pooled (not destroyed) enemy keeps
            // a perfectly valid, non-destroyed reference even after its GameObject gets recycled for a
            // completely different spawn, so without this we'd silently start tracking whatever NEW enemy
            // now occupies that slot instead of freezing where the original one died.
            if (!DamageNumberManager.IsDestroyed(_target) && _target.SpawnGeneration == _targetGeneration)
                basePos = _target.Visuals.position + _followOffset;
            // sin(t * pi) is 0 at t=0 and t=1 and peaks at _maxHeight at t=0.5 — rises then falls back to
            // exactly the start height by the end, same arc-bob shape as LeaperGloomPD's leap. X moves
            // linearly toward xOffset over the same duration, so the combined path traces an arc/parabola
            // rather than a straight vertical float.
            float arc = Mathf.Sin(t * Mathf.PI) * _maxHeight;
            transform.position = basePos + new Vector3(xOffset * t, arc, _zDrift * t);
            _text.alpha = 1f - t;
            if (_cam != null) transform.forward = _cam.transform.forward;
            yield return null;
        }

        gameObject.SetActive(false);
        _manager.Return(this);
    }
}
