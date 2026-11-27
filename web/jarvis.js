'use strict';

const node = id => document.getElementById(id);
const bridge = window.chrome?.webview;
const send = (command, value = '', id = '') => bridge?.postMessage({ command, value, id });
const create = (tag, text, className) => {
  const element = document.createElement(tag);
  if (text != null) element.textContent = text;
  if (className) element.className = className;
  return element;
};
const cardRows = new Map();
const activities = new Map();
const processRows = new Map();
let state = {};
let windows = [];
let history = [];
let approval;
let tab = 'activity';
let minimized = false;
let motionRequested = !matchMedia('(prefers-reduced-motion: reduce)').matches;
let sceneLoaded = false;

window.portfolioMotion = false;
const prompt = node('prompt');
const conversation = node('conversation');
const layout = document.querySelector('.layout');
const drawer = node('drawer');
const canvas = node('ambient-canvas');
const core = node('core-stage');

const templates = {
  chat: ['What are we working on?', 'Send a message or attach an image to get started.'],
  plan: ['Plan a project change', 'Describe the change you want to make in this folder.'],
  build: ['Build and test locally', 'Send a coding task and review any shell commands before they run.'],
  blender: ['Build a scene in Blender', 'Attach a reference image, describe the result, and answer any questions in this conversation.', 'Create a simple house with one window and one door. Save it as an editable Blender file.'],
  desktop: ['Work in a Windows app', 'Choose an open window above or launch an app here.'],
};

function setTab(name) {
  tab = name;
  for (const button of document.querySelectorAll('[data-tab]')) {
    const active = button.dataset.tab === name;
    button.setAttribute('aria-pressed', active);
    node(button.dataset.tab).hidden = !active;
  }
  drawer.hidden = false;
  layout.classList.remove('wide');
  node('drawer-toggle').setAttribute('aria-expanded', 'true');
  send('diagnostics', node('logs-control').open ? 'on' : 'off');
  applyMotion();
}

function applyMotion() {
  const show = !minimized;
  window.portfolioMotion = show && motionRequested && !document.hidden;
  canvas.classList.toggle('display-scene', show && motionRequested);
  core.classList.toggle('static', !window.portfolioMotion);
  if (window.portfolioMotion) {
    clipCanvas();
    if (!sceneLoaded) {
      sceneLoaded = true;
      import('./core-scene.js').catch(() => {
        canvas.hidden = true;
        node('motion').disabled = true;
        node('motion').textContent = 'Animation unavailable';
      });
    }
  }
  window.dispatchEvent(new Event('portfolio-motion'));
}

function clipCanvas() {
  const rect = core.getBoundingClientRect();
  const inspector = core.closest('.work').getBoundingClientRect();
  const top = Math.max(rect.top, inspector.top);
  const bottom = Math.min(rect.bottom, inspector.bottom);
  canvas.style.clipPath = bottom > top
    ? `inset(${top}px ${innerWidth - rect.right}px ${innerHeight - bottom}px ${rect.left}px)`
    : 'inset(100%)';
}

new ResizeObserver(clipCanvas).observe(core);
node('drawer').querySelector('.inspector-scroll').addEventListener('scroll', clipCanvas, { passive: true });
document.addEventListener('visibilitychange', applyMotion);

