using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ChoPilot.Server;

// ─────────────────────────────────────────────────────────────────────────────
// 요청 주체 해석 (ARCHITECTURE §8).
//
// 지금까지 서버는 X-ChoPilot-User 헤더를 그대로 믿었고, 그 위험은 주석과 README 한 줄에만
// 적혀 있었다. 문서는 배포를 막지 못한다. 그래서 두 가지를 코드로 옮긴다:
//
//   1. 주체 해석을 <b>한 인터페이스</b> 뒤로 갈라 실제 인증(JWT/OIDC)이 들어올 자리를 만든다.
//   2. 검증하지 않는 방식으로는 <b>운영 환경에서 서버가 뜨지 않는다</b>. 부주의로 나갈 수 없다.
//
// "누구냐"에 답하지 못하는 요청은 400(잘못된 요청)이 아니라 <b>401</b>이다 — 본문이 틀린 게
// 아니라 신원이 없는 것이고, 클라이언트가 고칠 것도 본문이 아니라 자격증명이다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>요청 주체. <see cref="Method"/>는 어떻게 알아냈는지 — 감사에 남을 값이다.</summary>
public sealed record UserPrincipal(
    string UserId,
    string Method,
    string TenantId = "default",
    IReadOnlySet<string>? Roles = null)
{
    public IReadOnlySet<string> EffectiveRoles { get; } =
        Roles ?? new HashSet<string>(StringComparer.Ordinal);

    public bool IsInRole(string role) => EffectiveRoles.Contains(role);
}

public static class ChoPilotRole
{
    public const string IngestionClient = "ingestion_client";
    public const string EndUser = "end_user";
    public const string Reviewer = "reviewer";
    public const string KnowledgeAdmin = "knowledge_admin";
    public const string OpsAuditor = "ops_auditor";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[] { IngestionClient, EndUser, Reviewer, KnowledgeAdmin, OpsAuditor },
        StringComparer.Ordinal);
}

public static class AuthMethod
{
    /// <summary>헤더 자칭. <b>검증이 없다</b> — 개발·측정 전용.</summary>
    public const string TrustedHeader = "trusted_header";

    /// <summary>서명·발급자·수신자·만료를 검증한 JWT.</summary>
    public const string Jwt = "jwt";
}

/// <summary>
/// 요청에서 주체를 해석한다. 구현이 무엇이든 <b>본문·쿼리에서는 사용자를 받지 않는다</b> —
/// 본문이 사용자를 정하면 누구나 남의 personal 스코프에 쓸 수 있고, D5의 격리가
/// 스코프 문자열로만 존재하게 된다.
/// </summary>
public interface IUserPrincipalResolver
{
    string Method { get; }

    /// <summary>주체를 <b>실제로 검증</b>하는가. false면 클라이언트가 자기 이름을 대는 것뿐이다.</summary>
    bool Verifies { get; }

    /// <summary>인증 실패·부재는 null. 예외를 던지지 않는다.</summary>
    UserPrincipal? Resolve(HttpContext context);

    /// <summary>401에 실을 인증 방식 안내. 헤더 방식엔 표준 스킴이 없으므로 null.</summary>
    string? Challenge { get; }
}

/// <summary>
/// 헤더를 그대로 믿는다 — <b>인증이 아니다.</b> 측정 콘솔과 로컬 개발을 위해 남아 있고,
/// 운영 환경에서는 기동이 거부된다(<see cref="AuthenticationSetup"/>).
/// </summary>
public sealed class TrustedHeaderPrincipalResolver : IUserPrincipalResolver
{
    public const string HeaderName = "X-ChoPilot-User";
    public const string TenantHeaderName = "X-ChoPilot-Tenant";
    public const string RolesHeaderName = "X-ChoPilot-Roles";

    public string Method => AuthMethod.TrustedHeader;
    public bool Verifies => false;
    public string? Challenge => null;

    public UserPrincipal? Resolve(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values) ||
            values.ToString().Trim() is not { Length: > 0 } value)
            return null;

        var tenant = context.Request.Headers[TenantHeaderName].ToString().Trim();
        if (tenant.Length == 0) tenant = "default";

        var suppliedRoles = context.Request.Headers[RolesHeaderName].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(ChoPilotRole.All.Contains)
            .ToHashSet(StringComparer.Ordinal);

        // trusted_header 자체가 개발 전용이다. 역할 헤더가 없으면 기존 측정 콘솔을 위해 전 역할을 준다.
        // 운영에서는 이 resolver로 기동할 수 없으므로 권한 경계를 약화시키지 않는다.
        return new UserPrincipal(value, Method, tenant,
            suppliedRoles.Count == 0 ? ChoPilotRole.All : suppliedRoles);
    }
}

/// <summary>JWT 검증 설정. 대칭키(HS256)와 비대칭 발급자 중 하나를 쓴다.</summary>
public sealed record JwtAuthOptions
{
    public string? Issuer { get; init; }
    public string? Audience { get; init; }

    /// <summary>HS256 공유 비밀. 운영에서는 발급자 공개키(JWKS)로 대체할 자리다.</summary>
    public string? SigningKey { get; init; }

    /// <summary>사용자 식별자를 담은 클레임. 기본 <c>sub</c>.</summary>
    public string SubjectClaim { get; init; } = "sub";
    public string TenantClaim { get; init; } = "tenant_id";
    public string RoleClaim { get; init; } = "role";
}

