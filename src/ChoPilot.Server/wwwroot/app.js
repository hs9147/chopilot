'use strict';

// Cho-Pilot 측정 콘솔 — PHASE0-MEASUREMENT.md의 jq/curl 절차를 화면으로 대체한다.
// 서버 저장소는 Storage:Path를 주면 저널로 남고, 없으면 서버 수명과 함께 사라진다(상단 배지).
// 수기 채점은 어느 쪽이든 브라우저 localStorage에 남는다 — 서버가 모르는 값이다.

const PASS = {
  h1: 0.90,   // 필드 획득률
  h2: 0.95,   // 화면·레코드 식별
  h3: 0.90,   // AI 매핑 정확률
  h3b: 0.95,  // 캐시 적중률
  h6: 3000,   // 관측→Guide p95 (ms)
};

const SCORING_KEY = 'chopilot.measure.scoring';
const SOURCES_KEY = 'chopilot.measure.sources';   // 재생 ID → 원본 파일명. 새로고침해도 유지된다.
const ACTOR_KEY = 'chopilot.measure.actor';       // 보정·승격을 기록할 사람
const AUTO_SECONDS_KEY = 'chopilot.measure.autoSeconds';   // 자동 반복 간격(초)

const state = {
  metrics: null,
  observations: [],
  routes: [],
  splitRoutes: 0,
  sources: loadJson(SOURCES_KEY, {}),   // observationId → 원본 파일명
  selected: null,
  detail: null,
  scoring: loadScoring(),
  review: [],
  decisions: [],
  suggestions: null,
  concepts: [],
  proposals: [],          // 업무 개선 제안(결정된 것 포함)
  proposalCriteria: null, // 선정 기준 — 자체 평가가 고친다
  proposalOutcomes: [],   // 종류별 채택률 — 자체 평가의 입력
  proposalSkipped: [],    // 마지막 생성에서 떨어진 후보. 서버가 보관하지 않으므로 새로고침하면 사라진다
  inferences: null,       // AI 추정 이력(trusted 포함). null이면 측정자 미지정이라 안 불렀다
  inferenceTotal: 0,      // 서버가 자르기 전 개수 — 잘린 사실을 화면이 말해야 한다
  inferenceFilter: 'all', // all | pending | trusted | excluded
  screens: {},            // 서명 → 화면 필드(ref·라벨·값). AI 판단을 원본과 대조하는 데 쓴다
  thetaHigh: 0.8,         // 서버의 Mapping:ThetaHigh — 신뢰도 판정 기준선
  editing: null,          // 보정 중인 검수 큐 항목
  actor: localStorage.getItem(ACTOR_KEY) || '',
  knowledge: [],          // 지식 문서(초안 + 게시)
  knowledgeVersion: 0,
  signals: null,          // 미지 개념 후보
  entities: null,         // 엔티티 결정 결과(H5)
  completions: null,      // 작업 완료 신호 집계(필수 필드 규칙의 증거)
  storage: null,          // 영속화 상태 — durable=false면 재시작에 전부 사라진다
  auth: null,             // 주체 해석 방식 — verified=false면 헤더로 누구든 사칭할 수 있다
  foundation: null,       // 기반 출처 상태 + 마스터 요약
  reconcile: null,        // 관측 ↔ 마스터 대사 결과
  myProfile: null,        // 사용자 축 뷰(저장되지 않음 — 매 조회마다 렌더된다)
  openDraft: null,

  // 자동 반복 적재. batch는 파싱된 관측이라 회차마다 파일을 다시 읽지 않는다.
  batch: [],
  autoOn: false,
  autoSeconds: Number(localStorage.getItem(AUTO_SECONDS_KEY)) || 10,
  autoTimer: null,
  autoRounds: 0,
  autoFailedRounds: 0,
};

/* ── 유틸 ─────────────────────────────────────────────── */

const $ = (id) => document.getElementById(id);

