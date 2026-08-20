const byId = (id) => document.getElementById(id);
const state = { tasks: [], current: null, concepts: [] };

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[c]);
}

function headers() {
  const user = byId('user').value.trim();
  localStorage.setItem('chopilot.user', user);
  return {
    'Content-Type': 'application/json',
    'X-ChoPilot-User': user,
    'X-ChoPilot-Roles': 'end_user'
  };
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { ...headers(), ...(options.headers || {}) }
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.error || String(response.status));
  return body;
}

function toast(message) {
  const el = byId('toast');
  el.textContent = message;
  el.hidden = false;
  setTimeout(() => { el.hidden = true; }, 3500);
}

function newId() {
  return crypto.randomUUID();
}

async function load() {
  byId('summary').textContent = '확인 항목을 불러오는 중…';
  try {
    const [review, ontology] = await Promise.all([
      api('/v1/me/review-tasks'), api('/v1/ontology')
    ]);
    state.tasks = review.tasks || [];
    state.concepts = ontology.concepts || [];
    show(0);
  } catch (error) {
    byId('summary').textContent = '불러오지 못했습니다: ' + error.message;
  }
}

function show(index) {
  state.current = state.tasks[index] || null;
  byId('summary').textContent = state.current
    ? state.tasks.length + '개의 확인 항목이 있습니다.'
    : '';
  byId('task').hidden = !state.current;
  byId('empty').hidden = !!state.current;
  byId('edit').hidden = true;
  byId('actions').hidden = false;
  if (!state.current) return;

  const task = state.current;
  byId('screen').textContent = [task.screenTitle, task.route].filter(Boolean).join(' · ');
  byId('business').textContent = task.businessObject;
  byId('confidence').textContent = '확신 ' + Math.round(task.confidence * 100) + '%';
  byId('fields').innerHTML = task.fields.map((field) =>
    '<div class="field"><span class="label">' + esc(field.label || field.elementKey) +
    '</span><span class="arrow">→</span><span class="concept">' + esc(field.concept) +
    (field.sensitive ? ' <span class="sensitive">민감 항목</span>' : '') +
    '</span></div>').join('');
  byId('element').innerHTML = task.fields.map((field) =>
    '<option value="' + esc(field.elementKey) + '">' +
    esc(field.label || field.elementKey) + ' → ' + esc(field.concept) +
    '</option>').join('');
  byId('concept').innerHTML = state.concepts.map((concept) =>
    '<option value="' + esc(concept.name) + '">' +
    esc((concept.aliases || [])[0] || concept.name) + ' (' + esc(concept.name) + ')' +
    '</option>').join('');
}

async function submit(decision, extra = {}) {
  const task = state.current;
  if (!task) return;
  const field = task.fields.find((item) =>
    item.elementKey === (extra.elementKey || (task.fields[0] || {}).elementKey));
  if (!field) return;

  const command = {
    feedbackId: newId(),
    observationId: task.observationId,
    target: { type: 'mapping', elementKey: field.elementKey },
    decision,
    reasonCode: extra.reason || 'correct',
    proposed: extra.concept ? { concept: extra.concept } : null,
    requestedScope: extra.scope || 'personal',
    expected: {
      mappingRevision: task.mappingRevision,
      knowledgeVersion: task.knowledgeVersion
    }
  };

  try {
    const result = await api('/v1/feedback', {
      method: 'POST',
      body: JSON.stringify(command)
    });
    state.tasks.shift();
    toast(result.status === 'pending_org_review'
      ? '조직 검수 요청으로 접수했습니다.'
      : '의견을 반영했습니다.');
    show(0);
  } catch (error) {
    toast('반영하지 못했습니다: ' + error.message);
  }
}

byId('user').value = localStorage.getItem('chopilot.user') || 'developer';
byId('reload').onclick = load;
byId('accept').onclick = () => submit('accept');
byId('defer').onclick = () => submit('defer', { reason: 'other' });
byId('correct').onclick = () => {
  byId('edit').hidden = false;
  byId('actions').hidden = true;
};
byId('cancel').onclick = () => {
  byId('edit').hidden = true;
  byId('actions').hidden = false;
};
byId('submit').onclick = () => submit('correct', {
  elementKey: byId('element').value,
  concept: byId('concept').value,
  reason: byId('reason').value,
  scope: byId('org').checked ? 'org' : 'personal'
});

load();
