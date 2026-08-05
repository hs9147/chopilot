# Cho-Pilot — Phase 1 상세 설계 (Web Agent + Guide, 읽기 전용)

> 목표 : 자체 Procurement 웹앱에서 **"지금 무슨 업무를 하는지 이해하고 가이드"** 한다. **자동화(쓰기) 없음.**
> 전제 : [ARCHITECTURE.md](ARCHITECTURE.md) D1~D5 확정. Phase 0(위험검증) GO 이후 착수.
> 연계 : [PHASE0-PLAN.md](PHASE0-PLAN.md) · [PHASE0-KIT.md](PHASE0-KIT.md)

---

## 1. 범위

**In**
- Procurement 웹앱 **핵심 화면 5~10종** 관측 (읽기 전용)
- Adaptive Semantic Mapping (AI 동적매핑 + 자가학습 캐시 + 자가치유) 운영
- 화면/레코드 식별, Business Object 산출
- **Guide UI** : 현재 업무 요약 + 다음 작업 힌트 (제안만, 실행 안 함)
- Privacy Gate, Audit, 사용자 on/off

**Out (후속 Phase)**
- Mail/Doc 수집·융합(Phase 2~3), 자동화(Phase 4)
- 개인화 **본검증**(다중 사용자) — 단 스키마에 scope/user_id 선반영

---

## 2. 컴포넌트 구성

### 2.1 Client — Cho-Pilot Agent (.NET 8, VDI 내부)
| 모듈 | 책임 | 핵심 기술 |
|------|------|----------|
| `WebObserver` | 활성 브라우저 창의 **접근성 트리** 순회, 변경 감지(이벤트 기반) | UIAutomation / FlaUI, `AutomationFocusChanged`·`StructureChanged` 이벤트 |
| `ScreenIdentifier` | URL·타이틀·앵커로 화면ID·레코드ID 판정 | UIA `ValuePattern`(주소창) |
| `TreeSerializer` | 트리 → 정규화 스냅샷(roles/names/values/구조) | 값은 최소 수집 |
| `PrivacyGate` | 전송 전 PII·민감필드 마스킹/차단 | 규칙·정규식(경량) |
| `EventBuffer` | durable 스풀(영속 VDI), 재시도·오프라인 | 로컬 SQLite |
| `Uploader` | 서버 전송 | gRPC(mTLS) |
| `GuidePanel` | 서버 Guide 결과 표시(읽기 전용 오버레이/트레이) | WPF/WebView2 |

> **폴링 금지.** 포커스·구조 변경 이벤트로만 관측해 VDI 리소스·서버 부하 억제.

### 2.2 Server (AWS, Tenant VPC)
| 서비스 | 책임 |
|--------|------|
| `IngestionService` | 이벤트 수신·검증·정규화 |
| `SignatureService` | 화면 구조 서명 계산(route + 스켈레톤 해시) |
| `MappingResolver` | 캐시 조회(scope 캐스케이드) → 미스 시 AI 추론 요청 |
| `AIMappingWorker` | Bedrock 호출로 트리→개념 매핑 추론, 신뢰도 산출 |
| `AppAdapterRegistry` | 매핑 캐시(scope/user_id/신뢰도/검수상태) — DynamoDB |
| `BusinessObjectBuilder` | 매핑 적용 → Business Object 생성 |
| `GuideService` | 현재 업무 요약 + 다음작업 힌트(규칙+Bedrock) |
| `AuditService` | 관측·판단 이력(불변) |
| `KnowledgeStore` | Plane 3 지식 문서 저장·수명주기(제출→승인→게시→폐기) + 컴파일(온톨로지·Guide 규칙·업무객체 힌트). 온톨로지는 이제 코드가 아니라 여기서 나온다 |
| `AxisAggregator` | 4축 신호 집계 → 지식 초안 생성 (Phase 2 — 스키마만 선반영) |
| `KnowledgeEditor` | LLM이 집계를 문서 초안으로 서술 (Phase 2 — 일 배치) |
| `FoundationStore` | 무료 API·MCP 출처의 기준정보 사실을 하나의 마스터로 병합 (등록 순서 = 우선순위) |
| `FoundationReconciler` | 관측 엔티티 ↔ 마스터 대사. 미등록 / 대사 불가 / 마스터 없음을 구분 |

