# Cho-Pilot

**Enterprise Work Intelligence Platform** — Screen을 이해하는 AI를 넘어 Business를 이해하는 AI.

세 축(Web · Mail · Document)의 업무 신호를 통합해 현재 업무·목적·진행률·다음 작업을 실시간으로 이해한다.

## 문서

| 문서 | 내용 |
|------|------|
| [Proposal Draft.md](Proposal%20Draft.md) | 제안서 (v1.1) |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 상세 아키텍처 설계 (전제 D1~D5, 컴포넌트, 데이터모델, Adaptive Mapping, 개인화) |
| [PHASE0-PLAN.md](PHASE0-PLAN.md) | Phase 0 위험 검증 실행계획 |
| [PHASE0-KIT.md](PHASE0-KIT.md) | Phase 0 실행 키트 (템플릿·스키마·측정표) |
| [PHASE1-DESIGN.md](PHASE1-DESIGN.md) | Phase 1 상세 설계 (Web Agent + Guide, 읽기 전용) |
| [SETUP.md](SETUP.md) | 개발환경 설정 (요구사항·빌드·설정·AWS 자격증명·실행·트러블슈팅) |

## 확정 설계 전제

- **AWS** (Amazon Bedrock, 테넌트 VPC 내부) · **영속 VDI 내부 데스크톱 에이전트**
- 1차 타깃 = **자체 Procurement 웹앱** (소스 접근 불가 → 사용자 관점 UIA 관측)
- **Adaptive Semantic Mapping** : AI 동적 매핑 + 자가학습 캐시 + 화면변경 자가치유
- **2-Plane 지식 모델** : 구조 지식 공유 + 사용자별 로직 격리·도출

## 코드 (Phase 0 실측 스캐폴딩)

> **요구:** Windows + .NET 8 SDK. (UIA는 Windows 전용 → 클라이언트는 `net8.0-windows`)
> 상세 환경설정·AWS 자격증명·트러블슈팅은 **[SETUP.md](SETUP.md)** 참조.
> 이 저장소는 현재 문서 환경에 dotnet이 없어 빌드 검증되지 않았다. 개발 PC에서 아래로 빌드한다.

```
src/
  ChoPilot.Core/       모델, SignatureService, PrivacyGate, Ontology   (net8.0, 크로스플랫폼)
  ChoPilot.Mapping/    MappingCache, MappingResolver, StubAiMapper,
                       BedrockAiMapper, PromptBuilder                  (net8.0)
  ChoPilot.Client/     UiaObserver + chopilot-dump CLI                 (net8.0-windows, FlaUI)
tests/
  ChoPilot.Tests/      Signature/Privacy/Stub/BedrockParse 단위테스트   (net8.0, 크로스플랫폼)
```

### 빌드

```bash
# 솔루션 구성 (최초 1회)
dotnet new sln -n ChoPilot
dotnet sln add src/ChoPilot.Core src/ChoPilot.Mapping src/ChoPilot.Client tests/ChoPilot.Tests

dotnet build
dotnet test          # Windows/AWS 불요 — 순수 로직 검증
```

### Phase 0 관측 도구 실행 (Windows)

```bash
# 3초 대기 동안 관측할 Procurement 화면을 브라우저에서 포그라운드로 두면,
# 접근성 트리를 캡처 → 마스킹 → 서명 계산 → JSON 저장.
dotnet run --project src/ChoPilot.Client -- --delay 3 --out out/pr_create.snapshot.json --baseline
```

- `--baseline` : StubAiMapper(alias 매칭)로 매핑 시도 → AI 매핑 대비 성능 대조군 (PHASE0-KIT §3.3)
- 산출 JSON은 [PHASE0-KIT.md](PHASE0-KIT.md) §2(관측 인벤토리)·§3(매핑) 측정의 원자료

### Bedrock 동적 매핑

`BedrockAiMapper`는 표준 AWS 자격증명 체인(환경변수/프로파일/IAM 역할)과 `InvokeModel`(Anthropic Messages)을 사용한다. 테넌트에서 가용한 모델 ID로 교체:

```csharp
var bedrock = new Amazon.BedrockRuntime.AmazonBedrockRuntimeClient();
var mapper  = new BedrockAiMapper(bedrock, "anthropic.claude-3-5-sonnet-20240620-v1:0");
var resolver = new MappingResolver(new InMemoryMappingCache(), mapper);
```
