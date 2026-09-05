using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 사운드 재생 정적 API (Docs/102 3장). 첫 호출 때 DontDestroyOnLoad 오브젝트를 만들어 씬 전환에도 이어진다.
///   Sound.Click()                     버튼 클릭 (RuntimeUI.Button / HUD Hook 이 자동으로 붙임)
///   Sound.Play(SfxId, volumeScale)    1회 효과음
///   Sound.SetLoop(LoopId, on)         상태 루프 (그리기·지우개·타이머 경고) — 같은 값이면 무시 (매 프레임 호출 가능)
///   Sound.PlayMusic(MusicId)          배경음 (같은 곡이면 무시, 크로스페이드)
/// SoundBank 가 없거나 클립이 비어 있으면 조용히 무시한다 — 에셋 없이도 동작.
/// </summary>
public static class Sound
{
    static SoundBank _bank;
    static SoundHost _host;
    static bool _bankLoaded;

    public static SoundBank Bank
    {
        get
        {
            if (!_bankLoaded) { _bank = Resources.Load<SoundBank>(SoundBank.ResourcePath); _bankLoaded = true; }
            return _bank;
        }
    }

    public static bool Muted;   // 디버그·자동화용 (AutoPilot 등)

    static SoundHost Host
    {
        get
        {
            if (_host != null) return _host;
            var go = new GameObject("Sound (runtime)");
            Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<SoundHost>();
            return _host;
        }
    }

    public static void Click() => Play(SfxId.Click);

    public static void Play(SfxId id, float volumeScale = 1f)
    {
        if (Muted) return;
        var b = Bank; var clip = b != null ? b.Get(id) : null;
        if (clip == null) return;
        Host.Sfx.PlayOneShot(clip, b.SfxVolume * volumeScale);
    }

    public static void SetLoop(LoopId id, bool on)
    {
        if (Muted && on) return;
        var b = Bank; var clip = b != null ? b.Get(id) : null;
        if (clip == null) return;
        var src = Host.Loop(id);
        if (on)
        {
            if (src.isPlaying && src.clip == clip) return;
            src.clip = clip; src.loop = true; src.volume = b.LoopVolume; src.Play();
        }
        else if (src.isPlaying) src.Stop();
    }

    public static void StopAllLoops()
    {
        if (_host == null) return;
        foreach (var s in _host.Loops.Values) if (s.isPlaying) s.Stop();
    }

    public static void PlayMusic(MusicId id)
    {
        if (Muted) return;
        var b = Bank; var clip = b != null ? b.Get(id) : null;
        Host.CrossfadeTo(clip, b != null ? b.MusicVolume : 0.3f, b != null ? b.MusicFade : 0.5f);
    }

    public static void StopMusic() => _host?.CrossfadeTo(null, 0f, Bank != null ? Bank.MusicFade : 0.5f);

    /// <summary>AudioSource 를 들고 있는 숨은 컴포넌트 — 외부에서 직접 쓰지 않는다.</summary>
    public class SoundHost : MonoBehaviour
    {
        public AudioSource Sfx;
        public readonly Dictionary<LoopId, AudioSource> Loops = new Dictionary<LoopId, AudioSource>();
        AudioSource _musicA, _musicB;
        AudioSource _musicCur;
        Coroutine _fade;

        void Awake()
        {
            Sfx = Make("Sfx", false);
            _musicA = Make("Music A", true);
            _musicB = Make("Music B", true);
            _musicCur = _musicA;
        }

        AudioSource Make(string name, bool loop)
        {
            var go = new GameObject(name); go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false; s.spatialBlend = 0f; s.loop = loop; s.ignoreListenerPause = true;
            return s;
        }

        public AudioSource Loop(LoopId id)
        {
            if (!Loops.TryGetValue(id, out var s)) { s = Make("Loop " + id, true); Loops[id] = s; }
            return s;
        }

        public void CrossfadeTo(AudioClip clip, float volume, float seconds)
        {
            if (_musicCur.isPlaying && _musicCur.clip == clip) { _musicCur.volume = volume; return; }
            if (clip == null && !_musicCur.isPlaying) return;
            var from = _musicCur;
            var to = from == _musicA ? _musicB : _musicA;
            if (_fade != null) StopCoroutine(_fade);
            if (clip != null) { to.clip = clip; to.volume = 0f; to.Play(); }
            _musicCur = to;
            _fade = StartCoroutine(Fade(from, to, clip != null ? volume : 0f, Mathf.Max(0.01f, seconds)));
        }

        IEnumerator Fade(AudioSource from, AudioSource to, float toVolume, float seconds)
        {
            float fromStart = from.volume, t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / seconds);
                from.volume = Mathf.Lerp(fromStart, 0f, k);
                if (to.clip != null) to.volume = Mathf.Lerp(0f, toVolume, k);
                yield return null;
            }
            from.Stop(); from.volume = 0f;
            _fade = null;
        }
    }
}
