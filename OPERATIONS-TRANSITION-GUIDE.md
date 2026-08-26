# Cho-Pilot 운영 전환 가이드

> 대상: Phase 1 baseline(관측 적재, 매핑, Guide, 사용자 검측/피드백)
>
> 이 문서는 개발·측정 환경을 **제한된 운영 전환 환경**으로 옮기는 절차다. 상세 설계는
> [ARCHITECTURE.md](ARCHITECTURE.md), 실행 명령은 [RUNBOOK.md](RUNBOOK.md)를 따른다.

---

## 1. 전환 범위와 출시 기준

이번 전환 범위는 Procurement UIA 관측, Privacy Gate, JWT 기반 API 접근, 사용자 Review UI,
로컬 durable journal까지다. 프로덕션 데이터는 아래 필수 항목이 모두 충족될 때만 받는다.

| 구분 | 전환 기준 | 판정 |
|---|---|---|
| 인증 | `Production` + `Auth:Mode=jwt`, 32바이트 이상 HS256 키, issuer/audience 검증 | 필수 |
| 권한 | `tenant_id`, `role` claim과 역할별 API smoke test | 필수 |
| 개인정보 | 제외 앱/URL 목록 승인, Privacy policy version 승인, 샘플 잔여 PII 검사 0건 | 필수 |
| 저장소 | 암호화된 영속 볼륨, 서비스 계정 전용 권한, 백업·복구 연습 | 필수 |
| 클라이언트 | Windows VDI 실제 Procurement 화면에서 UIA·동의·스풀 E2E | 필수 |
| 변경 통제 | Canary 대상/중단 기준/롤백 담당자 지정 | 필수 |

### 아직 출시 차단인 항목

- OIDC/JWKS와 자동 키 회전은 아직 구현되지 않았다. 외부 IdP 연동이 필요한 정식 운영은 이 항목이
  완료될 때까지 차단한다. 과도기에는 사내 VPC 내부에서만 Secret Manager가 주입하는 HS256 키를 쓴다.
- Aurora 단일 트랜잭션 + outbox도 아직 없다. 현재 JSONL journal은 PoC/제한 운영용이며,
  중단 시 projection 재시도로 일관성을 회복하는 수준이다.
- UIA 이벤트 구독·checkpoint/delta 저장은 feature-flag 후속 단계다. 현재는 안정 키와 값 포함
  지문으로 변경 없는 관측을 억제한다.

---

## 2. 역할과 책임

| 역할 claim | 허용 업무 | 운영 책임 |
|---|---|---|
| `ingestion_client` | 관측 적재 | VDI/에이전트 서비스 계정 |
| `end_user` | 자신의 Guide·검측·개인 보정 | 일반 사용자 |
| `reviewer` | 조직 공유 보정 승인/거절 | 도메인 검수자 |
| `knowledge_admin` | 지식·기반 정보 변경 | 지식 관리자 |
| `ops_auditor` | 감사·지표·운영 진단 조회 | SRE/보안 운영 |

JWT에는 `sub`, `tenant_id`, 반복 가능한 `role` claim을 넣는다. 기본 claim 이름을 바꿀 때는
`Auth__Jwt__SubjectClaim`, `Auth__Jwt__TenantClaim`, `Auth__Jwt__RoleClaim`을 IdP 발급 형식과
동시에 바꾼다. 역할 없는 토큰은 `/v1` API에서 기본 거부된다.

---

## 3. 사전 준비

1. 릴리스 후보에서 다음을 통과시킨다.

   ```bash
   dotnet test tests/ChoPilot.Tests/ChoPilot.Tests.csproj
   dotnet build src/ChoPilot.Client/ChoPilot.Client.csproj -p:EnableWindowsTargeting=true
   ```

2. 전용 서비스 계정 `chopilot`과 암호화된 영속 디렉터리를 만든다. 서비스 계정 외에는 읽지 못하게 한다.

   ```bash
   install -d -o chopilot -g chopilot -m 0700 /var/lib/chopilot
   ```

3. 시크릿 매니저에 `Auth__Jwt__SigningKey`를 저장한다. 소스, `appsettings*.json`, Windows 클라이언트
   설정 파일에는 토큰·키를 넣지 않는다.
