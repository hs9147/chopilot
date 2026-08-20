# Cho-Pilot
## Enterprise Work Intelligence Platform

> **Screen을 이해하는 AI를 넘어 Business를 이해하는 AI**

Version : 1.3
Status : Proposal  
연계 : 상세 설계는 [ARCHITECTURE.md](ARCHITECTURE.md) 참조

---

## 0. 개정 이력

| Version | 변경 |
|---------|------|
| 1.0 | 최초 제안 |
| 1.1 | 배포 형상 확정(**AWS + VDI 내부 데스크톱 앱**), 데이터 거버넌스·워크플로 안전장치·성공 지표(KPI) 추가, 위험검증 Phase 0 신설 |
| 1.2 | **변경 인지 관측(Change-aware Observation)** 추가: UIA 이벤트 기반 캡처, 변경 없음 억제, 체크포인트+델타 기록, 안정 노드 키·복원·보존 정책 및 KPI 반영 |
| 1.3 | Phase 1 baseline 구현 반영: 인증·역할·Privacy Envelope·멱등 ingestion, 사용자 검측/개인 보정 UI, 안정 노드 키·변경 없음 억제·스풀 quota. 체크포인트+델타와 UIA 이벤트 구독은 단계 배포 대상으로 명시 |

---

# 1. Executive Summary

## 1.1 프로젝트 개요

Cho-Pilot은 기업 사용자의 업무를 이해하고 지원하는 Enterprise AI Agent Platform이다.

기존 AI Agent는 화면(Screen)이나 애플리케이션(App)을 중심으로 동작한다.

Cho-Pilot은 화면 자체가 아니라 업무(Business)를 이해하는 것을 목표로 한다.

이를 위해 사용자의 업무를 다음 세 가지 축에서 동시에 분석한다.

- Enterprise Web Agent
- Enterprise Mail Intelligence
- Enterprise Document Intelligence

그리고 세 축의 정보를 Business Understanding Layer에서 통합하여

- 현재 수행 중인 업무
- 업무 목적
- 업무 진행률
- 다음 작업
- 자동화 가능 업무

를 실시간으로 판단한다.

---

# 2. 추진 배경

## 2.1 기업 환경의 현실

대부분의 기업은

- ERP
- SAP
- PLM
- MES
- 그룹웨어
- Outlook
- Office

등 여러 시스템을 동시에 사용한다.

업무 정보는

- 웹 시스템
- 메일
- 파일 서버

로 분산되어 있다.

사용자는

메일을 확인하고

↓

문서를 찾고

↓

ERP를 열고

↓

입력하고

↓

결재한다.

AI는 현재 이러한 업무 흐름 전체를 이해하지 못한다.

---

## 2.2 기존 AI Agent의 한계

기존 Computer Use Agent는

"현재 화면"

만 이해한다.

예를 들어

```
ERP 화면
```

은 이해하지만

- 왜 이 작업을 하는지

- 어떤 메일 때문인지

- 어떤 문서를 참고하는지

는 알지 못한다.

---

## 2.3 기업 보안 환경

기업에서는 다음과 같은 제약이 일반적이다.

- Chrome Extension 설치 제한
- Remote Debugging 금지
- DevTools 사용 제한
- 외부 Plugin 제한
- 폐쇄망 운영
- 관리자 권한 제한

따라서

브라우저 내부를 직접 제어하는 방식은

기업 적용성이 낮다.

---

# 3. 목표

Cho-Pilot은

업무를

화면

메일

문서

세 가지 관점에서 이해한다.

```
현재 업무

=

Web

+

Mail

+

Document
```

---

# 4. 핵심 철학

## Business First

기존

```
Screen

↓

Vision

↓

LLM
```

Cho-Pilot

```
Business

↓

Semantic

↓

LLM
```

Vision은

사용자와 같은 화면을 보기 위한 수단이다.

실제 업무는

Semantic Data로 처리한다.

---

# 5. 전체 아키텍처

