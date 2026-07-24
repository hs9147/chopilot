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
    { "type": "guide", "text": "수량·납기 입력이 남았습니다", "actionable": false }
  ],
  "confidence": 0.92,
  "provenance": "cache | ai"
}
```
> Phase 1의 `next_hints[].actionable`는 항상 `false`(가이드만). 자동화는 Phase 4.

---

## 5. 데이터 모델 (개인화 선반영)

- **매핑 캐시 엔트리**: [PHASE0-KIT.md](PHASE0-KIT.md) §3.1 구조 재사용 — `signature`·**`scope`**·**`user_id`**·`mapping[]`·`confidence`·`status`.
- **Business Object**: [ARCHITECTURE.md](ARCHITECTURE.md) §4.2.
- **UEP(Phase 1 최소)**: `user_id` → { 화면 사용 빈도/최근성, 개인 보정 매핑 }. 도출 로직은 Phase 3에서 본격화, Phase 1은 **축적만**.

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
