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
    public ServerConfig Server { get; set; } = new();
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

public sealed class ObservationConfig
{
    public int MaxDepth { get; set; } = 25;
    public int MaxNodes { get; set; } = 4000;
}

public sealed class ServerConfig
{
    /// <summary>Ingestion 엔드포인트 (Phase 1 업로드용, PoC 단계에선 미사용 가능).</summary>
    public string IngestionEndpoint { get; set; } = "";
}
