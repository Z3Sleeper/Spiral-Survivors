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

    public void SetName(string bossName)
    {
        if (nameText != null) nameText.text = bossName;
    }

    public void SetHealth(float current, float max)
    {
        if (healthBar != null && max > 0f)
            healthBar.value = current / max;
        if (hpText != null)
            hpText.text = $"{Mathf.RoundToInt(current)}/{Mathf.RoundToInt(max)}";
    }

    // progress: 1 = full time remaining before enraging, 0 = enraged (or this boss doesn't enrage at all).
    public void SetEnrage(float progress)
    {
        if (enrageBar != null) enrageBar.value = progress;
    }
}