function esc(v) {
  if (v === null || v === undefined) return '';
  return String(v).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

const pct = (r) => `${(r * 100).toFixed(1)}%`;
const shortSig = (s) => (s || '').replace(/^sha256:/, '').slice(0, 12);

function ratio(ok, total) {
  const o = Number(ok) || 0, t = Number(total) || 0;
  return t > 0 ? o / t : null;
}

function loadJson(key, fallback) {
  try {
    return Object.assign(fallback, JSON.parse(localStorage.getItem(key) || '{}'));
  } catch {
    return fallback;
  }
}

// state 초기화 시점에 호출되므로 호이스팅되는 함수 선언이어야 한다.
function loadScoring() {
  return loadJson(SCORING_KEY, { h1Total: '', h1Ok: '', h3Total: '', h3Ai: '', h3Base: '', h2: {} });
}

const saveScoring = () => localStorage.setItem(SCORING_KEY, JSON.stringify(state.scoring));
const saveSources = () => localStorage.setItem(SOURCES_KEY, JSON.stringify(state.sources));

async function api(path, options) {
  const res = await fetch(path, options);
  if (!res.ok) throw new Error(`${res.status} ${await errorText(res)}`);
  return res.json();
}

// 서버 오류는 {"error": "..."} 로 온다. 본문을 통째로 붙이면 사람이 읽는 자리에
// `{"error":"..."}` 가 그대로 뜬다 — 필드만 뽑고, 그 모양이 아니면 원문을 쓴다.
async function errorText(res) {
  const body = await res.text();
  try {
    const parsed = JSON.parse(body);
    if (parsed && typeof parsed.error === 'string') return parsed.error;
  } catch { /* JSON이 아니면 원문 그대로 */ }
  return body;
}

// 진행 중인 버튼을 잠그고 "…중"으로 바꾼다. 이건 피드백이지 가드가 아니다 —
// 탭 두 개·새로고침·직접 호출은 그대로 들어오므로 중복을 실제로 막는 건 서버의 409다.
async function whileBusy(button, label, work) {
  const original = button.textContent;
  button.disabled = true;
  button.textContent = label;
  try {
    return await work();
  } finally {
    button.disabled = false;
    button.textContent = original;
  }
}

/* ── 적재 ─────────────────────────────────────────────── */

function log(message, kind) {
  const box = $('uploadLog');
  box.hidden = false;
  const line = document.createElement('div');
  if (kind) line.className = `log-${kind}`;
  line.textContent = message;
  box.appendChild(line);
  box.scrollTop = box.scrollHeight;
}

// 적재 결과의 source 3분류. 미스와 AI 호출은 다르다 — 저신뢰 캐시 재사용은 둘 다 아니다.
const SOURCE_LABEL = {
  trusted_cache: '캐시 적중',
  deferred_cache: '캐시 재사용(θ 미만)',
  ai: 'AI 추론',
  excluded: '제외됨(판단 사용 안 함)',
};

// 매핑 1건이 어디서 왔는지. 사용자가 읽는 자리에 stub/cache 같은 내부 토큰을 그대로 두면
// 그것이 무엇인지 아는 사람만 판단 결과를 검수할 수 있다.
const PROVENANCE_LABEL = {
  ai: 'AI 추론',
  stub: '스텁(모의 AI)',
  cache: '캐시에서 재사용',
  user: '사람이 보정',
  seed: '시드',
};

const provenanceLabel = (p) => PROVENANCE_LABEL[p] || p || '—';

// 온톨로지의 정규 개념명은 영문(Vendor)이고 화면은 한국어(거래처)다. 판단 결과를 화면 말로
// 되돌려 주지 않으면 사용자는 "AI가 무엇이라고 했는지"를 온톨로지를 뒤져야 알 수 있다.
function conceptLabel(name) {
  if (!name) return '';
  const c = state.concepts.find((x) => x.name === name);
  const korean = (c?.aliases || []).find((a) => /[가-힣]/.test(a));
  return korean || name;
}

// 신뢰도는 숫자 자체가 아니라 θ 대비 위치가 뜻을 갖는다 — 0.60은 θ가 0.5면 통과, 0.8이면 탈락이다.
function confidenceCell(value) {
  const theta = state.thetaHigh;
  const pass = value >= theta;
  return `<span class="${pass ? 'ok' : 'warn-text'}">${value.toFixed(2)}</span>`
    + ` <span class="hint">${pass ? `θ ${theta} 이상 · 적중` : `θ ${theta} 미만 · 적중 안 됨`}</span>`;
}

// 덤프 파일은 {signature, observation:{...}} 로 감싸여 있고, 벗겨진 이벤트도 받아들인다.
function extractObservation(json) {
  const candidate = json.observation || json.Observation || json;
  const hasEventId = Object.keys(candidate).some((k) => k.toLowerCase() === 'eventid');
  if (!hasEventId) throw new Error('ObservationEvent 형식이 아니다 (eventId 없음)');
  return candidate;
}

function withEventId(observation, id) {
  const copy = { ...observation };
  for (const key of Object.keys(copy)) {
    if (key.toLowerCase() === 'eventid') delete copy[key];
  }
  copy.eventId = id;
  return copy;
}

// 파일을 읽어 관측으로 파싱만 한다. 전송과 분리한 것은 자동 반복이 <b>파일을 다시 읽지 않고</b>
// 같은 관측을 재전송하기 위해서다 — 매 회차 디스크를 다시 읽으면 간격이 파일 크기에 좌우된다.
async function parseBatch(fileList) {
  const files = Array.from(fileList).filter((f) => f.name.endsWith('.json'));
  if (files.length === 0) { log('JSON 파일이 없다', 'err'); return []; }

  const batch = [];
  for (const file of files) {
    try {
      batch.push({ name: file.name, observation: extractObservation(JSON.parse(await file.text())) });
    } catch (err) {
      log(`${file.name} → 읽기 실패: ${err.message}`, 'err');
    }
  }
  return batch;
}

// 파싱된 관측들을 순서대로 전송. 실패는 세어서 돌려준다 — 자동 반복의 중단 조건이다.
async function replayBatch(batch) {
  const assignNewIds = $('newIds').checked;
  let sent = 0;

  for (const { name, observation } of batch) {
    try {
      let payload = observation;
      if (assignNewIds) {
        const stamp = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
        payload = withEventId(observation, `${name.replace(/\.json$/, '')}-${stamp}-${Math.random().toString(36).slice(2, 6)}`);
      }

      const result = await api('/v1/observations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });

      state.sources[result.observation_id] = name;
      saveSources();
      sent++;
      // source로 적는다. cache_hit만 보면 deferred_cache(저신뢰 캐시 재사용)가 'AI 추론'으로
      // 찍혀, 반복 적재 로그가 매 회차 Bedrock을 부르는 것처럼 보인다 — 미스 ≠ AI 호출이다.
      log(`${name} → ${SOURCE_LABEL[result.source] || result.source} · ${shortSig(result.signature)} · ${result.business_object}`, 'ok');
    } catch (err) {
      log(`${name} → 실패: ${err.message}`, 'err');
    }
  }

  return { sent, failed: batch.length - sent };
}

async function uploadFiles(fileList) {
  const batch = await parseBatch(fileList);
  if (batch.length === 0) return;

  state.batch = batch;          // 자동 반복이 다시 쓸 원본
  await replayBatch(batch);
  await refreshAll();
}

/* ── 자동 반복 적재 ───────────────────────────────────── */

/// 전 건이 이만큼 연속 실패하면 멈춘다. 서버가 죽은 뒤 로그를 무한히 채우지 않기 위해서다.
const AUTO_MAX_FAILED_ROUNDS = 5;

function scheduleAuto() {
  clearTimeout(state.autoTimer);
  // setInterval이 아니라 체이닝이다. 한 회차(전송 + 15개 조회)가 간격보다 오래 걸리면
  // setInterval은 회차를 겹쳐 쏘고, 그러면 지표가 관측이 아니라 브라우저 폴링을 재게 된다.
  state.autoTimer = setTimeout(autoTick, Math.max(1, state.autoSeconds) * 1000);
}

async function autoTick() {
  state.autoTimer = null;
  if (!state.autoOn || state.batch.length === 0) return;

  const { failed } = await replayBatch(state.batch);
  state.autoRounds++;
  state.autoFailedRounds = failed === state.batch.length ? state.autoFailedRounds + 1 : 0;

  if (state.autoFailedRounds >= AUTO_MAX_FAILED_ROUNDS) {
    log(`자동 반복 중단 — ${AUTO_MAX_FAILED_ROUNDS}회 연속 전 건 실패`, 'err');
    setAuto(false);
    await refreshAll();
    return;
  }

  await refreshAll();
  if (state.autoOn) scheduleAuto();   // refreshAll 사이에 사용자가 껐을 수 있다
}

function setAuto(on) {
  state.autoOn = on && state.batch.length > 0;
  $('autoReplay').checked = state.autoOn;

  if (state.autoOn) {
    state.autoRounds = 0;
    state.autoFailedRounds = 0;
    log(`자동 반복 시작 — ${state.batch.length}건 / ${state.autoSeconds}초 간격`);
    scheduleAuto();
  } else {
    clearTimeout(state.autoTimer);
    state.autoTimer = null;
  }
  renderAuto();
}

function renderAuto() {
  const box = $('autoStatus');
  const toggle = $('autoReplay');

  toggle.disabled = state.batch.length === 0;
  if (state.batch.length === 0) {
    box.className = 'hint';
    box.textContent = '파일을 먼저 올려야 반복할 원본이 생긴다. '
      + '브라우저는 새로고침 후 예전 파일을 다시 읽을 수 없으므로 자동 반복은 항상 꺼진 채로 시작한다.';
    return;
  }

  box.className = state.autoOn ? 'warn' : 'hint';
  box.textContent = state.autoOn
    ? `반복 중 — ${state.batch.length}건 × ${state.autoRounds}회차, ${state.autoSeconds}초 간격`
      + (state.autoFailedRounds ? ` · 연속 실패 ${state.autoFailedRounds}/${AUTO_MAX_FAILED_ROUNDS}` : '')
    : `반복 대상 ${state.batch.length}건 대기 중 (${state.batch.map((b) => b.name).join(', ')})`;
}

/* ── 조회 & 렌더 ──────────────────────────────────────── */

// 지식 조회는 personal 스코프 문서를 위해 측정자 헤더가 필요하다.
const asUser = () => (state.actor ? { headers: { 'X-ChoPilot-User': state.actor } } : undefined);

async function refreshAll() {
  try {
    const [metrics, observations, signatures, review, decisions, suggestions, ontology,
           knowledge, signals, entities, foundation, reconcile, completions, storage, auth,
           proposals, inferences] = await Promise.all([
      api('/v1/metrics'), api('/v1/observations'), api('/v1/signatures'),
      api('/v1/review'), api('/v1/decisions?limit=20'), api('/v1/suggestions?limit=1'), api('/v1/ontology'),
      api('/v1/knowledge', asUser()), api('/v1/knowledge/signals'), api('/v1/entities'),
      api('/v1/foundation'), api('/v1/foundation/reconcile'), api('/v1/completions?limit=1'),
      api('/v1/storage'), api('/v1/auth'), api('/v1/proposals?limit=100'),
      // 개인 스코프를 본인 것만 실으려면 주체가 필요하다 — 측정자가 비어 있으면 부르지 않는다.
      state.actor ? api('/v1/inferences?limit=200', asUser()) : Promise.resolve(null),
    ]);
    state.metrics = metrics;
    state.observations = observations.items;
    state.routes = signatures.routes;
    state.splitRoutes = signatures.splitRoutes;
    state.review = review.entries;
    state.screens = review.screens || {};        // 서명 → 그 화면에 실제로 보였던 필드
    state.thetaHigh = review.thetaHigh ?? 0.8;   // 신뢰도를 판정으로 바꾸는 기준선
    state.proposals = proposals.items;
    state.proposalCriteria = proposals.criteria;
    state.proposalOutcomes = proposals.outcomes;
    state.inferences = inferences ? inferences.entries : null;   // null = 측정자 미지정
    state.inferenceTotal = inferences ? inferences.total : 0;    // 자르기 전 개수
    state.decisions = decisions.entries;
    state.suggestions = suggestions.stats;
    state.concepts = ontology.concepts;
    state.knowledge = knowledge.items;
    state.knowledgeVersion = knowledge.version;
    state.signals = signals;
    state.entities = entities;
    state.foundation = foundation;
    state.reconcile = reconcile;
    state.completions = completions;
    state.storage = storage;
    state.auth = auth;
    state.myProfile = knowledge.items.find((d) => d.kind === 'view' && d.axis === 'user') || null;
    $('health').className = 'pill pill-pass';
    $('health').textContent = '서버 연결됨';
  } catch (err) {
    $('health').className = 'pill pill-fail';
    $('health').textContent = `서버 오류: ${err.message}`;
  }

  renderMetrics();
  renderSignatures();
  renderReview();
  renderProposalCriteria();
  renderProposals();
  renderProposalSkipped();
  renderInferences();
  renderDecisions();
  renderKnowledge();
  renderAuto();
  renderStorage();
  renderAuth();
  renderFoundation();
  renderObservations();
  renderVerdict();
  if (state.selected) await selectObservation(state.selected);
}

function metricCard(label, value, target, verdict) {
  const cls = verdict === null ? '' : verdict ? 'pass' : 'fail';
  return `<div class="metric ${cls}">
    <div class="label">${esc(label)}</div>
    <div class="value">${esc(value)}</div>
    <div class="target">${esc(target)}</div>
  </div>`;
}

function renderMetrics() {
  const m = state.metrics;
  const box = $('metrics');
  if (!m || m.observations === 0) {
    box.innerHTML = '<p class="empty">아직 적재된 스냅샷이 없다. 위에서 파일을 올려라.</p>';
    $('thetaWarn').hidden = true;
    return;
  }

  box.innerHTML = [
    metricCard('캐시 적중률 (H3b)', pct(m.cacheHitRatio), `통과선 ≥ ${pct(PASS.h3b)}`, m.cacheHitRatio >= PASS.h3b),
    metricCard('지연 p95 (H6)', `${m.latencyP95Ms} ms`,
      `p50 ${m.latencyP50Ms} · 최대 ${m.latencyMaxMs} · 서버 구간만 ≤ ${PASS.h6}ms`, m.latencyP95Ms <= PASS.h6),
    metricCard('관측 수', m.observations, `서명 ${m.distinctSignatures}종`, null),
    metricCard('AI 호출 (H6)', m.aiCalls,
      `입력 ${m.inputTokens} / 출력 ${m.outputTokens} 토큰 · 재추론 보류 ${m.deferredReuses || 0}회`, null),
    metricCard('마스킹 (H4)', m.maskedRefs, `잔존 PII ${residualTotal()}건`, residualTotal() === 0),
    suggestionCard(),
  ].join('');

  // T1 — 스텁 신뢰도 0.6 < 기본 θ 0.8 이면 적중률이 구조적으로 0이 된다.
  const stubOnly = (m.byProvenance || {}).stub > 0 && !(m.byProvenance || {}).ai;
  const pending = (m.byStatus || {}).pending_review > 0;
  const warn = $('thetaWarn');
  if (stubOnly && pending && m.cacheHitRatio === 0) {
    warn.hidden = false;
    warn.innerHTML = '<strong>측정 함정 T1.</strong> 매핑이 전부 <code>pending_review</code>이고 적중률이 0이다 — '
      + 'StubAiMapper의 신뢰도(0.6)가 <code>Mapping:ThetaHigh</code>(기본 0.8) 미만이라 캐시가 구조적으로 적중하지 못한다. '
      + '<code>--Mapping:ThetaHigh=0.5</code> 로 재기동하거나 <code>UseBedrock=true</code>로 실제 AI를 써라. '
      + 'AI 호출은 재추론 백오프(<code>Mapping:ReinferAfterHours</code>, 기본 24h)가 막고 있지만, '
      + '<b>적중률 0은 그대로다</b> — 검수·보정(<code>/v1/review</code>)으로 신뢰도를 올려야 해소된다.';
  } else {
    warn.hidden = true;
  }
}

const residualTotal = () => state.observations.reduce((sum, o) => sum + o.residualPiiCount, 0);

// 제안 수락률 (ARCHITECTURE §9 KPI). Phase 0 가설이 아니라 통과선이 없어 판정하지 않는다.
function suggestionCard() {
  const s = state.suggestions;
  if (!s || s.impressions === 0)
    return metricCard('제안 수락률 (§9)', '—', '아직 노출된 제안 없음', null);

  const decided = s.accepted + s.rejected;
  return metricCard('제안 수락률 (§9)',
    decided === 0 ? '판단 없음' : pct(s.acceptanceRate),
    `노출 ${s.impressions} · 수락 ${s.accepted} / 거부 ${s.rejected} · 응답률 ${pct(s.responseRate)}`, null);
}

/* ── 검수 큐 & 보정 (HITL) ────────────────────────────── */

function renderReview() {
  const box = $('review');
  if (state.review.length === 0) {
    box.innerHTML = '<p class="empty">검수 대기 매핑 없음 — 저신뢰로 남은 화면이 없다</p>';
    renderCorrection();
    return;
  }

  const rows = state.review.map((e) => {
    const screen = state.screens[e.signature];
    return `<tr class="clickable ${state.editing && state.editing.signature === e.signature && state.editing.scope === e.scope ? 'selected' : ''}"
      data-sig="${esc(e.signature)}" data-scope="${esc(e.scope)}">
    <td>
      ${screen ? `<strong>${esc(screen.title || screen.route)}</strong>
                  <div class="hint mono">${esc(screen.route)} · ${shortSig(e.signature)}</div>`
               : `<span class="mono">${shortSig(e.signature)}</span>
                  <div class="hint">이 서명의 관측이 남아 있지 않다</div>`}
    </td>
    <td>${esc(e.businessObject)}</td>
    <td class="mono">${esc(e.scope)}</td>
    <td class="num">${confidenceCell(e.confidence)}</td>
    <td class="num">${e.mapping.length}</td>
    <td class="hint">${e.lastInferredAt ? esc(e.lastInferredAt.slice(0, 19).replace('T', ' ')) : '사람이 만든 매핑'}</td>
  </tr>`;
  }).join('');

  box.innerHTML = `<table>
    <thead><tr>
      <th>화면</th><th>업무객체</th><th>스코프</th>
      <th class="num">신뢰도</th><th class="num">필드</th><th>마지막 추론</th>
    </tr></thead>
    <tbody>${rows}</tbody></table>`;

  box.querySelectorAll('tr.clickable').forEach((tr) => {
    tr.addEventListener('click', () => {
      const entry = state.review.find((e) => e.signature === tr.dataset.sig && e.scope === tr.dataset.scope);
      state.editing = state.editing === entry ? null : entry;
      renderReview();
    });
  });

  renderCorrection();
}

/* ── 업무 개선 제안 ───────────────────────────────────── */

const PROPOSAL_KIND = {
  screen_split: '화면 갈림',
  workflow_shortcut: '업무 흐름',
  rework: '되돌아오기',
  master_gap: '기준정보 결손',
  correction_hotspot: '보정 집중',
};

const kindLabel = (k) => PROPOSAL_KIND[k] || k;

function renderProposalCriteria() {
  const box = $('proposalCriteria');
  const c = state.proposalCriteria;
  if (!c) { box.innerHTML = '<p class="empty">기준 없음</p>'; return; }

  const outcomes = Object.fromEntries((state.proposalOutcomes || []).map((o) => [o.kind, o]));

  const rows = c.rules.map((r) => {
    const o = outcomes[r.kind];
    // 기준을 고치는 건 평점이다. 채택률은 참고로만 보여준다 —
    // "근거는 맞지만 지금 손댈 수 없다"는 기각이 흔해서 유용성과 어긋난다.
    const rating = o && o.rated > 0
      ? `${o.meanAccuracy.toFixed(1)} / ${o.meanUsefulness.toFixed(1)} / ${o.meanActionability.toFixed(1)}`
        + ` <span class="hint">(${o.rated}건)</span>`
      : `<span class="hint">평가 없음</span>`;
    const rate = o && o.decided > 0
      ? `<span class="hint">${pct(o.acceptanceRate)}</span>`
      : `<span class="hint">—</span>`;
    return `<tr class="${r.enabled ? '' : 'row-gone'}">
      <td>${esc(kindLabel(r.kind))} <div class="hint mono">${esc(r.kind)}</div></td>
      <td>${r.enabled
        ? '<span class="badge badge-mask">켜짐</span>'
        : `<span class="badge badge-leak">꺼짐</span><div class="hint">${esc(r.disabledReason || '이유 미기재')}</div>`}</td>
      <td class="num">${r.minOccurrences}회</td>
      <td class="num">${r.minDistinctUsers}명</td>
      <td class="num">${r.minScore.toFixed(2)}</td>
      <td class="num">${o ? o.proposed : 0}</td>
      <td class="num">${rating}</td>
      <td class="num">${rate}</td>
    </tr>`;
  }).join('');

  box.innerHTML = `<p class="hint">
      기준 <strong>v${c.version}</strong> · ${esc(c.updatedAt.slice(0, 16).replace('T', ' '))} —
      ${esc(c.rationale)}<br>
      가중치: 근거 ${c.evidenceWeight} · 도달 ${c.reachWeight} · 최근성 ${c.recencyWeight} · 영향 ${c.impactWeight}
    </p>
    <table>
      <thead><tr>
        <th>종류</th><th>상태</th><th class="num">최소 관측</th><th class="num">최소 인원</th>
        <th class="num">최소 점수</th><th class="num">제안</th>
        <th class="num">평균 평가<br><span class="hint">정확성/유용성/실행</span></th><th class="num">채택률<br><span class="hint">참고</span></th>
      </tr></thead>
      <tbody>${rows}</tbody>
    </table>`;
}

// 평가 축 셋. 한 숫자로 뭉치면 "틀렸다"와 "못 한다"가 같은 값이 되고,
// 그러면 옳게 찾아낸 종류가 조직 사정 때문에 꺼진다.
const RATING_AXES = [
  ['accuracy', '정확성', '이런 현상이 실제로 있나'],
  ['usefulness', '유용성', '알 가치가 있나'],
  ['actionability', '실행 가능성', '우리가 할 수 있나 (기준을 움직이지 않는다)'],
];

function ratingAxes(i) {
  const options = [0, 1, 2, 3, 4, 5]
    .map((n) => `<option value="${n}"${n === 3 ? ' selected' : ''}>${n}</option>`).join('');
  return `<span class="rating-group">
    ${RATING_AXES.map(([key, label, help]) => `
      <label class="rating" title="${esc(help)}">${label}
        <select class="text" data-rating="${i}" data-axis="${key}">${options}</select>
      </label>`).join('')}
    <label class="rating" title="세 축 모두 비우고 결정한다 — 그 건은 기준 학습에 쓰이지 않는다">
      <input type="checkbox" data-skip="${i}"> 평가 안 함
    </label>
  </span>`;
}

function renderProposals() {
  const box = $('proposals');
  const items = state.proposals || [];
  if (items.length === 0) {
    box.innerHTML = '<p class="empty">아직 제안이 없다 — 위에서 생성해 보라</p>';
    return;
  }

  box.innerHTML = items.map((p, i) => {
    const ev = p.evidence;
    const bars = p.score.dimensions.map((d) => `
      <div class="dim">
        <span class="dim-name">${esc(d.name)}</span>
        <span class="dim-bar"><i style="width:${Math.round(d.value * 100)}%"></i></span>
        <span class="dim-val">${d.value.toFixed(2)}<span class="hint"> ×${d.weight}</span></span>
        <span class="hint">${esc(d.note)}</span>
      </div>`).join('');

    const decided = p.status !== 'proposed';
    return `<div class="detail ${decided ? 'row-gone' : ''}">
      <div class="detail-head">
        <h3 style="margin:0">
          <span class="badge">${esc(kindLabel(p.kind))}</span> ${esc(p.title)}
        </h3>
        <div>
          ${decided
            ? `<span class="badge ${p.status === 'accepted' ? 'badge-mask' : 'badge-leak'}">
                 ${p.status === 'accepted' ? '채택' : '기각'}</span>
               ${p.rating
                 ? `<span class="badge">정확성 ${p.rating.accuracy}</span>
                    <span class="badge">유용성 ${p.rating.usefulness}</span>
                    <span class="badge">실행 ${p.rating.actionability}</span>`
                 : '<span class="badge">평가 없음</span>'}
               <span class="hint">${esc(p.decidedBy || '')} ${esc((p.decidedAt || '').slice(0, 16).replace('T', ' '))}</span>`
            : `<span class="hint">점수 ${p.score.total.toFixed(2)} · 기준 v${p.criteriaVersion}</span>
               ${ratingAxes(i)}
               <button class="btn btn-sm btn-primary" data-accept="${i}">채택</button>
               <button class="btn btn-sm btn-ghost" data-reject="${i}">기각</button>`}
        </div>
      </div>
      <p>${esc(p.body)}</p>
      <p class="hint">
        근거: <strong>${ev.occurrences}회</strong> · <strong>${ev.distinctUsers}명</strong> ·
        ${esc(ev.firstSeen.slice(0, 10))} ~ ${esc(ev.lastSeen.slice(0, 10))} ·
        <span class="mono">${ev.refs.map(esc).join(', ')}</span>
      </p>
      <div class="dims">${bars}</div>
      ${p.decisionNote ? `<p class="hint">사유: ${esc(p.decisionNote)}</p>` : ''}
    </div>`;
  }).join('');

  // 평가는 그 제안 카드 안의 select들에서 읽는다 — 카드마다 다른 값이라 인덱스로 짚는다.
  const ratingOf = (i) => {
    if (box.querySelector(`[data-skip="${i}"]`)?.checked) return null;
    const rating = {};
    for (const [key] of RATING_AXES) {
      const el = box.querySelector(`[data-rating="${i}"][data-axis="${key}"]`);
      if (!el) return null;
      rating[key] = Number(el.value);
    }
    return rating;
  };

  box.querySelectorAll('[data-accept]').forEach((b) =>
    b.addEventListener('click', () =>
      decideProposal(items[Number(b.dataset.accept)], true, ratingOf(b.dataset.accept), b)));
  box.querySelectorAll('[data-reject]').forEach((b) =>
    b.addEventListener('click', () =>
      decideProposal(items[Number(b.dataset.reject)], false, ratingOf(b.dataset.reject), b)));
}

function renderProposalSkipped() {
  const box = $('proposalSkipped');
  const rows = state.proposalSkipped || [];
  if (rows.length === 0) {
    box.innerHTML = '<p class="empty">생성을 돌리면 탈락 사유가 여기 쌓인다 (새로고침하면 사라진다 — 서버가 보관하지 않는다)</p>';
    return;
  }
  box.innerHTML = `<table>
    <thead><tr><th>종류</th><th>대상</th><th class="num">점수</th><th>탈락 사유</th></tr></thead>
    <tbody>${rows.map((s) => `<tr>
      <td>${esc(kindLabel(s.kind))}</td>
      <td class="mono">${esc(s.key)}</td>
      <td class="num">${s.score.total.toFixed(2)}</td>
      <td class="hint">${esc(s.reason)}</td>
    </tr>`).join('')}</tbody></table>`;
}

async function generateProposals(button) {
  const msg = $('proposalMsg');
  const actor = requireActor(msg);
  if (!actor) return;

  await whileBusy(button, '평가·생성 중…', async () => {
    try {
      const r = await api('/v1/proposals/generate', {
        method: 'POST',
        headers: { 'X-ChoPilot-User': actor },
      });
      state.proposalSkipped = r.skipped;
      const tuned = r.tuning && r.tuning.changes.length > 0;
      msg.className = 'hint';
      msg.textContent = `제안 ${r.proposed.length}건 · 탈락 ${r.skipped.length}건 · 기준 v${r.criteriaVersion}`
        + (tuned ? ` — 자체 평가로 기준을 고쳤다: ${r.tuning.changes.map((c) => c.reason).join(' / ')}` : '');
      await refreshAll();
      renderProposalSkipped();
    } catch (err) {
      msg.className = 'warn';
      msg.textContent = `생성 실패: ${err.message}`;
    }
  });
}

async function decideProposal(proposal, accept, rating, button) {
  const msg = $('proposalMsg');
  const actor = requireActor(msg);
  if (!actor) return;

  const note = accept ? null : prompt(`기각 사유 (선택) — "${proposal.title}"`, '');
  if (!accept && note === null) return;   // 취소

  await whileBusy(button, accept ? '채택 중…' : '기각 중…', async () => {
    try {
      await api(`/v1/proposals/${encodeURIComponent(proposal.id)}/decide`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-ChoPilot-User': actor },
        body: JSON.stringify({ accept, rating, note: note || null }),
      });
      msg.className = 'hint';
      // 채택 여부가 아니라 평가가 기준을 고친다 — 평가를 비웠으면 그렇다고 말해 준다.
      msg.textContent = `${accept ? '채택' : '기각'}됨 — `
        + (rating
          ? `정확성 ${rating.accuracy} · 유용성 ${rating.usefulness} · 실행 가능성 ${rating.actionability}`
            + ' 가 기준 학습에 들어간다 (실행 가능성은 기준을 움직이지 않는다).'
          : '평가를 매기지 않아 기준 학습에는 쓰이지 않는다.');
      await refreshAll();
    } catch (err) {
      msg.className = 'warn';
      msg.textContent = `결정 실패: ${err.message}`;
    }
  });
}

/* ── AI 추정 이력 ─────────────────────────────────────── */

// 검수 큐가 "손봐야 하는 것"이라면 이쪽은 서 있는 판단 전부의 대장이다.
// 승격·보정으로 큐에서 빠진 판단도 계속 쓰이므로 보이지 않으면 존재를 알 수 없다.
function renderInferences() {
  const box = $('inferences');

  if (state.inferences === null) {
    box.innerHTML = '<p class="empty">상단바에 측정자 ID를 넣으면 보인다 — 개인 스코프는 본인 것만 싣는다</p>';
    return;
  }
  if (state.inferences.length === 0) {
    box.innerHTML = '<p class="empty">아직 서 있는 추정이 없다</p>';
    return;
  }

  // 인덱스는 state.inferences 기준을 그대로 들고 다닌다. 필터된 배열로 다시 매기면
  // 버튼이 화면에 보이는 행이 아니라 다른 추정을 건드린다.
  const all = state.inferences.map((e, i) => ({ e, i }));

  const FILTERS = [
    ['all', '전체', () => true],
    ['pending', '검수 대기', ({ e }) => !e.excluded && e.status !== 'trusted'],
    ['trusted', '채택됨', ({ e }) => !e.excluded && e.status === 'trusted'],
    ['excluded', '제외됨', ({ e }) => e.excluded],
  ];
  const active = FILTERS.find((f) => f[0] === state.inferenceFilter) || FILTERS[0];
  const shown = all.filter(active[2]);

  const chips = FILTERS.map(([key, label, pred]) =>
    `<button class="btn btn-sm ${key === active[0] ? '' : 'btn-ghost'}" data-filter="${key}">`
    + `${label} ${all.filter(pred).length}</button>`).join(' ');

  // 잘린 사실을 적는다 — 침묵하는 절단은 "이게 전부"로 읽힌다.
  const truncated = state.inferenceTotal > state.inferences.length
    ? `<span class="warn-text">서 있는 추정 ${state.inferenceTotal}건 중 최근 ${state.inferences.length}건만 실렸다</span>`
    : `서 있는 추정 ${state.inferenceTotal}건`;

  const screenCell = (signature) => {
    const screen = state.screens[signature];
    return screen
      ? `<strong>${esc(screen.title || screen.route)}</strong>
         <div class="hint mono">${esc(screen.route)}</div>`
      : `<span class="mono">${shortSig(signature)}</span>
         <div class="hint">관측이 남아 있지 않다</div>`;
  };

  const scopeBadge = (scope) => scope.startsWith('personal:')
    ? '<span class="badge">개인</span>'
    : `<span class="badge">${esc(scope)}</span>`;

  const rows = shown.map(({ e, i }) => {
    // lastInferredAt이 null이면 AI가 아니라 사람이 만든 매핑이다 — 그 구분이 이력의 요점이다.
    const when = e.lastInferredAt
      ? esc(e.lastInferredAt.slice(0, 16).replace('T', ' '))
      : '<span class="badge">사람이 만듦</span>';

    const status = e.excluded
      ? '<span class="badge badge-leak">제외됨</span>'
      : (e.status === 'trusted'
        ? '<span class="badge badge-mask">채택됨</span>'
        : '<span class="badge">검수 대기</span>');

    return `<tr class="${e.excluded ? 'row-gone' : ''}">
      <td class="hint">${when}</td>
      <td>${screenCell(e.signature)}</td>
      <td>${esc(e.businessObject)}</td>
      <td class="num">${e.mapping.length}</td>
      <td class="num">${confidenceCell(e.confidence)}</td>
      <td>${status} ${scopeBadge(e.scope)}</td>
      <td>
        <button class="btn btn-sm" data-edit="${i}" ${e.excluded ? 'disabled' : ''}>수정</button>
        <button class="btn btn-sm ${e.excluded ? '' : 'btn-ghost'}" data-toggle="${i}">
          ${e.excluded ? '포함' : '제외'}</button>
      </td>
    </tr>`;
  }).join('');

  box.innerHTML = `<div class="actions">${chips}<span class="hint">${truncated}</span></div>
    <table>
    <thead><tr>
      <th>추론 시각</th><th>화면</th><th>업무객체</th><th class="num">필드</th>
      <th class="num">신뢰도</th><th>상태</th><th></th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="7" class="empty">이 조건에 해당하는 추정이 없다</td></tr>'}</tbody></table>`;

  box.querySelectorAll('[data-filter]').forEach((btn) => {
    btn.addEventListener('click', () => { state.inferenceFilter = btn.dataset.filter; renderInferences(); });
  });

  box.querySelectorAll('[data-edit]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.editing = state.inferences[Number(btn.dataset.edit)];
      renderReview();
      $('correction').scrollIntoView({ block: 'center', behavior: 'smooth' });
    });
  });

  box.querySelectorAll('[data-toggle]').forEach((btn) => {
    btn.addEventListener('click', () => toggleInference(state.inferences[Number(btn.dataset.toggle)], btn));
  });
}