---

## 3. 데이터 흐름 (핵심 시퀀스)

```
[VDI Client]                                  [AWS Server]
 focus/structure 변경
   │
 WebObserver → TreeSerializer → PrivacyGate
   │  (정규화 스냅샷, 마스킹 완료)
   └── Uploader ──gRPC──▶ IngestionService
                              │ 검증·정규화
                              ▼
                          SignatureService  (구조 서명)
                              ▼
                          MappingResolver
                            ├ 캐시 HIT(고신뢰) ─────────────┐  [AI 미호출]
                            └ MISS/변경/저신뢰               │
                                 └▶ AIMappingWorker(Bedrock) │
                                       └▶ Registry 적재 ─────┤
                                                             ▼
                                              BusinessObjectBuilder
                                                             ▼
                                                    GuideService
                                                             │
   GuidePanel ◀──── 현재 업무 + 다음작업 힌트 ◀──────────────┘
   AuditService ◀── 전 과정 기록
```

---

## 4. API 계약

### 4.1 이벤트 수집 — `POST /v1/observations` (Client → Server)
```jsonc
{
  "event_id": "uuid",
  "session_id": "vdi-session-uuid",
  "user_id": "hashed-user-id",          // 개인화 스코프 키(D5)
  "captured_at": "ISO-8601",
  "browser": { "name": "edge", "version": "..." },
  "screen": {
    "url": "https://proc/pr/create?id=PR123",
    "title": "구매요청 등록",
    "record_hint": { "source": "url_query", "key": "id", "value": "PR123" }
  },
  "tree": {                              // 정규화 접근성 트리(마스킹 완료)
    "nodes": [
      { "ref": "n1", "role": "Edit", "name": "품목코드", "value": "M-001", "auto_id": "txtMat" },
      { "ref": "n2", "role": "Edit", "name": "단가", "value": "***MASKED***" }
    ]
  },
  "privacy": { "policy_version": "1.0", "masked_refs": ["n2"] }
}
```
응답:
```jsonc
{ "observation_id": "uuid", "signature": "sha256:...", "status": "accepted" }
```

### 4.2 매핑 해석 (내부: MappingResolver 로직)
```
resolve(signature, user_id):
  for scope in [personal:user_id, org:<org>, global]:      // 캐스케이드(D5)
     m = Registry.get(signature, scope)
     if m and m.confidence >= θ_high: return m
  m = AIMappingWorker.infer(tree, ontology, few_shot)       // 캐시 미스
  Registry.put(signature, scope="global"|"personal", m)     // 개인 보정은 personal
  return m
```

### 4.3 Guide 조회 — `GET /v1/guide?observation_id=` (Client ← Server)
```jsonc
{
  "current_task": { "business_object": "PurchaseRequest", "summary": "PR123 구매요청 작성 중" },
  "fields": { "Material": "M-001", "Quantity": null },   // 마스킹 필드는 제외
  "progress": { "filled": 3, "required": 5, "ratio": 0.6 },
  "next_hints": [
    { "id": "sg:9c4e…", "type": "guide",       "subject": "Quantity", "text": "수량 입력이 남았습니다", "actionable": false },
    { "id": "sg:a00a…", "type": "next_screen", "subject": "sha256:…", "text": "이 다음에는 보통 \"발주 목록\"(으)로 이동합니다 (4회, 약 35초 후)", "actionable": false }
  ],
  "confidence": 0.92,
  "provenance": "cache | ai"
}
```
> Phase 1의 `next_hints[].actionable`는 항상 `false`(가이드만). 자동화는 Phase 4.

- **`type: "guide"`** 는 지금 화면 안의 빈칸을 가리킨다. 화면 하나만 보면 여기까지다.
- **`type: "next_screen"`** 은 UEP 전이 그래프에서 나온다(§5) — 그 화면을 떠난 뒤의 **다음 작업**.
  2회 이상 관측된 경로만 제안한다(한 번 간 길은 흐름이 아니라 우연).
