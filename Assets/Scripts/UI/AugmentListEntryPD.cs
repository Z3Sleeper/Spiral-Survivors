using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

// Ported from _OldSystemArchive/UI/AugmentListEntry.cs — unchanged behavior, just renamed for convention.
public class AugmentListEntryPD : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] private TextMeshProUGUI nameText;

    private string _description;

    public void Setup(AugmentData data)
    {
        nameText.text = data.displayName;
        _description = data.description;
    }

    public void OnPointerEnter(PointerEventData eventData) => AugmentTooltipPD.instance?.Show(_description);
    public void OnPointerExit(PointerEventData eventData)  => AugmentTooltipPD.instance?.Hide();

    private void OnDisable() => AugmentTooltipPD.instance?.Hide();
}
