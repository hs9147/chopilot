# Cho-Pilot — Phase 0 실측 가이드

> **무엇을** 검증하나(가설·통과선) → [PHASE0-PLAN.md](PHASE0-PLAN.md)
> **어디에** 적나(측정표 양식) → [PHASE0-KIT.md](PHASE0-KIT.md)
> **어떻게** 재나(절차·명령) → **이 문서**

이 문서의 모든 명령은 실행해 동작을 확인한 것이다. 도구가 없어 수기로 해야 하는 항목은 그렇게 명시했다.

---

## 0. 핵심 전략 — 캡처와 측정을 분리한다

UIA 캡처는 Windows·VDI·Procurement 테스트 계정이 모두 있어야 하지만, **그 이후의 전 과정**(서명 계산, 매핑, 캐시, 마스킹 검증, 지표 산출)은 저장된 스냅샷 JSON만 있으면 **어느 PC에서든 반복 재생**할 수 있다.

```
[Windows / VDI]   chopilot-dump  →  out/*.snapshot.json     ← 1회, 짧게
                                          │
[아무 PC]         jq → 서버 재생 → /v1/metrics              ← 반복, 무제한
```

이 분리가 중요한 이유:

- 테스트 환경·전문가 시간은 희소 자원이다. **캡처 세션은 짧게 하고 스냅샷을 많이 모아라.**
- 알고리즘(서명 정규화·마스킹 규칙)을 고친 뒤 **같은 스냅샷으로 재측정**하면 개선 효과를 정확히 비교할 수 있다. 화면을 다시 열 필요가 없다.
- 스냅샷은 마스킹이 끝난 상태라 **원문 PII 없이** 공유·보관할 수 있다(반출 정책은 별도 확인).

---

## 1. 준비

### 1.1 측정 서버 기동

```bash
ASPNETCORE_URLS=http://127.0.0.1:5080 \
dotnet run --project src/ChoPilot.Server -c Release -- --Mapping:ThetaHigh=0.5
```

```bash
curl -s http://127.0.0.1:5080/health     # {"status":"ok"}
```

### 1.2 측정 콘솔 — 브라우저로 여는 기본 경로

**<http://127.0.0.1:5080/> 을 열면 이 문서의 절차가 화면으로 제공된다.** 아래 §3의 `jq`/`curl` 절차는
자동화·CI용으로 남겨둔 것이고, 손으로 측정할 때는 콘솔을 쓰는 편이 빠르다.

| 콘솔 단계 | 대응 절차 | 자동/수기 |
|-----------|----------|----------|
| ① 스냅샷 적재 | 파일을 끌어다 놓으면 재생된다 (§3 H3b의 `REPLAY`) | 자동 |
| ② 지표 | 적중률·지연·토큰·마스킹을 통과선과 대조해 PASS/FAIL 표시 (H3b·H6) | 자동 |
| ③ 서명 진단 | route별 서명 수 — **갈린 route를 경고로 띄운다** (H3b 원인) | 자동 |
| ④ 스냅샷별 상세 | 요소 인벤토리(H1), 레코드 식별(H2), 마스킹·잔존 PII(H4) | 표시는 자동, 채점은 수기 |
| ⑤ 채점 &amp; 리포트 | H1·H3 집계값 입력 → Go/No-Go 표 → Markdown/JSON 내려받기 | 수기 입력 |

- **H2 채점**은 스냅샷 상세에서 `정답 / 오답 / 제외` 버튼으로 하고, 콘솔이 자동 합산한다.
- **H1·H3**은 정답지가 화면 명세와 전문가 판단이라 집계값을 직접 넣는다(§3 참조).
- **H3의 AI 매핑 실행 자체**는 Windows + AWS가 필요해 콘솔이 대신할 수 없다 — §3 H3의 CLI를 쓴다.
- 채점 결과는 브라우저 localStorage에 남아 새로고침해도 유지된다. **서버 지표는 그렇지 않다**(T2).

> 콘솔은 서버에 붙어 있는 그대로의 데이터를 보여줄 뿐, 별도 저장소를 갖지 않는다.
> 세션을 끝내기 전에 ⑤에서 리포트를 내려받아라.

### 1.3 측정을 왜곡하는 함정 4가지 — 시작 전에 확인

