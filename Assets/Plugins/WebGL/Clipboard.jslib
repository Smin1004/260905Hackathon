// 브라우저 연동 (WebGL 전용) — Scripts/Common/Clipboard.cs 가 호출
//   CJ_CopyToClipboard : GUIUtility.systemCopyBuffer 가 WebGL 에서 동작하지 않아 navigator.clipboard 사용
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
  }
});
