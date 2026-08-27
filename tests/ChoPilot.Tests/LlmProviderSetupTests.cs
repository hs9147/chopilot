using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// LLM 공급자 결정 — 기본은 azure_openai.
//
// 갈림의 요점: 명시하지 않은 기본값이 준비 안 됐으면 스텁으로 내려앉고(설정 없는 서버도
// 떠야 한다), 명시한 공급자의 설정이 틀리면 기동을 거부한다(그건 오타지 기본값이 아니다).
// ─────────────────────────────────────────────────────────────────────────────
public class LlmProviderSetupTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static (string Key, string? Value)[] AzureReady => new (string, string?)[]
    {
        ("Llm:AzureOpenAI:Endpoint", "https://example.openai.azure.com"),
        ("Llm:AzureOpenAI:Deployment", "gpt-4o"),
        ("Llm:AzureOpenAI:ApiVersion", "2024-10-21"),
        ("Llm:AzureOpenAI:ApiKey", "secret"),
    };

    [Fact]
    public void DefaultIsAzureOpenAi_WhenItIsConfigured()
    {
        var selection = LlmProviderSetup.Resolve(Config(AzureReady));

        Assert.Equal(LlmProviderSetup.AzureOpenAi, selection.Provider);
        Assert.False(selection.Explicit);      // 아무도 적지 않았는데 azure로 간다
        Assert.False(selection.FellBack);
    }

    // 설정 없는 서버가 기동조차 못 하면 dotnet run 한 줄로 콘솔을 여는 길이 막힌다.
    [Fact]
    public void UnconfiguredDefault_FallsBackToStub_AndSaysWhatIsMissing()
    {
        var selection = LlmProviderSetup.Resolve(Config());

        Assert.Equal(LlmProviderSetup.Stub, selection.Provider);
        Assert.Equal(LlmProviderSetup.AzureOpenAi, selection.Requested);
        Assert.True(selection.FellBack);
        Assert.Contains("Endpoint", selection.FallbackReason);   // 무엇이 없는지 이름으로 말한다
    }

    // 적어 놓고 빠뜨린 것은 오타다 — 조용히 스텁으로 돌면 "AI를 붙였다"고 믿는 사람에게 거짓말이 된다.
    [Fact]
    public void ExplicitProviderWithBrokenConfig_RefusesToStart()
    {
        var azure = Assert.Throws<InvalidOperationException>(() =>
            LlmProviderSetup.Resolve(Config(("Llm:Provider", "azure_openai"))));
        Assert.Contains("Endpoint", azure.Message);

        var vertex = Assert.Throws<InvalidOperationException>(() =>
            LlmProviderSetup.Resolve(Config(("Llm:Provider", "vertex"))));
        Assert.Contains("ProjectId", vertex.Message);
    }

    [Fact]
    public void ExplicitStub_StaysStub_WithoutPretendingItFellBack()
    {
        var selection = LlmProviderSetup.Resolve(Config(("Llm:Provider", "stub")));

        Assert.Equal(LlmProviderSetup.Stub, selection.Provider);
        Assert.True(selection.Explicit);
        Assert.False(selection.FellBack);      // 고른 것이지 내려앉은 것이 아니다
        Assert.Null(selection.FallbackReason);
    }

    // 이미 UseBedrock=true 를 적어 둔 설정이 새 기본값 때문에 azure로 끌려가면 안 된다.
    [Fact]
    public void ExistingUseBedrockSetting_StillWins()
    {
        var selection = LlmProviderSetup.Resolve(Config(("UseBedrock", "true")));

        Assert.Equal(LlmProviderSetup.Bedrock, selection.Provider);
        Assert.True(selection.Explicit);
    }

    [Theory]
    [InlineData("azure", LlmProviderSetup.AzureOpenAi)]
    [InlineData("AZURE_OPENAI", LlmProviderSetup.AzureOpenAi)]
    [InlineData(" vertex_ai ", LlmProviderSetup.Vertex)]
    public void ProviderNames_AreNormalized(string written, string expected)
    {
        var ready = written.Trim().ToLowerInvariant().StartsWith("azure", StringComparison.Ordinal)
            ? AzureReady
            : new (string, string?)[]
            {
                ("Llm:Vertex:ProjectId", "p"), ("Llm:Vertex:Location", "us-central1"),
                ("Llm:Vertex:Model", "gemini-2.5-flash"),
            };

        var selection = LlmProviderSetup.Resolve(
            Config(ready.Append(("Llm:Provider", written)).ToArray()));

        Assert.Equal(expected, selection.Provider);
    }

    [Fact]
    public void UnknownProvider_IsRefused_WithTheAllowedNames()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LlmProviderSetup.Resolve(Config(("Llm:Provider", "openai"))));

        Assert.Contains("azure_openai", ex.Message);
        Assert.Contains("vertex", ex.Message);
    }
}

public class LlmStatusApiTests
{
    private static WebApplicationFactory<Program> Server(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))));

    // 기본값이 준비되지 않은 채 뜬 서버는 그 사실을 말해야 한다 — 배지가 이걸 읽는다.
    [Fact]
    public async Task LlmStatus_ReportsTheFallbackSoTheConsoleCanShowIt()
    {
        using var server = Server();
        var status = await server.CreateClient().GetFromJsonAsync<JsonElement>("/v1/llm");

        Assert.Equal("stub", status.GetProperty("provider").GetString());
        Assert.Equal("azure_openai", status.GetProperty("requested").GetString());
        Assert.True(status.GetProperty("fellBack").GetBoolean());
        Assert.False(status.GetProperty("isExplicit").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("fallbackReason").GetString()));
    }

    [Fact]
    public async Task ConfiguredAzure_ReportsItselfWithoutAFallback()
    {
        using var server = Server(
            ("Llm:AzureOpenAI:Endpoint", "https://example.openai.azure.com"),
            ("Llm:AzureOpenAI:Deployment", "gpt-4o"),
            ("Llm:AzureOpenAI:ApiVersion", "2024-10-21"),
            ("Llm:AzureOpenAI:ApiKey", "secret"));

        var status = await server.CreateClient().GetFromJsonAsync<JsonElement>("/v1/llm");

        Assert.Equal("azure_openai", status.GetProperty("provider").GetString());
        Assert.False(status.GetProperty("fellBack").GetBoolean());
        Assert.False(status.GetProperty("usingStub").GetBoolean());
    }

    // 자격증명이 없어도 나머지 기능은 그대로 돌아야 한다 — 그게 스텁 폴백의 목적이다.
    [Fact]
    public async Task ServerWithNoCredentials_StillServesTheRestOfTheApi()
    {
        using var server = Server();
        var client = server.CreateClient();

        Assert.Equal("ok", (await client.GetFromJsonAsync<JsonElement>("/health"))
            .GetProperty("status").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/v1/metrics"))
            .GetProperty("observations").GetInt32());
    }
}
