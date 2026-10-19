using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Drop on any Button to play a click sound on press AND a hover sound on pointer-enter, both through
// SFXManager's pooled 2D playback. Leave either override unassigned to use SFXManager's matching shared
// default (DefaultButtonClickClip / DefaultButtonHoverClip) — set once, shared by every button that doesn't
// need its own distinct sound; assign an override only for a button that should sound different (e.g. a
// confirm-weighted click for Ready Up vs. plain navigation clicks everywhere else). Not meant for
// AugmentCardUI's own pick button — that already has its own dedicated pickSound wired directly into
// OnPicked().
//
// OnPointerEnter is already an edge event — Unity's EventSystem only calls it on the actual enter transition,
// never continuously while hovering — so unhovering and re-hovering naturally retriggers the hover sound with
// no extra bookkeeping needed.
[RequireComponent(typeof(Button))]
public class ButtonClickSFX : MonoBehaviour, IPointerEnterHandler
{
    [SerializeField] private AudioClip clickClipOverride;
    [SerializeField] private float clickVolume = 1f;
    [SerializeField] private AudioClip hoverClipOverride;
    [SerializeField] private float hoverVolume = 1f;

    private Button _button;

    private void Awake()
    {
        _button = GetComponent<Button>();
        _button.onClick.AddListener(PlayClickSound);
    }

    private void OnDestroy()
    {
        if (_button != null) _button.onClick.RemoveListener(PlayClickSound);
    }

    private void PlayClickSound()
    {
        var clip = clickClipOverride != null ? clickClipOverride : SFXManager.instance?.DefaultButtonClickClip;
        SFXManager.instance?.Play(clip, Vector3.zero, clickVolume);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        // Pointer enter/exit fire regardless of Selectable.interactable — a greyed-out, unclickable button
        // shouldn't still give hover feedback.
        if (_button != null && !_button.interactable) return;
        var clip = hoverClipOverride != null ? hoverClipOverride : SFXManager.instance?.DefaultButtonHoverClip;
        SFXManager.instance?.Play(clip, Vector3.zero, hoverVolume);
    }
}