async function toggleInference(entry, button) {
  const msg = $('inferenceMsg');
  const actor = requireActor(msg);
  if (!actor) return;

  const screen = state.screens[entry.signature];
  const where = screen ? (screen.title || screen.route) : shortSig(entry.signature);
  const excluding = !entry.excluded;

  // 되돌릴 수 있는 스위치라 제외에는 확인을 묻지 않는다 — 되돌릴 수 없는 것에만 물어야
  // 확인 대화상자가 신호로 남는다. 다만 공용 스코프는 남에게도 가므로 그때는 묻는다.
  const shared = !entry.scope.startsWith('personal:');
  if (shared && !confirm(`"${where}"의 추정을 ${excluding ? '제외' : '포함'}한다.\n\n`
    + `스코프가 ${entry.scope}라 모두에게 영향이 간다.\n`
    + (excluding
      ? '판단은 지워지지 않는다 — 언제든 "포함"으로 되돌릴 수 있다.'
      : '이 판단을 다시 쓰기 시작한다.'))) return;

  await whileBusy(button, excluding ? '제외 중…' : '포함 중…', async () => {
    try {
      await api('/v1/inference/exclude', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-ChoPilot-User': actor },
        body: JSON.stringify({ signature: entry.signature, scope: entry.scope, excluded: excluding }),
      });
      msg.className = 'hint';
      msg.textContent = excluding
        ? `제외됨 — "${where}"의 판단을 쓰지 않는다. 지워지지 않았으니 "포함"으로 되돌릴 수 있다.`
        : `포함됨 — "${where}"의 판단을 다시 쓴다.`;
      if (excluding && state.editing === entry) state.editing = null;
      await refreshAll();
    } catch (err) {
      msg.className = 'warn';
      msg.textContent = `${excluding ? '제외' : '포함'} 실패: ${err.message}`;
    }
  });
}

