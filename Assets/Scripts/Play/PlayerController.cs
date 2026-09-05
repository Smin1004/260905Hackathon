using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 플랫포머 플레이어 컨트롤러 (Docs/202 1장, 조작 구성 확정 2026-09-05).
/// 조작: 이동 A/D 또는 ←/→, 점프 W 또는 ↑ (누른 길이로 높이 조절), 빠른 낙하 S 또는 ↓. 스페이스바 없음.
///
/// 손맛 보정 (Celeste·GMTK 툴킷 계열 기법):
///   - 코요테 타임 / 점프 버퍼
///   - 가변 점프 높이: 키를 떼면 상승 속도를 잘라 낮게 뜀
///   - 비대칭 중력: 하강 중력 > 상승 중력, 낙하 속도 상한 (얇은 선 터널링 방지)
///   - 꼭짓점 체공: 점프를 누른 채 정점 근처에서 중력 절반 (최대 ApexMaxTime)
///   - 모서리 보정: 상승 중 머리가 모서리에 살짝 걸리면 옆으로 밀어 통과
///   - 턱 자동 오르기: 걷다가 낮은 턱에 닿으면 올라섬 (스트로크 이음새 대응)
///   - 가속 램프: 지상 0.05s / 공중 0.1s, 감속·정지는 즉시
/// 경사: 지면 접선 방향으로 이동, 정지 시 마찰 재질 교체.
/// 피드백: 스쿼시 앤 스트레치, 착지 먼지, 절차 생성 효과음.
/// 뜻(제약) 시스템은 공개 파라미터(점프 속도, 중력, 가속 시간, 공중 제어)와 입력 훅(MoveOverride, RequestJump)을 조절해 붙인다.
/// </summary>
[RequireComponent(typeof(Rigidbody2D), typeof(BoxCollider2D))]
public class PlayerController : MonoBehaviour
{
    [Header("이동")]
    public float MoveSpeed = 5f;
    [Tooltip("0 → 최고 속도까지 걸리는 시간 (지상)")] public float GroundAccelTime = 0.05f;
    [Tooltip("0 → 최고 속도까지 걸리는 시간 (공중)")] public float AirAccelTime = 0.1f;

    [Header("점프")]
    public float JumpSpeed = 10f;                 // 최대 높이 ≈ 2.5u (상승 중력 2.0 기준)
    [Tooltip("키를 떼면 상승 속도에 곱함 → 최소 높이 ≈ 최대의 1/4")] public float JumpCutMultiplier = 0.5f;
    public float CoyoteTime = 0.1f;
    public float JumpBufferTime = 0.1f;

    [Header("중력")]
    public float RiseGravity = 2.0f;
    public float FallGravity = 3.2f;
    [Tooltip("S/↓ 홀드 시 하강 중력에 곱함")] public float FastFallMultiplier = 1.6f;
    public float MaxFallSpeed = 20f;
    [Tooltip("|상승 속도| 가 이 값 아래이고 점프를 누르고 있으면 중력 절반")] public float ApexThreshold = 1.5f;
    public float ApexGravityMultiplier = 0.5f;
    public float ApexMaxTime = 0.1f;

    [Header("보정")]
    [Tooltip("상승 중 머리가 걸린 모서리를 이만큼까지 옆으로 피함 (u)")] public float CornerCorrection = 0.15f;
    [Tooltip("걷다가 이 높이 이하의 턱은 자동으로 올라섬 (u)")] public float StepHeight = 0.15f;

    [Header("경사·지면")]
    public float IdleFriction = 1.0f;
    public float GroundCheckDistance = 0.12f;

    [Header("피드백")]
    public bool EnableSquashStretch = true;
    public bool EnableDust = true;
    public bool EnableSound = true;
    [Range(0f, 1f)] public float SoundVolume = 0.3f;

    // ---- 입력 상태 / 훅
    public bool InputEnabled = true;
    /// <summary>설정 시 키보드 대신 이 값을 이동 입력으로 사용 (-1~1). 자동 테스트·터치 UI·뜻 필터용.</summary>
    public float? MoveOverride;
    /// <summary>설정 시 키보드 대신 점프 홀드 상태로 사용.</summary>
    public bool? JumpHeldOverride;
    /// <summary>설정 시 키보드 대신 빠른 낙하 홀드 상태로 사용.</summary>
    public bool? FastFallOverride;

