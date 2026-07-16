using System.Collections;
using TMPro;
using UnityEngine;

public class DamageNumber : MonoBehaviour
{
    [SerializeField] private TextMeshPro _text;
    [SerializeField] private float _floatSpeed = 2f;
    [SerializeField] private float _duration = 1f;

    // Colors live here (not passed in by callers) so every damage-number spawn site just reports what
    // happened (isCrit/isHighroll) and this prefab owns how that's presented — tweak these in the
    // Inspector without touching weapon/enemy code.
    [SerializeField] private Color _normalColor = Color.white;
    [SerializeField] private Color _critColor = Color.red;
    [SerializeField] private Color _highrollColor = new(0.2f, 0.9f, 0.85f);
    [SerializeField] private Color _critHighrollColor = new(1f, 0.4f, 0.85f);

    private DamageNumberManager _manager;
    private Camera _cam;

    private void Awake() => _cam = Camera.main;

    public void Init(float amount, Vector3 worldPos, DamageNumberManager manager, bool isCrit, bool isHighroll)
    {
        _manager = manager;
        transform.position = worldPos;
        _text.text = Mathf.RoundToInt(amount).ToString() + ResolveSuffix(isCrit, isHighroll);
        _text.color = ResolveColor(isCrit, isHighroll);
        _text.fontStyle = ResolveStyle(isCrit, isHighroll);
        _text.alpha = 1f;
        gameObject.SetActive(true);
        StartCoroutine(Animate());
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

    // FontStyles is a [Flags] enum — Bold | Italic composes on its own, no separate "both" case needed.
    private FontStyles ResolveStyle(bool isCrit, bool isHighroll)
    {
        FontStyles style = FontStyles.Normal;
        if (isHighroll) style |= FontStyles.Bold;
        if (isCrit) style |= FontStyles.Italic;
        return style;
    }

    private IEnumerator Animate()
    {
        float elapsed = 0f;
        Vector3 startPos = transform.position;

        while (elapsed < _duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / _duration;
            transform.position = startPos + Vector3.up * (_floatSpeed * t);
            _text.alpha = 1f - t;
            if (_cam != null) transform.forward = _cam.transform.forward;
            yield return null;
        }

        gameObject.SetActive(false);
        _manager.Return(this);
    }
}