function applyState(next) {
  const lastMode = state.agent;
  const lastSelected = state.target;
  state = next;
  const grants = [next.blenderGrant && 'Blender', next.desktopGrant && 'Desktop', next.buildGrant && 'Build'].filter(Boolean);
  node('blender-grant').hidden = grants.length === 0;
  node('blender-grant-label').textContent = grants.length ? grants.join(', ') + ' allowed' : '';
  node('codex-queue').checked = next.acceptCodex;
  node('codex-queue').disabled = !next.ready;
  node('continue-blender').hidden = !next.canContinueBlender;
  node('continue-blender').disabled = next.busy;
  const status = next.status || '';
  node('core-state').textContent = status.startsWith('Waiting for approval') ? 'Waiting for approval'
    : /failed|unavailable/i.test(status) ? 'Failed'
    : status.startsWith('Running · blender_') ? 'Running Blender'
    : status.startsWith('Completed ·') ? 'Completed'
    : next.busy ? (/^Connecting/.test(status) ? 'Connecting' : 'Thinking')
    : next.ready ? 'Ready' : 'Starting';
  node('status').textContent = next.status;
  node('status').title = next.status;
  node('project').textContent = next.project;
  node('project-name').textContent = next.project.split(/[\\/]/).filter(Boolean).at(-1) || next.project;
  node('project-name').title = next.project;
  node('mode').value = next.agent;
  node('desktop-controls').hidden = next.agent !== 'desktop';
  node('focus').disabled = !next.target;
  node('attachment').hidden = !next.attachment;
  node('attachment').textContent = next.attachment ? next.attachment + ' ×' : '';
  node('force-blender').hidden = !next.forceBlender;
  node('execution-label').hidden = next.agent !== 'blender';
  node('execution').value = next.execution;
  node('execution').disabled = next.busy;
  node('show-blender').hidden = next.agent !== 'blender' || !next.forceBlender;
  node('console').checked = next.console;
  const disabled = !next.ready || next.busy;
  for (const element of document.querySelectorAll('[data-select="mode"],[data-command="folder"],[data-command="new"],[data-command="diff"],[data-command="attach"],[data-command="detach"],[data-command="windows"],[data-command="launch"],[data-command="history"],#windows-toggle')) {
    element.disabled = disabled;
  }
  for (const button of document.querySelectorAll('#history button'))
    button.classList.toggle('active', button.dataset.value === next.session);
  if (lastMode !== next.agent) {
    const [title, copy, example] = templates[next.agent] || templates.chat;
    node('welcome-title').textContent = title;
    node('welcome-copy').textContent = copy;
    node('suggestion').hidden = !example;
    node('suggestion').textContent = example ? 'Use the one-window house example' : '';
    node('suggestion').dataset.example = example || '';
  }
  if (lastSelected !== next.target) updateSelectedWindow();
  updateApprovalButtons();
  updateSend();
}

function updateSend() {
  node('send').disabled = !state.ready || state.busy || !prompt.value.trim()
    || (state.agent === 'desktop' && !state.target);
}

function updateSelectedWindow() {
  const target = windows.find(item => item.id === state.target);
  node('windows-toggle').textContent = target ? target.title + ' ▾' : 'Choose a window ▾';
  node('windows-toggle').title = target ? `${target.title} · ${target.process}, PID ${target.pid}` : 'Select a window';
}

function renderWindows() {
  const query = node('window-filter').value.toLocaleLowerCase().trim();
  const items = windows.filter(item => [item.title, item.process, String(item.pid)].some(value => value.toLocaleLowerCase().includes(query)));
  node('windows').replaceChildren(...items.map(item => {
    const button = create('button');
    button.dataset.target = item.id;
    if (item.icon) {
      const image = create('img');
      image.src = item.icon;
      image.alt = '';
      button.append(image);
    }
    const label = create('div');
    label.append(create('strong', item.title), create('small', `${item.process} · PID ${item.pid}`));
    button.append(label);
    return button;
  }));
  if (!items.length) node('windows').append(create('p', 'No matching open windows.', 'empty'));
  updateSelectedWindow();
}

function renderHistory() {
  node('history').replaceChildren(...history.map(item => {
    const button = create('button', item.title || 'Untitled conversation');
    button.dataset.command = 'history';
    button.dataset.value = item.id;
    button.title = button.textContent;
    button.disabled = state.busy || !state.ready;
    if (item.id === state.session) button.classList.add('active');
    return button;
  }));
}

function updateRow(row) {
  const follow = conversation.scrollHeight - conversation.scrollTop - conversation.clientHeight < 70;
  if (!cardRows.has(row.id)) {
    const card = create('article', null, row.author === 'You' ? 'message user' : 'message assistant');
    const body = create('p');
    card.append(create('h3', row.author), body);
    if (row.source) {
      const source = create('details', null, 'panel');
      source.append(create('summary', row.source.split('\n')[0]), create('p', row.source));
      card.append(source);
    }
    cardRows.set(row.id, body);
    node('messages').append(card);
    node('welcome').hidden = true;
  }
  cardRows.get(row.id).textContent = row.body;
  if (follow) conversation.scrollTop = conversation.scrollHeight;
}

function updateActivity(item) {
  let view = activities.get(item.id);
  if (!view) {
    const card = create('details', null, 'event panel');
    const summary = create('summary');
    const name = create('strong');
    const badge = create('span', '', 'badge');
    const target = create('p');
    const data = create('pre');
    data.tabIndex = 0;
    summary.append(name, badge);
    card.append(summary, target, data);
    node('timeline').prepend(card);
    view = { name, badge, target, data };
    activities.set(item.id, view);
    node('activity-empty').hidden = true;
  }
  view.name.textContent = item.Tool;
  view.badge.textContent = item.State;
  view.badge.dataset.state = item.State;
  view.target.textContent = item.Target;
  view.data.textContent = [item.Arguments, item.Detail].filter(Boolean).join('\n\n');
  view.data.hidden = !view.data.textContent;
}

