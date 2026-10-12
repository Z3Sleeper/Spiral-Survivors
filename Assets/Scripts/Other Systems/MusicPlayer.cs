using System.Collections;
using UnityEngine;

// Rewritten 2026-10-12 — the old intro/loop-as-two-AudioSources approach (AudioSource.PlayScheduled, with
// the loop's own start time computed as introClip.samples/introClip.frequency past the intro's own
// PlayScheduled start) turned out to have a small but real, inherent scheduling jitter: PlayScheduled() is
// only accurate to within roughly one audio engine buffer cycle, not sample-perfect, and that's true across
// TWO separate AudioSources no matter how precisely the offset between them is computed — confirmed live,
// even with PCM clips, exact matching lengths, and DSP Buffer Size on Best Latency, the loop still started a
// hair before the intro's true end, clipping its tail, by a variable (not fixed) amount from playthrough to
// playthrough. A manual padding fudge-factor "worked" but was a bandaid against jitter, not a real fix.
//
// This version avoids cross-AudioSource scheduling entirely: intro+loop now live in ONE combined clip
// (musicTrack), played on a SINGLE AudioSource with a plain Play() call — no PlayScheduled() math needed at
// all for the intro->loop handoff, since it's one continuous audio stream with no cross-source handoff to
// get wrong in the first place. The repeating loop-back (once playback reaches the end of musicTrack) is
// handled by polling AudioSource.timeSamples each frame and, once it reaches the clip's own end, seeking
// (not stopping/restarting) back to introLengthSeconds's own sample position — a well-established Unity
// technique for gapless loop REGIONS within a single clip (stock Unity's AudioSource has no native "loop
// only part of this clip" feature the way middleware like FMOD/Wwise does). This has its own precision limit
// (bound by how often Update() polls, worst case ~1 frame late), but that failure mode is a few EXTRA ms of
// already-correct audio playing before the seek fires — not a cut, overlap, or audible gap — and since it's
// a direct timeSamples seek on an already-playing voice (not a stop+restart), there's no re-trigger
// click/ramp risk either.
public class MusicPlayer : MonoBehaviour
{
    public static MusicPlayer instance;

    [SerializeField] private AudioSource musicSource;
    // The combined intro+loop audio (one continuous file, no split) — see class doc comment for why this
    // replaced the old separate intro/loop AudioClip pair.
    [SerializeField] private AudioClip musicTrack;
    // Exact point (seconds into musicTrack) where the repeating loop section begins — i.e. the old intro
    // clip's own known length. 0 (no intro authored) degrades correctly on its own: _loopStartSample ends up
    // 0, so reaching the end just seeks back to the very start, same as looping the whole clip normally.
    [SerializeField] private float introLengthSeconds = 0f;

    private int _loopStartSample;
    // Gates the Update() poll below — only actually watch for/act on reaching the clip's end while this
    // MusicPlayer is the one that started it (never fires from a stale state after FadeOut/Stop, and never
    // runs before Play() has ever been called).
    private bool _isActive;
    // Wrap-detection fallback for Update()'s own poll — see its own doc comment.
    private int _lastTimeSamples;

    // fadeOutDuration — NOT reusing LobbySceneController.lockInGraceSeconds or
    // GameManagerPD.minimumLoadingScreenSeconds: the former is the one real guaranteed-alive window (the
    // Lobby scene, and this MusicPlayer with it, is still around for at least that long before a scene swap
    // can destroy it), but it's an unrelated Steam-lobby-timing constant on a different class, not a
    // general-purpose "loading screen minimum duration" anything else should couple to; the latter governs
    // the OPPOSITE side of a transition (inside the NEW scene, after the swap already happened), so it's not
    // applicable to fading out a scene that's about to be destroyed. Authored as its own field instead,
    // defaulted to 1.5s to match that one real guaranteed window. Fine if the actual scene swap (whose exact
    // timing depends on real connection speed past that point, not a fixed number) cuts this fade short —
    // explicitly accepted, same as a hard stop.
    [SerializeField] private float fadeOutDuration = 1.5f;
    private Coroutine _fadeRoutine;
    private float _baseVolume;

