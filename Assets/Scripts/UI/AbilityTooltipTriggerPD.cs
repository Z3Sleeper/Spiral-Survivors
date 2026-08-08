using UnityEngine;
using UnityEngine.EventSystems;

// Sits on an ability icon's own hover-catcher Image (AbilityIcon under Ability1UI/Ability2UI — Raycast
// Target on; everything else in that icon's hierarchy, including the Tooltip Panel itself, Raycast Target
// off, so the tooltip popping up over the icon can't intercept the pointer and immediately hide itself
// again). Holds no tooltip state itself — just relays hover enter/exit into LocalUIManagerPD, which owns
// showing/hiding the panel and formatting the description text (see its own SetAbilityTooltipVisible).
public class AbilityTooltipTriggerPD : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] private bool isAbility1;

    public void OnPointerEnter(PointerEventData eventData) => LocalUIManagerPD.instance?.SetAbilityTooltipVisible(isAbility1, true);
    public void OnPointerExit(PointerEventData eventData) => LocalUIManagerPD.instance?.SetAbilityTooltipVisible(isAbility1, false);
}
