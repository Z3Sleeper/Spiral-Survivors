using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

// Populates the Encyclopedia panel's Content grid with a display-only card per augment in
// augmentDatabase — reuses the same AugmentCardUI/prefab the real level-up flow uses, just with the pick
// button disabled (LocalUIManagerPD.instance doesn't exist in the lobby scene, so a real pick can't happen
// here anyway). Pools its instantiated cards like AugmentListUIPD does, so re-opening the panel doesn't
// re-instantiate from scratch.
public class AugmentEncyclopediaUI : MonoBehaviour
{
    [SerializeField] private AugmentDatabase augmentDatabase;
    [SerializeField] private AugmentCardUI cardPrefab;
    [SerializeField] private Transform content;

    private readonly List<AugmentCardUI> _pool = new();

    private void OnEnable() => Populate();

    private void Populate()
    {
        if (augmentDatabase == null || cardPrefab == null || content == null) return;

        // Sorted by displayName (the title actually shown on the card) rather than the database's stored
        // order or the ScriptableObject asset's file name — those don't correspond to each other at all.
        // displayName itself is stored WITH its rich-text color markup already baked in (e.g.
        // "<color=#FF0000>Critxecute</color>"), so comparing it raw would sort by hex code, not by the
        // visible title — StripRichText is only used for the comparison key, never for what's shown.
        var sorted = new List<AugmentData>(augmentDatabase.augments);
        sorted.RemoveAll(a => a == null);
        sorted.Sort((a, b) => string.Compare(StripRichText(a.displayName), StripRichText(b.displayName), System.StringComparison.OrdinalIgnoreCase));

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
