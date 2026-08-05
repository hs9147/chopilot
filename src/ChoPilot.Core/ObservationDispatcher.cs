namespace ChoPilot.Core;

/// <summary>
/// 한 번의 전송 시도 결과. <c>Pending</c>은 시도 후 스풀에 남은 이벤트 수,
/// <c>Rejected</c>는 배출 중 영구 거부로 격리된 이벤트 수다.
/// </summary>
public sealed record DispatchResult(int Drained, int Rejected, SendOutcome Outcome, int Pending)
{
    public bool Sent => Outcome == SendOutcome.Sent;
}

/// <summary>
/// 관측 이벤트 전송 순서·유실 방지 정책 (PHASE1-DESIGN §7 가용성 NFR).
/// "밀린 스풀 먼저 → 현재 이벤트" 순서를 강제하고, <b>성공하지 못한 이벤트는 예외 여부와 무관하게</b>
/// durable 스풀에 적재한다. 인라인 try/catch가 아닌 단일 함수로 둬 단위 테스트가 가능하다.
/// </summary>
public static class ObservationDispatcher
{
    /// <param name="sender">전송 성공 시 true. 예외를 던져도 실패로 처리된다.</param>
    /// <remarks>
    /// 실패를 전부 재시도로 본다 — 서버가 계약 위반으로 <b>거부</b>한 이벤트까지 스풀에 쌓인다.
    /// 실제 전송 경로는 <see cref="SendOutcome"/> 오버로드를 써야 한다.
    /// </remarks>
    public static Task<DispatchResult> DispatchAsync(
        EventSpool spool,
        ObservationEvent evt,
        Func<ObservationEvent, Task<bool>> sender,
        CancellationToken ct = default) =>
        DispatchAsync(spool, evt,
            async e => await sender(e) ? SendOutcome.Sent : SendOutcome.Retry, ct);

    public static async Task<DispatchResult> DispatchAsync(
        EventSpool spool,
        ObservationEvent evt,
        Func<ObservationEvent, Task<SendOutcome>> sender,
        CancellationToken ct = default)
    {
        // 1) 서버 복구 시 밀린 스풀을 오래된 순으로 먼저 재전송.
        //    영구 거부는 격리되고 배출은 계속된다 — 그것 하나가 뒤를 다 막지 않도록.
        var drain = await spool.DrainAsync(sender, ct);

        // 2) 스풀이 아직 남아 있다 = 재전송이 재시도 가능한 실패로 중단됐다.
        //    현재 이벤트를 먼저 보내면 FIFO가 깨지므로 시도 없이 스풀 뒤에 붙인다.
        if (spool.PendingCount > 0)
        {
            spool.Enqueue(evt);
            return new DispatchResult(drain.Sent, drain.Rejected, SendOutcome.Retry, spool.PendingCount);
        }

        SendOutcome outcome;
        try { outcome = await sender(evt); }
        catch when (!ct.IsCancellationRequested) { outcome = SendOutcome.Retry; }

        // 3) 재시도 가능한 실패(오프라인·5xx·타임아웃)만 유실 대신 스풀 적재.
        //    영구 거부를 적재하면 다음 실행에서 큐 머리를 막아 그 뒤 관측이 전부 멎는다.
        if (outcome == SendOutcome.Retry) spool.Enqueue(evt);

        return new DispatchResult(drain.Sent, drain.Rejected, outcome, spool.PendingCount);
    }
}
