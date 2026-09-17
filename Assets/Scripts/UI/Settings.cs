using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Audio;
using TMPro;

public class Settings : MonoBehaviour
{
    [SerializeField] private Slider volumeSlider;
    [SerializeField] private TMP_InputField volumeInput;
    [SerializeField] private Slider sfxVolumeSlider;
    [SerializeField] private TMP_InputField sfxVolumeInput;
    [SerializeField] private Slider musicVolumeSlider;
    [SerializeField] private TMP_InputField musicVolumeInput;
    [SerializeField] private TMP_Dropdown framerateDropdown;
    [SerializeField] private Toggle seeOtherPlayerDamageToggle;
    [SerializeField] private Toggle controlsVisibilityToggle;
    [SerializeField] private Toggle hideExpandedUIToggle;
    // GameAudioMixer — SFX/Music route through its exposed SFXVolume/MusicVolume float parameters (see
    // SFXMixerParam/MusicMixerParam below); Master stays on AudioListener.volume since Master's own group
    // volume isn't exposed to script.
    [SerializeField] private AudioMixer audioMixer;

    private const string VolumeKey = "MasterVolume";
    private const string SFXVolumeKey = "SFXVolume";
    private const string MusicVolumeKey = "MusicVolume";
    private const string FramerateKey = "TargetFramerate";
    private const string SeeOtherPlayerDamageKey = "SeeOtherPlayerDamage";
    // Renamed from ControlsVisibilityKey/"ControlsVisibility" (2026-09-03, alongside HideControls' own
    // rename below) — a fresh key, not just a fresh default, so nobody's already-saved preference under the
    // OLD positive polarity gets silently reinterpreted backwards by the new inverted read.
    private const string ControlsHiddenKey = "ControlsHidden";
    private const string HideExpandedUIKey = "HideExpandedUI";
    private static readonly int[] FramerateOptions = { 30, 60, 120, 144, -1 };

    // Must match the exposed parameter names on GameAudioMixer.mixer exactly (Edit > Expose to Script, or
    // already-exposed — check the mixer's own Exposed Parameters list before renaming either side).
    private const string SFXMixerParam = "SFXVolume";
    private const string MusicMixerParam = "MusicVolume";
    // 0% maps to this instead of -infinity (Log10(0) is undefined) — matches the mixer's own
    // m_SuspendThreshold, effectively silent either way.
    private const float MinDb = -80f;
    // Master — a fresh install (no saved MasterVolume PlayerPrefs key) opens at 100%, matching SFX/Music's
    // own 100% default (see their own PlayerPrefs.GetFloat calls below). Was 80f; changed 2026-09-03 per
    // explicit "volumes maxed by default" request.
    private const float MasterDefaultPercent = 100f;

    // Default true/checked (2026-09-03, was false/opt-in) — explicit "see other players' damage numbers by
    // default" request.
    public static bool SeeOtherPlayerDamage { get; private set; } = true;
    // Default false/unchecked (opt-out, unlike SeeOtherPlayerDamage's opt-in) — a new "Controls" reminder
    // panel should be visible out of the box for anyone who hasn't touched this setting yet, not hidden
    // until they go find the toggle. Renamed+inverted 2026-09-03 (was ControlsVisible, true=shown,
    // default true) to match the Inspector's own "Hide Controls Panel in Game" label — checked now
    // directly means hidden, same polarity as HideExpandedUI just below (checked=default=hidden there;
    // this one still defaults to NOT hidden, only the naming/polarity changed, not the actual default
    // behavior). Read by LocalUIManagerPD.Awake() (the live scene reference this static context can't
    // reach — same "needs a live component" reasoning ApplySavedMixerVolumes already documents for the
    // AudioMixer) and pushed live via LocalUIManagerPD.SetControlsHidden on change.
    public static bool HideControls { get; private set; } = false;
    // Default false/unchecked (2026-09-03, was true — new players used to get the compact HUD with
    // ExpandedPlayerUI hidden by default; explicit request flipped this to NOT hidden by default instead).
    // Read by LocalUIManagerPD.Awake() (the live scene reference this static context can't reach) and
    // pushed live via LocalUIManagerPD.SetExpandedUIHidden on change.
    public static bool HideExpandedUI { get; private set; } = false;

