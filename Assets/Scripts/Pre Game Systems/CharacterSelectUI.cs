using System.Collections.Generic;
using PurrLobby;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CharacterSelectUI : MonoBehaviour
{
    public static CharacterSelectUI instance;

    // One PlayerListEntryUI instance per lobby member, rebuilt (Destroy + Instantiate, same convention
    // BuildColorPicker already uses) on every RefreshFromLobby — at most 4 lobby members, so this isn't a
    // hot path worth pooling. See PlayerListEntryUI's own doc comment for why each row carries its own
    // avatar image instead of one shared rich-text blob.
    [SerializeField] private Transform playerListContainer;
    [SerializeField] private PlayerListEntryUI playerListEntryPrefab;
    [SerializeField] private LobbyManager lobbyManager;
    [SerializeField] private CodeButton codeButton;
    [SerializeField] private PlayerColorPalette colorPalette;
    [SerializeField] private Transform colorPickerContainer;
    [SerializeField] private GameObject colorSwatchPrefab;

    [Header("Character Detail Panel")]
    [SerializeField] private TextMeshProUGUI characterDescriptionText;
    [SerializeField] private AugmentCardUI detailAugmentCardPrefab;
    [SerializeField] private Transform detailAugmentContent;

    [Header("Difficulty")]
    [SerializeField] private TextMeshProUGUI difficultyText;
    [SerializeField] private TextMeshProUGUI difficultyDescriptionText;
    // Hidden entirely (not just disabled) at the respective end of the range — see RefreshDifficultyDisplay.
    [SerializeField] private GameObject difficultyPrevArrow;
    [SerializeField] private GameObject difficultyNextArrow;

    // Toggled via .interactable, not SetActive — Ready should stay visible but unclickable while the lobby
    // Threat Level ceiling is still resolving (a new/departed member) rather than disappearing entirely. See
    // IsCeilingResolved/RefreshFromLobby's own doc comments.
    [Header("Ready")]
    [SerializeField] private Button readyButton;

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

    // External, player-facing class names (2026-10-07, Stuart's own renaming pass) — index-aligned with
    // CharacterRegistry's own prefab order (see Awake()'s own comment just below: Knight=0, Rogue=1,
    // Archer=2, Bladeweaver=3, Executioner=4). Internal code identifiers/file/prefab names are
    // DELIBERATELY left unchanged (KnightBerserkAbilityPD, the "Knight" prefab itself, etc.) — only text
    // actually shown to players uses these, currently just the lobby player-list line in
    // RefreshPlayerList() below.
    private static readonly string[] ExternalClassNames =
        { "Spiral Knight", "Spinfiltrator", "Helix Hunter", "Gyromancer", "Vortexecutor" };

    private Image[] _characterButtonImages;
    private Color[] _characterButtonDefaultColors;
    private Color _randomButtonDefaultColor;

    private readonly Dictionary<string, int> _charSelections = new();
    private readonly Dictionary<string, int> _colorSelections = new();
    private readonly Dictionary<string, long> _powerLevels = new();
    private readonly List<AugmentCardUI> _detailAugmentPool = new();
    private bool _isRandom;
    private bool _localIsReady;

    // null until the first RefreshFromLobby this lobby session — distinguishes "no snapshot yet" from "empty
    // lobby" so the very first pass after joining never spuriously reads as a membership CHANGE. Cleared in
    // ResetLocalLobbyState alongside every other per-lobby-session field. See DetectMembershipChangeAndUnready.
    private HashSet<string> _lastKnownMemberIds;

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

        RefreshDifficultyDisplay();
    }

    // Not DontDestroyOnLoad — a fresh instance is created every time this panel's scene loads, and the old
    // one is destroyed on the way out. Guarded (not unconditional), same reasoning as LocalUIManagerPD's own
    // identical fix: an unconditional `instance = null` risks nulling out a NEWER, still-live instance if
    // teardown/setup ordering ever overlaps. Without this, `instance` was left dangling on a destroyed
    // object — `?.`/direct calls against it don't behave like a real null check on a destroyed
    // UnityEngine.Object (Unity's own "fake null"), risking a silent misbehave instead of a clean no-op.
    private void OnDestroy()
    {
        if (instance == this) instance = null;
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

    // Same "lobby-wide, host-owner-enforced single writer" shape as ColorAssignmentsKey above, just a plain
    // int instead of a serialized dictionary — see ResolveDifficulty's own doc comment for the write side.
    private const string DifficultyKey = "Difficulty";

    // Per-member data — a non-authoritative REQUEST only ("I'd like this color"), never a claim. Only the
    // host ever turns a request into an actual assignment, via ResolveColorAssignments. Public (not private)
    // so LobbySceneController.InitializeForLobby can write a saved-preference request directly at join time,
    // before the player has manually clicked anything.
    public const string ColorRequestKey = "colorRequest";

    // Per-member data, self-write-only (same restriction ColorRequestKey's own doc comment describes — a
    // client can never write another player's Steam member data). Written once at join time
    // (LobbySceneController.InitializeForLobby) and never re-sent mid-session — see that write site's own
    // doc comment for why ProgressionManager.HighestUnlockedDifficulty is safe to treat as join-time-stable.
    // Read back host-only by ResolveDifficultyCeiling.
    public const string UnlockedDifficultyKey = "unlockedTL";

    // Lobby-wide, host-owner-enforced single writer — same shape as DifficultyKey above. Only meaningful
    // once TLCeilingResolvedKey reads "True"; stale/partial otherwise, since ResolveDifficultyCeiling never
    // writes this from an incomplete read.
    private const string TLCeilingKey = "TLCeiling";

    // Lobby-wide, host-owner-enforced single writer. "True" iff the host successfully read EVERY current
    // member's UnlockedDifficultyKey this pass. The one single explicit, observable piece of shared state
    // every client (host included) reads identically via IsCeilingResolved() to decide whether Ready can be
    // clicked — deliberately not an inferred/derived per-machine guess (see IsCeilingResolved's own doc
    // comment for why that distinction matters here).
    private const string TLCeilingResolvedKey = "TLCeilingResolved";

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

    // Honors a saved-preference ColorRequestKey on a member's very FIRST assignment (previously only
    // consulted on a LATER change-request, once already assigned — see ResolveColorAssignments' own second
    // loop) — falls back to FindFirstFreeColor if no valid request is on file yet, or the requested color is
    // already taken. This is what makes GameStartData.SavedColorIndex (restored automatically on lobby join,
    // written as an immediate ColorRequestKey by LobbySceneController.InitializeForLobby) actually take
    // effect on a fresh join, instead of every player always landing on FindFirstFreeColor's arbitrary pick
    // regardless of what they filed.
    private int AssignInitialColor(CSteamID lobbyCSteamID, ulong memberIdRaw, HashSet<int> taken)
    {
        string requestVal = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, new CSteamID(memberIdRaw), ColorRequestKey);
        if (int.TryParse(requestVal, out int requested) && requested >= 0 && requested < colorPalette.Count && !taken.Contains(requested))
            return requested;
        return FindFirstFreeColor(taken);
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

        // Host processed first — this method only ever runs on the host's own machine (see the IsOwner guard
        // above), so "host" == GameStartData.LocalUserId here. Resolving the host's own initial assignment
        // before anyone else's means a same-pass ColorRequestKey collision between the host and a client
        // always lands in the host's favor (the host's preferred slot is already in `taken` by the time a
        // client's own request gets checked below) — explicit design, host has priority on color conflicts.
        foreach (var member in lobby.Members)
        {
            if (member.Id != GameStartData.LocalUserId) continue;
            if (ulong.TryParse(member.Id, out ulong hostIdRaw) && !assignments.ContainsKey(member.Id))
            {
                int assigned = AssignInitialColor(lobbyCSteamID, hostIdRaw, taken);
                assignments[member.Id] = assigned;
                taken.Add(assigned);
                changed = true;
            }
            break;
        }

        // Runs for EVERY member, host included — the host-priority pass above only ever handles the host's
        // own FIRST (never-yet-assigned) color, not any later re-pick. By the time this loop reaches the
        // host, it's already guaranteed to have an assignment (from the pass above), so it naturally falls
        // into the "already assigned, check for a change request" branch below just like any other member —
        // this is what actually lets the host successfully change color after their first pick.
        foreach (var member in lobby.Members)
        {
            if (!ulong.TryParse(member.Id, out ulong memberIdRaw)) continue;
            bool hasAssignment = assignments.TryGetValue(member.Id, out int currentColor);

            if (!hasAssignment)
            {
                int assigned = AssignInitialColor(lobbyCSteamID, memberIdRaw, taken);
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

    // Same "host resolves its own local truth into the lobby-wide blob, only writing on an actual change"
    // shape as ResolveColorAssignments above — comparing against the actual currently-stored value (not a
    // separate dirty flag) for the same reason: Steam fires a fresh LobbyDataUpdate_t for every SetLobbyData
    // call regardless of whether the value actually differs, so an unconditional write here would retrigger
    // RefreshFromLobby -> ResolveDifficulty -> write -> LobbyDataUpdate_t -> RefreshFromLobby -> ... forever.
    // No-ops entirely for a non-host (same IsOwner guard, and Steam itself enforces lobby-wide data as
    // host-owner-only regardless). GamePresets.instance.Difficulty is the single source of truth this reads
    // from — never the other way around, so a client's own local GamePresets value (whatever THEY last
    // hosted with) never leaks into what gets displayed or played.
    private void ResolveDifficulty(Lobby lobby)
    {
        if (!lobby.IsOwner) return;
        if (GamePresets.instance == null) return;
        if (!ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw)) return;
        var lobbyCSteamID = new CSteamID(lobbyIdRaw);

        string resolved = GamePresets.instance.Difficulty.ToString();
        if (SteamMatchmaking.GetLobbyData(lobbyCSteamID, DifficultyKey) != resolved)
            SteamMatchmaking.SetLobbyData(lobbyCSteamID, DifficultyKey, resolved);
    }

    // Host-only (same IsOwner guard/reasoning as ResolveDifficulty above) — recomputes the lobby-wide Threat
    // Level ceiling (min of every current member's own ProgressionManager.HighestUnlockedDifficulty) fresh,
    // from scratch, every single pass, rather than maintaining a persisted per-member dictionary that would
    // need explicit pruning on leave. A player who joins and leaves again before their UnlockedDifficultyKey
    // write ever arrives simply drops out of lobby.Members before the next pass — nothing is left referencing
    // them, so nothing can ever get permanently stuck waiting on data that will never come.
    //
    // "resolved" (see TLCeilingResolvedKey) means the host successfully read EVERY current member's own
    // UnlockedDifficultyKey this pass — an empty/unpropagated read (a member who just joined and hasn't had
    // their own write reach this host yet) marks the WHOLE pass unresolved rather than being silently
    // skipped/defaulted, so TLCeilingKey is never written from a partial read. Down-only clamp on the live
    // selection: never raises GamePresets.instance.Difficulty back up even if the ceiling just rose (e.g. the
    // constraining player left) — see RefreshDifficultyDisplay's own Next-arrow logic for how the ceiling
    // rising is instead surfaced (arrow becomes clickable again, selection itself doesn't move).
    private void ResolveDifficultyCeiling(Lobby lobby)
    {
        if (!lobby.IsOwner) return;
        if (GamePresets.instance == null) return;
        if (!ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw)) return;
        var lobbyCSteamID = new CSteamID(lobbyIdRaw);

        bool resolved = lobby.Members.Count > 0;
        int ceiling = int.MaxValue;

        foreach (var member in lobby.Members)
        {
            int memberUnlock;
            if (member.Id == GameStartData.LocalUserId)
            {
                // No round-trip for the host's own entry — same convention this method's own per-member read
                // loop below and RefreshFromLobby's char/color/power resolution already use.
                memberUnlock = ProgressionManager.HighestUnlockedDifficulty;
            }
            else
            {
                if (!ulong.TryParse(member.Id, out ulong memberIdRaw)) { resolved = false; continue; }
                string raw = SteamMatchmaking.GetLobbyMemberData(lobbyCSteamID, new CSteamID(memberIdRaw), UnlockedDifficultyKey);
                if (!int.TryParse(raw, out memberUnlock)) { resolved = false; continue; }
            }
            if (memberUnlock < ceiling) ceiling = memberUnlock;
        }

        if (resolved)
        {
            if (ceiling < GamePresets.instance.Difficulty)
                GamePresets.instance.SetDifficulty(ceiling);

            string ceilingStr = ceiling.ToString();
            if (SteamMatchmaking.GetLobbyData(lobbyCSteamID, TLCeilingKey) != ceilingStr)
                SteamMatchmaking.SetLobbyData(lobbyCSteamID, TLCeilingKey, ceilingStr);
        }

        string resolvedStr = resolved.ToString();
        if (SteamMatchmaking.GetLobbyData(lobbyCSteamID, TLCeilingResolvedKey) != resolvedStr)
            SteamMatchmaking.SetLobbyData(lobbyCSteamID, TLCeilingResolvedKey, resolvedStr);
    }

    // Every client's own reaction to a detected join/leave (see the diff below) — un-readies ONLY itself,
    // never another player, since Steam member data is self-write-only. Every client independently observing
    // the same lobby.Members change and reacting identically is what achieves "everyone's ready resets" for
    // free, without any host-to-everyone push mechanism (which isn't possible for another player's own ready
    // flag anyway). Gated on _localIsReady actually being true (not an unconditional write) for the same
    // "only write on an actual change" reasoning ResolveDifficulty/ResolveColorAssignments already follow.
    private void DetectMembershipChangeAndUnready(Lobby lobby)
    {
        if (lobbyManager == null) return;

        var currentIds = new HashSet<string>();
        foreach (var member in lobby.Members) currentIds.Add(member.Id);

        if (_lastKnownMemberIds != null && !_lastKnownMemberIds.SetEquals(currentIds))
        {
            if (_localIsReady)
            {
                _localIsReady = false;
                lobbyManager.SetIsReady(GameStartData.LocalUserId, false);
            }
        }

        _lastKnownMemberIds = currentIds;
    }

    // The single explicit, observable source of truth every client (host included) reads IDENTICALLY to
    // decide whether the Ready button can be clicked right now — deliberately not each machine independently
    // re-deriving "is the ceiling resolved" from its own local view of lobby.Members, since two machines'
    // own local snapshots can be momentarily out of sync with each other (a join visible to the host before
    // it's visible to a given client, or vice versa). Reading a per-machine value as if it were already
    // synced is exactly the class of bug this project's prior Threat-Level desync came from. No lobby
    // (solo/offline testing) always reads as resolved — nothing to wait on.
    private bool IsCeilingResolved()
    {
        if (lobbyManager == null || !lobbyManager.CurrentLobby.IsValid) return true;
        if (!ulong.TryParse(lobbyManager.CurrentLobby.LobbyId, out ulong lobbyIdRaw)) return true;
        string resolvedStr = SteamMatchmaking.GetLobbyData(new CSteamID(lobbyIdRaw), TLCeilingResolvedKey);
        return resolvedStr == "True";
    }

    // The LOBBY ceiling (min of every current member's own unlock, synced via TLCeilingKey), never this
    // machine's own ProgressionManager.HighestUnlockedDifficulty directly — reading the local value directly
    // here would be the exact bug this whole feature exists to fix (a host with a higher personal unlock
    // could see/click a Next arrow past what other members had actually unlocked). Falls back to the personal
    // value only when there's genuinely no lobby to sync through (solo/offline testing), same fallback IsHost
    // itself already uses, or before TLCeilingKey has ever been written (a lobby's very first pass).
    private int GetLobbyCeilingOrPersonal()
    {
        if (lobbyManager == null || !lobbyManager.CurrentLobby.IsValid) return ProgressionManager.HighestUnlockedDifficulty;
        if (!ulong.TryParse(lobbyManager.CurrentLobby.LobbyId, out ulong lobbyIdRaw)) return ProgressionManager.HighestUnlockedDifficulty;
        string stored = SteamMatchmaking.GetLobbyData(new CSteamID(lobbyIdRaw), TLCeilingKey);
        return int.TryParse(stored, out int ceiling) ? ceiling : ProgressionManager.HighestUnlockedDifficulty;
    }

    public void RefreshFromLobby(Lobby lobby)
    {
        if (playerListContainer == null || playerListEntryPrefab == null || CharacterRegistry.instance == null) return;
        codeButton?.Init(lobby.LobbyId);

        // Every client's own reaction to a join/leave — see the method's own doc comment. Runs before the
        // Resolve* calls below purely for ordering clarity; it doesn't depend on their output.
        DetectMembershipChangeAndUnready(lobby);

        // Host reconciles first so its own read immediately below (both here and everyone else's, once this
        // client's write propagates) reflects the freshest possible assignment — no-ops entirely for non-host
        // clients, whose color always comes purely from reading this same blob, never from self-assignment.
        ResolveColorAssignments(lobby);
        // Before ResolveDifficulty — a down-clamp this pass must already be reflected in what
        // ResolveDifficulty persists into DifficultyKey below, not lag a full pass behind it.
        ResolveDifficultyCeiling(lobby);
        ResolveDifficulty(lobby);

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

        foreach (Transform child in playerListContainer)
            Destroy(child.gameObject);

        foreach (var member in lobby.Members)
        {
            string charName = "...";
            int colorIndex = 0;

            if (member.Id == GameStartData.LocalUserId)
            {
                charName = _isRandom ? "Random" : ExternalClassNames[GameStartData.LocalCharacterIndex];
                colorIndex = GameStartData.LocalColorIndex;
            }
            else
            {
                if (_charSelections.TryGetValue(member.Id, out int charIdx))
                    charName = charIdx == -1 ? "Random" : ExternalClassNames[charIdx];
                if (_colorSelections.TryGetValue(member.Id, out int cIdx))
                    colorIndex = cIdx;
            }

            string colorHex = colorPalette != null && colorPalette.Count > 0
                ? ColorUtility.ToHtmlStringRGB(colorPalette.Get(colorIndex))
                : "FFFFFF";

            bool isReady = member.Id == GameStartData.LocalUserId ? _localIsReady : member.IsReady;
            string readyState = isReady ? "Ready" : "Not Ready";
            string readyColor = isReady ? "green" : "white";
            long power = member.Id == GameStartData.LocalUserId
                ? ProgressionManager.SpentPoints
                : (_powerLevels.TryGetValue(member.Id, out long p) ? p : 0L);
            string powerStr = ProgressionManager.FormatPoints(power);
            // Outer <color> covers the glyph AND the rest of the line (player's own color); the nested
            // <color> for readyState overrides just that one span, then reverts to the outer color for
            // everything after it closes — TMP rich text nests/reverts like a stack.
            string line = $"<color=#{colorHex}>■ <color={readyColor}>{readyState}</color> - {member.DisplayName} - {charName} - Spent Points: {powerStr}</color>";
            var row = Instantiate(playerListEntryPrefab, playerListContainer);
            row.SetData(member.Avatar, line);
        }

        // Re-checked on every lobby refresh, not just once at Awake() — lobbyManager.CurrentLobby isn't
        // necessarily valid yet at Awake()-time for a client still mid-connect, so IsHost could read a stale
        // "no lobby, treat as host" fallback until the first real RefreshFromLobby lands. Keeps the arrows'
        // host-only visibility correct as connection state actually resolves.
        RefreshDifficultyDisplay();
        // Re-checked every pass alongside the arrows above, same reasoning — see IsCeilingResolved's own
        // doc comment for why this is a plain synced-value read, not a per-machine derived guess.
        if (readyButton != null) readyButton.interactable = IsCeilingResolved();
    }

    public void OnKnightClicked()       => SelectCharacter(0);
    public void OnRogueClicked()        => SelectCharacter(1);
    public void OnArcherClicked()       => SelectCharacter(2);
    public void OnBladeweaverClicked()  => SelectCharacter(3);
    public void OnExecutionerClicked()  => SelectCharacter(4);

    // Host-only (2026-08-20) — only the host's own GamePresets value actually carries over into the Game
    // scene, so letting a client change it here would be pure UI lying (their own click would visibly
    // "work" locally but have zero effect on the match that actually starts). No lobby / lobbyManager
    // unassigned (e.g. solo/offline testing) is treated as host — same "nothing to arbitrate" fallback
    // SelectColor's own no-lobby branch already uses.
    private bool IsHost => lobbyManager == null || !lobbyManager.CurrentLobby.IsValid || lobbyManager.CurrentLobby.IsOwner;

    public void OnDifficultyPrevClicked() => ChangeDifficulty(-1);
    public void OnDifficultyNextClicked() => ChangeDifficulty(1);

    private void ChangeDifficulty(int delta)
    {
        // Real logical guard, not just relying on the arrows being hidden for a non-host — see
        // SelectCharacter's own identical LoadingScreen guard for the same "don't just trust the visuals"
        // reasoning.
        if (!IsHost) return;
        if (GamePresets.instance == null) return;
        GamePresets.instance.SetDifficulty(GamePresets.instance.Difficulty + delta);
        // Belt-and-suspenders on top of GamePresets' own personal-unlock clamp, which has no notion of the
        // lobby ceiling — a host whose OWN unlock exceeds the current lobby ceiling could otherwise still
        // click past it via this arrow even though the arrow itself should already be hidden past that point
        // (same "don't just trust the visuals" reasoning SelectCharacter's own LoadingScreen guard uses).
        if (lobbyManager != null && lobbyManager.CurrentLobby.IsValid)
        {
            int ceiling = GetLobbyCeilingOrPersonal();
            if (GamePresets.instance.Difficulty > ceiling) GamePresets.instance.SetDifficulty(ceiling);
        }
        // Written immediately here, not left to the next passive RefreshFromLobby pass — unlike a color
        // pick (which piggybacks on Steam's own member-data-changed callback firing from SetLobbyMemberData
        // even for a mere request), nothing else naturally triggers a lobby refresh right after this click,
        // so without this a client wouldn't see the new difficulty until some UNRELATED lobby event
        // happened to fire next (another player readying up, joining, etc.) — could be a long, confusing wait.
        if (lobbyManager != null) ResolveDifficulty(lobbyManager.CurrentLobby);
        RefreshDifficultyDisplay();
    }

    private void RefreshDifficultyDisplay()
    {
        if (GamePresets.instance == null) return;

        // The host's own local GamePresets value IS the single source of truth (see ChangeDifficulty's own
        // host-only gate and ResolveDifficulty's own doc comment) — a client instead reads back whatever the
        // host most recently resolved into the lobby-wide Steam blob, same "host resolves, everyone reads
        // the shared value" split ResolveColorAssignments/RefreshFromLobby already use for color. Never the
        // other way around — a client's own local GamePresets.Difficulty (whatever THEY last hosted with,
        // completely irrelevant here) must never leak into what gets shown or played in someone else's lobby.
        int difficulty = GamePresets.instance.Difficulty;
        if (!IsHost && lobbyManager != null && lobbyManager.CurrentLobby.IsValid
            && ulong.TryParse(lobbyManager.CurrentLobby.LobbyId, out ulong lobbyIdRaw))
        {
            string stored = SteamMatchmaking.GetLobbyData(new CSteamID(lobbyIdRaw), DifficultyKey);
            if (int.TryParse(stored, out int synced)) difficulty = synced;
        }
        if (difficultyText != null) difficultyText.text = $"Threat Level {difficulty}";
        if (difficultyDescriptionText != null)
        {
            // Matches EnemySpawnerPD's own independent difficultyDamage/Health/MoveSpeed/AttackSpeedGrowthPerLevel
            // rates exactly (20%/100%/10%/10% per level as of 2026-09-01, damage retuned down from 50% — see
            // that field's own doc comment) — difficulty 1 is the no-op baseline (all 0% here), not
            // "difficulty * rate" (which would wrongly show a bonus at difficulty 1 already). Empty at
            // difficulty 1 rather than showing "+0%" for all.
            int levelsAboveBase = difficulty - 1;
            int damagePercent    = levelsAboveBase * 20;
            int healthPercent    = levelsAboveBase * 100;
            // Move Speed and Attack Speed share this one variable/line (2026-08-31) since
            // difficultyMoveSpeedGrowthPerLevel and difficultyAttackSpeedGrowthPerLevel happen to both be 10%
            // today — coincidental, not a true pairing (same reasoning EnemySpawnerPD's own two separate
            // fields exist for). If they're ever retuned to diverge, split this back into two percentages/
            // two lines instead of assuming they stay equal.
            int moveSpeedPercent = levelsAboveBase * 10;
            // Upgrade Points bonus — mirrors GameManagerPD.DifficultyPointMultiplier's own formula by VALUE,
            // not read live off GameManagerPD itself — same "hardcoded to match" convention every other line
            // in this block already uses, since this UI lives in a different scene from GameManagerPD.
            // Linear +100%/level (2026-08-29, reworked from an exponential 1.5^levelsAboveBase curve — see
            // DifficultyPointMultiplier's own doc comment) — retune both together if it ever changes.
            int upgradePointsPercent = levelsAboveBase * 100;
            string upgradePointsLine = levelsAboveBase > 0 ? $"<color=green>+{upgradePointsPercent}% increased Upgrade Points</color>\n" : "";
            // Pure text, describes the tier-count consequence of the shortened TL1-3 win condition (see
            // project_threat_level_shorter_early_wins memory / EnemySpawnerPD.GetBossesToDefeatForWin) — NOT
            // a gameplay change, GetCurrentTierNumber itself is untouched. TL1 sees only tier1 enemies (its
            // 1-boss run ends before any tier-up), TL2 sees tiers 1-2, TL3 sees tiers 1-3, TL4+ sees the full
            // 1-4 range same as before. Capped at 3 (matches "levelsAboveBase" cap shape every other line
            // here already uses) so this doesn't keep climbing past TL4.
            int enemyTierBonus = Mathf.Min(levelsAboveBase, 3);
            string enemyTierLine = enemyTierBonus > 0 ? $"<color=red>+{enemyTierBonus} Enemy Tier{(enemyTierBonus > 1 ? "s" : "")}</color>\n" : "";
            // Threat Level 2's own unique modifier (CharacterStatsPD.ActivateDifficultyModifiers,
            // threatLevel2SlowMultiplier/Duration) — same "levelsAboveBase > 0" gate as the stat lines above
            // since Threat Level 1 has no unique modifier of its own, and this one's threshold happens to be
            // exactly difficulty >= 2 too. Hardcoded text, not read live off CharacterStatsPD's own fields —
            // same convention the three stat lines above already use (matches EnemySpawnerPD's formula by
            // value, not by reference), since this UI lives in a different scene from the player prefab.
            string threatLevel2Line = difficulty >= 2 ? "\nLevel 2: Players are 40% Slowed for 1.5 seconds when they take unshielded damage." : "";
            // Threat Level 3's own unique modifier (EnemySpawnerPD.threatLevel3EliteStatMultiplier) —
            // reworked 2026-08-26 from "Double Elite spawns" to "Double Elite Health/Damage".
            string threatLevel3Line = difficulty >= 3 ? "\nLevel 3: Double Elite Health and Damage." : "";
            // Threat Level 4's own unique modifier (EnemySpawnerPD.threatLevel4PlebMoveSpeedMultiplier).
            string threatLevel4Line = difficulty >= 4 ? "\nLevel 4: Spikies are 25% faster." : "";
            // Threat Level 5's own unique modifier (EnemySpawnerPD.threatLevel5AttackCooldownMultiplier,
            // nerfed 2026-09-01 from 0.5f/"twice as often" to 0.75f/"50% more often").
            string threatLevel5Line = difficulty >= 5 ? "\nLevel 5: Enemies attack 50% more often." : "";
            // Threat Level 6's own unique modifier (EnemySpawnerPD.threatLevel6BossCount).
            string threatLevel6Line = difficulty >= 6 ? "\nLevel 6: Double Boss spawns." : "";
            difficultyDescriptionText.text = levelsAboveBase > 0
                ? $"{upgradePointsLine}{enemyTierLine}+{damagePercent}% Enemy Damage\n+{healthPercent}% Enemy Health\n+{moveSpeedPercent}% Enemy Move Speed and Attack Speed{threatLevel2Line}{threatLevel3Line}{threatLevel4Line}{threatLevel5Line}{threatLevel6Line}"
                : "";
        }

        // Hidden entirely (SetActive, not Button.interactable) rather than just greyed — either because a
        // non-host has no business touching this at all (see IsHost's own doc comment), or because there's
        // nothing further to click in that direction: Prev at the absolute floor, Next at whichever comes
        // first — the LOBBY ceiling (min of every current member's own unlock, see GetLobbyCeilingOrPersonal's
        // own doc comment for why this is no longer just the host's own personal unlock), or (the common
        // case) simply not having unlocked anything past this yet. GamePresets.SetDifficulty enforces the
        // same HighestUnlockedDifficulty clamp independently, so this is belt-and-suspenders with that, not
        // the only thing stopping a locked pick.
        bool isHost = IsHost;
        int highestUnlocked = GetLobbyCeilingOrPersonal();
        if (difficultyPrevArrow != null) difficultyPrevArrow.SetActive(isHost && difficulty > GamePresets.MinDifficulty);
        if (difficultyNextArrow != null) difficultyNextArrow.SetActive(isHost && difficulty < highestUnlocked);
    }

    public void OnRandomClicked()
    {
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsShowing) return;

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
                ability1Card.AbilitySetup(ability.Name, ability, ability.Icon);
                ability1Card.SetInteractive(false);
                count++;
            }
            foreach (var ability in abilities)
            {
                if (ability == null || ability.Slot != AbilitySlot.Ability2) continue;
                var ability2Card = GetDetailCard(count);
                ability2Card.gameObject.SetActive(true);
                ability2Card.AbilitySetup(ability.Name, ability, ability.Icon);
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
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsShowing) return;
        // Real logical guard, not just relying on the button's own visible interactable state — same
        // "don't just trust the visuals" reasoning ChangeDifficulty's own belt-and-suspenders clamp uses.
        if (readyButton != null && !readyButton.interactable) return;
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
    // after GameStartData.LocalCharacterIndex got reset back to GameStartData.SavedCharacterIndex by
    // InitializeForLobby), and the cached
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
        // null, not .Clear() — see the field's own doc comment for why "no snapshot yet" must stay
        // distinguishable from "snapshot was an empty set".
        _lastKnownMemberIds = null;
    }

    // Just files a REQUEST — never claims the color directly. The host (see ResolveColorAssignments) is the
    // only thing that ever actually grants it, via ColorAssignmentsKey; GameStartData.LocalColorIndex only
    // updates once RefreshFromLobby reads that confirmation back, not optimistically here. For the host's
    // own clicks this resolves within the same RefreshFromLobby call below (no network round-trip needed to
    // read back its own just-written Steam data); for a non-host client it resolves once the host's answer
    // propagates back, same latency as everyone else's data in this screen.
    private void SelectColor(int index)
    {
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsShowing) return;

        // Saved regardless of whether the host actually grants this particular request this session — this
        // records what the player WANTS, for LobbySceneController.InitializeForLobby to request again
        // automatically next time they join a lobby (see GameStartData.SavedColorIndex's own doc comment).
        GameStartData.SavedColorIndex = index;

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
        // Locked out while LoadingScreen is up — covers an ordinary match-start attempt (shown from
        // LobbySceneController.OnAllReady/OnRoomUpdated through TryLoadGameScene). A real logical guard, not
        // just relying on the loading screen visually covering this panel, in case that ever fails to render
        // for some reason — see LoadingScreen.IsShowing's own doc comment. Every other character-select
        // button below guards the same way.
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsShowing) return;

        _isRandom = false;
        GameStartData.LocalCharacterIndex = index;
        // See GameStartData.SavedCharacterIndex's own doc comment — restored automatically next time this
        // player joins a lobby.
        GameStartData.SavedCharacterIndex = index;
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
