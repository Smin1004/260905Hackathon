using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 플랫포머 플레이어 컨트롤러 (Docs/202 1장). 좌우 이동 + 점프만.
/// 조작: 이동 A/D 또는 ←/→, 점프 W 또는 ↑ (스페이스바 없음). 신 Input System의 Keyboard.current 를 직접 읽는다.
/// 뜻(제약) 시스템은 이후 입력 필터(VowModifier)로 MoveInput/점프 허용을 가로채는 방식으로 붙인다 (Docs/202 2장).
/// </summary>
[RequireComponent(typeof(Rigidbody2D), typeof(BoxCollider2D))]
public class PlayerController : MonoBehaviour
{
    [Header("튜닝 (Docs/202 1장 초기값)")]
    public float MoveSpeed = 5f;
    public float JumpSpeed = 10f;
    public float GravityScale = 2f;

    public bool InputEnabled = true;
    public bool IsGrounded { get; private set; }
    public float MoveInput { get; private set; }
    public int JumpCount { get; private set; }

    public event Action Jumped;

    Rigidbody2D _rb;
    ContactFilter2D _groundFilter;
    SpriteRenderer _visual;
    bool _jumpQueued;

    public static readonly Vector2 BodySize = new Vector2(0.6f, 0.9f);

    /// <summary>플레이어를 코드로 생성 (플레이스홀더 비주얼). 아트 교체 시 프리팹으로 대체 — Docs/102 1.1.</summary>
    public static PlayerController Spawn(Vector2 position, Transform parent = null)
    {
        var go = new GameObject("Player");
        go.tag = "Player";
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(position.x, position.y, 0f);

        var rb = go.AddComponent<Rigidbody2D>();
        var col = go.AddComponent<BoxCollider2D>();
        col.size = BodySize;
        col.sharedMaterial = new PhysicsMaterial2D("Player (no friction)") { friction = 0f, bounciness = 0f };

        var visual = new GameObject("Visual");
        visual.transform.SetParent(go.transform, false);
        visual.transform.localScale = new Vector3(BodySize.x, BodySize.y, 1f);
        var sr = visual.AddComponent<SpriteRenderer>();
        sr.sprite = RuntimeSprites.White;
        sr.color = new Color(0.95f, 0.45f, 0.2f);
        sr.sortingOrder = 20;

        var pc = go.AddComponent<PlayerController>();
        pc._visual = sr;
        pc.ConfigureBody(rb);
        return pc;
    }

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        ConfigureBody(_rb);
        _groundFilter = new ContactFilter2D();
        _groundFilter.useTriggers = false;
        _groundFilter.SetNormalAngle(40f, 140f);   // 위를 향한 접촉면만 지면 (경사 ±50°까지)
        if (_visual == null) _visual = GetComponentInChildren<SpriteRenderer>();
    }

    void ConfigureBody(Rigidbody2D rb)
    {
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = GravityScale;
        rb.freezeRotation = true;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;   // 두께 없는 선 콜라이더 터널링 방지
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.sleepMode = RigidbodySleepMode2D.NeverSleep;
    }

    void Update()
    {
        if (!InputEnabled) { MoveInput = 0f; return; }
        var kb = Keyboard.current;
        if (kb == null) return;

        float x = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
        MoveInput = x;

        if (kb.wKey.wasPressedThisFrame || kb.upArrowKey.wasPressedThisFrame) _jumpQueued = true;

        if (_visual != null && x != 0f) _visual.flipX = x < 0f;
    }

    void FixedUpdate()
    {
        IsGrounded = _rb.IsTouching(_groundFilter);

        var v = _rb.linearVelocity;
        v.x = MoveInput * MoveSpeed;

        if (_jumpQueued)
        {
            _jumpQueued = false;
            if (IsGrounded)
            {
                v.y = JumpSpeed;
                JumpCount++;
                Jumped?.Invoke();
            }
        }
        _rb.linearVelocity = v;
    }

    /// <summary>시작점으로 복귀. 속도·점프 카운터 초기화 (Docs/100 7.3: 리스폰 시 뜻 카운터 초기화).</summary>
    public void Respawn(Vector2 position)
    {
        _rb.linearVelocity = Vector2.zero;
        _rb.angularVelocity = 0f;
        _rb.position = position;
        transform.position = new Vector3(position.x, position.y, 0f);
        JumpCount = 0;
        _jumpQueued = false;
    }

    public void Freeze()
    {
        InputEnabled = false;
        MoveInput = 0f;
        _rb.linearVelocity = new Vector2(0f, _rb.linearVelocity.y);
    }
}