/// <summary>
/// Bearer 토큰의 <b>서명·발급자·수신자·만료</b>를 검증하고 주체 클레임을 꺼낸다.
///
/// <para>
/// 검증 실패를 예외로 새어 나가게 두지 않는다 — 만료된 토큰 하나가 500을 만들면
/// 클라이언트 스풀이 그것을 서버 장애로 읽는다(§7.1).
/// </para>
/// </summary>
public sealed class JwtPrincipalResolver : IUserPrincipalResolver
{
    private readonly TokenValidationParameters _parameters;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly string _subjectClaim;
    private readonly string _tenantClaim;
    private readonly string _roleClaim;

    public JwtPrincipalResolver(JwtAuthOptions options)
    {
        if (options.SigningKey is not { Length: > 0 } key)
            throw new InvalidOperationException("Auth:Jwt:SigningKey가 없다 — 서명을 검증할 수 없는 인증은 인증이 아니다");
        if (Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("Auth:Jwt:SigningKey는 최소 32바이트여야 한다");

        _subjectClaim = options.SubjectClaim;
        _tenantClaim = options.TenantClaim;
        _roleClaim = options.RoleClaim;
        _parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateIssuer = options.Issuer is { Length: > 0 },
            ValidIssuer = options.Issuer,
            ValidateAudience = options.Audience is { Length: > 0 },
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        };
    }

    public string Method => AuthMethod.Jwt;
    public bool Verifies => true;
    public string? Challenge => "Bearer";

    public UserPrincipal? Resolve(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0) return null;

        try
        {
            var claims = _handler.ValidateToken(token, _parameters, out _);
            var subject = claims.FindFirst(_subjectClaim)?.Value
                          ?? claims.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (subject is not { Length: > 0 }) return null;

            var tenant = claims.FindFirst(_tenantClaim)?.Value;
            if (string.IsNullOrWhiteSpace(tenant)) tenant = "default";

            var roles = claims.FindAll(_roleClaim)
                .Concat(claims.FindAll(ClaimTypes.Role))
                .SelectMany(c => c.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(ChoPilotRole.All.Contains)
                .ToHashSet(StringComparer.Ordinal);

            return new UserPrincipal(subject, Method, tenant, roles);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;   // 서명 위조·만료·발급자 불일치 — 전부 "신원 없음"이다
        }
    }
}

/// <summary>기동 시 인증 설정을 고르고, 안전하지 않은 조합을 <b>거부</b>한다.</summary>
public static class AuthenticationSetup
{
    public const string ModeHeader = "header";
    public const string ModeJwt = "jwt";

    /// <summary>
    /// 설정에서 해석기를 만든다.
    ///
    /// <para>
    /// 검증하지 않는 방식은 운영 환경에서 <b>예외로 기동을 막는다</b>. 경고 로그로 두면
    /// 아무도 읽지 않고, 그 서버는 헤더 한 줄로 누구든 사칭할 수 있는 채로 돌아간다.
    /// 정말 그래야 한다면 <c>Auth:AllowUnverifiedInProduction=true</c>로 <b>명시</b>하게 한다 —
    /// 사고가 아니라 결정이 되도록.
    /// </para>
    /// </summary>
    public static IUserPrincipalResolver Create(IConfiguration cfg, bool isProduction)
    {
        var mode = cfg["Auth:Mode"]?.Trim().ToLowerInvariant() ?? ModeHeader;

        if (mode == ModeJwt)
        {
            var issuer = cfg["Auth:Jwt:Issuer"];
            var audience = cfg["Auth:Jwt:Audience"];
            if (isProduction && (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience)))
                throw new InvalidOperationException(
                    "운영 JWT는 Auth:Jwt:Issuer와 Auth:Jwt:Audience를 모두 검증해야 한다");

            return new JwtPrincipalResolver(new JwtAuthOptions
            {
                Issuer = issuer,
                Audience = audience,
                SigningKey = cfg["Auth:Jwt:SigningKey"],
                SubjectClaim = cfg["Auth:Jwt:SubjectClaim"] ?? "sub",
                TenantClaim = cfg["Auth:Jwt:TenantClaim"] ?? "tenant_id",
                RoleClaim = cfg["Auth:Jwt:RoleClaim"] ?? "role",
            });
        }

        if (mode != ModeHeader)
            throw new InvalidOperationException($"Auth:Mode '{mode}'는 {ModeHeader}|{ModeJwt} 중 하나여야 한다");

        if (isProduction && !cfg.GetValue<bool>("Auth:AllowUnverifiedInProduction"))
            throw new InvalidOperationException(
                $"운영 환경에서 Auth:Mode={ModeHeader}는 거부된다 — {TrustedHeaderPrincipalResolver.HeaderName} " +
                "헤더는 검증되지 않아 누구든 사칭할 수 있다. Auth:Mode=jwt로 바꾸거나, " +
                "신뢰 경계 안(VPC/mTLS)임을 확인했다면 Auth:AllowUnverifiedInProduction=true로 명시하라.");

        return new TrustedHeaderPrincipalResolver();
    }
}

/// <summary>
/// 모든 요청이 지나는 단 하나의 주체 관문. 클라이언트의 PrivacyGate가 전송 경계에 하나만
/// 있는 것과 같은 이유다 — 관문이 여럿이면 그중 하나는 반드시 빠뜨린다.
/// </summary>
public sealed class PrincipalMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IUserPrincipalResolver _resolver;

    public PrincipalMiddleware(RequestDelegate next, IUserPrincipalResolver resolver)
    {
        _next = next;
        _resolver = resolver;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_resolver.Resolve(context) is { } principal)
            context.Items[RequestUser.ItemKey] = principal;

        return _next(context);
    }
}
