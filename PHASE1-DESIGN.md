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

---

## 5. 데이터 모델 (개인화 선반영)

- **매핑 캐시 엔트리**: [PHASE0-KIT.md](PHASE0-KIT.md) §3.1 구조 재사용 — `signature`·**`scope`**·**`user_id`**·`mapping[]`·`confidence`·`status`.
- **Business Object**: [ARCHITECTURE.md](ARCHITECTURE.md) §4.2.
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
