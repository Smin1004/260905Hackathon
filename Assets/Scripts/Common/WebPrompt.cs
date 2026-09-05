using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

/// <summary>
/// WebGL 텍스트 입력 보조. uGUI InputField 는 브라우저에서 한글 IME 조합을 받지 못하므로(영문·숫자만 됨)
/// WebGL 빌드에서는 입력창을 클릭하면 브라우저 prompt() 로 대신 입력받아 필드에 넣는다. 다른 플랫폼에서는 아무 일도 하지 않는다.
/// 사용: <c>WebPrompt.Attach(inputField, "닉네임을 입력하세요")</c> — Plugins/WebGL/Clipboard.jslib 의 CJ_Prompt.
/// </summary>
public static class WebPrompt
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern string CJ_Prompt(string title, string defaultValue);
    public static bool Supported => true;
#else
    public static bool Supported => false;
#endif

    /// <returns>입력 문자열. 취소하면 null</returns>
    public static string Show(string title, string defaultValue)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        var r = CJ_Prompt(title ?? "", defaultValue ?? "");
        if (r == null || r == "") return null;
        return r;
#else
        return null;
#endif
    }

    public static void Attach(InputField field, string title)
    {
        if (field == null || !Supported) return;
        var h = field.gameObject.GetComponent<WebPromptField>();
        if (h == null) h = field.gameObject.AddComponent<WebPromptField>();
        h.Field = field; h.Title = title;
        field.readOnly = true;   // 키보드 입력 대신 prompt 로만 받는다 (커서 깜빡임 방지)
    }
}

/// <summary>WebGL 에서 InputField 클릭 → 브라우저 prompt 로 입력 (WebPrompt.Attach 가 붙임)</summary>
public class WebPromptField : MonoBehaviour, IPointerClickHandler
{
    public InputField Field;
    public string Title;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (Field == null || !Field.interactable) return;
        var r = WebPrompt.Show(Title, Field.text);
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        if (r == null) return;
        r = r.Trim();
        if (Field.characterLimit > 0 && r.Length > Field.characterLimit) r = r.Substring(0, Field.characterLimit);
        Field.text = r;   // onValueChanged 발생 → 로비 버튼 갱신
    }
}
