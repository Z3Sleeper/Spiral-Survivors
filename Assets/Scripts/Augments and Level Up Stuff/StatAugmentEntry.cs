using UnityEngine;

[System.Serializable]
public class StatAugmentEntry
{
    public StatType statType;
    public string displayName;
    public Color color = Color.white;
    public float value;
    public string prependText;
    public string appendText;

    public string ColoredName => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{displayName}</color>";
    public string Colored(string text) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
}