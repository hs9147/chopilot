namespace ChoPilot.Core;

/// <summary>
/// Cho-Pilot 설정 (appsettings.json + 환경변수 CHOPILOT_* + appsettings.local.json).
/// 비밀정보(AWS 자격증명)는 여기에 두지 않는다 — 표준 AWS 자격증명 체인에서 해석.
/// </summary>
public sealed class ChoPilotConfig
{
    public AwsConfig Aws { get; set; } = new();
    public MappingConfig Mapping { get; set; } = new();
    public PrivacyConfig Privacy { get; set; } = new();
    public ObservationConfig Observation { get; set; } = new();
    public LlmConfig Llm { get; set; } = new();
    public ServerConfig Server { get; set; } = new();
    public ConsentConfig Consent { get; set; } = new();
}

/// <summary>사용자 동의·관측 범위 제어 (ARCHITECTURE §8, PHASE1-DESIGN Exit #4).</summary>
public sealed class ConsentConfig
{
    /// <summary>관측 전역 on/off. false면 어떤 화면도 관측·전송하지 않는다.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>제외할 앱/창 (타이틀·프로세스명 부분일치, 대소문자 무시). 예: 개인 뱅킹·메신저.</summary>
    public List<string> ExcludedApps { get; set; } = new();

    /// <summary>제외할 URL 부분일치 패턴(대소문자 무시). 예: 사내 급여·건강 포털.</summary>
    public List<string> ExcludedUrlPatterns { get; set; } = new();
}

public sealed class AwsConfig
{
    /// <summary>Bedrock 리전 (예: us-east-1, ap-northeast-2).</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// 모델 ID. 현재 Anthropic 모델은 ON_DEMAND 직접 호출이 안 되고
    /// <b>inference profile</b>(us./global. 접두사)만 지원한다.
    /// (검증된 값 예시; 테넌트에서 `aws bedrock list-inference-profiles`로 확인 후 교체)
    /// </summary>
    public string BedrockModelId { get; set; } = "us.anthropic.claude-haiku-4-5-20251001-v1:0";
}

public sealed class MappingConfig
{
    /// <summary>캐시 채택/승격 신뢰도 임계치 (ARCHITECTURE §5.2).</summary>
    public double ThetaHigh { get; set; } = 0.8;

    /// <summary>Shared Plane org 스코프 식별자 (D5).</summary>
    public string OrgId { get; set; } = "default";
}

public sealed class PrivacyConfig
{
    public string PolicyVersion { get; set; } = "1.0";
}

/// <summary>클라이언트 진단과 서버가 공통으로 쓰는 공급자별 LLM 설정. 비밀은 환경변수/보안 저장소에서 주입한다.</summary>
public sealed class LlmConfig
{
    public int TimeoutSeconds { get; set; } = 45;
    public VertexLlmConfig Vertex { get; set; } = new();
    public AzureOpenAiLlmConfig AzureOpenAI { get; set; } = new();
}

public sealed class VertexLlmConfig
{
    public string ProjectId { get; set; } = "";
    public string Location { get; set; } = "us-central1";
    public string Model { get; set; } = "";
}

public sealed class AzureOpenAiLlmConfig
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiVersion { get; set; } = "2024-10-21";
    public string ApiKey { get; set; } = "";
    public string BearerToken { get; set; } = "";
}

public sealed class ObservationConfig
{
    public int MaxDepth { get; set; } = 25;
    public int MaxNodes { get; set; } = 4000;
}

public sealed class ServerConfig
{
    /// <summary>Ingestion 엔드포인트 (Phase 1 업로드용, PoC 단계에선 미사용 가능).</summary>
    public string IngestionEndpoint { get; set; } = "";

    /// <summary>JWT/OIDC access token. 운영에서는 파일이 아니라 OS 보안 저장소/토큰 공급자에서 주입한다.</summary>
    public string BearerToken { get; set; } = "";

    /// <summary>개발 헤더 모드 또는 토큰 subject와의 계약 검증에 쓰는 사용자 ID.</summary>
    public string UserId { get; set; } = "";

    public string TenantId { get; set; } = "default";
}
