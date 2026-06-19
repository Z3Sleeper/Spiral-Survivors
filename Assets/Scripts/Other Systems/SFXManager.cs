using UnityEngine;
using UnityEngine.Audio;

public class SFXManager : MonoBehaviour
{
    public static SFXManager instance;

    [SerializeField] private AudioMixerGroup sfxGroup;
    [SerializeField] private int poolSize = 64;

    private AudioSource[] _pool;
    private int _index;
    private InputSystem_Actions _input;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);

        _input = new InputSystem_Actions();
        _input.Enable();

        _pool = new AudioSource[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            var go = new GameObject("SFXSource");
            go.transform.SetParent(transform);
            _pool[i] = go.AddComponent<AudioSource>();
            _pool[i].outputAudioMixerGroup = sfxGroup;
        }
    }

    private void Update()
    {
        if (_input.Player.Mute.WasPressedThisFrame())
            AudioListener.volume = AudioListener.volume > 0f ? 0f : 1f;
    }

    public void Play(AudioClip clip, Vector3 position, float volume = 1f)
    {
        if (clip == null) return;
        var source = _pool[_index % poolSize];
        _index++;
        source.transform.position = position;
        source.clip = clip;
        source.volume = volume;
        source.pitch = Random.Range(0.9f, 1.1f);
        source.Play();
    }
}
