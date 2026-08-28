# Cho-Pilot 실행 가이드

설계는 [ARCHITECTURE.md](ARCHITECTURE.md), 개요는 [README.md](README.md).
이 문서는 **띄우고, 넣고, 확인하는** 절차만 담는다.
Production 전환·Canary·롤백은 [OPERATIONS-TRANSITION-GUIDE.md](OPERATIONS-TRANSITION-GUIDE.md)를 따른다.

---

## 0. 한 줄 요약

```bash
dotnet run --project src/ChoPilot.Server      # → http://localhost:5080
```

브라우저로 열면 측정 콘솔이 뜬다. 이 상태는 **인메모리 · 인증 없음 · AI 없음(스텁)** 이다.
상단 배지 세 개가 그걸 항상 알려 준다.

| 배지 | 뜻 |
|---|---|
| `LLM 스텁 (Azure OpenAI 미설정)` | 기본 공급자는 `azure_openai`인데 설정이 없어 스텁으로 내려앉았다 |
| `인증 없음 (헤더 자칭)` | `X-ChoPilot-User` 헤더를 그대로 믿는다 — 신뢰 경계 밖에 두지 마라 |
| `인메모리` | 재시작하면 지식·매핑 캐시·감사 로그가 사라진다 |

---

## 1. 준비

| 필요한 것 | 용도 |
|---|---|
| .NET 8 SDK | 전 구성요소 |
| Windows + VDI | `chopilot-dump`(UIA 관측)만 해당. 서버·콘솔은 Linux/macOS에서 돈다 |
| 클라우드 자격증명 | 선택한 LLM 공급자: Bedrock(IAM), Vertex AI(ADC), Azure OpenAI(API key 또는 Entra bearer) |

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

### 2.5 LLM 공급자 선택

**기본은 `azure_openai`다.** `Llm:Provider`를 비워 두면 Azure OpenAI를 고른다
(`UseBedrock=true`를 적어 둔 기존 설정은 그대로 Bedrock을 고른다).
명시적으로 선택하면 `stub | bedrock | vertex | azure_openai` 중 하나여야 한다 —
`azure` · `vertex_ai` 같은 표기도 받는다.

**기본값이 준비돼 있지 않으면 스텁으로 내려앉는다.** 설정 없는 서버가 기동조차 못 하면
`dotnet run` 한 줄로 콘솔을 여는 길이 막히고 테스트도 자격증명을 요구하게 된다.
대신 조용히 내려앉지는 않는다 — 기동 로그가 경고하고, `/v1/llm`과 상단 배지가 이유를 적는다:

```bash
curl -s localhost:5080/v1/llm
# {"provider":"stub","requested":"azure_openai","fellBack":true,
#  "fallbackReason":"azure_openai 설정이 없어 스텁으로 돈다 — Llm:AzureOpenAI:Endpoint는 https 절대 URL이어야 한다"}
```

**반대로 직접 적어 둔 공급자의 설정이 틀리면 기동을 거부한다.** `Llm:Provider=azure_openai`라고
써 놓고 엔드포인트를 빼먹었다면 그건 오타지 기본값이 아니다 — 조용히 스텁으로 도는 서버는
"AI를 붙였다"고 믿는 사람에게 거짓말을 한다.

#### Bedrock

```bash
UseBedrock=true Aws__Region=us-east-1 dotnet run --project src/ChoPilot.Server
```

기본은 `StubAiMapper`(별칭 매칭)다. 모델 ID는 **inference profile**(`us.`/`global.` 접두)이어야 한다 —
현재 Anthropic 모델은 ON_DEMAND base id로 호출하면 `ResourceNotFoundException`이다.
지식 초안 서술까지 AI로 다듬으려면 `Knowledge__UseEditor=true`를 함께 준다(초안 1건당 1회).

#### Vertex AI — ADC

Vertex는 API key/서비스 계정 JSON을 앱 설정으로 읽지 않고 표준 **Application Default Credentials**를
쓴다. 개발 PC에서는 먼저 `gcloud auth application-default login`을 실행하고, 운영에서는 Workload
Identity 또는 런타임 service account에 `aiplatform.models.predict` 권한을 부여한다.

```bash
Llm__Provider=vertex \
Llm__Vertex__ProjectId=my-gcp-project \
Llm__Vertex__Location=us-central1 \
Llm__Vertex__Model=gemini-2.5-flash \
dotnet run --project src/ChoPilot.Server
```

#### Azure OpenAI — deployment endpoint

Azure OpenAI는 모델명이 아니라 **deployment**를 호출한다. API key 또는 Entra bearer token 중 하나만
시크릿 매니저에서 주입한다.

```bash
Llm__Provider=azure_openai \
Llm__AzureOpenAI__Endpoint=https://<resource>.openai.azure.com \
Llm__AzureOpenAI__Deployment=<deployment> \
Llm__AzureOpenAI__ApiVersion=2024-10-21 \
Llm__AzureOpenAI__ApiKey='<secret>' \
dotnet run --project src/ChoPilot.Server
```

`Knowledge__UseEditor=true`이면 선택한 Bedrock·Vertex·Azure 공급자가 지식 초안 서술에도 쓰인다.

##### `Endpoint`는 어디까지인가 — 그리고 404가 났을 때

`Endpoint`는 **리소스 호스트까지**만 적는다. 나머지 경로는 서버가 붙인다:

```
{Endpoint}/openai/deployments/{Deployment}/chat/completions?api-version={ApiVersion}
```