| # | 함정 | 증상 | 대응 |
|---|------|------|------|
| **T1** | `StubAiMapper`의 필드 신뢰도는 **0.6**인데 기본 `Mapping:ThetaHigh`는 **0.8** | 매핑이 항상 `pending_review`로 남아 **`cacheHitRatio`가 구조적으로 0** | 스텁으로 캐시를 측정하려면 위처럼 `--Mapping:ThetaHigh=0.5`. 실제 값을 재려면 `UseBedrock=true` |
| **T2** | 서버 저장소가 전부 **인메모리** | 재시작하면 지표·캐시·감사로그가 전멸 | **한 측정 세션 = 서버 1회 연속 실행.** 끝내기 전에 `/v1/metrics`를 파일로 저장 |
| **T3** | `UiaObserver.TryGetUrl`이 "첫 번째 Edit 자손"을 주소창으로 가정 | 폼 입력칸 값이 URL로 잡혀 H2가 통째로 무의미해짐 | 캡처 직후 §2.2로 **URL부터 육안 확인** |
| **T4** | `Observation:MaxNodes`(기본 4000) 절단 | 절단 지점이 방문마다 달라지면 같은 화면이 다른 서명을 가짐 | 캡처 직후 노드 수 확인(§2.2). 상한에 닿으면 `MaxNodes`를 올리고 재캡처 |

> 스냅샷 JSON의 한글은 `\uXXXX`로 이스케이프된다. 눈으로 읽을 땐 `jq`를 거쳐라(자동 디코드).

---

## 2. 캡처 (Windows, PHASE0-PLAN WS2)

### 2.1 화면당 최소 3회 캡처

| 캡처 | 목적 |
|------|------|
| (a) **신규 등록** 화면 (레코드 ID 없음) | H2에서 "ID 없음"이 정답인 케이스 |
| (b) 기존 레코드 1건 | H1·H2 본 측정 |
| (c) **다른 레코드 / 다른 행 수**의 같은 화면 | 서명 안정성 = 캐시 적중의 전제 |

```bash
dotnet run --project src/ChoPilot.Client -- --delay 5 --out out/pr_create_new.snapshot.json
dotnet run --project src/ChoPilot.Client -- --delay 5 --out out/pr_create_PR123.snapshot.json
dotnet run --project src/ChoPilot.Client -- --delay 5 --out out/pr_create_PR456.snapshot.json
```

`--delay` 초 동안 대상 화면을 포그라운드로 두면 캡처된다.

### 2.2 캡처 직후 위생 점검 (T3·T4)

```bash
F=out/pr_create_PR123.snapshot.json

jq -r '.observation.Screen.Url'                              "$F"   # 진짜 주소창 URL인가? (T3)
jq '[.observation.Tree | recurse(.Children[])] | length'     "$F"   # MaxNodes(4000)에 닿았나? (T4)
jq -c '.observation.Screen.RecordHint'                       "$F"   # 레코드 식별 결과
```

URL이 엉뚱하거나 노드 수가 상한에 닿으면 **그 스냅샷으로는 측정하지 마라.** 설정을 고쳐 재캡처한다.

---

## 3. 가설별 측정 절차

### H1 — 필드 획득률 ≥ 90% (수기 채점)

**정답지(ground truth)는 화면·필드 명세 문서다.** 도구가 자동 채점할 수 없다 — 명세의 어느 필드가 트리의 어느 노드인지는 사람이 잇는다.

요소 인벤토리를 뽑아 PHASE0-KIT §2.1 표에 붙인다:

```bash
jq -r '["ref","role","name","value","automationId"], (
  [.observation.Tree | recurse(.Children[])] | .[]
  | [.Ref, .Role, (.Name // "-"), (if (.Value // "") == "" then "X" else "O" end), (.AutomationId // "-")]
) | @tsv' "$F"
```

```
ref	role	name	value	automationId
n4	Edit	품목코드	O	txtMat
n5	Edit	수량	O	txtQty
n6	Text	단가	X	-
n7	Edit	-	O	txtPrice
n8	Edit	납기	X	txtDue
```

채점:

- **Name 획득** = 명세 필드의 라벨이 어느 노드의 `name`으로 나타나는가
- **Value 획득** = 그 필드의 값을 읽을 수 있는가 (`value` 컬럼이 `O`)
- 획득률 = (Name 획득 ∧ Value 획득) / 명세 필드 수 → KIT §2.2

