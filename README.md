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
| ④ 검수 큐 &amp; 보정 (저신뢰 매핑 정정·승격) | H3b 해소 |
| ⑤ 스냅샷별 상세 (인벤토리·식별·마스킹) | H1 · H2 · H4 |
| ⑥ 채점 &amp; 리포트 (Markdown/JSON) | 종합 Go/No-Go |

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
| `deferredReuses` | 저신뢰 캐시 재사용(재추론 백오프로 아낀 호출) | — |
| `maskedRefs` | H4 마스킹 적용량 | — |

> **주의:** `StubAiMapper`의 필드 신뢰도는 0.6이라 기본 `Mapping:ThetaHigh=0.8`에서는 매핑이
> `pending_review`로 남아 **캐시가 절대 적중하지 않는다**(`cacheHitRatio`가 항상 0).
> 캐시 경로를 보려면 `--bedrock`(실 AI)을 쓰거나 `Mapping:ThetaHigh`를 낮춘다.

> **`cache_hit=false`와 "AI를 호출했다"는 다른 말이다.** 저신뢰 매핑은 자기 신뢰도로 적중 조건을
> 만족시킬 수 없지만(θ 절벽) 매 관측마다 다시 묻지도 않는다(재추론 백오프, `Mapping:ReinferAfterHours`
> 기본 24h). 관측 응답의 `source`가 셋을 구분한다 — `trusted_cache` / `deferred_cache` / `ai`.
> `aiCalls`는 `ai`만 센다. 백오프를 끄고 순수 호출량을 재려면 `--Mapping:ReinferAfterHours=0`.

가설별 측정 절차·채점 기준·함정은 **[PHASE0-MEASUREMENT.md](PHASE0-MEASUREMENT.md)** 에 있다.
스냅샷을 서버로 재생하면 Windows 없이도 H3b·H6를 반복 측정할 수 있다.

### 개인화 · 검수(HITL) API

2-Plane 지식 모델(D5, ARCHITECTURE §5.4)과 저신뢰 매핑 검수(§5.2 step 6).

```bash
U="X-ChoPilot-User: alice"

curl -H "$U" localhost:5080/v1/uep          # 내 화면 사용 빈도·최근성 + 화면 전이 그래프 (UEP)
curl        localhost:5080/v1/ontology      # 개념 목록(별칭 포함) — 게시된 지식의 컴파일 결과
curl        localhost:5080/v1/review        # 검수 큐 (pending_review, 개인 스코프 제외)
curl -H "$U" -X POST localhost:5080/v1/review/promote -d '{...}'  # 검수 통과 → trusted
curl -H "$U" -X POST localhost:5080/v1/correction     -d '{...}'  # 개인 보정
curl        localhost:5080/v1/decisions     # 승격·보정·지식 결정 이력 (누가 언제)
```

### 지식 수명주기 API (Curated Knowledge Plane)

온톨로지·가이드 규칙·업무객체 힌트는 코드가 아니라 **버전 관리되는 지식 문서**다
(ARCHITECTURE §5.4 Plane 3, §5.5). 개념 문서가 승인되면 **재배포 없이** 보정·가이드·AI
프롬프트에 반영되고, 지식 버전 변경은 저신뢰 매핑의 재추론 백오프를 즉시 만료시킨다.

```bash
curl        localhost:5080/v1/knowledge                    # 문서 목록 + 지식 버전
curl        localhost:5080/v1/knowledge/signals            # 집계 원자료(거부된 개념 시도)
curl -H "$U" -X POST localhost:5080/v1/knowledge -d '{...}'            # 초안 제출 → pending_review
curl -H "$U" -X POST "localhost:5080/v1/knowledge/aggregate?dryRun=true"  # 신호 → 초안 (미리보기)
curl -H "$U" -X POST localhost:5080/v1/knowledge/{id}/approve          # 게시 (버전 증가)
curl -H "$U" -X POST localhost:5080/v1/knowledge/{id}/deprecate        # 폐기 (해당 매핑 정리)
```

- **삭제는 없다** — 폐기만 있고, 폐기 문서는 이력으로 남는다.
- 민감 개념의 **민감 → 비민감 하향은 승인 단계에서 거부**된다(마스킹 2차 방어선).
- 개념 폐기 시 그 개념을 쓰는 매핑에서 필드를 제거하고, 남은 신뢰도가 θ 미만이면
  `pending_review`로 강등한다.

**신호 → 초안 → 승인 루프.** 사용자가 온톨로지에 없는 개념으로 정정을 시도하면 거부되지만
그 시도는 신호로 남는다. 집계기가 지지도·k인 게이트를 통과한 후보를 초안으로 만들고, 사람이
승인해야 온톨로지가 된다 — **자동 게시는 없다.**

```
3명이 "결제조건" 정정 시도 → 400 거부 (+ 신호 기록)
→ POST /v1/knowledge/aggregate → 초안 1건 (민감으로 제안)
→ approve(kim) → 온톨로지 v1(8개) → v2(9개)
→ 같은 보정 재시도 → 200 수락
```

집계는 **LLM 없이** 돈다(무엇이 몇 번, 몇 사람에게서 관측됐는지는 세면 된다). 선택적으로
`Knowledge:UseEditor=true`를 켜면 초안 1건당 1회 Bedrock이 **본문 서술만** 다듬는다 —
개념명·민감 여부·규칙은 집계기가 만든 것을 그대로 쓴다. 비용이 관측 수가 아니라 후보 수에
비례하는 이유가 여기 있다.

`GET /v1/knowledge?axis=user`는 **저장되지 않은** 개인 업무 프로파일을 요청 시 렌더한다 —
자주 쓰는 화면, 업무 흐름, 반복 거부한 제안. 본인만 조회할 수 있다(D5).

