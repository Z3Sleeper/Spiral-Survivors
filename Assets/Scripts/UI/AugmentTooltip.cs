using TMPro;
using UnityEngine;

public class AugmentTooltip : MonoBehaviour
{
    public static AugmentTooltip instance;

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