function updateApprovalButtons() {
  for (const button of document.querySelectorAll('[data-decision]'))
    button.disabled = state.deciding || !approval || (!approval.fullInput && !['reject', 'workspace-session'].includes(button.dataset.decision));
}

function showApproval(value) {
  approval = value;
  const chatGrant = Boolean(value?.chatGrant);
  const covered = Boolean(value?.covered);
  document.querySelector('[data-decision=workspace-session]').hidden = !chatGrant;
  document.querySelector('[data-decision=once]').hidden = chatGrant || covered;
  const note = node('session-note');
  note.hidden = !(chatGrant || covered);
  note.textContent = covered ? 'Refocus the selected window to resume under the existing approval.' : ({
    blender: 'Covers scene inspection, parts, Python, previews and managed saves. Python runs with your Windows account permissions.',
    desktop: 'Covers observation and input for the selected window and process.',
    build: 'Covers project edits, Git inspection and supported build, test, restore and run commands.',
  })[value?.group] || '';
  node('permission').hidden = !value;
  updateApprovalButtons();
  if (!value) return;
  node('permission-action').textContent = value.action;
  node('permission-target').textContent = value.target;
  node('permission-args').textContent = value.arguments || 'No arguments';
  setTab('activity');
  node('drawer').querySelector('.inspector-scroll').scrollTop = 0;
}

const number = (value, unit = '', digits = 1) => value == null ? 'Unavailable' :
  Number(value).toLocaleString(undefined, { maximumFractionDigits: digits }) + unit;
const rate = value => number(value == null ? null : value / 1048576, ' MiB/s', 2);
const pair = (used, total, unit) => used == null || total == null ? 'Unavailable' :
  number(used) + ' / ' + number(total, unit);

function stats(id, readings) {
  const root = node(id);
  const children = [...root.children];
  readings.forEach(([label, value], index) => {
    let card = children[index];
    if (!card) {
      card = create('div', null, 'stat');
      card.append(create('small'), create('strong'));
      root.append(card);
    }
    card.firstChild.textContent = label;
    card.lastChild.textContent = value;
  });
}

function updateMetrics(value) {
  stats('stats', [
    ['VRAM', pair(value.vramUsed, value.vramTotal, ' MiB')],
    ['RAM', pair(value.ramUsed, value.ramTotal, ' GiB')],
    ['SYSTEM CPU', number(value.cpu, '%')],
    ['PHYSICAL DRIVE WRITES', rate(value.physicalWrites)]
  ]);
  const live = new Set(value.processes.map(process => process.pid));
  for (const [pid, element] of processRows) {
    if (live.has(pid)) continue;
    element.remove();
    processRows.delete(pid);
  }
  for (const process of value.processes) {
    let row = processRows.get(process.pid);
    if (!row) {
      row = create('div', null, 'process');
      row.append(create('strong'), create('small'), create('small'));
      node('processes').append(row);
      processRows.set(process.pid, row);
    }
    row.children[0].textContent = `${process.name} · PID ${process.pid}`;
    row.children[1].textContent = `CPU ${number(process.cpu, '%')} · RAM ${number(process.ram, ' MiB')} · VRAM ${number(process.vram, ' MiB')}`;
    row.children[2].textContent = `Writes ${rate(process.writeRate)} · ${number(process.writes, ' B total', 0)}`;
  }
}

let readings = [];
let readingIndex = 0;
function showCoreReading() {
  node('core-reading').textContent = readings[readingIndex] || 'Unavailable';
}
setInterval(() => {
  if (minimized || document.hidden || !motionRequested) return;
  readingIndex = (readingIndex + 1) % readings.length;
  showCoreReading();
}, 3000);
function updateModel(value) {
  readings = [value.model, number(value.output, ' output tokens', 0),
    number(value.rate, ' tok/s'), 'First token · ' + number(value.ttft, ' ms', 0)];
  showCoreReading();
  stats('inference', [
    ['MODEL', value.model], ['FIRST TOKEN', number(value.ttft, ' ms', 0)],
    ['UNCACHED INPUT', number(value.input, '', 0)], ['OUTPUT TOKENS', number(value.output, '', 0)],
    ['CACHED INPUT', number(value.cached, '', 0)], ['REASONING TOKENS', number(value.reasoning, '', 0)], ['GENERATION', number(value.rate, ' tok/s')]
  ]);
  node('rate').textContent = value.rate == null ? '' : number(value.rate, ' tok/s');
}

