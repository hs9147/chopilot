# Cho-Pilot — Architecture Design

> Enterprise Work Intelligence Platform
> **Screen을 이해하는 AI를 넘어 Business를 이해하는 AI**

Version : 1.0 (Architecture)
Status : Design
연계 문서 : [Proposal Draft.md](Proposal%20Draft.md) v1.1

---

## 0. 설계 확정 전제 (Design Decisions)

| # | 결정 | 내용 | 파급 |
|---|------|------|------|
| D1 | **LLM/추론 플랫폼** | **AWS 기반**. LLM = Amazon Bedrock(Claude), 임베딩 = Bedrock Titan/Cohere. 모든 데이터는 **테넌트 VPC 내부**에서만 이동 | 외부 전송 없음. Bedrock 무학습 정책 → 거버넌스 근거 확보 |
| D2 | **1차 타깃 환경** | **영속(persistent) VDI 세션 내부에서 동작하는 데스크톱 앱**. 사용자별 계정·프로파일 유지 | 세션 내부 실행 → **UIA 트리 정상 접근**(픽셀-only 문제 해소). 계정 유지 → **로컬 상태 신뢰 가능**. 단 GPU는 여전히 부재 |
| D3 | **1차 타깃 애플리케이션** | **자체 개발 General Procurement 시스템**. 단 **소스 접근 불가 → 사용자 관점(외부) 관측만 허용** | 협조형(Tier 0) 불가. **Tier 1(UIA)** 로 관측. 사내 시스템이므로 **화면/필드 문서·테스트 환경·도메인 전문가** 확보는 가능(시드·검증용) |
| D4 | **동적 환경 · AI 적응형 로직** | 테스트/운영 환경이 **동적으로 구성**되어 화면·필드가 고정적이지 않음. 따라서 매핑을 하드코딩하지 않고 **런타임에 내부 AI(Bedrock) 호출로 화면을 해석·적응** | 정적 규칙 우선 → **Adaptive Semantic Mapping(§5)**: AI가 접근성 트리를 해석해 매핑을 생성하고 **자가학습 캐시**에 적재, 화면 변경 시 **자동 재추론(self-healing)**. 대신 비용·지연·정확도 관리가 핵심(캐시·서명·신뢰도) |
| D5 | **사용자별 환경·로직 축적 및 도출** | **사용자마다 환경(앱·화면·데이터·습관)과 로직을 축적**하고, 그 환경에 **맞는 로직을 도출**(개인화) | **2-Plane 지식 모델(§5.4)**: ① 구조 지식(화면→개념 매핑)은 전역/조직 공유(효율·콜드스타트) ② 개인 로직(워크플로 습관·엔티티 별칭·보정·선호)은 **사용자별 격리**. 도출은 **개인 > 조직 > 전역 캐스케이드** + 피드백 학습. 개인정보 격리(멀티테넌시) 필수 |

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

---

## 2. 배포 토폴로지 (AWS + VDI)

