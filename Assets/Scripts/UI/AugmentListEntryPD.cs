using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

// Ported from _OldSystemArchive/UI/AugmentListEntry.cs — unchanged behavior, just renamed for convention.
public class AugmentListEntryPD : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] private TextMeshProUGUI nameText;

    private string _description;

    // stats optional — see AugmentCardUI.AugmentSetup's own doc comment for the same contract. This entry
    // is the in-match "current build" (Tab-held) overlay, so its caller (AugmentListUIPD) always has the
    // local player's own live stats on hand to pass.
    public void Setup(AugmentData data, CharacterStatsPD stats = null)
    {
        nameText.text = data.displayName;
        _description = AugmentDescriptionFormatter.Format(data, stats);
    }

    public void OnPointerEnter(PointerEventData eventData) => AugmentTooltipPD.instance?.Show(_description);
    public void OnPointerExit(PointerEventData eventData)  => AugmentTooltipPD.instance?.Hide();

    private void OnDisable() => AugmentTooltipPD.instance?.Hide();
}
