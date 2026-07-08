using UnityEngine;

public class PlayableGraphCleaner : MonoBehaviour
{
    private void OnApplicationQuit()
    {
        foreach (var animator in Resources.FindObjectsOfTypeAll<Animator>())
        {
            if (animator.playableGraph.IsValid())
                animator.playableGraph.Destroy();
            animator.enabled = false;
        }
    }
}
