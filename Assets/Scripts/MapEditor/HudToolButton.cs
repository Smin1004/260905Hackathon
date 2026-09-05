using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 도구 패널·굵기 칩용 버튼 — 선택(active)/비활성(disabled) 상태에 따라 배경 스프라이트와 아이콘·라벨 색을 바꾼다.
/// 호버·눌림은 Button 의 ColorTint 로 처리하고, 상태 스프라이트는 여기서 직접 교체한다.
/// 프리팹에서 참조만 채우면 되고 로직은 MapEditorHud 가 Apply() 로 호출한다.
/// </summary>
public class HudToolButton : MonoBehaviour
{
    public Button Button;
    public Image Background;
    public Image Icon;          // 없어도 됨
    public Text Label;          // 없어도 됨

    [Header("배경 스프라이트")]
    public Sprite Normal;
    public Sprite Active;
    public Sprite Disabled;

    [Header("아이콘·라벨 색")]
    public Color ContentNormal = Color.white;
    public Color ContentActive = new Color32(18, 37, 60, 255);
    public Color ContentDisabled = new Color32(90, 108, 132, 255);

    public bool IsActive { get; private set; }

    public void Apply(bool active, bool interactable)
    {
        IsActive = active;
        if (Button != null) Button.interactable = interactable;
        if (Background != null)
        {
            var s = !interactable ? Disabled : active ? Active : Normal;
            if (s != null) Background.sprite = s;
        }
        var c = !interactable ? ContentDisabled : active ? ContentActive : ContentNormal;
        if (Icon != null) Icon.color = c;
        if (Label != null) Label.color = c;
    }
}
