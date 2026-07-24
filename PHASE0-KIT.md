# Cho-Pilot — Phase 0 실행 키트 (템플릿 & 스키마)

> [PHASE0-PLAN.md](PHASE0-PLAN.md)의 산출물을 즉시 채울 수 있는 양식·스키마 모음.
> 팀이 Day 1부터 이 문서에 값을 채우며 진행한다.

---

## 1. 대상 화면 선정 워크시트 (WS1 — 도메인 전문가와 작성)

핵심 업무 흐름을 대표하는 화면 3~5종을 고른다. "가장 자주·가장 오래 쓰는 화면 + Mail/Doc 연결이 잦은 화면" 우선.

| # | 화면명 | 화면ID/URL 패턴 | 대표 업무 | 핵심 필드(업무개념) | Mail/Doc 연결성 | 우선순위 |
|---|--------|----------------|----------|--------------------|----------------|---------|
| 1 | 구매요청 등록 | `/pr/create` | PurchaseRequest 생성 | 품목, 수량, 납기, 거래처 | 견적 메일/견적서 | ★★★ |
| 2 | 발주 조회 | `/po/view?id=` | PurchaseOrder 확인 | 발주번호, 거래처, 금액 | 발주 확인 메일 | ★★★ |
| 3 | 입고 확인 | `/gr/...` | GoodsReceipt | 입고번호, 수량, 검수 | 납품 통보 | ★★ |
| 4 | | | | | | |
| 5 | | | | | | |

---

## 2. UIA 관측 인벤토리 & 획득률 리포트 (WS2 — H1/H2)

### 2.1 요소 인벤토리 (화면별)
| 요소 | UIA ControlType | Name(라벨) 획득 | Value 획득 | AutomationId 유무 | 비고(가상화/iframe 등) |
|------|-----------------|----------------|-----------|-------------------|----------------------|
| 품목코드 입력 | Edit | O | O | 있음 | |
| 수량 | Edit | O | O | 없음(위치기반) | |
| 발주 테이블 | DataGrid/Table | O | △(가상화) | | 스크롤 동기 필요 |

### 2.2 획득률 집계 (통과선 ≥ 90%)
| 화면 | 대상 필드 수 | Name 획득 | Value 획득 | **획득률** | 판정 |
|------|-----------|----------|-----------|-----------|------|
| 구매요청 등록 | 12 | 12 | 11 | 92% | PASS |
| 발주 조회 | | | | | |

### 2.3 화면/레코드 식별 (H2, 통과선 ≥ 95%)
| 화면 | 식별 신호 | 레코드ID 위치 | 판정 정확률 |
|------|----------|--------------|-----------|
| 발주 조회 | URL `?id=PO123` | URL 쿼리 | 100% |
| 구매요청 등록 | URL + 화면 타이틀 | (신규는 ID 없음) | |

---

## 3. Adaptive Semantic Mapping (WS3 — H3/H3b, D4)

정적 규칙을 손으로 짜지 않는다. **화면 서명 → 캐시 조회 → (미스 시) AI 동적 매핑 → 캐시 적재**. 아래는 그 자료구조·측정 양식.

### 3.0 Core Ontology (시드, 고정)
```jsonc
{ "concepts": [
  {"name":"Material","type":"string","aliases":["품목","품목코드","자재"]},
  {"name":"Quantity","type":"number","aliases":["수량","발주수량"]},
  {"name":"UnitPrice","type":"number","aliases":["단가"],"sensitive":true},
  {"name":"Vendor","type":"string","aliases":["거래처","협력사"],"entity_ref":"Company"},
  {"name":"DeliveryDate","type":"date","aliases":["납기","납품일"]}
]}
```

### 3.1 화면 서명 & 캐시 엔트리 (Layer B, App Adapter Registry)
```jsonc
{
  "signature": "sha256(route:/pr/create | skeleton:Form>[Edit,Edit,Combo,Table...])",
  "scope": "global",              // global | org:<id> | personal:<user_id>  (D5 선반영)
  "user_id": null,                // personal 스코프일 때만
  "business_object": "PurchaseRequest",
  "record_id": { "source": "url_query", "key": "id" },
  "mapping": [
    { "element": {"role":"Edit","label":"품목코드","auto_id":"txtMaterialCode"},
      "concept": "Material", "confidence": 0.98, "provenance": "ai" },
    { "element": {"role":"Edit","label":"단가"},
      "concept": "UnitPrice", "confidence": 0.91, "provenance": "ai", "sensitive": true }
  ],
  "status": "trusted | pending_review",
  "model": "bedrock:claude-...", "created_at": "..."
}
```

