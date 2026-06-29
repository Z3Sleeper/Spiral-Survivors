using System.Collections.Generic;
using UnityEngine;

public class AugmentListUI : MonoBehaviour
{
    [SerializeField] private Transform container;
    [SerializeField] private AugmentListEntry entryPrefab;

    private readonly List<AugmentListEntry> _pool = new();

    private void OnEnable() => Refresh();

    private void Refresh()
    {
        foreach (var entry in _pool)
            entry.gameObject.SetActive(false);

        CharacterStats localStats = null;
        foreach (var s in CharacterStats.All)
            if (s.isOwner) { localStats = s; break; }

        if (localStats == null || !localStats.owner.HasValue) return;

        var picked = AugmentManager.instance.GetPickedAugmentsForPlayer(localStats.owner.Value);
        if (picked == null) return;
        int i = 0;
        foreach (var augment in picked)
        {
            var entry = GetEntry(i++);
            entry.Setup(augment);
            entry.gameObject.SetActive(true);
        }
    }

    private AugmentListEntry GetEntry(int index)
    {
        if (index < _pool.Count) return _pool[index];
        var entry = Instantiate(entryPrefab, container);
        _pool.Add(entry);
        return entry;
    }
}
