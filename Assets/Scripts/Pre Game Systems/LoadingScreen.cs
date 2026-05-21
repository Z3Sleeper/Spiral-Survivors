using TMPro;
using UnityEngine;

public class LoadingScreen : MonoBehaviour
{
    [SerializeField] private GameObject canvasGameObject;
    public static LoadingScreen instance;
    
    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void Show() => canvasGameObject.SetActive(true);
    public void Hide() => canvasGameObject.SetActive(false);
}