```
                    Cho-Pilot

                         │

         Business Understanding Layer

                         │

──────────────────────────────────────────

      Enterprise Observation Layer

──────────────────────────────────────────

      │               │                │

 Web Agent      Mail Agent      Document Agent

      │               │                │

 UI Automation   Outlook      File System

 Vision          Mail Parser  Office Parser

 Accessibility   Thread       PDF Parser

                 Graph API    OCR

──────────────────────────────────────────

          Semantic Engine

──────────────────────────────────────────

          Knowledge Graph

──────────────────────────────────────────

            Workflow Engine

──────────────────────────────────────────

               LLM Planner
```

---

# 6. Enterprise Web Agent

## 목적

현재 사용자가

무슨 업무를 하고 있는지

이해한다.

예)

- 구매 요청
- 결재
- BOM 조회
- 생산 등록
- 자재 검색

---

## Observation

관측은 소스 통제 수준에 따라 3계층으로 운영한다.

- **Tier 1 (UIA)** — 접근성 트리 파싱 → **자체 Procurement 1차 적용** (소스 접근 불가, 사용자 관점 관측)
- **Tier 2 (Vision/OCR)** — 픽셀 폴백·검증
- **Tier 0 (Cooperative)** — 향후 소스 협조 가능 시 계측으로 고정확 방출(현재 미적용)

환경이 **동적으로 구성**되므로 매핑을 하드코딩하지 않고 **런타임 AI(Bedrock) 해석 + 자가학습 캐시 + 화면변경 자가치유**로 적응한다(Adaptive Semantic Mapping). 사내 시스템이므로 확보 가능한 **화면 명세·테스트 환경·도메인 전문가**는 AI의 few-shot 시드·검수 기준으로 활용한다.

### 변경 인지 관측 (Change-aware Observation)

Cho-Pilot이 말하는 기본 캡처는 화면 이미지가 아니라 **접근성 트리의 상태 캡처**다. Canvas·이미지처럼
UIA로 읽을 수 없는 영역만 Vision 캡처로 보완한다. 사용자가 아무 작업도 하지 않았는데 주기마다
전체 상태를 저장하면 VDI 부하·네트워크·저장비용이 늘고, 감사 로그와 캐시 적중률도 반복 횟수에
따라 부풀려진다. 따라서 관측은 **변경이 있을 때만 기록**하고, 같은 화면 안의 작은 변화는
**변경분(delta)만 저장**한다.

```
UIA 변경 이벤트
  → 디바운스·노이즈 제거
  → 영향 화면/서브트리 캡처
  → Local Privacy Gate
  → 직전 상태와 비교
      ├─ 첫 관측·화면 전환·구조 변경 → 전체 체크포인트
      ├─ 동일 화면의 값/상태 변경     → 델타
      └─ 의미 변화 없음               → 저장·전송 생략
```

| 구분 | 기록 정책 |
|------|-----------|
| 첫 관측·재접속 | 복원 기준이 되는 전체 체크포인트 |
| URL route·레코드·구조 변경 | 새 체크포인트 + 필요 시 Adaptive Mapping 재추론 |
| 입력값·검증 오류·업무 상태 변경 | 안정 노드 키 기준 `add/remove/replace` 델타 |
| 포커스·커서·스크롤·로딩 애니메이션·시계 | 업무 의미가 없으면 폐기 |
| 저장·제출·승인 동작 | 디바운스하지 않고 즉시 기록 |

입력 중에는 글자마다 저장하지 않는다. 일반 변경은 마지막 이벤트 후 약 750ms에 합치고, 연속 입력도
최대 3초마다 한 번만 기록한다. 이벤트 누락을 복구하기 위해 30~60초 주기의 저빈도 전체 확인을
병행한다. 체크포인트는 화면 전환·구조 변경 외에도 델타 수 또는 시간이 임계치를 넘으면 다시 생성해
복원 비용을 제한한다.

