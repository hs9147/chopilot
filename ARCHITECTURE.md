# Cho-Pilot — Architecture Design

> Enterprise Work Intelligence Platform
> **Screen을 이해하는 AI를 넘어 Business를 이해하는 AI**

Version : 1.3 (Architecture)
Status : Design + Phase 1 baseline implemented
연계 문서 : [Proposal Draft.md](Proposal%20Draft.md) v1.3

---

## 0. 설계 확정 전제 (Design Decisions)

| # | 결정 | 내용 | 파급 |
|---|------|------|------|
| D1 | **LLM/추론 플랫폼** | **AWS 기반**. LLM = Amazon Bedrock(Claude), 임베딩 = Bedrock Titan/Cohere. 모든 데이터는 **테넌트 VPC 내부**에서만 이동 | 외부 전송 없음. Bedrock 무학습 정책 → 거버넌스 근거 확보 |
| D2 | **1차 타깃 환경** | **영속(persistent) VDI 세션 내부에서 동작하는 데스크톱 앱**. 사용자별 계정·프로파일 유지 | 세션 내부 실행 → **UIA 트리 정상 접근**(픽셀-only 문제 해소). 계정 유지 → **로컬 상태 신뢰 가능**. 단 GPU는 여전히 부재 |
| D3 | **1차 타깃 애플리케이션** | **자체 개발 General Procurement 시스템**. 단 **소스 접근 불가 → 사용자 관점(외부) 관측만 허용** | 협조형(Tier 0) 불가. **Tier 1(UIA)** 로 관측. 사내 시스템이므로 **화면/필드 문서·테스트 환경·도메인 전문가** 확보는 가능(시드·검증용) |
| D4 | **동적 환경 · AI 적응형 로직** | 테스트/운영 환경이 **동적으로 구성**되어 화면·필드가 고정적이지 않음. 따라서 매핑을 하드코딩하지 않고 **런타임에 내부 AI(Bedrock) 호출로 화면을 해석·적응** | 정적 규칙 우선 → **Adaptive Semantic Mapping(§5)**: AI가 접근성 트리를 해석해 매핑을 생성하고 **자가학습 캐시**에 적재, 화면 변경 시 **자동 재추론(self-healing)**. 대신 비용·지연·정확도 관리가 핵심(캐시·서명·신뢰도) |
| D5 | **사용자별 환경·로직 축적 및 도출** | **사용자마다 환경(앱·화면·데이터·습관)과 로직을 축적**하고, 그 환경에 **맞는 로직을 도출**(개인화) | **2-Plane 지식 모델(§5.4)**: ① 구조 지식(화면→개념 매핑)은 전역/조직 공유(효율·콜드스타트) ② 개인 로직(워크플로 습관·엔티티 별칭·보정·선호)은 **사용자별 격리**. 도출은 **개인 > 조직 > 전역 캐스케이드** + 피드백 학습. 개인정보 격리(멀티테넌시) 필수 |
| D6 | **변경 인지 관측·델타 기록** | UI 상태를 주기마다 전량 저장하지 않고 **변경 이벤트를 합쳐 의미 있는 변화만 기록**. 첫 관측·화면/구조 변경은 체크포인트, 동일 화면의 값·상태 변경은 델타 | VDI/UIA 부하·전송량·보관량을 줄이면서 시간 순서 복원 가능. 안정 노드 키, Privacy Gate 이후 비교, 기준 누락 시 전체 재동기화, 이벤트 멱등성이 필수(§3.4·§4.1) |
| D7 | **사용자 검측·피드백 반영** | 운영 측정 콘솔과 최종 사용자 검측 UI를 분리한다. 사용자는 시스템이 이해한 결과와 불확실한 항목만 확인하고 `맞음·수정·나중에·개인정보 신고`로 응답한다 | 개인 보정은 즉시 개인 스코프에 적용하고, 조직 공유는 검수 후 승격한다. 모든 판단은 대상 관측·필드·버전·사유·적용 상태·되돌리기를 남긴다(§3.5·§4.4) |

### 0.1 D2가 만드는 제약과 대응
1. **GPU 부재** → 로컬에서 OCR·임베딩·비전 모델 실행 비현실적. → **무거운 추론은 전량 서버(AWS GPU)로.** 클라이언트는 UIA 파싱 + 경량 규칙 기반 PII 필터만.
2. **영속 세션(계정 유지)** → 로컬 디스크 상태 **신뢰 가능**. → 로컬에 **durable 이벤트 스풀**(오프라인·재시도 내성), 사용자별 경량 캐시(최근 컨텍스트·매핑 규칙 사본) 유지 가능. 단 원본(source of truth)은 서버.
3. **배포/업데이트** → 영속 VDI는 표준 소프트웨어 배포(SCCM/Intune 등)로 클라이언트 설치·갱신 가능(골든 이미지 리빌드 불요). 그래도 **클라이언트는 얇게(observe-only)** 유지 — 매핑·로직은 서버에서 원격 갱신해 업데이트 빈도 최소화.

---

## 1. 설계 원칙

1. **Client = 관측·정규화·필터만.** 판단(추론)은 서버. 클라이언트에 비즈니스 로직을 넣지 않는다(배포 제약 D2-3).
2. **Privacy Gate를 전송 경계에 강제.** 어떤 데이터도 이 관문을 거치지 않고 나가지 못한다.
3. **관측(Read)과 실행(Actuation/Write)을 분리.** 안전성·감사(Audit) 용이.
4. **추론은 계층적(Tiered).** 규칙 → 임베딩/소형 → LLM 순으로 escalate. LLM은 모호한 소수 케이스의 판정자로만.
5. **연결(Correlation)은 결정론 우선.** LLM에 3축 연결을 통째로 맡기지 않는다.
6. **변화만 기록하되 복원 가능해야 한다.** 변경 없음은 버리고, 델타는 명시적 체크포인트와 순서 위에서만 적용한다. 최적화 때문에 감사 가능성을 잃지 않는다.
7. **사용자 피드백은 명령이 아니라 검증된 결정이다.** 인증 주체·소유권·대상 버전·적용 범위를 확인하고, 개인 적용과 조직 지식 승격을 분리한다.

---

## 2. 배포 토폴로지 (AWS + VDI)

```
┌─────────────── VDI 세션 (사용자, AWS WorkSpaces/AppStream 또는 사내 VDI) ────────────┐
│                                                                                       │
│   관측 대상 앱: SAP GUI, PLM Client, Outlook, Office, 파일 서버(마운트)               │
│        ▲                                                                              │
│        │ UI Automation / COM / FileSystemWatcher                                      │
│   ┌────┴───────────────── Cho-Pilot Agent (.NET 8, 초경량) ──────────────────┐        │
│   │  Observation Adapters → Privacy Gate → Change Detector/Delta Encoder           │    │
│   │                                      → Event Buffer(durable) → gRPC/HTTPS        │    │
│   └───────────────────────────────┬───────────────────────────────────────────┘      │
└───────────────────────────────────┼───────────────────────────────────────────────── ┘
                                     │  VPC 내부 통신 (TLS, PrivateLink)
                                     ▼
┌──────────────────────────── AWS (Tenant VPC, Private Subnet) ─────────────────────────┐
│                                                                                       │
│  [API Gateway / ALB]  ─→  [Ingestion Service (ECS Fargate)]                           │
│                              ├ Server-side OCR / Embedding (GPU 노드, 필요 시)         │
│                              └ Semantic Engine ─ App Adapter Registry(DynamoDB)        │
│                                       │                                                │
│                              [Entity Resolver] ──┐                                     │
│                                                  ▼                                     │
│   [Aurora PostgreSQL + pgvector]   ← Knowledge Graph + Vector Store                    │
│   [S3]                              ← 원본 문서/첨부 (SSE-KMS 암호화)                   │
│   [OpenSearch (선택)]               ← 대규모 시 Vector/Full-text 분리                   │
│                                                                                       │
│  [User Environment Profile + Personalization]  ← 사용자별 축적·도출(§5.4, 격리)       │
│  [Business Understanding Service (ECS)]                                               │
│  [Workflow Engine (Step Functions + ECS)]                                             │
│  [LLM Gateway (ECS)] ──→ Amazon Bedrock (Claude / Titan Embeddings)                    │
│                                                                                       │
│  [관측성] CloudWatch / OpenTelemetry   [보안] KMS, IAM, VPC Endpoint, CloudTrail(Audit)│
└─────────────────────────────────────────────────────────────────────────────────────┘
```

