'use strict';

// Cho-Pilot 측정 콘솔 — PHASE0-MEASUREMENT.md의 jq/curl 절차를 화면으로 대체한다.
// 서버 저장소가 인메모리라 지표는 서버 수명과 함께 사라진다. 채점만 localStorage에 남긴다.

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
  editing: null,          // 보정 중인 검수 큐 항목
  actor: localStorage.getItem(ACTOR_KEY) || '',
  knowledge: [],          // 지식 문서(초안 + 게시)
  knowledgeVersion: 0,
  signals: null,          // 미지 개념 후보
  entities: null,         // 엔티티 결정 결과(H5)
  foundation: null,       // 기반 출처 상태 + 마스터 요약
  reconcile: null,        // 관측 ↔ 마스터 대사 결과
  myProfile: null,        // 사용자 축 뷰(저장되지 않음 — 매 조회마다 렌더된다)
  openDraft: null,
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
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
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

async function uploadFiles(fileList) {
  const files = Array.from(fileList).filter((f) => f.name.endsWith('.json'));
  if (files.length === 0) { log('JSON 파일이 없다', 'err'); return; }

  const assignNewIds = $('newIds').checked;

  for (const file of files) {
    try {
      let observation = extractObservation(JSON.parse(await file.text()));

      if (assignNewIds) {
        const stamp = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
        observation = withEventId(observation, `${file.name.replace(/\.json$/, '')}-${stamp}-${Math.random().toString(36).slice(2, 6)}`);
      }

      const result = await api('/v1/observations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(observation),
      });

      state.sources[result.observation_id] = file.name;
      saveSources();
      log(`${file.name} → ${result.cache_hit ? '캐시 적중' : 'AI 추론'} · ${shortSig(result.signature)} · ${result.business_object}`, 'ok');
    } catch (err) {
      log(`${file.name} → 실패: ${err.message}`, 'err');
    }
  }

  await refreshAll();
}

/* ── 조회 & 렌더 ──────────────────────────────────────── */

// 지식 조회는 personal 스코프 문서를 위해 측정자 헤더가 필요하다.
const asUser = () => (state.actor ? { headers: { 'X-ChoPilot-User': state.actor } } : undefined);

