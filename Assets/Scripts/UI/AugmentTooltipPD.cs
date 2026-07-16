using TMPro;
using UnityEngine;

// Ported from _OldSystemArchive/UI/AugmentTooltip.cs — a plain local MonoBehaviour, no PD/networking
// concerns at all (matches feedback_pd_architecture: UI/orchestration stays plain MonoBehaviour).
public class AugmentTooltipPD : MonoBehaviour
{
    public static AugmentTooltipPD instance;

    [SerializeField] private RectTransform panel;
    [SerializeField] private TextMeshProUGUI descriptionText;
    [SerializeField] private Canvas canvas;
    [SerializeField] private Vector2 offset = new(16f, -16f);

    private void Awake()
    {
        instance = this;
        panel.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (!panel.gameObject.activeSelf) return;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.GetComponent<RectTransform>(),
            Input.mousePosition,
            canvas.worldCamera,
            out var localPoint);
        panel.localPosition = localPoint + offset;
    }

    public void Show(string text)
    {
        descriptionText.text = text;
        panel.gameObject.SetActive(true);
    }

    public void Hide()
    {
        panel.gameObject.SetActive(false);
    }
}
