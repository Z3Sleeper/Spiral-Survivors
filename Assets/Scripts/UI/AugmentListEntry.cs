using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

public class AugmentListEntry : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] private TextMeshProUGUI nameText;

    private string _description;

    public void Setup(AugmentData data)
    {
        nameText.text = data.displayName;
        _description = data.description;
    }

    public void OnPointerEnter(PointerEventData eventData) => AugmentTooltip.instance.Show(_description);
    public void OnPointerExit(PointerEventData eventData)  => AugmentTooltip.instance.Hide();

    private void OnDisable() => AugmentTooltip.instance?.Hide();
}