델타 비교와 생성은 반드시 **Privacy Gate 이후**에 수행한다. 민감 필드는 원문 대신 마스킹 토큰과
채움 여부만 남기고, 이전 값(`oldValue`)은 중복 보관하지 않는다. UIA 순회 순서로 붙는 임시 번호가
아니라 `AutomationId + 상위 경로 + Role` 기반의 **안정 노드 키**를 사용해 노드 하나가 추가됐다고
화면 전체가 변경된 것처럼 기록되는 것을 방지한다.

> **단계적 전환:** 현재 Phase 1 구현은 일정 간격으로 전체 UIA 트리를 읽은 뒤 값 포함 지문으로
> 변경 없음을 걸러낸다. 다음 단계에서 체크포인트+델타를 먼저 적용하고, 이후 UIA 이벤트 구독으로
> 캡처 자체의 빈도까지 줄인다. 이벤트 구독이 불안정한 앱에서는 저빈도 폴링을 안전망으로 유지한다.

### UI Automation

수집

- Button
- Edit
- ComboBox
- Table
- Tree
- Menu

Windows 공식 API 사용

Chrome Extension 불필요

---

### Vision

Vision은

- 현재 화면 확인
- Canvas
- 이미지
- 차트
- 최종 검증

용도로만 사용한다.

---

### Semantic Parser

UI를

Business Object로 변환한다.

예)

품목코드

↓

Material

수량

↓

Quantity

납기

↓

Delivery Date

---

### Workflow

예)

구매 등록

↓

품목 입력

↓

수량 입력

↓

저장

↓

결재 요청

---

# 7. Enterprise Mail Intelligence

메일은

업무의 이유(Why)를 설명한다.

---

## 수집 대상

- Subject
- Body
- Thread
- Attachment
- Sender
- Receiver

---

## Mail Parser

HTML

↓

본문

↓

Signature 제거

↓

Quote 제거

↓

Clean Text

---

## Thread Builder

Mail

↓

Conversation

↓

Task

---

## Action Extractor

메일에서

자동 추출

- 해야 할 일

- 담당자

- 일정

- 마감일

---

# 8. Enterprise Document Intelligence

문서는

업무의 근거(Knowledge)이다.

---

## 대상

- Word
- Excel
- PowerPoint
- PDF
- TXT
- Markdown

---

## Folder Scanner

최초 실행

Folder

↓

Recursive Scan

↓

Metadata

↓

Embedding

↓

Index

---

## File Watcher

변경 감지

↓

재분석

↓

Index Update

---

## Office Parser

Word

↓

Heading

↓

Paragraph

↓

Table

Excel

↓

Sheet

↓

Cell

↓

Formula

PowerPoint

↓

Slide

↓

Title

↓

Bullet

PDF

↓

Text Layer

↓

OCR

↓

Paragraph

---

## Folder Understanding

예)

Project

├── 계약

├── 회의

├── 견적

├── 생산

├── 품질

↓

Agent

↓

현재 프로젝트

관련 업체

관련 문서

최신 파일

자동 파악

---

# 9. Business Understanding Layer

Cho-Pilot의 핵심 기능이다.

세 Agent의 결과를

통합한다.

예)

Web

```
SAP 구매등록
```

+

Mail

```
A사 견적 등록 요청
```

+

Document

```
A사 견적서.pdf
```

↓

Business Context

```
현재 업무

A사 구매 등록

진행률 70%

다음 작업

결재 요청
```

---

# 10. Knowledge Graph

모든 업무 정보를 연결한다.

```
Project

│

├── Task

├── Mail

├── Document

├── ERP

├── Schedule

├── Person

└── Company
```

---

# 10-1. 사용자별 개인화 (환경·로직 축적·도출)

사용자마다

환경(앱·화면·데이터·습관)이

다르다.

Cho-Pilot은

사용자별로

환경과 로직을 축적하고

그 환경에 맞는 로직을 도출한다.

## 2-Plane 지식 모델

