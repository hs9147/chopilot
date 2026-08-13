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
UseBedrock=true Aws__Region=us-east-1 dotnet run --project src/ChoPilot.Server
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

**원클릭.** 빌드 산출물의 `chopilot-watch.cmd`를 더블클릭하면 10초 간격 자동 관측이 시작된다.
간격은 `CHOPILOT_WATCH_SECONDS` 환경변수로 바꾼다.

**명령줄.**

```bash
<<<<<<< HEAD
chopilot-dump --delay 5 --out pr-create.snapshot.json     # 캡처만
chopilot-dump --upload http://localhost:5080                  # 캡처 + 전송
=======
chopilot-dump --delay 5 --out pr-create.snapshot.json     # 한 번, 캡처만
chopilot-dump --upload http://<서버>:5080                  # 한 번, 캡처 + 전송
chopilot-dump --watch --upload                            # 자동 (10초 간격, Ctrl+C로 중단)
chopilot-dump --watch 30 --rounds 20 --upload             # 30초 간격 20회차
>>>>>>> 51b7a377dedadb67bfc4e9fef2fca28898d6aac5
chopilot-dump --completed --upload                        # 저장 버튼 직후 = 작업 완료 신호
```

- `--delay` 동안 관측할 화면을 포그라운드로 둔다(자동 모드는 첫 회차 전에만).
- **화면이 그대로면 보내지 않는다.** 값까지 포함한 지문으로 판정하므로, 같은 화면에서 수량만
  고쳐도 새 관측이다(서명은 그대로지만 사건은 일어났다). `--resend-unchanged`로 끌 수 있다.
  이 건너뜀이 없으면 아무 일 없는 화면이 계속 쌓여 적중률이 학습이 아니라 반복을 센 값이 된다.
- **동의 게이트는 매 회차 다시 본다.** 도중에 제외 대상 앱으로 옮겨가면 그 회차부터 막힌다.
- 한 회차가 간격보다 오래 걸리면 다음 회차가 밀린다 — 겹쳐 쏘지 않는다.
- 한 회차의 예외(창이 닫히는 등)는 루프를 죽이지 않는다.
- `--bedrock` / `--baseline`은 자동 모드에서 **첫 회차만** 돈다. 매 회차 호출하면 비용이 시간에 비례한다.
- 자동 모드의 `--out`은 회차마다 `<이름>-001.json`으로 쌓인다. stdout 덤프는 생략된다.

**`--completed`는 `--watch`와 함께 쓸 수 없다.** 완료 신호는 "저장을 눌렀다"는 1회성 판단이라,
반복 캡처에 붙이면 작성 중간 화면이 전부 완료로 기록되어 필수 필드 규칙의 증거가 오염된다.
저장 직후에 따로 한 번 실행하라(ARCHITECTURE §5.7). 이 조합은 실행 시 거부된다.

전송 실패분은 durable 스풀에 남아 다음 회차·다음 실행에서 재전송된다.
서버가 **4xx로 거부**한 것은 재시도하지 않고 `*.bad`로 격리된다 — 큐 머리를 막지 않기 위해서다.

### 4.2 스냅숏 재생 — 측정 콘솔

콘솔 ① 섹션에 `*.snapshot.json`을 끌어다 놓는다. **캡처는 Windows가 필요하지만 재생은 어디서나** 된다.
"재생할 때마다 새 ID 부여"를 켜면 같은 파일을 다시 올려 캐시 적중을 확인할 수 있다.

**자동 반복 적재.** 같은 섹션의 체크박스를 켜면 마지막에 올린 파일을 **N초 간격(기본 10초)** 으로
다시 적재한다. 적중률이 언제 안정되는지, θ 절벽과 재추론 백오프가 시간에 따라 어떻게 움직이는지를
손으로 누르지 않고 보기 위한 것이다.

- **화면을 새로 캡처하지는 않는다.** 브라우저는 UIA에 닿을 수 없다 — 캡처는 여전히 `chopilot-dump`다.
- 간격은 1~3600초로 잘린다. 숫자가 아니면 기본값 10으로 돌아간다.
- 회차는 <b>겹치지 않는다</b>. 한 회차(전송 + 조회)가 간격보다 오래 걸리면 다음 회차는 그만큼 밀린다 —
  겹쳐 쏘면 지표가 관측이 아니라 브라우저 폴링 주기를 재게 된다.
- 전 건이 **5회 연속 실패**하면 스스로 멈춘다(서버가 죽은 뒤 로그를 무한히 채우지 않도록).
- 새로고침하면 항상 꺼진 채로 시작한다. 브라우저는 예전에 고른 파일을 다시 읽을 수 없다.

적재 로그는 `캐시 적중` / `캐시 재사용(θ 미만)` / `AI 추론`을 구분해 적는다.
**미스는 AI 호출이 아니다** — 저신뢰 캐시 재사용은 둘 중 어느 쪽도 아니고,
그 구분이 없으면 반복 적재 로그가 매 회차 Bedrock을 부르는 것처럼 보인다.

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
| `--watch`가 계속 "변화 없음"만 찍음 | 정상이다 — 화면이 그대로면 보내지 않는다. 강제로 보내려면 `--resend-unchanged` |
| `--watch`가 "관측 중단"만 찍음 | 동의 게이트가 막고 있다. `Consent:Enabled`와 제외 목록을 확인하라 |
| `--completed --watch`가 거부됨 | 의도된 것이다. 완료 신호는 저장 직후 한 번만 따로 실행한다 |
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