| 적은 값 | 실제로 보내는 경로 |
|---|---|
| `https://<resource>.openai.azure.com` | `/openai/deployments/<d>/chat/completions?api-version=…` ✅ |
| `https://<resource>.openai.azure.com/` | 위와 동일 (후행 `/`는 잘라낸다) ✅ |
| `https://<resource>.openai.azure.com/openai/v1` | `/openai/v1/openai/deployments/…` ❌ 404 |

경로를 붙여도 **기동은 통과한다** — https 절대 URL이면 형식상 맞기 때문이다. APIM 같은
게이트웨이를 앞에 두면 경로 접두사가 정당한 경우도 있어서 기동 시점에 거부하지 않는다.
대신 **첫 관측에서 나는 404가 스스로를 설명한다**: 예외 문구에 공급자가 보낸 원인 코드와
우리가 실제로 요청한 URL이 함께 실린다.

```
Azure OpenAI returned 404 (Not Found) for POST
https://<resource>.openai.azure.com/openai/v1/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21:
{"error":{"code":"DeploymentNotFound","message":"The API deployment for this resource does not exist. …"}}
```

`/openai`가 두 번 들어간 것이 그 한 줄에서 보인다. 404가 났을 때 위에서부터 짚는다.

1. **`/openai`가 두 번** 나오면 `Endpoint`에서 경로를 뗀다.
2. `DeploymentNotFound` — `Deployment`는 **모델명이 아니라 배포 이름**이다.
   Azure AI Foundry의 *Deployments* 목록에 뜨는 이름을 그대로 쓴다 (`gpt-4o`가 아니라
   `gpt-4o-prod`처럼 직접 지은 이름인 경우가 많다).
3. 호스트가 `*.services.ai.azure.com`이면 **다른 리소스 종류**다. Azure OpenAI 리소스의
   `*.openai.azure.com` 엔드포인트를 쓴다.
4. `api-version`이 그 경로를 모를 수도 있다. 리소스가 지원하는 버전으로 바꿔 본다.
5. 이 코드베이스는 **classic deployment 경로만** 말한다. 신형 `/openai/v1` 서피스는
   구현돼 있지 않으므로 `Endpoint`에 그 경로를 넣어도 닿지 않는다.

서버를 거치지 않고 한 줄로 같은 것을 확인할 수 있다.

```bash
curl -sS -o /dev/null -w '%{http_code}\n' \
  -X POST "$ENDPOINT/openai/deployments/$DEPLOYMENT/chat/completions?api-version=2024-10-21" \
  -H "api-key: $KEY" -H 'Content-Type: application/json' \
  -d '{"messages":[{"role":"user","content":"ping"}],"max_tokens":1}'
```

Vertex AI도 같은 방식으로 실패를 설명한다 — 403이면 문구에 `PERMISSION_DENIED`와 함께
`projects/…/locations/…/models/…` 경로가 실려서, 권한 문제인지 프로젝트·리전이 다른 것인지
바로 갈린다.

---

## 3. 설정 한눈에

환경변수는 `:` 대신 `__`를 쓴다 (`Auth:Mode` → `Auth__Mode`).

| 키 | 기본 | 뜻 |
|---|---|---|
| `Auth:Mode` | `header` | `header`(검증 없음) \| `jwt` |
| `Auth:AllowUnverifiedInProduction` | `false` | 운영에서 검증 없는 인증을 명시적으로 허용 |
| `Auth:Jwt:SigningKey` / `Issuer` / `Audience` | — | jwt 모드 필수. Production에서는 모두 필수이며 issuer/audience를 검증 |
| `Storage:Path` | (없음) | 저널 디렉터리. 비면 인메모리 |
| `UseBedrock` | `false` | 실 AI 추론 |
| `Llm:Provider` | (빈 값 → **`azure_openai`**) | `stub` \| `bedrock` \| `vertex` \| `azure_openai`. 미설정 기본값이 준비 안 되면 스텁으로 내려앉고 `/v1/llm`에 이유가 남는다 |
| `Llm:Vertex:ProjectId` / `Location` / `Model` | — | Vertex AI `generateContent`. 인증은 **GCP ADC 체인** — 키 파일을 설정에 넣지 않는다 |
| `Llm:AzureOpenAI:Endpoint` / `Deployment` / `ApiVersion` | — | Azure OpenAI Chat Completions deployment |
| `Llm:AzureOpenAI:ApiKey` / `BearerToken` | — | 둘 중 정확히 하나만. 소스·설정 파일에 커밋 금지 |
| `Knowledge:UseEditor` | `false` | 초안 본문을 선택한 Bedrock·Vertex·Azure LLM이 다듬음 |
| `Knowledge:MinSupport` / `MinDistinctUsers` | `3` / `2` | 지식 승격 게이트(지지도·k인) |
| `Mapping:ThetaHigh` | `0.8` | 신뢰 임계 θ. 미만은 캐시에 있어도 적중이 아니다 |
| `Mapping:ReinferAfterHours` | `24` | 저신뢰 매핑 재추론 백오프. **0으로 두지 마라** — θ 절벽이 되살아난다 |
| `Foundation:ExchangeRate:Enabled` | `false` | 무료·키 불필요 환율 |
| `Foundation:Holiday:ServiceKey` | — | 공공데이터포털 공휴일 |
| `Foundation:BusinessStatus:ServiceKey` | — | 국세청 사업자등록 상태 |
| `Foundation:Mcp:0:Endpoint` / `Tool` / `Kind` / `KeyArgument` | — | MCP 서버 도구를 기반 출처로 |
| `Limits:IngestionPerMinute` | `120` | 적재 속도 상한. 넘으면 **429** |
| `Llm:TimeoutSeconds` | `45` | LLM 호출 타임아웃 (5~300으로 절단) |

