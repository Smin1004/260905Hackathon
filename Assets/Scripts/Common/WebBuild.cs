#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// WebGL 빌드 설정·실행 (에디터 전용 — Docs/205 2.3).
///   [Chojiilgwan > WebGL > Apply Settings]  플레이어 설정만 적용 (커밋용 — ProjectSettings 변경은 통합 담당)
///   [Chojiilgwan > WebGL > Build]           설정 적용 후 Builds/WebGL 로 빌드 (gitignore 대상)
///
/// 설정 근거
///   - 압축 Gzip + 압축 해제 폴백: 서버 헤더 설정 없이 어떤 정적 호스팅(itch.io·GitHub Pages·로컬 http 서버)에서도 실행
///   - 코드 스트리핑 Low: Unity Services / Netcode 가 리플렉션을 쓰므로 High 는 런타임 누락 위험
///   - IL2CPP Release + OptimizeSize: 빌드 크기·시간 절충
///   - 예외 지원 ExplicitlyThrownExceptionsOnly(기본): try/catch 로 잡는 네트워크 예외가 동작해야 한다
///   - runInBackground: 탭이 뒤로 가도 타이머·네트워크가 계속 돌게
///   - 기본 캔버스 1280×720 (템플릿이 창 크기에 맞춰 스케일)
/// Relay 는 WebGL 에서 SDK 가 자동으로 WSS 를 고르고 UnityTransport 가 RelayServerData.IsWebSocket 을 보고 웹소켓으로 붙는다.
/// </summary>
public static class WebBuild
{
    const string OutputDir = "Builds/WebGL";

    [MenuItem("Chojiilgwan/WebGL/Apply Settings")]
    public static void ApplySettings()
    {
        var target = NamedBuildTarget.WebGL;
        PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Low);
        PlayerSettings.SetIl2CppCompilerConfiguration(target, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetIl2CppCodeGeneration(target, Il2CppCodeGeneration.OptimizeSize);

        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.template = "APPLICATION:Default";
        PlayerSettings.WebGL.powerPreference = WebGLPowerPreference.HighPerformance;
        PlayerSettings.WebGL.showDiagnostics = false;
        PlayerSettings.WebGL.nameFilesAsHashes = false;
        PlayerSettings.WebGL.threadsSupport = false;

        PlayerSettings.runInBackground = true;
        PlayerSettings.defaultWebScreenWidth = 1280;
        PlayerSettings.defaultWebScreenHeight = 720;

        AssetDatabase.SaveAssets();
        Debug.Log("[WebBuild] WebGL 플레이어 설정 적용 — Gzip+폴백, 스트리핑 Low, IL2CPP Release/OptimizeSize, 1280×720, runInBackground");
    }

    [MenuItem("Chojiilgwan/WebGL/Build")]
    public static void Build()
    {
        ApplySettings();
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) { Debug.LogError("[WebBuild] Build Settings 에 활성 씬이 없습니다"); return; }
        Directory.CreateDirectory(OutputDir);
        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = OutputDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        var s = report.summary;
        Debug.Log($"[WebBuild] {s.result} — {OutputDir} ({s.totalSize / (1024f * 1024f):0.0} MB, {s.totalTime.TotalSeconds:0}s, 오류 {s.totalErrors}, 경고 {s.totalWarnings})");
    }
}
#endif