### 기반 정보 — 무료 API·MCP 출처와 대사

네 축 중 기반 축만 **계보가 반대**다. 거래처가 실재하는지, 그 날이 공휴일인지는 화면을
아무리 봐도 알 수 없다 — 외부 출처가 마스터를 주고 관측은 거기에 **대사**될 뿐이다.
관측된 값이 마스터로 올라가는 경로는 의도적으로 없다(ARCHITECTURE §5.6).

```bash
curl        localhost:5080/v1/foundation             # 마스터 요약 + 출처 상태·라이선스
curl -H "$U" -X POST localhost:5080/v1/foundation/refresh   # 출처 갱신 (밖으로 나가는 유일한 호출)
curl        localhost:5080/v1/foundation/reconcile   # 관측 ↔ 마스터 대사
```

출처는 무료 API와 MCP 서버다 — 사람이 손으로 심는 시드가 아니다. **기본은 전부 비활성**이라
켜지 않으면 테스트도 CI도 네트워크로 나가지 않는다.

```bash
Foundation__ExchangeRate__Enabled=true      # open.er-api.com — 무료, 키 불필요
Foundation__Holiday__ServiceKey=…           # 공공데이터포털 특일 정보 (무료, 키 필요)
Foundation__BusinessStatus__ServiceKey=…    # 국세청 사업자등록 상태조회 (무료, 키 필요)
Foundation__Mcp__0__Endpoint=https://…/mcp  # MCP 서버 (Streamable HTTP, JSON-RPC)
Foundation__Mcp__0__Tool=list_vendors
Foundation__Mcp__0__KeyArgument=names       # 주면 관측된 키만 물어본다 (조회형 출처)
```

대사 판정이 넷인 것이 핵심이다. 둘로 나누면 경보가 무의미해진다.

| 판정 | 뜻 |
|---|---|
| `matched` | 마스터에 있다 |
| `unmatched` | 마스터에 없다 — **이것만 경보다** |
| `unverifiable` | 키 공간이 달라 물어볼 수 없다 (마스터는 사업자번호, 관측은 상호명) |
| `no_master` | 이 종류의 출처가 아직 없다 |

마스터가 없는데 미등록으로 세면 관측 전량이 경보가 되고, 그러면 사람이 경보를 보지 않게 된다.
같은 이유로 조회 실패는 조용한 빈 목록이 아니라 오류로 남고, 실패한 갱신은 직전 사실을 지우지
않는다. MCP·공개 API 응답은 외부 데이터라 **마스터 조회 키와 개수로만** 쓰이고 개념 문서나
AI 프롬프트가 되지 않는다.

- **개인 보정은 `personal:<user>` 스코프에만 적재**되고 캐스케이드에서 1순위로 적중한다 →
  같은 화면 재방문 시 AI를 호출하지 않는다. 다른 사용자에게는 영향이 없다.
- 보정 개념은 **이름과 별칭 모두** 받는다(`단가` → `UnitPrice`). 온톨로지에 없는 개념은
  민감 여부를 알 수 없으므로 **400으로 거부**한다 — 통과시키면 `Sensitive=false`로 굳어져
  Business Object의 민감값 억제가 무력화된다.

> ⚠️ **`X-ChoPilot-User`는 인증이 아니다.** 헤더 값을 그대로 신뢰한다.
> 개인 스코프의 읽기·쓰기가 본문·쿼리가 아닌 **한 곳(`RequestUser`)만 통과**하도록 좁혀 둔
> 자리이며, 실제 인증(mTLS/OIDC)이 들어갈 seam이다. 그때까지 이 서버를 신뢰 경계 밖에
> 노출하면 안 된다(ARCHITECTURE §8: VPC 내부, mTLS).

### 다음 작업 제안 · 판단 수집 API

가이드는 화면 안의 빈칸(`type: guide`)뿐 아니라 **그 화면을 떠난 뒤의 다음 작업**(`type: next_screen`)도
제안한다. 후자는 UEP 화면 전이 그래프에서 나오며 2회 이상 관측된 경로만 쓴다.

```bash
curl "localhost:5080/v1/guide?observation_id=<id>"     # 제안 조회 (조회 시점에 '노출' 기록)
curl -H "$U" -X POST localhost:5080/v1/suggestions/feedback \
     -d '{"observationId":"<id>","suggestionId":"sg:…","outcome":"accepted"}'
curl        localhost:5080/v1/suggestions              # 수락률·응답률 집계
```

- 제안 ID는 **(업무객체, 종류, 대상)에서 결정적으로** 유도된다. 렌더마다 난수를 발급하면 세션·사용자를
  가로지르는 집계가 불가능해지고 수락률이 화면 새로고침 횟수의 함수가 된다.
- **무시는 보고하지 않는다** — 판단의 부재가 곧 무시다. `acceptanceRate`는 명시적 판단 중 수락 비율,
  `responseRate`는 노출 중 판단이 달린 비율이다. 둘을 섞으면 "제안이 틀렸다"와 "사용자가 바빴다"가
  한 숫자가 된다.
- 보여준 적 없는 제안에 대한 판단은 **404**. 분모 없는 분자는 KPI를 무의미하게 만든다.

### Bedrock 동적 매핑

`BedrockAiMapper`는 표준 AWS 자격증명 체인(환경변수/프로파일/IAM 역할)과 `InvokeModel`(Anthropic Messages)을 사용한다. 테넌트에서 가용한 모델 ID로 교체:

```csharp
var bedrock = new Amazon.BedrockRuntime.AmazonBedrockRuntimeClient();
var mapper  = new BedrockAiMapper(bedrock, "anthropic.claude-3-5-sonnet-20240620-v1:0");
var resolver = new MappingResolver(new InMemoryMappingCache(), mapper);
```