> 서비스 키·서명 키는 **환경변수로만** 넣어라. `appsettings.json`에 넣으면 커밋된다.

**반복 관측과 적재 상한.** `--watch`와 콘솔의 자동 반복은 둘 다 이 상한에 걸릴 수 있다.
분당 120건이 기본이므로 `--watch 1`(회차당 1건)은 여유가 있지만, 여러 대가 동시에 붙거나
콘솔 반복을 같이 돌리면 넘길 수 있다. 429는 **재시도 가능한 실패**로 분류되므로 이벤트는
유실되지 않고 스풀에 남았다가 다음 회차에 나간다 — 다만 그 상태가 길어지면 상한을 올리거나
간격을 늘려야 한다.

---

## 4. 관측을 넣는 두 가지 길

실측(Windows UIA)과 재생(콘솔). Windows에서 처음부터 훑는 순서는 §5에 따로 있다.

### 4.1 Windows 실측 — `chopilot-dump`

**원클릭.** 빌드 산출물의 `chopilot-watch.cmd`를 더블클릭하면 10초 간격 자동 관측이 시작된다.
간격은 `CHOPILOT_WATCH_SECONDS` 환경변수로 바꾼다.

**명령줄.**

```bash
chopilot-dump --delay 5 --out pr-create.snapshot.json     # 한 번, 캡처만
chopilot-dump --upload http://<서버>:5080                  # 한 번, 캡처 + 전송
chopilot-dump --watch --upload                            # 자동 (10초 간격, Ctrl+C로 중단)
chopilot-dump --watch 30 --rounds 20 --upload             # 30초 간격 20회차
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

### 4.3 측정자 ID

상단바의 **측정자** 칸이다. 보정·승격(④), 집계(⑤), 출처 갱신(⑥)이 전부 이 이름으로 남는다
(`X-ChoPilot-User`). 비어 있으면 칸이 노랗게 표시되고 그 버튼들은 동작하지 않는다.

`Auth:Mode=header`에서는 **검증되지 않는다** — 옆의 인증 배지가 그걸 말한다.
`Auth:Mode=jwt`로 돌리면 콘솔은 토큰을 갖고 있지 않으므로 쓰기가 전부 401이 된다.
콘솔은 신뢰 경계 안(개발·측정)에서 쓰는 물건이다.

### 4.4 겹쳐 누르면 두 번째는 거절된다 (409)

**출처 갱신**(⑥)과 **집계 → 검수 큐**(⑤)는 한 번에 하나만 돈다. 이미 도는 중에 들어온 요청은
`409`로 거절된다 — 콘솔에는 `이미 실행 중이다 — 끝나면 다시 눌러라`로 뜬다.

바깥으로 비용이 나가기 때문이다. 출처 갱신은 무료 API의 일일 할당량을 쓰고, 집계는
`Knowledge:UseEditor=true`일 때 **초안 1건당 LLM을 1회** 부른다. 두 경로 모두 "이미 존재" 판정이
호출보다 뒤에 있어서, 겹쳐 들어오면 같은 일을 두 번 하고 나서야 두 번째가 걸린다.

버튼도 진행 중에는 비활성으로 바뀌지만 그건 **피드백일 뿐 가드가 아니다** — 탭 두 개,
요청 중 새로고침, 직접 호출은 버튼을 지나가지 않는다. 실제로 막는 건 서버의 409다.

> **미리보기(`dryRun`)는 막히지 않는다.** 쓰지도, 밖으로 호출하지도 않으므로 집계가 도는 동안에도 볼 수 있다.

### 4.5 AI 판단을 읽는 법

AI가 내리는 판단은 두 가지다 — **이 화면은 어떤 업무객체인가**, **각 칸은 어떤 개념인가**.
둘 다 ④ 검수 큐와 ⑦ 상세에서 같은 모양으로 본다.

```
화면의 칸   화면의 값        읽은 개념            신뢰도                  어디서 왔나
거래처      ㈜대한           거래처 (Vendor)      0.60 θ 0.8 미만·적중 안 됨  스텁(모의 AI)
총액        [마스킹]         금액 (TotalAmount)   0.60 θ 0.8 미만·적중 안 됨  스텁(모의 AI)
```

읽는 순서는 왼쪽에서 오른쪽이다. **화면에 무엇이 있었고 → AI가 무엇이라고 읽었고 → 그 판단이
채택되는지**. 판단이 틀렸으면 "읽은 개념" 칸을 고쳐 **보정**, 맞으면 **승격**한다.

- **개념은 한국어와 정규명을 함께** 보여준다. 온톨로지의 정규명은 영문(`Vendor`)이지만 화면은
  한국어(`거래처`)다. 정규명만 보이면 판단을 검수하려고 온톨로지를 따로 뒤져야 한다.
- **신뢰도는 θ 판정과 같이** 나온다. `0.60`은 그 자체로 통과인지 탈락인지 말해 주지 않는다 —
  `Mapping:ThetaHigh`가 0.5면 통과, 0.8이면 탈락이다.
- **마스킹된 칸은 값 대신 `마스킹`** 이 뜬다. 검수 화면이 마스킹 방어선을 우회하는 창구가 되면 안 된다.
- **"어디서 왔나"** 는 그 매핑의 출처다 — `AI 추론` / `스텁(모의 AI)` / `캐시에서 재사용` / `사람이 보정` / `시드`.
  `UseBedrock=false`면 전부 **스텁**이다. 스텁 신뢰도는 0.6이라 기본 θ(0.8)를 못 넘는다(측정 함정 T1).

⑦ 목록의 **판단 출처** 열은 회차 단위로 `캐시 적중` / `캐시 재사용(θ 미만)` / `AI 추론` 세 갈래다.
두 갈래로 뭉치면 재추론 보류가 AI 호출로 읽혀 ②의 `AI 호출` 수치와 어긋난다.

### 4.6 추정 이력 — 수정과 제외 ⇄ 포함

④ 아래쪽 **AI 추정 이력**은 지금 캐시에 서 있는 판단 **전부**다. 검수 큐와 다르다:

| | 검수 큐 | 추정 이력 |
|---|---|---|
| 범위 | θ 미만(`pending_review`)만 | `trusted`까지 전부 |
| 정렬 | 신뢰도 낮은 순 (작업 목록) | 최근 추론순 (대장) |
| 개인 스코프 | 안 보임 | **본인 것만** 보임 |

승격·보정으로 큐에서 빠진 판단도 계속 쓰인다. 그것까지 보이지 않으면 "AI가 무엇을 결정해
두었나"를 볼 방법이 없다. `추론 시각`이 `사람이 만듦`이면 AI가 아니라 보정·승격으로 생긴 것이다.

**수정**은 위 보정 폼을 연다 (개념을 고쳐 개인 스코프에 심거나, 그대로 승격).

**제외 ⇄ 포함**은 그 판단을 쓸지 말지를 뒤집는 **스위치**다. 지우지 않으므로 언제든 되돌린다.

- 제외하면 캐스케이드가 그 엔트리를 **건너뛴다**. 좁은 스코프를 꺼도 넓은 스코프는 그대로 쓴다 —
  끈 것은 그 스코프의 판단이지 화면 전체가 아니다.
- **전역 판단이 꺼져 있으면 AI를 새로 부르지 않는다.** 새 추론은 같은 자리(`global`)에 쓰이므로
  여기서 부르면 꺼 둔 원본을 덮어써 되돌릴 것이 없어진다. 그 관측의 판단 출처는 `excluded`로
  기록된다 — 적중도 호출도 아니다.
- 다시 **포함**하면 그 판단이 곧바로 살아난다. 재추론하지 않으므로 **비용이 들지 않는다**.
- 공용 스코프(`global`·`org:*`)를 뒤집으면 **모두에게 영향이 간다**. 확인을 한 번 묻고,
  누가 했는지 아래 `최근 결정`에 `inference_exclude` / `inference_include`로 남는다.
  개인 스코프는 본인에게만 가므로 묻지 않는다 — 되돌릴 수 없는 것에만 물어야 확인이 신호로 남는다.
- **남의 개인 스코프는 건드릴 수 없다** — 404로 떨어지고 존재 여부도 알려주지 않는다.

제외된 판단은 목록에서 **회색 행 + `제외됨` 배지 + `포함` 버튼**으로 남는다. 지워지지 않으니
`서 있는 추정 N건`도 줄지 않는다. 꺼진 판단은 고칠 수 없어 `수정`은 비활성이다 —
고치려면 먼저 포함해야 한다.

위쪽 칩으로 **전체 / 검수 대기 / 채택됨 / 제외됨**을 거른다. 옆에 `서 있는 추정 N건`이 뜨고,
서버가 자른 경우 `N건 중 최근 M건만 실렸다`로 **잘린 사실을 적는다** — 조용히 자르면
"이게 전부"로 읽힌다. 기본 상한은 200건이고 `?limit=`으로 바꾼다.

측정자 ID가 비어 있으면 이력이 뜨지 않는다. 개인 스코프를 본인 것만 실으려면 주체가 필요하다.

---

## 5. Windows에서 처음부터 — 순서대로

관측은 Windows에서만 된다(UIA). 서버와 콘솔은 어디서 돌아도 되지만, 아래는 **한 대에서 전부**
돌리는 가장 짧은 경로다. VDI 안에서 이 순서대로 하면 된다.

### ① .NET 8 SDK

```powershell
dotnet --version    # 8.x 가 나오면 건너뛴다
```

없으면 관리자 권한 없이 사용자 로컬 설치:

```powershell
iwr https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir $env:LOCALAPPDATA\Microsoft\dotnet
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"

