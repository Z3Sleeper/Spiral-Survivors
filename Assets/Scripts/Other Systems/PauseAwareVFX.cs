using UnityEngine;

public class PauseAwareVFX : MonoBehaviour
{
    private ParticleSystem[] _particles;
    private Animator _animator;

    private void Awake()
    {
        _particles = GetComponentsInChildren<ParticleSystem>(true);
        TryGetComponent(out _animator);
        GameManager.OnPauseChanged += OnPauseChanged;
    }

    private void Start()
    {
        if (GameManager.instance != null && GameManager.instance.IsPaused)
            OnPauseChanged(true);
    }

    private void OnDestroy()
    {
        GameManager.OnPauseChanged -= OnPauseChanged;
    }

    private void OnPauseChanged(bool paused)
    {
        foreach (var ps in _particles)
        {
            if (paused) ps.Pause();
            else ps.Play();
        }
        if (_animator != null)
            _animator.speed = paused ? 0f : 1f;
    }
}
