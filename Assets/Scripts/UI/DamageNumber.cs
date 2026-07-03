using System.Collections;
using TMPro;
using UnityEngine;

public class DamageNumber : MonoBehaviour
{
    [SerializeField] private TextMeshPro _text;
    [SerializeField] private float _floatSpeed = 2f;
    [SerializeField] private float _duration = 1f;

    private DamageNumberManager _manager;
    private Camera _cam;

    private void Awake() => _cam = Camera.main;

    public void Init(float amount, Vector3 worldPos, DamageNumberManager manager, Color color)
    {
        _manager = manager;
        transform.position = worldPos;
        _text.text = Mathf.RoundToInt(amount).ToString();
        _text.color = color;
        _text.alpha = 1f;
        gameObject.SetActive(true);
        StartCoroutine(Animate());
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
