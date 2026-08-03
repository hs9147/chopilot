# Cho-Pilot — 개발환경 설정 (Setup)

Phase 0 스캐폴딩을 빌드·실행하기 위한 환경 설정 가이드.

---

## 1. 사전 요구 (Prerequisites)

| 항목 | 요구 | 비고 |
|------|------|------|
| OS | **Windows 10/11** | UI Automation은 Windows 전용. 클라이언트는 `net8.0-windows` |
| .NET SDK | **.NET 8 SDK** | https://dotnet.microsoft.com/download/dotnet/8.0 |
| 브라우저 | Chrome / Edge | Procurement 웹앱 관측 대상 |
| AWS | 계정 + **Bedrock 모델 액세스** | `--bedrock` 모드에만 필요 |

> `ChoPilot.Core` / `ChoPilot.Mapping` / `ChoPilot.Tests`는 크로스플랫폼(net8.0)이라 macOS/Linux에서도 빌드·테스트된다. **UIA 실측(`chopilot-dump`)만 Windows 필수.**

확인:
```bash
dotnet --version   # 8.x
```

---

## 2. 빌드

솔루션 파일 `ChoPilot.sln`은 저장소에 포함되어 있다.

```bash
dotnet restore
dotnet build
```

> 설치가 안 돼 있으면 공식 스크립트로 사용자 로컬 설치(관리자 불요):
> ```bash
> powershell -ExecutionPolicy Bypass -Command "iwr https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1; & $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir $env:LOCALAPPDATA\Microsoft\dotnet"
> ```
> 그 후 `%LOCALAPPDATA%\Microsoft\dotnet` 을 PATH에 추가한다.

---

## 3. 애플리케이션 설정 (appsettings)

우선순위(뒤가 우선): `appsettings.json` → `appsettings.local.json` → 환경변수 `CHOPILOT_*`

| 키 | 기본값 | 설명 |
|----|--------|------|
| `Aws:Region` | ap-northeast-2 | Bedrock 리전 |
| `Aws:BedrockModelId` | anthropic.claude-3-5-... | 테넌트 가용 모델 ID로 교체 |
| `Mapping:ThetaHigh` | 0.8 | 캐시 채택/승격 신뢰도 임계치 |
| `Mapping:OrgId` | default | Shared Plane org 스코프(D5) |
| `Privacy:PolicyVersion` | 1.0 | Privacy Gate 정책 버전 |
| `Observation:MaxDepth` / `MaxNodes` | 25 / 4000 | 트리 수집 상한 |

**로컬 오버라이드:**
```bash
cp src/ChoPilot.Client/appsettings.local.example.json src/ChoPilot.Client/appsettings.local.json
# 편집 후 저장 — 이 파일은 .gitignore 대상(커밋 안 됨)
```
**환경변수 오버라이드 예:**
```bash
# 구분자는 이중 밑줄(__)
export CHOPILOT_Aws__Region=us-east-1
export CHOPILOT_Aws__BedrockModelId=anthropic.claude-3-5-sonnet-20240620-v1:0
```

---

## 4. AWS 자격증명 (secrets는 설정 파일에 넣지 않는다)

표준 AWS 자격증명 체인에서 해석된다. 택1:

```bash
# (a) AWS CLI 프로파일
aws configure                 # Access Key/Secret/Region 입력

# (b) 환경변수
export AWS_ACCESS_KEY_ID=...
export AWS_SECRET_ACCESS_KEY=...
export AWS_REGION=ap-northeast-2

# (c) 운영: EC2/ECS/VDI 인스턴스의 IAM 역할 (키 불요, 권장)
```

- 필요한 최소 권한: `bedrock:InvokeModel` (해당 모델 ARN).
- 폐쇄망/사내 VDI: **VPC Endpoint(PrivateLink for Bedrock)** 로 인터넷 미경유 경로 사용(ARCHITECTURE §2).

Bedrock 접근 확인:
```bash
aws sts get-caller-identity
aws bedrock list-foundation-models --region us-east-1 --by-provider anthropic
```

> **⚠️ Inference Profile 필수 (실측 확인됨)**
> 현재 Anthropic 모델(Haiku 4.5, Sonnet 4.5 등)은 **ON_DEMAND 직접 호출을 지원하지 않고 inference profile로만** 호출된다. 모델 ID로 base ID(`anthropic.claude-...`)가 아니라 **profile ID(`us.` / `global.` 접두사)**를 써야 한다.
> ```bash
> aws bedrock list-inference-profiles --region us-east-1
> # 예: us.anthropic.claude-haiku-4-5-20251001-v1:0  (검증됨)
> ```
> base ID를 쓰면 `ResourceNotFoundException`(legacy/ON_DEMAND 불가)이 난다.

---

## 5. 실행

```bash
# 순수 로직 테스트 (Windows/AWS 불요)
dotnet test

# UIA 관측 (Windows) — 3초 안에 대상 브라우저 화면을 포그라운드로
dotnet run --project src/ChoPilot.Client -- --delay 3 --out out/pr_create.snapshot.json --baseline

# 실 Bedrock 동적 매핑까지 (AWS 자격증명·모델 액세스 필요)
dotnet run --project src/ChoPilot.Client -- --delay 3 --bedrock
```

산출 JSON은 [PHASE0-KIT.md](PHASE0-KIT.md) §2/§3 측정의 원자료로 사용한다.

---

## 6. 트러블슈팅

| 증상 | 원인/조치 |
|------|-----------|
| 트리에 값이 거의 안 잡힘 | 브라우저 접근성 트리 미노출 → 대상 창을 실제 포그라운드로. Chrome은 접근성 자동 활성; 안 되면 `--force-renderer-accessibility` |
| `AmazonBedrockRuntimeException` AccessDenied | 모델 액세스 미승인 또는 IAM 권한 부족 → Bedrock 콘솔에서 모델 활성화, `bedrock:InvokeModel` 부여 |
| `ResourceNotFoundException` / "Legacy" / on-demand 불가 | base 모델 ID 사용이 원인 → **inference profile ID(`us.`/`global.`)로 교체** (`list-inference-profiles`) |
| 모델 ID 오류 | 리전별 가용 모델·프로파일 상이 → `list-inference-profiles`로 확인 후 `Aws:BedrockModelId` 교체 |
| FlaUI 관련 빌드 경고 | `net8.0-windows` 타깃·Windows에서 빌드 확인 |