    private void Awake()
    {
        instance = this;
        musicSource.clip = musicTrack;
        // true, not false — see Update()'s own doc comment for why a non-looping source is the wrong
        // choice here: Unity's own real-time audio engine clock runs independently of Update()'s
        // frame-rate-limited polling, so a NON-looping source that reaches its true end between two
        // Update() calls auto-stops itself natively before the poll below ever gets a chance to redirect
        // it, permanently killing playback instead of looping. loop = true means the worst case if Unity's
        // own native wrap-to-0 ever wins that race is a brief continuation (caught and corrected next
        // frame), never a dead stop.
        musicSource.loop = true;
        _loopStartSample = musicTrack != null ? Mathf.RoundToInt(introLengthSeconds * musicTrack.frequency) : 0;
        // Captured once, pre-fade — FadeOut interpolates down FROM this, not from whatever
        // AudioSource.volume happens to already be (which a previous, interrupted FadeOut call could have
        // left partway down).
        _baseVolume = musicSource.volume;
    }

    // Not DontDestroyOnLoad — see LocalUIManagerPD.OnDestroy's own doc comment for the full reasoning
    // (guarded against nulling out a newer instance; without this, `instance` was left dangling on a
    // destroyed object across a scene return to lobby).
    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    public void Play()
    {
        if (musicTrack == null) return;
        musicSource.timeSamples = 0;
        musicSource.Play();
        _isActive = true;
        _lastTimeSamples = 0;
    }

    // Polls for "reached the end of musicTrack" and seeks back to the loop section's own start — see class
    // doc comment for why this replaces the old cross-AudioSource PlayScheduled() approach. Two checks, not
    // one:
    // 1. Proactive — timeSamples >= samples - 1 catches it just before Unity's own native end-of-clip wrap
    //    (loop = true) would fire, landing the seek precisely where intended on the common case.
    // 2. Reactive fallback — timeSamples < _lastTimeSamples catches it the frame AFTER Unity's own real-time
    //    audio engine clock (which runs independently of this frame-rate-limited poll) won the race and
    //    already wrapped to sample 0 on its own before check #1 above could catch it coming. Without this,
    //    the proactive check alone can still occasionally lose that race on a slow/hitching frame, in which
    //    case nothing would ever redirect it and the whole track would just silently replay from its own
    //    beginning (intro included) instead of looping only the intended section.
    private void Update()
    {
        if (!_isActive || !musicSource.isPlaying) return;

        int current = musicSource.timeSamples;
        if (current >= musicTrack.samples - 1 || current < _lastTimeSamples)
            current = musicSource.timeSamples = _loopStartSample;

        _lastTimeSamples = current;
    }

    // Called from LoadingScreen.Show() (every call site, not duplicated per-caller). Restartable — a second
    // Show() call (or a fresh Play() on a still-fading source, not that anything currently does that) just
    // stops the old coroutine and starts a fresh fade from wherever the volume currently sits, never
    // fighting itself.
    public void FadeOut()
    {
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeOutRoutine());
    }

    private IEnumerator FadeOutRoutine()
    {
        _isActive = false;
        float startVolume = musicSource.volume;
        float t = 0f;

        // Unscaled — same reasoning as every other loading-screen-adjacent timer (LoadingScreen's own dot/
        // hint cycling): this plays out while Time.timeScale could legitimately already be 0.
        while (t < fadeOutDuration)
        {
            t += Time.unscaledDeltaTime;
            musicSource.volume = Mathf.Lerp(startVolume, 0f, Mathf.Clamp01(t / fadeOutDuration));
            yield return null;
        }

        musicSource.Stop();
        musicSource.volume = _baseVolume;
        _fadeRoutine = null;
    }
}
