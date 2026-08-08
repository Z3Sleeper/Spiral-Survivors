using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Deliberately dumb — no Update(), no cached IEnemyPD reference. LocalUIManagerPD resolves each active
// boss fresh every frame (enemies are pooled/reused, so a reference held here across frames could end up
// silently pointing at a different, later-spawned enemy) and just pushes the current values in.
public class BossHealthBarUI : MonoBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private TextMeshProUGUI hpText;
    [SerializeField] private TextMeshProUGUI nameText;
    [SerializeField] private Slider enrageBar;

    // LocalUIManagerPD.UpdateBossHealthBars calls SetHealth/SetEnrage every frame for every active boss —
    // these guard against redundant writes/allocation the same way LocalUIManagerPD's own SetText/SetSlider
    // helpers do, rather than unconditionally rebuilding hpText's string and rewriting both sliders every
    // single frame even while a boss's health/enrage genuinely hasn't changed.
    private float _lastCurrent = float.NaN;
    private float _lastMax = float.NaN;

    public void SetName(string bossName)
    {
        if (nameText != null && nameText.text != bossName) nameText.text = bossName;
    }

    public void SetHealth(float current, float max)
    {
        if (healthBar != null && max > 0f)
        {
            float value = current / max;
            if (!Mathf.Approximately(healthBar.value, value)) healthBar.value = value;
        }
        if (hpText != null && (current != _lastCurrent || max != _lastMax))
        {
            _lastCurrent = current;
            _lastMax = max;
            hpText.text = $"{Mathf.RoundToInt(current)}/{Mathf.RoundToInt(max)}";
        }
    }

    // progress: 1 = full time remaining before enraging, 0 = enraged (or this boss doesn't enrage at all).
    public void SetEnrage(float progress)
    {
        if (enrageBar != null && !Mathf.Approximately(enrageBar.value, progress)) enrageBar.value = progress;
    }
}