- VDI가 AWS(WorkSpaces/AppStream)라면 **에이전트↔서버 트래픽이 전부 VPC 내부** → 지연·거버넌스 모두 유리.
- 사내 VDI라면 **Direct Connect/VPN + VPC Endpoint(PrivateLink)** 로 Bedrock까지 사설 경로 유지(인터넷 미경유).

---

## 3. 컴포넌트 상세

### 3.1 Client — Cho-Pilot Agent (.NET 8)
| 컴포넌트 | 책임 | 기술 |
|---------|------|------|
| Web Adapter | **브라우저 접근성 트리(UIA)** 순회와 변경 이벤트 구독 → 정규화 UI 이벤트. 주소창 전용 식별자로 얻은 URL만 경로 단위로 정규화하고 query·fragment는 기본 제거 | `System.Windows.Automation` / FlaUI (Chrome·Edge 접근성 트리, Extension 불요) |
| Change Listener | `FocusChanged`·`StructureChanged`·`PropertyChanged`·`Invoke` 수신, 디바운스·최대 대기·노이즈 제거 | UIA 이벤트 + 앱별 폴링 안전망 |
| Mail Adapter | 메일 이벤트 | **Graph API 우선**, Outlook COM fallback |
| Doc Adapter | 파일 변경 감지, 메타데이터 | `FileSystemWatcher` |
| **Local Privacy Gate** | 전송 전 `value`뿐 아니라 `name·title·url·record hint`를 포함한 전체 문자열과 메타데이터의 PII 탐지·마스킹·차단 | 정규식/사전 기반 + 송신 직전 잔여 민감정보 검사(경량, GPU 불요) |
| State Cache / Delta Encoder | Privacy Gate를 통과한 직전 상태 유지, 안정 노드 키 기반 체크포인트·델타 생성 | 메모리 + 로컬 SQLite 체크포인트 |
| Event Buffer | 정규화 이벤트 큐, 오프라인·재시도 | 인메모리 + **durable 로컬 스풀**(영속 VDI, 재부팅 내성) |
| Local Cache | 최근 컨텍스트·매핑 규칙 사본(읽기용) | 로컬 SQLite. 원본은 서버 |
| Uploader | 서버 전송 | gRPC(우선)/HTTPS, mTLS |

> **원칙:** OCR·임베딩·비전은 클라이언트에서 하지 않는다(GPU 부재). 스크린샷이 필요한 경우(Canvas/이미지)만 캡처해 Privacy Gate 통과 후 서버 OCR로 전송.

### 3.2 Server
| 서비스 | 책임 |
|--------|------|
| Ingestion | 이벤트 수신·검증·정규화, `event_id` 멱등 처리, 서버측 OCR/임베딩 오케스트레이션 |
| Observation State Store | 체크포인트+델타 순서 검증·원자 적용·현재 상태 재구성, 기준 누락 시 재동기화 요청 |
| Semantic Engine | UI 이벤트 → **Business Object** 변환 (**Adaptive Semantic Mapping**, §5) |
| App Adapter Registry | **자가학습 매핑 캐시(Shared Structural Plane)** — 화면 서명별 AI 도출 매핑, 신뢰도·검수상태, 스코프(Global/Org), 화면 변경 시 자동 무효화 |
| **User Environment Profile (UEP)** | **사용자별 환경·로직 축적(Personal Plane, §5.4)** — 화면 사용 빈도, 개인 엔티티 별칭, 워크플로 습관, 선호·보정 이력. 사용자별 격리 |
| **Personalization / Derivation** | 개인 ▷ 조직 ▷ 전역 캐스케이드로 환경 맞춤 로직 도출 + 피드백 학습 |
| **Review Task / Feedback** | 저신뢰·표본 검측 과제 생성, 필드 단위 피드백 검증, 개인 즉시 적용, 조직 검수 큐·되돌리기·적용 영수증 |
| **Entity Resolver** | 3축 연결(§6) |
| Knowledge Graph | 업무 엔티티·관계 저장(§4) |
| Vector Store | 메일/문서 청크 임베딩, Semantic Search |
| Business Understanding | 3축 융합 → 현재업무/목적/진행률/다음작업 |
| Workflow Engine | Guide→Validation→승인→Automation→Audit(§7) |
| LLM Gateway | Bedrock 라우팅, 토큰/비용 관리, 프롬프트 캐시, 모델 추상화. **모델 ID는 inference profile(us./global.) 사용** — 현재 Anthropic 모델은 ON_DEMAND 직접 호출 미지원(PoC 실측) |
| **User Review UI** | 최종 사용자에게 자신의 검측 과제와 반영 결과만 제공. 내부 서명·임계치·캐시 상태는 숨기고 원문 라벨↔해석 개념·근거·신뢰도만 설명 |
| **Operations Console** | 관측·비용·지식·저장소·감사 상태를 운영자에게 제공. 최종 사용자 UI와 경로·권한을 분리 |

### 3.3 Observation Tiers — 협조 수준별 관측 (D3 반영)

관측 대상을 **소스 통제 가능성**에 따라 계층화한다. 상위 계층일수록 고정확·저비용.

| Tier | 방식 | 정확도/비용 | 적용 |
|------|------|-----------|------|
| Tier 0 — Cooperative | 소스 통제 앱 계측(안정 ID / Context API) | 최고 / 최저 | (현재 미적용 — 소스 접근 불가, 향후 협조 가능 시) |
| **Tier 1 — UIA Structured** ★ | 접근성 트리 파싱 (native/web 공통) + **Adaptive Semantic Mapping**(AI 동적 매핑, §5) | 중 (첫 해석은 AI, 이후 캐시로 안정) | **자체 Procurement (1차)**, 상용 ERP |
| Tier 2 — Vision/OCR | 픽셀 폴백 (Canvas·차트·커스텀 컨트롤) | 하 | 잔여·검증 용도 |

> **전략(D3·D4 확정):** 1차 타깃(자체 Procurement)은 소스 접근 불가 → **Tier 1(UIA)** 로 관측하되, 환경이 동적이므로 매핑은 하드코딩하지 않고 **런타임 AI 해석 + 자가학습 캐시**로 적응한다(§5). 관측 정확도(§9 KPI ≥90%)와 **AI 매핑 정확도·캐시 적중률**이 Phase 0의 최우선 검증 대상.
>
> **완충 요소(시드·검증용):** 사내 시스템이므로 **화면·필드 명세 문서·테스트 환경·도메인 전문가**를 확보해 AI 매핑의 few-shot 시드와 검수 기준으로 활용한다. (단, 화면이 동적이므로 문서는 초기 시드일 뿐 최종 규칙이 아님)

### 3.4 Change-aware Observation Pipeline (D6)

기본 캡처 단위는 픽셀 이미지가 아니라 **정규화된 접근성 트리 상태**다. 현재 Phase 1 구현은
일정 간격으로 전체 트리를 읽은 뒤 값 포함 지문을 직전 상태와 비교해 변경 없음을 전송하지 않는다.
목표 구조는 UIA 이벤트로 캡처 시점을 좁히고, 동일 화면 안의 변화는 델타만 보내는 것이다.

```
UIA event ─▶ debounce/coalesce ─▶ affected tree capture ─▶ Privacy Gate
                                                        │
                                                        ▼
                                              stable node-key diff
                                                        │
                          ┌─────────────────────────────┼─────────────────────────┐
                          ▼                             ▼                         ▼
                    no semantic change            value/state delta       route/structure change
                         drop                   append delta event         new checkpoint
```

**트리거와 합치기 정책.** UIA는 한 번의 사용자 입력에도 여러 이벤트를 낸다. 일반 값·구조 이벤트는
마지막 이벤트 후 기본 750ms에 한 번 처리하고, 입력이 계속돼도 최대 3초마다 중간 상태를 한 번만
남긴다. `Invoke(save|submit|approve)`와 화면 전환은 즉시 처리한다. 이벤트 유실·앱별 UIA 품질 편차를
복구하기 위해 30~60초 간격의 저빈도 전체 확인을 병행하며, 앱별 실측으로 값을 조정한다.

**의미 없는 변화.** 포커스·커서·선택·스크롤 위치·로딩 애니메이션·현재 시각·반복 live region은
업무 의미가 없으면 저장하지 않는다. 매핑된 업무 필드, 검증 오류, 버튼 동작, 업무 상태 변화는
우선 보존한다. 필터 규칙의 변경은 관측 정책 버전으로 남겨 측정 기준이 중간에 바뀌었음을 알 수 있게 한다.

**안정 노드 키.** 현재 캡처 순서에서 파생된 `n1`, `n2`는 앞 노드 하나가 추가되면 뒤 키가 전부
밀리므로 델타 키로 쓰지 않는다. 다음 캐스케이드를 사용한다.

