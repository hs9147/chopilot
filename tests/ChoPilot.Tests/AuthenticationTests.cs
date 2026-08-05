using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ChoPilot.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 요청 주체 해석 (ARCHITECTURE §8).
//
// 이전에는 X-ChoPilot-User 헤더를 그대로 믿었고, 그 위험이 주석과 README에만 적혀 있었다.
// 문서는 배포를 막지 못한다. 그래서 두 가지를 시험한다:
//
//   1. 실제 검증(JWT)이 들어갈 자리가 열려 있고, 위조·만료·발급자 불일치가 전부 거부되는가.
//   2. 검증하지 않는 방식으로는 <b>운영 환경에서 서버가 뜨지 않는가</b>.
//
// 그리고 신원 없음은 400이 아니라 401이다 — 클라이언트가 고칠 것이 본문이 아니라 자격증명이다.
// ─────────────────────────────────────────────────────────────────────────────

internal static class TestTokens
{
    public const string Key = "chopilot-test-signing-key-at-least-32-bytes-long!!";
    public const string Issuer = "https://idp.test";
    public const string Audience = "chopilot";

    public static string Issue(
        string subject, string? issuer = Issuer, string? audience = Audience,
        string key = Key, TimeSpan? lifetime = null, string claimType = "sub")
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);

        // notBefore는 만료보다 앞서야 한다 — 뒤집힌 토큰은 만료가 아니라 형식 오류로 걸린다.
        var expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10));
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim(claimType, subject) },
            notBefore: expires.AddMinutes(-10),
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static JwtPrincipalResolver Resolver(string? issuer = Issuer, string? audience = Audience) =>
        new(new JwtAuthOptions { Issuer = issuer, Audience = audience, SigningKey = Key });

    public static DefaultHttpContext Bearer(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }
}

public class TrustedHeaderResolverTests
{
    private readonly TrustedHeaderPrincipalResolver _resolver = new();

    [Fact]
    public void DeclaresItselfUnverified()
    {
        // 이 한 줄이 운영 기동 거부와 콘솔 경고를 모두 굴린다.
        Assert.False(_resolver.Verifies);
        Assert.Equal(AuthMethod.TrustedHeader, _resolver.Method);
    }

    [Fact]
    public void ReadsTheHeader_AndNothingElse()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TrustedHeaderPrincipalResolver.HeaderName] = "  alice  ";

        Assert.Equal("alice", _resolver.Resolve(context)!.UserId);
        Assert.Null(_resolver.Resolve(new DefaultHttpContext()));
    }

    [Fact]
    public void EmptyHeader_IsNotAPrincipal()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TrustedHeaderPrincipalResolver.HeaderName] = "   ";
        Assert.Null(_resolver.Resolve(context));
    }
}

public class JwtResolverTests
{
    [Fact]
    public void ValidToken_YieldsTheSubject()
    {
        var principal = TestTokens.Resolver().Resolve(TestTokens.Bearer(TestTokens.Issue("alice")));

        Assert.Equal("alice", principal!.UserId);
        Assert.Equal(AuthMethod.Jwt, principal.Method);
    }

    [Fact]
    public void ForgedSignature_IsRejected()
    {
        var forged = TestTokens.Issue("alice", key: "a-different-signing-key-also-32-bytes-long!!!");
        Assert.Null(TestTokens.Resolver().Resolve(TestTokens.Bearer(forged)));
    }

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        var expired = TestTokens.Issue("alice", lifetime: TimeSpan.FromMinutes(-10));
        Assert.Null(TestTokens.Resolver().Resolve(TestTokens.Bearer(expired)));
    }

    [Fact]
    public void WrongIssuerOrAudience_IsRejected()
    {
        Assert.Null(TestTokens.Resolver().Resolve(
            TestTokens.Bearer(TestTokens.Issue("alice", issuer: "https://evil.test"))));
        Assert.Null(TestTokens.Resolver().Resolve(
            TestTokens.Bearer(TestTokens.Issue("alice", audience: "someone-else"))));
    }

    [Fact]
    public void GarbageToken_IsRejected_NotThrown()
    {
        // 만료된 토큰 하나가 500을 만들면 클라이언트 스풀이 그것을 서버 장애로 읽는다(§7.1).
        Assert.Null(TestTokens.Resolver().Resolve(TestTokens.Bearer("not-a-jwt")));
        Assert.Null(TestTokens.Resolver().Resolve(TestTokens.Bearer("")));
        Assert.Null(TestTokens.Resolver().Resolve(new DefaultHttpContext()));
    }

    [Fact]
    public void HeaderIsIgnored_WhenJwtModeIsOn()
    {
        // 검증되는 방식을 켰는데 헤더가 여전히 통하면 켠 의미가 없다.
        var context = new DefaultHttpContext();
        context.Request.Headers[TrustedHeaderPrincipalResolver.HeaderName] = "alice";

        Assert.Null(TestTokens.Resolver().Resolve(context));
    }

    [Fact]
    public void SigningKeyIsRequired()
    {
        // 서명을 검증할 수 없는 인증은 인증이 아니다 — 조용히 통과시키느니 기동을 막는다.
        var error = Assert.Throws<InvalidOperationException>(() =>
            new JwtPrincipalResolver(new JwtAuthOptions { Issuer = TestTokens.Issuer }));

        Assert.Contains("SigningKey", error.Message);
    }
}

