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
    [Tooltip("최고 속도 → 0 까지 걸리는 시간 (지상). 0 = 즉시 정지")] public float GroundDecelTime = 0f;
    [Tooltip("0 → 최고 속도까지 걸리는 시간 (공중)")] public float AirAccelTime = 0.1f;
    [Tooltip("가속 시간이 이 값 이하일 때만 방향 전환 시 즉시 제동(스냅). 얼음·미끄러운 발처럼 가속이 느리면 관성이 유지되어 반대 키를 눌러도 미끄러진다")] public float TurnSnapMaxAccelTime = 0.15f;
    [Tooltip("false 면 공중에서 수평 속도를 바꿀 수 없다 (뜻: 공중 제어 금지)")] public bool AirControl = true;

    [Header("점프")]
    public float JumpSpeed = 10f;                 // 최대 높이 ≈ 2.5u (상승 중력 2.0 기준)
    [Tooltip("키를 떼면 상승 속도에 곱함 → 최소 높이 ≈ 최대의 1/4")] public float JumpCutMultiplier = 0.5f;
    public float CoyoteTime = 0.1f;
    public float JumpBufferTime = 0.1f;
    [Tooltip("시도당 점프 상한. 0 = 무제한 (뜻: 점프 횟수 제한). 리스폰 시 초기화")] public int MaxJumpsPerAttempt = 0;
    [Tooltip("착지 후 이 시간 동안 점프 불가. 0 = 없음 (뜻: 점프 쿨다운)")] public float JumpCooldownAfterLanding = 0f;

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
    public float JumpCooldownRemaining => Mathf.Max(0f, _cooldownUntil - Time.fixedTime);
    public bool JumpsExhausted => MaxJumpsPerAttempt > 0 && JumpCount >= MaxJumpsPerAttempt;
    public Vector2 CurrentBodySize => _bodySize;
    public Rigidbody2D Body => _rb;

    public event Action Jumped;
    public event Action<float> Landed;   // 착지 속도(양수)
    /// <summary>위험 구역(HazardStroke)에 닿음. PlaySession 이 구독해 리스폰한다 (Docs/101 1장 빨강).</summary>
    public event Action HazardTouched;

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
    float _cooldownUntil = float.NegativeInfinity;
    Vector2 _bodySize = BodySize;

    // ---- 발밑 표면 (색상별 기능 — Docs/101 1장). UpdateGround 가 갱신, 공중에서는 null
    Collider2D _groundCollider;
    SurfaceModifier _surface;      // 파랑 얼음: 서 있는 동안만 배율/하한 적용 (뜻이 정한 기준값은 그대로)
    BounceStroke _groundBounce;    // 초록 바운스
    bool _bouncing;                // 바운스로 떠 있는 중 (점프와 달리 키 떼기 컷 없음)
    float _pendingBounce;          // 이번 스텝 착지에서 발동할 바운스 배율 (0 = 없음)
    bool _groundChanged;           // 이번 스텝에 발밑 콜라이더가 바뀜 (검정 → 초록으로 걸어 올라선 경우도 바운스)

    /// <summary>현재 발밑 표면 보정 (얼음 위가 아니면 null).</summary>
    public SurfaceModifier CurrentSurface => _surface;
    /// <summary>얼음 등 표면 보정을 반영한 실효 지상 가속 시간. 뜻이 바꾼 GroundAccelTime 에 표면의 추가 시간을 더한다 (가산 → 미끄러운 발 뜻과 겹치면 더 미끄럽다).</summary>
    public float EffectiveGroundAccelTime => _surface != null ? GroundAccelTime + _surface.ExtraGroundAccelTime : GroundAccelTime;
    public float EffectiveGroundDecelTime => _surface != null ? GroundDecelTime + _surface.ExtraGroundDecelTime : GroundDecelTime;
    public float EffectiveIdleFriction => _surface != null ? IdleFriction * _surface.FrictionMultiplier : IdleFriction;

    // ---- 비주얼
    Transform _visual;
    SpriteRenderer _visualSr;
    PlayerSpriteSet _sprites;      // 스프라이트 시트 (없으면 사각형 플레이스홀더)
    bool _pivotBottom;             // 비주얼 스프라이트 피벗이 발(bottom)인가 — 시트는 발, 플레이스홀더 사각형은 중앙
    float _animTime;
    Vector2 _squash = Vector2.one;
    float _squashTimer;
    const float SquashDuration = 0.14f;

    struct Dust { public Transform T; public Vector2 V; public float Life; public SpriteRenderer Sr; }
    readonly List<Dust> _dust = new List<Dust>();

    AudioSource _audio;
    static AudioClip _sfxDeath;   // 사망만 절차음 (에셋 없음). 점프·착지는 SoundBank (Sound.Play)

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
        var sr = visual.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 20;

        var pc = go.AddComponent<PlayerController>();
        pc._visual = visual.transform;
        pc._visualSr = sr;
        pc._sprites = PlayerSpriteSet.LoadOrNull();
        if (pc._sprites != null)
        {
            // 시트: PPU 가 idle 높이 = BodySize.y 로 맞춰져 있고 피벗은 발 → 스케일 1, 색 흰색 (Docs/102 1.1)
            sr.sprite = pc._sprites.Idle[0];
            sr.color = Color.white;
            pc._pivotBottom = true;
        }
        else
        {
            // 플레이스홀더: 1u 흰 사각형(중앙 피벗)을 몸 크기로 늘림
            sr.sprite = RuntimeSprites.White;
            sr.color = BodyColor;
            pc._pivotBottom = false;
        }
        pc.ApplyVisualTransform(Vector2.one);
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
        if (IsGrounded) { _lastGroundedTime = now; _jumping = false; _jumpCut = false; _apexTimer = 0f; _bouncing = false; }

        // 착지 감지 (피드백 + 초록 바운스)
        _pendingBounce = 0f;
        if (IsGrounded && !_wasGrounded)
        {
            if (JumpCooldownAfterLanding > 0f) _cooldownUntil = now + JumpCooldownAfterLanding;
            if (_lastFallSpeed > 2f) OnLanded(_lastFallSpeed);
        }
        // 초록 바운스: 착지했거나 걸어서 초록 선 위로 올라선 스텝에 발동 (속도 확정 후 아래에서 적용)
        if (IsGrounded && _groundBounce != null && (!_wasGrounded || _groundChanged))
            _pendingBounce = Mathf.Max(0f, _groundBounce.SpeedMultiplier);
        _wasGrounded = IsGrounded;

        bool jumpBuffered = Time.time - _jumpPressedTime <= JumpBufferTime;
        bool canJump = (IsGrounded || now - _lastGroundedTime <= CoyoteTime) && !JumpsExhausted && now >= _cooldownUntil;
        bool doJump = jumpBuffered && canJump && _pendingBounce <= 0f;   // 바운스 스텝에는 점프를 소모하지 않는다
        if (jumpBuffered && !canJump && (JumpsExhausted || now < _cooldownUntil)) _jumpPressedTime = float.NegativeInfinity;   // 막힌 입력은 버퍼에 남기지 않는다

        var v = _rb.linearVelocity;
        float target = MoveInput * MoveSpeed;
        bool idle = Mathf.Approximately(target, 0f);

        // ---- 수평 이동
        if (IsGrounded && !doJump)
        {
            var tangent = new Vector2(GroundNormal.y, -GroundNormal.x);        // 노멀에 수직, 오른쪽 방향
            float decelTime = EffectiveGroundDecelTime;                          // 얼음 위에서는 추가 감속 시간이 더해짐 (Docs/101 파랑)
            if (idle)
            {
                if (decelTime <= 0f) v = Vector2.zero;                                              // 정지 즉시 (마찰 재질이 경사에서 붙잡음)
                else { float along = Vector2.Dot(v, tangent); along = Mathf.MoveTowards(along, 0f, (MoveSpeed / decelTime) * dt); v = tangent * along; }
            }
            else
            {
                float along = Vector2.Dot(v, tangent);
                along = Accelerate(along, target, EffectiveGroundAccelTime, dt);
                v = tangent * along;                                             // 경사를 따라 이동
            }
        }
        else
        {
            if (AirControl) v.x = Accelerate(v.x, target, AirAccelTime, dt);   // 공중: 수평만 제어 (공중 제어 금지 뜻이면 유지)
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

        // ---- 초록 바운스: 착지 스텝에 상승 속도 부여 (점프 카운트·쿨다운 무관, 키 떼기 컷 없음)
        bool bounced = false;
        if (_pendingBounce > 0f)
        {
            v.y = Mathf.Max(v.y, JumpSpeed * _pendingBounce);
            _jumpPressedTime = float.NegativeInfinity;
            _lastGroundedTime = float.NegativeInfinity;   // 코요테 점프로 바운스 속도를 덮어쓰지 않게
            _jumping = false; _jumpCut = false; _apexTimer = 0f;
            _bouncing = true;
            bounced = true;
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
        ApplyMaterial(IsGrounded && idle && !doJump && !bounced);

        // ---- 보정 (속도 확정 후 위치 미세 조정)
        if (v.y > 0f && CornerCorrection > 0f) TryCornerCorrection(v.y * dt);
        if (IsGrounded && !idle && StepHeight > 0f) TryStepUp(Mathf.Sign(target));
    }

    float Accelerate(float current, float target, float accelTime, float dt)
    {
        if (Mathf.Approximately(target, 0f)) return 0f;                                      // 감속 즉시
        if (accelTime <= TurnSnapMaxAccelTime && !Mathf.Approximately(current, 0f) && Mathf.Sign(current) != Mathf.Sign(target)) current = 0f;   // 방향 전환 즉시 제동 (미끄러운 상태에서는 관성 유지)
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
        Collider2D nearest = null; float nearestDist = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            var h = _hits[i];
            if (h.collider == _col || h.collider.attachedRigidbody == _rb) continue;
            if (h.normal.sqrMagnitude < 0.5f) continue;
            sum += h.normal; count++;
            if (h.distance < nearestDist) { nearestDist = h.distance; nearest = h.collider; }
        }
        IsGrounded = count > 0;
        GroundNormal = count > 0 ? sum.normalized : Vector2.up;

        // 점프·바운스로 떠오르는 첫 스텝들은 발이 아직 지면 근처라 "지면"으로 잡힌다 → 상승 중에는 무시 (경사 오르막은 _jumping 이 false 라 영향 없음)
        if ((_jumping || _bouncing) && _rb.linearVelocity.y > 0f) { IsGrounded = false; GroundNormal = Vector2.up; nearest = null; }

        SetGroundCollider(nearest);
    }

    /// <summary>발밑 콜라이더가 바뀌면 색상 기능 컴포넌트(얼음·바운스)를 다시 읽고, 얼음 마찰을 정지 재질에 반영한다.</summary>
    void SetGroundCollider(Collider2D ground)
    {
        _groundChanged = ground != _groundCollider;
        if (_groundChanged)
        {
            _groundCollider = ground;
            _surface = ground != null ? ground.GetComponent<SurfaceModifier>() : null;
            _groundBounce = ground != null ? ground.GetComponent<BounceStroke>() : null;
        }
        // 마찰은 매 스텝 비교 — RefreshMaterials(뜻)가 IdleFriction 을 다시 써도 얼음 배율이 유지된다
        float friction = EffectiveIdleFriction;
        if (!Mathf.Approximately(_matIdle.friction, friction))
        {
            _matIdle.friction = friction;
            if (_idleMaterialApplied) _col.sharedMaterial = _matIdle;
        }
    }

    /// <summary>빨강 위험 구역 접촉 → HazardTouched (리스폰 자체는 PlaySession 담당).</summary>
    void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.collider != null && collision.collider.TryGetComponent<HazardStroke>(out _)) HazardTouched?.Invoke();
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
        _cooldownUntil = float.NegativeInfinity;
        _jumpPressedTime = float.NegativeInfinity;
        _lastGroundedTime = float.NegativeInfinity;
        _jumping = false; _jumpCut = false; _apexTimer = 0f;
        _bouncing = false; _pendingBounce = 0f; SetGroundCollider(null);
        _lastFallSpeed = 0f; _wasGrounded = false;
        _squash = Vector2.one; _squashTimer = 0f;
        ApplyVisualTransform(Vector2.one);
        if (_visualSr != null) _visualSr.color = _sprites != null ? Color.white : BodyColor;
        _animTime = 0f;
    }

    public void Freeze()
    {
        InputEnabled = false;
        MoveInput = 0f; JumpHeld = false; FastFallHeld = false;
        _jumpPressedTime = float.NegativeInfinity;
        _rb.linearVelocity = new Vector2(0f, _rb.linearVelocity.y);
    }

    public void Unfreeze() => InputEnabled = true;

    /// <summary>몸집 배율 (뜻: 큰 몸집). 콜라이더·비주얼을 함께 키우고 발이 같은 높이에 오도록 위로 올린다.</summary>
    public void ApplyBodyScale(float scale)
    {
        scale = Mathf.Max(0.25f, scale);
        var before = _bodySize;
        _bodySize = BodySize * scale;
        _col.size = _bodySize;
        ApplyVisualTransform(Vector2.one);
        float lift = (_bodySize.y - before.y) * 0.5f + 0.02f;
        if (lift > 0f) { _rb.position += new Vector2(0f, lift); transform.position += new Vector3(0f, lift, 0f); }
    }

    /// <summary>IdleFriction 등 재질 파라미터를 바꾼 뒤 호출 (뜻: 미끄러운 발).</summary>
    public void RefreshMaterials()
    {
        _matIdle.friction = IdleFriction;
        _col.sharedMaterial = _idleMaterialApplied ? _matIdle : _matMoving;
    }

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
        if (EnableSound) Sound.Play(SfxId.Jump);
    }

    void OnLanded(float fallSpeed)
    {
        Landed?.Invoke(fallSpeed);
        float k = Mathf.Clamp01(fallSpeed / MaxFallSpeed);
        if (EnableSquashStretch) { _squash = new Vector2(1.1f + 0.35f * k, 0.9f - 0.3f * k); _squashTimer = SquashDuration; }
        if (EnableDust) SpawnDust(new Vector2(transform.position.x, _col.bounds.min.y), 3 + Mathf.RoundToInt(3 * k), 1.5f + 2f * k);
        if (EnableSound) Sound.Play(SfxId.Land, 0.5f + 0.5f * k);
    }

    /// <summary>
    /// 비주얼 스케일·위치를 몸집 배율(뜻: 큰 몸집)과 스쿼시 배율로 맞춘다. 발이 콜라이더 바닥에 붙는다.
    /// 시트: 스케일 1 = 몸 크기(PPU 로 맞춤), 피벗 = 발. 플레이스홀더: 1u 사각형(중앙 피벗)을 BodySize 로 늘림.
    /// </summary>
    void ApplyVisualTransform(Vector2 squash)
    {
        if (_visual == null) return;
        float kx = _bodySize.x / BodySize.x, ky = _bodySize.y / BodySize.y;   // 몸집 배율
        float unitX = _sprites != null ? 1f : BodySize.x, unitY = _sprites != null ? 1f : BodySize.y;
        _visual.localScale = new Vector3(unitX * kx * squash.x, unitY * ky * squash.y, 1f);
        float feet = -_bodySize.y * 0.5f;
        float height = _bodySize.y * squash.y;
        _visual.localPosition = new Vector3(0f, _pivotBottom ? feet : feet + height * 0.5f, 0f);
    }

    /// <summary>시트 애니메이션: 지상 정지 Idle / 지상 이동 Walk(속도 비례) / 공중 상승 JumpUp(속도가 줄수록 뒤 프레임) / 공중 하강 JumpDown(낙하가 빨라질수록 뒤 프레임)</summary>
    void UpdateAnimation()
    {
        if (_sprites == null || _visualSr == null) return;
        Sprite frame;
        var v = _rb != null ? _rb.linearVelocity : Vector2.zero;
        if (IsGrounded)
        {
            float speed = Mathf.Abs(v.x);
            if (speed > 0.3f || Mathf.Abs(MoveInput) > 0.1f)
            {
                _animTime += Time.deltaTime * Mathf.Clamp(speed / Mathf.Max(0.1f, MoveSpeed), 0.5f, 1.2f);
                frame = PlayerSpriteSet.Loop(_sprites.Walk, _animTime, _sprites.WalkFps);
            }
            else
            {
                _animTime += Time.deltaTime;
                frame = PlayerSpriteSet.Loop(_sprites.Idle, _animTime, _sprites.IdleFps);
            }
        }
        else if (v.y > 0f)
        {
            frame = PlayerSpriteSet.ByProgress(_sprites.JumpUp, 1f - Mathf.Clamp01(v.y / Mathf.Max(0.1f, JumpSpeed)));   // 최고점에 가까울수록 뒤 프레임
        }
        else
        {
            frame = PlayerSpriteSet.ByProgress(_sprites.JumpDown, Mathf.Clamp01(-v.y / Mathf.Max(0.1f, JumpSpeed)));     // 낙하가 빨라질수록 뒤 프레임 (착지 자세)
        }
        if (frame != null && _visualSr.sprite != frame) _visualSr.sprite = frame;
    }

    void UpdateVisualFeedback()
    {
        if (_visual != null)
        {
            if (_squashTimer > 0f)
            {
                _squashTimer -= Time.deltaTime;
                float t = 1f - Mathf.Clamp01(_squashTimer / SquashDuration);   // 0 → 1
                ApplyVisualTransform(Vector2.Lerp(_squash, Vector2.one, t));
                if (_squashTimer <= 0f) ApplyVisualTransform(Vector2.one);
            }
        }
        UpdateAnimation();

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
        if (_sfxDeath != null) return;
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
