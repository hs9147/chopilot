namespace ChoPilot.Server;

/// <summary>
/// 해석된 요청 주체를 엔드포인트가 읽는 자리.
///
/// <para>
/// 주체는 <see cref="PrincipalMiddleware"/>가 <b>한 번</b> 정하고 여기서 읽기만 한다.
/// 엔드포인트가 직접 헤더를 읽던 시절에는 인증 방식을 바꾸려면 모든 엔드포인트를 고쳐야 했고,
/// 그중 하나를 빠뜨리면 그 엔드포인트만 옛 방식으로 열려 있었다.
/// </para>
/// <para>
/// 어느 구현이든 <b>본문·쿼리에서 사용자를 받지 않는다</b>. 본문이 사용자를 정하면
/// 누구나 남의 personal 스코프에 매핑을 심을 수 있고, D5의 격리가 스코프 문자열로만 존재한다.
/// </para>
/// </summary>
public static class RequestUser
{
    internal const string ItemKey = "chopilot.principal";

    /// <summary>헤더 방식의 헤더 이름. 측정 콘솔과 테스트가 쓴다 — 인증이 아니다.</summary>
    public const string Header = TrustedHeaderPrincipalResolver.HeaderName;

    /// <summary>해석된 주체. 인증되지 않았으면 null.</summary>
    public static UserPrincipal? Principal(HttpRequest request) =>
        request.HttpContext.Items.TryGetValue(ItemKey, out var value) ? value as UserPrincipal : null;

    /// <summary>사용자 식별자만. 인증되지 않았으면 null.</summary>
    public static string? From(HttpRequest request) => Principal(request)?.UserId;

    public static string Tenant(HttpRequest request) => Principal(request)?.TenantId ?? "default";

    public static bool HasAnyRole(HttpRequest request, params string[] roles) =>
        Principal(request) is { } principal && roles.Any(principal.IsInRole);

    /// <summary>
    /// 주체가 필요한 엔드포인트의 한 줄 관문.
    /// 신원이 없으면 <b>401</b>이다 — 본문이 틀린 게 아니라 자격증명이 없는 것이다.
    /// </summary>
    public static IResult? Require(HttpRequest request, out string userId)
    {
        if (From(request) is { } resolved)
        {
            userId = resolved;
            return null;
        }

        userId = "";
        var resolver = request.HttpContext.RequestServices.GetRequiredService<IUserPrincipalResolver>();
        if (resolver.Challenge is { } scheme)
            request.HttpContext.Response.Headers.WWWAuthenticate = scheme;

        return Results.Json(new
        {
            error = "unauthenticated",
            detail = resolver.Method == AuthMethod.TrustedHeader
                ? $"{Header} 헤더가 필요하다 (개발 모드 — 검증되지 않는다)"
                : "유효한 Bearer 토큰이 필요하다",
            method = resolver.Method,
        }, statusCode: StatusCodes.Status401Unauthorized);
    }

    public static IResult? RequireAnyRole(HttpRequest request, out UserPrincipal principal, params string[] roles)
    {
        if (Principal(request) is not { } resolved)
        {
            principal = new UserPrincipal("", "");
            Require(request, out _);
            var resolver = request.HttpContext.RequestServices.GetRequiredService<IUserPrincipalResolver>();
            if (resolver.Challenge is { } scheme)
                request.HttpContext.Response.Headers.WWWAuthenticate = scheme;
            return Results.Json(new { error = "unauthenticated", method = resolver.Method },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        principal = resolved;
        if (roles.Length == 0 || roles.Any(resolved.IsInRole)) return null;

        return Results.Json(new
        {
            error = "forbidden",
            required_roles = roles,
        }, statusCode: StatusCodes.Status403Forbidden);
    }
}
