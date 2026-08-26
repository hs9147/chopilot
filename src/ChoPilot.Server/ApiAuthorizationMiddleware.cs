namespace ChoPilot.Server;

/// <summary>모든 /v1 API에 기본 거부 권한 정책을 한 곳에서 적용한다.</summary>
public sealed class ApiAuthorizationMiddleware
{
    private readonly RequestDelegate _next;

    public ApiAuthorizationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/v1/", StringComparison.Ordinal) || path == "/v1/auth")
        {
            await _next(context);
            return;
        }

        // Header 모드는 로컬 측정 전용이며 기존 CLI/테스트의 무인 적재도 지원한다.
        // 검증되는 JWT/OIDC 운영 경로에서는 아래 정책이 항상 적용된다.
        // 각 쓰기 endpoint는 여전히 RequestUser 관문에서 필요한 경우 401을 낸다.
        var resolver = context.RequestServices.GetRequiredService<IUserPrincipalResolver>();
        if (!resolver.Verifies)
        {
            await _next(context);
            return;
        }

        var roles = RequiredRoles(context.Request.Method, path);
        if (RequestUser.RequireAnyRole(context.Request, out _, roles) is { } denied)
        {
            await denied.ExecuteAsync(context);
            return;
        }
        await _next(context);
    }

    private static string[] RequiredRoles(string method, string path)
    {
        if (path == "/v1/observations" && method == HttpMethods.Post)
            return new[] { ChoPilotRole.IngestionClient, ChoPilotRole.EndUser };
        if (path.StartsWith("/v1/guide", StringComparison.Ordinal) ||
            path.StartsWith("/v1/me/", StringComparison.Ordinal) ||
            path.StartsWith("/v1/feedback", StringComparison.Ordinal) ||
            path == "/v1/uep" ||
            path.StartsWith("/v1/correction", StringComparison.Ordinal) ||
            path.StartsWith("/v1/suggestions/feedback", StringComparison.Ordinal) ||
            path.StartsWith("/v1/suggestions/impressions", StringComparison.Ordinal))
            return new[] { ChoPilotRole.EndUser, ChoPilotRole.OpsAuditor };
        if (path.StartsWith("/v1/reviews", StringComparison.Ordinal) ||
            path.StartsWith("/v1/review", StringComparison.Ordinal))
            return new[] { ChoPilotRole.Reviewer };
        if (path.StartsWith("/v1/knowledge", StringComparison.Ordinal))
            return method == HttpMethods.Get
                ? new[] { ChoPilotRole.EndUser, ChoPilotRole.Reviewer, ChoPilotRole.KnowledgeAdmin, ChoPilotRole.OpsAuditor }
                : new[] { ChoPilotRole.KnowledgeAdmin };
        if (path == "/v1/ontology")
            return new[] { ChoPilotRole.EndUser, ChoPilotRole.Reviewer, ChoPilotRole.KnowledgeAdmin };
        if (path.StartsWith("/v1/foundation", StringComparison.Ordinal))
            return method == HttpMethods.Get
                ? new[] { ChoPilotRole.OpsAuditor, ChoPilotRole.KnowledgeAdmin }
                : new[] { ChoPilotRole.KnowledgeAdmin };
        return new[] { ChoPilotRole.OpsAuditor };
    }
}
