using TMPro;
using UnityEngine;
using UnityEngine.UI;

// A single icon in the buff bar. Plain, non-predicted UI — buffs are shown per-owning-client only, driven
// by whatever augment/effect wants to display one, not part of the simulation itself. See BuffBarPD for
// how one of these gets spawned.
public class BuffIconPD : MonoBehaviour
{
    [SerializeField] private Image icon;
    [SerializeField] private Image cooldownSweep;
    // Hidden by default in the prefab — generic, not tied to any one effect. Only shown once something
    // actually calls SetStackText (e.g. a stacking effect writing "x10"); most buff icons never touch this
    // at all.
    [SerializeField] private TextMeshProUGUI stackText;

    public void SetIcon(Sprite sprite)
    {
        if (icon != null) icon.sprite = sprite;
    }

    // 0 = fully ready/available (sweep empty), 1 = just triggered/fully on cooldown (sweep full) — standard
    // "radial cooldown overlay that empties as it becomes ready again" convention.
    public void SetCooldownFill(float value01)
    {
        if (cooldownSweep != null) cooldownSweep.fillAmount = Mathf.Clamp01(value01);
    }

    // Null or empty hides it again — callers don't need their own separate show/hide bookkeeping, just pass
    // whatever should currently be displayed (or nothing) every time it changes.
    public void SetStackText(string text)
    {
        if (stackText == null) return;
        bool show = !string.IsNullOrEmpty(text);
        stackText.gameObject.SetActive(show);
        if (show) stackText.text = text;
    }
}