    // TODO: status popups (DamageNumberManager.SpawnStatusOnPlayer/SpawnStatusAtLocation) currently have no
    // visibility gating at all — every client always sees every player's popups. Add a SEPARATE toggle here
    // (own PlayerPrefs key, e.g. "SeeOtherPlayerStatusPopups") when this gets revisited — deliberately NOT
    // reusing SeeOtherPlayerDamage, per explicit request to keep status popups fully independent of damage
    // numbers. Default should stay "visible" (both this and SeeOtherPlayerDamage now default visible too,
    // 2026-09-03 — keep them matching unless a future request says otherwise).
    // IMPORTANT: StatusPopupKind.Death must be EXEMPT from this toggle when it's added — explicit request
    // that everyone always sees a death, regardless of this (or any future ally-visibility) setting.

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyOnLoad()
    {
        AudioListener.volume = PlayerPrefs.GetFloat(VolumeKey, MasterDefaultPercent) / 100f;
        Application.targetFrameRate = PlayerPrefs.GetInt(FramerateKey, -1);
        SeeOtherPlayerDamage = PlayerPrefs.GetInt(SeeOtherPlayerDamageKey, 1) == 1;
        HideControls = PlayerPrefs.GetInt(ControlsHiddenKey, 0) == 1;
        HideExpandedUI = PlayerPrefs.GetInt(HideExpandedUIKey, 0) == 1;
        // SFX/Music deliberately NOT applied here — this static callback has no live AudioMixer reference to
        // call SetFloat on (unlike AudioListener.volume/Application.targetFrameRate, which are global engine
        // state, not asset-instance state). See ApplySavedMixerVolumes' own doc comment for where/how they
        // actually get applied this early instead.
    }

    // Was Awake() (2026-09-03 fix) — Awake only ever fires ONCE, the first time this GameObject (which
    // starts inactive as a hidden panel) gets activated. That one-shot sync raced against the Toggle
    // components' own internal init on that same first activation — SetIsOnWithoutNotify could get called
    // before/clobbered around the Toggle's own OnEnable, leaving the VISUAL checked-state stuck on
    // whatever the prefab was authored with (checked) forever after, even though the static Settings
    // properties it read from stayed correct the whole time (never touched again after that one bad
    // sync) — which is exactly why the underlying setting always behaved correctly while the toggle's own
    // displayed state didn't, and why a single manual click "fixed" it (a real click drives the Toggle's
    // own normal state directly, no longer dependent on this sync). OnEnable fires every single time the
    // panel is shown, not just once, so it re-stamps every control from the actual saved values on every
    // open — every line in this method is idempotent (just re-applying already-correct values), so this is
    // a pure safety upgrade with no downside for the sliders/dropdown either, even though only the 3
    // toggles were reported as visibly wrong.
    private void OnEnable()
    {
        float saved = PlayerPrefs.GetFloat(VolumeKey, MasterDefaultPercent);
        SetVolume(saved);

        if (audioMixer != null) ApplySavedMixerVolumes(audioMixer);
        SyncMixerSliderToUI(sfxVolumeSlider, sfxVolumeInput, PlayerPrefs.GetFloat(SFXVolumeKey, 100f));
        SyncMixerSliderToUI(musicVolumeSlider, musicVolumeInput, PlayerPrefs.GetFloat(MusicVolumeKey, 100f));

        int savedFps = PlayerPrefs.GetInt(FramerateKey, -1);
        int index = System.Array.IndexOf(FramerateOptions, savedFps);
        if (index < 0) index = FramerateOptions.Length - 1;
        framerateDropdown.SetValueWithoutNotify(index);
        Application.targetFrameRate = savedFps;

        if (seeOtherPlayerDamageToggle != null)
            seeOtherPlayerDamageToggle.SetIsOnWithoutNotify(SeeOtherPlayerDamage);
        if (controlsVisibilityToggle != null)
            controlsVisibilityToggle.SetIsOnWithoutNotify(HideControls);
        if (hideExpandedUIToggle != null)
            hideExpandedUIToggle.SetIsOnWithoutNotify(HideExpandedUI);
    }

