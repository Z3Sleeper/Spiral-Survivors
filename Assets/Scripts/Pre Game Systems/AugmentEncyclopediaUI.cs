using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

// Populates the Encyclopedia panel's Content grid with a display-only card per augment in
// augmentDatabase — reuses the same AugmentCardUI/prefab the real level-up flow uses, just with the pick
// button disabled (LocalUIManagerPD.instance doesn't exist in the lobby scene, so a real pick can't happen
// here anyway). Pools its instantiated cards like AugmentListUIPD does, so re-opening the panel doesn't
// re-instantiate from scratch.
//
// Populated exactly once, in Start() (2026-10-18) — the panel itself is now shown/hidden via CanvasGroup
// (LobbySceneController.SetEncyclopediaVisible), not SetActive, specifically so repeat opens never re-enable
// ~140 cards' worth of GameObjects and pay Unity's own Canvas/layout rebuild cost for them. A full Lobby
// scene load (LoadSceneMode.Single, confirmed in ReturnToLobbyManager) happens every time the player returns
// from a match, which destroys and recreates this component fresh — so Start() firing once per instance is
// exactly once per lobby visit, same timing GrantQualifyingUnlocks() already relied on to catch a just-won
// unlock before this change.
public class AugmentEncyclopediaUI : MonoBehaviour
{
    [SerializeField] private AugmentDatabase augmentDatabase;
    [SerializeField] private AugmentCardUI cardPrefab;
    [SerializeField] private Transform content;
    // Only needed to proactively re-check qualification right before filtering below — see Populate()'s own
    // GrantQualifyingUnlocks() call for why. Optional (null-checked): the filter still works correctly
    // without it, just potentially one match-start behind a tag the player's account already qualifies for.
    [SerializeField] private AugmentUnlockTagDatabase unlockTagDatabase;

    private readonly List<AugmentCardUI> _pool = new();

    private void Start() => Populate();

    private void Populate()
    {
        if (augmentDatabase == null || cardPrefab == null || content == null) return;

        // Normally only runs once per actual match spawn (CharacterStatsPD.ComputeUnlockedAugmentTagMask)
        // — the Lobby scene has no live character, so without this, a tag the player's account JUST
        // qualified for (e.g. reaching Threat Level 3 via the shop) wouldn't actually get written to
        // ProgressionManager's save-backed unlockedAugmentTags until their NEXT match start, leaving the
        // Encyclopedia showing it as locked even though they already qualify. Idempotent/cheap to call here
        // too — see its own doc comment.
        unlockTagDatabase?.GrantQualifyingUnlocks();

        // Sorted by displayName (the title actually shown on the card) rather than the database's stored
        // order or the ScriptableObject asset's file name — those don't correspond to each other at all.
        // displayName itself is stored WITH its rich-text color markup already baked in (e.g.
        // "<color=#FF0000>Critxecute</color>"), so comparing it raw would sort by hex code, not by the
        // visible title — StripRichText is only used for the comparison key, never for what's shown.
        var sorted = new List<AugmentData>(augmentDatabase.augments);
        // Hides anything gated behind an unlockTag the player hasn't earned yet (2026-10-12) — checked via
        // ProgressionManager.IsAugmentTagUnlocked, the PERSISTENT save-backed unlock (tied to
        // HighestUnlockedDifficulty via AugmentUnlockTagDatabase's own unlockConditions), deliberately NOT
        // the live per-run CharacterStatsPD.UnlockedAugmentTagMask/AugmentManagerPD.IsAugmentTagUnlocked
        // check (that one doesn't exist outside an active match anyway — no live character in this scene —
        // and per Stuart's own explicit framing isn't the one that should gate Encyclopedia visibility).
        // Empty/null unlockTag (every augment by default) always passes, same "no restriction" convention
        // unlockTag's own doc comment on AugmentData establishes.
        sorted.RemoveAll(a => a == null || (!string.IsNullOrEmpty(a.unlockTag) && !ProgressionManager.IsAugmentTagUnlocked(a.unlockTag)));

        // Sort key computed once per augment up front (2026-10-18 perf fix) rather than inside the
        // comparator — List<T>.Sort calls its comparator O(n log n) times, so stripping rich text there
        // meant ~1000 regex passes for ~140 augments instead of the ~140 actually needed. Same sort result,
        // just without the redundant repeat work.
        var sortKeys = new Dictionary<AugmentData, string>(sorted.Count);
        foreach (var a in sorted) sortKeys[a] = StripRichText(a.displayName);
        sorted.Sort((a, b) => string.Compare(sortKeys[a], sortKeys[b], System.StringComparison.OrdinalIgnoreCase));

        for (int i = 0; i < sorted.Count; i++)
        {
            var card = GetCard(i);
            card.gameObject.SetActive(true);
            card.AugmentSetup(sorted[i], i);
            card.SetInteractive(false);
        }

        for (int i = sorted.Count; i < _pool.Count; i++)
            _pool[i].gameObject.SetActive(false);
    }

    private AugmentCardUI GetCard(int index)
    {
        if (index < _pool.Count) return _pool[index];
        var card = Instantiate(cardPrefab, content);
        _pool.Add(card);
        return card;
    }

    private static string StripRichText(string text) =>
        string.IsNullOrEmpty(text) ? text : Regex.Replace(text, "<.*?>", string.Empty);
}