1. 앱이 제공하는 안정 `AutomationId`
2. `parentKey + Role + AutomationId`
3. `parentKey + Role + normalized Name`
4. 마지막 수단으로 정규화된 구조 경로

반복 테이블의 행 번호처럼 데이터에 따라 바뀌는 숫자는 키에서 정규화한다. 업무 레코드 키가
관측됐을 때만 행 식별자로 승격하며, 충돌이 감지되면 부분 델타 대신 전체 체크포인트로 폴백한다.

**체크포인트 생성 조건.** 세션 첫 관측, 앱 재접속, route/레코드 전환, 구조 서명 변경,
Privacy/관측 정책 버전 변경, 기준 이후 델타 50건 또는 10분 경과, 서버의 재동기화 요청에서
전체 체크포인트를 생성한다. 임계치는 초기값이며 §9 KPI로 조정한다.

**실패와 순서.** 로컬 스풀은 체크포인트와 종속 델타를 FIFO로 전송한다. 서버는 `(session_id,
sequence)`의 연속성과 `base_snapshot_id`를 확인해 상태 적용과 이벤트 기록을 원자적으로 수행한다.
같은 `event_id` 재전송은 성공 응답을 재사용하고 파생 통계를 다시 누적하지 않는다. 기준이 없거나
순서가 비면 `resync_required`를 반환하고, 클라이언트는 같은 델타를 무한 재시도하지 않고 새
체크포인트를 보낸다.

### 3.5 User Review & Feedback Pipeline (D7)

검측은 전체 화면을 다시 묻는 절차가 아니라 **틀릴 가능성이 큰 필드만 짧게 확인하는 작업함**이다.
저신뢰 항목을 우선 배치하고, 고신뢰 결과도 1~5%를 무작위 표본으로 넣어 시스템이 놓친 오류를
측정한다. 같은 사용자에게 반복 질문하지 않도록 일 단위 예산과 묶음 검측을 적용한다.

```
Observation/Guide ─▶ Review Task ─▶ User Review UI ─▶ Feedback Service
                          │                                  │
                    low confidence / sample                  ├─ personal ─▶ 즉시 보정 + undo
                                                             └─ org/global ─▶ reviewer queue
                                                                                 │
                                                    Mapping/Knowledge revision ◀─┘
                                                                 │
                                                    적용 영수증 + Audit/Decision
```

사용자에게는 `시스템이 이해한 항목`, 원래 화면 라벨, 해석된 업무 개념, 불확실한 이유만 보여 준다.
기본 동작은 **“나머지는 맞아요”**, **“틀린 항목 고치기”**, **“나중에”** 세 가지이며, 자유문보다
구조화된 사유 코드를 먼저 받는다. 개인정보로 보이는 값은 별도 `privacy_report`로 즉시 차단·조사한다.

Feedback Service는 인증 주체에서 tenant/user를 정하고 본문의 사용자를 신뢰하지 않는다. 대상
관측의 소유권, `element_key` 존재 여부, 기대 매핑·지식 버전, 스코프 권한, 멱등 키를 검증한다.
개인 수정은 현재 사용자에게 즉시 적용하되 조직·전역 지식은 검수자의 승인 없이는 게시하지 않는다.
버전 충돌은 마지막 쓰기로 덮지 않고 최신 해석과 함께 `409 conflict`를 반환한다.

---

## 4. 데이터 모델

### 4.1 정규화 이벤트 (Client → Server 계약)
```jsonc
{
  "event_id": "uuid",
  "session_id": "vdi-session-uuid",
  "device_id": "managed-device-id",
  "source": "web | mail | doc",
  "captured_at": "ISO-8601",
  "trigger": "focus_changed | structure_changed | property_changed | save_clicked | null",
  "event_kind": "checkpoint | delta | fact",  // web 상태는 앞의 둘, mail/doc 단건은 fact
  "sequence": 42,
  "base_snapshot_id": "uuid | null",
  "screen_signature": "sha256:...",       // route + 구조, 매핑 캐시 키
  "content_fingerprint": "sha256:...",    // Privacy Gate 이후 의미 있는 값
  "app": { "name": "SAP GUI", "window_title": "...", "screen_id": "ME51N" },
  "payload": { /* checkpoint.tree 또는 delta.changes */ },
  "privacy": { "masked_fields": ["..."], "policy_version": "1.3" }
}
```

`tenant_id`와 `user_id`는 이벤트 본문에서 받지 않고 인증 토큰의 검증된 claim으로 서버가 부여한다.
클라이언트가 보내는 `device_id`도 신원 자체가 아니라 등록된 장치와 세션을 추적하는 보조 키다.

Web 관측의 `checkpoint`는 전체 정규화 트리를, `delta`는 기준 체크포인트 이후의 변경 연산만 담는다.

```jsonc
{
  "event_kind": "delta",
  "base_snapshot_id": "snap-100",
  "sequence": 43,
  "payload": {
    "changes": [
      { "op": "replace", "node_key": "form/item/quantity", "property": "value", "value": "20" },
      { "op": "replace", "node_key": "form/vendor", "property": "value", "value": "***MASKED***" }
    ]
  }
}
```

초기 연산은 `add | remove | replace`만 지원한다. `old_value`는 저장하지 않는다 — 기준 상태에 이미
있고 민감 원문을 중복 보유할 이유가 없다. 델타 비교·지문·직렬화는 모두 **Privacy Gate 이후**에
수행한다. `block` 필드는 값 없이 `present/empty`만, `mask` 필드는 마스킹 토큰과 채움 여부만 남긴다.

서버가 의미 상태를 재구성하는 규칙은 결정적이어야 한다. `checkpoint + sequence 순 델타`를 접고,
중복 `event_id`는 무시하며, 기준 누락·순서 공백·노드 키 충돌 시 추측 적용하지 않고 전체
재동기화를 요구한다. 이렇게 재구성한 현재 상태가 Semantic Engine과 Guide의 입력이다.

### 4.2 Business Object (Semantic Engine 산출)
```jsonc
{
  "bo_id": "uuid",
  "type": "PurchaseRequest | Approval | BOMQuery | ...",
  "fields": { "Material": "M-00231", "Quantity": 100, "DeliveryDate": "2026-08-01" },
  "confidence": 0.0,          // 매핑 신뢰도
  "provenance": "rule | model | llm",
  "source_event_id": "uuid"
}
```

### 4.3 Knowledge Graph 스키마
노드: `Project · Task · Mail · Document · ERPRecord · Person · Company · Schedule`
엣지(예):
```
Project ─has─ Task
Task ─triggered_by─ Mail
Task ─references─ Document
Task ─writes─ ERPRecord
Mail ─from/to─ Person
Company ─owns─ (Mail | Document | ERPRecord)
ERPRecord ─about─ Company        // "협력사코드 00231" ↔ "A사"
KnowledgeDoc ─about─ (Concept | Screen | Company | ...)   // §5.4 Plane 3 문서의 대상 연결
```
> **초기 저장소:** Aurora PostgreSQL(관계형 + 재귀 CTE)로 시작. 그래프 순회 부하가 커지면 Neptune/Neo4j로 이관. **그래프 "모델"이 자산이지 DB 엔진이 아니다.**

### 4.4 사용자 피드백 계약

기존의 화면 서명 전체 교체 요청 대신, 한 관측의 한 필드를 대상으로 하는 버전 조건부 명령을 쓴다.

```jsonc
{
  "feedback_id": "uuid",                 // 멱등 키
  "observation_id": "uuid",
  "target": { "type": "mapping", "element_key": "form/item/quantity" },
  "decision": "accept | correct | defer | privacy_report",
  "reason_code": "wrong_concept | missing_field | wrong_context | privacy",
  "proposed": { "concept": "Quantity" },
  "requested_scope": "personal | org",
  "expected": { "mapping_revision": 3, "knowledge_version": 12 }
}
```

응답은 `applied_personal | pending_org_review | conflict | rejected` 상태, 실제 적용 버전과 시각,
`review_id`, `undo_until`을 반환한다. 제출·검수·승격·되돌리기는 하나의 `feedback_id` 계보로 연결하고,
같은 요청을 재전송해도 보정·통계를 한 번만 반영한다.

---

## 5. Adaptive Semantic Mapping — 동적 환경 대응 (R2·D4 대응)