function conceptOptions() {
  // 사용자는 화면에 보이는 말("단가")로 정정한다. 정규 이름과 별칭을 모두 후보로 준다.
  const names = state.concepts.flatMap((c) => [c.name, ...(c.aliases || [])]);
  return names.map((n) => `<option value="${esc(n)}"></option>`).join('');
}

function renderCorrection() {
  const box = $('correction');
  const entry = state.editing;
  if (!entry) { box.innerHTML = ''; return; }

  const screen = state.screens[entry.signature];
  const field = (ref) => (screen?.fields || []).find((f) => f.ref === ref);

  const rows = entry.mapping.map((m, i) => {
    const f = field(m.elementRef);
    return `<tr>
    <td>
      ${f?.label ? `<strong>${esc(f.label)}</strong>` : '<span class="badge">라벨 없음</span>'}
      <div class="hint mono">${esc(m.elementRef)}</div>
    </td>
    <td class="mono">${f?.masked
      ? '<span class="badge badge-mask">마스킹</span>'
      : (f?.value ? esc(f.value) : '<span class="badge">비어있음</span>')}</td>
    <td>
      <input type="text" class="text" data-field="${i}" value="${esc(m.concept)}" list="conceptList">
      <div class="hint">AI 판단: ${esc(conceptLabel(m.concept))} <span class="mono">(${esc(m.concept)})</span></div>
    </td>
    <td class="num">${confidenceCell(m.confidence)}</td>
    <td>${esc(provenanceLabel(m.provenance))}</td>
  </tr>`;
  }).join('');

  box.innerHTML = `<div class="detail">
    <div class="detail-head">
      <h3 style="margin:0">보정 — ${screen ? esc(screen.title || screen.route) : `<code>${shortSig(entry.signature)}</code>`}
        <span class="hint">${esc(entry.businessObject)}</span></h3>
      <div>
        <button id="applyCorrection" class="btn btn-primary btn-sm">보정 저장 (개인)</button>
        <button id="promoteEntry" class="btn btn-sm">그대로 승격 (공용)</button>
      </div>
    </div>
    <p class="hint">
      <strong>보정</strong>은 이 매핑을 <code>personal:{측정자}</code>에 신뢰도 1.0으로 심는다 — 본인에게만 적용된다.
      <strong>승격</strong>은 지금 매핑을 그대로 공용 평면에서 trusted로 올린다 — 모두에게 적용된다.
      개념은 별칭으로도 받는다(예: <code>단가</code>). 온톨로지에 없는 개념이 하나라도 있으면 전체가 거부된다.
    </p>
    <datalist id="conceptList">${conceptOptions()}</datalist>
    <div class="table-scroll"><table>
      <thead><tr>
        <th>화면의 칸</th><th>화면의 값</th><th>AI가 붙인 개념</th>
        <th class="num">신뢰도</th><th>어디서 왔나</th>
      </tr></thead>
      <tbody>${rows}</tbody></table></div>
    <p id="correctionMsg" class="hint"></p>
  </div>`;

  $('applyCorrection').addEventListener('click', () => applyCorrection(entry));
  $('promoteEntry').addEventListener('click', () => promoteEntry(entry));
}

// 미지정은 실패 시점이 아니라 상시로 보여야 한다 — 버튼을 누른 뒤에야 알면 이미 늦다.
function renderActor() {
  $('actor').closest('.actor-field').classList.toggle('unset', !state.actor);
}

function requireActor(msgBox) {
  if (state.actor) return state.actor;
  msgBox.className = 'warn';
  msgBox.textContent = '상단바의 측정자 ID를 먼저 입력하라 — 누가 승인했는지 남지 않는 보정은 되돌릴 수 없다.';
  // focus()는 스크롤을 옮긴다. 측정자 칸은 상단바에 상시 보이므로 옮길 필요가 없고,
  // 옮기면 방금 쓴 경고 문구가 화면 밖에 남는다 — 사용자는 이유 없이 튄 화면만 본다.
  $('actor').focus({ preventScroll: true });
  return null;
}

async function applyCorrection(entry) {
  const msg = $('correctionMsg');
  const actor = requireActor(msg);
  if (!actor) return;

  const mapping = entry.mapping.map((m, i) => ({
    elementRef: m.elementRef,
    concept: $('correction').querySelector(`[data-field="${i}"]`).value.trim(),
  }));

  try {
    const saved = await api('/v1/correction', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-ChoPilot-User': actor },
      body: JSON.stringify({ signature: entry.signature, businessObject: entry.businessObject, mapping }),
    });
    msg.className = 'hint';
    msg.textContent = `저장됨 — ${saved.scope} · 신뢰도 ${saved.confidence}. 같은 화면은 이제 AI 호출 없이 적중한다.`;
    state.editing = null;
    await refreshAll();
  } catch (err) {
    msg.className = 'warn';
    msg.textContent = `거부됨: ${err.message}`;
  }
}

async function promoteEntry(entry) {
  const msg = $('correctionMsg');
  const actor = requireActor(msg);
  if (!actor) return;

  try {
    await api('/v1/review/promote', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-ChoPilot-User': actor },
      body: JSON.stringify({ signature: entry.signature, scope: entry.scope }),
    });
    state.editing = null;
    await refreshAll();
  } catch (err) {
    msg.className = 'warn';
    msg.textContent = `승격 실패: ${err.message}`;
  }
}

/* ── 지식 (온톨로지·규칙) ─────────────────────────────── */

