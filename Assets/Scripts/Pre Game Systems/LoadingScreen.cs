using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class LoadingScreen : MonoBehaviour
{
    [SerializeField] private GameObject canvasGameObject;
    [SerializeField] private TextMeshProUGUI loadPercentText;
    [SerializeField] private Slider loadingBar;
    public static LoadingScreen instance;
    private AsyncOperation _op;
    
    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void SetOperation(AsyncOperation op)
    {
        _op = op;
    }

    private void Update()
    {
        if (_op == null) return;
        float progress = _op.progress;
        // set your loading bar fill to progress
        loadPercentText.text = (progress * 100).ToString() + "%";
        loadingBar.value = progress;
    }
    
    public void Show() => canvasGameObject.SetActive(true);
    public void Hide() => canvasGameObject.SetActive(false);
}