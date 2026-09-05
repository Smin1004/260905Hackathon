using UnityEngine;

/// <summary>
/// ButtonUIScene 전용 정적 UI 배치. 버튼은 시각 확인용이며 기능을 연결하지 않는다.
/// </summary>
public sealed class ButtonUISceneLayout : MonoBehaviour
{
    bool _built;

    void Awake()
    {
        Build();
    }

    void Build()
    {
        if (_built) return;
        _built = true;

        var canvas = RuntimeUI.Canvas("Button Layout Canvas", 10);
        var root = canvas.transform;
        var buttonColor = new Color(0.16f, 0.21f, 0.28f, 1f);
        var accentColor = new Color(0.18f, 0.48f, 0.68f, 1f);
        var secondaryColor = new Color(0.28f, 0.31f, 0.36f, 1f);

        RuntimeUI.Button(root, new Vector2(0.28f, 0.72f), new Vector2(0.48f, 0.82f), "방 만들기", NoAction, accentColor, 24);
        RuntimeUI.Button(root, new Vector2(0.52f, 0.72f), new Vector2(0.72f, 0.82f), "방 참가", NoAction, buttonColor, 24);
        RuntimeUI.Button(root, new Vector2(0.40f, 0.61f), new Vector2(0.60f, 0.68f), "방 코드 복사", NoAction, secondaryColor, 20);

        RuntimeUI.Button(root, new Vector2(0.24f, 0.42f), new Vector2(0.38f, 0.54f), "뜻 후보 1", NoAction, buttonColor, 20);
        RuntimeUI.Button(root, new Vector2(0.43f, 0.42f), new Vector2(0.57f, 0.54f), "뜻 후보 2", NoAction, buttonColor, 20);
        RuntimeUI.Button(root, new Vector2(0.62f, 0.42f), new Vector2(0.76f, 0.54f), "뜻 후보 3", NoAction, buttonColor, 20);

        RuntimeUI.Button(root, new Vector2(0.34f, 0.25f), new Vector2(0.50f, 0.33f), "뜻 선택 완료", NoAction, accentColor, 20);
        RuntimeUI.Button(root, new Vector2(0.50f, 0.25f), new Vector2(0.66f, 0.33f), "준비 완료", NoAction, secondaryColor, 20);
        RuntimeUI.Button(root, new Vector2(0.42f, 0.12f), new Vector2(0.58f, 0.19f), "게임 종료", NoAction, new Color(0.50f, 0.22f, 0.24f, 1f), 20);
    }

    static void NoAction()
    {
    }
}
