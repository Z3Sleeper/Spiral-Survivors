using UnityEngine;

public class MusicPlayer : MonoBehaviour
{
    public static MusicPlayer instance;
    [SerializeField] private AudioSource introSource;
    [SerializeField] private AudioSource loopSource;
    [SerializeField] private AudioClip intro;
    [SerializeField] private AudioClip loop;

    private void Awake()
    {
        instance = this;
        introSource.clip = intro;
        introSource.loop = false;
        loopSource.clip = loop;
        loopSource.loop = true;
    }

    // Not DontDestroyOnLoad — see LocalUIManagerPD.OnDestroy's own doc comment for the full reasoning
    // (guarded against nulling out a newer instance; without this, `instance` was left dangling on a
    // destroyed object across a scene return to lobby).
    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    public void Play()
    {
        double startTime = AudioSettings.dspTime + 0.1;

        // intro is optional — leave it unassigned in the Inspector for a theme with no intro, and loopSource
        // starts immediately instead of waiting on an intro clip that doesn't exist.
        if (intro == null)
        {
            loopSource.PlayScheduled(startTime);
            return;
        }

        double introLength = (double)intro.samples / intro.frequency;
        introSource.PlayScheduled(startTime);
        loopSource.PlayScheduled(startTime + introLength);
    }
}
