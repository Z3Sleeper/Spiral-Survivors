using System.Collections.Generic;
using UnityEngine;

// Ported from _OldSystemArchive/UI/AugmentListUI.cs — refreshes itself whenever this panel becomes
// active, which happens exactly when PlayerControllerPD's Tab-hold calls
// LocalUIManagerPD.SetAugmentListVisible(true), so no separate "refresh on pick" hook is needed.
public class AugmentListUIPD : MonoBehaviour
{
    [SerializeField] private Transform container;
    [SerializeField] private AugmentListEntryPD entryPrefab;

    private readonly List<AugmentListEntryPD> _pool = new();
    private readonly List<AugmentData> _picked = new();

    private void OnEnable() => Refresh();

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
    }

    private AugmentListEntryPD GetEntry(int index)
    {
        if (index < _pool.Count) return _pool[index];
        var entry = Instantiate(entryPrefab, container);
        _pool.Add(entry);
        return entry;
    }
}
