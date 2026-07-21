using TMPro;
using UnityEngine;

public class LoadingScreen : MonoBehaviour
{
    [SerializeField] private GameObject canvasGameObject;
    [SerializeField] private TextMeshProUGUI loadingText;
    [SerializeField] private string baseText = "Loading";
    [SerializeField] private float dotInterval = 0.4f;
    [SerializeField] private int maxDots = 3;

    public static LoadingScreen instance;

    // Cycles 0..maxDots, wrapping back to 0 (bare "Loading") rather than resetting straight to maxDots —
    // just a plain incrementing counter, not predicted/networked state, since this is purely cosmetic and
    // every machine's own loading screen is local anyway.
    private float _dotTimer;
    private int _dotCount;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        // Only ticks while actually shown — cheap either way, but no reason to keep animating text nobody
        // can see.
        if (loadingText == null || canvasGameObject == null || !canvasGameObject.activeSelf) return;

        // Unscaled — the loading screen can be up during moments Time.timeScale is 0 (e.g. mid pause-menu
        // transition), and the dots should keep animating regardless so it never LOOKS frozen, which is the
        // entire point of this.
        _dotTimer += Time.unscaledDeltaTime;
        if (_dotTimer < dotInterval) return;
        _dotTimer -= dotInterval;

        _dotCount = (_dotCount + 1) % (maxDots + 1);
        loadingText.text = baseText + new string('.', _dotCount);
    }

    public void Show()
    {
        canvasGameObject.SetActive(true);
        _dotTimer = 0f;
        _dotCount = 0;
        if (loadingText != null) loadingText.text = baseText;
    }

    public void Hide() => canvasGameObject.SetActive(false);
}