4. reverse proxy에서 TLS를 종료하고, 서버 포트는 VPC/호스트 내부에서만 열어 둔다. health check는
   `GET /health`를 사용한다.
5. 개인정보 책임자가 `Consent:ExcludedApps`, `Consent:ExcludedUrlPatterns`, `Privacy:PolicyVersion`을
   승인한다. 민감 앱은 allow-list가 아니라 제외 정책만으로 충분하다고 가정하지 않는다.

---

## 4. 서버 배포

### 4.1 필수 환경 변수

아래 값은 Secret Manager 또는 배포 플랫폼의 환경 변수로 주입한다.

```bash
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5080

Auth__Mode=jwt
Auth__Jwt__SigningKey='<32 bytes or longer secret>'
Auth__Jwt__Issuer='https://idp.example.internal'
Auth__Jwt__Audience='chopilot'
Auth__Jwt__TenantClaim='tenant_id'
Auth__Jwt__RoleClaim='role'

Storage__Path=/var/lib/chopilot
Limits__IngestionPerMinute=120
UseBedrock=false
```

`Production`에서 header mode는 의도적으로 기동이 거부된다. issuer나 audience가 비어도 기동이
거부된다. `Auth__AllowUnverifiedInProduction=true`는 비상 개발 목적 외에는 사용하지 않는다.

Bedrock을 켤 때에는 별도의 IAM role에 필요한 Bedrock inference profile만 허용하고,
`UseBedrock=true`, `Aws__Region`, `Aws__BedrockModelId`를 추가한다. 모델 호출 비용과 개인정보
경계를 승인받기 전에는 기본값(`false`)을 유지한다.

### 4.3 Vertex AI / Azure OpenAI 공급자 전환

LLM 공급자는 한 배포에서 하나만 선택한다. `Llm__Provider`를 명시하면 기존 `UseBedrock` 값은
호환 fallback으로만 남고 선택에는 쓰이지 않는다.

| Provider | 필수 설정 | 인증 경계 |
|---|---|---|
| `vertex` | `Llm__Vertex__ProjectId`, `Location`, `Model` | ADC: Workload Identity/런타임 service account 우선. 개발자 ADC는 개발 환경에만 |
| `azure_openai` | `Llm__AzureOpenAI__Endpoint`, `Deployment`, `ApiVersion` | `ApiKey` 또는 단기 `BearerToken` 중 정확히 하나 |
| `bedrock` | `Aws__Region`, `Aws__BedrockModelId` | IAM role/inference profile |

Vertex/Azure로 프롬프트가 나가면 AWS VPC 내부 전송이라는 기존 가정은 성립하지 않는다. 해당 cloud
project/resource의 리전, DLP·보존 정책, private endpoint/egress, 모델 학습·로그 정책을 개인정보·보안
승인에 포함한다. Azure API key와 GCP ADC credential file을 이미지·VDI 설정 파일·journal에 저장하지 않는다.

### 4.2 기동 및 확인

```bash
dotnet ChoPilot.Server.dll

curl --fail http://127.0.0.1:5080/health
curl --fail -H "Authorization: Bearer $OPS_TOKEN" http://127.0.0.1:5080/v1/metrics
curl --fail -H "Authorization: Bearer $USER_TOKEN" http://127.0.0.1:5080/v1/me/review-tasks
```

`OPS_TOKEN`은 `ops_auditor`, `USER_TOKEN`은 `end_user` 역할을 가져야 한다. 운영자 토큰으로
사용자 검측 API를 대리 호출하지 말고, 역할 거부(403)도 함께 확인한다.

다음 상태를 배포 기록에 남긴다.

- `/v1/auth`: `method=jwt`, `verified=true`
- `/v1/storage`: `durable=true`, 복원 오류·손상 줄 수
- `/v1/metrics`: 배포 직후 기준선
- reverse proxy의 TLS 인증서/health check 상태

---

## 5. Windows VDI 클라이언트 전환

1. `appsettings.local.json`에는 서버 URL, 사용자 ID(개발 fallback), tenant만 둔다. Bearer token은
   장기 파일 값으로 보관하지 않고 OS 보안 저장소 또는 기업 토큰 공급자에서 주입한다.