```
┌─────────────── VDI 세션 (사용자, AWS WorkSpaces/AppStream 또는 사내 VDI) ────────────┐
│                                                                                       │
│   관측 대상 앱: SAP GUI, PLM Client, Outlook, Office, 파일 서버(마운트)               │
│        ▲                                                                              │
│        │ UI Automation / COM / FileSystemWatcher                                      │
│   ┌────┴───────────────── Cho-Pilot Agent (.NET 8, 초경량) ──────────────────┐        │
│   │  Observation Adapters → Local Privacy Gate → Event Buffer(durable) → gRPC/HTTPS│    │
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
| Web Adapter | **브라우저 접근성 트리(UIA)** 순회 → 정규화 UI 이벤트. **URL·화면 타이틀로 화면/레코드 식별** | `System.Windows.Automation` / FlaUI (Chrome·Edge 접근성 트리, Extension 불요) |
| Mail Adapter | 메일 이벤트 | **Graph API 우선**, Outlook COM fallback |
| Doc Adapter | 파일 변경 감지, 메타데이터 | `FileSystemWatcher` |
| **Local Privacy Gate** | 전송 전 PII 탐지·마스킹·정책 필터 | 정규식/사전 기반(경량, GPU 불요) |
| Event Buffer | 정규화 이벤트 큐, 오프라인·재시도 | 인메모리 + **durable 로컬 스풀**(영속 VDI, 재부팅 내성) |
| Local Cache | 최근 컨텍스트·매핑 규칙 사본(읽기용) | 로컬 SQLite. 원본은 서버 |
| Uploader | 서버 전송 | gRPC(우선)/HTTPS, mTLS |

> **원칙:** OCR·임베딩·비전은 클라이언트에서 하지 않는다(GPU 부재). 스크린샷이 필요한 경우(Canvas/이미지)만 캡처해 Privacy Gate 통과 후 서버 OCR로 전송.

### 3.2 Server
| 서비스 | 책임 |
|--------|------|
| Ingestion | 이벤트 수신·검증·정규화, 서버측 OCR/임베딩 오케스트레이션 |
| Semantic Engine | UI 이벤트 → **Business Object** 변환 (**Adaptive Semantic Mapping**, §5) |
| App Adapter Registry | **자가학습 매핑 캐시(Shared Structural Plane)** — 화면 서명별 AI 도출 매핑, 신뢰도·검수상태, 스코프(Global/Org), 화면 변경 시 자동 무효화 |
| **User Environment Profile (UEP)** | **사용자별 환경·로직 축적(Personal Plane, §5.4)** — 화면 사용 빈도, 개인 엔티티 별칭, 워크플로 습관, 선호·보정 이력. 사용자별 격리 |
| **Personalization / Derivation** | 개인 ▷ 조직 ▷ 전역 캐스케이드로 환경 맞춤 로직 도출 + 피드백 학습 |
| **Entity Resolver** | 3축 연결(§6) |
| Knowledge Graph | 업무 엔티티·관계 저장(§4) |
| Vector Store | 메일/문서 청크 임베딩, Semantic Search |
| Business Understanding | 3축 융합 → 현재업무/목적/진행률/다음작업 |
| Workflow Engine | Guide→Validation→승인→Automation→Audit(§7) |
| LLM Gateway | Bedrock 라우팅, 토큰/비용 관리, 프롬프트 캐시, 모델 추상화. **모델 ID는 inference profile(us./global.) 사용** — 현재 Anthropic 모델은 ON_DEMAND 직접 호출 미지원(PoC 실측) |

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

---

## 4. 데이터 모델

### 4.1 정규화 이벤트 (Client → Server 계약)
```jsonc
{
  "event_id": "uuid",
  "session_id": "vdi-session-uuid",
  "user_id": "hashed-user-id",
  "source": "web | mail | doc",
  "captured_at": "ISO-8601",
  "trigger": "focus_changed | structure_changed | save_clicked | null",  // 예약 — 완료 신호(§11)
  "app": { "name": "SAP GUI", "window_title": "...", "screen_id": "ME51N" },
  "payload": { /* source별 정규화 구조 */ },
  "privacy": { "masked_fields": ["..."], "policy_version": "1.3" }
}
```

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
   - 축: 사용자(personal 파생 뷰) · 업무/품목(org) · 도메인/구매(org) · 기반 정보(global 시드)
   - Plane 1·2가 "관측이 쌓이는 곳"이라면 Plane 3은 "관측이 지식으로 응결되는 곳"(§5.5)
   - 게시본은 컴파일되어 온톨로지(Layer A)·Guide 규칙·AI 프롬프트의 원천이 된다
   - 문서 종류: view(스토어에서 자동 재생성 — 편집·승인 불가) / curated(사람 승인 필수)
   - 불변식: 문서에는 관측된 "값"이 들어가지 않는다 — 구조·의미·규칙만 (§8 마스킹과 별개의 2차 방어선)
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
| 최소수집 | Privacy Gate에서 PII 마스킹·화이트리스트 필드만 승격 |
| 동의/투명성 | 관측 대상·범위 사용자 고지 UX, on/off 및 앱별 제외 목록 |
| 보존/파기 | 이벤트·임베딩·원본별 보존기간(TTL), 사용자 삭제 요청 처리 경로 |
| 접근통제 | IAM 최소권한, **사용자별 데이터 격리(필수, D5)** — Personal Plane(§5.4)은 사용자 간 격리, Shared Plane 승격은 사적정보 제거 후에만 |
| 감사 | CloudTrail + 앱 감사로그, 자동화 액션 전량 기록 |
| 지식 승인 | Plane 3 게시·폐기(§5.5)는 온톨로지·규칙을 영구히 바꾼다 — 실제 인증(mTLS/OIDC) 도입 시 **승인 엔드포인트가 1순위 보호 대상** |

---

## 9. 관측 지표 (KPI) — 제안서에 없던 성공 정의

| 지표 | 정의 | 목표(초기) | 산출 |
|------|------|-----------|------|
| 관측 정확도 | UIA→Business Object 필드 정확 매핑률 | ≥ 90% | 수기 채점 |
| **AI 매핑 정확도** | 캐시 미스 시 AI 동적 매핑 정확률 | ≥ 90% | 수기 채점 |
| **매핑 캐시 적중률** | 정상 상태 캐시 HIT 비율(AI 미호출) | ≥ 95% | `/v1/metrics.cacheHitRatio` |
| **화면 변경 자가치유** | 화면 변경 감지→재추론 성공률 | ≥ 95% | 서명 변경 후 `source=ai` |
| **개인화 수렴** | 웜스타트(개인축적 후) vs 콜드스타트 정확도·수락률 개선폭 | 유의미 향상 | `/v1/suggestions` 시계열 |
| **제안 수락률** | 도출 로직(다음작업/자동화) 사용자 수락 비율 | 상승 추세 | `/v1/suggestions.acceptanceRate` |
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

제안서의 Web→Mail→Doc→BUL 순서는 최난도(융합·거버넌스)를 뒤로 미룹니다. **Phase 0로 핵심 위험을 선검증**합니다.

| Phase | 목표 | 검증/산출 |
|-------|------|-----------|
| **0. 위험 검증 (4~6주)** | **자체 Procurement** 대상 ① **UIA 관측 정확도 실측** ② **Adaptive Mapping**(AI 동적매핑·캐시·자가치유) PoC ③ Privacy Gate ④ Entity Resolver 결정론 매칭 | "UIA로 안 읽히거나 AI 동적매핑이 성립 안 하면 무의미"한 가정 검증 |
| **1. Web Agent + Guide(읽기전용)** | 자체 Procurement에서 "현재 업무 이해 + 가이드" | Tier 1 UIA·Adaptive Mapping·Registry |
| **2. Mail + Doc 수집·인덱싱** | Graph 수집, Office/PDF 파싱, KG·Vector 적재 | Knowledge Graph v1 |
| **3. Business Understanding** | 3축 융합, 진행률/다음작업 | Entity Resolver 3단, BUL |
| **4. Workflow Automation** | 승인형 → 단계적 자동화 | 안전장치·Audit·Kill Switch |

> **개인화(D5, §5.4)는 특정 Phase가 아니라 전 Phase를 관통하는 축이다.** Phase 1부터 Shared Structural Plane(공유 매핑 캐시)과 Personal Plane(UEP) 스코프를 데이터 모델에 반영하고, Phase 3~4에서 도출 로직(다음작업·자동화)의 개인화·피드백 학습을 본격화한다. Phase 0에서는 캐시 스코프 필드만 선반영(개인화 검증은 다중 사용자 확보 후).

---

## 11. 미해결/후속 결정 사항
- ~~자체 Procurement의 형태~~ → **확정: 웹앱**. 브라우저(Chrome/Edge) 접근성 트리로 UIA 접근, URL을 레코드 식별 신호로 활용
- **완충 자원 확보** — 화면/필드 명세 문서, 비운영 테스트 환경, 도메인 전문가 접근 가능 여부
- **브라우저 표준** — 사내 표준 브라우저(Chrome/Edge) 및 버전, 접근성 트리 노출 정책 확인
- VDI 구체 형상(AWS WorkSpaces vs 사내 VDI) — 네트워크 경로 상세
- 2차 확장 대상(SAP/PLM 등) 우선순위 — Tier 1/2 Adapter 계획
- 사용자 규모 → Aurora vs OpenSearch/Neptune 분기 시점
- ~~멀티테넌시 필요 여부~~ → **확정: 사용자별 격리 필수(D5)**. 남은 결정은 격리 입도(사용자/부서/법인)와 Shared Plane 승격 정책·검수 주체
- 개인화 레이어 저장 위치(UEP 스키마: Aurora vs 별도 스토어)와 피드백 신호 보존기간
- **지식 문서 저장소**(§5.4 Plane 3) — PoC는 코드 시드 + 인메모리, 운영은 Aurora(버전·감사 이력 포함) 예정. 지식 초안 생성기(축별 집계·LLM 편집자)의 실행 주기와 비용 상한
- **작업 완료 신호** — 도메인 축 규칙 검증(필수 필드 학습)에 필요. 이벤트 계약에 `trigger` 필드는 예약됨(§4.1), 클라이언트 구현(저장 버튼 관측 vs 전이 기반 추정)은 미정
