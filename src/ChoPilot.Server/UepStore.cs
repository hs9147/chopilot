using System.Collections.Concurrent;
using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>
/// 방문 1회의 원자료. UEP의 상태(빈도·최근성·전이)는 이 입력들의 <b>접기 결과</b>라
/// 접힌 결과가 아니라 입력을 남긴다 — 접는 규칙(세션 단절 기준 등)이 바뀌어도
/// 예전 관측이 새 규칙으로 다시 접힌다.
/// </summary>
public sealed record UepVisit(
    string UserId, string Signature, DateTimeOffset At, string? Route, string? Title,
    string EventId = "", string TenantId = "default");

/// <summary>
/// User Environment Profile 저장소 (ARCHITECTURE §5.4 Personal Plane, PHASE1-DESIGN §5).
/// 관측마다 (user_id, signature) 방문을 누적 → 사용 빈도·최근성, 그리고 <b>화면 전이</b>.
/// 사용자별 격리. PoC는 인메모리. 운영은 Aurora/별도 스토어(ARCHITECTURE §11).
/// </summary>
public sealed class UepStore
{
    /// <summary>
    /// 세션 단절 기준. 이보다 긴 간격은 업무 흐름이 아니라 <b>자리 비움</b>이다.
    /// 끊지 않으면 "구매요청 → (점심) → 발주조회"가 업무 순서로 학습된다.
    /// </summary>
    public static readonly TimeSpan DefaultSessionGap = TimeSpan.FromMinutes(30);

    /// <summary>엣지당 보관하는 간격 표본 수. 중앙값은 최근 이만큼에서만 낸다(메모리 상한).</summary>
    private const int GapSamples = 64;

    /// <summary>
    /// 제안에 필요한 최소 관측 횟수. 한 번 간 길은 흐름이 아니라 우연이다.
    /// </summary>
    public const int DefaultMinTransitionCount = 2;

    private readonly TimeSpan _sessionGap;
    private readonly ConcurrentDictionary<string, UserState> _byUser = new();
    private readonly HashSet<string> _events = new(StringComparer.Ordinal);
    private readonly object _eventGate = new();
    private readonly IJournal<UepVisit> _journal;

    public UepStore() : this(DefaultSessionGap) { }

    public UepStore(TimeSpan sessionGap, IJournalFactory? journals = null)
    {
        _sessionGap = sessionGap;
        _journal = (journals ?? NullJournalFactory.Instance).Open<UepVisit>("uep");

        // 저널은 시간순이다(추가 전용). 전이는 직전 화면에 의존하므로 순서가 곧 정확성이다.
        foreach (var visit in _journal.Load())
        {
            Apply(visit);
            if (visit.EventId.Length > 0) _events.Add(EventKey(visit));
        }
    }

    public UepStore(IJournalFactory? journals) : this(DefaultSessionGap, journals) { }

    /// <summary>
    /// 화면 방문 1회 기록. FirstSeen 보존, Count 증가, LastSeen 갱신,
    /// 직전 화면이 있으면 전이 1건 누적.
    /// <para>
    /// <paramref name="at"/>는 <b>서버 수신 시각</b>을 넘겨라. 이벤트의 <c>CapturedAt</c>은
    /// VDI 클라이언트가 보낸 값이라 시계가 틀어지면 빈도·최근성·전이 간격이 통째로 오염된다.
    /// </para>
    /// </summary>
    public void RecordVisit(
        string userId, string signature, DateTimeOffset at, string? route = null, string? title = null,
        string eventId = "", string tenantId = "default")
    {
        var visit = new UepVisit(userId, signature, at, route, title, eventId, tenantId);
        lock (_eventGate)
        {
            var key = EventKey(visit);
            if (eventId.Length > 0 && _events.Contains(key)) return;
            _journal.Append(visit);
            Apply(visit);
            if (eventId.Length > 0) _events.Add(key);
        }
    }

    /// <summary>접기 1회. 복원 경로가 저널에 다시 쓰지 않도록 기록과 분리돼 있다.</summary>
    private void Apply(UepVisit visit)
    {
        var (userId, signature, at, route, title, _, tenantId) = visit;
        var state = _byUser.GetOrAdd(UserKey(tenantId, userId), _ => new UserState());

        // 방문 누적과 전이 기록은 "직전 화면"을 함께 읽고 쓴다 → 사용자 단위 직렬화.
        lock (state.Gate)
        {
            state.Screens[signature] = state.Screens.TryGetValue(signature, out var prev)
                ? prev with
                {
                    Route = route ?? prev.Route,
                    Title = title ?? prev.Title,
                    Count = prev.Count + 1,
                    FirstSeen = at < prev.FirstSeen ? at : prev.FirstSeen,
                    LastSeen = at > prev.LastSeen ? at : prev.LastSeen,
                }
                : new ScreenUsage(signature, route, title, 1, at, at);

            RecordEdge(state, signature, at, title);

            state.LastSignature = signature;
            state.LastAt = at;
        }
    }

