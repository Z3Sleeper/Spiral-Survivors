using System.Collections.Generic;
using PurrLobby;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CharacterSelectUI : MonoBehaviour
{
    public static CharacterSelectUI instance;

    [SerializeField] private TextMeshProUGUI playerListText;
    [SerializeField] private LobbyManager lobbyManager;
    [SerializeField] private CodeButton codeButton;
    [SerializeField] private PlayerColorPalette colorPalette;
    [SerializeField] private Transform colorPickerContainer;
    [SerializeField] private GameObject colorSwatchPrefab;

    [Header("Character Detail Panel")]
    [SerializeField] private TextMeshProUGUI characterDescriptionText;
    [SerializeField] private AugmentCardUI detailAugmentCardPrefab;
    [SerializeField] private Transform detailAugmentContent;

    // Named fields (one per class), not a raw array — an array element's position is easy to mix up when
    // wiring buttons in the Inspector with no label to check against; these get folded into an internal,
    // CharacterRegistry-index-aligned array in Awake() so the rest of the highlight logic can still just
    // work off a plain index.
    [Header("Character Buttons (selection highlight)")]
    [SerializeField] private Image knightButtonImage;
    [SerializeField] private Image rogueButtonImage;
    [SerializeField] private Image archerButtonImage;
    [SerializeField] private Image bladeweaverButtonImage;
    [SerializeField] private Image executionerButtonImage;
    [SerializeField] private Image randomButtonImage;
    [SerializeField] private Color selectedButtonColor = Color.green;

    private Image[] _characterButtonImages;
    private Color[] _characterButtonDefaultColors;
    private Color _randomButtonDefaultColor;

    private readonly Dictionary<string, int> _charSelections = new();
    private readonly Dictionary<string, int> _colorSelections = new();
    private readonly Dictionary<string, long> _powerLevels = new();
    private readonly List<AugmentCardUI> _detailAugmentPool = new();
    private bool _isRandom;
    private bool _localIsReady;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        BuildColorPicker();

        // Order matches CharacterRegistry's prefab index order (Knight=0, Rogue=1, Archer=2, Bladeweaver=3,
        // Executioner=4) — this is the one place that mapping needs to be kept correct, not the Inspector.
        _characterButtonImages = new[]
        {
            knightButtonImage, rogueButtonImage, archerButtonImage, bladeweaverButtonImage, executionerButtonImage
        };
        _characterButtonDefaultColors = new Color[_characterButtonImages.Length];
        for (int i = 0; i < _characterButtonImages.Length; i++)
            if (_characterButtonImages[i] != null) _characterButtonDefaultColors[i] = _characterButtonImages[i].color;

        if (randomButtonImage != null) _randomButtonDefaultColor = randomButtonImage.color;
    }

    private void UpdateCharacterButtonHighlight(int selectedIndex)
    {
        for (int i = 0; i < _characterButtonImages.Length; i++)
        {
            if (_characterButtonImages[i] == null) continue;
            _characterButtonImages[i].color = i == selectedIndex ? selectedButtonColor : _characterButtonDefaultColors[i];
        }
        if (randomButtonImage != null) randomButtonImage.color = _randomButtonDefaultColor;
    }

    private void UpdateRandomButtonHighlight()
    {
        for (int i = 0; i < _characterButtonImages.Length; i++)
            if (_characterButtonImages[i] != null) _characterButtonImages[i].color = _characterButtonDefaultColors[i];
        if (randomButtonImage != null) randomButtonImage.color = selectedButtonColor;
    }

    private void BuildColorPicker()
    {
        if (colorPickerContainer == null || colorSwatchPrefab == null || colorPalette == null) return;

        foreach (Transform child in colorPickerContainer)
            Destroy(child.gameObject);

        for (int i = 0; i < colorPalette.Count; i++)
        {
            int index = i;
            var obj = Instantiate(colorSwatchPrefab, colorPickerContainer);
            obj.GetComponent<Image>().color = colorPalette.Get(index);
            obj.GetComponent<Button>().onClick.AddListener(() => SelectColor(index));
        }
    }

    public void SetLocalDefault(string userId, int charIndex, int colorIndex)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            _charSelections[userId] = charIndex;
            _colorSelections[userId] = colorIndex;
        }
    }

    public UnityEngine.Color GetColor(int index) => colorPalette != null ? colorPalette.Get(index) : UnityEngine.Color.white;

    // Lobby-wide data (SetLobbyData) — as opposed to per-member data (SetLobbyMemberData) — can only ever be
    // written by the lobby owner. That's an actual Steam-enforced single-writer guarantee, which is what
    // makes this blob a true single source of truth for color assignment instead of the old "every client
    // independently claims its own slot" design (which had a real race — two clients could both read a color
    // as free before either write propagated, and both claim it). Format: "steamId1:0,steamId2:2,...".
    // Non-host clients never write this directly — see SelectColor/ColorRequestKey.
    private const string ColorAssignmentsKey = "ColorAssignments";

    // Per-member data — a non-authoritative REQUEST only ("I'd like this color"), never a claim. Only the
    // host ever turns a request into an actual assignment, via ResolveColorAssignments.
    private const string ColorRequestKey = "colorRequest";

    private static Dictionary<string, int> ParseColorAssignments(string blob)
    {
        var result = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(blob)) return result;

        foreach (var entry in blob.Split(','))
        {
            int sep = entry.IndexOf(':');
            if (sep <= 0) continue;
            if (int.TryParse(entry.Substring(sep + 1), out int idx))
                result[entry.Substring(0, sep)] = idx;
        }
        return result;
    }

    private static string SerializeColorAssignments(Dictionary<string, int> assignments)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var kvp in assignments)
        {
            if (!first) sb.Append(',');
            sb.Append(kvp.Key).Append(':').Append(kvp.Value);
            first = false;
        }
        return sb.ToString();
    }

    // Pure function over already-known state (no Steam calls) — ResolveColorAssignments already has the
    // full picture of who currently holds what via the working `taken` set.
    private int FindFirstFreeColor(HashSet<int> taken)
    {
        for (int i = 0; i < colorPalette.Count; i++)
            if (!taken.Contains(i)) return i;
        return 0;
    }

    // True iff the two assignment maps hold exactly the same member->color entries — used to double-check
    // ResolveColorAssignments' own `changed` bookkeeping actually reflects a real difference before writing,
    // see that method's own doc comment for why this extra check exists.
    private static bool AssignmentsEqual(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
            if (!b.TryGetValue(kvp.Key, out int value) || value != kvp.Value) return false;
        return true;
    }

    // HOST-ONLY. Reconciles ColorAssignments against the current member list and everyone's outstanding
    // colorRequest: prunes members who've left (freeing their color back up), auto-assigns anyone missing
    // an assignment (new joiners), and grants a pending request iff the requested color is actually free —
    // otherwise the requester silently keeps their current color until they request a different, free one.
    // Returns the resolved map (whether or not it needed writing) so callers like
    // LobbySceneController.OnAllReady can capture the exact just-resolved result atomically, with no
    // re-fetch/staleness window. Returns null for a non-host call or if colorPalette isn't set up.
    public Dictionary<string, int> ResolveColorAssignments(Lobby lobby)
    {
        if (!lobby.IsOwner) return null;
        if (colorPalette == null || colorPalette.Count == 0) return null;
        if (!ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw)) return null;
        var lobbyCSteamID = new CSteamID(lobbyIdRaw);

        // originalAssignments is an unmutated snapshot of what was actually read — kept alongside the
        // working `assignments` map (which the reconciliation below mutates freely) so the write at the
        // bottom can compare against what's ACTUALLY currently stored, not just trust `changed`'s own
        // field-by-field bookkeeping. See that check's own doc comment for why this distinction matters.
        var originalAssignments = ParseColorAssignments(SteamMatchmaking.GetLobbyData(lobbyCSteamID, ColorAssignmentsKey));
        var assignments = new Dictionary<string, int>(originalAssignments);
        var taken = new HashSet<int>(assignments.Values);
        bool changed = false;

        var memberIds = new HashSet<string>();
        foreach (var member in lobby.Members) memberIds.Add(member.Id);
        foreach (var staleId in new List<string>(assignments.Keys))
        {
            if (memberIds.Contains(staleId)) continue;
            taken.Remove(assignments[staleId]);
            assignments.Remove(staleId);
            changed = true;
        }

        foreach (var member in lobby.Members)
        {
            if (!ulong.TryParse(member.Id, out ulong memberIdRaw)) continue;
            bool hasAssignment = assignments.TryGetValue(member.Id, out int currentColor);

            if (!hasAssignment)
            {
                int assigned = FindFirstFreeColor(taken);
                assignments[member.Id] = assigned;
                taken.Add(assigned);
                changed = true;
                continue;
            }

            string requestVal = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, new CSteamID(memberIdRaw), ColorRequestKey);
            if (int.TryParse(requestVal, out int requested) && requested != currentColor
                && requested >= 0 && requested < colorPalette.Count && !taken.Contains(requested))
            {
                taken.Remove(currentColor);
                taken.Add(requested);
                assignments[member.Id] = requested;
                changed = true;
            }
        }

        // AssignmentsEqual is the actual guard, not `changed` alone — Steam fires a fresh LobbyDataUpdate_t
        // for every SetLobbyData call regardless of whether the written value actually differs from what's
        // already stored. If this method's own local read of its own previous write is ever even one tick
        // stale (a real risk in an async/callback-driven system, since GetLobbyData reads whatever this
        // machine's local Steam cache currently holds, not necessarily the exact value this SAME function
        // just wrote moments ago), `changed` can keep computing true for a state that's already actually
        // resolved — redundantly re-writing the same data, which by itself is enough to fire another
        // LobbyDataUpdate_t and re-trigger this whole method again (RefreshFromLobby -> resolve -> write ->
        // LobbyDataUpdate_t -> RefreshFromLobby -> ...), with nothing external ever needing to change to
        // keep it going. Comparing the actual resulting map against what was actually read, right before the
        // one real Steam write, closes that loop regardless of why `changed` thought something needed fixing.
        if (changed && !AssignmentsEqual(assignments, originalAssignments))
            SteamMatchmaking.SetLobbyData(lobbyCSteamID, ColorAssignmentsKey, SerializeColorAssignments(assignments));

        return assignments;
    }

    public void RefreshFromLobby(Lobby lobby)
    {
        if (playerListText == null || CharacterRegistry.instance == null) return;
        codeButton?.Init(lobby.LobbyId);

        // Host reconciles first so its own read immediately below (both here and everyone else's, once this
        // client's write propagates) reflects the freshest possible assignment — no-ops entirely for non-host
        // clients, whose color always comes purely from reading this same blob, never from self-assignment.
        ResolveColorAssignments(lobby);

        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
        {
            var lobbyCSteamID = new CSteamID(lobbyIdRaw);
            var colorAssignments = ParseColorAssignments(SteamMatchmaking.GetLobbyData(lobbyCSteamID, ColorAssignmentsKey));

            if (colorAssignments.TryGetValue(GameStartData.LocalUserId, out int myColor))
            {
                GameStartData.LocalColorIndex = myColor;
                GameStartData.LocalColor = GetColor(myColor);
            }

            foreach (var member in lobby.Members)
            {
                if (member.Id == GameStartData.LocalUserId) continue;
                if (!ulong.TryParse(member.Id, out ulong memberIdRaw)) continue;
                var memberSteamID = new CSteamID(memberIdRaw);

                string charVal = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, memberSteamID, "char");
                if (int.TryParse(charVal, out int charIdx))
                    _charSelections[member.Id] = charIdx;

                if (colorAssignments.TryGetValue(member.Id, out int colorIdx))
                    _colorSelections[member.Id] = colorIdx;

                string powerVal = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, memberSteamID, "power");
                if (long.TryParse(powerVal, out long power))
                    _powerLevels[member.Id] = power;
            }
        }

        var sb = new System.Text.StringBuilder($"Player List ({lobby.Members.Count}/{lobby.MaxPlayers}):\n");
        foreach (var member in lobby.Members)
        {
            string charName = "...";
            int colorIndex = 0;

            if (member.Id == GameStartData.LocalUserId)
            {
                charName = _isRandom ? "Random" : CharacterRegistry.instance.GetPredictedPrefab(GameStartData.LocalCharacterIndex).name;
                colorIndex = GameStartData.LocalColorIndex;
            }
            else
            {
                if (_charSelections.TryGetValue(member.Id, out int charIdx))
                    charName = charIdx == -1 ? "Random" : CharacterRegistry.instance.GetPredictedPrefab(charIdx).name;
                if (_colorSelections.TryGetValue(member.Id, out int cIdx))
                    colorIndex = cIdx;
            }

            string colorHex = colorPalette != null && colorPalette.Count > 0
                ? ColorUtility.ToHtmlStringRGB(colorPalette.Get(colorIndex))
                : "FFFFFF";

            bool isReady = member.Id == GameStartData.LocalUserId ? _localIsReady : member.IsReady;
            string readyState = isReady ? "Ready" : "Not Ready";
            long power = member.Id == GameStartData.LocalUserId
                ? ProgressionManager.SpentPoints
                : (_powerLevels.TryGetValue(member.Id, out long p) ? p : 0L);
            string powerStr = ProgressionManager.FormatPoints(power);
            sb.AppendLine($"<color=#{colorHex}>■</color> {readyState} - {member.DisplayName} - {charName} - Spent Points: {powerStr}");
        }
        playerListText.text = sb.ToString();
    }

    public void OnKnightClicked()       => SelectCharacter(0);
    public void OnRogueClicked()        => SelectCharacter(1);
    public void OnArcherClicked()       => SelectCharacter(2);
    public void OnBladeweaverClicked()  => SelectCharacter(3);
    public void OnExecutionerClicked()  => SelectCharacter(4);

    public void OnRandomClicked()
    {
        _isRandom = true;
        GameStartData.LocalCharacterIndex = UnityEngine.Random.Range(0, CharacterRegistry.instance.PrefabCount());
        _charSelections[GameStartData.LocalUserId] = -1;

        // Deliberately doesn't reveal the just-rolled character's description/augments — same
        // surprise-until-match-start convention the player list already follows for Random (shows "Random"
        // there too, never the actual rolled character). The Random button itself turning green is the only
        // visible confirmation of the pick now that there's no separate name text.
        if (characterDescriptionText != null) characterDescriptionText.text = string.Empty;
        PopulateDetailAugments(null, null);
        UpdateRandomButtonHighlight();

        if (lobbyManager != null)
        {
            var lobby = lobbyManager.CurrentLobby;
            if (lobby.IsValid && ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
                SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "char", "-1");
            RefreshFromLobby(lobby);
        }
    }

    // Updates the character-select detail panel (button highlight/description/starting-augment cards) for
    // whichever character index is currently selected — called on manual pick (SelectCharacter) and once at
    // initial join for the default Knight pick (LobbySceneController.InitializeForLobby), since that path
    // sets GameStartData.LocalCharacterIndex directly rather than going through SelectCharacter.
    public void RefreshCharacterDetails(int characterIndex)
    {
        var prefab = CharacterRegistry.instance != null ? CharacterRegistry.instance.GetPredictedPrefab(characterIndex) : null;
        if (prefab == null) return;

        UpdateCharacterButtonHighlight(characterIndex);

        var stats = prefab.GetComponent<CharacterStatsPD>();
        if (characterDescriptionText != null)
            characterDescriptionText.text = stats != null ? stats.ClassDescription : string.Empty;

        // GetComponents, not GetComponent — a character can carry both an Ability1-slot and an Ability2-slot
        // component (e.g. Rogue's RogueSpinAbilityPD + RogueVanishAbilityPD), and a plain single
        // GetComponent would only ever surface one of them.
        PopulateDetailAugments(prefab.GetComponents<IAbilityPD>(), stats != null ? stats.ClassPassiveAugments : null);
    }

    private void PopulateDetailAugments(IAbilityPD[] abilities, AugmentData[] augments)
    {
        if (detailAugmentCardPrefab == null || detailAugmentContent == null) return;

        // Nulls filtered out first (rather than skipped mid-loop) so pool index i always lines up with
        // "the i-th card that should currently be visible" — skipping in place would leave a stale
        // previously-active card sitting at that pool slot, untouched by either this loop or the trailing
        // deactivation pass below.
        int count = 0;

        // Ability cards go first, ahead of the passive augment cards, matching how they take priority in the
        // player's actual kit (input-driven, always available) over passives (passive, always-on). Ordered
        // Ability1 before Ability2 so the card order matches the input labeling regardless of component
        // order on the prefab.
        if (abilities != null)
        {
            foreach (var ability in abilities)
            {
                if (ability == null || ability.Slot != AbilitySlot.Ability1) continue;
                var ability1Card = GetDetailCard(count);
                ability1Card.gameObject.SetActive(true);
                ability1Card.AbilitySetup(ability.Name, ability.Description, ability.Icon);
                ability1Card.SetInteractive(false);
                count++;
            }
            foreach (var ability in abilities)
            {
                if (ability == null || ability.Slot != AbilitySlot.Ability2) continue;
                var ability2Card = GetDetailCard(count);
                ability2Card.gameObject.SetActive(true);
                ability2Card.AbilitySetup(ability.Name, ability.Description, ability.Icon);
                ability2Card.SetInteractive(false);
                count++;
            }
        }

        if (augments != null)
        {
            foreach (var augment in augments)
            {
                if (augment == null) continue;
                var card = GetDetailCard(count);
                card.gameObject.SetActive(true);
                card.AugmentSetup(augment, count);
                card.SetInteractive(false);
                count++;
            }
        }

        for (int i = count; i < _detailAugmentPool.Count; i++)
            _detailAugmentPool[i].gameObject.SetActive(false);
    }

    private AugmentCardUI GetDetailCard(int index)
    {
        if (index < _detailAugmentPool.Count) return _detailAugmentPool[index];
        var card = Instantiate(detailAugmentCardPrefab, detailAugmentContent);
        _detailAugmentPool.Add(card);
        return card;
    }

    public void OnReadyClicked()
    {
        if (lobbyManager == null) return;
        _localIsReady = !_localIsReady;
        // Explicit value, not ToggleLocalReady() — that toggles based on the lobby provider's own mirrored
        // localLobbyUser.IsReady, a separate state source from _localIsReady above that can drift out of
        // sync with it, which was causing client/host ready state mismatches (unreadying on the client
        // could read as readying on the host, since the two toggles didn't necessarily agree on the
        // starting value). Sending the exact bool we just computed removes that ambiguity entirely.
        lobbyManager.SetIsReady(GameStartData.LocalUserId, _localIsReady);
        RefreshFromLobby(lobbyManager.CurrentLobby);

        if (_localIsReady)
        {
            var lobby = lobbyManager.CurrentLobby;
            bool allOthersReady = true;
            foreach (var m in lobby.Members)
            {
                if (m.Id == GameStartData.LocalUserId) continue;
                if (!m.IsReady) { allOthersReady = false; break; }
            }
            if (allOthersReady && LoadingScreen.instance != null)
                LoadingScreen.instance.Show();
        }
    }

    // Renamed from ResetReadyState — that only ever cleared _localIsReady, but auditing turned up two more
    // local, lobby-specific fields with the exact same "never reset on leave" gap: _isRandom (so picking
    // Random in one lobby wouldn't silently keep showing "Random" as your char name in a later lobby even
    // after GameStartData.LocalCharacterIndex got reset back to 0 by InitializeForLobby), and the cached
    // OTHER-members' selections (_charSelections/_colorSelections/_powerLevels — keyed by userId, so a
    // player who hops between lobbies with you could otherwise briefly show stale data from the previous
    // lobby until the next RefreshFromLobby overwrites it). Called from both LobbySceneController.OnRoomLeft
    // and InitializeForLobby, so it's covered whichever path a lobby transition takes.
    public void ResetLocalLobbyState()
    {
        _localIsReady = false;
        _isRandom = false;
        _charSelections.Clear();
        _colorSelections.Clear();
        _powerLevels.Clear();
    }

    // Narrower than ResetLocalLobbyState() — only the ready flag, none of the character/color/power-cache
    // resets that method also does. Used by LobbySceneController.AbortStuckMatchStart's "stay in the lobby
    // and retry" path: a failed match-start attempt shouldn't wipe anyone's character/color picks, just
    // their readiness (since the whole point is forcing everyone to re-confirm ready before another attempt
    // auto-fires, not making them redo their selections). Caller is still responsible for the matching
    // lobbyManager.SetIsReady(..., false) write and a RefreshFromLobby call to actually show it.
    public void ResetReadyOnly() => _localIsReady = false;

    // Just files a REQUEST — never claims the color directly. The host (see ResolveColorAssignments) is the
    // only thing that ever actually grants it, via ColorAssignmentsKey; GameStartData.LocalColorIndex only
    // updates once RefreshFromLobby reads that confirmation back, not optimistically here. For the host's
    // own clicks this resolves within the same RefreshFromLobby call below (no network round-trip needed to
    // read back its own just-written Steam data); for a non-host client it resolves once the host's answer
    // propagates back, same latency as everyone else's data in this screen.
    private void SelectColor(int index)
    {
        if (lobbyManager != null)
        {
            var lobby = lobbyManager.CurrentLobby;
            if (lobby.IsValid && ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            {
                SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), ColorRequestKey, index.ToString());
                RefreshFromLobby(lobby);
                return;
            }
        }

        // No active lobby (e.g. solo/offline) — nothing to arbitrate, just set it directly.
        GameStartData.LocalColorIndex = index;
        GameStartData.LocalColor = GetColor(index);
    }

    private void SelectCharacter(int index)
    {
        _isRandom = false;
        GameStartData.LocalCharacterIndex = index;
        _charSelections[GameStartData.LocalUserId] = index;
        RefreshCharacterDetails(index);

        if (lobbyManager == null) return;
        var lobby = lobbyManager.CurrentLobby;
        if (!lobby.IsValid) return;

        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "char", index.ToString());

        RefreshFromLobby(lobby);
    }
}