# 이 두 줄을 빠뜨리면 chopilot-dump.exe 가 런타임을 못 찾는다 (아래 설명)
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
setx DOTNET_ROOT "$env:LOCALAPPDATA\Microsoft\dotnet"
```

> **`DOTNET_ROOT`를 왜 굳이 박는가.** 비표준 위치 설치는 `dotnet run`만 고친다.
> `dotnet.exe`는 .NET 설치 폴더 **안에** 있어서 자기 옆에서 `hostfxr.dll`을 찾지만,
> `chopilot-dump.exe`는 남의 폴더에 있는 apphost라 밖에서 찾아야 한다 —
> `DOTNET_ROOT` → 레지스트리 `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64` →
> `C:\Program Files\dotnet` 순서다. 사용자 로컬 설치는 셋 다 비므로
> **`dotnet run`은 되는데 exe만 `hostfxr.dll not found`로 죽는다.**
> `setx`는 다음에 여는 창부터 적용된다 — `.cmd` 더블클릭도 그때부터 산다.
>
> 아예 이 탐색을 없애려면 ⑤의 self-contained 배포를 쓴다. VDI 배포는 그쪽이 기본이다.

### ② 빌드

```powershell
git clone https://github.com/hs9147/chopilot.git
cd chopilot
dotnet build ChoPilot.sln -c Release
```

Windows에서만 솔루션 전체(클라이언트 포함)가 빌드된다. Linux/macOS는 `ChoPilot.Client`가
`net8.0-windows`라 제외된다.

### ③ 서버 띄우기

```powershell
dotnet run --project src\ChoPilot.Server
```

`http://localhost:5080` 에서 측정 콘솔이 뜬다. **이 창은 켜 둔 채로** 다음 단계로 간다.