**주의 — 값이 비어 보이는 세 경우를 구분하라.** 위 예에서 `n6 단가`가 `X`인 것은 실패가 아니다.

| 관측 | 의미 | H1 채점 |
|------|------|--------|
| 라벨 노드(`Text`)와 값 노드(`Edit`)가 분리 (`n6`+`n7`) | 정상 — 웹에서 흔한 형태 | **성공** (값은 `n7`에 있다) |
| 사용자가 입력을 안 함 (`n8 납기`) | 정상 | 분모에서 제외하거나 별도 표기 |
| 값이 존재하는데 트리에 안 나옴 | **진짜 실패** | 실패 |

마스킹된 값(`***MASKED***`)은 **획득 성공**이다. 읽는 데 성공했기 때문에 마스킹된 것이다.

---

### H2 — 화면·레코드 식별 ≥ 95% (수기 채점)

```bash
for f in out/*.snapshot.json; do
  printf '%s\t%s\t%s\n' "$(basename "$f")" \
    "$(jq -r '.observation.Screen.Url' "$f")" \
    "$(jq -c '.observation.Screen.RecordHint' "$f")"
done
```

채점 기준:

- 기존 레코드 화면 → `RecordHint.Value`가 **실제 레코드 번호와 일치**하면 정답
- 신규 등록 화면 → **`null`이 정답이다.** 여기서 아무 값이나 잡아내면 오답(과탐지)
- 분모는 캡처한 화면 수 전체. 신규 등록 화면을 분모에서 빼지 마라

식별 출처(`Source`)가 `url_query`·`url_path`·`title` 중 무엇인지도 KIT §2.3에 기록한다. `title`에 의존하는 화면은 브라우저 탭 제목이 바뀌면 깨지므로 리스크로 남겨야 한다.

---

### H3 — AI 매핑 정확률 ≥ 90% (Windows + AWS 필요)

같은 화면에 대해 **대조군(alias 매칭)과 실험군(Bedrock)** 을 각각 돌려 비교한다. 매핑 상세는 stderr로 나온다.

```bash
dotnet run --project src/ChoPilot.Client -- --delay 5 --baseline 2> baseline.log
dotnet run --project src/ChoPilot.Client -- --delay 5 --bedrock  2> bedrock.log
```

```
[chopilot-dump] bedrock(ai) mapped 5 fields:
    n4 -> Material (conf 0.95, ai)
    n7 -> UnitPrice (conf 0.91, ai)
```

채점 (KIT §3.3):

| | 명세 필드 수 | 정확 매핑 | 정확률 |
|---|---|---|---|
| baseline (alias) | 12 | ? | ? |
| **bedrock (AI)** | 12 | ? | **≥90% 판정** |

- 오매핑은 **원인까지** 적어라(라벨 없음 / 동음이의 / 온톨로지에 개념 부재). H3 미달 시 프롬프트를 고칠지 온톨로지를 늘릴지가 여기서 갈린다
- 온톨로지에 없는 개념을 모델이 지어내면 코드가 이미 버린다(환각 방지). 그래서 **누락은 발생해도 없는 개념이 들어오지는 않는다** — 누락 쪽을 집중해서 봐라

---

### H3b — 캐시 적중률 ≥ 95% / 자가치유 ≥ 95% (자동)

스냅샷 재생으로 측정한다. 아래는 실행 확인된 절차다.

**재생 함수** (측정 세션 내내 재사용):

```bash
POST() { curl -s -X POST http://127.0.0.1:5080/v1/observations \
           -H 'Content-Type: application/json' --data-binary @- \
         | jq -c '{observation_id, cache_hit, signature: .signature[7:19]}'; }

REPLAY() { jq -c --arg id "$2" '.observation | .EventId = $id' "$1" | POST; }
```

**(1) 적중률** — 같은 화면의 여러 레코드를 순서대로 재생:

```bash
REPLAY out/pr_create_PR123.snapshot.json  r1     # 첫 조우 → cache_hit false (AI 호출)
REPLAY out/pr_create_PR456.snapshot.json  r2     # 같은 화면 → cache_hit true 여야 정상
```

```bash
curl -s http://127.0.0.1:5080/v1/metrics | jq -c '{cacheHitRatio, distinctSignatures, aiCalls}'
```