function reset() {
  cardRows.clear();
  activities.clear();
  node('messages').replaceChildren();
  node('timeline').replaceChildren();
  node('activity-empty').hidden = false;
  node('welcome').hidden = false;
  node('capture').hidden = true;
  node('capture').removeAttribute('src');
  node('preview-empty').hidden = false;
  node('diff').hidden = true;
}

bridge?.addEventListener('message', event => {
  const { type, value } = event.data;
  switch (type) {
    case 'runtime-settings': renderRuntimeSettings(value); break;
    case 'runtime-result': node('runtime-result').textContent = value; break;
    case 'runtime-path': document.getElementById(value.key).value = value.path; break;
    case 'state': applyState(value); break;
    case 'history': history = value; renderHistory(); break;
    case 'windows': windows = value; renderWindows(); break;
    case 'reset': reset(); break;
    case 'row': updateRow(value); break;
    case 'activity': updateActivity(value); break;
    case 'permission': showApproval(value); break;
    case 'codex-tasks':
      node('codex-tasks').textContent = value.map(task => task.Id.slice(0, 8) + ' · ' + task.State).join(' · ');
      break;
    case 'sent': if (prompt.value === value) prompt.value = ''; updateSend(); break;
    case 'image':
      if (/^data:image\/(png|jpeg|webp);base64,/i.test(value)) {
        node('capture').src = value;
        node('capture').hidden = false;
        node('preview-empty').hidden = true;
      }
      break;
    case 'diff':
      setTab('activity');
      node('diff').hidden = false;
      node('diff').open = true;
      node('diff-value').textContent = value || 'No tracked changes.';
      node('diff').scrollIntoView({ block: 'nearest' });
      break;
    case 'metrics': updateMetrics(value); break;
    case 'metrics-error': node('processes').title = value; break;
    case 'model': updateModel(value); break;
    case 'models': {
      const select = node('model');
      const tags = [...new Set([value.selected, ...value.tags, ...(value.inklingAvailable ? ['inkling'] : [])])];
      select.replaceChildren(...tags.map(tag => new Option(tag === 'inkling' ? 'Inkling 2 Mini' : tag, tag)));
      select.value = value.selected;
      select.disabled = value.refreshing || value.pulling;
      node('models-toggle').textContent = 'Models · ' + (value.selected === 'inkling' ? 'Inkling 2 Mini' : value.selected);
      node('model-capabilities').textContent = value.pending ? 'Next run: ' + value.pending : value.known
        ? (value.vision ? 'Images supported' : 'Text only') + ' · ' + (value.tools ? 'Tools supported' : 'No tool calling')
        : 'Model capabilities unavailable';
      node('model-download').disabled = value.pulling || value.refreshing;
      node('model-cancel').hidden = !value.pulling;
      node('model-error').textContent = value.error || '';
      break;
    }
    case 'model-progress': node('model-progress').textContent = value; break;
    case 'blender-output':
      node('blender-output').hidden = !value.output && !value.checkpoint;
      node('blend-path').textContent = value.output ? 'Verified output: ' + value.output : '';
      node('checkpoint-path').textContent = value.checkpoint ? 'Pre-edit checkpoint: ' + value.checkpoint : '';
      break;
    case 'logs': node('logs').textContent = value || 'No runtime output yet.'; break;
    case 'connector': {
      const label = node(value.name + '-state');
      if (label) label.textContent = value.state;
      break;
    }
    case 'visibility': minimized = value.minimized; applyMotion(); break;
  }
});

