using System;
using UnityEngine;

/// <summary>
/// Play 씬 진입점 (교환 플레이 모드 — Docs/202 3장). MatchData.OpponentMap 을 PlaySession 으로 띄운다.
/// GameFlow(Boot) 가 이 씬을 애디티브로 로드하고 static Finished 이벤트로 결과를 받는다.
/// 씬을 단독으로 실행하면(MatchData 에 상대 맵이 없으면) 데모 맵으로 동작해 혼자서도 테스트할 수 있다.
/// </summary>
public class PlayBootstrap : MonoBehaviour
{
    /// <summary>교환 플레이 종료 (클리어 / 기권 / 시간 만료). GameFlow 가 구독.</summary>
    public static event Action<PlayResult> Finished;

    public PlaySession Session { get; private set; }

    void Awake()
    {
        Application.runInBackground = true;
        var cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera (runtime)");
            go.tag = "MainCamera";
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
            if (gameObject.scene.IsValid() && go.scene != gameObject.scene)
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, gameObject.scene);   // 애디티브 언로드 시 함께 정리
        }
        if (UnityEngine.EventSystems.EventSystem.current == null && FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var es = new GameObject("EventSystem (runtime)");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.17f, 0.2f);
        CanvasView.FitCamera(cam, 0.08f, 0f);   // 상단 HUD 바만 비워 둔다
    }

    void Start()
    {
        var data = MatchData.Instance;
        var map = data.OpponentMap;
        string title;
        if (map == null)
        {
            map = DemoMap();
            title = "교환 플레이 (데모 맵 — 상대 맵 없음)";
        }
        else title = $"교환 플레이 — {data.OpponentNickname}의 맵";

        Session = PlaySession.Begin(map, title, transform, data.MyVows);   // 교환 플레이 = 자기 뜻 (Docs/100 4.2)
        Session.TimeLimit = data.Settings.PlayTimeLimit;
        Session.AttemptLimit = data.Settings.AttemptLimit;
        Session.AbortMeansGiveUp = true;
        Session.AbortLabel = "기권 (ESC)";
        Session.RefreshAbortLabel();
        Session.Completed += OnFinished;
    }

    void OnFinished(PlayResult r)
    {
        Finished?.Invoke(r);
    }

    void OnDestroy()
    {
        if (Session != null) Session.Completed -= OnFinished;
    }

    static MapData DemoMap()
    {
        var m = new MapData();
        m.Strokes.Add(new StrokeData { Width = 0.3f, ColorId = 0, Points = { new Vector2(6f, 0f), new Vector2(6f, 2.5f) } });
        m.Strokes.Add(new StrokeData { Width = 0.3f, ColorId = 0, Points = { new Vector2(10f, 3f), new Vector2(14f, 3f) } });
        m.Strokes.Add(new StrokeData { Width = 0.3f, ColorId = 0, Points = { new Vector2(17f, 5f), new Vector2(21f, 5f) } });
        m.GoalPos = new Vector2(24f, 1f);
        return m;
    }
}