환경이 동적으로 구성되어 화면·필드가 고정적이지 않다. 따라서 매핑을 **하드코딩하지 않고**, 런타임에 **내부 AI(Bedrock)가 접근성 트리를 해석**해 매핑을 생성하고, 이를 **자가학습 캐시**로 재사용한다. 화면이 바뀌면 서명이 달라져 **자동 재추론(self-healing)** 된다.

### 5.1 3층 구조 (역할 재정의)
```
Layer A. Curated Knowledge : 온톨로지·업무 규칙 — 버전 관리되는 큐레이션 지식.
                              관측 신호에서 초안이 생성되고 사람이 승인하면 게시되며(§5.5),
                              게시본이 컴파일되어 Layer C의 프롬프트와 Guide 규칙이 된다.
                              "고정"이 아니라 "느리게, 승인을 거쳐 변한다".
Layer B. Mapping Cache     : 화면 서명(signature)별 AI 도출 매핑 + 신뢰도 + 검수상태
                              → App Adapter Registry(DynamoDB). 규칙이 아니라 "학습된 캐시"
Layer C. AI Inference      : 캐시 미스/신뢰도 미달/화면 변경 시 Bedrock이 트리→개념 매핑 추론.
                              Layer A의 버전이 바뀌면 저신뢰 매핑의 재추론 백오프가 즉시 만료된다.
```

### 5.2 런타임 파이프라인
```
1. Observe   : 활성 화면의 접근성 트리(roles/names/values/구조) 수집
2. Signature : 구조 서명 계산 = 화면 route + 정규화된 요소 스켈레톤 해시
3. Cache 조회 (Layer B, signature 키):
     ├ HIT(고신뢰)        → 캐시 매핑 적용 → Business Object   [AI 호출 없음]
     └ MISS / 서명변경 / 저신뢰 → 4
4. AI Infer  : Bedrock에 (접근성 트리 + Core Ontology + few-shot 시드) 전송
                → {UI요소 → 개념} 매핑 + 필드별 신뢰도 반환
5. Validate  : 스키마·타입 검증, 필수 개념 충족 확인
6. Cache 기록: signature 아래 매핑 저장(provenance=ai, confidence). 저신뢰는 검수 큐로
7. Emit      : Business Object 산출
```

### 5.3 비용·정확도 관리 (R5)
- **정상 상태에서는 대부분 캐시 HIT → AI 미호출.** AI는 최초 조우·화면 변경 시에만.
- Bedrock **프롬프트 캐시**로 Ontology/시스템 프롬프트 토큰 절감.
- 서명은 **값이 아니라 구조**로 계산 → 같은 화면의 다른 레코드는 재추론 안 함.
- **신뢰도 임계치** 미달 매핑은 도메인 전문가 검수 후 "trusted" 승격(HITL, 선택).
- 화면 명세 문서 = AI few-shot 시드 + 검수 기준(정적 규칙 아님).

> **핵심 전환:** 구조 지식(Layer B)은 "사람이 규칙을 작성" → "**AI가 규칙을 생성하고 시스템이 캐싱·자가치유**".
> 의미 지식(Layer A)은 "사람이 코드에 지식을 작성" → "**시스템이 관측에서 초안을 만들고 사람이 승인**"(§5.5).
> 어느 쪽도 지식이 배포 주기에 묶이지 않는다 — 이것이 "지식 체계를 모방하여 코딩하는 것이 아니라
> 지식 체계를 형성하고 유지보수하는 시스템"의 구현이다.

### 5.4 3-Plane 지식 모델 — 사용자별 환경·로직 축적·도출 (D5)

사용자마다 환경(쓰는 앱·화면·데이터·업무 습관)이 다르다. 지식을 **공유 가능한 것**과 **개인 고유한 것**,
그리고 **사람이 승인한 것**으로 분리해, 효율(공유)·개인화(격리)·신뢰(큐레이션)를 동시에 달성한다.

```
Plane 1. Shared Structural Plane  (전역/조직 공유 — 기계 유지)
   - 화면 서명 → 개념 매핑 (Adaptive Mapping 캐시)
   - 근거: 화면 "구조"는 사용자 사적 정보가 아님 → 공유가 콜드스타트·효율에 유리
   - 스코프: Global(전사) · Org(부서/역할)

Plane 2. Personal Adaptive Plane  (사용자별 격리 — 기계 유지) ★
   - User Environment Profile(UEP): 자주 쓰는 화면·빈도/최근성, 개인 엔티티 별칭
     (예: 이 사용자의 거래처 약어), 워크플로 습관(화면 전이 패턴), 선호·보정 이력,
     제안 수락/거부 판단 기록
   - Personal Work Graph: 이 사용자의 Project/Task/Mail/Doc (본래 개인 격리)

Plane 3. Curated Knowledge Plane  (사람 승인 지식 — 4축) ★신설
   - 축: 사용자(personal 파생 뷰) · 업무/품목(org) · 도메인/구매(org) · 기반 정보(global, 외부 출처)
   - Plane 1·2가 "관측이 쌓이는 곳"이라면 Plane 3은 "관측이 지식으로 응결되는 곳"(§5.5)
   - 게시본은 컴파일되어 온톨로지(Layer A)·Guide 규칙·AI 프롬프트의 원천이 된다
   - 문서 종류: view(스토어에서 자동 재생성 — 편집·승인 불가) / curated(사람 승인 필수)
   - 불변식: 문서에는 **레코드가 그대로** 들어가지 않는다. 값이 실리는 경우는 여러 사람에게서
     반복 관측되어 패턴으로 승격된 엔티티뿐이다(품목 축) — 한 번의 관측은 레코드이고,
     반복된 공동 출현이라야 지식이다. 그래서 승격 게이트가 곧 프라이버시 경계다.
     민감 개념의 값은 클라이언트 마스킹으로 서버에 도달하지 않으므로 구조적으로 실릴 수 없다.
```

**로직 도출 (Derivation Cascade)** — 현재 환경(활성 화면 + 사용자 맥락)에 맞는 로직을 계층 병합으로 도출:
```
DerivedLogic = resolve( Personal ▷ Org ▷ Global )      // 개인 > 조직 > 전역, 신뢰도 가중
   - 매핑     : 개인 보정이 있으면 우선, 없으면 조직/전역 캐시
   - 워크플로 : 개인 습관 패턴에서 "다음 작업" 예측
   - 자동화   : 개인이 반복하는 시퀀스를 자동화 후보로 승격
```

**피드백 학습 루프:** 사용자가 제안(매핑/다음작업/자동화)을 수락·수정·거부한 신호를 UEP에 축적 → 개인 레이어가 점점 그 사용자의 방식에 수렴. 공통적으로 반복되면 Org/Global로 승격 검토.

**승격 사다리 (Plane 2 → 3):** 개인 패턴이 조직 지식이 되는 유일한 경로.
```
Plane 2 개인 패턴 ──(k인 이상 반복 관측 + 사적정보 제거)──▶ Plane 3 org 초안
                 ──(사람 승인)──▶ 게시 ──(컴파일)──▶ 온톨로지·규칙·프롬프트
```
단일 사용자에게서만 관측된 패턴은 org로 오르지 못한다 — 그 사람의 활동이 유출되기 때문이다(아래 승격 게이트와 동일 원칙).

**콜드→웜 스타트:** 신규 사용자는 Global/Org 지식으로 즉시 동작(콜드스타트), 사용할수록 Personal 레이어가 정밀화(웜스타트).

**격리·거버넌스:** Plane 2는 **사용자별 데이터 격리**(§8 멀티테넌시). Plane 1로의 승격은 **사적 정보가 제거된 구조 지식만** 허용(승격 게이트).

### 5.5 지식 형성 루프 — 쓰기 경로 (§5.2와 대칭)

§5.2가 관측당 실행되는 **읽기 경로**라면, 이 루프는 배치로 실행되는 **쓰기 경로**다.
AI 호출은 3단계에만 있고 비용은 관측 수가 아니라 **엔티티 수에 비례**한다 — 읽기 경로의
호출 구조(캐시·백오프)는 이 루프의 존재와 무관하게 유지된다.

```
1. Signal    : 검수 큐(저신뢰 매핑) · 보정에서 거부된 미지 개념 · 반복 거부된 제안 ·
               교차 사용자 전이 (배치 수집)
2. Aggregate : 축별 결정적 집계 — LLM 없음. 지지도(minCount)·k인(DistinctUsers) 임계 적용
3. Draft     : LLM 편집자가 집계를 문서 초안으로 서술 (일 배치)
4. Review    : 초안 → pending_review → 사람 승인 (기존 HITL 재사용, DecisionLog 기록)
5. Publish   : 게시 + 지식 버전 증가
6. Compile   : 게시본 → 온톨로지(Layer A)·Guide 규칙·AI 프롬프트 재생성
7. Invalidate: 버전 변경 → 저신뢰 매핑의 재추론 백오프 즉시 만료(선택적 재추론).
               개념 "추가"는 기존 매핑을 무효화하지 않는다 — 의미 변경·폐기만 해당 매핑을
               pending_review로 강등한다. 민감 개념은 삭제 불가, 폐기(deprecate)만 가능하다.
```

