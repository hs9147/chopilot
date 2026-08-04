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

const state = {
  metrics: null,
  observations: [],
  routes: [],
  splitRoutes: 0,
  sources: loadJson(SOURCES_KEY, {}),   // observationId → 원본 파일명
  selected: null,
  detail: null,
  scoring: loadScoring(),
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

async function refreshAll() {
  try {
    const [metrics, observations, signatures] = await Promise.all([
      api('/v1/metrics'), api('/v1/observations'), api('/v1/signatures'),
    ]);
    state.metrics = metrics;
    state.observations = observations.items;
    state.routes = signatures.routes;
    state.splitRoutes = signatures.splitRoutes;
    $('health').className = 'pill pill-pass';
    $('health').textContent = '서버 연결됨';
  } catch (err) {
    $('health').className = 'pill pill-fail';
    $('health').textContent = `서버 오류: ${err.message}`;
  }

  renderMetrics();
  renderSignatures();
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
    metricCard('AI 호출 (H6)', m.aiCalls, `입력 ${m.inputTokens} / 출력 ${m.outputTokens} 토큰`, null),
    metricCard('마스킹 (H4)', m.maskedRefs, `잔존 PII ${residualTotal()}건`, residualTotal() === 0),
  ].join('');

  // T1 — 스텁 신뢰도 0.6 < 기본 θ 0.8 이면 적중률이 구조적으로 0이 된다.
  const stubOnly = (m.byProvenance || {}).stub > 0 && !(m.byProvenance || {}).ai;
  const pending = (m.byStatus || {}).pending_review > 0;
  const warn = $('thetaWarn');
  if (stubOnly && pending && m.cacheHitRatio === 0) {
    warn.hidden = false;
    warn.innerHTML = '<strong>측정 함정 T1.</strong> 매핑이 전부 <code>pending_review</code>이고 적중률이 0이다 — '
      + 'StubAiMapper의 신뢰도(0.6)가 <code>Mapping:ThetaHigh</code>(기본 0.8) 미만이라 캐시가 구조적으로 적중하지 못한다. '
      + '<code>--Mapping:ThetaHigh=0.5</code> 로 재기동하거나 <code>UseBedrock=true</code>로 실제 AI를 써라.';
  } else {
    warn.hidden = true;
  }
}

const residualTotal = () => state.observations.reduce((sum, o) => sum + o.residualPiiCount, 0);

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
    ['H6', '지연 p95', '≤ 3000ms', m && m.observations ? m.latencyP95Ms : null, (v) => v <= PASS.h6, '서버 구간만'],
  ];

  return rows.map(([id, name, target, value, ok, note]) => {
    let display = '—', pill = '<span class="badge">미측정</span>';
    if (value !== null && value !== undefined) {
      display = id === 'H4' ? `${value}건` : id === 'H6' ? `${value} ms` : pct(value);
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
    (m.inputTokens || m.outputTokens)
      ? ''
      : '- 토큰이 0인 것은 비용이 없다는 뜻이 아니라 **미측정**이다 (`UseBedrock=true`일 때만 채워진다).',
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
