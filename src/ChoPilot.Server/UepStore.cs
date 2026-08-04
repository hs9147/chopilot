using System.Collections.Concurrent;
using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>
/// User Environment Profile 저장소 (ARCHITECTURE §5.4 Personal Plane, PHASE1-DESIGN §5).
/// 관측마다 (user_id, signature) 방문을 누적 → 사용 빈도·최근성. 사용자별 격리.
/// PoC는 인메모리. 운영은 Aurora/별도 스토어(ARCHITECTURE §11).
/// </summary>
public sealed class UepStore
{
    // userId → (signature → usage)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ScreenUsage>> _byUser = new();

    /// <summary>
    /// 화면 방문 1회 기록. FirstSeen 보존, Count 증가, LastSeen 갱신.
    /// <para>
    /// <paramref name="at"/>는 <b>서버 수신 시각</b>을 넘겨라. 이벤트의 <c>CapturedAt</c>은
    /// VDI 클라이언트가 보낸 값이라 시계가 틀어지면 빈도·최근성이 통째로 오염된다.
    /// </para>
    /// </summary>
    public void RecordVisit(string userId, string signature, DateTimeOffset at)
    {
        var screens = _byUser.GetOrAdd(userId, _ => new());
        screens.AddOrUpdate(
            signature,
            _ => new ScreenUsage(signature, 1, at, at),
            (_, prev) => prev with
            {
                Count = prev.Count + 1,
                FirstSeen = at < prev.FirstSeen ? at : prev.FirstSeen,
                LastSeen = at > prev.LastSeen ? at : prev.LastSeen,
            });
    }

    /// <summary>사용자 프로파일 조회. 사용 빈도 내림차순(자주 쓰는 화면 우선).</summary>
    public UserEnvironmentProfile? Get(string userId)
    {
        if (!_byUser.TryGetValue(userId, out var screens)) return null;

        var ordered = screens.Values
            .OrderByDescending(s => s.Count)
            .ThenByDescending(s => s.LastSeen)
            .ToList();

        return new UserEnvironmentProfile(userId, ordered);
    }
}
