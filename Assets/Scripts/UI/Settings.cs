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
    // GameAudioMixer — SFX/Music route through its exposed SFXVolume/MusicVolume float parameters (see
    // SFXMixerParam/MusicMixerParam below); Master stays on AudioListener.volume since Master's own group
    // volume isn't exposed to script.
    [SerializeField] private AudioMixer audioMixer;

    private const string VolumeKey = "MasterVolume";
    private const string SFXVolumeKey = "SFXVolume";
    private const string MusicVolumeKey = "MusicVolume";
    private const string FramerateKey = "TargetFramerate";
    private const string SeeOtherPlayerDamageKey = "SeeOtherPlayerDamage";
    private static readonly int[] FramerateOptions = { 30, 60, 120, 144, -1 };

    // Must match the exposed parameter names on GameAudioMixer.mixer exactly (Edit > Expose to Script, or
    // already-exposed — check the mixer's own Exposed Parameters list before renaming either side).
    private const string SFXMixerParam = "SFXVolume";
    private const string MusicMixerParam = "MusicVolume";
    // 0% maps to this instead of -infinity (Log10(0) is undefined) — matches the mixer's own
    // m_SuspendThreshold, effectively silent either way.
    private const float MinDb = -80f;
    // Master only — a fresh install (no saved MasterVolume PlayerPrefs key) opens at 80%, not "true 100%".
    // SFX/Music both still default to 100% (see their own PlayerPrefs.GetFloat calls below) — this is
    // deliberately NOT applied to them.
    private const float MasterDefaultPercent = 80f;

    public static bool SeeOtherPlayerDamage { get; private set; }

    // TODO: status popups (DamageNumberManager.SpawnStatusOnPlayer/SpawnStatusAtLocation) currently have no
    // visibility gating at all — every client always sees every player's popups. Add a SEPARATE toggle here
    // (own PlayerPrefs key, e.g. "SeeOtherPlayerStatusPopups") when this gets revisited — deliberately NOT
    // reusing SeeOtherPlayerDamage, per explicit request to keep status popups fully independent of damage
    // numbers. Default should stay "visible" (opt-out, not opt-in) unlike SeeOtherPlayerDamage's default-off.
    // IMPORTANT: StatusPopupKind.Death must be EXEMPT from this toggle when it's added — explicit request
    // that everyone always sees a death, regardless of this (or any future ally-visibility) setting.

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyOnLoad()
    {
        AudioListener.volume = PlayerPrefs.GetFloat(VolumeKey, MasterDefaultPercent) / 100f;
        Application.targetFrameRate = PlayerPrefs.GetInt(FramerateKey, -1);
        SeeOtherPlayerDamage = PlayerPrefs.GetInt(SeeOtherPlayerDamageKey, 0) == 1;
        // SFX/Music deliberately NOT applied here — this static callback has no live AudioMixer reference to
        // call SetFloat on (unlike AudioListener.volume/Application.targetFrameRate, which are global engine
        // state, not asset-instance state). See ApplySavedMixerVolumes' own doc comment for where/how they
        // actually get applied this early instead.
    }

    private void Awake()
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
    }

    public void OnFramerateChanged(int index)
    {
        int fps = FramerateOptions[index];
        Application.targetFrameRate = fps;
        PlayerPrefs.SetInt(FramerateKey, fps);
    }

    // ── Master ────────────────────────────────────────────────────────────
    public void OnSliderChanged(float value)
    {
        AudioListener.volume = value / 100f;
        PlayerPrefs.SetFloat(VolumeKey, value);
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
