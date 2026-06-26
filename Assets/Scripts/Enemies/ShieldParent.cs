using UnityEngine;

public class ShieldParent : MonoBehaviour
{
    private GloomShield[] _shields;

    private void Start()
    {
        _shields = GetComponentsInChildren<GloomShield>();
    }

    public void OnShieldTriggered(GloomShield source)
    {
        foreach (var shield in _shields)
            if (shield != source)
                shield.TriggerWindup();
    }
}