영속화까지 켜려면(재시작에도 지식·캐시가 남는다):

```powershell
$env:Storage__Path = "$env:LOCALAPPDATA\ChoPilot\store"
dotnet run --project src\ChoPilot.Server
```

### ④ 클라이언트에 서버 주소 알려주기

같은 PC면 기본값(`http://127.0.0.1:5080`)이라 생략해도 된다.
다른 PC의 서버를 볼 때만:

```powershell
copy src\ChoPilot.Client\appsettings.local.example.json src\ChoPilot.Client\appsettings.local.json
notepad src\ChoPilot.Client\appsettings.local.json
```
```jsonc
{ "Server": { "IngestionEndpoint": "http://<서버IP>:5080" } }
```

이 파일은 `.gitignore` 대상이라 커밋되지 않는다.

### ⑤ 관측 — 원클릭

관측기를 한 폴더에 모은다. **VDI에서는 `--self-contained`를 쓴다:**

```powershell
dotnet publish src\ChoPilot.Client -c Release -r win-x64 --self-contained -o C:\ChoPilot
```

런타임이 폴더 안에 통째로 들어가 `hostfxr.dll`이 exe 옆에 생긴다 → ①의 탐색 순서를
아예 거치지 않는다. 폴더가 200MB쯤 되지만, .NET 설치 상태가 PC마다 다른 사내 VDI에서
유일하게 안 막히는 길이다. **폴더째** 옮겨야 한다 — exe 하나만 떼면 같은 오류가 난다.

이제 탐색기에서 **`C:\ChoPilot\chopilot-watch.cmd` 를 더블클릭**한다.

5초 뒤부터 10초 간격으로 포그라운드 창을 관측한다. **그 5초 안에 Procurement 화면을 앞으로
두면 된다.** 화면이 그대로면 보내지 않는다. 중단은 Ctrl+C 또는 창 닫기.

간격을 바꾸려면 실행 전에:

```powershell
$env:CHOPILOT_WATCH_SECONDS = "30"
```

개발 중이라 매번 배포하기 번거로우면 빌드 산출물 폴더에서 바로 실행해도 된다 —
①의 `DOTNET_ROOT`가 잡혀 있어야 한다:

```
src\ChoPilot.Client\bin\Release\net8.0\chopilot-watch.cmd
```

### ⑥ 관측 — 명령줄

```powershell
# 한 번만
dotnet run --project src\ChoPilot.Client -- --delay 5 --upload

# 자동 반복
dotnet run --project src\ChoPilot.Client -- --watch --upload

# 저장 버튼을 누른 직후 (작업 완료 신호) — 반복 모드와 함께 쓸 수 없다
dotnet run --project src\ChoPilot.Client -- --delay 3 --completed --upload
```

⑤로 배포한 폴더에서는 `dotnet` 없이 exe를 직접 부른다:

```powershell
C:\ChoPilot\chopilot-dump.exe --watch --upload
C:\ChoPilot\chopilot-dump.exe --delay 3 --completed --upload
```

### ⑦ 확인

브라우저에서 `http://localhost:5080` 을 열고 ② 지표가 올라가는지 본다.
상단 배지 두 개(`인증 없음` / `인메모리` 여부)로 지금 어떤 모드인지 항상 알 수 있다.

관측이 안 잡히면 §9 "자주 걸리는 것"의 "트리에 값이 거의 안 잡힘" 항목부터 본다.

---

## 6. 5분 점검 절차

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

## 7. 업무 개선 제안 (⑨)

쌓인 작업 이력에서 **사람이 할 일**을 뽑는다. ⑤ 지식과 다른 통이다 —
지식은 승인되면 시스템 동작이 바뀌지만, 제안은 **승인해도 시스템은 그대로다**.

```bash
curl -s -X POST -H 'X-ChoPilot-User: ops' localhost:5080/v1/proposals/generate | jq
```

**LLM은 여기 없다.** 후보 산출도 선정도 결정적 집계이고, 본문은 근거 숫자를 그대로 문장에
넣어 만든다. 모델이 문구를 쓰면 관측되지 않은 주장이 근거를 단 문장으로 섞여 들어오고,
읽는 사람은 그 둘을 구분할 수 없다.

### 7.1 무엇을 보는가

| 종류 | 입력 | 무엇을 말하나 |
|---|---|---|
| `screen_split` | 관측 서명 | 한 route가 여러 서명으로 갈렸다 — 적중률에 구조적 상한이 생긴다 |
| `rework` | UEP 전이 | A↔B 왕복이 잦다 — 앞 화면에 필요한 정보가 없다 |
| `workflow_shortcut` | UEP 전이 | 같은 전이를 반복한다 — 바로가기·일괄처리 후보 |
| `master_gap` | 대사 결과 | 반복 관측되는데 마스터에 없다 |
| `correction_hotspot` | 결정 이력 | 한 화면에서 보정이 반복된다 — AI가 계속 틀린다 |

### 7.2 선정 기준과 자체 평가

제안은 네 축을 가중 합산한 점수와 **종류별 문턱**으로 걸러진다.

