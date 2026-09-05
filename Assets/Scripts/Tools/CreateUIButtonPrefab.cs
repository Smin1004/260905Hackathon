#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class CreateUIButtonPrefab
{
    const string PrefabFolder = "Assets/Prefabs/UI";
    const string PrefabPath = PrefabFolder + "/UIButton.prefab";
    const string ThemePath = "Assets/Resources/UITheme.asset";
    const string ScenePath = "Assets/Scenes/ButtonUIScene.unity";

    [MenuItem("Tools/UI/Create Button Kit")]
    public static void CreateButtonKit()
    {
        EnsureFolder("Assets/Prefabs");
        EnsureFolder(PrefabFolder);
        EnsureFolder("Assets/Resources");

        var theme = AssetDatabase.LoadAssetAtPath<UITheme>(ThemePath);
        if (theme == null)
        {
            theme = ScriptableObject.CreateInstance<UITheme>();
            theme.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            AssetDatabase.CreateAsset(theme, ThemePath);
        }

        var buttonObject = new GameObject("UIButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(ButtonThemeApplier));
        var buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.sizeDelta = new Vector2(320, 64);
        buttonRect.pivot = new Vector2(0.5f, 0.5f);

        var image = buttonObject.GetComponent<Image>();
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.RemoveAllListeners();

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        labelObject.transform.SetParent(buttonObject.transform, false);
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(12, 4);
        labelRect.offsetMax = new Vector2(-12, -4);
        var label = labelObject.GetComponent<Text>();
        label.text = "BUTTON";
        label.alignment = TextAnchor.MiddleCenter;
        label.fontSize = 22;
        label.fontStyle = FontStyle.Bold;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;

        var applier = buttonObject.GetComponent<ButtonThemeApplier>();
        applier.SetTheme(theme);
        PrefabUtility.SaveAsPrefabAsset(buttonObject, PrefabPath);
        Object.DestroyImmediate(buttonObject);

        BuildScene(theme);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Created UI button kit and placed it in ButtonUIScene.");
    }

    static void BuildScene(UITheme theme)
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var oldLayout = Object.FindFirstObjectByType<ButtonUISceneLayout>();
        if (oldLayout != null) Object.DestroyImmediate(oldLayout);

        var canvasObject = GameObject.Find("UI Button Kit Canvas");
        if (canvasObject == null)
        {
            canvasObject = new GameObject("UI Button Kit Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var labels = new[] { "방 만들기", "방 참가", "방 코드 복사", "뜻 후보 1", "뜻 후보 2", "뜻 후보 3", "뜻 선택 완료", "준비 완료", "게임 종료" };
        var anchors = new[]
        {
            new Vector4(0.28f, 0.72f, 0.48f, 0.82f), new Vector4(0.52f, 0.72f, 0.72f, 0.82f), new Vector4(0.40f, 0.61f, 0.60f, 0.68f),
            new Vector4(0.24f, 0.42f, 0.38f, 0.54f), new Vector4(0.43f, 0.42f, 0.57f, 0.54f), new Vector4(0.62f, 0.42f, 0.76f, 0.54f),
            new Vector4(0.34f, 0.25f, 0.50f, 0.33f), new Vector4(0.50f, 0.25f, 0.66f, 0.33f), new Vector4(0.42f, 0.12f, 0.58f, 0.19f)
        };

        for (int i = 0; i < labels.Length; i++)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, canvasObject.transform);
            instance.name = "Button - " + labels[i];
            var rect = instance.GetComponent<RectTransform>();
            var a = anchors[i];
            rect.anchorMin = new Vector2(a.x, a.y);
            rect.anchorMax = new Vector2(a.z, a.w);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            instance.GetComponentInChildren<Text>().text = labels[i];
            instance.GetComponent<ButtonThemeApplier>().SetTheme(theme);
        }

        EditorSceneManager.SaveScene(scene);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = path.Substring(0, path.LastIndexOf('/'));
        var folder = path.Substring(path.LastIndexOf('/') + 1);
        AssetDatabase.CreateFolder(parent, folder);
    }
}
#endif