> 사람이 개입하는 지점은 4(승인)뿐이지만, 그 지점이 전부다 — **LLM 자동 게시는 없다.**
> 신뢰도 1.0으로 캐스케이드 상위를 차지하는 지식은 반드시 사람 서명을 거친다는
> 개인 보정의 원칙이 조직 지식에도 그대로 적용된다.

### 5.6 기반 정보 축 — 계보가 반대인 축

앞의 세 축(사용자·품목·도메인)은 관측을 집계해 지식을 **만든다**. 기반 축은 만들 수 없다.
거래처가 실재하는지, 그 날이 공휴일인지, 그 통화가 무엇인지는 우리 화면을 아무리 많이 봐도
알 수 없다. 관측이 할 수 있는 일은 **대사(reconciliation)**뿐이다.

```
        외부 출처 ──▶ 마스터 ──▶ (대사) ──▶ 관측
        ▲                                    │
        └──────────── 이 방향은 없다 ────────┘
```

관측된 거래처 목록을 마스터로 승격하는 경로는 **의도적으로 없다.** 있으면 화면의 오타가
기준정보가 되고, 그 뒤로는 어떤 대사도 그 오타를 통과시킨다.

**출처는 무료 API와 MCP 서버다** — 사람이 손으로 심는 시드가 아니다.

| 출처 | 종류 | 비용 | 등록 |
|------|------|------|------|
| 내장 표준 코드 (ISO 4217 통화 · 계량 단위) | Currency, UnitOfMeasure | — | 항상 |
| open.er-api.com 기준환율 | ExchangeRate | 무료·**키 불필요** | `Foundation:ExchangeRate:Enabled` |
| 공공데이터포털 특일 정보 (한국천문연구원) | Holiday | 무료·키 필요 | `Foundation:Holiday:ServiceKey` |
| 국세청 사업자등록 상태조회 (odcloud) | Company | 무료·키 필요 | `Foundation:BusinessStatus:ServiceKey` |
| 임의의 MCP 서버 도구 | 설정으로 지정 | 서버 제공자 조건 | `Foundation:Mcp[]` |

병합 우선순위는 **등록 순서**다(뒤가 앞을 덮는다). 조회 완료 순서로 정하면 같은 입력에
마스터가 실행마다 달라진다 — 기준정보가 비결정적이면 대사도 비결정적이다.

**조회형 출처.** 전체 사업자 명부를 주는 무료 API는 없다. 그래서 조회는 관측된 키를 질의로
받는다(`FoundationQuery`): 목록형 출처(공휴일·환율)는 무시하고, 조회형 출처(국세청·키 인자를
설정한 MCP 도구)는 "우리가 본 것"만 물어본다.

**판정이 넷인 이유.** 대사 결과를 일치/불일치 둘로 나누면 경보가 무의미해진다.

```
matched      : 마스터에 있다
unmatched    : 마스터에 없다 ← 이것만 경보다
unverifiable : 키 공간이 달라 물어볼 수 없다 (마스터는 사업자번호, 관측은 상호명)
no_master    : 이 종류의 출처가 아직 없다
```

마스터가 없는데 미등록으로 세면 관측 전량이 경보가 되고, 그러면 사람이 경보를 보지 않게 된다.
같은 이유로 조회 실패는 조용한 빈 목록이 아니라 오류로 남고, 실패한 갱신은 직전 사실을 지우지
않는다 — 일시적 장애로 마스터가 비면 대사가 통째로 뒤집힌다.

**외부 데이터의 격리.** MCP·공개 API 응답은 외부에서 들어온 값이다. 여기서 나온 사실은
마스터 조회 키와 개수로만 쓰이고 **개념 문서나 AI 프롬프트가 되지 않는다** — LLM 편집자가
문자열만 돌려주게 만든 것과 같은 방어선이다(§5.5 3단계). 외부 값이 `Sensitive`나 개념 이름을
정할 수 있으면 그게 곧 마스킹 우회 경로다.

기반 축이 검수 큐에 올리는 문서는 둘뿐이다:

- **출처 등록** — "이 외부 당사자를 우리 기준정보의 권위로 인정하는가". 관측이 없는 문서라
  k인 게이트를 걸지 않는다. 내장 표준은 우리가 배포한 코드라 물을 상대가 없으므로 제외된다.
- **대사 결핍** — 마스터에 없는 관측 키. 값이 실리므로 품목 축과 같은 게이트를 건다.
  승인해도 마스터가 되지 않고 담당자가 확인할 대상으로만 남는다.

### 5.7 작업 완료 신호 — 규칙을 추측에서 관측으로

`rule.required.{BO}`는 시드 상태에서 **추측**이다. "구매요청에는 품목·수량·납기·거래처가
필요하다"고 문서가 말하지만 아무도 확인한 적이 없고, 가이드의 "…입력이 남았습니다" 제안이
전부 그 위에 서 있다. 반복 거부되는 제안이 도메인 축 신호로 잡히는 것도 같은 뿌리다 —
사용자가 게으른 게 아니라 규칙이 틀렸을 수 있다.

작성 **중간**의 화면은 증거가 되지 않는다. 빈칸이 아직 안 채운 것인지 필요 없는 것인지
구분되지 않기 때문이다. **저장을 누른 순간**의 화면만이 "이 업무객체가 실제로 무엇을
요구했는가"를 말해 준다.

```
관측(trigger=save_clicked) → 개념별 (화면에 있던 횟수, 채워진 횟수)
                           → 채움률 → 필수 필드 규칙 개정 초안 → 사람 승인 → 가이드 변경
```

**채움률의 분모는 완료 건수가 아니라 관측 건수다.** 그 필드가 없는 화면 변형에서
"안 채웠다"로 세면 변형 하나가 규칙 전체를 흔든다.

**판정은 세 갈래다.** 90% 이상이면 필수로, 50% 이하면 비필수로 제안하고, **그 사이는
건드리지 않는다**. 애매한 증거로 규칙을 흔들면 가이드가 배치마다 말을 바꾸고, 그러면
사용자가 가이드를 믿지 않게 된다. 관측되지 않은 개념도 그대로 둔다 — 증거 없음은 불필요가 아니다.

**마스킹된 민감 필드는 채움으로 센다.** `mask`는 값을 `***MASKED***`로 바꿀 뿐 지우지
않으므로 사용자는 실제로 입력했다. 빈칸으로 세면 단가가 영원히 "안 채워진" 것이 되어
필수에서 빠지고, 가이드는 이미 채워진 칸을 계속 채우라고 한다. 가이드와 이 집계는
같은 판정 함수(`FieldFill`)를 쓴다 — 갈리면 규칙 개정이 모순 위에 선다.

값은 저장되지 않는다. 남는 것은 개념 이름과 개수뿐이다.

---

## 6. Entity Resolver — 3축 연결 (제품의 핵심)

단일 LLM 호출 금지. **3단 캐스케이드**, 상위에서 확정되면 종료:

```
1) Deterministic  : 공유 키 매칭
    - 문서 첨부 ↔ 메일 (attachment id)
    - 파일 경로 ↔ Project (폴더 구조)
    - ERP 문서번호/협력사코드 ↔ ERPRecord/Company
2) Temporal       : 시간창 가중치
    - "이 화면을 열기 직전 N분 내 읽은 메일/문서"에 높은 점수
3) Semantic       : 임베딩 유사도 상위 후보만 LLM에 "동일 업무?" 판정
```
- 1·2에서 신뢰도 임계치 초과 시 LLM 미호출 → 대부분 저비용 처리.
- 매칭 결과는 KG 엣지로 영속화, `confidence`·`provenance` 기록.

**구현 현황: 1단만.** 2단(Temporal)과 3단(Semantic)은 **메일·문서 관측이 전제**라 ERP 단일 축인
현재 범위 밖이다. 1단은 Business Object의 비민감 값에서 엔티티를 추출해 정규화 키로 접는다
(`EntityRef`가 지정된 개념만 — 어떤 개념이 엔티티인지는 지식 문서가 정하므로 재배포 없이 바뀐다).