| 축 | 뜻 | 포화점 |
|---|---|---|
| 근거 | 관측 횟수 | 문턱의 2배 |
| 도달 | 몇 명에게 걸리나 | 문턱의 2배 |
| 최근성 | 마지막 관측이 얼마나 최근인가 | 반감기 14일 |
| 영향 | 종류별 파급 | — |

포화점을 **문턱의 2배**로 두는 이유: 문턱을 겨우 넘긴 근거가 만점이 되면 점수가 게이트와
같은 말을 두 번 하게 되고, 그 위에서 순위를 매길 수 없다.

**도달(몇 명)은 사생활 경계이기도 하다.** 한 사람의 관측만으로 만든 제안은 그 사람의 업무
습관을 팀에 드러낸다. 그래서 흐름·되돌아오기는 최소 2명이고, 화면 갈림은 구조적 사실이라 1명이다.

### 7.3 사용자 평가가 기준을 고친다

**기준을 움직이는 것은 평가이지 채택 여부가 아니다.** "근거는 맞지만 지금 손댈 수 없다"는
기각이 흔한데, 그걸 쓸모없음으로 세면 **옳게 찾아낸 종류의 문턱이 올라간다**.
채택률은 표에 참고로만 보인다. **평가 없이 결정하면 그 건은 학습에 쓰이지 않는다.**

**평가는 세 축이고 척도는 0부터다.** 한 숫자로 뭉치지 않는 이유는 셋이 **서로 다른 결정을
이끌기 때문**이다. 0은 "전혀 아니다"이고 1과 다른 판단이다 — 1부터 시작하면 완전한 부정을
표현할 칸이 없어 최저점이 두 뜻을 겸한다.

| 축 | 묻는 것 | 낮으면 |
|---|---|---|
| **정확성** | 이런 현상이 실제로 있나 | 생성기가 **없는 것을 말한다.** 문턱을 올려도 소용없다 — 틀린 것 중 점수 높은 것이 남을 뿐이라 **그 종류를 끈다** |
| **유용성** | 알 가치가 있나 | 사실이지만 쓸모없다 — **문턱을 올려 걸러낸다** |
| **실행 가능성** | 우리가 할 수 있나 | 맞고 유용하지만 못 한다 — **기준을 움직이지 않는다.** 조직 사정이지 생성기 품질이 아니다 |

**종합평가는 세 항목의 기하평균이다. 하나라도 0이면 0이다.**

```
종합평가 = ∛(정확성 × 유용성 × 실행 가능성)
```

세 축은 더해서 메우는 관계가 아니라 **전부 있어야** 성립하는 조건이기 때문이다 —
사실이 아니거나, 알 가치가 없거나, 아예 할 수 없으면 나머지가 만점이어도 제안으로서 값이 없다.
산술평균이면 `5·5·0`이 3.3으로 남아 "괜찮은 제안"처럼 보인다.
곱이라 **치우침에도 벌점이 붙는다**: `5·5·1`은 기하 2.92, 산술 3.67이다.

> 실행 가능성이 종합평가에 들어가므로 **문턱을 끌어올린다.** 다만 종합평가가 낮은 원인이
> 실행 가능성뿐이면(정확성·유용성이 3.0 이상) **그 종류를 끄지는 않는다** — 끄면 다시 제안되지
> 않아 조직 사정이 풀렸을 때 그 사실을 알 방법이 사라진다. 문턱은 이미 상한이라 억제는 걸려 있다.

**종류별 문턱** — 정확성 게이트를 먼저 보고, 통과하면 종합평가로 움직인다.

| 조건 | 동작 |
|---|---|
| 평가 5건 미만 | 건드리지 않는다 |
| **정확성 평균 < 2.0** | **그 종류를 끈다** — 문턱을 올리는 우회로를 거치지 않는다 |
| 종합평가 < 2.5 | 문턱 +0.10 (상한 0.80) |
| 상한에서도 < 2.5 | 그 종류를 끈다 (원인이 실행 가능성뿐이면 제외) |
| 종합평가 > 3.8 | 문턱 −0.05 (하한 0.20) |
| 2.5 ~ 3.8 | 흔들지 않는다 |

표본이 얇을 때 건드리지 않는 것이 가장 중요한 규칙이다. 두세 번 낮게 평가됐다고 종류를 끄면
그 종류는 다시 제안되지 않으므로 **그 평가가 맞았는지 확인할 표본이 영원히 늘지 않는다.**

**축 가중치** — **유용성**과 같이 움직인 축을 올리고, 반대로 움직인 축을 내린다.

목표를 유용성으로 잡는 이유: 정확성은 생성기 품질이고, 축 가중치가 정하는 것은
"어떤 것이 알 가치가 있는가"이므로 그 축과 맞춰야 한다.

어느 축이 실제로 유용성을 예측하는지는 시드에서 **추측한 값**이다. 사람이 매긴 점수와 축값의
상관이 그 추측을 관측으로 바꾼다. 가중치는 **모든 종류에 걸리는** 변경이라 조건이 빡빡하다:

- 평가 **8건 이상** (문턱은 5건)
- 상관 **0.3 이상** — 약한 상관은 표본 잡음과 구분되지 않는다
- 한 번에 **0.05만** — 크게 움직이면 다음 회차가 되돌리며 진동한다
- 축값이나 유용성이 전부 같으면 **상관을 말할 수 없다**(0이 아니라 미정). 건드리지 않는다
- 조정 후 합을 1로 정규화 — 눈금이 회차마다 달라지면 총점을 비교할 수 없다

기준은 **덮이지 않고 쌓인다**(`GET /v1/proposals/criteria`). 제안은 자기가 통과한 기준 버전을
들고 있어 나중에 그 시점의 잣대로 되짚을 수 있다.