- **`id`** 는 (업무객체, 종류, 대상)에서 결정적으로 유도한 제안의 **정체성**이다. 렌더마다 새로 뽑지
  않는다 — 측정 대상이 "이 렌더가 클릭됐는가"가 아니라 "이 제안이 쓸모 있는가"이기 때문이다.
- 조회 시점에 **노출(impression)** 이 기록된다(수락률의 분모). 같은 관측을 재조회해도 분모는 늘지 않고
  이미 내려진 판단도 덮이지 않는다.

### 4.4 제안 판단 — `POST /v1/suggestions/feedback` (Client → Server)
```jsonc
// 헤더: X-ChoPilot-User: <userId>   ← 사용자는 본문에 없다
{ "observationId": "…", "suggestionId": "sg:a00a…", "outcome": "accepted | rejected" }
```
- **무시는 보고하지 않는다** — 판단의 부재가 곧 무시다(`pending`). 무시를 거부로 세면
  "제안이 틀렸다"와 "사용자가 바빴다"가 한 숫자에 섞인다.
- 보여준 적 없는 제안은 `404`. 분모 없는 분자는 KPI를 무의미하게 만든다.
- 집계는 `GET /v1/suggestions` — **수락률**(명시적 판단 중 수락)과 **응답률**(노출 중 판단)을 분리해 낸다.

### 4.5 지식 수명주기 — `/v1/knowledge` (ARCHITECTURE §5.5)

```
GET  /v1/knowledge                 # 문서 목록(axis·status 필터) + 현재 지식 버전. personal 스코프는 헤더 사용자만
GET  /v1/knowledge/{id}            # 문서 1건
GET  /v1/knowledge/signals         # 집계 원자료 — 거부된 보정 개념 시도(승인 판단의 근거)
POST /v1/knowledge                 # 초안 제출 → pending_review (헤더 사용자 = 작성자)
POST /v1/knowledge/aggregate       # 축별 신호 집계 → 초안 생성 (?dryRun=true 로 미리보기)
POST /v1/knowledge/{id}/approve    # 게시 (헤더 사용자 = 승인자, DecisionLog 기록, 버전 증가)
POST /v1/knowledge/{id}/deprecate  # 폐기 (개념이면 해당 매핑 필드 제거·강등, 버전 증가)
```

- **삭제는 없다.** 폐기만 있고, 폐기된 문서는 이력으로 남는다. 민감 개념도 폐기만 가능하다.
- 승인 시 검증: 민감 개념의 **민감 → 비민감 하향은 거부**된다(마스킹 2차 방어선 보호).
- 개념 **추가**는 기존 매핑에 영향이 없다. 개념 **폐기**는 그 개념을 쓰는 매핑에서 해당 필드를
  제거하고, 남은 필드의 신뢰도가 θ 미만이면 `pending_review`로 강등한다.
- `/v1/ontology`는 이제 하드코딩이 아니라 **게시된 지식의 컴파일 결과**를 서빙한다.

### 4.6 축별 집계 — 신호가 초안이 되는 지점

`POST /v1/knowledge/aggregate`는 4축 신호를 결정적으로 집계해 초안을 만든다. **LLM이 없다** —
무엇이 몇 번, 몇 사람에게서 관측됐는지는 세면 되고, AI는 본문 서술 품질에만 쓰인다(Phase 2).

| 축 | 신호 | 산출 초안 |
|---|---|---|
| domain | 보정에서 거부된 미지 개념 | `concept.{용어}` — **민감으로 제안**(모르면 닫는다), 승인자가 내린다 |
| domain | 여러 사용자의 공통 화면 전이 | `note.flow.{from}--{to}` — **route만** 싣는다 |
| domain | 여러 사용자가 반복 거부한 제안 | `note.rejected.{bo}.{concept}` — 규칙 재검토 |
| domain | **완료 시점의 개념별 채움률** | `rule.required.{bo}` **개정** — 시드 추측을 관측으로 교체 |
| item | 엔티티 공동 출현(품목↔거래처) | `note.item.{품목}` — 값이 실리는 축, 게이트가 곧 프라이버시 경계 |
| foundation | 무료 API·MCP 출처 등록 | `note.foundation.source.{출처}` — 관측이 없어 k인 게이트 없음 |
| foundation | 마스터에 없는 관측 키 | `note.foundation.unmatched.{종류}` — 승인해도 마스터가 되지 않는다 |
| user | UEP + 제안 판단 | `view.user.{id}` — **저장하지 않고 요청 시 렌더**(파생물) |