document.addEventListener('click', event => {
  const button = event.target.closest('button');
  if (!button || button.disabled) return;
  const { command, value, target, decision, tab: nextTab } = button.dataset;
  if (command) send(command, value);
  if (decision && approval) send('permission', decision, approval.id);
  if (nextTab) setTab(nextTab);
  if (target) {
    send('target', target);
    node('windows-popup').hidden = true;
    node('windows-toggle').setAttribute('aria-expanded', 'false');
    node('windows-toggle').focus();
  }
});
document.addEventListener('change', event => {
  const { select } = event.target.dataset;
  if (select) send(select, event.target.value);
});
node('models-toggle').addEventListener('click', event => {
  const panel = node('models-panel');
  panel.hidden = !panel.hidden;
  event.currentTarget.setAttribute('aria-expanded', !panel.hidden);
  if (!panel.hidden) send('models-refresh');
});
node('model-download').addEventListener('click', () => send('model-pull', node('model-tag').value));
node('console').addEventListener('change', event => send('console', event.target.checked ? 'on' : 'off'));
node('logs-control').addEventListener('toggle', event => send('diagnostics', event.target.open ? 'on' : 'off'));
node('drawer-toggle').addEventListener('click', event => {
  drawer.hidden = !drawer.hidden;
  layout.classList.toggle('wide', drawer.hidden);
  event.target.setAttribute('aria-expanded', !drawer.hidden);
  send('diagnostics', !drawer.hidden && node('logs-control').open ? 'on' : 'off');
  applyMotion();
});
node('windows-toggle').addEventListener('click', event => {
  const popup = node('windows-popup');
  popup.hidden = !popup.hidden;
  event.target.setAttribute('aria-expanded', !popup.hidden);
  if (!popup.hidden) { send('windows'); node('window-filter').focus(); }
});
node('window-filter').addEventListener('input', renderWindows);
node('suggestion').addEventListener('click', event => {
  prompt.value = event.target.dataset.example;
  updateSend();
  prompt.focus();
});
node('motion').addEventListener('click', event => {
  motionRequested = !motionRequested;
  event.target.setAttribute('aria-pressed', motionRequested);
  event.target.textContent = motionRequested ? 'Pause reactor' : 'Animate reactor';
  applyMotion();
});
prompt.addEventListener('input', updateSend);
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && state.busy) { event.preventDefault(); send('stop'); }
  if (event.key === 'Enter' && event.ctrlKey && !node('send').disabled) {
    event.preventDefault();
    send('send', prompt.value);
  }
});
node('send').addEventListener('click', () => send('send', prompt.value));
updateMetrics({ processes: [] });
updateModel({ model: 'Local model' });
node('motion').textContent = motionRequested ? 'Pause reactor' : 'Animate reactor';
node('motion').setAttribute('aria-pressed', motionRequested);
applyMotion();
send('hello');

node('codex-queue').addEventListener('change', event => send('codex-queue', event.target.checked ? 'on' : 'off'));

function renderRuntimeSettings(settings) {
  const form = node('runtime-fields');
  form.replaceChildren();
  for (const field of settings.fields) {
    const row = document.createElement('div');
    const label = document.createElement('label');
    label.htmlFor = field.key;
    label.textContent = field.label;
    const input = document.createElement('input');
    input.id = field.key;
    input.value = field.value;
    row.append(label, input);
    if (field.key !== 'JARVIS_COMFYUI') {
      const browse = document.createElement('button');
      browse.textContent = 'Browse';
      browse.onclick = () => send('runtime-browse', '', field.key);
      row.append(browse);
    }
    form.append(row);
  }
  node('runtime-managed').checked = settings.managed;
  const dialog = node('runtime-dialog');
  const actions = node('runtime-actions');
  actions.replaceChildren();
  const button = (label, action) => {
    const control = document.createElement('button');
    control.textContent = label;
    control.onclick = action;
    actions.append(control);
  };
  button('Save and apply', () => {
    const values = Object.fromEntries(settings.fields.map(field => [field.key, node(field.key).value]));
    values.JARVIS_OLLAMA_MODE = node('runtime-managed').checked ? 'managed' : 'desktop';
    send('runtime-save', JSON.stringify(values));
  });
  for (const [label, action] of [['Open Ollama Desktop','ollama-desktop'], ['Start Ollama','ollama-start'],
    ['Stop owned Ollama','ollama-stop'], ['Restart owned Ollama','ollama-restart'], ['Runtime logs','logs'],
    ['Test ComfyUI','comfy-test'], ['Add knowledge files','add-knowledge']]) {
    button(label, () => send('runtime-' + action, node('JARVIS_COMFYUI').value));
  }
  for (const [label, action] of [['Find installed ComfyUI','search'], ['Search Pinokio registry','registry'],
    ['Install selected recipe','install'], ['Start selected app','launch']]) {
    button(label, () => send('runtime-pinokio-' + action, node('pinokio-target').value));
  }
  button('Close', () => dialog.close());
  if (!dialog.open) dialog.showModal();
}