```bash
curl -s -X POST -H 'X-ChoPilot-User: hong' -H 'Content-Type: application/json' \
  -d '{"accept":false,
       "rating":{"accuracy":1,"usefulness":4,"actionability":3},
       "note":"이런 왕복은 실제로 없다"}' \
  localhost:5080/v1/proposals/<id>/decide
```

> 위 조합의 종합평가는 `∛(1×4×3) ≈ 2.29`다. 정확성 축이 별도 게이트로 먼저 잡아내 그 종류를 끈다:
> `정확성 1.0 < 2.0 — 없는 현상을 말한다 (6건 평가)`

- 각 축은 **0~5**여야 한다. 벗어나면 400이고(어느 축인지 이름으로 알려준다) 제안은 결정되지
  않은 채 남는다 — 조용히 잘라 넣으면 학습이 없는 값으로 돌아간다.
- **결정된 제안은 다시 올라오지 않는다.** 두 번째 결정은 404다 — 결정이 뒤집히면 평균이 흔들린다.
- 결정은 `proposal_accepted` / `proposal_rejected`로 세 축과 함께 결정 이력에 남는다.
- 생성을 겹쳐 누르면 **409**다(§4.4와 같은 게이트).

### 7.4 탈락한 후보

게이트에 걸린 후보는 **사유와 함께** 돌아온다(`skipped`). 제안이 0건일 때 근거가 없어서인지
기준이 높아서인지 구분되어야 한다. 사유는 실패한 숫자를 그대로 적는다 —
`관측 2회 < 기준 6회`, `1명 < 기준 2명 (한 사람의 습관과 구분되지 않는다)`.

> 탈락 목록은 **서버가 보관하지 않는다.** 생성 응답에만 담기므로 콘솔을 새로고침하면 사라진다.

---

## 8. 지식 루프 한 바퀴 돌려보기

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