두 게이트가 항상 걸린다: **지지도**(`Knowledge:MinSupport`, 기본 3)와 **k인**(`Knowledge:MinDistinctUsers`,
기본 2). 걸러진 항목은 이유와 함께 `skipped`로 보고된다 — 침묵하는 절단은 "다 훑었다"로 읽힌다.

**LLM 편집자(선택).** `Knowledge:UseEditor=true`(+`UseBedrock=true`)이면 초안 1건당 1회
Bedrock을 호출해 **본문 서술만** 다듬는다. 반환값이 문자열 하나뿐인 것은 의도다 —
개념명·`Sensitive`·필수 필드는 결정적 집계기가 만든 것을 그대로 쓴다. LLM이 `Sensitive=false`를
쓸 수 있으면 문서 편집이 마스킹 방어선으로 가는 프롬프트 주입 경로가 된다. 호출이 실패하면
집계기 본문을 그대로 쓴다 — 서술 품질은 초안 생성의 전제가 아니다. 기본값은 AI 없음.

**폐기된 문서는 다시 제안하지 않는다.** 사람이 "아니다"라고 판단한 것을 집계기가 매번 되살리면
검수 큐가 무한 잔소리가 된다.

**필수 필드 개정만 예외다.** `rule.required.{bo}`는 시드로 이미 게시돼 있고 이 집계의 목적이
바로 그 추측을 교체하는 것이라, "이미 존재"로 건너뛰면 영원히 돌지 않는다. 대신 두 가지로
잔소리를 막는다 — 대기 중인 개정본이 있으면 쌓지 않고, 관측이 현재 규칙과 같으면 초안을
만들지 않는다(설계 근거는 [ARCHITECTURE.md](ARCHITECTURE.md) §5.7).

**사용자 축 뷰는 저장하지 않는다.** UEP·판단 스토어에서 재생성되는 파생물이라 저장하면 즉시 낡고
"누가 이 페이지의 진실을 소유하는가"가 무너진다. 편집·승인 대상이 아니며(`kind=view`) 컴파일에도
참여하지 않는다. 개인 뷰에는 화면 **제목**이 실리지만(본인만 조회, D5), org 초안에는 **route**만
실린다 — 제목에는 레코드 식별자가 섞일 수 있기 때문이다.

### 4.7 기반 정보 축 — 무료 API·MCP 출처와 대사

계보와 설계 근거는 [ARCHITECTURE.md](ARCHITECTURE.md) §5.6. 여기서는 운영 인터페이스만 적는다.

| 엔드포인트 | 하는 일 |
|---|---|
| `GET /v1/foundation` | 마스터 요약(종류별 건수) + 출처별 상태·라이선스·마지막 갱신·오류 |
| `POST /v1/foundation/refresh` | 모든 출처 갱신. **유일하게 밖으로 나가는 호출**이라 사용자 헤더를 요구하고 `DecisionLog`에 남긴다 |
| `GET /v1/foundation/reconcile` | 관측 ↔ 마스터 대사 결과 (matched / unmatched / unverifiable / no_master) |

**설정 (기본은 전부 비활성).** 키를 채우거나 `Enabled`를 켜야 출처가 등록된다 — 켜지 않으면
테스트도 CI도 네트워크로 나가지 않는다. 서비스 키는 소스에 커밋하지 말고 환경변수로 넣는다:

```
Foundation__ExchangeRate__Enabled=true          # 키 불필요
Foundation__Holiday__ServiceKey=…               # 공공데이터포털
Foundation__BusinessStatus__ServiceKey=…        # 국세청
Foundation__Mcp__0__Endpoint=https://…/mcp      # MCP 서버 (Streamable HTTP)
Foundation__Mcp__0__Tool=list_vendors
Foundation__Mcp__0__Kind=Company
Foundation__Mcp__0__KeyArgument=names           # 주면 조회형: 관측된 키만 물어본다
```

MCP 클라이언트는 `initialize` → `notifications/initialized` → `tools/call` 세 번의 JSON-RPC로
끝나는 최소 구현이다. 공식 SDK를 끌어오지 않은 것은 의도다 — 이 경로에 필요한 건 도구 호출
하나뿐이고, 서버 프로젝트에 프리뷰 의존성을 추가하면 CI가 패키지 복원에 묶인다. 도구 결과는
`structuredContent`가 있으면 그쪽을, 없으면 텍스트 콘텐츠의 JSON을 쓴다(배열 또는
`facts`/`items`/`data`/`results`). 해석할 수 없으면 **빈 목록이 아니라 오류**다.

**서비스 키는 노출되지 않는다.** `Origin`은 호스트까지만 적고(지식 문서 본문에 그대로 실린다),
오류 메시지의 응답 본문은 200자로 자른다.

---

## 5. 데이터 모델 (개인화 선반영)

- **매핑 캐시 엔트리**: [PHASE0-KIT.md](PHASE0-KIT.md) §3.1 구조 재사용 — `signature`·**`scope`**·**`user_id`**·`mapping[]`·`confidence`·`status`.
- **Business Object**: [ARCHITECTURE.md](ARCHITECTURE.md) §4.2.
- **지식 문서(KnowledgeDoc, Plane 3)**: `id`(예: `concept.UnitPrice`, `rule.required.PurchaseRequest`) ·
  `axis`(user|item|domain|foundation) · `kind`(view|curated) · `type`(concept|required_fields|business_hint|note) ·
  `scope`(D5 재사용) · 타입별 페이로드 · `body`(서술 — **관측 값 금지**) · `version` · `status`(pending_review|published|deprecated) ·
  `provenance`(근거 신호 · SupportCount · DistinctUsers) · `approvedBy`(curated 게시엔 필수).
  게시·폐기마다 **지식 버전**이 증가하고, `MappingEntry.OntologyVersion`과 대조되어 재추론 백오프를 만료시킨다(ARCHITECTURE §5.5).
- **UEP(Phase 1)**: `user_id` → { 화면 사용 빈도/최근성, **화면 전이 그래프**, 개인 보정 매핑 }.
  - **전이** `(from → to, count, medianGapSeconds, lastSeen)` — "무엇 다음에 무엇을 하는가". 빈도만으로는
    다음 작업을 말할 수 없어서 따로 쌓는다. 세 가지를 걸러낸다:
    **자기루프**(클라이언트가 한 화면을 여러 번 보낸 것은 이동이 아니다),
    **긴 공백**(기본 30분 초과는 업무 순서가 아니라 자리 비움),
    **평균**(세션 안의 딴짓 한 번이 흐름을 규정하지 못하게 중앙값을 쓴다. 표본은 엣지당 최근 64개).
  - **판단 기록** `(사용자, 관측, 제안) → 노출 · 수락/거부` — §4.4. 거부가 신호다. 수락만 모으면
    이미 맞은 제안만 확인하게 된다.
- 도출은 Phase 3에서 본격화하되, **다음 화면 제안**(§4.3 `next_screen`)은 Phase 1에서 이미 전이 그래프로 낸다.

---

## 6. Privacy Gate (Phase 1 확정)
- `sensitive:true` 개념(단가·금액 등) + 패턴 PII(주민번호=차단, 이메일/전화=마스킹).
- 마스킹은 **클라이언트에서** 수행 → 원문이 서버로 나가지 않음.
- 정책 버전은 서버에서 원격 갱신, 클라이언트는 버전 태깅.

---

