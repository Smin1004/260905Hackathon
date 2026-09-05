using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
[RequireComponent(typeof(Button))]
public sealed class ButtonThemeApplier : MonoBehaviour
{
    [SerializeField] UITheme theme;

    public void SetTheme(UITheme value)
    {
        theme = value;
        Apply();
    }

    void OnEnable()
    {
        Apply();
    }

    void OnValidate()
    {
        Apply();
    }

    public void Apply()
    {
        if (theme == null) return;

        var button = GetComponent<Button>();
        var image = GetComponent<Image>();
        var label = GetComponentInChildren<Text>();
        if (image != null) image.color = theme.buttonNormal;
        if (label != null)
        {
            label.font = theme.font;
            label.color = theme.buttonText;
        }

        var colors = button.colors;
        colors.normalColor = theme.buttonNormal;
        colors.highlightedColor = theme.buttonHighlighted;
        colors.pressedColor = theme.buttonPressed;
        colors.disabledColor = theme.buttonDisabled;
        button.colors = colors;
    }
}
