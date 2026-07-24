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

    private readonly Dictionary<string, int> _charSelections = new();
    private readonly Dictionary<string, int> _colorSelections = new();
    private readonly Dictionary<string, long> _powerLevels = new();
    private bool _isRandom;
    private bool _localIsReady;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        BuildColorPicker();
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

    // HOST-ONLY. Reconciles ColorAssignments against the current member list and everyone's outstanding
    // colorRequest: prunes members who've left (freeing their color back up), auto-assigns anyone missing
    // an assignment (new joiners), and grants a pending request iff the requested color is actually free —
    // otherwise the requester silently keeps their current color until they request a different, free one.
    private void ResolveColorAssignments(Lobby lobby)
    {
        if (!lobby.IsOwner) return;
        if (colorPalette == null || colorPalette.Count == 0) return;
        if (!ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw)) return;
        var lobbyCSteamID = new CSteamID(lobbyIdRaw);

        var assignments = ParseColorAssignments(SteamMatchmaking.GetLobbyData(lobbyCSteamID, ColorAssignmentsKey));
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

        if (changed)
            SteamMatchmaking.SetLobbyData(lobbyCSteamID, ColorAssignmentsKey, SerializeColorAssignments(assignments));
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

        if (lobbyManager != null)
        {
            var lobby = lobbyManager.CurrentLobby;
            if (lobby.IsValid && ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
                SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "char", "-1");
            RefreshFromLobby(lobby);
        }
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

        if (lobbyManager == null) return;
        var lobby = lobbyManager.CurrentLobby;
        if (!lobby.IsValid) return;

        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "char", index.ToString());

        RefreshFromLobby(lobby);
    }
}