// 문서 본문은 markdown 조각이다. esc() 이후에만 태그를 만든다 — 순서가 뒤집히면 주입이 된다.
function miniMarkdown(text) {
  return esc(text)
    .split('\n')
    .map((line) => {
      const inline = line
        .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
        .replace(/`(.+?)`/g, '<code>$1</code>');
      if (inline.startsWith('## ')) return `<h4>${inline.slice(3)}</h4>`;
      if (inline.startsWith('- ')) return `<div class="md-li">• ${inline.slice(2)}</div>`;
      return inline.trim() ? `<p>${inline}</p>` : '';
    })
    .join('');
}

const isDraft = (d) => d.status === 'pending_review';

function renderKnowledge() {
  renderDrafts();
  renderKnowledgeSignals();
  renderCompletions();
  renderPublished();
  renderMyProfile();
  renderDraftBody();
}

function renderDrafts() {
  const box = $('knowledgeDrafts');
  const drafts = state.knowledge.filter(isDraft);
  if (drafts.length === 0) {
    box.innerHTML = '<p class="empty">승인 대기 초안 없음 — 위에서 신호를 집계해 보라</p>';
    return;
  }

  box.innerHTML = `<table>
    <thead><tr>
      <th>문서</th><th>타입</th><th>축</th>
      <th class="num">지지도</th><th class="num">사용자</th><th>출처</th><th>작업</th>
    </tr></thead>
    <tbody>${drafts.map((d) => `<tr class="clickable ${state.openDraft === d.id ? 'selected' : ''}" data-id="${esc(d.id)}">
      <td class="mono">${esc(d.id)}</td>
      <td><span class="badge">${esc(d.type)}</span>${d.concept && d.concept.sensitive ? ' <span class="badge badge-mask">민감</span>' : ''}</td>
      <td>${esc(d.axis)}</td>
      <td class="num">${d.provenance.supportCount}</td>
      <td class="num">${d.provenance.distinctUsers}</td>
      <td class="hint">${esc((d.provenance.signalRefs || []).join(', '))} · ${esc(d.createdBy)}</td>
      <td>
        <button class="btn btn-sm btn-primary" data-approve="${esc(d.id)}">승인</button>
        <button class="btn btn-sm btn-ghost" data-deprecate="${esc(d.id)}">폐기</button>
      </td>
    </tr>`).join('')}</tbody></table>`;

  box.querySelectorAll('tr.clickable').forEach((tr) => {
    tr.addEventListener('click', (e) => {
      if (e.target.closest('button')) return;
      state.openDraft = state.openDraft === tr.dataset.id ? null : tr.dataset.id;
      renderDrafts();
      renderDraftBody();
    });
  });
  box.querySelectorAll('[data-approve]').forEach((b) =>
    b.addEventListener('click', () => knowledgeAction(b.dataset.approve, 'approve')));
  box.querySelectorAll('[data-deprecate]').forEach((b) =>
    b.addEventListener('click', () => knowledgeAction(b.dataset.deprecate, 'deprecate')));
}

function renderDraftBody() {
  const box = $('draftBody');
  const doc = state.knowledge.find((d) => d.id === state.openDraft);
  if (!doc) { box.innerHTML = ''; return; }

  box.innerHTML = `<div class="detail">
    <h3 style="margin-top:0">${esc(doc.title)}</h3>
    <div class="md">${miniMarkdown(doc.body)}</div>
    ${doc.concept ? `<p class="hint">제안된 개념: <code>${esc(doc.concept.name)}</code> ·
      타입 ${esc(doc.concept.type)} · 별칭 ${esc((doc.concept.aliases || []).join(', '))} ·
      ${doc.concept.sensitive ? '<strong>민감</strong>' : '비민감'}</p>` : ''}
    ${doc.required ? `<p class="hint">필수 필드: <code>${esc((doc.required.concepts || []).join(', '))}</code></p>` : ''}
  </div>`;
}

function renderKnowledgeSignals() {
  const box = $('knowledgeSignals');
  const s = state.signals;
  if (!s || s.candidates.length === 0) {
    box.innerHTML = '<p class="empty">거부된 개념 시도 없음</p>';
    return;
  }

  box.innerHTML = `<table>
    <thead><tr><th>용어</th><th class="num">시도</th><th class="num">사용자</th><th>업무객체</th><th>마지막</th></tr></thead>
    <tbody>${s.candidates.map((c) => `<tr>
      <td>${esc(c.term)}</td>
      <td class="num">${c.attempts}</td>
      <td class="num">${c.distinctUsers}</td>
      <td class="hint">${esc((c.businessObjects || []).join(', '))}</td>
      <td class="hint">${esc(c.lastSeen.slice(0, 19).replace('T', ' '))}</td>
    </tr>`).join('')}</tbody></table>
    <p class="hint">초안이 되려면 <strong>지지도</strong>(기본 3회)와 <strong>k인</strong>(기본 2명)을 모두 넘어야 한다 —
    한 사람에게서만 나온 패턴을 조직 지식으로 올리면 그 사람의 활동이 유출된다.</p>`;
}

function renderPublished() {
  const box = $('knowledgePublished');
  const docs = state.knowledge.filter((d) => d.status === 'published' && d.kind === 'curated');
  box.innerHTML = `<p class="hint">지식 버전 <strong>${state.knowledgeVersion}</strong> ·
    개념 ${state.concepts.length}개 · 게시 문서 ${docs.length}건</p>`
    + `<table>
      <thead><tr><th>문서</th><th>타입</th><th>제목</th><th>승인자</th><th>작업</th></tr></thead>
      <tbody>${docs.map((d) => `<tr>
        <td class="mono">${esc(d.id)}</td>
        <td><span class="badge">${esc(d.type)}</span></td>
        <td>${esc(d.title)}</td>
        <td>${esc(d.approvedBy || '-')}</td>
        <td><button class="btn btn-sm btn-ghost" data-dep="${esc(d.id)}">폐기</button></td>
      </tr>`).join('')}</tbody></table>`;

  box.querySelectorAll('[data-dep]').forEach((b) =>
    b.addEventListener('click', () => knowledgeAction(b.dataset.dep, 'deprecate')));
}

function renderMyProfile() {
  const box = $('myProfile');
  if (!state.actor) { box.innerHTML = '<p class="empty">측정자 ID를 입력하면 본인 프로파일이 보인다</p>'; return; }
  if (!state.myProfile) { box.innerHTML = `<p class="empty">${esc(state.actor)}의 관측이 없다</p>`; return; }
  box.innerHTML = `<div class="detail"><div class="md">${miniMarkdown(state.myProfile.body)}</div></div>`;
}

async function knowledgeAction(id, action) {
  const msg = $('aggregateMsg');
  const actor = requireActor(msg);
  if (!actor) return;

  // 폐기는 개념이면 그 개념을 쓰는 매핑까지 정리한다 — 되돌릴 수 없다.
  if (action === 'deprecate' && !confirm(`${id} 를 폐기한다. 이 개념을 쓰는 매핑에서 필드가 제거된다. 계속할까?`)) return;

  try {
    const result = await api(`/v1/knowledge/${encodeURIComponent(id)}/${action}`, {
      method: 'POST',
      headers: { 'X-ChoPilot-User': actor },
    });
    msg.className = 'hint';
    msg.textContent = action === 'approve'
      ? `${id} 게시됨 — 재배포 없이 온톨로지·가이드에 반영된다`
      : `${id} 폐기됨 · 매핑 ${result.touchedMappings ?? 0}건 정리`;
    state.openDraft = null;
    await refreshAll();
  } catch (err) {
    msg.className = 'warn';
    msg.textContent = `실패: ${err.message}`;
  }
}

async function aggregate(dryRun) {
  const msg = $('aggregateMsg');
  const actor = requireActor(msg);
  if (!actor) return;

  try {
    const result = await api(`/v1/knowledge/aggregate?dryRun=${dryRun}`, {
      method: 'POST',
      headers: { 'X-ChoPilot-User': actor },
    });
    const count = dryRun ? result.drafts.length : result.submitted;
    msg.className = 'hint';
    msg.textContent = `${dryRun ? '미리보기' : '검수 큐 적재'}: 초안 ${count}건`
      + (result.skipped.length ? ` · 제외 ${result.skipped.length}건 (${result.skipped[0]}${result.skipped.length > 1 ? ' 외' : ''})` : '');
    if (!dryRun) await refreshAll();
  } catch (err) {
    msg.className = 'warn';
    msg.textContent = `집계 실패: ${err.message}`;
  }
}

/* ── 인증 ─────────────────────────────────────────────── */

// 이 배지가 노란색이면 이 서버는 신뢰 경계 밖에 두면 안 된다 — 헤더 한 줄로 누구든 사칭한다.
function renderAuth() {
  const pill = $('auth');
  const a = state.auth;

  if (!a) {
    pill.className = 'pill pill-muted';
    pill.textContent = '인증 —';
    return;
  }

  pill.className = a.verified ? 'pill pill-pass' : 'pill pill-warn';
  pill.textContent = a.verified ? `인증 ${a.method}` : '인증 없음 (헤더 자칭)';
  pill.title = a.verified
    ? '토큰의 서명·발급자·수신자·만료를 검증한다.'
    : 'X-ChoPilot-User 헤더를 그대로 믿는다. 운영 환경에서는 서버가 기동을 거부한다.';
}

/* ── 영속화 ───────────────────────────────────────────── */

// 재시작에 무엇이 남는지는 측정 세션을 시작하기 전에 알아야 한다 — 끝난 뒤에 알면 늦다.
function renderStorage() {
  const pill = $('storage');
  const note = $('storageNote');
  const s = state.storage;

  if (!s) {
    pill.className = 'pill pill-muted';
    pill.textContent = '저장소 —';
    note.className = 'hint';
    note.textContent = '';
    return;
  }

  if (!s.durable) {
    pill.className = 'pill pill-warn';
    pill.textContent = '인메모리';
    note.className = 'warn';
    note.innerHTML = '서버 저장소가 <strong>인메모리</strong>다 — 재시작하면 지식·매핑 캐시·감사 로그가 함께 사라진다. '
      + '<code>Storage:Path</code>(또는 <code>Storage__Path</code>)를 주면 저널로 남는다. '
      + '그때까지는 세션을 끝내기 전에 내려받아라.';
    return;
  }

  pill.className = s.corrupt > 0 ? 'pill pill-warn' : 'pill pill-pass';
  pill.textContent = s.corrupt > 0 ? `저장소 · 손상 ${s.corrupt}줄` : '저장소 durable';

  const detail = s.journals.filter((j) => j.restored > 0)
    .map((j) => `${esc(j.name)} ${j.restored}`).join(' · ') || '없음';

  note.className = s.corrupt > 0 ? 'warn' : 'hint';
  note.innerHTML = `저널 <code>${esc(s.path)}</code> — 부팅 시 <strong>${s.restored}건</strong> 복원 (${detail}).`
    + (s.corrupt > 0
      ? ` <strong>${s.corrupt}줄을 건너뛰었다</strong> — 쓰기 도중 종료된 흔적이다.`
      : ' 재시작해도 남는다.');
}

/* ── 완료 신호 (필수 필드 규칙의 증거) ────────────────── */

// 집계기의 판정 구간과 같은 값이어야 한다 — 화면과 초안이 다른 색을 말하면 승인자가 혼란스럽다.
const REQUIRED_FILL_RATE = 0.9;
const OPTIONAL_FILL_RATE = 0.5;

