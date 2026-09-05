#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// SoundBank 에셋 생성기 (에디터 전용 — Docs/201 6장 규칙상 Editor 폴더 대신 #if UNITY_EDITOR).
/// 메뉴 [Chojiilgwan > Build SoundBank]: Assets/Audio 의 파일을 이름 규약으로 Resources/Audio/SoundBank.asset 에 연결한다.
///   bgm_lobby_edit / bgm_battle / sfx_click / sfx_jump / sfx_land / sfx_confirm / sfx_drawing / sfx_eraser / sfx_clock (확장자 무관)
/// 이미 있는 에셋은 클립 참조만 갱신하고 볼륨 등 수치는 유지한다.
/// </summary>
public static class SoundBankBuilder
{
    const string AudioDir = "Assets/Audio";
    const string AssetPath = "Assets/Resources/" + SoundBank.ResourcePath + ".asset";

    [MenuItem("Chojiilgwan/Build SoundBank")]
    public static void Build()
    {
        var bank = AssetDatabase.LoadAssetAtPath<SoundBank>(AssetPath);
        bool created = bank == null;
        if (created)
        {
            bank = ScriptableObject.CreateInstance<SoundBank>();
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            AssetDatabase.CreateAsset(bank, AssetPath);
        }

        bank.MusicLobbyEdit = Find("bgm_lobby_edit");
        bank.MusicBattle = Find("bgm_battle");
        bank.Click = Find("sfx_click");
        bank.Jump = Find("sfx_jump");
        bank.Land = Find("sfx_land");
        bank.Confirm = Find("sfx_confirm");
        bank.Drawing = Find("sfx_drawing");
        bank.Eraser = Find("sfx_eraser");
        bank.Clock = Find("sfx_clock");

        // 배경음·루프는 압축 상태로 메모리에(WebGL 은 Streaming 미지원), 짧은 효과음은 메모리 디코드
        foreach (var name in new[] { "bgm_lobby_edit", "bgm_battle" }) SetImport(name, AudioClipLoadType.CompressedInMemory);
        foreach (var name in new[] { "sfx_drawing", "sfx_eraser", "sfx_clock" }) SetImport(name, AudioClipLoadType.CompressedInMemory);
        foreach (var name in new[] { "sfx_click", "sfx_jump", "sfx_land", "sfx_confirm" }) SetImport(name, AudioClipLoadType.DecompressOnLoad);

        EditorUtility.SetDirty(bank);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SoundBankBuilder] {(created ? "생성" : "갱신")} — {AssetPath}  (lobby:{Has(bank.MusicLobbyEdit)} battle:{Has(bank.MusicBattle)} click:{Has(bank.Click)} jump:{Has(bank.Jump)} land:{Has(bank.Land)} confirm:{Has(bank.Confirm)} drawing:{Has(bank.Drawing)} eraser:{Has(bank.Eraser)} clock:{Has(bank.Clock)})");
    }

    static string Has(Object o) => o != null ? "OK" : "없음";

    static string PathOf(string baseName)
    {
        if (!Directory.Exists(AudioDir)) return null;
        foreach (var f in Directory.GetFiles(AudioDir))
        {
            if (f.EndsWith(".meta")) continue;
            if (Path.GetFileNameWithoutExtension(f).Equals(baseName, System.StringComparison.OrdinalIgnoreCase)) return f.Replace('\\', '/');
        }
        return null;
    }

    static AudioClip Find(string baseName)
    {
        var p = PathOf(baseName);
        if (p == null) { Debug.LogWarning($"[SoundBankBuilder] {AudioDir}/{baseName}.* 없음"); return null; }
        return AssetDatabase.LoadAssetAtPath<AudioClip>(p);
    }

    static void SetImport(string baseName, AudioClipLoadType loadType)
    {
        var p = PathOf(baseName);
        if (p == null) return;
        var imp = AssetImporter.GetAtPath(p) as AudioImporter;
        if (imp == null) return;
        var s = imp.defaultSampleSettings;
        bool changed = s.loadType != loadType || imp.forceToMono;
        s.loadType = loadType;
        s.compressionFormat = AudioCompressionFormat.Vorbis;
        s.quality = 0.7f;
        imp.defaultSampleSettings = s;
        imp.forceToMono = false;
        imp.loadInBackground = false;
        if (changed) imp.SaveAndReimport();
    }
}
#endif