    // Wire the panel's own Back button to this instead of a scene-specific manager's OnSettingsBackClicked —
    // this is the one piece of Settings UI wiring that has no scene-specific side effects to preserve (open
    // has pre-conditions, e.g. LobbySceneController's LoadingScreen/ready-state checks; close never did), so
    // letting the panel close itself is what actually makes it prefab-safe across Lobby/Game.
    public void OnBackClicked() => gameObject.SetActive(false);

    public void OnSeeOtherPlayerDamageChanged(bool value)
    {
        SeeOtherPlayerDamage = value;
        PlayerPrefs.SetInt(SeeOtherPlayerDamageKey, value ? 1 : 0);
        PlayerPrefs.Save();
    }

    // Method/field names kept as-is (2026-09-03 fix) despite the HideControls rename above — this is still
    // wired to the same Inspector Toggle's OnValueChanged, no rewiring needed. Only the internal polarity
    // changed: value now means "hide" directly (matching the Inspector's own "Hide Controls Panel in Game"
    // label) instead of being assigned straight into the old positive-phrased ControlsVisible, which is
    // what made checking "Hide..." actually SHOW the panel before this fix.
    public void OnControlsVisibilityChanged(bool value)
    {
        HideControls = value;
        PlayerPrefs.SetInt(ControlsHiddenKey, value ? 1 : 0);
        PlayerPrefs.Save();
        // Pushes the change live for whichever player is currently in a match — see LocalUIManagerPD.
        // SetControlsHidden's own doc comment. Settings can be opened outside a live match too (main menu/
        // lobby), where instance is null — same harmless no-op every other cross-scene singleton reference
        // in this codebase already treats that as.
        LocalUIManagerPD.instance?.SetControlsHidden(value);
    }

    public void OnHideExpandedUIChanged(bool value)
    {
        HideExpandedUI = value;
        PlayerPrefs.SetInt(HideExpandedUIKey, value ? 1 : 0);
        PlayerPrefs.Save();
        // Pushes the change live — see OnControlsVisibilityChanged's own identical null-safety comment.
        LocalUIManagerPD.instance?.SetExpandedUIHidden(value);
    }

    public void OnFramerateChanged(int index)
    {
        int fps = FramerateOptions[index];
        Application.targetFrameRate = fps;
        PlayerPrefs.SetInt(FramerateKey, fps);
        PlayerPrefs.Save();
    }

    // ── Master ────────────────────────────────────────────────────────────
    public void OnSliderChanged(float value)
    {
        AudioListener.volume = value / 100f;
        PlayerPrefs.SetFloat(VolumeKey, value);
        PlayerPrefs.Save();
        volumeInput.SetTextWithoutNotify(Mathf.RoundToInt(value).ToString());
    }

    public void OnInputEndEdit(string text)
    {
        float value = float.TryParse(text, out float parsed)
            ? Mathf.Clamp(parsed, 0f, 100f)
            : volumeSlider.value;
        SetVolume(value);
    }

    private void SetVolume(float value)
    {
        value = Mathf.Clamp(value, 0f, 100f);
        AudioListener.volume = value / 100f;
        PlayerPrefs.SetFloat(VolumeKey, value);
        PlayerPrefs.Save();
        volumeSlider.SetValueWithoutNotify(value);
        volumeInput.SetTextWithoutNotify(Mathf.RoundToInt(value).ToString());
    }

    // ── SFX ───────────────────────────────────────────────────────────────
    public void OnSfxSliderChanged(float value)
    {
        ApplyAndSaveMixerVolume(SFXMixerParam, SFXVolumeKey, value);
        if (sfxVolumeInput != null) sfxVolumeInput.SetTextWithoutNotify(Mathf.RoundToInt(value).ToString());
    }

