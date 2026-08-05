# Cho-Pilot 실행 가이드

설계는 [ARCHITECTURE.md](ARCHITECTURE.md), 개요는 [README.md](README.md).
이 문서는 **띄우고, 넣고, 확인하는** 절차만 담는다.

---

## 0. 한 줄 요약

```bash
dotnet run --project src/ChoPilot.Server      # → http://localhost:5080
```

브라우저로 열면 측정 콘솔이 뜬다. 이 상태는 **인메모리 · 인증 없음 · AI 없음(스텁)** 이다.
상단 배지 두 개가 그걸 항상 알려 준다.

| 배지 | 뜻 |
|---|---|
| `인증 없음 (헤더 자칭)` | `X-ChoPilot-User` 헤더를 그대로 믿는다 — 신뢰 경계 밖에 두지 마라 |
| `인메모리` | 재시작하면 지식·매핑 캐시·감사 로그가 사라진다 |

---

## 1. 준비

| 필요한 것 | 용도 |
|---|---|
| .NET 8 SDK | 전 구성요소 |
| Windows + VDI | `chopilot-dump`(UIA 관측)만 해당. 서버·콘솔은 Linux/macOS에서 돈다 |
| AWS 자격증명 | Bedrock 실추론(`UseBedrock=true`)만 해당 |

```bash
dotnet build ChoPilot.sln                                  # Windows
dotnet build tests/ChoPilot.Tests/ChoPilot.Tests.csproj    # Linux/macOS (클라이언트 제외)
dotnet test  tests/ChoPilot.Tests/ChoPilot.Tests.csproj
```

> `ChoPilot.Client`는 `net8.0-windows`(FlaUI/UIA)라 Linux에서 빌드되지 않는다. CI의 windows 잡이 검증한다.

---

## 2. 실행 모드

### 2.1 로컬 측정 (기본)

```bash
dotnet run --project src/ChoPilot.Server
```

`launchSettings.json`이 `ASPNETCORE_ENVIRONMENT=Development`를 선언하므로 인증 방어선에 걸리지 않는다.

### 2.2 영속 실행 — 재시작에 살아남게

```bash
Storage__Path=/var/lib/chopilot dotnet run --project src/ChoPilot.Server
```

저장소별 `*.jsonl` 저널이 그 디렉터리에 쌓이고 부팅 때 복원된다.
**기반 마스터는 저장하지 않는다** — 외부 출처에서 다시 받는 것이 맞다(ARCHITECTURE §5.6, §12).

### 2.3 인증을 켠 실행

```bash
Auth__Mode=jwt \
Auth__Jwt__SigningKey='<32바이트 이상>' \
Auth__Jwt__Issuer='https://idp.example' \
Auth__Jwt__Audience='chopilot' \
dotnet run --project src/ChoPilot.Server
```

이후 모든 주체 요구 엔드포인트는 `Authorization: Bearer <token>`을 받고, 주체는 토큰의 `sub`다.

### 2.4 운영 배포

`ASPNETCORE_ENVIRONMENT`를 주지 않으면 ASP.NET Core는 **Production**으로 본다.
그 상태에서 `Auth:Mode=header`면 **서버가 뜨지 않는다**(의도된 방어선, ARCHITECTURE §8.1).

```bash
# 최소 구성
ASPNETCORE_ENVIRONMENT=Production
Auth__Mode=jwt
Auth__Jwt__SigningKey=…            # 시크릿 매니저에서. 소스에 커밋하지 마라
Storage__Path=/var/lib/chopilot
```

정말 신뢰 경계 안(VPC/mTLS)이라 헤더 방식을 써야 한다면
`Auth__AllowUnverifiedInProduction=true`로 **명시**해야 한다.

### 2.5 Bedrock 실추론

```bash
UseBedrock=true Aws__Region=ap-northeast-2 dotnet run --project src/ChoPilot.Server
```

기본은 `StubAiMapper`(별칭 매칭)다. 모델 ID는 **inference profile**(`us.`/`global.` 접두)이어야 한다 —
현재 Anthropic 모델은 ON_DEMAND base id로 호출하면 `ResourceNotFoundException`이다.
지식 초안 서술까지 AI로 다듬으려면 `Knowledge__UseEditor=true`를 함께 준다(초안 1건당 1회).

---

## 3. 설정 한눈에

