using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class VolumeSettings : MonoBehaviour
{
    [SerializeField] private Slider volumeSlider;
    [SerializeField] private TMP_InputField volumeInput;

    private const string VolumeKey = "MasterVolume";

    private void Awake()
    {
        float saved = PlayerPrefs.GetFloat(VolumeKey, 100f);
        SetVolume(saved);
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