    public void OnSfxInputEndEdit(string text)
    {
        float value = float.TryParse(text, out float parsed)
            ? Mathf.Clamp(parsed, 0f, 100f)
            : sfxVolumeSlider.value;
        SetSfxVolume(value);
    }

    private void SetSfxVolume(float value)
    {
        value = Mathf.Clamp(value, 0f, 100f);
        ApplyAndSaveMixerVolume(SFXMixerParam, SFXVolumeKey, value);
        if (sfxVolumeSlider != null) sfxVolumeSlider.SetValueWithoutNotify(value);
        if (sfxVolumeInput != null) sfxVolumeInput.SetTextWithoutNotify(Mathf.RoundToInt(value).ToString());
    }

    // ── Music ─────────────────────────────────────────────────────────────
    public void OnMusicSliderChanged(float value)
    {
        ApplyAndSaveMixerVolume(MusicMixerParam, MusicVolumeKey, value);
        if (musicVolumeInput != null) musicVolumeInput.SetTextWithoutNotify(Mathf.RoundToInt(value).ToString());
    }

    public void OnMusicInputEndEdit(string text)
    {
        float value = float.TryParse(text, out float parsed)
            ? Mathf.Clamp(parsed, 0f, 100f)
            : musicVolumeSlider.value;
        SetMusicVolume(value);
    }

    private void SetMusicVolume(float value)
    {
        value = Mathf.Clamp(value, 0f, 100f);
        ApplyAndSaveMixerVolume(MusicMixerParam, MusicVolumeKey, value);
        if (musicVolumeSlider != null) musicVolumeSlider.SetValueWithoutNotify(value);
        if (musicVolumeInput != null) musicVolumeInput.SetTextWithoutNotify(Mathf.RoundToInt(value).ToString());
    }

    private void ApplyAndSaveMixerVolume(string mixerParam, string prefsKey, float percent)
    {
        if (audioMixer != null) audioMixer.SetFloat(mixerParam, PercentToDb(percent));
        PlayerPrefs.SetFloat(prefsKey, percent);
        PlayerPrefs.Save();
    }

    private static void SyncMixerSliderToUI(Slider slider, TMP_InputField input, float percent)
    {
        if (slider != null) slider.SetValueWithoutNotify(percent);
        if (input != null) input.SetTextWithoutNotify(Mathf.RoundToInt(percent).ToString());
    }

    // Public/static so SFXManager's own Awake() (a DontDestroyOnLoad singleton created at true app boot, with
    // a live AudioMixer reference via its own AudioMixerGroup) can apply the player's saved SFX/Music volume
    // immediately too — without this, a player who never opens the Settings panel this session would keep
    // hearing the mixer's own EDITOR-authored default volume all session, since this panel's own Awake()
    // only fires once its (initially-inactive) GameObject first activates. Same "apply as early as possible"
    // reasoning ApplyOnLoad already covers for Master/Framerate/SeeOtherPlayerDamage — SFX/Music just need a
    // live AudioMixer reference to do it, which a static context doesn't have on its own.
    public static void ApplySavedMixerVolumes(AudioMixer mixer)
    {
        if (mixer == null) return;
        mixer.SetFloat(SFXMixerParam, PercentToDb(PlayerPrefs.GetFloat(SFXVolumeKey, 100f)));
        mixer.SetFloat(MusicMixerParam, PercentToDb(PlayerPrefs.GetFloat(MusicVolumeKey, 100f)));
    }

    // Standard perceptual "20*log10(linear)" curve nearly every game's volume slider uses — a straight
    // linear dB slider crams almost all its useful range into the last few percent, which reads as broken to
    // a player. Master (AudioListener.volume) deliberately does NOT use this — it's a genuinely linear
    // multiplier, not a mixer dB parameter, so no conversion applies there.
    private static float PercentToDb(float percent) => percent <= 0f ? MinDb : 20f * Mathf.Log10(percent / 100f);
}