function renderCompletions() {
  const box = $('completions');
  const c = state.completions;

  if (!c || c.count === 0) {
    box.innerHTML = '<p class="empty">완료 신호 없음 — <code>chopilot-dump --completed</code> 로 '
      + '저장 직후 화면을 캡처하면 여기 쌓인다. 그때까지 필수 필드 규칙은 <strong>시드 추측</strong>이다.</p>';
    return;
  }

  const sections = c.businessObjects.map((bo) => {
    const rows = bo.concepts.map((k) => {
      const verdict = k.fillRate >= REQUIRED_FILL_RATE ? ['pill-pass', '필수로 제안']
        : k.fillRate <= OPTIONAL_FILL_RATE ? ['pill-warn', '비필수로 제안']
        : ['pill-muted', '판단 보류'];
      return `<tr>
        <td class="mono">${esc(k.concept)}</td>
        <td class="num">${k.observed}</td>
        <td class="num">${k.filled}</td>
        <td class="num">${pct(k.fillRate)}</td>
        <td class="num">${k.distinctUsers}</td>
        <td><span class="pill ${verdict[0]}">${verdict[1]}</span></td>
      </tr>`;
    }).join('');

    return `<h4>${esc(bo.businessObject)}
        <span class="hint">— 완료 ${bo.completions}건 · ${bo.distinctUsers}명</span></h4>
      <table>
        <thead><tr><th>개념</th><th class="num">화면에 있던</th><th class="num">채워진</th>
                   <th class="num">채움률</th><th class="num">사람</th><th>판정</th></tr></thead>
        <tbody>${rows}</tbody>
      </table>`;
  }).join('');

  box.innerHTML = sections
    + `<p class="hint">채움률 ${pct(OPTIONAL_FILL_RATE)}~${pct(REQUIRED_FILL_RATE)} 구간은 손대지 않는다 — `
    + '애매한 증거로 규칙을 흔들면 가이드가 배치마다 말을 바꾼다.</p>';
}

/* ── 기반 정보 (무료 API · MCP) ───────────────────────── */

// 판정을 색으로 가른다. unmatched만 붉다 — no_master·unverifiable을 경보로 칠하면
// 출처를 붙이기 전까지 화면이 온통 빨개지고, 그러면 사람이 경보를 보지 않게 된다.
const RECONCILE_VERDICT = {
  matched: ['pill-pass', '마스터에 있음'],
  unmatched: ['pill-fail', '미등록'],
  unverifiable: ['pill-warn', '대사 불가'],
  no_master: ['pill-muted', '마스터 없음'],
};

function renderFoundation() {
  renderFoundationSources();
  renderReconcile();
}

function renderFoundationSources() {
  const box = $('foundationSources');
  const f = state.foundation;
  if (!f) {
    box.innerHTML = '<p class="empty">데이터 없음</p>';
    return;
  }

  const kinds = f.master.kinds.length
    ? f.master.kinds.map((k) => `${esc(k.kind)} ${k.count}건`).join(' · ')
    : '없음';

  const rows = f.sources.map((s) => `<tr class="${s.error ? 'row-split' : ''}">
    <td>${esc(s.title)}</td>
    <td class="mono">${esc(s.kind)}</td>
    <td class="mono">${esc(s.origin)}</td>
    <td class="num">${s.facts}</td>
    <td class="hint">${esc(s.license)}</td>
    <td>${s.error
      ? `<span class="pill pill-fail">${esc(s.error)}</span>`
      : !s.requiresNetwork
        ? '<span class="pill pill-muted">내장</span>'
        : s.fetchedAt
          ? `<span class="pill pill-pass">${esc(s.fetchedAt.slice(0, 16).replace('T', ' '))}</span>`
          : '<span class="pill pill-warn">갱신 전</span>'}</td>
  </tr>`).join('');

  box.innerHTML = `<p class="hint">마스터 <strong>${f.master.count}건</strong> — ${kinds}</p>
    <table>
      <thead><tr><th>출처</th><th>종류</th><th>엔드포인트</th><th class="num">사실</th>
                 <th>이용 조건</th><th>상태</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>`;
}

function renderReconcile() {
  const box = $('foundationReconcile');
  const r = state.reconcile;
  if (!r || r.checked === 0) {
    box.innerHTML = '<p class="empty">대사할 관측 엔티티 없음 — 스냅샷을 먼저 적재하라</p>';
    return;
  }

  const rows = r.rows.map((row) => {
    const [cls, label] = RECONCILE_VERDICT[row.status] || ['pill-muted', row.status];
    return `<tr>
      <td class="mono">${esc(row.kind)}</td>
      <td class="mono">${esc(row.key)}</td>
      <td class="num">${row.distinctUsers}</td>
      <td class="num">${row.mentions}</td>
      <td><span class="pill ${cls}">${esc(label)}</span></td>
      <td class="hint">${esc(row.detail || '')}</td>
    </tr>`;
  }).join('');

  box.innerHTML = `<div class="metrics">
      ${metricCard('대사 대상', r.checked, '관측 엔티티', null)}
      ${metricCard('일치', r.matched, '마스터에 있음', null)}
      ${metricCard('미등록', r.unmatched, '0건이 목표', r.unmatched === 0)}
      ${metricCard('대사 불가', r.unverifiable + r.noMaster, '출처·키 공간 문제', null)}
    </div>
    <div class="table-scroll"><table>
      <thead><tr><th>종류</th><th>키</th><th class="num">사람</th><th class="num">관측</th>
                 <th>판정</th><th>비고</th></tr></thead>
      <tbody>${rows}</tbody>
    </table></div>`
    + (r.notes.length ? `<p class="warn">${r.notes.map(esc).join('<br>')}</p>` : '');
}

async function refreshFoundation() {
  const msg = $('foundationMsg');
  const actor = requireActor(msg);
  if (!actor) return;

  msg.className = 'hint';
  msg.textContent = '';        // 진행 표시는 버튼이 한다 — 지난 결과만 지운다

  try {
    const result = await api('/v1/foundation/refresh', {
      method: 'POST',
      headers: { 'X-ChoPilot-User': actor },
    });
    msg.className = result.failures.length ? 'warn' : 'hint';
    msg.textContent = `출처 ${result.refreshed}개 · 사실 ${result.facts}건`
      + (result.failures.length
        ? ` · 실패 ${result.failures.length}건 (${result.failures[0].source}: ${result.failures[0].error})`
        : '');
    await refreshAll();
  } catch (err) {
    msg.className = 'warn';
    msg.textContent = `갱신 실패: ${err.message}`;
  }
}

function renderDecisions() {
  const box = $('decisions');
  if (state.decisions.length === 0) {
    box.innerHTML = '<p class="empty">아직 검수·보정 이력 없음</p>';
    return;
  }
  box.innerHTML = `<table>
    <thead><tr><th>시각</th><th>행위</th><th>사람</th><th>서명</th><th>스코프</th><th>내용</th></tr></thead>
    <tbody>${state.decisions.map((d) => `<tr>
      <td class="hint">${esc(d.at.slice(0, 19).replace('T', ' '))}</td>
      <td><span class="badge">${esc(d.action)}</span></td>
      <td>${esc(d.actor)}</td>
      <td class="mono">${shortSig(d.signature)}</td>
      <td class="mono">${esc(d.scope)}</td>
      <td class="hint">${esc(d.detail)}</td>
    </tr>`).join('')}</tbody></table>`;
}

function renderSignatures() {
  const box = $('signatures');
  if (state.routes.length === 0) {
    box.innerHTML = '<p class="empty">데이터 없음</p>';
    return;
  }

  const rows = state.routes.map((r) => {
    const detail = r.signatures
      .map((s) => `${shortSig(s.signature)} <span class="badge">${s.observationCount}건</span>`)
      .join(' · ');
    return `<tr class="${r.split ? 'row-split' : ''}">
      <td class="mono">${esc(r.route)}</td>
      <td class="num">${r.observationCount}</td>
      <td class="num">${r.signatureCount}</td>
      <td>${r.split
        ? '<span class="pill pill-warn">갈림 — 조사 필요</span>'
        : '<span class="pill pill-pass">정상</span>'}</td>
      <td class="mono">${detail}</td>
    </tr>`;
  }).join('');

  box.innerHTML = `<div class="table-scroll"><table>
    <thead><tr><th>route</th><th class="num">관측</th><th class="num">서명</th><th>판정</th><th>서명별 건수</th></tr></thead>
    <tbody>${rows}</tbody></table></div>`
    + (state.splitRoutes > 0
      ? `<p class="warn"><strong>${state.splitRoutes}개 route에서 서명이 갈렸다.</strong> 원인은 대개 둘 중 하나다 — `
        + '노드 수가 <code>Observation:MaxNodes</code> 상한에 닿아 트리가 다른 지점에서 잘렸거나(아래 표의 노드 수를 비교하라), '
        + '화면에 아직 정규화되지 않은 동적 구조가 남아 있다.</p>'
      : '');
}

function renderObservations() {
  const box = $('observations');
  if (state.observations.length === 0) {
    box.innerHTML = '<p class="empty">적재된 스냅샷 없음</p>';
    return;
  }

  const rows = state.observations.map((o) => {
    const hint = o.recordHint
      ? `<span class="mono">${esc(o.recordHint.value)}</span> <span class="badge">${esc(o.recordHint.source)}</span>`
      : '<span class="badge">없음</span>';
    const leak = o.residualPiiCount > 0
      ? `<span class="badge badge-leak">누출 ${o.residualPiiCount}</span>`
      : `<span class="badge badge-mask">${o.maskedCount}</span>`;
    return `<tr class="clickable ${state.selected === o.observationId ? 'selected' : ''}" data-id="${esc(o.observationId)}">
      <td>${esc(state.sources[o.observationId] || o.observationId)}</td>
      <td class="hint">${esc((o.capturedAt || '').slice(0, 16).replace('T', ' ')) || '—'}</td>
      <td class="mono">${esc(o.route)}</td>
      <td>${hint}</td>
      <td class="num">${o.nodeCount}</td>
      <td class="num">${o.namedCount}</td>
      <td class="num">${o.valuedCount}</td>
      <td>${leak}</td>
      <td><span class="badge">${esc(SOURCE_LABEL[o.source] || o.source)}</span></td>
      <td class="mono">${shortSig(o.signature)}</td>
      <td>${scoreBadge(state.scoring.h2[o.observationId])}</td>
    </tr>`;
  }).join('');

  box.innerHTML = `<table>
    <thead><tr>
      <th>스냅샷</th><th>관측 시각</th><th>route</th><th>레코드(H2)</th>
      <th class="num">노드</th><th class="num">Name</th><th class="num">Value</th>
      <th>마스킹(H4)</th><th>판단 출처</th><th>서명</th><th>H2 채점</th>
    </tr></thead>
    <tbody>${rows}</tbody></table>`;

  box.querySelectorAll('tr.clickable').forEach((tr) => {
    tr.addEventListener('click', () => selectObservation(tr.dataset.id));
  });
}

function scoreBadge(verdict) {
  if (verdict === 'ok') return '<span class="pill pill-pass">정답</span>';
  if (verdict === 'ng') return '<span class="pill pill-fail">오답</span>';
  if (verdict === 'na') return '<span class="badge">제외</span>';
  return '<span class="badge">미채점</span>';
}

async function selectObservation(id) {
  state.selected = id;
  try {
    state.detail = await api(`/v1/observations/${encodeURIComponent(id)}`);
  } catch (err) {
    $('detail').innerHTML = `<p class="warn">상세를 불러오지 못했다: ${esc(err.message)}</p>`;
    return;
  }
  renderObservations();
  renderDetail();
}

