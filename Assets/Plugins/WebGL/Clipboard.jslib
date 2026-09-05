// 브라우저 연동 (WebGL 전용) — Scripts/Common/Clipboard.cs, Scripts/Common/WebPrompt.cs 가 호출
//   CJ_CopyToClipboard : GUIUtility.systemCopyBuffer 가 WebGL 에서 동작하지 않아 navigator.clipboard 사용
//   CJ_Prompt          : uGUI InputField 가 WebGL 에서 한글 IME 를 받지 못해 브라우저 prompt() 로 대신 입력
mergeInto(LibraryManager.library, {
  CJ_CopyToClipboard: function (ptr) {
    var text = UTF8ToString(ptr);
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text);
        return 1;
      }
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.style.position = 'fixed';
      ta.style.top = '-1000px';
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand('copy');
      document.body.removeChild(ta);
      return ok ? 1 : 0;
    } catch (e) {
      console.warn('[Clipboard] copy failed', e);
      return 0;
    }
  },

  CJ_Prompt: function (titlePtr, defaultPtr) {
    var title = UTF8ToString(titlePtr);
    var def = UTF8ToString(defaultPtr);
    var result = null;
    try { result = window.prompt(title, def); } catch (e) { console.warn('[Prompt] failed', e); }
    if (result === null || result === undefined) result = '';   // 취소 표시 (빈 문자열과 구분)
    var size = lengthBytesUTF8(result) + 1;
    var buffer = _malloc(size);
    stringToUTF8(result, buffer, size);
    return buffer;
  }
});
