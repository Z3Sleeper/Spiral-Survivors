using UnityEngine;
using UnityEngine.EventSystems;

// Fixes a well-known Unity UI quirk: Selectable/Button has a separate "Selected" state from "Highlighted",
// meant for keyboard/gamepad navigation — EventSystem.current.currentSelectedGameObject keeps pointing at
// whatever was last clicked, and that button visually gets stuck showing its Selected color instead of
// properly re-highlighting on hover, until something else changes the selection (e.g. clicking a different
// button). This project is mouse-driven, not keyboard/gamepad-navigated, so nothing actually needs that
// persistence — clearing the selection immediately after every click is the standard fix. One instance
// fixes every Button in whatever scene it's placed in; no per-button changes needed.
public class ClearUISelectionOnClick : MonoBehaviour
{
    private void Update()
    {
        if (Input.GetMouseButtonUp(0) && EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            EventSystem.current.SetSelectedGameObject(null);
    }
}