function renderDetail() {
  const d = state.detail;
  if (!d) return;
  const s = d.summary;

  const nodeRows = d.nodes.map((n) => {
    const flags = [
      n.masked ? '<span class="badge badge-mask">마스킹</span>' : '',
      n.residualPii ? '<span class="badge badge-leak">PII 잔존</span>' : '',
    ].filter(Boolean).join(' ');
    return `<tr>
      <td class="mono">${esc(n.ref)}</td>
      <td class="mono">${esc(n.role)}</td>
      <td style="padding-left:${10 + n.depth * 12}px">${esc(n.name) || '<span class="badge">-</span>'}</td>
      <td class="mono">${esc(n.value) || '<span class="badge">비어있음</span>'}</td>
      <td class="mono">${esc(n.automationId) || '-'}</td>
      <td>${flags}</td>
    </tr>`;
  }).join('');

  // 판단(ref → 개념)과 원본(ref → 라벨·값)을 두 표로 나눠 두면 사람이 ref를 눈으로 대조해야 한다.
  // 필드가 스무 개면 대조도 스무 번이다 — 판단은 그것이 무엇에 대한 판단인지와 같은 줄에 있어야 한다.
  const byRef = Object.fromEntries(d.nodes.map((n) => [n.ref, n]));
  const mapRows = d.mapping.length
    ? d.mapping.map((m) => {
        const n = byRef[m.elementRef];
        return `<tr>
        <td>
          ${n?.name ? `<strong>${esc(n.name)}</strong>` : '<span class="badge">라벨 없음</span>'}
          <div class="hint mono">${esc(m.elementRef)}</div>
        </td>
        <td class="mono">${n?.masked
          ? '<span class="badge badge-mask">마스킹</span>'
          : (n?.value ? esc(n.value) : '<span class="badge">비어있음</span>')}</td>
        <td>${esc(conceptLabel(m.concept))} <span class="hint mono">${esc(m.concept)}</span></td>
        <td class="num">${confidenceCell(m.confidence)}</td>
        <td>${esc(provenanceLabel(m.provenance))}</td>
        <td>${m.sensitive ? '<span class="badge badge-mask">민감</span>' : ''}</td>
      </tr>`;
      }).join('')
    : '<tr><td colspan="6" class="empty">매핑된 필드 없음</td></tr>';

  const hint = s.recordHint
    ? `${esc(s.recordHint.value)} <span class="badge">${esc(s.recordHint.source)} · ${esc(s.recordHint.key)}</span>`
    : '<span class="badge">신호 없음</span>';

  $('detail').innerHTML = `<div class="detail">
    <div class="detail-head">
      <h3 style="margin:0">${esc(state.sources[s.observationId] || s.observationId)}</h3>
      <div>
        <span class="hint">H2 채점:</span>
        <button class="btn btn-sm" data-verdict="ok">정답</button>
        <button class="btn btn-sm" data-verdict="ng">오답</button>
        <button class="btn btn-sm btn-ghost" data-verdict="na">제외</button>
      </div>
    </div>

    <div class="kv">
      <div><span>URL</span> <code>${esc(s.url) || '-'}</code></div>
      <div><span>타이틀</span> ${esc(s.title) || '-'}</div>
      <div><span>레코드(H2)</span> ${hint}</div>
      <div><span>서명</span> <code>${shortSig(s.signature)}</code></div>
      <div><span>업무객체</span> ${esc(s.businessObject)} <span class="badge">${esc(s.status)}</span></div>
      <div><span>노드</span> ${s.nodeCount} · Name ${s.namedCount} · Value ${s.valuedCount}</div>
      <div><span>마스킹(H4)</span> ${s.maskedCount}건 ${s.residualPiiCount > 0
        ? `<span class="badge badge-leak">잔존 PII ${s.residualPiiCount}건 — H4 미달</span>`
        : '<span class="badge">잔존 PII 없음</span>'}</div>
    </div>

    <h3>요소 인벤토리 <span class="hint">— H1 채점의 원자료 (PHASE0-KIT §2.1)</span></h3>
    <div class="table-scroll inventory"><table>
      <thead><tr><th>ref</th><th>role</th><th>name</th><th>value</th><th>automationId</th><th>플래그</th></tr></thead>
      <tbody>${nodeRows}</tbody></table></div>

    <h3>AI 판단 <span class="hint">— 화면의 각 칸을 무엇이라고 읽었는지</span></h3>
    <div class="table-scroll"><table>
      <thead><tr>
        <th>화면의 칸</th><th>화면의 값</th><th>읽은 개념</th>
        <th class="num">신뢰도</th><th>어디서 왔나</th><th></th>
      </tr></thead>
      <tbody>${mapRows}</tbody></table></div>
  </div>`;

  $('detail').querySelectorAll('[data-verdict]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.scoring.h2[s.observationId] = btn.dataset.verdict;
      saveScoring();
      renderObservations();
      renderVerdict();
    });
  });
}

/* ── 채점 & 판정 ──────────────────────────────────────── */

function h2Tally() {
  const verdicts = state.observations
    .map((o) => state.scoring.h2[o.observationId])
    .filter((v) => v === 'ok' || v === 'ng');
  const ok = verdicts.filter((v) => v === 'ok').length;
  return { ok, total: verdicts.length, ratio: ratio(ok, verdicts.length) };
}

function renderScores() {
  const h1 = ratio(state.scoring.h1Ok, state.scoring.h1Total);
  $('h1Out').textContent = h1 === null ? '—' : `획득률 ${pct(h1)} · ${h1 >= PASS.h1 ? 'PASS' : 'FAIL'}`;

  const h3 = ratio(state.scoring.h3Ai, state.scoring.h3Total);
  const base = ratio(state.scoring.h3Base, state.scoring.h3Total);
  $('h3Out').textContent = h3 === null
    ? '—'
    : `AI ${pct(h3)} · ${h3 >= PASS.h3 ? 'PASS' : 'FAIL'}${base === null ? '' : ` (대조군 ${pct(base)})`}`;
}

function verdictRows() {
  const m = state.metrics;
  const h2 = h2Tally();
  const rows = [
    ['H1', '필드 획득률', '≥ 90%', ratio(state.scoring.h1Ok, state.scoring.h1Total), (v) => v >= PASS.h1, '수기 채점'],
    ['H2', '화면·레코드 식별', '≥ 95%', h2.ratio, (v) => v >= PASS.h2, `채점 ${h2.total}건`],
    ['H3', 'AI 매핑 정확률', '≥ 90%', ratio(state.scoring.h3Ai, state.scoring.h3Total), (v) => v >= PASS.h3, '수기 채점'],
    ['H3b', '캐시 적중률', '≥ 95%', m && m.observations ? m.cacheHitRatio : null, (v) => v >= PASS.h3b, '자동'],
    ['H4', '잔존 PII 0건', '0건', m && m.observations ? residualTotal() : null, (v) => v === 0, '자동(반증)'],
    ['H5', '엔티티 갈림', '0건', state.entities ? state.entities.splitCandidates : null, (v) => v === 0, '자동(ERP 축 1단)'],
    ['H6', '지연 p95', '≤ 3000ms', m && m.observations ? m.latencyP95Ms : null, (v) => v <= PASS.h6, '서버 구간만'],
  ];

  return rows.map(([id, name, target, value, ok, note]) => {
    let display = '—', pill = '<span class="badge">미측정</span>';
    if (value !== null && value !== undefined) {
      display = (id === 'H4' || id === 'H5') ? `${value}건` : id === 'H6' ? `${value} ms` : pct(value);
      pill = ok(value) ? '<span class="pill pill-pass">PASS</span>' : '<span class="pill pill-fail">FAIL</span>';
    }
    return { id, name, target, display, pill, note, value, passed: value === null || value === undefined ? null : ok(value) };
  });
}

function renderVerdict() {
  renderScores();
  const rows = verdictRows();
  $('verdict').innerHTML = `<table>
    <thead><tr><th>가설</th><th>항목</th><th>통과선</th><th>측정값</th><th>판정</th><th>비고</th></tr></thead>
    <tbody>${rows.map((r) => `<tr>
      <td><strong>${r.id}</strong></td><td>${esc(r.name)}</td><td>${esc(r.target)}</td>
      <td class="num">${esc(r.display)}</td><td>${r.pill}</td><td class="hint">${esc(r.note)}</td>
    </tr>`).join('')}</tbody></table>
    <p class="hint">GO 조건은 <strong>H1·H2·H3·H3b</strong> (PHASE0-PLAN §8). H5(결정론 연결)는 구현이 없어 수기로만 측정한다.</p>`;
}

/* ── 내보내기 ─────────────────────────────────────────── */