2. 대상 VDI에서 제외 앱·URL을 검증한 뒤, 별도 테스트 계정으로 한 건을 업로드한다.

   ```powershell
   chopilot-dump --delay 3 --upload https://chopilot.internal
   chopilot-dump --watch 30 --rounds 3 --upload https://chopilot.internal
   ```

3. 정적 화면에서 `변화 없음`이 표시되는지, 값 변경 후 새 관측이 생기는지, 네트워크 차단 뒤
   spool 재전송과 `*.bad` 격리가 예상대로인지 확인한다.
4. `/review.html`에서 해당 사용자의 과제가 보이는지, 개인 보정은 즉시 반영되는지, 조직 공유 보정은
   reviewer 승인 전까지 `pending_org_review`인지 검증한다.

Canary는 한 테넌트의 2~5명으로 시작한다. 다음 중 하나라도 발생하면 해당 VDI의 업로드를 중지하고
직전 버전으로 롤백한다: 잔여 PII, 다른 사용자/테넌트 데이터 노출, spool 급증, 5xx 지속, 매핑 오류의
집중 보고.

---

## 6. 관측·알림·일상 운영

| 신호 | 확인 방법 | 초기 조치 |
|---|---|---|
| 401/403 증가 | proxy 로그, API 응답 | issuer/audience/clock/role claim 확인 |
| 429 증가 | ingress 로그, `Limits:IngestionPerMinute` | VDI 수·업로드 주기 확인 후 한도 조정 |
| spool `*.bad` | VDI spool 디렉터리 | 4xx `detail` 확인, 계약/Privacy/토큰 수정 |
| journal 손상 | `/v1/storage`의 `corrupt` | 직전 백업 보존, 손상 줄과 종료 원인 조사 |
| AI 비용·지연 | `/v1/metrics`의 AI 호출·p95 | 선택 공급자 비활성화 또는 재추론 설정 검토 |
| PII 신고 | feedback `privacy_report`, 운영 알림 | 해당 관측·클라이언트 정책 격리, 보안 절차 개시 |

현재 API 지표는 운영 진단용이다. 장기 운영 전에는 중앙 로그, tenant별 대시보드, 보존·삭제 정책,
경보 임계치를 별도 관측 플랫폼으로 이전한다.

---

## 7. 롤백과 복구

### 애플리케이션 롤백

1. 새 VDI 배포/업로드를 중지한다.
2. 서버를 정상 종료하고 `/var/lib/chopilot`을 읽기 전용 백업한다.
3. 직전 검증 이미지/바이너리로 되돌린 뒤 health, JWT, storage smoke test를 반복한다.
4. 새 버전에서 기록한 journal을 제거하거나 임의로 편집하지 않는다. 호환성 검토 후 재처리한다.

### 보안 사고

- 키/토큰 의심: 배포를 중지하고 시크릿을 교체한다. HS256 키 회전은 모든 발급 토큰의 재발급을
  동반하므로 영향 사용자에게 재로그인을 안내한다.
- PII 의심: 해당 VDI의 `Consent:Enabled=false`로 관측을 즉시 중지하고, journal 접근을 보안 담당자로
  제한한다. 관측 ID·tenant·시간 범위를 보존한 뒤 조직의 사고 대응 절차를 따른다.
- 저장소 손상: 원본 볼륨을 보존하고, 마지막 정상 백업을 별도 환경에서 복구·검증한다.

---

## 8. 전환 완료 기록

아래 정보를 릴리스 티켓에 남기면, 다음 전환에서 재현 가능하다.

- Git commit/배포 이미지 digest, 배포자, 시각, Canary tenant/VDI 목록
- JWT issuer/audience 및 claim mapping(비밀값 제외)
- Privacy policy version, 제외 앱/URL 승인 번호
- `/v1/storage`, `/v1/metrics`, smoke test 결과
- 롤백 이미지 위치와 담당자
- Windows UIA E2E 결과 및 알려진 제한사항

이 기록 없이 Production 전환을 완료로 처리하지 않는다.