- **Shared Structural Plane** (전역/조직 공유)
  - 화면 → 개념 매핑 (구조 지식, 사적 정보 아님)
  - 신규 사용자 콜드스타트에 재사용

- **Personal Adaptive Plane** (사용자별 격리)
  - 자주 쓰는 화면·빈도
  - 개인 엔티티 별칭 (예: 거래처 약어)
  - 워크플로 습관
  - 선호·보정 이력

## 로직 도출

```
개인 ▷ 조직 ▷ 전역
```

순으로 병합해

현재 환경에 맞는

매핑 / 다음작업 / 자동화 후보

를 도출한다.

사용자가 수락·수정·거부한 피드백을 축적해

개인 레이어가

그 사용자의 방식에 수렴한다.

개인 정보는 사용자별로 격리한다.

---

# 11. Workflow Engine

Workflow

Guide

↓

Validation (Dry-run)

↓

**Human Approval** (기본값)

↓

Automation

↓

Verify (결과 확인)

↓

Audit (불변 로그)

## 안전장치

- 기본값은 **사전 승인(Human-in-the-loop)**. 신뢰도 누적 후 저위험 액션만 단계적 자동화
- 모든 자동화 실행은 **불변 Audit Log**(CloudTrail + 감사테이블)
- **롤백 규칙 + 전역 Kill Switch** 필수
- 실행 권한은 관측과 분리된 별도 경로/권한

---

# 12. 기술 구성

## 배포 형상 (확정)

- 추론/데이터 플랫폼 : **AWS** (모든 데이터 테넌트 VPC 내부)
- 관측 대상 환경 : **영속(persistent) VDI 세션 내부에서 동작하는 데스크톱 앱** (사용자별 계정 유지)
  - 에이전트가 VDI 세션 안에서 실행 → 내부 앱의 **UI Automation 트리 정상 접근** (VDI 픽셀-only 한계 해소)
  - 계정 유지 → 로컬 durable 스풀·캐시 신뢰 가능 (오프라인·재시도 내성)
  - 남은 제약 : VDI GPU 부재 → 무거운 추론(OCR·임베딩·Vision)은 서버로, **클라이언트는 초경량(관측 전용)** 유지

## Client (VDI 내부, .NET 8)

Windows UI Automation

UIA Change Listener (`FocusChanged`·`StructureChanged`·`PropertyChanged`·`Invoke`) + 디바운스

Checkpoint/Delta Encoder + 최근 상태 캐시

Microsoft Graph (Mail, 우선)

Outlook COM (fallback)

File Watcher

Local Privacy Gate (PII 마스킹, 경량)

> OCR·임베딩·Vision은 GPU 부재로 서버에서 수행 (스크린샷 필요 시에만 캡처→전송)

---

## Server (AWS, Tenant VPC)

LLM : **Amazon Bedrock (Claude)**

Embedding : Bedrock (Titan / Cohere)

Vector Store : Aurora PostgreSQL + pgvector (→ 규모 시 OpenSearch)

Knowledge Graph : Aurora PostgreSQL (→ 규모 시 Neptune)

Storage : S3 (SSE-KMS 암호화)

Semantic Engine / Entity Resolver

Business Understanding Service

Workflow Engine (Step Functions)

관측성/보안 : CloudWatch, KMS, IAM, VPC Endpoint, CloudTrail

---

# 13. 보안 정책 대응

Cho-Pilot은

다음 기술을 사용하지 않는다.

- Chrome Extension
- Remote Debugging
- DevTools
- DLL Injection
- Process Hooking

사용 기술

- Windows UI Automation
- Outlook COM
- Microsoft Graph
- OCR
- Vision
- Office Parser

기업 보안 정책 친화적으로 설계한다.

---

# 13-1. 데이터 거버넌스

관측 *방법*뿐 아니라 관측한 *데이터*의 처리도 통제한다.

