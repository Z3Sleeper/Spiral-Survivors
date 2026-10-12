using System.Collections;
using TMPro;
using UnityEngine;

public class LoadingScreen : MonoBehaviour
{
    [SerializeField] private GameObject canvasGameObject;
    [SerializeField] private TextMeshProUGUI loadingText;
    [SerializeField] private string baseText = "Loading";
    [SerializeField] private float dotInterval = 0.4f;
    [SerializeField] private int maxDots = 3;

    [Header("Hints")]
    [SerializeField] private TextMeshProUGUI hintText;
    [SerializeField] private string[] hints;
    [SerializeField] private float hintInterval = 5f;

    [Header("Spinner")]
    [SerializeField] private RectTransform spinnerTransform;
    [SerializeField] private float spinnerDegreesPerSecond = 180f;

    public static LoadingScreen instance;

    // Backed directly by the canvas's own active state (not a separate tracked bool) — a logical "are we
    // currently blocking the player" signal that button-click guards check, independent of whatever
    // rendering layer actually makes the canvas visible. If Show()/Hide() are ever called correctly but the
    // canvas somehow fails to visually render for some other reason, this still correctly reflects the
    // logical state and still blocks input — see CharacterSelectUI/LobbySceneController's button handlers.
    public bool IsShowing => canvasGameObject != null && canvasGameObject.activeSelf;

    // Cycles 0..maxDots, wrapping back to 0 (bare "Loading") rather than resetting straight to maxDots —
    // just a plain incrementing counter, not predicted/networked state, since this is purely cosmetic and
    // every machine's own loading screen is local anyway.
    private float _dotTimer;
    private int _dotCount;

    private Coroutine _hintRoutine;
    // Excluded from the next pick (when there's more than one hint to choose from) so the same hint never
    // shows twice in a row.
    private int _lastHintIndex = -1;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        // Only ticks while actually shown — cheap either way, but no reason to keep animating text/spinner
        // nobody can see.
        if (canvasGameObject == null || !canvasGameObject.activeSelf) return;

        // Unscaled, same reasoning as the dot/spinner timers below — the loading screen can be up during a
        // Time.timeScale == 0 moment (e.g. mid pause-menu transition) and should never visually freeze.
        // Negative Z so it spins clockwise (positive Z rotation is counter-clockwise in Unity's standard
        // screen-space convention) — Stuart's own explicit "Clockwise!" spec.
        if (spinnerTransform != null)
            spinnerTransform.Rotate(0f, 0f, -spinnerDegreesPerSecond * Time.unscaledDeltaTime);

        if (loadingText == null) return;

        _dotTimer += Time.unscaledDeltaTime;
        if (_dotTimer < dotInterval) return;
        _dotTimer -= dotInterval;

        _dotCount = (_dotCount + 1) % (maxDots + 1);
        loadingText.text = baseText + new string('.', _dotCount);
    }

    // Idempotent while already showing — several independent call sites (CharacterSelectUI's own instant
    // "my ready click just completed the lobby" feedback, LobbySceneController's official host/client
    // ready-confirmation reactions) can legitimately all call this for the SAME loading-screen session
    // moments apart, by design (see each call site's own doc comment). Restarting the dot/hint cycle on
    // every one of those redundant calls used to be harmless when Show() only reset a cosmetic timer, but
    // once hint-cycling was added a second Show() call would yank away whatever hint the first one just
    // displayed a fraction of a second earlier (confirmed live) — so a real re-entry into an ALREADY-shown
    // screen is now a no-op, same "harmless to call repeatedly" contract this always had, just genuinely
    // true again.
    public void Show()
    {
        if (canvasGameObject.activeSelf) return;

        canvasGameObject.SetActive(true);
        _dotTimer = 0f;
        _dotCount = 0;
        if (loadingText != null) loadingText.text = baseText;

        // Fades out whichever scene's MusicPlayer is currently alive (2026-10-12) — hooked here, once, so
        // every one of this method's own call sites gets it for free rather than needing the same call
        // duplicated at each. Relies on this method's own idempotent early-return above to only actually
        // fire once per real show, same contract the hint-cycle restart fix right above already depends on.
        MusicPlayer.instance?.FadeOut();

        if (_hintRoutine != null) StopCoroutine(_hintRoutine);
        if (hintText != null && hints != null && hints.Length > 0)
            _hintRoutine = StartCoroutine(CycleHints());
    }

    // Minimum-display-time enforcement lives in GameManagerPD's synced State (loadingScreenTimer/
    // loadingScreenRevealed) instead of here — that's what guarantees every machine reveals at the exact
    // same simulated tick, single source of truth, no per-machine local timer to drift out of sync (e.g. a
    // client whose own Show() call happened later than the host's due to Steam lobby data propagation
    // delay). This Hide() is just the dumb, immediate view-layer call every path (that reveal included)
    // uses.
    public void Hide()
    {
        if (_hintRoutine != null) { StopCoroutine(_hintRoutine); _hintRoutine = null; }
        if (hintText != null) hintText.text = string.Empty;
        canvasGameObject.SetActive(false);
    }

    // Shows a random hint immediately, then a fresh random one every hintInterval seconds for as long as
    // the loading screen stays up — stopped by Hide() (or a fresh Show() restarting it), same coroutine-
    // per-Show() lifecycle as everything else here.
    private IEnumerator CycleHints()
    {
        while (true)
        {
            ShowRandomHint();
            // Unscaled — same reasoning as the dot animation's own timer: the loading screen can be up
            // during a Time.timeScale == 0 moment, and hints should keep cycling regardless.
            yield return new WaitForSecondsRealtime(hintInterval);
        }
    }

    private void ShowRandomHint()
    {
        int index = Random.Range(0, hints.Length);
        while (hints.Length > 1 && index == _lastHintIndex)
            index = Random.Range(0, hints.Length);

        _lastHintIndex = index;
        hintText.text = hints[index];
    }
}