    public bool IsGrounded { get; private set; }
    public Vector2 GroundNormal { get; private set; } = Vector2.up;
    public float MoveInput { get; private set; }
    public bool JumpHeld { get; private set; }
    public bool FastFallHeld { get; private set; }
    public int JumpCount { get; private set; }
    public Rigidbody2D Body => _rb;

    public event Action Jumped;
    public event Action<float> Landed;   // 착지 속도(양수)

    Rigidbody2D _rb;
    BoxCollider2D _col;
    ContactFilter2D _groundFilter, _solidFilter;
    readonly RaycastHit2D[] _hits = new RaycastHit2D[8];
    readonly Collider2D[] _overlaps = new Collider2D[8];
    PhysicsMaterial2D _matMoving, _matIdle;
    bool _idleMaterialApplied;

    float _lastGroundedTime = float.NegativeInfinity;   // Time.fixedTime
    float _jumpPressedTime = float.NegativeInfinity;    // Time.time
    bool _jumping;          // 점프로 떠 있는 중 (낙하로 떨어진 것과 구분)
    bool _jumpCut;
    float _apexTimer;
    bool _wasGrounded;
    float _lastFallSpeed;

    // ---- 비주얼
    Transform _visual;
    SpriteRenderer _visualSr;
    Vector2 _squash = Vector2.one;
    float _squashTimer;
    const float SquashDuration = 0.14f;

    struct Dust { public Transform T; public Vector2 V; public float Life; public SpriteRenderer Sr; }
    readonly List<Dust> _dust = new List<Dust>();

    AudioSource _audio;
    static AudioClip _sfxJump, _sfxLand, _sfxDeath;

    public static readonly Vector2 BodySize = new Vector2(0.6f, 0.9f);
    static readonly Color BodyColor = new Color(0.95f, 0.45f, 0.2f);

    // ------------------------------------------------------------------ spawn

    /// <summary>플레이어를 코드로 생성 (플레이스홀더 비주얼). 아트 교체 시 프리팹으로 대체 — Docs/102 1.1.</summary>
    public static PlayerController Spawn(Vector2 position, Transform parent = null)
    {
        var go = new GameObject("Player");
        go.tag = "Player";
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(position.x, position.y, 0f);

        go.AddComponent<Rigidbody2D>();
        var col = go.AddComponent<BoxCollider2D>();
        col.size = BodySize;

        var visual = new GameObject("Visual");
        visual.transform.SetParent(go.transform, false);
        visual.transform.localScale = new Vector3(BodySize.x, BodySize.y, 1f);
        var sr = visual.AddComponent<SpriteRenderer>();
        sr.sprite = RuntimeSprites.White;
        sr.color = BodyColor;
        sr.sortingOrder = 20;

        var pc = go.AddComponent<PlayerController>();
        pc._visual = visual.transform;
        pc._visualSr = sr;
        return pc;
    }

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _col = GetComponent<BoxCollider2D>();
        _rb.bodyType = RigidbodyType2D.Dynamic;
        _rb.gravityScale = RiseGravity;
        _rb.freezeRotation = true;
        _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        _rb.sleepMode = RigidbodySleepMode2D.NeverSleep;

        _groundFilter = new ContactFilter2D { useTriggers = false };
        _groundFilter.SetNormalAngle(40f, 140f);      // 위를 향한 면만 지면 (경사 ±50°)
        _solidFilter = new ContactFilter2D { useTriggers = false };

        _matMoving = new PhysicsMaterial2D("Player Moving") { friction = 0f, bounciness = 0f };
        _matIdle = new PhysicsMaterial2D("Player Idle") { friction = IdleFriction, bounciness = 0f };
        _col.sharedMaterial = _matMoving;

        if (_visual == null) { _visualSr = GetComponentInChildren<SpriteRenderer>(); if (_visualSr != null) _visual = _visualSr.transform; }