> **`distinctSignatures`가 진짜 진단값이다.** 캡처한 화면이 3종인데 이 값이 3보다 크면, 같은 화면이 여러 서명으로 갈라졌다는 뜻이고 적중률은 절대 95%에 못 간다. 원인은 §1.3의 T4(절단)이거나 화면에 남아 있는 동적 구조다. 갈린 두 스냅샷의 구조를 직접 비교하려면 `SignatureService.Skeleton()`이 해시 이전 형태를 돌려준다.

**(2) 자가치유** — 화면이 바뀌면 서명이 바뀌고 재추론되는가:

```bash
REPLAY out/pr_create_v1.snapshot.json  h1        # 변경 전
REPLAY out/pr_create_v2.snapshot.json  h2        # 필드 추가/재배치 후
```

**통과 조건: 서명이 달라지고 `cache_hit=false`.** (확인 예시)

```
{"observation_id":"h1","cache_hit":false,"signature":"56917f85b35c"}
{"observation_id":"h2","cache_hit":false,"signature":"b698b49ff5e7"}   ← 서명 변경 → 재추론
```

**(3) 반증 테스트 — 바뀌면 안 되는 것도 확인하라.** 목록 화면을 행 수만 다르게 캡처해 재생한다. **서명이 같고 두 번째가 `cache_hit=true`여야 정상**이다. 여기서 서명이 갈리면 실제 운영에서 매 스크롤마다 AI를 호출하게 된다.

```
{"observation_id":"grid-3","cache_hit":false,"signature":"b8f83a2dd4f9"}
{"observation_id":"grid-10","cache_hit":true,"signature":"b8f83a2dd4f9"}   ← 행 수 무관, 정상
```

---

### H4 — 마스킹 재현율 ≥ 99% (반자동)

**(1) 기대 목록 대비 재현율.** 화면 명세에서 민감 필드를 정하고, §2.2 인벤토리에서 그 `ref`를 찾아 `expected`에 넣는다:

```bash
jq --argjson expected '["n7","n10"]' '
  .observation.Privacy.MaskedRefs as $actual
  | ($expected - $actual) as $missed
  | {expected: ($expected|length),
     missed:   $missed,
     extra:    ($actual - $expected),
     recall:   (1 - (($missed|length) / ($expected|length)))}' "$F"
```

```json
{ "expected": 2, "missed": [], "extra": [], "recall": 1 }
```

- `missed`가 **재현율을 떨어뜨리는 유일한 항목**이다. 하나라도 나오면 그 노드의 형태(라벨 위치·AutomationId)를 기록해 규칙을 보강한다
- `extra`는 과마스킹이다. **통과선을 깎지는 않지만** 매핑 정확도(H3)를 떨어뜨리므로 건수는 기록한다

**(2) 잔존 PII 스캔 — 반증 테스트.** 기대 목록이 틀렸을 수도 있으니, 전송 페이로드 전체를 훑어 원문이 남았는지 직접 본다:

```bash
jq -r '[.observation.Tree | recurse(.Children[]) | .Value | select(. != null and . != "")] | .[]' "$F" \
  | grep -Ein '[0-9]{6}-[0-9]{7}|[A-Za-z0-9._%-]+@[A-Za-z0-9.-]+|01[016789]-?[0-9]{3,4}-?[0-9]{4}' \
  || echo "잔존 PII 없음 (통과)"
```

이 스캔은 캡처한 **모든** 스냅샷에 돌려라. 한 건이라도 걸리면 H4는 미달이다.

---

### H6 — 지연 p95 ≤ 3s / 매핑당 비용 (부분 자동)

```bash
curl -s http://127.0.0.1:5080/v1/metrics | jq -c '{latencyP50Ms, latencyP95Ms, latencyMaxMs, aiCalls, inputTokens, outputTokens}'
```

**`latencyP95Ms`를 그대로 NFR과 비교하지 마라.** 이 값은 서버가 이벤트를 받은 뒤 *서명→매핑→BO 생성*까지의 구간만 잰다. NFR "관측→Guide 표시 p95 ≤ 3s"에는 다음이 빠져 있다:

| 구간 | 계측 여부 | 재는 법 |
|------|----------|--------|
| UIA 트리 캡처 | ✗ | 클라이언트 실행을 `Measure-Command`로 감싸 측정 |
| 네트워크 왕복 | ✗ | `curl -w '%{time_total}'` 로 별도 측정 |
| 서명→매핑→BO | ✓ | `latencyP95Ms` |
| Guide 조회 | ✗ | `curl -w '%{time_total}' .../v1/guide?...` |

