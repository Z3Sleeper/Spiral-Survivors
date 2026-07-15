using System.Collections.Generic;
using UnityEngine;

// Editor-only authoring data — register the named colors you use in AugmentData descriptions here
// once, then AugmentDataEditor reads this list to offer one-click "wrap selected text" buttons. Never
// referenced by runtime code (the color tags themselves get baked directly into the strings), so this
// intentionally lives in an Editor folder rather than shipping in builds.
[CreateAssetMenu(fileName = "RichTextColorPalette", menuName = "Spiral Survivors/Rich Text Color Palette")]
public class RichTextColorPalette : ScriptableObject
{
    [System.Serializable]
    public struct NamedColor
    {
        public string name;
        public Color color;
    }

    public List<NamedColor> colors = new();
}
