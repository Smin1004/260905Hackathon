using UnityEngine;

[CreateAssetMenu(menuName = "UI/Theme", fileName = "UITheme")]
public sealed class UITheme : ScriptableObject
{
    public Font font;
    public Color buttonNormal = new Color(0.16f, 0.21f, 0.28f, 1f);
    public Color buttonHighlighted = new Color(0.24f, 0.38f, 0.52f, 1f);
    public Color buttonPressed = new Color(0.10f, 0.16f, 0.22f, 1f);
    public Color buttonDisabled = new Color(0.28f, 0.30f, 0.33f, 1f);
    public Color buttonText = Color.white;
    public Color accent = new Color(0.18f, 0.48f, 0.68f, 1f);
}
