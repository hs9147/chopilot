namespace ChoPilot.Server;

/// <summary>
/// 요청자 식별 — <b>인증이 아니다.</b> 헤더 값을 그대로 신뢰한다.
///
/// <para>
/// 그럼에도 이 한 곳을 통과하게 만드는 이유는, 개인 스코프의 읽기·쓰기가
/// <b>요청 본문이나 쿼리스트링에서 사용자를 받지 않게</b> 하기 위해서다.
/// 본문이 사용자를 정하면 누구나 남의 personal 스코프에 매핑을 심을 수 있고,
/// D5가 요구하는 사용자별 격리가 스코프 문자열로만 존재하게 된다.
/// </para>
/// <para>
/// 여기가 실제 인증(mTLS 클라이언트 인증서 / OIDC 토큰)이 들어갈 자리다.
/// 그때까지 <b>이 서버를 신뢰 경계 밖에 노출하면 안 된다</b> (ARCHITECTURE §8: VPC 내부, mTLS).
/// </para>
/// </summary>
public static class RequestUser
{
    public const string Header = "X-ChoPilot-User";

    /// <summary>헤더에서 사용자 식별자를 읽는다. 없거나 비면 null.</summary>
    public static string? From(HttpRequest request) =>
        request.Headers.TryGetValue(Header, out var values) &&
        values.ToString() is { Length: > 0 } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}
