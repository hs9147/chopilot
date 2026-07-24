# Cho-Pilot
## Enterprise Work Intelligence Platform

> **Screen을 이해하는 AI를 넘어 Business를 이해하는 AI**

Version : 1.0  
Status : Proposal

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

# 11. Workflow Engine

Workflow

Guide

↓

Validation

↓

Automation

↓

Approval

↓

History

---

# 12. 기술 구성

## Client

Windows UI Automation

Vision

OCR

OpenCV

ONNX Runtime

Outlook COM

Microsoft Graph

Office Parser

---

## Server

LLM

Embedding

Knowledge Graph

Workflow Engine

Semantic Search

Memory

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

## Phase 1

Enterprise Web Agent

- UI Automation
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