### 3.2 AI 매핑 추론 요청/응답 (캐시 미스 시 Bedrock)
```jsonc
// 요청: 접근성 트리 + Core Ontology + few-shot 시드(화면 명세 문서 발췌)
// 응답:
{ "business_object": "PurchaseRequest",
  "fields": [ {"element_ref":"n12","concept":"Material","confidence":0.98}, ... ],
  "unmapped": ["n7"], "notes": "..." }
```

### 3.3 AI 매핑 정확률 측정 (H3, 통과선 ≥ 90%)
| 업무객체 | 필드 수 | AI 정확 매핑 | 정확률 | 오매핑 원인 |
|---------|--------|-------------|-------|-----------|
| PurchaseRequest | 12 | 11 | 92% | 단가/공급가 라벨 유사 |

### 3.4 캐시·자가치유 측정 (H3b, 통과선 ≥ 95%)
| 항목 | 시행 | 성공 | 비율 |
|------|------|------|------|
| 캐시 적중(동일 화면 재방문, AI 미호출) | | | |
| 화면 변경 감지 → 자동 재추론 성공 | | | |
| 매핑당 AI 토큰/비용·지연(H6) | | | |

---

## 4. Privacy Gate 규칙 템플릿 (WS4 — H4)

전송 경계에서 강제. `sensitive:true` 필드 + 패턴 기반 PII를 마스킹.

```jsonc
{
  "policy_version": "0.1",
  "field_rules": [
    { "concept": "UnitPrice", "action": "mask" },       // 도메인 민감
    { "concept": "TotalAmount", "action": "mask" }
  ],
  "pattern_rules": [
    { "name": "email", "regex": "[\\w.]+@[\\w.]+", "action": "mask" },
    { "name": "rrn", "regex": "\\d{6}-\\d{7}", "action": "block" },   // 주민번호: 차단
    { "name": "phone", "regex": "01[016789]-?\\d{3,4}-?\\d{4}", "action": "mask" }
  ],
  "default": "allow_whitelisted_only"                    // 화이트리스트 필드만 승격
}
```

### 4.1 마스킹 재현율 측정 (통과선 ≥ 99%)
| 규칙 | 테스트 케이스 | 탐지 | 재현율 | 오탐 |
|------|-------------|------|-------|------|
| 주민번호 차단 | 50 | 50 | 100% | 0 |
| 단가 마스킹 | | | | |

---

## 5. Entity Resolver 결정론 규칙 (WS5 — H5)

Procurement 레코드 ↔ Mail/Doc 연결. 상위에서 확정되면 종료.

| 순위 | 규칙 | 신호 | 예시 |
|------|------|------|------|
| 1 | 문서번호 일치 | 발주번호/PR번호 | 메일 본문 "PO123" ↔ 발주 레코드 PO123 |
| 2 | 거래처코드 일치 | Vendor code | 화면 거래처 00231 ↔ 메일 발신 도메인/서명 매핑 |
| 3 | 첨부-메일 | attachment id | 견적서.pdf ↔ 견적 요청 메일 |
| 4 | 시간창 | ±30분 관측 | 발주 화면 열기 직전 읽은 메일 가중치 |
| 5 | (폴백) 의미 유사도 | 임베딩 Top-k → LLM 판정 | 위 무매칭 시 소수만 |

### 5.1 연결 정밀도 측정 (통과선 Precision ≥ 0.9)
| 규칙 | 연결 시도 | 정탐 | 오탐 | Precision |
|------|----------|------|------|-----------|
| 문서번호 일치 | | | | |

---

## 6. Go/No-Go 리포트 템플릿 (WS6)

```
■ Phase 0 결과 요약
  H1 관측 정확도    : ___%  (기준 90%)  [PASS/FAIL]
  H2 화면/레코드 식별: ___%  (기준 95%)  [PASS/FAIL]
  H3 매핑 정확률    : ___%  (기준 90%)  [PASS/FAIL]
  H4 마스킹 재현율  : ___%  (기준 99%)  [PASS/FAIL]
  H5 연결 정밀도    : ___   (기준 0.9)  [PASS/FAIL]
  H6 지연/리소스    : ___   (기준 3s)   [PASS/FAIL]

■ 판정 : [GO / 조건부 GO / NO-GO]
■ 미달 항목 원인 및 완화안 :
■ Phase 1 권고 (범위·일정·자원) :
■ 재검토 필요 리스크 :
```

### 판정 규칙 (PHASE0-PLAN §8)
- **GO** : H1·H2·H3 통과 → Phase 1 착수
- **조건부 GO** : H1 통과 + H3 일부 미달 → 매핑 보강 후 착수
- **NO-GO** : H1 미달 → Tier 2 비중·비용 재산정, 접근방식 재검토