async function refreshAll() {
  try {
    const [metrics, observations, signatures, review, decisions, suggestions, ontology,
           knowledge, signals, entities, foundation, reconcile] = await Promise.all([
      api('/v1/metrics'), api('/v1/observations'), api('/v1/signatures'),
      api('/v1/review'), api('/v1/decisions?limit=20'), api('/v1/suggestions?limit=1'), api('/v1/ontology'),
      api('/v1/knowledge', asUser()), api('/v1/knowledge/signals'), api('/v1/entities'),
      api('/v1/foundation'), api('/v1/foundation/reconcile'),
    ]);
    state.metrics = metrics;
    state.observations = observations.items;
    state.routes = signatures.routes;
    state.splitRoutes = signatures.splitRoutes;
    state.review = review.entries;
    state.decisions = decisions.entries;
    state.suggestions = suggestions.stats;
    state.concepts = ontology.concepts;
    state.knowledge = knowledge.items;
    state.knowledgeVersion = knowledge.version;
    state.signals = signals;
    state.entities = entities;
    state.foundation = foundation;
    state.reconcile = reconcile;
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
  renderDecisions();
  renderKnowledge();
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

  const rows = state.review.map((e) => `<tr class="clickable ${state.editing && state.editing.signature === e.signature && state.editing.scope === e.scope ? 'selected' : ''}"
      data-sig="${esc(e.signature)}" data-scope="${esc(e.scope)}">
    <td class="mono">${shortSig(e.signature)}</td>
    <td>${esc(e.businessObject)}</td>
    <td class="mono">${esc(e.scope)}</td>
    <td class="num">${e.confidence.toFixed(2)}</td>
    <td class="num">${e.mapping.length}</td>
    <td class="hint">${e.lastInferredAt ? esc(e.lastInferredAt.slice(0, 19).replace('T', ' ')) : '사람이 만든 매핑'}</td>
  </tr>`).join('');

  box.innerHTML = `<table>
    <thead><tr>
      <th>서명</th><th>업무객체</th><th>스코프</th>
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

function conceptOptions() {
  // 사용자는 화면에 보이는 말("단가")로 정정한다. 정규 이름과 별칭을 모두 후보로 준다.
  const names = state.concepts.flatMap((c) => [c.name, ...(c.aliases || [])]);
  return names.map((n) => `<option value="${esc(n)}"></option>`).join('');
}

function renderCorrection() {
  const box = $('correction');
  const entry = state.editing;
  if (!entry) { box.innerHTML = ''; return; }

  const rows = entry.mapping.map((m, i) => `<tr>
    <td class="mono">${esc(m.elementRef)}</td>
    <td><input type="text" class="text" data-field="${i}" value="${esc(m.concept)}" list="conceptList"></td>
    <td class="num">${m.confidence.toFixed(2)}</td>
    <td>${esc(m.provenance)}</td>
  </tr>`).join('');

  box.innerHTML = `<div class="detail">
    <div class="detail-head">
      <h3 style="margin:0">보정 — <code>${shortSig(entry.signature)}</code> ${esc(entry.businessObject)}</h3>
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
      <thead><tr><th>ref</th><th>개념</th><th class="num">신뢰도</th><th>출처</th></tr></thead>
      <tbody>${rows}</tbody></table></div>
    <p id="correctionMsg" class="hint"></p>
  </div>`;

  $('applyCorrection').addEventListener('click', () => applyCorrection(entry));
  $('promoteEntry').addEventListener('click', () => promoteEntry(entry));
}

function requireActor(msgBox) {
  if (state.actor) return state.actor;
  msgBox.className = 'warn';
  msgBox.textContent = '측정자 ID를 먼저 입력하라 — 누가 승인했는지 남지 않는 보정은 되돌릴 수 없다.';
  $('actor').focus();
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
  msg.textContent = '갱신 중…';

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
      <td class="mono">${esc(o.route)}</td>
      <td>${hint}</td>
      <td class="num">${o.nodeCount}</td>
      <td class="num">${o.namedCount}</td>
      <td class="num">${o.valuedCount}</td>
      <td>${leak}</td>
      <td>${o.cacheHit ? '<span class="badge">캐시</span>' : '<span class="badge">AI</span>'}</td>
      <td class="mono">${shortSig(o.signature)}</td>
      <td>${scoreBadge(state.scoring.h2[o.observationId])}</td>
    </tr>`;
  }).join('');

  box.innerHTML = `<table>
    <thead><tr>
      <th>스냅샷</th><th>route</th><th>레코드(H2)</th>
      <th class="num">노드</th><th class="num">Name</th><th class="num">Value</th>
      <th>마스킹(H4)</th><th>매핑</th><th>서명</th><th>H2 채점</th>
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

  const mapRows = d.mapping.length
    ? d.mapping.map((m) => `<tr>
        <td class="mono">${esc(m.elementRef)}</td>
        <td>${esc(m.concept)}</td>
        <td class="num">${m.confidence.toFixed(2)}</td>
        <td>${esc(m.provenance)}</td>
        <td>${m.sensitive ? '<span class="badge badge-mask">민감</span>' : ''}</td>
      </tr>`).join('')
    : '<tr><td colspan="5" class="empty">매핑된 필드 없음</td></tr>';

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

    <h3>적용된 매핑</h3>
    <div class="table-scroll"><table>
      <thead><tr><th>ref</th><th>개념</th><th class="num">신뢰도</th><th>출처</th><th></th></tr></thead>
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

  drop.addEventListener('click', () => files.click());
  files.addEventListener('change', () => { uploadFiles(files.files); files.value = ''; });

  ['dragenter', 'dragover'].forEach((type) =>
    drop.addEventListener(type, (e) => { e.preventDefault(); drop.classList.add('over'); }));
  ['dragleave', 'drop'].forEach((type) =>
    drop.addEventListener(type, (e) => { e.preventDefault(); drop.classList.remove('over'); }));
  drop.addEventListener('drop', (e) => uploadFiles(e.dataTransfer.files));

  $('refresh').addEventListener('click', refreshAll);

  const actor = $('actor');
  actor.value = state.actor;
  actor.addEventListener('input', () => {
    state.actor = actor.value.trim();
    localStorage.setItem(ACTOR_KEY, state.actor);
  });
  // 측정자가 바뀌면 personal 스코프 문서(내 프로파일)가 달라진다 — 입력이 멎으면 다시 읽는다.
  actor.addEventListener('change', refreshAll);

  $('aggregatePreview').addEventListener('click', () => aggregate(true));
  $('aggregateSubmit').addEventListener('click', () => aggregate(false));
  $('foundationRefresh').addEventListener('click', refreshFoundation);

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