## 7. 비기능 요구 (NFR)
| 항목 | 목표 |
|------|------|
| 관측→Guide 표시 p95 | ≤ 3s |
| 캐시 적중률(정상상태) | ≥ 95% (AI 호출 최소화) |
| 매핑당 AI 비용 | Phase 0 실측 예산 내 |
| VDI 리소스 | 세션 여유 내(CPU/메모리 상한 준수) |
| 보안 | mTLS, VPC 내부, SSE-KMS, 전 관측 Audit |
| 가용성 | 서버 장애 시 클라이언트 스풀 후 재전송 |
| 내구성 | `Storage:Path` 설정 시 서버 저장소가 저널로 남아 재시작에 살아남는다 (ARCHITECTURE §12) |

### 7.1 적재 계약과 영구 거부 — 스풀이 막히지 않게 하는 조건

스풀은 FIFO를 지키느라 **실패한 이벤트를 큐 머리에 남긴다**. 그래서 "영원히 실패할 이벤트"와
"나중에 성공할 이벤트"를 같이 취급하면 전자 하나가 그 뒤의 모든 관측을 영구히 막는다.
파일이 멀쩡하므로 손상 파일 격리에도 걸리지 않는다 — 조용한 실패 모드다.

양쪽에서 막는다:

- **서버**: `ObservationContract.Validate`를 통과하지 못한 이벤트는 **400**이다. 계약 위반은
  장애가 아니다. 500을 내면 클라이언트가 그것을 서버 장애로 읽는다. 검증은 트리 깊이
  (`MaxDepth` 64)와 노드 수(`MaxNodes` 20,000)도 본다 — 서명·마스킹·BO 생성이 전부 재귀라
  깊은 트리는 `StackOverflowException`으로 **프로세스를 죽인다**(잡을 수 없는 예외다).
  검증기 자신은 반복문으로 훑는다: 재귀로 검증하면 막으려던 깊이에서 검증기가 먼저 넘어간다.
- **클라이언트**: `SendOutcome`이 실패를 둘로 나눈다. `Retry`(오프라인·5xx·타임아웃·408·429)는
  스풀에 남고, `Rejected`(그 밖의 4xx)는 **격리**(`*.bad`)되고 배출은 계속된다.
  현재 이벤트가 거부되면 스풀에 넣지 않는다 — 넣으면 다음 실행에서 큐 머리를 막는다.

어느 한쪽만 고치면 다른 쪽이 여전히 막는다. 서버만 고치면 구버전 클라이언트가 400을 재시도로
보고, 클라이언트만 고치면 500이 계속 재시도로 분류된다.

---

## 8. 완료 기준 (Exit Criteria → Phase 2 진입)
1. 핵심 화면 5~10종에서 **관측 정확도 ≥90%, 캐시 적중률 ≥95%** 지속
2. 화면 변경 시 **자가치유** 정상 동작
3. Guide가 **현재 업무·진행률**을 정확히 표시(사람 평가 ≥80%)
4. Privacy Gate·Audit·on/off 동작 검증
5. 개인화 scope/user_id 데이터가 정상 축적(도출은 미검증 허용)

---

## 9. 작업 분해 (WBS, 마일스톤)
| M | 산출 |
|---|------|
| M1 | Client 관측 파이프라인(WebObserver→PrivacyGate→Uploader) + Ingestion 스텁 |
| M2 | SignatureService + AppAdapterRegistry + MappingResolver(캐시) |
| M3 | AIMappingWorker(Bedrock) + BusinessObjectBuilder, 자가치유 |
| M4 | GuideService + GuidePanel(읽기전용 UI) |
| M5 | Privacy/Audit/on-off, NFR 튜닝, Exit Criteria 측정 |

---

## 10. 리스크 (Phase 1 고유)
| 리스크 | 대응 |
|--------|------|
| 이벤트 폭주(빈번한 포커스 변경) | 디바운스·구조변경만 트리거, 서버 큐 |
| AI 매핑 지연이 Guide 체감 저하 | 캐시 우선, AI는 비동기+낙관적 표시 후 갱신 |
| Guide 오판이 사용자 신뢰 훼손 | 신뢰도 표시, 저신뢰 시 단정 대신 "확인 필요" 톤 |
| 개인 보정이 전역 캐시 오염 | 보정은 personal scope에만 기록, 승격은 게이트 통과 시 |
