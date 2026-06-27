using UnityEngine;

[CreateAssetMenu(fileName = "PlayerColorPalette", menuName = "Spiral Survivors/Player Color Palette")]
public class PlayerColorPalette : ScriptableObject
{
    public Color[] colors = new Color[0];

    public int Count => colors.Length;

    public Color Get(int index) => colors[Mathf.Clamp(index, 0, colors.Length - 1)];
}