| 영역 | 정책 |
|------|------|
| 전송 경계 | 모든 데이터 테넌트 VPC 내부. Bedrock은 VPC Endpoint 경유(인터넷 미경유) |
| LLM 데이터 | Bedrock 무학습 정책. 프롬프트/응답 로그 보존기간·암호화 |
| 저장 | S3/Aurora SSE-KMS 암호화, 전송 TLS/mTLS |
| 최소수집 | Privacy Gate에서 PII 마스킹, 화이트리스트 필드만 승격. 변경 없음은 폐기하고 동일 화면은 델타 우선 |
| 동의/투명성 | 관측 범위 고지 UX, on/off·앱별 제외 |
| 보존/파기 | 항목별 TTL, 사용자 삭제 요청 처리 경로 |
| 접근/감사 | IAM 최소권한, 사용자별 격리, 전 액션 감사 |

---

# 13-2. 성공 지표 (KPI)

| 지표 | 목표(초기) |
|------|-----------|
| 관측 정확도 (UI→Business Object) | ≥ 90% |
| 변경 없음 억제율 | ≥ 95% |
| 전체 스냅숏 대비 평균 전송량 절감 | ≥ 70% |
| 체크포인트+델타 상태 재구성 성공률 | 100% |
| 연결 정밀도 (Entity Resolver) | Precision ≥ 0.9 |
| 업무 인식 정확도 | ≥ 80% |
| 자동화 성공률 | ≥ 95% |
| 업무 판단 지연 (p95) | ≤ 3s |
| 활성 사용자당 LLM 비용 | 예산 내 |

---

# 14. 라이선스

| 구성요소 | License | 비용 |
|----------|---------|------|
| Windows UI Automation | Windows 포함 | 무료 |
| MSAA | Windows 포함 | 무료 |
| Outlook COM | Office 포함 | 무료 |
| Microsoft Graph | Microsoft 365 | 대부분 무료 |
| OpenCV | Apache 2.0 | 무료 |
| PaddleOCR | Apache 2.0 | 무료 |
| Tesseract | Apache 2.0 | 무료 |
| ONNX Runtime | MIT | 무료 |

운영 비용은

LLM API 사용량이 대부분을 차지한다.

---

# 15. 개발 단계

> 최난도(융합·거버넌스)를 뒤로 미루지 않기 위해 **위험 검증 Phase 0**를 선행한다.

## Phase 0 — 위험 검증 (4~6주)

**1차 타깃 : 자체 개발 General Procurement 시스템** (소스 접근 불가, 사용자 관점 관측)

- **UIA 관측 정확도 실측** — 핵심 화면 필드 매핑률 (최우선)
- App Adapter 매핑 규칙 PoC
- Local Privacy Gate 검증
- Entity Resolver 결정론 매칭 검증

→ UIA로 필드가 안 읽히면 이후 단계 무의미 → Phase 0에서 반드시 선검증

---

## Phase 1

Enterprise Web Agent

- UI Automation
- 변경 없음 억제(값 포함 지문) 및 안정 노드 키
- 체크포인트+델타·상태 재구성·누락 시 전체 재동기화는 feature flag 단계 배포
- UIA 이벤트 구독 + 디바운스는 앱별 검증 후 폴링에서 단계 전환
- Vision
- Workflow Guide

---

## Phase 2

Enterprise Mail Intelligence

- Outlook
- Mail Parser
- Thread Builder
- Action Extractor

---

## Phase 3

Enterprise Document Intelligence

- Folder Scanner
- Office Parser
- PDF Parser
- Knowledge Graph

---

## Phase 4

Business Understanding Layer

- Work Graph
- Context Engine
- Workflow Reasoning
- Automation Planner

---

# 16. 장기 비전

Cho-Pilot은

단순한 Copilot이 아니다.

Cho-Pilot은

기업 업무를 이해하고

기억하고

연결하고

자동화하는

Enterprise Work Intelligence Platform이다.

```
Web

+

Mail

+

Document

↓

Business Understanding

↓

Knowledge Graph

↓

Workflow

↓

Enterprise AI
```
