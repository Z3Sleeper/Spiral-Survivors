using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Ported from _OldSystemArchive/UI/AugmentListUI.cs — this component's own GameObject (Augment List) stays
// permanently active now; container (List Container, holding the GridLayoutGroup) is what actually shows/
// hides, via SetVisible below, called from LocalUIManagerPD.SetAugmentListVisible on Tab hold/release.
public class AugmentListUIPD : MonoBehaviour
{
    [SerializeField] private RectTransform container;
    [SerializeField] private AugmentListEntryPD entryPrefab;

    private readonly List<AugmentListEntryPD> _pool = new();
    private readonly List<AugmentData> _picked = new();
    private GridLayoutGroup _gridLayoutGroup;
    private Coroutine _fixLayoutRoutine;

    private void Awake() => _gridLayoutGroup = container.GetComponent<GridLayoutGroup>();

    public void SetVisible(bool visible)
    {
        container.gameObject.SetActive(visible);
        if (visible) Refresh();
    }

    private void Refresh()
    {
        foreach (var entry in _pool)
            entry.gameObject.SetActive(false);

        if (AugmentManagerPD.instance == null) return;

        // No CharacterStatsPD.Local equivalent in PD — resolved the same way EnemySpawnerPD/etc. find the
        // local player elsewhere in this codebase, by scanning CharacterStatsPD.All for isOwner.
        CharacterStatsPD localStats = null;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (!stats.isOwner) continue;
            localStats = stats;
            break;
        }
        if (localStats == null || !localStats.owner.HasValue) return;

        AugmentManagerPD.instance.GetPickedAugmentsForPlayer(localStats.owner.Value, _picked);
        for (int i = 0; i < _picked.Count; i++)
        {
            var entry = GetEntry(i);
            entry.Setup(_picked[i], localStats);
            entry.gameObject.SetActive(true);
        }

        // container just activated THIS frame (SetVisible above) — Unity hasn't fully resolved its layout
        // yet, so a rebuild forced synchronously here still computes against stale data (confirmed live).
        // Deferred one frame so it runs after Unity's own activation pass settles.
        if (_fixLayoutRoutine != null) StopCoroutine(_fixLayoutRoutine);
        _fixLayoutRoutine = StartCoroutine(FixLayoutNextFrame());
    }

    private IEnumerator FixLayoutNextFrame()
    {
        yield return null;
        // Disable/re-enable the GridLayoutGroup itself — runs Unity's own OnEnable -> SetDirty path, the
        // same path an Inspector edit uses (which is what fixed this manually) — SetActive on the pooled
        // entries alone doesn't reliably trigger this.
        _gridLayoutGroup.enabled = false;
        _gridLayoutGroup.enabled = true;
        LayoutRebuilder.ForceRebuildLayoutImmediate(container);
        _fixLayoutRoutine = null;
    }

    private AugmentListEntryPD GetEntry(int index)
    {
        if (index < _pool.Count) return _pool[index];
        var entry = Instantiate(entryPrefab, container);
        _pool.Add(entry);
        return entry;
    }
}