        _audio = gameObject.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.spatialBlend = 0f;
        EnsureSfx();
    }

    // ------------------------------------------------------------------ input (Update)

    void Update()
    {
        var kb = Keyboard.current;
        float x = 0f;
        bool jumpHeld = false, fastFall = false;

        if (InputEnabled)
        {
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
                if (kb.wKey.wasPressedThisFrame || kb.upArrowKey.wasPressedThisFrame) RequestJump();
                jumpHeld = kb.wKey.isPressed || kb.upArrowKey.isPressed;
                fastFall = kb.sKey.isPressed || kb.downArrowKey.isPressed;
            }
            if (MoveOverride.HasValue) x = Mathf.Clamp(MoveOverride.Value, -1f, 1f);
            if (JumpHeldOverride.HasValue) jumpHeld = JumpHeldOverride.Value;
            if (FastFallOverride.HasValue) fastFall = FastFallOverride.Value;
        }
        MoveInput = x;
        JumpHeld = jumpHeld;
        FastFallHeld = fastFall;

        if (_visualSr != null && x != 0f) _visualSr.flipX = x < 0f;
        UpdateVisualFeedback();
    }

    // ------------------------------------------------------------------ physics (FixedUpdate)

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        UpdateGround();

        float now = Time.fixedTime;
        if (IsGrounded) { _lastGroundedTime = now; _jumping = false; _jumpCut = false; _apexTimer = 0f; }

        // 착지 감지 (피드백)
        if (IsGrounded && !_wasGrounded && _lastFallSpeed > 2f) OnLanded(_lastFallSpeed);
        _wasGrounded = IsGrounded;

        bool jumpBuffered = Time.time - _jumpPressedTime <= JumpBufferTime;
        bool canJump = IsGrounded || now - _lastGroundedTime <= CoyoteTime;
        bool doJump = jumpBuffered && canJump;

        var v = _rb.linearVelocity;
        float target = MoveInput * MoveSpeed;
        bool idle = Mathf.Approximately(target, 0f);

        // ---- 수평 이동
        if (IsGrounded && !doJump)
        {
            var tangent = new Vector2(GroundNormal.y, -GroundNormal.x);        // 노멀에 수직, 오른쪽 방향
            if (idle) v = Vector2.zero;                                          // 정지 즉시 (마찰 재질이 경사에서 붙잡음)
            else
            {
                float along = Vector2.Dot(v, tangent);
                along = Accelerate(along, target, GroundAccelTime, dt);
                v = tangent * along;                                             // 경사를 따라 이동
            }
        }
        else
        {
            v.x = Accelerate(v.x, target, AirAccelTime, dt);                     // 공중: 수평만 제어
        }

        // ---- 점프
        if (doJump)
        {
            v.y = JumpSpeed;
            _jumpPressedTime = float.NegativeInfinity;
            _lastGroundedTime = float.NegativeInfinity;
            _jumping = true;
            _jumpCut = false;
            _apexTimer = 0f;
            JumpCount++;
            OnJumped();
        }

        // ---- 가변 점프: 키를 떼면 상승 속도 컷
        if (_jumping && !_jumpCut && !JumpHeld && v.y > 0f)
        {
            v.y *= JumpCutMultiplier;
            _jumpCut = true;
        }

        // ---- 중력 (비대칭 + 꼭짓점 체공 + 빠른 낙하)
        float g;
        if (v.y > 0f)
        {
            g = RiseGravity;
            if (_jumping && !_jumpCut && JumpHeld && v.y < ApexThreshold && _apexTimer < ApexMaxTime)
            {
                g *= ApexGravityMultiplier;
                _apexTimer += dt;
            }
        }
        else
        {
            g = FallGravity;
            if (FastFallHeld && !IsGrounded) g *= FastFallMultiplier;
        }
        if (v.y <= -MaxFallSpeed) { v.y = -MaxFallSpeed; g = 0f; }   // 종단 속도: 중력을 끊어 정확히 유지
        _rb.gravityScale = g;
        _lastFallSpeed = -v.y;

        _rb.linearVelocity = v;
        ApplyMaterial(IsGrounded && idle && !doJump);

        // ---- 보정 (속도 확정 후 위치 미세 조정)
        if (v.y > 0f && CornerCorrection > 0f) TryCornerCorrection(v.y * dt);
        if (IsGrounded && !idle && StepHeight > 0f) TryStepUp(Mathf.Sign(target));
    }

    float Accelerate(float current, float target, float accelTime, float dt)
    {
        if (Mathf.Approximately(target, 0f)) return 0f;                                      // 감속 즉시
        if (!Mathf.Approximately(current, 0f) && Mathf.Sign(current) != Mathf.Sign(target)) current = 0f;   // 방향 전환 즉시 제동
        if (accelTime <= 0f) return target;
        return Mathf.MoveTowards(current, target, (MoveSpeed / accelTime) * dt);
    }

    /// <summary>지면 판정 — 발 아래 얇은 BoxCast (접촉 캐시 대신 기하 질의 → 리스폰·텔레포트 직후에도 정확).</summary>
    void UpdateGround()
    {
        var b = _col.bounds;
        var origin = new Vector2(b.center.x, b.min.y + 0.05f);
        var size = new Vector2(b.size.x * 0.9f, 0.1f);
        int n = Physics2D.BoxCast(origin, size, 0f, Vector2.down, _groundFilter, _hits, GroundCheckDistance);

        var sum = Vector2.zero; int count = 0;
        for (int i = 0; i < n; i++)
        {
            var h = _hits[i];
            if (h.collider == _col || h.collider.attachedRigidbody == _rb) continue;
            if (h.normal.sqrMagnitude < 0.5f) continue;
            sum += h.normal; count++;
        }
        IsGrounded = count > 0;
        GroundNormal = count > 0 ? sum.normalized : Vector2.up;

        // 점프로 떠오르는 첫 스텝들은 발이 아직 지면 근처라 "지면"으로 잡힌다 → 상승 중에는 무시 (경사 오르막은 _jumping 이 false 라 영향 없음)
        if (_jumping && _rb.linearVelocity.y > 0f) { IsGrounded = false; GroundNormal = Vector2.up; }
    }

    /// <summary>상승 중 머리가 모서리에 걸리면 (CornerCorrection 이내) 옆으로 밀어 속도 손실 없이 넘어간다.</summary>
    void TryCornerCorrection(float upMove)
    {
        var b = _col.bounds;
        var headSize = new Vector2(b.size.x * 0.96f, 0.1f);
        var headCenter = new Vector2(b.center.x, b.max.y - 0.05f);
        float dist = upMove + 0.03f;

        if (!CastHits(headCenter, headSize, Vector2.up, dist)) return;

        const int steps = 3;
        for (int s = 1; s <= steps; s++)
        {
            float off = CornerCorrection * s / steps;
            for (int d = 0; d < 2; d++)
            {
                float dir = d == 0 ? -1f : 1f;
                var shifted = new Vector2(dir * off, 0f);
                if (CastHits(headCenter + shifted, headSize, Vector2.up, dist)) continue;
                if (OverlapsBody((Vector2)b.center + shifted)) continue;
                _rb.position += shifted;
                transform.position += new Vector3(shifted.x, 0f, 0f);
                return;
            }
        }
    }

    /// <summary>걷는 방향의 낮은 턱(StepHeight 이하, 거의 수직인 면)에 닿으면 그 위로 올라선다.</summary>
    void TryStepUp(float dir)
    {
        var b = _col.bounds;
        var lowSize = new Vector2(b.size.x, StepHeight * 0.9f);
        var lowCenter = new Vector2(b.center.x, b.min.y + StepHeight * 0.5f);
        int n = Physics2D.BoxCast(lowCenter, lowSize, 0f, new Vector2(dir, 0f), _solidFilter, _hits, 0.08f);

        bool wallAhead = false;
        for (int i = 0; i < n; i++)
        {
            var h = _hits[i];
            if (h.collider == _col || h.collider.attachedRigidbody == _rb) continue;
            if (Mathf.Abs(h.normal.x) < 0.8f) continue;          // 경사는 턱이 아님
            wallAhead = true; break;
        }
        if (!wallAhead) return;

        // 턱 윗면 높이: 턱 바로 위에서 아래로 얇은 캐스트
        float frontX = b.center.x + dir * (b.extents.x + 0.06f);
        var probeSize = new Vector2(0.12f, 0.05f);
        var probeStart = new Vector2(frontX, b.min.y + StepHeight + 0.08f);
        int m = Physics2D.BoxCast(probeStart, probeSize, 0f, Vector2.down, _solidFilter, _hits, StepHeight + 0.1f);
        float top = float.NegativeInfinity;
        for (int i = 0; i < m; i++)
        {
            var h = _hits[i];
            if (h.collider == _col || h.collider.attachedRigidbody == _rb) continue;
            top = Mathf.Max(top, h.point.y);
        }
        if (top == float.NegativeInfinity) return;

        float lift = top - b.min.y + 0.01f;
        if (lift <= 0.005f || lift > StepHeight + 0.02f) return;
        var newCenter = (Vector2)b.center + new Vector2(0f, lift);
        if (OverlapsBody(newCenter)) return;

        _rb.position += new Vector2(0f, lift);
        transform.position += new Vector3(0f, lift, 0f);
    }

    bool CastHits(Vector2 center, Vector2 size, Vector2 dir, float dist)
    {
        int n = Physics2D.BoxCast(center, size, 0f, dir, _solidFilter, _hits, dist);
        for (int i = 0; i < n; i++)
            if (_hits[i].collider != _col && _hits[i].collider.attachedRigidbody != _rb) return true;
        return false;
    }

    bool OverlapsBody(Vector2 center)
    {
        int n = Physics2D.OverlapBox(center, _col.size * 0.98f, 0f, _solidFilter, _overlaps);
        for (int i = 0; i < n; i++)
            if (_overlaps[i] != _col && _overlaps[i].attachedRigidbody != _rb) return true;
        return false;
    }

    void ApplyMaterial(bool idleOnGround)
    {
        if (idleOnGround == _idleMaterialApplied) return;
        _idleMaterialApplied = idleOnGround;
        _col.sharedMaterial = idleOnGround ? _matIdle : _matMoving;
    }

    // ------------------------------------------------------------------ public control

    /// <summary>점프 입력 등록 (키보드와 동일 경로 — 버퍼·코요테 규칙 적용). 터치 UI·자동 테스트용.</summary>
    public void RequestJump()
    {
        if (InputEnabled) _jumpPressedTime = Time.time;
    }

    /// <summary>시작점으로 복귀. 속도·점프 카운터·보정 타이머 초기화 (Docs/100 7.3: 리스폰 시 뜻 카운터 초기화).</summary>
    public void Respawn(Vector2 position)
    {
        _rb.linearVelocity = Vector2.zero;
        _rb.angularVelocity = 0f;
        _rb.position = position;
        transform.position = new Vector3(position.x, position.y, 0f);
        _rb.gravityScale = RiseGravity;
        JumpCount = 0;
        _jumpPressedTime = float.NegativeInfinity;
        _lastGroundedTime = float.NegativeInfinity;
        _jumping = false; _jumpCut = false; _apexTimer = 0f;
        _lastFallSpeed = 0f; _wasGrounded = false;
        _squash = Vector2.one; _squashTimer = 0f;
        if (_visual != null) { _visual.localScale = new Vector3(BodySize.x, BodySize.y, 1f); _visual.localPosition = Vector3.zero; }
        if (_visualSr != null) _visualSr.color = BodyColor;
    }

    public void Freeze()
    {
        InputEnabled = false;
        MoveInput = 0f; JumpHeld = false; FastFallHeld = false;
        _jumpPressedTime = float.NegativeInfinity;
        _rb.linearVelocity = new Vector2(0f, _rb.linearVelocity.y);
    }

    public void Unfreeze() => InputEnabled = true;

    /// <summary>사망 연출 (PlaySession 이 리스폰 전에 호출): 색 변화 + 먼지 + 효과음.</summary>
    public void PlayDeathFeedback()
    {
        if (_visualSr != null) _visualSr.color = new Color(1f, 0.2f, 0.2f);
        PlaySfx(_sfxDeath);
        if (EnableDust) SpawnDust(transform.position, 8, 3.5f);
    }

    // ------------------------------------------------------------------ feedback

    void OnJumped()
    {
        Jumped?.Invoke();
        if (EnableSquashStretch) { _squash = new Vector2(0.75f, 1.3f); _squashTimer = SquashDuration; }
        PlaySfx(_sfxJump);
    }

    void OnLanded(float fallSpeed)
    {
        Landed?.Invoke(fallSpeed);
        float k = Mathf.Clamp01(fallSpeed / MaxFallSpeed);
        if (EnableSquashStretch) { _squash = new Vector2(1.1f + 0.35f * k, 0.9f - 0.3f * k); _squashTimer = SquashDuration; }
        if (EnableDust) SpawnDust(new Vector2(transform.position.x, _col.bounds.min.y), 3 + Mathf.RoundToInt(3 * k), 1.5f + 2f * k);
        PlaySfx(_sfxLand, 0.5f + 0.5f * k);
    }

    void UpdateVisualFeedback()
    {
        if (_visual != null)
        {
            if (_squashTimer > 0f)
            {
                _squashTimer -= Time.deltaTime;
                float t = 1f - Mathf.Clamp01(_squashTimer / SquashDuration);   // 0 → 1
                var s = Vector2.Lerp(_squash, Vector2.one, t);
                _visual.localScale = new Vector3(BodySize.x * s.x, BodySize.y * s.y, 1f);
                _visual.localPosition = new Vector3(0f, (BodySize.y * s.y - BodySize.y) * 0.5f, 0f);   // 발 기준으로 늘고 줄게
            }
            else if (_visual.localScale.y != BodySize.y)
            {
                _visual.localScale = new Vector3(BodySize.x, BodySize.y, 1f);
                _visual.localPosition = Vector3.zero;
            }
        }

        for (int i = _dust.Count - 1; i >= 0; i--)
        {
            var d = _dust[i];
            d.Life -= Time.deltaTime;
            if (d.Life <= 0f || d.T == null) { if (d.T != null) Destroy(d.T.gameObject); _dust.RemoveAt(i); continue; }
            d.V += Vector2.down * 6f * Time.deltaTime;
            d.T.position += new Vector3(d.V.x, d.V.y, 0f) * Time.deltaTime;
            var c = d.Sr.color; c.a = Mathf.Clamp01(d.Life / 0.35f) * 0.8f; d.Sr.color = c;
            _dust[i] = d;
        }
    }

    void SpawnDust(Vector2 at, int count, float speed)
    {
        for (int i = 0; i < count; i++)
        {
            var sr = RuntimeSprites.MakeSquare("Dust", null, at, Vector2.one * UnityEngine.Random.Range(0.08f, 0.16f), new Color(0.9f, 0.9f, 0.85f, 0.8f), 19);
            if (gameObject.scene.IsValid() && sr.gameObject.scene != gameObject.scene)
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(sr.gameObject, gameObject.scene);
            float ang = UnityEngine.Random.Range(20f, 160f) * Mathf.Deg2Rad;
            var v = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang) * 0.6f) * speed * UnityEngine.Random.Range(0.5f, 1f);
            _dust.Add(new Dust { T = sr.transform, V = v, Life = UnityEngine.Random.Range(0.25f, 0.4f), Sr = sr });
        }
    }

    void PlaySfx(AudioClip clip, float volumeScale = 1f)
    {
        if (!EnableSound || clip == null || _audio == null) return;
        _audio.PlayOneShot(clip, SoundVolume * volumeScale);
    }

    void OnDestroy()
    {
        foreach (var d in _dust) if (d.T != null) Destroy(d.T.gameObject);
        _dust.Clear();
    }

    // ---- 절차 생성 효과음 (에셋 없이 동작하는 플레이스홀더 — Docs/102 3장 오디오 에셋으로 교체 가능)

    static void EnsureSfx()
    {
        if (_sfxJump != null) return;
        _sfxJump = MakeTone("sfx_jump", 0.09f, 420f, 880f, 0.6f);
        _sfxLand = MakeTone("sfx_land", 0.07f, 180f, 90f, 0.8f);
        _sfxDeath = MakeTone("sfx_death", 0.22f, 600f, 120f, 0.7f);
    }

    static AudioClip MakeTone(string name, float duration, float f0, float f1, float gain)
    {
        const int rate = 44100;
        int n = Mathf.CeilToInt(duration * rate);
        var data = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n;
            float f = Mathf.Lerp(f0, f1, t);
            phase += 2f * Mathf.PI * f / rate;
            float env = Mathf.Sin(Mathf.PI * t);                 // 부드러운 어택·릴리즈
            float sq = Mathf.Sin(phase) >= 0f ? 1f : -1f;
            data[i] = (sq * 0.35f + Mathf.Sin(phase) * 0.65f) * env * gain;
        }
        var clip = AudioClip.Create(name, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