> **자동 병합하지 않는다.** 정규화(공백·대소문자·전각)로 접히지 않는 표기 차이(`M-001` vs `M001`)는
> `/v1/entities`의 `splits`로 **보고만** 한다. 잘못 합치면 서로 다른 품목이 하나가 되고, 그 오류는
> 이후의 모든 품목 지식에 전파된다 — 갈린 채로 두는 쪽이 회복 가능하다.

---

## 7. Workflow 안전장치 (제안서 11장 보강)

```
Guide → Validation(Dry-run) → [Human Approval] → Automation → Verify(Vision 결과확인) → Audit
```
- **기본값 = 사전 승인(Human-in-the-loop).** 신뢰도 누적 후 저위험 액션만 단계적 자동화.
- 모든 실행은 **불변 Audit Log(CloudTrail + 앱 감사테이블)**.
- **롤백 규칙 + 전역 Kill Switch** 필수.
- 실행 권한은 관측과 분리된 별도 IAM 역할/경로.

---

## 8. 데이터 거버넌스 & 보안 (제안서 13장 확장 — R1 대응)

| 영역 | 정책 |
|------|------|
| 전송 경계 | 모든 데이터 **테넌트 VPC 내부**. Bedrock은 VPC Endpoint 경유, 인터넷 미경유 |
| LLM 데이터 | Bedrock 무학습 정책 근거. 프롬프트/응답 로그 보존기간 명시·암호화 |
| 저장 | S3/Aurora **SSE-KMS 암호화(고객 관리 키)**, 전송 TLS/mTLS |
| 최소수집 | Privacy Gate에서 값·이름·제목·URL 등 전체 문자열과 메타데이터를 검사하고 화이트리스트 필드만 승격. 변경 없음은 폐기하고 동일 화면은 델타 우선 |
| 동의/투명성 | 관측 대상·범위 사용자 고지 UX, on/off 및 앱별 제외 목록 |
| 보존/파기 | 이벤트·임베딩·원본별 보존기간(TTL), 사용자 삭제 요청 처리 경로 |
| 접근통제 | IAM 최소권한, **사용자별 데이터 격리(필수, D5)** — Personal Plane(§5.4)은 사용자 간 격리, Shared Plane 승격은 사적정보 제거 후에만 |
| 감사 | CloudTrail + 앱 감사로그, 자동화 액션 전량 기록 |
| 지식 승인 | Plane 3 게시·폐기(§5.5)는 온톨로지·규칙을 영구히 바꾼다 — 승인 엔드포인트가 1순위 보호 대상 |
| 주체 인증 | 요청 주체는 미들웨어 한 곳에서 정해진다(§8.1). 검증 없는 방식은 운영 환경에서 **기동 거부** |

### 8.1 요청 주체 — 관문은 하나

주체는 <b>미들웨어 한 곳</b>에서 해석되고 엔드포인트는 읽기만 한다. 엔드포인트마다 헤더를
읽던 시절에는 인증 방식을 바꾸려면 전부를 고쳐야 했고, 하나를 빠뜨리면 그 엔드포인트만
옛 방식으로 열려 있었다 — 클라이언트의 PrivacyGate를 전송 경계에 하나만 둔 것과 같은 이유다.

어느 구현이든 **본문·쿼리에서는 사용자를 받지 않는다.** 본문이 사용자를 정하면 누구나 남의
personal 스코프에 매핑을 심을 수 있고, D5의 격리가 스코프 문자열로만 존재하게 된다.

| 방식 | 검증 | 용도 |
|---|---|---|
| `header` | **없음** — `X-ChoPilot-User`를 그대로 믿는다 | 로컬 개발·측정 콘솔 |
| `jwt` | 서명·발급자·수신자·만료 | 그 밖의 모든 경우 |

**검증 없는 방식으로는 운영 환경에서 서버가 뜨지 않는다.** 이전에는 이 위험이 주석과
README 한 줄에만 있었는데, 문서는 배포를 막지 못한다. 정말 신뢰 경계 안이라면
`Auth:AllowUnverifiedInProduction=true`로 명시하게 한다 — 사고가 아니라 결정이 되도록.
(ASP.NET Core는 환경 변수가 없으면 Production으로 보므로, 로컬 실행은 `launchSettings.json`이
Development를 선언해 이 방어선에 걸리지 않는다. 배포된 컨테이너에는 그 파일이 없다.)

신원이 없는 요청은 **401**이다 — 400이 아니다. 본문이 틀린 게 아니라 자격증명이 없는 것이고,
클라이언트가 고칠 것도 본문이 아니다. 검증 실패(위조·만료·발급자 불일치)는 예외로 새어
나가지 않는다: 만료된 토큰 하나가 500을 만들면 클라이언트 스풀이 그것을 서버 장애로 읽는다.

### 8.2 권한·소유권 경계

인증은 “누구인가”만 답한다. 각 API는 다음 역할과 리소스 소유권을 별도로 확인한다.

| 역할 | 허용 범위 |
|------|-----------|
| `ingestion_client` | 자신의 등록 장치에서 이벤트 제출. 감사·관측 조회 불가 |
| `end_user` | 자신의 Review Task·관측 요약 조회, 개인 피드백 제출·되돌리기 |
| `reviewer` | 조직 범위 매핑/피드백 검수. 자신이 제출한 조직·전역 초안의 자기 승인 금지 |
| `knowledge_admin` | 지식 게시·폐기·기반 출처 관리. 모든 변경은 사유와 버전 필수 |
| `ops_auditor` | 메트릭·감사·저장소 상태 읽기. 업무 원문은 별도 최소권한 |

라우트는 `/v1/me/*`, `/v1/reviews/*`, `/v1/admin/*`, `/v1/ingestion/*` 그룹으로 분리하고 정책을
기본 거부(default deny)로 적용한다. 관측 상세와 가이드도 소유권을 확인하며, GET은 노출 통계를
변경하지 않는다. 실제 화면 노출 시 별도 멱등 `impression` 명령을 기록한다.

---

## 9. 관측 지표 (KPI) — 제안서에 없던 성공 정의

| 지표 | 정의 | 목표(초기) | 산출 |
|------|------|-----------|------|
| 관측 정확도 | UIA→Business Object 필드 정확 매핑률 | ≥ 90% | 수기 채점 |
| **변경 없음 억제율** | 후보 관측 중 의미 변화가 없어 저장·전송하지 않은 비율 | ≥ 95% (정적 화면) | 클라이언트 회차 통계 |
| **전송량 절감률** | 전체 스냅숏만 보낼 때 대비 체크포인트+델타 바이트 감소 | ≥ 70% | 업로더 바이트 계측 |
| **상태 재구성 성공률** | 수신 체크포인트+델타를 순서대로 접어 기대 상태와 일치한 비율 | 100% | 재생/무결성 테스트 |
| **변경 관측 지연** | UIA 변경 발생→서버 수신 p95(저빈도 안전망 제외) | ≤ 2s | 이벤트 시각·수신 시각 |
| **AI 매핑 정확도** | 캐시 미스 시 AI 동적 매핑 정확률 | ≥ 90% | 수기 채점 |
| **매핑 캐시 적중률** | 정상 상태 캐시 HIT 비율(AI 미호출) | ≥ 95% | `/v1/metrics.cacheHitRatio` |
| **화면 변경 자가치유** | 화면 변경 감지→재추론 성공률 | ≥ 95% | 서명 변경 후 `source=ai` |
| **개인화 수렴** | 웜스타트(개인축적 후) vs 콜드스타트 정확도·수락률 개선폭 | 유의미 향상 | `/v1/suggestions` 시계열 |
| **제안 수락률** | 도출 로직(다음작업/자동화) 사용자 수락 비율 | 상승 추세 | `/v1/suggestions.acceptanceRate` |
| **검측 완료 시간** | Review Task 노출→사용자 결정 p50/p95 | p50 ≤ 20s | Feedback 이벤트 |
| **검측 조작 수** | 한 과제 확인·수정에 필요한 클릭/입력 수 | 확인 ≤ 1, 수정 ≤ 3 | UI telemetry |
| **피드백 반영 지연** | 개인 보정 제출→다음 해석에 적용되는 시간 p95 | ≤ 5s | revision/effective timestamp |
| **공유 승격 정밀도** | 조직 승격본 중 재검수에서 유지된 비율 | ≥ 95% | review decision |
| **고신뢰 표본 오류율** | 고신뢰 무작위 표본 중 사용자 수정 비율 | 하락 추세 | sampled review task |
| **지식 초안 승인률** | 관측에서 생성된 지식 초안 중 사람이 승인한 비율(§5.5) | 상승 추세 | `/v1/knowledge` 집계 |
| **온톨로지 성장** | 게시된 개념·규칙 수와 버전 (배포 없이 성장하는가) | 증가 | `/v1/knowledge.version` |
| 연결 정밀도 | Entity Resolver 정탐/오탐 | Precision ≥ 0.9 | 미구현 |
| 업무 인식 정확도 | "현재 업무" 판정 정확률(사람 라벨 대비) | ≥ 80% | 수기 채점 |
| 자동화 성공률 | 승인된 워크플로 무개입 완료율 | ≥ 95% | 미구현(Phase 4) |
| LLM 단가 | 활성 사용자당 일 토큰/비용 | 예산 내 | `/v1/metrics.{aiCalls,inputTokens}` |
| 지연 | 관측→업무판단 p95 | ≤ 3s | `/v1/metrics.latencyP95Ms` |

