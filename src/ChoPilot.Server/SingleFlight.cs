namespace ChoPilot.Server;

/// <summary>
/// 이름 하나당 동시에 한 번만 도는 작업 게이트.
///
/// <para>
/// 대상은 <b>바깥에 비용이 나가는</b> 수동 트리거다 — 기반 출처 갱신(외부 API 할당량)과
/// 축 집계(초안 1건당 LLM 호출 1회). 이 둘은 요청 시작 시점의 상태로 할 일을 정한 뒤
/// 한참 뒤에 결과를 쓰기 때문에, 겹쳐 들어오면 <b>같은 일을 두 번 하고 나서야</b>
/// 두 번째가 "이미 존재"로 걸린다. 중복 판정이 비용보다 뒤에 있다.
/// </para>
/// <para>
/// 콘솔에서 버튼을 비활성화하는 것으로는 못 막는다. 탭 두 개, 요청 중 새로고침,
/// 자동 반복, 직접 호출이 전부 우회한다 — 버튼 비활성은 피드백이지 가드가 아니다.
/// </para>
/// <para>
/// 두 번째 요청을 첫 번째에 <b>합류시키지 않고 거절</b>한다. 합류하려면 결과를 캐시해야 하고,
/// 그러면 "언제 시점의 결과인지" 모호한 응답이 생긴다. 거절이 정직하다.
/// </para>
/// </summary>
public sealed class SingleFlight
{
    private readonly object _gate = new();
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    /// <summary>지금 도는 중인 작업 이름들. 진단용.</summary>
    public IReadOnlyCollection<string> Running
    {
        get { lock (_gate) return _running.ToArray(); }
    }

    /// <summary>
    /// <paramref name="name"/> 작업을 실행한다. 이미 도는 중이면 <paramref name="work"/>를
    /// 호출하지 않고 <c>null</c>을 돌려준다 — 호출자가 409로 바꾼다.
    /// </summary>
    public async Task<T?> RunAsync<T>(string name, Func<Task<T>> work) where T : class
    {
        lock (_gate)
        {
            if (!_running.Add(name)) return null;
        }

        try
        {
            return await work();
        }
        finally
        {
            // 예외로 빠져나가도 반드시 푼다 — 안 그러면 한 번 실패한 작업이 영구히 막힌다.
            lock (_gate) _running.Remove(name);
        }
    }
}