환경변수는 `:` 대신 `__`를 쓴다 (`Auth:Mode` → `Auth__Mode`).

| 키 | 기본 | 뜻 |
|---|---|---|
| `Auth:Mode` | `header` | `header`(검증 없음) \| `jwt` |
| `Auth:AllowUnverifiedInProduction` | `false` | 운영에서 검증 없는 인증을 명시적으로 허용 |
| `Auth:Jwt:SigningKey` / `Issuer` / `Audience` | — | jwt 모드 필수(SigningKey), 나머지는 주면 검증 |
| `Storage:Path` | (없음) | 저널 디렉터리. 비면 인메모리 |
| `UseBedrock` | `false` | 실 AI 추론 |
| `Knowledge:UseEditor` | `false` | 초안 본문을 LLM이 다듬음 (`UseBedrock`도 필요) |
| `Knowledge:MinSupport` / `MinDistinctUsers` | `3` / `2` | 지식 승격 게이트(지지도·k인) |
| `Mapping:ThetaHigh` | `0.8` | 신뢰 임계 θ. 미만은 캐시에 있어도 적중이 아니다 |
| `Mapping:ReinferAfterHours` | `24` | 저신뢰 매핑 재추론 백오프. **0으로 두지 마라** — θ 절벽이 되살아난다 |
| `Foundation:ExchangeRate:Enabled` | `false` | 무료·키 불필요 환율 |
| `Foundation:Holiday:ServiceKey` | — | 공공데이터포털 공휴일 |
| `Foundation:BusinessStatus:ServiceKey` | — | 국세청 사업자등록 상태 |
| `Foundation:Mcp:0:Endpoint` / `Tool` / `Kind` / `KeyArgument` | — | MCP 서버 도구를 기반 출처로 |

> 서비스 키·서명 키는 **환경변수로만** 넣어라. `appsettings.json`에 넣으면 커밋된다.

---

## 4. 관측을 넣는 두 가지 길

### 4.1 Windows 실측 — `chopilot-dump`

```bash
chopilot-dump --delay 5 --out pr-create.snapshot.json     # 캡처만
chopilot-dump --upload http://<서버>:5080                  # 캡처 + 전송
chopilot-dump --completed --upload                        # 저장 버튼 직후 = 작업 완료 신호
```

- `--delay` 동안 관측할 화면을 포그라운드로 둔다.
- `--completed`는 **사람이 붙이는 표시**다. 이 도구는 스냅숏 도구라 클릭을 볼 수 없다.
  저장을 누른 직후에 캡처해야 그 화면이 필수 필드 규칙의 증거가 된다(ARCHITECTURE §5.7).
- 전송 실패분은 durable 스풀에 남아 다음 실행에서 재전송된다.
  서버가 **4xx로 거부**한 것은 재시도하지 않고 `*.bad`로 격리된다 — 큐 머리를 막지 않기 위해서다.

### 4.2 스냅숏 재생 — 측정 콘솔

콘솔 ① 섹션에 `*.snapshot.json`을 끌어다 놓는다. **캡처는 Windows가 필요하지만 재생은 어디서나** 된다.
"재생할 때마다 새 ID 부여"를 켜면 같은 파일을 다시 올려 캐시 적중을 확인할 수 있다.

---

## 5. 5분 점검 절차

서버를 띄운 직후 이 순서로 확인하면 전 구간이 살아 있는지 알 수 있다.

```bash
H=http://localhost:5080
U="X-ChoPilot-User: ops"

curl -s $H/health                     # {"status":"ok"}
curl -s $H/v1/auth                    # method · verified
curl -s $H/v1/storage                 # durable · restored · corrupt
curl -s $H/v1/ontology                # 지식 버전 + 개념 (시드는 8개)
```

스냅숏을 몇 건 올린 뒤:

```bash
curl -s $H/v1/metrics                 # 적중률(H3b) · 지연 p95(H6) · AI 호출 수
curl -s $H/v1/signatures              # route당 서명이 갈렸는지 (적중률 미달의 1차 원인)
curl -s $H/v1/review                  # θ 미만 매핑 = 보정 대상
curl -s $H/v1/entities                # 엔티티 · 공동 출현 · 갈림 후보(H5)
curl -s $H/v1/completions             # 개념별 채움률 (필수 필드 규칙의 증거)
curl -s $H/v1/foundation/reconcile    # 관측 ↔ 기반 마스터 대사
curl -s -H "$U" -X POST "$H/v1/knowledge/aggregate?dryRun=true"   # 어떤 지식 초안이 나오는지
```