function download(filename, text, mime) {
  const url = URL.createObjectURL(new Blob([text], { type: mime }));
  const a = document.createElement('a');
  a.href = url; a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

const stamp = () => new Date().toISOString().slice(0, 16).replace(/[:T]/g, '');

function buildMarkdown() {
  const m = state.metrics || {};
  const rows = verdictRows();
  const h2 = h2Tally();

  const lines = [
    '# Cho-Pilot Phase 0 측정 결과',
    '',
    `- 생성: ${new Date().toISOString()}`,
    `- 관측 수: ${m.observations || 0} · 서명 ${m.distinctSignatures || 0}종 · AI 호출 ${m.aiCalls || 0}회`,
    '',
    '## Go / No-Go',
    '',
    '| 가설 | 항목 | 통과선 | 측정값 | 판정 | 비고 |',
    '|------|------|--------|--------|------|------|',
    ...rows.map((r) => `| ${r.id} | ${r.name} | ${r.target} | ${r.display} | ${r.passed === null ? '미측정' : r.passed ? 'PASS' : 'FAIL'} | ${r.note} |`),
    '',
    `> GO 조건 = H1·H2·H3·H3b 통과 (PHASE0-PLAN §8). H2는 ${h2.ok}/${h2.total}건 정답.`,
    '',
    '## 서명 진단 (H3b 원인)',
    '',
    '| route | 관측 | 서명 | 판정 |',
    '|-------|------|------|------|',
    ...state.routes.map((r) => `| \`${r.route}\` | ${r.observationCount} | ${r.signatureCount} | ${r.split ? '**갈림**' : '정상'} |`),
    '',
    '## 스냅샷별 (PHASE0-KIT §2.2 / §2.3 / §4)',
    '',
    '| 스냅샷 | route | 레코드 식별 | 노드 | Name | Value | 마스킹 | 잔존 PII | H2 채점 |',
    '|--------|-------|------------|------|------|-------|--------|----------|---------|',
    ...state.observations.map((o) => {
      const hint = o.recordHint ? `${o.recordHint.value} (${o.recordHint.source})` : '없음';
      const v = state.scoring.h2[o.observationId];
      const verdict = v === 'ok' ? '정답' : v === 'ng' ? '오답' : v === 'na' ? '제외' : '미채점';
      return `| ${state.sources[o.observationId] || o.observationId} | \`${o.route}\` | ${hint} | ${o.nodeCount} | ${o.namedCount} | ${o.valuedCount} | ${o.maskedCount} | ${o.residualPiiCount} | ${verdict} |`;
    }),
    '',
    '## 성능·비용 (H6)',
    '',
    `- 지연 p50 / p95 / 최대: ${m.latencyP50Ms} / ${m.latencyP95Ms} / ${m.latencyMaxMs} ms`,
    '- **이 값은 서버 내부 구간(서명→매핑→BO)만 잰 하한이다.** UIA 캡처·네트워크 왕복·Guide 조회는 별도로 측정해 더해야 NFR(≤3s)과 비교할 수 있다.',
    `- AI 호출 ${m.aiCalls || 0}회 · 입력 ${m.inputTokens || 0} / 출력 ${m.outputTokens || 0} 토큰`,
    `- 재추론 보류 ${m.deferredReuses || 0}회 — 저신뢰 캐시를 재사용해 아끼지 않았다면 AI 호출은 ${(m.aiCalls || 0) + (m.deferredReuses || 0)}회였다`,
    (m.inputTokens || m.outputTokens)
      ? ''
      : '- 토큰이 0인 것은 비용이 없다는 뜻이 아니라 **미측정**이다 (`UseBedrock=true`일 때만 채워진다).',
    '',
    '## 검수·보정 (HITL)',
    '',
    `- 검수 대기 ${state.review.length}건 · 결정 이력 ${state.decisions.length}건`,
    ...(state.decisions.length
      ? ['', '| 시각 | 행위 | 사람 | 서명 | 내용 |', '|------|------|------|------|------|',
         ...state.decisions.map((d) => `| ${d.at.slice(0, 19).replace('T', ' ')} | ${d.action} | ${d.actor} | \`${shortSig(d.signature)}\` | ${d.detail} |`)]
      : ['- 아직 사람이 개입한 이력이 없다. 저신뢰 매핑이 남아 있다면 적중률은 올라가지 않는다.']),
    '',
    '## 지식 (ARCHITECTURE §5.5)',
    '',
    `- 지식 버전 ${state.knowledgeVersion} · 개념 ${state.concepts.length}개 · 승인 대기 초안 ${state.knowledge.filter(isDraft).length}건`,
    ...(state.signals && state.signals.candidates.length
      ? ['', '| 거부된 개념 | 시도 | 사용자 |', '|------|------|--------|',
         ...state.signals.candidates.map((c) => `| ${c.term} | ${c.attempts} | ${c.distinctUsers} |`)]
      : ['- 거부된 개념 시도 없음 — 온톨로지 결핍이 관측되지 않았거나 보정을 아무도 시도하지 않았다.']),
    '',
    '## 엔티티 결정 (H5, ARCHITECTURE §6 1단)',
    '',
    state.entities
      ? `- 엔티티 ${state.entities.count}종 · 공동 출현 ${state.entities.links.length}쌍 · 갈림 후보 ${state.entities.splitCandidates}건`
      : '- 미측정',
    ...(state.entities && state.entities.splits.length
      ? ['', '| 타입 | 갈린 키 |', '|------|--------|',
         ...state.entities.splits.map((s) => `| ${s.type} | ${s.keys.join(' / ')} |`),
         '', '> 자동 병합하지 않는다 — 잘못 합치면 서로 다른 실체가 하나가 되고 오류가 전파된다.']
      : []),
    '',
    '## 인증',
    '',
    state.auth && state.auth.verified
      ? `- 주체 해석 \`${state.auth.method}\` — 서명·발급자·수신자·만료를 검증한다`
      : '- **검증 없음.** `X-ChoPilot-User` 헤더를 그대로 믿는다 — 이 리포트의 사용자별 수치는 자칭에 근거한다.',
    '',
    '## 영속화',
    '',
    state.storage && state.storage.durable
      ? `- 저널 \`${state.storage.path}\` — 부팅 시 ${state.storage.restored}건 복원`
        + (state.storage.corrupt ? `, **손상 ${state.storage.corrupt}줄 폐기**` : '')
      : '- **인메모리.** 이 리포트의 수치는 서버 재시작과 함께 사라진다.',
    '',
    '## 작업 완료 신호 (ARCHITECTURE §11)',
    '',
    state.completions && state.completions.count
      ? `- 완료 관측 ${state.completions.count}건 — 필수 필드 규칙이 시드 추측에서 관측으로 교체될 수 있다`
      : '- **완료 신호 0건.** `rule.required.*`는 아직 검증되지 않은 시드 추측이고, 가이드의 빈칸 제안은 전부 그 위에 서 있다.',
    ...(state.completions && state.completions.count
      ? state.completions.businessObjects.flatMap((bo) => [
          '',
          `### ${bo.businessObject} — 완료 ${bo.completions}건 / ${bo.distinctUsers}명`,
          '',
          '| 개념 | 화면에 있던 | 채워진 | 채움률 |', '|------|------|------|------|',
          ...bo.concepts.map((k) => `| ${k.concept} | ${k.observed} | ${k.filled} | ${pct(k.fillRate)} |`)])
      : []),
    '',
    '## 기반 정보 (무료 API · MCP)',
    '',
    state.foundation
      ? `- 출처 ${state.foundation.sources.length}개 · 마스터 ${state.foundation.master.count}건`
        + ` (${state.foundation.master.kinds.map((k) => `${k.kind} ${k.count}`).join(', ') || '없음'})`
      : '- 미측정',
    ...(state.foundation && state.foundation.sources.length
      ? ['', '| 출처 | 종류 | 엔드포인트 | 사실 | 상태 |', '|------|------|------------|------|------|',
         ...state.foundation.sources.map((s) =>
           `| ${s.title} | ${s.kind} | \`${s.origin}\` | ${s.facts} | ${s.error || (s.requiresNetwork ? '정상' : '내장')} |`)]
      : []),
    ...(state.reconcile && state.reconcile.checked
      ? ['',
         `- 대사 ${state.reconcile.checked}건 — 일치 ${state.reconcile.matched} / **미등록 ${state.reconcile.unmatched}**`
         + ` / 대사 불가 ${state.reconcile.unverifiable} / 마스터 없음 ${state.reconcile.noMaster}`,
         '- 미등록만 경보다. 마스터가 없거나 키 공간이 어긋난 것은 대사가 성립하지 않은 것이라 결핍이 아니다.',
         ...state.reconcile.notes.map((n) => `- ${n}`)]
      : ['- 대사할 관측 엔티티가 없다.']),
    '',
    '## 제안 수락률 (ARCHITECTURE §9)',
    '',
    state.suggestions && state.suggestions.impressions
      ? `- 노출 ${state.suggestions.impressions} · 수락 ${state.suggestions.accepted} / 거부 ${state.suggestions.rejected} / 무응답 ${state.suggestions.pending}`
        + ` · 수락률 ${pct(state.suggestions.acceptanceRate)} · 응답률 ${pct(state.suggestions.responseRate)}`
      : '- 노출된 제안이 없다 (`/v1/guide`를 호출해야 집계된다).',
    '- 수락률은 **명시적 판단 중** 수락 비율이다. 무응답은 거부가 아니라 응답률에만 반영된다.',
    '',
    '## 미측정 항목',
    '',
    '- **H5 결정론 연결** — Entity Resolver 구현이 없다. PHASE0-MEASUREMENT §3 H5의 수기 절차를 따른다.',
  ];

  return lines.filter((l) => l !== null).join('\n');
}

/* ── 초기화 ───────────────────────────────────────────── */

function bind() {
  const drop = $('drop'), files = $('files');

  // 입력 자신에서 온 클릭은 되쏘지 않는다 — 되쏘면 파일 선택창이 두 번 열린다.
  drop.addEventListener('click', (e) => { if (e.target !== files) files.click(); });
  files.addEventListener('change', () => { uploadFiles(files.files); files.value = ''; });

  ['dragenter', 'dragover'].forEach((type) =>
    drop.addEventListener(type, (e) => { e.preventDefault(); drop.classList.add('over'); }));
  ['dragleave', 'drop'].forEach((type) =>
    drop.addEventListener(type, (e) => { e.preventDefault(); drop.classList.remove('over'); }));
  drop.addEventListener('drop', (e) => uploadFiles(e.dataTransfer.files));

  $('refresh').addEventListener('click', refreshAll);

  const autoSeconds = $('autoSeconds');
  autoSeconds.value = state.autoSeconds;
  autoSeconds.addEventListener('change', () => {
    // 숫자가 아니면 기본값으로, 그다음 [1, 3600]으로 자른다. 0·음수가 10과 1로 갈리지 않도록
    // 순서를 이렇게 둔다 — 상한 1시간은 그보다 길면 반복이 아니라 방치이기 때문이다.
    const parsed = Number(autoSeconds.value);
    state.autoSeconds = Math.min(3600, Math.max(1, Number.isFinite(parsed) && parsed > 0 ? parsed : 10));
    autoSeconds.value = state.autoSeconds;
    localStorage.setItem(AUTO_SECONDS_KEY, state.autoSeconds);
    if (state.autoOn) scheduleAuto();   // 다음 회차부터 새 간격
    renderAuto();
  });

  $('autoReplay').addEventListener('change', (e) => setAuto(e.target.checked));

  const actor = $('actor');
  actor.value = state.actor;
  actor.addEventListener('input', () => {
    state.actor = actor.value.trim();
    localStorage.setItem(ACTOR_KEY, state.actor);
    renderActor();
  });
  // 측정자가 바뀌면 personal 스코프 문서(내 프로파일)가 달라진다 — 입력이 멎으면 다시 읽는다.
  actor.addEventListener('change', refreshAll);
  renderActor();

  $('aggregatePreview').addEventListener('click', (e) =>
    whileBusy(e.currentTarget, '미리보는 중…', () => aggregate(true)));
  $('aggregateSubmit').addEventListener('click', (e) =>
    whileBusy(e.currentTarget, '집계 중…', () => aggregate(false)));
  $('foundationRefresh').addEventListener('click', (e) =>
    whileBusy(e.currentTarget, '갱신 중…', refreshFoundation));
  $('proposalGenerate').addEventListener('click', (e) => generateProposals(e.currentTarget));

  for (const key of ['h1Total', 'h1Ok', 'h3Total', 'h3Ai', 'h3Base']) {
    const input = $(key);
    input.value = state.scoring[key] ?? '';
    input.addEventListener('input', () => {
      state.scoring[key] = input.value;
      saveScoring();
      renderVerdict();
    });
  }

  $('exportMd').addEventListener('click', () =>
    download(`chopilot-measure-${stamp()}.md`, buildMarkdown(), 'text/markdown'));

  $('exportJson').addEventListener('click', () =>
    download(`chopilot-measure-${stamp()}.json`, JSON.stringify({
      generatedAt: new Date().toISOString(),
      metrics: state.metrics,
      routes: state.routes,
      observations: state.observations,
      sources: state.sources,
      scoring: state.scoring,
      review: state.review,
      decisions: state.decisions,
      suggestions: state.suggestions,
      knowledgeVersion: state.knowledgeVersion,
      knowledge: state.knowledge,
      signals: state.signals,
      entities: state.entities,
      foundation: state.foundation,
      reconcile: state.reconcile,
      completions: state.completions,
      storage: state.storage,
      auth: state.auth,
    }, null, 2), 'application/json'));

  $('resetScoring').addEventListener('click', () => {
    if (!confirm('채점을 모두 지운다. 계속할까?')) return;
    state.scoring = { h1Total: '', h1Ok: '', h3Total: '', h3Ai: '', h3Base: '', h2: {} };
    saveScoring();
    for (const key of ['h1Total', 'h1Ok', 'h3Total', 'h3Ai', 'h3Base']) $(key).value = '';
    renderObservations();
    renderVerdict();
  });
}

bind();
refreshAll();
