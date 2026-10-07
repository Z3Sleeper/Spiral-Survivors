using UnityEngine;

// Sits on the BuffBar GameObject (the one with the Horizontal Layout Group + Content Size Fitter) — plain
// singleton, matching LocalUIManagerPD's own local-only-UI convention, since this is purely cosmetic
// per-viewer UI, not part of the simulation. A plain Instantiate() (not hierarchy.Create()) is correct
// here for the same reason — nothing about a buff icon needs to be replicated/predicted, it's local
// presentation only.
public class BuffBarPD : MonoBehaviour
{
    public static BuffBarPD instance;

    [SerializeField] private BuffIconPD buffIconPrefab;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    // Not DontDestroyOnLoad — see LocalUIManagerPD.OnDestroy's own doc comment for the full reasoning
    // (guarded against nulling out a newer instance; without this, `instance` was left dangling on a
    // destroyed object across a scene return to lobby).
    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    // Spawns a new icon into the bar — caller keeps the returned reference to update (SetCooldownFill) or
    // remove it later. Parented directly under this transform so the Horizontal Layout Group picks it up
    // automatically.
    public BuffIconPD AddBuffIcon(Sprite sprite)
    {
        if (buffIconPrefab == null) return null;
        var iconInstance = Instantiate(buffIconPrefab, transform);
        iconInstance.SetIcon(sprite);
        return iconInstance;
    }

    public void RemoveBuffIcon(BuffIconPD iconInstance)
    {
        if (iconInstance != null) Destroy(iconInstance.gameObject);
    }
}