콘솔 ⑧에서 **리포트 내려받기(Markdown)** 를 누르면 위 전부가 한 장으로 나온다.
인메모리로 돌고 있다면 세션을 끝내기 전에 반드시 받아라.

---

## 6. 지식 루프 한 바퀴 돌려보기

```bash
H=http://localhost:5080; U="X-ChoPilot-User: ops"

# 1) 온톨로지에 없는 개념으로 세 사람이 보정 시도 → 전부 400이지만 신호는 남는다
for u in alice bob carol; do
  curl -s -o /dev/null -X POST $H/v1/correction -H 'Content-Type: application/json' \
    -H "X-ChoPilot-User: $u" \
    -d '{"signature":"sig-x","businessObject":"PurchaseRequest",
         "mapping":[{"elementRef":"n2","concept":"결제조건"}]}'
done

curl -s $H/v1/knowledge/signals                       # 시도 3회가 후보로 잡힌다
curl -s -X POST "$H/v1/knowledge/aggregate" -H "$U"   # 초안 1건 (민감으로 제안)
curl -s -X POST "$H/v1/knowledge/concept.결제조건/approve" -H "X-ChoPilot-User: kim"
curl -s $H/v1/ontology                                # 버전 +1, 개념 9개
```

승인 뒤 같은 보정을 다시 하면 200이다. **자동 게시는 없다** — 사람 승인이 유일한 통로다.

---

## 7. 자주 걸리는 것

| 증상 | 원인 · 조치 |
|---|---|
| 기동 즉시 종료, `운영 환경에서 Auth:Mode=header는 거부된다` | 의도된 방어선. `Auth__Mode=jwt`로 바꾸거나 `ASPNETCORE_ENVIRONMENT=Development` |
| 모든 요청이 401 | 주체가 없다. header 모드면 `X-ChoPilot-User`, jwt 모드면 `Authorization: Bearer` |
| 적중률이 오르지 않음 | ① `/v1/signatures`에서 route당 서명이 갈렸는지 ② θ 미만 매핑은 캐시에 있어도 적중이 아니다 → 콘솔 ④에서 보정·승격 |
| AI 호출이 관측마다 발생 | `Mapping:ReinferAfterHours=0`이면 θ 절벽이 되살아난다. 기본 24로 두어라 |
| 재시작하면 다 사라짐 | `Storage__Path`가 없다. 상단 배지가 `인메모리`면 그 상태다 |
| 부팅 로그에 `N줄을 건너뛰었다` | 쓰기 도중 종료된 흔적. 그 줄만 폐기됐고 나머지는 무사하다 |
| 관측 POST가 400 | 계약 위반이다. 응답 `detail`에 사유가 있다(`tree.children 없음`, `privacy.maskedRefs 없음` 등) |
| 스풀에 `*.bad`가 쌓임 | 서버가 영구 거부(4xx)한 이벤트다. `detail`을 보고 클라이언트를 고쳐라 |
| 기반 대사가 전부 `no_master` | 출처를 아직 안 붙였다. `Foundation:*` 설정 후 `POST /v1/foundation/refresh` |
| 기반 대사가 `unverifiable` | 마스터 키는 사업자번호인데 관측은 상호명이다. 화면에서 사업자번호를 함께 읽어야 한다 |

---

## 8. 지금 검증되지 않은 것

정직하게 적어 둔다. 아래는 코드가 없는 게 아니라 **이 환경에서 실행해 보지 못한** 것들이다.

| 항목 | 필요한 것 |
|---|---|
| H1·H2·H3 (관측 정확도·식별·매핑 정확률) | Windows VDI + 실제 Procurement 화면 |
| Bedrock 실호출 | AWS 자격증명 |
| 무료 API 3종(환율·공휴일·사업자상태) 실호출 | 해당 호스트로의 아웃바운드 + 서비스 키 |
| 상주 에이전트의 저장 클릭 자동 관측 | Phase 2 (지금은 `--completed`로 사람이 표시) |

MCP 기반 출처는 로컬 MCP 서버를 띄워 **실제 소켓 위에서** 검증했다.