**AI 호출 수는 캐시 미스 수가 아니다.** 신뢰도가 θ_high 미만인 매핑은 자기 신뢰도로 적중 조건을
만족시킬 수 없어(θ 절벽) 캐시에 있어도 미스로 판정되지만, 매 관측마다 다시 추론하지는 않는다
(재추론 백오프, 기본 24h). 세 경우를 `source`로 구분한다 — `trusted_cache`(적중) /
`deferred_cache`(재사용, 호출 없음) / `ai`(실제 호출). 저신뢰 상태를 벗어나는 길은 재추론이 아니라
**사람의 검수·보정**이다(§5.2 step 6).

**수락률의 분모는 노출이고, 무시는 거부가 아니다.** 제안이 화면에 나간 시점에 노출이 기록되고,
사용자의 명시적 판단(수락/거부)만 분자에 들어간다. 무응답은 별도의 **응답률**로만 잡힌다 —
규칙을 고쳐야 하는 신호(수락률 하락)와 제안 UX를 고쳐야 하는 신호(응답률 하락)는 다르기 때문이다.

---

## 10. 실행 로드맵 (위험 제거형)

제안서의 Web→Mail→Doc→BUL 순서는 최난도(융합·거버넌스)를 뒤로 미룬다. 코드 리뷰에서 확인한
신뢰 경계와 멱등성 문제를 먼저 닫은 뒤 **Phase 0로 핵심 제품 가정을 선검증**한다.

| Phase | 목표 | 검증/산출 |
|-------|------|-----------|
| **-1. 신뢰 경계 안정화 (1~2 sprint)** | 인증 주체 강제, 역할·소유권, 전체 Privacy Envelope, 원자적 멱등 ingestion, 입력 한도 | P0 보안/재전송 테스트 전부 통과. 이 단계 전에는 운영 데이터 사용 금지 |
| **0. 위험 검증 (4~6주)** | **자체 Procurement** 대상 ① **UIA 관측 정확도 실측** ② **Adaptive Mapping**(AI 동적매핑·캐시·자가치유) PoC ③ Privacy Gate ④ Entity Resolver 결정론 매칭 | "UIA로 안 읽히거나 AI 동적매핑이 성립 안 하면 무의미"한 가정 검증 |
| **1. Web Agent + Guide + Review** | 자체 Procurement에서 "현재 업무 이해 + 가이드 + 빠른 검측" | Tier 1 UIA·Adaptive Mapping·Registry, 사용자 Review UI·개인 보정, 이후 안정 키 기반 체크포인트/델타와 UIA 이벤트 구독 |
| **2. Mail + Doc 수집·인덱싱** | Graph 수집, Office/PDF 파싱, KG·Vector 적재 | Knowledge Graph v1 |
| **3. Business Understanding** | 3축 융합, 진행률/다음작업 | Entity Resolver 3단, BUL |
| **4. Workflow Automation** | 승인형 → 단계적 자동화 | 안전장치·Audit·Kill Switch |

> **개인화(D5, §5.4)는 특정 Phase가 아니라 전 Phase를 관통하는 축이다.** Phase 1부터 Shared Structural Plane(공유 매핑 캐시)과 Personal Plane(UEP) 스코프를 데이터 모델에 반영하고, Phase 3~4에서 도출 로직(다음작업·자동화)의 개인화·피드백 학습을 본격화한다. Phase 0에서는 캐시 스코프 필드만 선반영(개인화 검증은 다중 사용자 확보 후).

---

## 11. 미해결/후속 결정 사항
- ~~자체 Procurement의 형태~~ → **확정: 웹앱**. 브라우저(Chrome/Edge) 접근성 트리로 UIA 접근, Privacy Gate를 통과한 정규화 URL 경로만 화면·레코드 식별의 보조 신호로 활용
- **완충 자원 확보** — 화면/필드 명세 문서, 비운영 테스트 환경, 도메인 전문가 접근 가능 여부
- **브라우저 표준** — 사내 표준 브라우저(Chrome/Edge) 및 버전, 접근성 트리 노출 정책 확인
- VDI 구체 형상(AWS WorkSpaces vs 사내 VDI) — 네트워크 경로 상세
- 2차 확장 대상(SAP/PLM 등) 우선순위 — Tier 1/2 Adapter 계획
- 사용자 규모 → Aurora vs OpenSearch/Neptune 분기 시점
- ~~멀티테넌시 필요 여부~~ → **확정: 사용자별 격리 필수(D5)**. 남은 결정은 격리 입도(사용자/부서/법인)와 Shared Plane 승격 정책·검수 주체
- 개인화 레이어 저장 위치(UEP 스키마: Aurora vs 별도 스토어)와 피드백 신호 보존기간
- **지식 문서 저장소**(§5.4 Plane 3) — PoC는 코드 시드 + 저널(§12), 운영은 Aurora(버전·감사 이력 포함) 예정. 지식 초안 생성기(축별 집계·LLM 편집자)의 실행 주기와 비용 상한
- **작업 완료 신호** — 서버 경로는 구현됨(§5.7). 목표는 §3.4의 UIA `Invoke` 이벤트에서 저장·제출 버튼을 식별하는 것이다. 앱별 이벤트 신뢰도가 검증되기 전에는 `--completed` 수동 표시를 유지한다. 폼→목록 전이 추정은 오탐이 필수 필드 규칙을 오염시키므로 보조 증거로만 사용한다
- **변경 관측 임계치** — 디바운스 750ms·최대 대기 3초·안전망 30~60초·체크포인트 50건/10분은 초기값이다. 앱별 이벤트 폭주·누락률과 §9 KPI를 측정해 확정한다
- **안정 노드 키 충돌 정책** — 반복 테이블·가상화 목록에서 `AutomationId`가 재사용될 때 레코드 키를 어디까지 결합할지 Phase 1 실측으로 결정한다. 불확실하면 델타가 아니라 체크포인트로 폴백한다
- **검측 방해 예산** — 사용자·업무별 일 최대 질문 수와 고신뢰 표본률(초기 1~5%)을 실측으로 확정한다
- **조직 검수 주체와 SLA** — `reviewer` 지정 방식, 개인 보정의 조직 승격 기준, 미처리 초안 만료기간을 확정한다

---

## 12. 영속화 — 저널

저장소는 전부 인메모리였다. 재시작하면 사람이 승인한 지식, AI 비용을 치르고 얻은 매핑 캐시,
그리고 §8이 요구하는 "전 관측 Audit"이 함께 사라진다 — **사라지는 감사 로그는 감사 로그가 아니다.**

각 저장소가 **추가 전용 저널**을 하나씩 갖고, 부팅 때 전량을 읽어 상태를 복원한다.
저장소들이 이미 그 모양이라 자연스럽다: 전부 부팅 시 메모리에 올리고 메모리에서 응답하며,
변경은 추가(또는 키 단위 덮어쓰기)뿐이다. 그래서 전량을 읽는 부팅이 낭비가 아니라 맞는 모양이다.

**무엇을 남기는가**가 저장소마다 다르다.

| 저장소 | 남기는 것 | 이유 |
|---|---|---|
| 매핑 캐시 · 제안 판단 | **결과 레코드** | 키가 레코드에서 유도돼 마지막 쓰기가 이긴다(로그 구조 저장소) |
| 관측 | **체크포인트 + 순서 있는 델타** | 현재 상태를 복원하면서 동일 화면의 전체 트리 중복 저장을 피한다. `event_id`는 멱등 키다 |
| 감사 · 결정 · 미지 개념 · 완료 신호 | **결과 레코드** | 원래 추가 전용이다 |
| UEP · 엔티티 | **입력** | 상태가 입력의 접기 결과다. 접는 규칙(세션 단절 기준 등)이 바뀌면 예전 관측이 새 규칙으로 다시 접힌다 |
| 지식 | **연산**(제출·승인·폐기) | 승인·폐기는 대기본을 <b>지우는</b> 부수효과가 있어 문서만 봐서는 복원되지 않는다 |
| 기반 마스터 | **남기지 않는다** | 외부 출처에서 다시 받는 것이 맞다. 관측 파생물을 마스터로 굳히면 계보가 뒤집힌다(§5.6) |

