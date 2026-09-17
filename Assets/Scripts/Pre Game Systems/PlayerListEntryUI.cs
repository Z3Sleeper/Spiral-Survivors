using TMPro;
using UnityEngine;
using UnityEngine.UI;

// One row in CharacterSelectUI's own player list — replaces the old single-TextMeshProUGUI blob (one shared
// rich-text string for every member) with one instance per lobby member, so each row can also carry its own
// avatar image. RawImage, not Image — LobbyUser.Avatar is a raw Texture2D (SteamLobbyProvider's own resolved
// avatar), and RawImage.texture takes that directly; Image.sprite would need a runtime-wrapped Sprite
// recreated every refresh for no benefit, same reasoning PurrLobby's own (now unused in scene) MemberEntry
// prefab already followed for this exact case.
public class PlayerListEntryUI : MonoBehaviour
{
    [SerializeField] private RawImage avatarImage;
    [SerializeField] private TextMeshProUGUI lineText;

    // avatar can be null (Steam's avatar callback hasn't resolved yet) — left as whatever the prefab's own
    // placeholder texture is rather than forced blank, so a not-yet-loaded avatar doesn't flash to nothing
    // for a frame; CharacterSelectUI.RefreshFromLobby gets called again once the real avatar arrives (see
    // SteamLobbyProvider.UpdateUserAvatar's own OnLobbyUpdated invoke), which then sets the real texture here.
    public void SetData(Texture2D avatar, string richText)
    {
        if (avatarImage != null && avatar != null)
            avatarImage.texture = avatar;
        if (lineText != null)
            lineText.text = richText;
    }
}
