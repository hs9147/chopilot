namespace ChoPilot.Core;

/// <summary>한 번의 전송 시도 결과. <c>Pending</c>은 시도 후 스풀에 남은 이벤트 수.</summary>
public sealed record DispatchResult(int Drained, bool Sent, int Pending);

/// <summary>
/// 관측 이벤트 전송 순서·유실 방지 정책 (PHASE1-DESIGN §7 가용성 NFR).
/// "밀린 스풀 먼저 → 현재 이벤트" 순서를 강제하고, <b>성공하지 못한 이벤트는 예외 여부와 무관하게</b>
/// durable 스풀에 적재한다. 인라인 try/catch가 아닌 단일 함수로 둬 단위 테스트가 가능하다.
/// </summary>
public static class ObservationDispatcher
{
    /// <param name="sender">전송 성공 시 true. 예외를 던져도 실패로 처리된다.</param>
    public static async Task<DispatchResult> DispatchAsync(
        EventSpool spool,
        ObservationEvent evt,
        Func<ObservationEvent, Task<bool>> sender,
        CancellationToken ct = default)
    {
        // 1) 서버 복구 시 밀린 스풀을 오래된 순으로 먼저 재전송.
        var drained = await spool.DrainAsync(sender, ct);

        // 2) 스풀이 아직 남아 있다 = 재전송이 실패해 중단됐다.
        //    현재 이벤트를 먼저 보내면 FIFO가 깨지므로 시도 없이 스풀 뒤에 붙인다.
        if (spool.PendingCount > 0)
        {
            spool.Enqueue(evt);
            return new DispatchResult(drained, Sent: false, spool.PendingCount);
        }

        bool sent;
        try { sent = await sender(evt); }
        catch when (!ct.IsCancellationRequested) { sent = false; }

        // 3) 실패(오프라인·5xx·타임아웃)면 유실 대신 스풀 적재.
        if (!sent) spool.Enqueue(evt);

        return new DispatchResult(drained, sent, spool.PendingCount);
    }
}