**관측 압축과 보존.** 운영 저장소는 체크포인트와 그 이후 델타를 하나의 복원 단위로 다룬다.
새 체크포인트가 확정되면 이전 체크포인트+델타를 접어 압축할 수 있다. 원시 UI 상태는 짧은 TTL을,
Business Object·완료 증거·감사 결정은 목적별 장기 보존정책을 적용한다. 기준 체크포인트만 먼저
삭제해 고아 델타를 만들지 않으며, 파기·사용자 삭제도 복원 단위 전체에 원자적으로 적용한다.

PoC의 현재 JSON Lines 관측 저널은 전체 결과 레코드를 저장한다. 체크포인트/델타 계약이 구현되면
같은 `IJournal` 경계 안에서 이벤트 종류를 확장하고, 복원 시 `sequence` 공백·중복·손상 건수를
별도로 보고한다. 최적화 전후를 비교할 수 있도록 전체 스냅숏 바이트, 실제 전송 바이트,
체크포인트/델타 건수도 운영 지표로 남긴다.

**지식 버전은 되감기지 않는다.** 복원 시 게시·폐기 횟수만큼 올린다 — 되감기면 저신뢰 매핑의
재추론 백오프가 "온톨로지가 바뀌었다"고 오인해 캐시가 통째로 무효화된다(§5.5 7단계).

**형식은 JSON Lines**다. 여기 담기는 것은 설계와 함께 계속 바뀌는 record라, 스키마를 손으로
들고 있으면 필드 하나 늘 때마다 마이그레이션이 붙는다. JSON Lines는 nullable 필드 추가가
공짜이고 예전 줄도 그대로 읽힌다. 질의·인덱스는 어차피 쓰지 않는다.

**쓰기 도중 종료**되면 마지막 줄이 잘린다. 그 줄만 건너뛰고 **세어서 보고**한다 — 전체를
버리면 멀쩡한 앞부분까지 잃고, 조용히 넘어가면 유실을 아무도 모른다.

**영속화는 선택이다.** `Storage:Path`를 주지 않으면 지금까지와 똑같이 인메모리로 돈다.
테스트와 CI가 디스크를 건드리지 않는 이유이고, 콘솔 상단 배지가 어느 쪽인지 항상 보여 준다.
운영은 여전히 Aurora/DynamoDB(§11)이며, 저널은 그 저장소 인터페이스를 미리 갈라 둔 자리다.

---

## 13. 코드 리뷰 기준 보완 계획 및 이행 상태 (2026-08-20)

### 13.1 검토 범위와 검증 상태

Core·Mapping·Server·Windows Client, 정적 Web UI, 테스트, 설정을 함께 검토했다. .NET 8 런타임에서
서버·코어·매핑 테스트 **325건이 모두 통과**했고, `net8.0-windows` Client는 Windows targeting 빌드에서
경고 0·오류 0을 확인했다. 로컬 서버에서 Review UI의 과제 조회 → 수정 제출 → 개인 반영 → 완료 상태를
브라우저로 검증했다. 실제 Windows VDI의 UIA 이벤트 구독·접근성 트리 품질·재시작 복구는 별도 E2E/장애
주입 검증 대상이며, 이 문서에서 완료로 표기하지 않는다.

### 13.2 우선순위별 발견 사항

| 우선순위 | 발견 사항 | 이행 상태 | 남은 보완 |
|---------|-----------|-----------|-----------|
| **P0** | 본문 주체 위조·토큰 전달 부재 | **완료:** Bearer/tenant 전달, JWT subject와 본문 불일치 403 | OIDC/JWKS 키 회전으로 전환 |
| **P0** | API 역할/소유권 경계 부족 | **완료:** JWT 기본 거부 RBAC, 관측·가이드·검수 ownership 분리 | 정책 엔진/권한 행렬 E2E |
| **P0** | UIA 메타데이터 PII 유출 가능 | **완료:** 문자열 Privacy Envelope, 주소창 전용 URL, query/fragment 제거, 서버 잔여 스캔 | 정규식 사전 운영 튜닝 |
| **P0** | 재전송 시 파생 통계 중복 | **완료:** tenant/user/event 단위 동시성 관문 및 replay 영수증, projection 멱등키 | DB 트랜잭션+outbox, 장애 주입 |
| **P0** | 부분 저널 실패 일관성 | **PoC 완화:** projection 멱등 저널 | Aurora 단일 트랜잭션+outbox 전환 전에는 운영 보장 아님 |
| **P1** | 동시 AI 중복·취소 전파·응답 검증 | **완료:** signature+ontology single-flight, 호출자 취소 분리, ref allowlist·confidence clamp·prompt budget | 모델 JSON schema/timeout·토큰 상한 강제 |
| **P1** | 불안정한 노드 키와 변경 지문 | **부분 완료:** AutomationId+상위 경로 기반 안정 ref, 값 포함 변경 없음 억제 | 체크포인트/델타·UIA event 구독은 feature flag 단계 |
| **P1** | 세션/스풀 순서와 용량 | **완료:** 실행별 UUID, monotonic spool sequence, quota/TTL/quarantine/status | 우선순위별 backpressure와 운영 telemetry |
| **P1** | 피드백 정확성/조직 승격 | **완료:** Review Task, revision 검증, 개인 즉시/조직 검수, 자기 검수 차단, undo | 승인자 다수결·감사 조회 UI |
| **P1** | 입력·전송 자원 상한 | **완료:** 크기/깊이/문자열/중복 ref/요청 크기/rate limit | tenant/user별 동적 quota |
| **P2** | JSONL은 무한 증가·전량 복원·부분 쓰기 중심이고 compaction/checksum/fsync 정책이 없음 | `Core/JsonLineJournal.cs`, §12 저장소 | PoC rotation/snapshot/checksum, 운영 DB 이관·TTL·복구 훈련 |
| **P2** | AWS SDK 버전이 floating이고 SDK/restore lock이 없어 재현성이 약함 | 프로젝트 파일·저장소 루트 | exact package version, `packages.lock.json`, `global.json`, 분석기/경고 정책 |
| **P2** | 운영 콘솔이 사용자 검측까지 겸하며 새로고침마다 다수 API를 호출. CSP·rate limit·통합 오류 UX가 없음 | `wwwroot/index.html`, `wwwroot/app.js` | `/review`와 `/admin` 분리, 집계 endpoint/SSE, CSP·보안 헤더·접근성·오류 복구 |

### 13.3 실행 순서와 완료 조건

| 묶음 | 기간 기준 | 작업 | 완료 조건 |
|------|-----------|------|-----------|
| **A. P0 Gate** | **구현·회귀 통과** | 인증/인가·Privacy Envelope·멱등 ingestion·입력 상한 | 운영 DB outbox 및 장애 주입을 추가 완료 조건으로 둔다 |
| **B. Feedback Correctness** | **구현·브라우저 스모크 통과** | Review Task/Feedback API, 개인/조직 workflow, 사용자 UI, revision/undo | 다중 승인·운영 감사 UI |
| **C. Runtime Reliability** | **구현·회귀 통과** | AI single-flight/검증, 안정 키·세션 순서, spool quota, guide GET 부작용 제거 | 재시작·오프라인 Windows E2E |
| **D. Production Hardening** | 2~3 sprint | 운영 DB/compaction, OIDC/JWKS·키 회전, rate limit/CSP, 의존성 고정, 관측성 | 부하·복구·권한 행렬·Windows UIA E2E·보존/삭제 시험 통과 |
| **E. Change-aware rollout** | Phase 1 | UIA event, stable key, checkpoint/delta를 앱별 feature flag로 단계 적용 | §9 재구성 100%, 전송량 ≥70% 절감, 이벤트 누락 시 자동 resync, 앱별 rollback 가능 |

**착수 규칙.** A를 통과하기 전 운영 사용자 데이터와 조직 공유 지식을 받지 않는다. D6 델타는
멱등 ingestion과 안정 노드 키가 먼저 확보된 뒤 활성화한다. B의 개인 피드백은 빠르게 적용하되,
조직·전역 승격은 역할 분리와 감사가 완성될 때까지 대기 상태만 허용한다.