public class AuthenticationSetupTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void DefaultsToHeader_OutsideProduction()
    {
        var resolver = AuthenticationSetup.Create(Config(), isProduction: false);
        Assert.Equal(AuthMethod.TrustedHeader, resolver.Method);
    }

    [Fact]
    public void RefusesToStart_WithUnverifiedAuthInProduction()
    {
        // 경고 로그로 두면 아무도 읽지 않고, 그 서버는 헤더 한 줄로 사칭 가능한 채 돈다.
        var error = Assert.Throws<InvalidOperationException>(() =>
            AuthenticationSetup.Create(Config(), isProduction: true));

        Assert.Contains(RequestUser.Header, error.Message);
        Assert.Contains("Auth:Mode=jwt", error.Message);
    }

    [Fact]
    public void ExplicitOptIn_AllowsUnverifiedInProduction()
    {
        // 정말 신뢰 경계 안이라면 가능해야 한다 — 단 사고가 아니라 결정으로.
        var resolver = AuthenticationSetup.Create(
            Config(("Auth:AllowUnverifiedInProduction", "true")), isProduction: true);

        Assert.False(resolver.Verifies);
    }

    [Fact]
    public void JwtMode_NeedsNoOptIn_InProduction()
    {
        var resolver = AuthenticationSetup.Create(Config(
            ("Auth:Mode", "jwt"),
            ("Auth:Jwt:SigningKey", TestTokens.Key),
            ("Auth:Jwt:Issuer", TestTokens.Issuer)), isProduction: true);

        Assert.True(resolver.Verifies);
    }

    [Fact]
    public void UnknownMode_IsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            AuthenticationSetup.Create(Config(("Auth:Mode", "basic")), isProduction: false));

        Assert.Contains("basic", error.Message);
    }
}

public class AuthenticationApiTests
{
    private static WebApplicationFactory<Program> Server(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))));

    private static WebApplicationFactory<Program> JwtServer() => Server(
        ("Auth:Mode", "jwt"),
        ("Auth:Jwt:SigningKey", TestTokens.Key),
        ("Auth:Jwt:Issuer", TestTokens.Issuer),
        ("Auth:Jwt:Audience", TestTokens.Audience));

    [Fact]
    public async Task Auth_ReportsTheModeAndWhetherItVerifies()
    {
        using var header = Server();
        var reported = await header.CreateClient().GetFromJsonAsync<JsonElement>("/v1/auth");
        Assert.Equal(AuthMethod.TrustedHeader, reported.GetProperty("method").GetString());
        Assert.False(reported.GetProperty("verified").GetBoolean());

        using var jwt = JwtServer();
        var secured = await jwt.CreateClient().GetFromJsonAsync<JsonElement>("/v1/auth");
        Assert.Equal(AuthMethod.Jwt, secured.GetProperty("method").GetString());
        Assert.True(secured.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task MissingPrincipal_Is401_WithAReason()
    {
        using var server = Server();
        var response = await server.CreateClient().PostAsync("/v1/foundation/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unauthenticated", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(AuthMethod.TrustedHeader, doc.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task JwtMode_AcceptsABearerToken_AndRecordsThatSubject()
    {
        using var server = JwtServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Issue("alice"));

        var refresh = await client.PostAsync("/v1/foundation/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        // 결정 이력에 남는 사람은 토큰의 주체다 — 헤더로 대는 이름이 아니다.
        var decisions = await client.GetFromJsonAsync<JsonElement>("/v1/decisions");
        Assert.Contains(decisions.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("actor").GetString() == "alice");
    }

    [Fact]
    public async Task JwtMode_RejectsTheTrustedHeader_WithAChallenge()
    {
        // 검증되는 방식을 켠 뒤에도 헤더가 통하면 켠 의미가 없다.
        using var server = JwtServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(RequestUser.Header, "alice");

        var response = await client.PostAsync("/v1/foundation/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task JwtMode_RejectsAForgedToken()
    {
        using var server = JwtServer();
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Issue("alice", key: "a-different-signing-key-also-32-bytes-long!!!"));

        var response = await client.PostAsync("/v1/foundation/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PersonalScopeFollowsTheToken_NotTheBody()
    {
        // 본문이 사용자를 정하면 누구나 남의 personal 스코프에 매핑을 심을 수 있다(D5).
        using var server = JwtServer();

        var alice = server.CreateClient();
        alice.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Issue("alice"));
        await alice.PostAsJsonAsync("/v1/correction",
            new CorrectionRequest("sig-x", "PurchaseRequest",
                new() { new CorrectionField("n2", "단가") }));

        var bob = server.CreateClient();
        bob.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Issue("bob"));
        var bobsProfile = await bob.GetAsync("/v1/uep");

        var decisions = await alice.GetFromJsonAsync<JsonElement>("/v1/decisions");
        var entry = Assert.Single(decisions.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("action").GetString() == "correction");
        Assert.Equal("alice", entry.GetProperty("actor").GetString());
        Assert.Equal("personal:alice", entry.GetProperty("scope").GetString());

        Assert.Equal(HttpStatusCode.NotFound, bobsProfile.StatusCode);   // bob의 프로파일은 없다
    }
}
