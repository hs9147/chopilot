# Cho-Pilot

[![CI](https://github.com/hs9147/chopilot/actions/workflows/ci.yml/badge.svg)](https://github.com/hs9147/chopilot/actions/workflows/ci.yml)

**Enterprise Work Intelligence Platform** — Screen을 이해하는 AI를 넘어 Business를 이해하는 AI.

세 축(Web · Mail · Document)의 업무 신호를 통합해 현재 업무·목적·진행률·다음 작업을 실시간으로 이해한다.

## 문서

| 문서 | 내용 |
|------|------|
| [Proposal Draft.md](Proposal%20Draft.md) | 제안서 (v1.1) |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 상세 아키텍처 설계 (전제 D1~D5, 컴포넌트, 데이터모델, Adaptive Mapping, 개인화) |
| [PHASE0-PLAN.md](PHASE0-PLAN.md) | Phase 0 위험 검증 실행계획 |
| [PHASE0-KIT.md](PHASE0-KIT.md) | Phase 0 실행 키트 (템플릿·스키마·측정표) |
| [PHASE0-MEASUREMENT.md](PHASE0-MEASUREMENT.md) | **Phase 0 실측 가이드** (가설별 절차·명령·함정) |
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
> 빌드·테스트는 매 푸시마다 [CI](.github/workflows/ci.yml)가 Linux·Windows 양쪽에서 검증한다.

```
src/
  ChoPilot.Core/       모델, SignatureService, PrivacyGate, Ontology,
                       ScreenIdentifier(레코드 식별), ConsentPolicy(on/off·제외),
                       EventSpool(durable 재전송 스풀), Uep(개인 프로파일),
                       Uploader + ObservationDispatcher(전송·스풀 정책) (net8.0, 크로스플랫폼)
  ChoPilot.Mapping/    MappingCache, MappingResolver, StubAiMapper,
                       BedrockAiMapper, PromptBuilder,
                       BusinessObjectBuilder, GuideService             (net8.0)
  ChoPilot.Client/     UiaObserver + chopilot-dump CLI                 (net8.0-windows, FlaUI)
  ChoPilot.Server/     Ingestion + Guide + Audit + Metrics
                       + UEP/검수(HITL)/개인 보정 + DecisionLog
                       + wwwroot/ 측정 콘솔 (Phase 0 실측 UI)          (net8.0, ASP.NET)
tests/
  ChoPilot.Tests/      Signature/Privacy/Stub/Bedrock/Guide +
                       ScreenIdentifier/Consent/EventSpool/Resolver +
                       Uploader/Dispatcher(전송 유실 방지) +
                       UEP/검수/개인 보정 + 캐스케이드 격리 +
                       Server end-to-end + Metrics                     (net8.0, 크로스플랫폼)
```

### 빌드

솔루션 파일 `ChoPilot.sln`은 저장소에 포함되어 있다.

```bash
dotnet build         # net8.0 4개(Core/Mapping/Server/Tests) + net8.0-windows 클라이언트
dotnet test          # Windows/AWS 불요 — 순수 로직 + 서버 end-to-end 73개 검증
```
> 빌드 0 경고 / 0 오류, 테스트 73/73 통과. Windows 클라이언트 포함 전체 솔루션 빌드는
> [CI](.github/workflows/ci.yml)의 `build-windows` 잡에서 매 푸시마다 검증된다.

### Phase 0 관측 도구 실행 (Windows)

```bash
# 3초 대기 동안 관측할 Procurement 화면을 브라우저에서 포그라운드로 두면,
# 접근성 트리를 캡처 → 마스킹 → 서명 계산 → JSON 저장.
dotnet run --project src/ChoPilot.Client -- --delay 3 --out out/pr_create.snapshot.json --baseline
```

- `--baseline` : StubAiMapper(alias 매칭)로 매핑 시도 → AI 매핑 대비 성능 대조군 (PHASE0-KIT §3.3)
- 산출 JSON은 [PHASE0-KIT.md](PHASE0-KIT.md) §2(관측 인벤토리)·§3(매핑) 측정의 원자료

### 측정 콘솔

서버를 띄우고 **<http://localhost:5080/>** 를 열면 Phase 0 측정을 화면에서 진행할 수 있다.
스냅샷을 끌어다 놓으면 재생되고, 지표·서명 진단·요소 인벤토리·채점·리포트 내려받기까지 한 화면에서 끝난다.

```bash
ASPNETCORE_URLS=http://127.0.0.1:5080 \
dotnet run --project src/ChoPilot.Server -c Release -- --Mapping:ThetaHigh=0.5
```

| 콘솔 단계 | 다루는 가설 |
|-----------|------------|
| ① 스냅샷 적재 (드래그&드롭 재생) | — |
| ② 지표 (통과선 대비 PASS/FAIL) | H3b 적중률 · H6 지연·토큰 |
| ③ 서명 진단 (갈린 route 경고) | H3b 원인 |
| ④ 스냅샷별 상세 (인벤토리·식별·마스킹) | H1 · H2 · H4 |
| ⑤ 채점 &amp; 리포트 (Markdown/JSON) | 종합 Go/No-Go |

### 측정 API (자동화용)

서버는 감사 로그에서 지표를 직접 산출한다. PHASE0-KIT의 측정표를 손으로 채우는 대신 이 값을 쓴다.

```bash
curl localhost:5080/v1/metrics       # 적중률·지연·토큰 집계
curl localhost:5080/v1/signatures    # route별 서명 그룹핑 (갈림 진단)
curl localhost:5080/v1/observations  # 스냅샷 목록 + 인벤토리 집계
```

| 필드 | 대응 가설 | 통과선 |
|------|----------|--------|
| `cacheHitRatio` | H3b 자가학습 캐시 적중률 | ≥ 0.95 |
| `distinctSignatures` | 같은 화면이 여러 서명으로 갈라졌는지 진단 | 화면 수와 일치 |
| `latencyP95Ms` | H6 관측→Guide p95 | ≤ 3000 |
| `aiCalls` / `inputTokens` / `outputTokens` | H6 매핑당 AI 비용 | 예산 내 |
| `maskedRefs` | H4 마스킹 적용량 | — |

> **주의:** `StubAiMapper`의 필드 신뢰도는 0.6이라 기본 `Mapping:ThetaHigh=0.8`에서는 매핑이
> `pending_review`로 남아 **캐시가 절대 적중하지 않는다**(`cacheHitRatio`가 항상 0).
> 캐시 경로를 보려면 `--bedrock`(실 AI)을 쓰거나 `Mapping:ThetaHigh`를 낮춘다.

가설별 측정 절차·채점 기준·함정은 **[PHASE0-MEASUREMENT.md](PHASE0-MEASUREMENT.md)** 에 있다.
스냅샷을 서버로 재생하면 Windows 없이도 H3b·H6를 반복 측정할 수 있다.

### 개인화 · 검수(HITL) API

2-Plane 지식 모델(D5, ARCHITECTURE §5.4)과 저신뢰 매핑 검수(§5.2 step 6).
Phase 1은 **축적과 보정까지** — 개인화 도출(다음작업·자동화)은 Phase 3.

```bash
U="X-ChoPilot-User: alice"

curl -H "$U" localhost:5080/v1/uep          # 내 화면 사용 빈도/최근성 (UEP)
curl        localhost:5080/v1/review        # 검수 큐 (pending_review, 개인 스코프 제외)
curl -H "$U" -X POST localhost:5080/v1/review/promote -d '{...}'  # 검수 통과 → trusted
curl -H "$U" -X POST localhost:5080/v1/correction     -d '{...}'  # 개인 보정
curl        localhost:5080/v1/decisions     # 승격·보정 결정 이력 (누가 언제)
```

- **개인 보정은 `personal:<user>` 스코프에만 적재**되고 캐스케이드에서 1순위로 적중한다 →
  같은 화면 재방문 시 AI를 호출하지 않는다. 다른 사용자에게는 영향이 없다.
- 보정 개념은 **이름과 별칭 모두** 받는다(`단가` → `UnitPrice`). 온톨로지에 없는 개념은
  민감 여부를 알 수 없으므로 **400으로 거부**한다 — 통과시키면 `Sensitive=false`로 굳어져
  Business Object의 민감값 억제가 무력화된다.

> ⚠️ **`X-ChoPilot-User`는 인증이 아니다.** 헤더 값을 그대로 신뢰한다.
> 개인 스코프의 읽기·쓰기가 본문·쿼리가 아닌 **한 곳(`RequestUser`)만 통과**하도록 좁혀 둔
> 자리이며, 실제 인증(mTLS/OIDC)이 들어갈 seam이다. 그때까지 이 서버를 신뢰 경계 밖에
> 노출하면 안 된다(ARCHITECTURE §8: VPC 내부, mTLS).

### Bedrock 동적 매핑

`BedrockAiMapper`는 표준 AWS 자격증명 체인(환경변수/프로파일/IAM 역할)과 `InvokeModel`(Anthropic Messages)을 사용한다. 테넌트에서 가용한 모델 ID로 교체:

```csharp
var bedrock = new Amazon.BedrockRuntime.AmazonBedrockRuntimeClient();
var mapper  = new BedrockAiMapper(bedrock, "anthropic.claude-3-5-sonnet-20240620-v1:0");
var resolver = new MappingResolver(new InMemoryMappingCache(), mapper);
```