## 9. 자주 걸리는 것

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
| 트리에 값이 거의 안 잡힘 | 브라우저 접근성 트리 미노출. 대상 창을 **실제 포그라운드**로 두어라. Chrome은 자동 활성이지만 안 되면 `--force-renderer-accessibility`로 실행 |
| `chopilot-watch.cmd` 더블클릭 시 창이 바로 닫힘 | `chopilot-dump.exe`가 옆에 없다. `dotnet build` 산출물 폴더(`bin\...\net8.0\`)에서 실행하거나 §5 ⑤로 한 폴더에 모아라 |
| **`hostfxr.dll not found`** (`dotnet run`은 되는데 exe만 실패) | apphost가 .NET 설치 위치를 못 찾는다. `dotnet.exe`는 자기 옆에서 찾고 exe는 밖에서 찾기 때문이다. `setx DOTNET_ROOT "<dotnet --info 의 Base Path 상위>"` 후 **새 창**에서 실행. 근본 해결은 §5 ⑤ self-contained 배포 |
| `hostfxr.dll not found` (`dotnet`도 없음) | .NET 자체가 없다. **Desktop Runtime**을 깔아야 한다 — `FlaUI.Core`가 `Microsoft.WindowsDesktop.App.WindowsForms`를 요구하므로 일반 런타임만으로는 다음 단계에서 또 막힌다 |
| `Microsoft.WindowsDesktop.App was not found` | 위와 같다. 일반 런타임만 깔린 상태 — Desktop Runtime 8.0 (x64) |
| Windows에서 `dotnet build` 실패 (NETSDK1073 등) | Linux/macOS에서 솔루션 전체를 빌드하려 한 것이다. 클라이언트는 `net8.0-windows`라 Windows에서만 빌드된다 |
| `--watch`가 계속 "변화 없음"만 찍음 | 정상이다 — 화면이 그대로면 보내지 않는다. 강제로 보내려면 `--resend-unchanged` |
| 적재가 429로 밀림 | `Limits:IngestionPerMinute`(기본 120) 초과. 유실은 아니고 스풀에 남는다 — 간격을 늘리거나 상한을 올려라 |
| `--watch`가 "관측 중단"만 찍음 | 동의 게이트가 막고 있다. `Consent:Enabled`와 제외 목록을 확인하라 |
| `--completed --watch`가 거부됨 | 의도된 것이다. 완료 신호는 저장 직후 한 번만 따로 실행한다 |
| 기반 대사가 전부 `no_master` | 출처를 아직 안 붙였다. `Foundation:*` 설정 후 `POST /v1/foundation/refresh` |
| 기반 대사가 `unverifiable` | 마스터 키는 사업자번호인데 관측은 상호명이다. 화면에서 사업자번호를 함께 읽어야 한다 |

---

## 10. 실측 창 — Phase 0 관문 통과 절차

§11이 "실행해 보지 못한 것"을 나열한다. 이 장은 **그것을 닫는 절차**다.
로드맵(ARCHITECTURE §10)이 Phase 0에 세운 가정은 하나다 —
*"UIA로 안 읽히거나 AI 동적매핑이 성립 안 하면 이 제품은 무의미하다."*
그 가정에 아직 숫자가 없다. 서버 쪽은 이미 그 위에 높이 쌓였으므로, **더 쌓기 전에 잰다.**

측정 도구는 새로 만들 것이 없다. 콘솔 ②(지표)·⑦(AI 판단)·추정 이력이 그대로 채점 도구다.

### 10.1 시작 전 (Day 1)

세 가지가 갖춰져야 축적이 의미를 갖는다.

| | 확인 | 안 하면 |
|---|---|---|
| 영속화 | 상단 배지가 `인메모리`가 **아니어야** 한다 (§2.2) | 재시작 한 번에 2주가 사라진다 |
| LLM 실호출 | 상단 배지가 `LLM 스텁`이 **아니어야** 한다 (§2.5) | 미스가 AI로 가지 않아 H3를 잴 수 없다 |
| 측정자 ID | 사람마다 다른 값 (§4.3) | 개인화·k-인원 게이트가 전부 한 사람으로 접힌다 |

동시에 걸어 둘 것(코드가 아니라 협의라 리드타임이 길다): Graph API 접근 권한,
화면·필드 명세 문서, 사내 표준 브라우저 확인 (ARCHITECTURE §11).

### 10.2 조기 게이트 (Day 2) — 여기서 멈출 수도 있다

H1의 치명 신호는 2주가 아니라 **이틀이면 나온다.** 트리에 값이 잡히는지는 첫 회차에 보인다.

핵심 화면 3개(예: 구매요청 등록 / 목록 / 상세)를 각각 한 번씩 `--delay 5`로 찍고,
콘솔 ⑦에서 **필드에 라벨과 값이 함께 보이는지** 센다.

- **3개 중 2개 이상** → 진행. 나머지 한 개는 개별 문제로 따로 다룬다.
- **2개 미만** → **축적을 멈추고** 접근성 트리 노출과 먼저 싸운다
  (§9의 "트리에 값이 거의 안 잡힘", Chrome `--force-renderer-accessibility`).
  이 상태로 2주를 모으면 빈 트리 2주가 쌓인다.

> 기준을 숫자로 박아 두는 이유: "일부는 읽히는데 일부는 안 읽힌다"가 가장 흔한 결과이고,
> 그때 판단이 흔들리면 게이트가 장식이 된다.

### 10.3 축적 (Week 1–2)

`chopilot-watch.cmd` 상시 + 저장 직후 `--completed` 1회(§5 ⑥). 그게 전부다.

같은 창에서 **뒤 Phase의 관문 두 개가 부산물로 딸려 온다** — 창을 두 번 열 이유가 없다.

| 부산물 | 무엇의 관문인가 | 어디서 읽나 |
|---|---|---|
| 전송 바이트 기준선(전체 스냅숏 방식) | 체크포인트/델타의 절감률 ≥70% KPI **분모** | 업로더 회차 로그 |
| 저장 클릭을 사람이 몇 번 놓쳤나 | 상주 에이전트(UIA `Invoke` 구독)의 필요성 근거 | `--completed` 실행 수 ↔ 실제 저장 수 |

**관측기는 하나만 띄운다.** 두 개를 돌리면 스풀을 공유해 서로의 파일을 지우고,
같은 화면이 서로 다른 `eventId`로 두 번 적재돼 적중률 분모가 부푼다 —
*학습이 아니라 반복을 센* 결과가 된다.

### 10.4 채점과 판정 (Week 2 말)

수기 채점(H1·H3)은 **미루지 않는다.** 화면 기억이 흐려지면 채점 품질이 떨어진다.
주 1회로 나눠 하는 편이 낫다.

| 지표 | 목표 | 어디서 |
|---|---|---|
| 매핑 캐시 적중률 | ≥ 95% | `/v1/metrics.cacheHitRatio` (콘솔 ②) |
| AI 매핑 정확도 (H3) | ≥ 90% | 콘솔 ⑦에서 수기 채점 |
| 관측 정확도 (H1) | ≥ 90% | 콘솔 ⑦에서 수기 채점 |
| 변경 없음 억제율 | ≥ 95% | watch 회차 통계("변화 없음" 비율) |
| 지연 p95 | ≤ 3s | `/v1/metrics.latencyP95Ms` |
| LLM 단가 | 예산 내 | `/v1/metrics.{aiCalls,inputTokens}` |

**판정은 H3가 가른다.**

- **H3 ≥ 90%** → Phase 2(Mail·Doc) 설계 착수. Day 1에 걸어 둔 협의가 여기서 회수된다.
- **H3 < 90%** → 방향은 "축 추가"가 아니라 **프롬프트·온톨로지 개선 루프**다.
  `PromptBuilder`와 지식 문서를 고치고 재추론시켜 같은 화면으로 다시 잰다.
  이 루프가 일상이 되면 그때가 LLM 트레이싱(OTel GenAI 컨벤션)을 붙일 시점이다.

적중률만 높고 정확도가 낮은 경우를 특히 주의한다 — **틀린 매핑을 캐시가 잘 재사용하고 있다는 뜻**이다.
θ 미만 매핑은 캐시에 있어도 적중이 아니므로(ARCHITECTURE §9 KPI 각주), 콘솔 ④에서 보정·승격으로 빠져나간다.

---

## 11. 지금 검증되지 않은 것

정직하게 적어 둔다. 아래는 코드가 없는 게 아니라 **이 환경에서 실행해 보지 못한** 것들이다.

| 항목 | 필요한 것 |
|---|---|
| H1·H2·H3 (관측 정확도·식별·매핑 정확률) | Windows VDI + 실제 Procurement 화면 |
| Bedrock 실호출 | AWS 자격증명 |
| 무료 API 3종(환율·공휴일·사업자상태) 실호출 | 해당 호스트로의 아웃바운드 + 서비스 키 |
| 상주 에이전트의 저장 클릭 자동 관측 | Phase 2 (지금은 `--completed`로 사람이 표시) |

MCP 기반 출처는 로컬 MCP 서버를 띄워 **실제 소켓 위에서** 검증했다.
