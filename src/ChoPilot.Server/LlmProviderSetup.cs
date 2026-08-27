using ChoPilot.Mapping;

namespace ChoPilot.Server;

/// <summary>
/// 어떤 LLM 공급자로 뜨는지. <b>요청한 것과 실제로 쓰는 것을 따로 담는다</b> —
/// 기본값이 준비되지 않아 스텁으로 내려앉았을 때 그 사실이 보여야 한다.
/// </summary>
public sealed record LlmProviderSelection(
    string Provider,
    string Requested,

    /// <summary>사람이 <c>Llm:Provider</c>(또는 <c>UseBedrock</c>)로 <b>명시</b>했나.</summary>
    bool Explicit,

    /// <summary>스텁으로 내려앉았다면 무엇이 없어서인지. 정상이면 null.</summary>
    string? FallbackReason)
{
    public bool UsingStub => Provider == LlmProviderSetup.Stub;
    public bool FellBack => FallbackReason is not null;
}

/// <summary>
/// LLM 공급자 결정.
///
/// <para>
/// 기본값은 <b>azure_openai</b>다. 다만 아무것도 설정하지 않은 서버가 기동조차 못 하면
/// <c>dotnet run</c> 한 줄로 콘솔을 여는 길이 막히고 테스트도 자격증명을 요구하게 된다.
/// 그래서 <b>명시하지 않은</b> 기본값이 준비돼 있지 않으면 스텁으로 내려앉되,
/// 무엇이 없어서인지를 이유로 남기고 <c>/v1/llm</c>과 상단 배지로 드러낸다.
/// </para>
/// <para>
/// <b>명시한 경우는 다르다.</b> <c>Llm:Provider=azure_openai</c>라고 적어 놓고 엔드포인트를
/// 빼먹었다면 그건 오타지 기본값이 아니므로 기동을 거부한다 — 조용히 스텁으로 도는 서버는
/// "AI를 붙였다"고 믿는 사람에게 거짓말을 한다.
/// </para>
/// </summary>
public static class LlmProviderSetup
{
    public const string Stub = "stub";
    public const string Bedrock = "bedrock";
    public const string Vertex = "vertex";
    public const string AzureOpenAi = "azure_openai";

    /// <summary>설정이 없을 때 고르는 공급자.</summary>
    public const string Default = AzureOpenAi;

    public static LlmProviderSelection Resolve(IConfiguration cfg)
    {
        var configured = Normalize(cfg["Llm:Provider"]);

        var (requested, isExplicit) = configured is { Length: > 0 }
            ? (configured, true)
            // 기존 설정 호환: UseBedrock 도 사람이 적어 둔 의사 표시다.
            : cfg.GetValue<bool>("UseBedrock")
                ? (Bedrock, true)
                : (Default, false);

        if (requested is not (Stub or Bedrock or Vertex or AzureOpenAi))
            throw new InvalidOperationException(
                $"Llm:Provider '{requested}'는 {Stub}|{Bedrock}|{Vertex}|{AzureOpenAi} 중 하나여야 한다");

        var missing = Missing(cfg, requested);
        if (missing is null)
            return new LlmProviderSelection(requested, requested, isExplicit, null);

        // 명시한 공급자의 설정이 틀린 것은 오타다 — 여기서 세운다.
        if (isExplicit) throw new InvalidOperationException(missing);

        return new LlmProviderSelection(Stub, requested, false,
            $"{requested} 설정이 없어 스텁으로 돈다 — {missing}");
    }

    /// <summary>
    /// 그 공급자로 뜰 수 있는지. 준비됐으면 null, 아니면 무엇이 없는지.
    /// <b>판정을 복제하지 않고</b> 실제 기동에 쓰는 <c>Validate()</c>를 그대로 부른다 —
    /// 규칙이 갈라지면 "확인은 통과했는데 기동은 실패"가 생긴다.
    /// </summary>
    public static string? Missing(IConfiguration cfg, string provider)
    {
        try
        {
            switch (provider)
            {
                case Vertex:
                    VertexOptions(cfg).Validate();
                    break;
                case AzureOpenAi:
                    AzureOptions(cfg).Validate();
                    break;
                // stub은 설정이 없고, bedrock 자격증명은 AWS 체인이 런타임에 푼다.
            }
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    public static VertexAiOptions VertexOptions(IConfiguration cfg) => new(
        cfg["Llm:Vertex:ProjectId"] ?? "",
        cfg["Llm:Vertex:Location"] ?? "",
        cfg["Llm:Vertex:Model"] ?? "");

    public static AzureOpenAiOptions AzureOptions(IConfiguration cfg) => new(
        cfg["Llm:AzureOpenAI:Endpoint"] ?? "",
        cfg["Llm:AzureOpenAI:Deployment"] ?? "",
        cfg["Llm:AzureOpenAI:ApiVersion"] ?? "",
        cfg["Llm:AzureOpenAI:ApiKey"],
        cfg["Llm:AzureOpenAI:BearerToken"]);

    /// <summary>표기 흔들림을 하나로 모은다 — <c>azure</c>, <c>vertex_ai</c> 도 받는다.</summary>
    private static string Normalize(string? raw) => (raw?.Trim().ToLowerInvariant() ?? "") switch
    {
        "azure" => AzureOpenAi,
        "vertex_ai" or "vertexai" => Vertex,
        var other => other,
    };
}
