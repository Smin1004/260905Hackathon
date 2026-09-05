using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

/// <summary>클립보드 복사 — 데스크톱은 GUIUtility.systemCopyBuffer, WebGL 은 Plugins/WebGL/Clipboard.jslib (navigator.clipboard).</summary>
public static class Clipboard
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern int CJ_CopyToClipboard(string text);
#endif

    /// <returns>복사를 시도했고 실패가 확인되지 않았으면 true</returns>
    public static bool Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
#if UNITY_WEBGL && !UNITY_EDITOR
        return CJ_CopyToClipboard(text) != 0;
#else
        GUIUtility.systemCopyBuffer = text;
        return true;
#endif
    }
}