`latencyP95Ms`는 **하한값**으로 쓰고, 나머지를 더해 KIT에 기록한다.

**토큰은 `UseBedrock=true`일 때만 채워진다.** 스텁으로 돌리면 `inputTokens`/`outputTokens`가 0이다(비용 0이 아니라 미측정). 매핑당 비용 = `(inputTokens × 입력단가 + outputTokens × 출력단가) / aiCalls`.

---

### H5 — 결정론 연결 정밀도 ≥ 0.9 (**구현 없음 — 전량 수기**)

Entity Resolver는 코드에 존재하지 않는다. Phase 0에서는 **알고리즘이 성립하는지만** 수기로 확인한다.

1. 캡처한 Procurement 레코드 20~30건의 식별자를 뽑는다: `jq -r '.observation.Screen.RecordHint.Value' out/*.snapshot.json`
2. 연관된 메일/문서 샘플을 모은다(실물 또는 모의)
3. KIT §5의 규칙 순위(문서번호 일치 → 거래처코드 → 첨부·시간창)로 **손으로 매칭**한다
4. 정밀도 = 맞게 연결한 건수 / 연결한 건수 전체. **분모는 "연결한 건"이지 "연결해야 했던 건"이 아니다**(정밀도이므로)
5. 모호 케이스는 따로 모아 둔다 — Phase 2 설계의 입력이 된다

H5는 H1·H3와 달리 **Go/No-Go의 필수 조건이 아니다**(PHASE0-PLAN §8). 자원이 부족하면 여기를 줄인다.

---

## 4. 측정 세션 운영

인메모리 저장소(T2) 때문에 **세션 = 서버 1회 연속 실행**이다. 끝내기 전에 반드시 결과를 파일로 남긴다.

```bash
STAMP=$(date +%Y%m%d-%H%M)
curl -s http://127.0.0.1:5080/v1/metrics        > "measure/metrics-$STAMP.json"
curl -s 'http://127.0.0.1:5080/v1/audit?limit=1000' > "measure/audit-$STAMP.json"
```

감사 로그에는 관측 1건마다 서명·신뢰도·적중여부·지연·마스킹 수가 남는다. 지표가 이상하면 여기서 어느 이벤트가 원인인지 되짚을 수 있다:

```bash
jq -r '.entries[] | [.seq, .signature[7:19], .cacheHit, .confidence, .durationMs, .maskedRefCount] | @tsv' \
  "measure/audit-$STAMP.json"
```

---

## 5. 집계 → Go/No-Go

| 가설 | 통과선 | 출처 | 자동화 |
|------|--------|------|--------|
| H1 필드 획득률 | ≥ 90% | 인벤토리 + 명세 대조 | 수기 채점 |
| H2 화면·레코드 식별 | ≥ 95% | `Screen.RecordHint` | 수기 채점 |
| H3 AI 매핑 정확률 | ≥ 90% | `--bedrock` stderr + 전문가 라벨 | 수기 채점 |
| H3b 캐시 적중률 | ≥ 95% | `cacheHitRatio` | **자동** |
| H3b 자가치유 | ≥ 95% | 변경 전/후 서명 + `cache_hit` | **자동** |
| H4 마스킹 재현율 | ≥ 99% | `Privacy.MaskedRefs` + 잔존 스캔 | 반자동 |
| H6 지연 p95 | ≤ 3s | `latencyP95Ms` + 미계측 구간 합산 | 부분 |
| H6 매핑당 비용 | 예산 내 | `inputTokens`/`outputTokens`/`aiCalls` | **자동** |
| H5 연결 정밀도 | ≥ 0.9 | — | **전량 수기** |

판정 규칙은 [PHASE0-PLAN.md §8](PHASE0-PLAN.md)을 따른다. GO 조건은 **H1·H2·H3·H3b** 이며 H4·H5·H6는 조건부 항목이다.

> 미달 항목은 숫자만 적지 말고 **왜 미달인지 한 줄**을 함께 남겨라. "H3b 62%"보다 "H3b 62% — `distinctSignatures`가 화면 3종에 11개, 목록 화면이 필터 상태별로 갈림"이 Phase 1 착수 여부를 가른다.