    /// <summary>사용자 프로파일 조회. 사용 빈도 내림차순(자주 쓰는 화면 우선).</summary>
    public UserEnvironmentProfile? Get(string userId, string tenantId = "default")
    {
        if (!_byUser.TryGetValue(UserKey(tenantId, userId), out var state)) return null;

        lock (state.Gate)
        {
            var screens = state.Screens.Values
                .OrderByDescending(s => s.Count)
                .ThenByDescending(s => s.LastSeen)
                .ToList();

            var transitions = state.Edges
                .Select(kv => Project(kv.Key.From, kv.Key.To, kv.Value))
                .OrderByDescending(t => t.Count)
                .ThenByDescending(t => t.LastSeen)
                .ToList();

            return new UserEnvironmentProfile(userId, screens, transitions);
        }
    }

    /// <summary>
    /// 전체 사용자의 프로파일 (축별 집계 전용, ARCHITECTURE §5.5 2단계).
    ///
    /// <para>
    /// 개인 격리(D5)를 뚫는 유일한 통로이므로 <b>집계기만 호출한다</b> — 조회 API에 노출하면
    /// 남의 프로파일이 새어 나간다. 집계 결과가 org로 승격되려면 k인 게이트를 통과해야 하고,
    /// 문서에 실리는 것은 개인 식별자가 아니라 route와 횟수뿐이다.
    /// </para>
    /// </summary>
    public IReadOnlyList<UserEnvironmentProfile> AllProfiles() =>
        _byUser.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => Get(UserFromKey(k), TenantFromKey(k)))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

    /// <summary>
    /// 이 화면 다음에 이어질 화면 후보. 빈도 내림차순 — 다음 작업 제안의 입력.
    /// <paramref name="minCount"/> 미만은 근거가 얇아 제외한다.
    /// </summary>
    public IReadOnlyList<ScreenTransition> NextScreens(
        string userId, string fromSignature, int limit = 3, int minCount = DefaultMinTransitionCount,
        string tenantId = "default")
    {
        if (limit <= 0 || !_byUser.TryGetValue(UserKey(tenantId, userId), out var state))
            return Array.Empty<ScreenTransition>();

        lock (state.Gate)
        {
            return state.Edges
                .Where(kv => kv.Key.From == fromSignature && kv.Value.Count >= minCount)
                .Select(kv => Project(kv.Key.From, kv.Key.To, kv.Value))
                .OrderByDescending(t => t.Count)
                .ThenByDescending(t => t.LastSeen)
                .Take(limit)
                .ToList();
        }
    }

    private static string UserKey(string tenantId, string userId) => $"{tenantId}\u001f{userId}";
    private static string TenantFromKey(string key) => key.Split('\u001f', 2)[0];
    private static string UserFromKey(string key) => key.Split('\u001f', 2)[1];
    private static string EventKey(UepVisit visit) =>
        $"{visit.TenantId}\u001f{visit.UserId}\u001f{visit.EventId}";

    /// <summary>직전 화면에서 지금 화면으로의 전이를 누적. 호출자가 <see cref="UserState.Gate"/>를 잡고 있어야 한다.</summary>
    private void RecordEdge(UserState state, string to, DateTimeOffset at, string? title)
    {
        if (state.LastSignature is not { } from) return;

        // 같은 화면의 재관측은 이동이 아니다. 클라이언트는 한 화면을 여러 번 보내므로
        // 자기 자신으로의 엣지를 남기면 모든 사용자의 그래프를 자기루프가 뒤덮는다.
        if (from == to) return;

        var gap = at - state.LastAt;
        if (gap < TimeSpan.Zero || gap > _sessionGap) return;   // 순서 역전 / 자리 비움

        if (!state.Edges.TryGetValue((from, to), out var edge))
            state.Edges[(from, to)] = edge = new Edge();

        edge.Count++;
        edge.LastSeen = at;
        edge.ToTitle = title ?? edge.ToTitle;

        edge.Gaps.Enqueue(gap.TotalSeconds);
        while (edge.Gaps.Count > GapSamples) edge.Gaps.Dequeue();
    }

    private static ScreenTransition Project(string from, string to, Edge edge) => new(
        FromSignature: from,
        ToSignature: to,
        ToTitle: edge.ToTitle,
        Count: edge.Count,
        MedianGapSeconds: Math.Round(Median(edge.Gaps), 1),
        LastSeen: edge.LastSeen);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>한 사용자의 상태. 방문·전이·직전 화면을 한 잠금 아래 둔다.</summary>
    private sealed class UserState
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, ScreenUsage> Screens = new();
        public readonly Dictionary<(string From, string To), Edge> Edges = new();
        public string? LastSignature;
        public DateTimeOffset LastAt;
    }

    private sealed class Edge
    {
        public int Count;
        public DateTimeOffset LastSeen;
        public string? ToTitle;
        public readonly Queue<double> Gaps = new();   // 최근 GapSamples개만
    }
}
