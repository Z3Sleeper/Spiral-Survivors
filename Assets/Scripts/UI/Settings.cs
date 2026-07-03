using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class Settings : MonoBehaviour
{
    [SerializeField] private Slider volumeSlider;
    [SerializeField] private TMP_InputField volumeInput;
    [SerializeField] private TMP_Dropdown framerateDropdown;
    [SerializeField] private Toggle seeOtherPlayerDamageToggle;

    private const string VolumeKey = "MasterVolume";
    private const string FramerateKey = "TargetFramerate";
    private const string SeeOtherPlayerDamageKey = "SeeOtherPlayerDamage";
    private static readonly int[] FramerateOptions = { 30, 60, 120, 144, -1 };

    public static bool SeeOtherPlayerDamage { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyOnLoad()
    {
        AudioListener.volume = PlayerPrefs.GetFloat(VolumeKey, 100f) / 100f;
        Application.targetFrameRate = PlayerPrefs.GetInt(FramerateKey, -1);
        SeeOtherPlayerDamage = PlayerPrefs.GetInt(SeeOtherPlayerDamageKey, 0) == 1;
    }

    private void Awake()
    {
        float saved = PlayerPrefs.GetFloat(VolumeKey, 100f);
        SetVolume(saved);

        int savedFps = PlayerPrefs.GetInt(FramerateKey, -1);
        int index = System.Array.IndexOf(FramerateOptions, savedFps);
        if (index < 0) index = FramerateOptions.Length - 1;
        framerateDropdown.SetValueWithoutNotify(index);
        Application.targetFrameRate = savedFps;

        if (seeOtherPlayerDamageToggle != null)
            seeOtherPlayerDamageToggle.SetIsOnWithoutNotify(SeeOtherPlayerDamage);
    }

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
}
