using System;
using UnityEngine;

/// <summary>방 설정 4종 — Docs/100 7.1, Docs/205 3장 세션 프로퍼티와 1:1.</summary>
[Serializable]
public class RoomSettings
{
    public bool ParTimeMode = false;
    /// <summary>0 = 무한 / 3 / 5</summary>
    public int AttemptLimit = 0;
    /// <summary>초. 120 / 300 / 600</summary>
    public int DrawTimeLimit = 300;
    /// <summary>초. 120 / 180 / 300 — 검증·교환 플레이 공용</summary>
    public int PlayTimeLimit = 180;
    /// <summary>라운드마다 각자 고르는 뜻의 개수 (Docs/100 4.1 "N개 선택")</summary>
    public int VowPickCount = 1;
    /// <summary>제시되는 후보 수 (전체 이상이면 전체). 0 = 전체</summary>
    public int VowCandidateCount = 5;
}

/// <summary>플레이 결과 — Docs/206 1장.</summary>
[Serializable]
public class PlayerRecord
{
    public bool Cleared;
    /// <summary>초. 시작부터 골 도달까지 (리스폰해도 리셋 안 됨)</summary>
    public float ClearTime;
    /// <summary>1 + R키 수동 리스폰 횟수 (낙하 자동 리스폰은 미소모)</summary>
    public int AttemptsUsed = 1;
    /// <summary>미클리어 (시도 제한 소진 또는 플레이 시간 만료)</summary>
    public bool GaveUp;
}

/// <summary>
/// 매치 전체 상태 — Docs/201 3장 공유 데이터 계약. Boot 소유, DontDestroyOnLoad.
/// Boot 씬이 없는 상태(개별 씬 단독 테스트)에서도 동작하도록 Instance 접근 시 자동 생성한다.
/// </summary>
public class MatchData : MonoBehaviour
{
    static MatchData _instance;

    public static MatchData Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<MatchData>();
                if (_instance == null)
                {
                    var go = new GameObject("MatchData");
                    _instance = go.AddComponent<MatchData>();
                }
            }
            return _instance;
        }
    }

    /// <summary>내가 고른 뜻 — 교환 플레이(상대 맵)에 적용</summary>
    public System.Collections.Generic.List<VowId> MyVows = new System.Collections.Generic.List<VowId>();
    /// <summary>상대가 고른 뜻 — 내 맵 검증 플레이에 적용, 에디터에 표시</summary>
    public System.Collections.Generic.List<VowId> OpponentVows = new System.Collections.Generic.List<VowId>();
    public MapData MyMap;            // 내가 만든 맵 (검증 완료본)
    public MapData OpponentMap;      // 상대가 만든 맵
    public RoomSettings Settings = new RoomSettings();
    public float MyParTime;          // 내 맵의 검증 기록(패타임)
    public float OpponentParTime;
    public PlayerRecord MyResult;
    public PlayerRecord OpponentResult;
    public string MyNickname = "플레이어1";
    public string OpponentNickname = "플레이어2";

    void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>같은 방에서 다시 하기 — 방 설정·닉네임은 유지.</summary>
    public void ResetMatch()
    {
        MyVows.Clear(); OpponentVows.Clear();
        MyMap = OpponentMap = null;
        MyParTime = OpponentParTime = 0f;
        MyResult = OpponentResult = null;
    }
}
