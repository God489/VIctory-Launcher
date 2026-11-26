'use strict';
// VIctory Launcher UI. Talks to the host (Launcher.cs) over WebView2 messages.
// Copyright (C) 2026 God489. GPL-3.0-or-later.

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const host = window.chrome && window.chrome.webview;

// ---------------- bridge ----------------
const waiting = new Map();
const handlers = {};
let seq = 0;
function call(cmd, args = {}) {
  if (!host) return Mock.call(cmd, args);
  return new Promise((resolve, reject) => {
    const id = String(++seq);
    waiting.set(id, { resolve, reject });
    host.postMessage({ cmd, id, ...args });
  });
}
function callWith(cmd, objects) {
  return new Promise((resolve, reject) => {
    const id = String(++seq);
    waiting.set(id, { resolve, reject });
    host.postMessageWithAdditionalObjects({ cmd, id }, objects);
  });
}
function receive(m) {
  if (m.type === 'reply') {
    const w = waiting.get(m.id);
    if (!w) return;
    waiting.delete(m.id);
    m.ok ? w.resolve(m.data) : w.reject(new Error(m.error || 'Failed'));
  } else if (m.type === 'event' && handlers[m.name]) {
    handlers[m.name](m.data);
  }
}
if (host) host.addEventListener('message', e => receive(e.data));
const send = (cmd, args) => call(cmd, args).catch(e => toast(e.message, 'bad'));

// ---------------- state ----------------
const DEFAULTS = {
  console: 'auto', memory: 'auto', mode: 'fast', ambientMode: 'estimate', patches: 'rtx', output: 'auto', textureCheck: 'exact', lastResort: 'on', fps60: false, panel: false, pipelineBook: 'on', rayTracing: false,
  ambient: '0.8', textureBack: 'on', unimplemented: 'trap', strict: false, titleArgs: '', customEnv: '',
  dataDir: '', cacheDir: '', minimizeOnPlay: false, logLevel: 'info',
  scanLanes: 'auto', scanValidate: true, firstRunSeen: false,
};
const AMBIENT = 0.8;
const S = { settings: {}, info: {}, game: null, system: null, running: false, started: 0, scanning: false };
const get = k => (S.settings[k] !== undefined && S.settings[k] !== null ? S.settings[k] : DEFAULTS[k]);
let saveTimer = 0;
function set(k, v) {
  S.settings[k] = v;
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => send('saveSettings', { settings: S.settings }), 250);
  syncControls();
}

// ---------------- helpers ----------------
const GB = n => (n / 1073741824);
const fmtGB = n => (n >= 1073741824 * 100 ? GB(n).toFixed(0) : GB(n).toFixed(1)) + ' GB';
const num = n => Number(n).toLocaleString('en-US');
const esc = s => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
function toast(text, kind = '') {
  const t = document.createElement('div');
  t.className = 'toast ' + kind;
  t.textContent = text;
  $('#toasts').appendChild(t);
  setTimeout(() => { t.classList.add('out'); setTimeout(() => t.remove(), 300); }, kind === 'bad' ? 6500 : 3800);
}
function confirmBox(title, text, yes = 'OK') {
  return new Promise(resolve => {
    $('#mTitle').textContent = title;
    $('#mText').textContent = text;
    $('#mYes').textContent = yes;
    $('#modal').hidden = false;
    const done = v => { $('#modal').hidden = true; $('#mYes').onclick = $('#mNo').onclick = null; resolve(v); };
    $('#mYes').onclick = () => done(true);
    $('#mNo').onclick = () => done(false);
  });
}
const sm = v => v ? (v >> 4) + '.' + (v & 15) : '—';

// ---------------- navigation & window ----------------
const TITLES = { play: 'Play', settings: 'Settings', compat: 'Compatibility', inside: "What's inside", logs: 'Logs', about: 'About' };
function go(page) {
  $$('#nav button').forEach(b => b.classList.toggle('active', b.dataset.page === page));
  $$('.page').forEach(p => p.classList.toggle('active', p.id === 'page-' + page));
  $('#crumb').textContent = TITLES[page];
  if (page === 'logs') { unread = 0; $('#logBadge').hidden = true; scrollLog(true); }
  if (page === 'compat') loadTests();
}
$$('#nav button').forEach(b => b.onclick = () => go(b.dataset.page));
$('#btnMin').onclick = () => send('minimize');
$('#btnClose').onclick = async () => {
  if (S.running && !(await confirmBox('Close the launcher?', 'The game keeps running. You can open the launcher again later; its log of this session is saved to a file.', 'Close'))) return;
  send('close');
};
document.addEventListener('mousedown', e => {
  if (e.button !== 0 || !host) return;
  const d = e.target.closest('[data-drag]');
  if (d && !e.target.closest('button, input, a, .seg')) { e.preventDefault(); send('drag'); }
});
document.addEventListener('keydown', e => {
  if ((e.ctrlKey && ['r', 'p', 'f', 'u', 's', '+', '-', '=', '0'].includes(e.key.toLowerCase())) || e.key === 'F5') e.preventDefault();
});
document.addEventListener('contextmenu', e => { if (!e.target.closest('input, .console, pre')) e.preventDefault(); });
document.addEventListener('click', e => {
  const a = e.target.closest('a[href^="https://"]');
  if (a) { e.preventDefault(); send('openUrl', { url: a.href }); }
  const o = e.target.closest('[data-open]');
  if (o) send('open', { what: o.dataset.open });
});

// ---------------- the game ----------------
function renderGame() {
  const g = S.game;
  const ready = g && g.found;
  $('#gameEmpty').hidden = ready;
  $('#gameReady').hidden = !ready;
  if (!ready) {
    $('#heroEyebrow').textContent = 'Xbox Series X|S runtime for Windows';
    $('#heroTitle').textContent = 'Add your game';
    $('#heroSub').textContent = g && g.problem ? g.problem : 'Point the launcher at your own copy of the game. No game files come with it.';
    $('#firstRun').hidden = true;
    return;
  }
  $('#heroEyebrow').textContent = 'Ready to play';
  $('#heroTitle').textContent = g.title || 'Your game';
  $('#heroSub').textContent = 'VIctory runs the console release from your folder. Pick the console model, then press Play.';
  const chips = [];
  chips.push(`<span class="chip"><i></i>${esc(g.exeName)}</span>`);
  if (g.version) chips.push(`<span class="chip"><i></i>Version ${esc(g.version)}</span>`);
  chips.push(g.config ? '<span class="chip"><i></i>MicrosoftGame.config</span>' : '<span class="chip warn"><i></i>No MicrosoftGame.config</span>');
  chips.push(g.appdata0 ? '<span class="chip"><i></i>appdata0.rpf</span>' : '<span class="chip bad"><i></i>appdata0.rpf missing: the title stops at start</span>');
  if (g.rpfCount) chips.push(`<span class="chip"><i></i>${num(g.rpfCount)} archive${g.rpfCount === 1 ? '' : 's'}</span>`);
  $('#gameChips').innerHTML = chips.join('');
  $('#gamePath').textContent = g.exe;
  $('#firstRun').hidden = !!get('firstRunSeen');
}
async function useGame(promise) {
  try {
    const g = await promise;
    if (!g) return;
    S.game = g;
    S.settings.gamePath = g.exe;
    $('#verifyBox').hidden = true;
    renderGame();
    toast('Game added: ' + g.exeName, 'ok');
  } catch (e) { toast(e.message, 'bad'); }
}
$('#btnBrowse').onclick = () => useGame(call('browseGame'));
$('#btnChange').onclick = () => useGame(call('browseGame'));
$('#btnExe').onclick = () => useGame(call('browseExe'));
$('#btnFolder').onclick = () => send('open', { what: 'game' });
$('#firstRunX').onclick = () => { set('firstRunSeen', true); $('#firstRun').hidden = true; };

// drag and drop: the host reads the dropped file's path
let dragDepth = 0;
document.addEventListener('dragenter', e => { e.preventDefault(); if (++dragDepth === 1) document.body.classList.add('dragging'); });
document.addEventListener('dragleave', e => { e.preventDefault(); if (--dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('dragging'); } });
document.addEventListener('dragover', e => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; });
document.addEventListener('drop', e => {
  e.preventDefault();
  dragDepth = 0;
  document.body.classList.remove('dragging');
  if (!host) return toast('Drop works in the launcher window.', 'warn');
  if (!e.dataTransfer.files.length) return;
  useGame(callWith('drop', e.dataTransfer.files));
});

// verify
$('#btnVerify').onclick = async () => {
  try {
    await call('verify');
    const v = $('#verifyBox');
    v.hidden = false;
    v.innerHTML = '<div class="card"><h3>Checking the game with VIctory…</h3><div class="bar"><i></i></div></div>';
  } catch (e) { toast(e.message, 'bad'); }
};
handlers.verified = r => {
  const v = $('#verifyBox');
  v.hidden = false;
  const ok = !r.error && r.code === 0;
  const known = r.build && r.build !== 'unknown';
  const miss = r.missing || [];
  const head = !ok ? 'VIctory could not prepare this game' : known ? 'VIctory reads this game' : 'VIctory does not know this build';
  const text = r.error ? esc(r.error) : known ? 'The executable was mapped and bound without starting it.'
    : 'The executable was mapped and bound, but its hash is not one of the builds VIctory knows. It may still run; the known builds get checks of their own (such as appdata0.rpf).';
  v.innerHTML = `<div class="card"><h3>${head}</h3><p class="muted">${text}</p>
    <div class="vgrid">
      <div><b>${esc(r.build || 'unknown')}</b><span>title build</span></div>
      <div><b>${esc(r.console || '—')}</b><span>console model</span></div>
      <div><b>${num(r.dlls || 0)}</b><span>title DLLs loaded</span></div>
      <div><b>${num(miss.length)}</b><span>imports VIctory lacks</span></div>
    </div>
    ${miss.length ? `<details class="raw"><summary>Missing imports (${miss.length})</summary><pre>${esc(miss.join('\n'))}</pre></details>` : ''}
    <details class="raw"><summary>Output</summary><pre>${esc((r.lines || []).join('\n'))}</pre></details></div>`;
};

// play / stop
$('#btnPlay').onclick = async () => {
  const b = $('#btnPlay');
  b.disabled = true; b.classList.add('busy');
  try {
    const st = await call('play');
    if (!get('firstRunSeen')) set('firstRunSeen', true);
    $('#firstRun').hidden = true;
    applyState(st);
    toast('Starting the game…', 'ok');
  } catch (e) {
    toast(e.message, 'bad');
  } finally { b.disabled = false; b.classList.remove('busy'); }
};
$('#btnStop').onclick = async () => {
  if (await confirmBox('Stop the game?', 'Unsaved progress is lost. VIctory and the game end at once.', 'Stop')) send('stop');
};
$('#btnShow').onclick = () => send('focusGame');

let tick = 0;
function applyState(st) {
  S.running = !!(st && st.running);
  if (st && st.started) S.started = st.started;
  $('#btnPlay').hidden = S.running;
  $('#runningBox').hidden = !S.running;
  const wasLive = !$('#liveCard').hidden;
  $('#liveCard').hidden = !S.running;
  if (S.running && !wasLive) liveInit();
  $('#segConsole').hidden = S.running;
  $('#btnChange').hidden = S.running;
  const pill = $('#runPill');
  pill.classList.toggle('on', S.running);
  pill.querySelector('span').textContent = S.running ? 'Game running' : 'Ready';
  if (st && st.caption) {
    const m = /([\d.]+)\s*fps\s+([\d.]+)\s*ms\/frame/i.exec(st.caption);
    if (m) { $('#stFps').textContent = m[1]; $('#stMs').textContent = m[2]; }
  }
  clearInterval(tick);
  if (S.running) {
    const upd = () => {
      const s = Math.max(0, Math.floor((Date.now() - S.started) / 1000));
      const h = Math.floor(s / 3600), mi = Math.floor(s / 60) % 60, se = s % 60;
      $('#stElapsed').textContent = (h ? h + ':' + String(mi).padStart(2, '0') : mi) + ':' + String(se).padStart(2, '0');
    };
    upd();
    tick = setInterval(upd, 1000);
  } else { $('#stFps').textContent = '—'; $('#stMs').textContent = '—'; }
}
handlers.state = st => applyState(st);
handlers.exited = r => {
  applyState({ running: false });
  const mins = (r.seconds / 60).toFixed(1);
  const code = r.code < 0 || r.code > 0xFFFF ? '0x' + (r.code >>> 0).toString(16).toUpperCase() : String(r.code);
  const why = (r.error || '').replace(/^\S+Z\s+ERROR\s+/, '').replace(/^VIctory:\s*/, '');
  if (r.code === 0) toast(`The game ended after ${mins} min.`, 'ok');
  else if (r.stopped) toast('The game was stopped.', 'warn');
  else toast(`The game stopped with code ${code}${why ? ': ' + why : ''}. See Logs${r.crashLog ? ' and crashes.log' : ''}.`, 'bad');
};

// ---------------- this PC ----------------
function tile(cls, k, v, d) { return `<div class="tile ${cls}"><div class="k"><i></i>${k}</div><div class="v">${v}</div><div class="d">${d}</div></div>`; }
function renderSystem() {
  const s = S.system;
  if (!s) return;
  const g = s.gpu || {};
  const t = [];
  const cpu = (s.cpu || 'Unknown CPU').replace(/\((R|TM|tm)\)/g, '').replace(/\b(CPU|Processor)\b/g, '').replace(/\s*@.*$/, '').replace(/\d+-Core/, '').replace(/\s+/g, ' ').trim();
  t.push(tile(s.avx2 ? '' : 'bad', 'Processor', esc(cpu), s.avx2 ? `${s.cores} threads · AVX2` : 'No AVX2: VIctory needs it'));
  t.push(tile(g.d3d12 ? (g.vram >= 15 * 1073741824 ? '' : 'warn') : 'bad', 'Graphics', esc(g.name || 'No GPU found'),
    g.d3d12 ? `${fmtGB(g.vram)} video memory${g.vram < 15 * 1073741824 ? ' · the world uses more than a 16 GB card has' : ''}` : esc(g.error || 'No Direct3D 12 device')));
  const wave = g.waveMax ? (g.waveMin === g.waveMax ? `${g.waveMax} lanes` : `${g.waveMin}–${g.waveMax} lanes`) : '—';
  const halves = g.waveMax && g.waveMax < 64;
  t.push(tile(g.waveMax ? 'hl' : 'warn', 'Wave size', wave,
    halves ? 'The console\'s 64-lane waves run as two halves (#68, #128)' : (g.waveMax ? 'Runs the console\'s 64-lane waves natively' : 'Unknown')));
  t.push(tile(g.shaderModel >= 0x66 ? '' : 'bad', 'Shader model', g.shaderModel ? 'Shader Model ' + sm(g.shaderModel) : '—', g.shaderModel >= 0x66 ? '6.6 or later is needed: supported' : '6.6 or later is needed'));
  t.push(tile(g.meshTier >= 10 ? '' : 'bad', 'Mesh shaders', g.meshTier >= 10 ? 'Tier 1' : 'Not supported', g.meshTier >= 10 ? 'The world is drawn with them' : 'Needed to draw the world'));
  const ramOk = s.ramTotal >= 60 * 1073741824, commitOk = s.commitFree >= 48 * 1073741824;
  t.push(tile(ramOk && commitOk ? '' : 'warn', 'Memory', `${fmtGB(s.ramTotal)} RAM${ramOk ? '' : ' · 64 GB recommended'}`,
    `${fmtGB(s.commitFree)} of ${fmtGB(s.commitLimit)} commit free${commitOk ? '' : ' · the world takes 40–46 GB: raise the page file'}`));
  $('#readyGrid').innerHTML = t.join('');
  renderGpuCard();
}
handlers.system = s => { S.system = s; renderSystem(); syncControls(); };

function renderGpuCard() {
  const s = S.system, g = (s && s.gpu) || {};
  if (!s) return;
  const halves = g.waveMax && g.waveMax < 64;
  const explain = !g.waveMax ? 'The launcher could not read this card\'s wave sizes.' : halves
    ? `This card runs waves of ${g.waveMax} lanes only. The console runs most of the game's compute in waves of 64, so this build runs each console wave as two halves of 32 that exchange what they need through group-shared memory: groups of one and of several console waves, LDS, wave reductions, permutes and the scalars branches and loops leave (PRs #68 and #128).`
    : `This card runs the console's 64-lane waves (it reports ${g.waveMin}–${g.waveMax}), so wave64 programs ask for them and run as on the console.`;
  $('#gpuCard').innerHTML = `<div><div class="eyebrow">${esc(g.vendor || '')} graphics</div><div class="gpu-name">${esc(g.name || 'Unknown GPU')}</div><p class="explain">${explain}</p></div>
    <div class="gpu-facts">
      <div><b>${g.waveMax ? (g.waveMin === g.waveMax ? g.waveMax : g.waveMin + '–' + g.waveMax) : '—'}</b><span>wave lanes (D3D12)</span></div>
      <div><b>${g.vram ? fmtGB(g.vram) : '—'}</b><span>video memory</span></div>
      <div><b>${g.shaderModel ? sm(g.shaderModel) : '—'}</b><span>highest shader model</span></div>
      <div><b>${g.meshTier >= 10 ? 'Tier 1' : 'None'}</b><span>mesh shaders</span></div>
    </div>`;
}

// ---------------- settings ----------------
function syncControls() {
  $$('.seg[data-setting]').forEach(seg => {
    const v = String(get(seg.dataset.setting));
    $$('button', seg).forEach(b => b.classList.toggle('on', b.dataset.v === v));
  });
  $$('input[type=checkbox][data-setting]').forEach(i => { i.checked = !!get(i.dataset.setting); });
  $$('input[type=text][data-setting], textarea[data-setting]').forEach(i => { if (document.activeElement !== i) i.value = get(i.dataset.setting); });
  const amb = String(get('ambient'));
  const on = amb !== 'off';
  const level = on ? parseFloat(amb) || AMBIENT : AMBIENT;
  $('#ambientOn').checked = on;
  $('#ambientLevel').disabled = !on;
  $('#ambientLevel').value = level;
  $('#ambientOut').textContent = on ? level.toFixed(2) + (Math.abs(level - AMBIENT) < 0.001 ? ' · as played' : '') : 'off';
  $('#dataDirShow').textContent = get('dataDir') || (S.info.dataDir || 'Default');
  $('#cacheDirShow').textContent = get('cacheDir') || (S.info.cacheDir || 'Default');
  const hasValidator = !!S.info.validator;
  $('#scanValidate').disabled = !hasValidator;
  if (!hasValidator) $('#scanValidate').checked = false;
  $('#validatorText').textContent = hasValidator ? 'Checks each translated shader with ' + S.info.validator : 'Needs dxil.dll from the Windows SDK, which is not installed: shaders are translated only.';
  // what Auto picks on this PC (the launcher decides the same way: under 48 GB, Limited)
  const ram = S.system && S.system.ramTotal;
  const base = 'Limited is what codec_d/cool_fox runs with on an RTX 5090 with 32 GB of RAM: textures\' levels made on demand, every title buffer copied into the card\'s memory, the air\'s light raised, residency budgets. Standard is the 7900XTX fork\'s, for 64 GB. Auto picks Limited under 48 GB of RAM.';
  $('#memoryText').textContent = ram ? `${base} This PC: ${fmtGB(ram)}, so Auto is ${ram < 48 * 1073741824 ? 'Limited' : 'Standard'}.` : base;
}
$$('.seg[data-setting]').forEach(seg => seg.addEventListener('click', e => {
  const b = e.target.closest('button');
  if (b) set(seg.dataset.setting, b.dataset.v);
}));
$$('input[type=checkbox][data-setting]').forEach(i => i.addEventListener('change', () => set(i.dataset.setting, i.checked)));
$$('input[type=text][data-setting], textarea[data-setting]').forEach(i => i.addEventListener('input', () => set(i.dataset.setting, i.value)));
$('#ambientOn').addEventListener('change', e => set('ambient', e.target.checked ? String(AMBIENT) : 'off'));
$('#ambientLevel').addEventListener('input', e => set('ambient', parseFloat(e.target.value).toFixed(2)));
$$('[data-pick]').forEach(b => b.onclick = async () => {
  const k = b.dataset.pick;
  try {
    const p = await call('pickFolder', { title: k === 'dataDir' ? 'Choose the data folder' : 'Choose the cache folder', start: get(k) || S.info[k] || '' });
    if (p) set(k, p);
  } catch (e) { toast(e.message, 'bad'); }
});
$$('[data-reset]').forEach(b => b.onclick = () => set(b.dataset.reset, ''));
$('#btnDefaults').onclick = async () => {
  if (!(await confirmBox('Reset every option?', 'All settings go back to VIctory\'s defaults. Your game folder stays.', 'Reset'))) return;
  const keep = { gamePath: S.settings.gamePath, firstRunSeen: S.settings.firstRunSeen };
  S.settings = Object.assign({}, keep);
  set('console', DEFAULTS.console);
  toast('Settings reset.', 'ok');
};
$('#btnClearCache').onclick = async () => {
  if (!(await confirmBox('Clear the shader cache?', 'Everything in the cache folder is deleted. The next start translates every shader and builds the pipelines again, which takes minutes.', 'Clear cache'))) return;
  try { const n = await call('clearCache'); toast(n ? `Cache cleared (${n} items).` : 'The cache was already empty.', 'ok'); }
  catch (e) { toast(e.message, 'bad'); }
};

// ---------------- before the first run ----------------
const toolBox = kind => $(kind === 'shaders' ? '#toolShaders' : '#toolWarm');
$$('[data-tool]').forEach(b => b.onclick = async () => {
  const kind = b.dataset.tool, box = toolBox(kind);
  try {
    await call('tool', { kind });
    box.classList.add('busy');
    b.disabled = true;
    $('[data-cancel-tool]', box).hidden = false;
    $('[data-tool-state]', box).textContent = 'Running…';
    $('[data-tool-last]', box).textContent = '';
  } catch (e) { toast(e.message, 'bad'); }
});
$$('[data-cancel-tool]').forEach(b => b.onclick = () => send('cancelTool'));
const toolSeen = { shaders: [], warm: [] };
handlers.toolLine = l => {
  const box = toolBox(l.kind);
  (toolSeen[l.kind] = toolSeen[l.kind] || []).push(l.t);
  if (box && l.t.trim()) $('[data-tool-last]', box).textContent = l.t;
};
handlers.toolDone = r => {
  const box = toolBox(r.kind);
  box.classList.remove('busy');
  $('[data-tool]', box).disabled = false;
  $('[data-cancel-tool]', box).hidden = true;
  const seen = (toolSeen[r.kind] || []).join('\n');
  toolSeen[r.kind] = [];
  // `victory warm --used` knows the pipelines earlier runs wrote down: none before the first run.
  if (r.kind === 'warm' && /\b0 pipelines known\b/.test(seen)) {
    $('[data-tool-state]', box).textContent = 'Nothing to warm yet: play once, then warm.';
    $('[data-tool-last]', box).textContent = '';
    toast('No pipelines recorded yet. Play once with "Remember pipelines" on, then warm.', 'warn');
    return;
  }
  $('[data-tool-state]', box).textContent = r.code === 0 ? 'Done.' : `Stopped (code ${r.code}). It goes on from there next time.`;
  toast(r.kind === 'shaders' ? (r.code === 0 ? 'Shaders prepared.' : 'Shader preparation stopped.') : (r.code === 0 ? 'Pipelines warmed.' : 'Warming stopped.'), r.code === 0 ? 'ok' : 'warn');
};

// ---------------- shader scan ----------------
let scanLines = [];
$('#btnScan').onclick = async () => {
  let lanes = get('scanLanes');
  const g = S.system && S.system.gpu;
  if (lanes === 'auto') lanes = g && g.waveMax ? `${g.waveMin}-${g.waveMax}` : '';
  if (lanes === 'any') lanes = '';
  try {
    await call('scan', { lanes, validate: !!get('scanValidate') && !!S.info.validator });
    scanLines = [];
    S.scanning = true; S.scanStart = Date.now();
    $('#btnScan').disabled = true;
    $('#btnScanCancel').hidden = false;
    $('#scanBar').hidden = false;
    $('#scanResults').hidden = true;
    $('#scanRawBox').hidden = true;
    scanClock();
  } catch (e) { toast(e.message, 'bad'); }
};
$('#btnScanCancel').onclick = () => send('cancelScan');
// ---------------- live ----------------
const LV = { light: null, kind: null, air: null };
function liveInit() {
  const amb = String(get('ambient'));
  const level = amb === 'off' ? AMBIENT : parseFloat(amb.replace('constant:', '')) || AMBIENT;
  const limited = get('memory') === 'limited' || (get('memory') !== 'standard' && S.system && S.system.ramTotal < 48 * 1073741824);
  LV.light = level; LV.kind = get('ambientMode') || 'constant'; LV.air = limited ? 1.5 : 0;
  $('#lvLight').value = LV.light; $('#lvAir').value = LV.air; $('#lvMore').value = '';
  liveShow();
}
function liveShow() {
  $('#lvLightOut').textContent = Number(LV.light).toFixed(2);
  $('#lvAirOut').textContent = Number(LV.air) > 0 ? Number(LV.air).toFixed(1) : 'title';
  $$('#lvKind button').forEach(b => b.classList.toggle('on', b.dataset.v === LV.kind));
}
let liveTimer = 0;
function liveSend() {
  liveShow();
  clearTimeout(liveTimer);
  liveTimer = setTimeout(async () => {
    const lines = [`set VICTORY_AMBIENT=${LV.kind === 'constant' ? 'constant:' : ''}${Number(LV.light).toFixed(2)}`];
    if (Number(LV.air) > 0) lines.push(`set VICTORY_AMBIENT_AIR=${Number(LV.air).toFixed(1)}`);
    $('#lvMore').value.split('\n').map(l => l.trim()).filter(l => /^VICTORY_[A-Z0-9_]+\s*=/.test(l)).forEach(l => lines.push('set ' + l));
    try { await call('liveSet', { text: lines.join('\n') }); $('#lvSaid').textContent = 'Sent: ' + lines.length + ' value' + (lines.length === 1 ? '' : 's'); }
    catch (e) { $('#lvSaid').textContent = e.message; }
  }, 150);
}
$('#lvLight').addEventListener('input', e => { LV.light = parseFloat(e.target.value); liveSend(); });
$('#lvAir').addEventListener('input', e => { LV.air = parseFloat(e.target.value); liveSend(); });
$$('#lvKind button').forEach(b => b.onclick = () => { LV.kind = b.dataset.v; liveSend(); });
$('#lvMore').addEventListener('change', liveSend);
$('#lvKeep').onclick = () => { set('ambient', Number(LV.light).toFixed(2)); set('ambientMode', LV.kind); toast('The light is kept for the next runs', 'ok'); };
handlers.live = l => {
  if (!l || !l.frames) return;
  $('#lvMedian').textContent = Number(l.median).toFixed(1);
  $('#lvFps').textContent = l.median > 0 ? (1000 / l.median).toFixed(1) : '—';
  $('#lvP90').textContent = Number(l.p90).toFixed(1);
  $('#lvDraws').textContent = num(l.draws);
};

// ---------------- test run ----------------
const SETTING_NAMES = {
  patches: { rtx: 'patches 1+5', all: 'patches all', off: 'no patches' },
  textureCheck: { exact: 'exact textures', guard: 'fast textures' },
  lastResort: { on: 'last resort', off: 'no last resort' },
  mode: { fast: 'Fast', safe: 'Safe' },
};
function settingsLine(s) {
  if (!s) return '';
  const v = k => s[k] == null ? DEFAULTS[k] : s[k];
  const parts = ['mode', 'patches', 'textureCheck', 'lastResort'].map(k => (SETTING_NAMES[k] || {})[v(k)] || `${k} ${v(k)}`);
  parts.push(s.consoleModel === 'series-s' ? 'Series S' : 'Series X');
  if (s.limited) parts.push('limited memory');
  if (v('fps60')) parts.push('60 fps');
  if (v('rayTracing')) parts.push('ray tracing');
  if (s.customEnv) parts.push('+ variables');
  return parts.join(' · ');
}
async function loadTests() {
  try { renderTests(await call('tests')); } catch (e) { /* no results yet */ }
}
function renderTests(list) {
  if (!list || !list.length) { $('#testResults').innerHTML = ''; return; }
  const rows = list.map(r => {
    const when = String(r.id || '').replace(/^(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2}).*/, '$1-$2-$3 $4:$5');
    const result = r.problem ? `<td colspan="4" class="e">${esc(r.problem)}</td>`
      : `<td class="c">${Number(r.median).toFixed(1)} ms</td><td class="c">${Number(r.fps).toFixed(1)}</td><td class="c">${Number(r.p90).toFixed(1)} ms</td><td class="c">${num(r.draws)}</td>`;
    const pic = r.picture ? `<button class="btn ghost sm" data-file="${esc(r.picture)}">Picture</button>` : '';
    return `<tr><td>${esc(when)}</td><td>${esc(settingsLine(r.settings))}</td>${result}<td class="c">${Number(r.peakGB || 0).toFixed(1)} GB</td><td>${pic}</td></tr>`;
  }).join('');
  $('#testResults').innerHTML = `<table class="reasons"><thead><tr><th>When</th><th>Settings</th><th style="text-align:right">Median</th><th style="text-align:right">fps</th><th style="text-align:right">90%</th><th style="text-align:right">Draws</th><th style="text-align:right">Memory</th><th></th></tr></thead><tbody>${rows}</tbody></table>`;
  $$('#testResults [data-file]').forEach(b => b.onclick = () => send('openFile', { path: b.dataset.file }));
}
$('#btnTest').onclick = async () => {
  try {
    const st = await call('test');
    applyState(st);
    $('#btnTest').disabled = true;
    $('#testStatus').textContent = 'Starting the game…';
  } catch (e) { toast(e.message, 'bad'); }
};
handlers.test = t => {
  $('#btnTest').disabled = true;
  const s = Math.floor(t.seconds);
  $('#testStatus').textContent = t.phase === 'waiting'
    ? `Waiting for the busy scene… ${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
    : `Measuring… ${Math.ceil(t.left)} s left, ${t.samples} samples`;
};
handlers.testDone = r => {
  $('#btnTest').disabled = false;
  $('#testStatus').textContent = r.problem ? `Ended: ${r.problem}` : `Median ${Number(r.median).toFixed(1)} ms a frame (${Number(r.fps).toFixed(1)} fps)`;
  toast(r.problem ? 'Test run ended: ' + r.problem : `Test run: ${Number(r.fps).toFixed(1)} fps`, r.problem ? 'warn' : 'ok');
  loadTests();
};
function scanClock() {
  if (!S.scanning) return;
  const s = Math.floor((Date.now() - S.scanStart) / 1000);
  $('#scanState').textContent = `Translating… ${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  setTimeout(scanClock, 500);
}
handlers.scanLine = l => { scanLines.push(l.t); };
handlers.scanDone = r => {
  S.scanning = false;
  $('#btnScan').disabled = false;
  $('#btnScanCancel').hidden = true;
  $('#scanBar').hidden = true;
  $('#scanRaw').textContent = scanLines.join('\n');
  $('#scanRawBox').hidden = false;
  const p = parseScan(scanLines);
  if (r.code !== 0 && !p.found) {
    $('#scanState').textContent = r.code === -1 || r.code === 1 && !scanLines.length ? 'Cancelled.' : 'The scan stopped with code ' + r.code + '. See the full output.';
    return;
  }
  $('#scanState').textContent = `Done in ${Math.round((Date.now() - S.scanStart) / 1000)} s${p.lanes ? ' · for ' + p.lanes + ' lanes' : ''}.`;
  renderScan(p);
};
function parseScan(lines) {
  const p = { found: false, reasons: [] };
  for (const l of lines) {
    let m;
    if ((m = /^host waves\s+(\S+) lanes/.exec(l))) p.lanes = m[1];
    if ((m = /^containers\s+(\d+) \((\d+) duplicates/.exec(l))) { p.containers = +m[1]; p.found = true; }
    if ((m = /^programs\s+(\d+) translated of (\d+)/.exec(l))) { p.translated = +m[1]; p.programs = +m[2]; }
    if ((m = /(\d+) emitted of (\d+) vertex and pixel programs; (\d+) validated/.exec(l))) p.vp = [+m[1], +m[2], +m[3]];
    if ((m = /(\d+) emitted of (\d+) compute programs; (\d+) validated/.exec(l))) p.cs = [+m[1], +m[2], +m[3]];
    if ((m = /(\d+) emitted of (\d+) mesh programs; (\d+) validated/.exec(l))) p.ms = [+m[1], +m[2], +m[3]];
    if ((m = /^\s+(\d+)\s{2}(.+?)\s{2}\(e\.g\. (.*)\)\s*$/.exec(l))) p.reasons.push([+m[1], m[2], m[3]]);
  }
  p.validated = /^validator/m.test(lines.join('\n'));
  return p;
}
function renderScan(p) {
  if (!p.containers) {
    $('#scanResults').innerHTML = '<p class="muted" style="margin:4px 0 0">No shaders were found in this folder. The game keeps them in its <code>.rpf</code> archives, such as <code>shaders.rpf</code>; point the launcher at the full game folder.</p>';
    $('#scanResults').hidden = false;
    return;
  }
  const stat = (k, a) => {
    if (!a) return '';
    const [em, tot, val] = a;
    const good = p.validated ? val : em;
    const pct = tot ? (good / tot) * 100 : 0;
    return `<div class="stat"><div class="k">${k}</div><div class="n">${num(good)} <small>of ${num(tot)}</small></div>
      <div class="meter"><i style="width:${pct.toFixed(1)}%"></i></div>
      <div class="s">${pct.toFixed(1)}% ${p.validated ? 'translate and validate' : 'translate'}${p.validated && em !== val ? ` · ${num(em - val)} fail validation` : ''}</div></div>`;
  };
  const rows = p.reasons.slice(0, 40).map(r => `<tr><td class="c">${num(r[0])}</td><td>${esc(r[1])}</td><td class="e">${esc(r[2])}</td></tr>`).join('');
  $('#scanResults').innerHTML = `<div class="stat-grid">${stat('Compute', p.cs)}${stat('Vertex and pixel', p.vp)}${stat('Mesh', p.ms)}</div>
    ${rows ? `<table class="reasons"><thead><tr><th style="text-align:right">Count</th><th>Why a program does not translate</th><th>Example</th></tr></thead><tbody>${rows}</tbody></table>` : '<p class="muted" style="margin-top:14px">Every program translated.</p>'}`;
  $('#scanResults').hidden = false;
}

// ---------------- what's inside ----------------
const BASE = [
  ['List calls written down, not carried out, on the title\'s threads', 'Eight threads of ours translate them (VICTORY_THIN): 75 to 68 ms a frame.'],
  ['Submissions prepared on a thread of ours and joined', 'Refreshes, compares (AVX2), descriptor records and landings spread over threads (VICTORY_ASYNC_SUBMIT, VICTORY_JOIN_PARTS).'],
  ['What shaders wrote comes back beside the write guards', 'By the dwords that changed, on a thread of its own (VICTORY_RELAY, VICTORY_LAND).'],
  ['Buffer fills done on the card', 'Fills that need bytes still on the card run there instead of waiting for it (VICTORY_CARD_STEPS).'],
  ['The title\'s writes caught more cheaply', 'One view opened at a fault, blocks that rest, places in the title\'s code that look before they write: 69 to about 6 ms of threads\' time.'],
  ['Textures on a copy queue of their own', 'A slot shows a texture only after its upload ran, which ended a device loss every 15 minutes or so.'],
  ['Boats float', 'Textures that shaders write and the title\'s CPU reads go back to its memory (texture read-back, from fxceless and Geckomacabre).'],
  ['Frame record and replay', 'Per-pass times and shader variants compared turn by turn.'],
];
const OURS = [
  { t: 'ds_bpermute and ds_permute per 32-lane half', tag: 'fix', d: 'As AMD\'s RDNA ISA says (10.4.4). ds_bpermute crossed halves on 64-lane GPUs and was refused on NVIDIA: the title\'s checkerboard shadow-mask generators. ds_permute was not translated at all.' },
  { t: 'Groups that are not a multiple of 32 threads', tag: 'fix', d: 'The one short host wave of a group of 48, 80 or 112 threads could take the first console threads and leave others to no thread (24 lanes counted of 40). It takes the group\'s last run now.' },
  { t: 'Scalars after divergent loops and branches', tag: 'fix', d: 'Every lane reads what the wave\'s last iteration or last path left, as on the console (upstream b67d15d, TheHoracio), over both halves on NVIDIA. Before, each lane read its own: 1, 2, 3 … instead of 64.' },
  { t: 'No group barrier inside a divergent loop', tag: 'fix', d: 'The blocks of a loop that lanes leave at different times were counted as blocks every lane runs, so an exchange there put a barrier in a divergent loop.' },
  { t: 'WaveActiveAnyTrue choosing a value; masks grown in branches', d: 'A mask operation\'s SCC read as a number is the console wave\'s, and a mask grown inside a branch translates on 64-lane GPUs. Waterfall loops stay without wave operations.' },
  { t: 'DPP row masks name the console\'s rows', tag: 'fix', d: 'In the upper half of a console wave run as two host waves, row_mask bits 2 and 3 name its rows; the host lanes 0 to 31 named rows 0 and 1.' },
  { t: 'Wave64 last resort', d: 'A console wave64 program that no form translates for a 32-lane card takes its per-lane branches by the whole wave at once, instead of the game going without it. 2,757 of the game\'s 2,763 compute programs translate on NVIDIA (2,737 before): the water\'s volumetric light and GPU particle emitters among them; none that translated changed.' },
  { t: 'Whole-wave sums and minimums over both halves', tag: 'fix', d: 'The s_add or s_min joining the two halves\' results stopped the program (upstream a8bfe78\'s scalar steps, ported); a console wave with no upper half counts it as the identity, not 0.' },
];
const COOLFOX = [
  { t: 'Memory for 32 GB of RAM', d: 'Residency and upload budgets; a GPU pool that cannot grow backs off instead of failing, and uploads wait while Windows is short of commit. 120 heavy intervals with no out-of-memory return at a 34.4 GB peak (Memory profile: Limited).' },
  { t: 'Descriptor slots freed when the card is done', tag: 'fix', d: 'Slots go back when the GPU finishes the work that used them, and bindless refreshes stay within their own heap.' },
  { t: 'Series S two-plane black screen', tag: 'fix', d: 'The retail plane is read at its 72-byte stride; the two planes compose again.' },
  { t: 'More wave64 programs on NVIDIA', d: 'Wave-wide branches on by default for two scene kernels checked on RTX cards, and for loops that run while any lane of the console wave does: both host halves keep going until both finish. A mask operation\'s SCC chosen on is the whole console wave\'s. A program left needing 64-lane waves on a 32-lane card is refused at translation.' },
  { t: 'XMA audio, movie planes, fence wakes', tag: 'fix', d: 'XMA decoding fixes, movie-plane uploads spread over frames, fences woken with WaitOnAddress instead of polling.' },
  { t: 'Shader fixes', tag: 'fix', d: 'd16 typed integer loads and stores, and the write guard on join edges no lane reaches.' },
];
const SMALL = [
  { t: 'Root views copy their cell where shared memory is short', tag: 'fix', d: 'A root view\'s window reached up to 32 MiB past its 4 MiB cell (avatarmanz #329, against a card fault on an RX 7900 XT). Every window is a copy in the PC\'s memory shared with the card, and the world\'s first frame asks for hundreds: under a 12 GiB budget for that memory (PCs with less than about 24 GB of RAM) a window is its cell again, as before.' },
  { t: 'Out of memory is waited out, not the end of the run', tag: 'fix', d: 'A command list, its prelude or a reset that Windows refused memory for ended the run. The idle command lists are let go and the call is tried again for up to 1.6 s, while Windows grows its page file.' },
  { t: 'Textures stop at the brink of shared memory', d: 'The texture queue counted the card\'s own memory only. It makes no texture while the PC\'s memory shared with the card is at 95 % of its budget.' },
  { t: 'Scarlett picture fixes (avatarmanz #329)', tag: 'fix', d: 'From RYUKI-7900XT via zocram4cc: early depth, 16-bit depth image bits, a NaN guard on colour exports, strict scalar spills; an aliased view is copied again when its card-written source changes. Every shader is translated again once: Settings, Prepare shaders.' },
  { t: 'Mesh groups of two and three dimensions (Marvisak #3304)', tag: 'fix', d: 'A mesh group\'s index divides by X × Y × Z threads and the output maxima, not X alone: a group of 8 × 8 read another group\'s meshlets. fuzeofficial\'s fix, ported with its test.' },
  { t: 'Launcher: the game\'s path survives a failed read', tag: 'fix', d: 'A start that could not read settings.json (another program held it, or a save was cut short) showed “Add your game”, and closing it wrote the empty settings over the path. It now reads again, falls back to the last copy, merges instead of overwriting, and saves by an atomic replace. Test run pictures come from the game\'s window itself, under other windows too.' },
  { t: 'Taken back out: avatarmanz #293', d: 'Textures refused for want of memory were asked for again every 500 ms; on an 8 GB card the one budget left for them is the shared memory, the one the runs met.' },
];
function renderInside() {
  const i = S.info;
  const c = [[i.victoryCommit, 'this build'], [i.baseCommit, 'avatarmanz/VIctory-7900XTX'], [i.fixCommit, 'our fixes'], [i.built, 'built']];
  $('#commits').innerHTML = c.map(([v, k]) => `<div><b>${esc(v || '—')}</b><span>${k}</span></div>`).join('');
  if (i.source) $('#insideSub').innerHTML = `The VIctory build this launcher carries, and what is in it. Source: <a href="${esc(i.source)}">${esc(i.source.replace('https://', ''))}</a>`;
  let h = `<div class="group"><div class="group-head"><h2>The fastest fork as its base</h2><span>by avatarmanz ·</span><a href="https://github.com/avatarmanz/VIctory-7900XTX">VIctory-7900XTX</a></div>
    <div class="measure"><div class="big">280 → 44.9 ms</div><p>a frame in the same scenes on an RX 7900 XTX, as its author measured: 57–75 ms before its renderer ports, 44.9 ms (about 22 fps) with ports 1, 2, 5, 6 and 7. Merged here up to c9c932cb. Built on fxceless/victory-fork, with DexNom's, Belobug's, ZJOU's and myokex's work in its history.</p></div>
    <div class="patches">${BASE.map(([t, d]) => `<div class="patch"><div class="t">${esc(t)}</div><p>${esc(d)}</p></div>`).join('')}</div></div>`;
  h += `<div class="group"><div class="group-head"><h2>Added here: console wave64 exact on NVIDIA</h2><span>by God489 ·</span><a href="https://github.com/Belobug/VIctory-Belobug-AMD/pull/128">from #68 and #128</a></div>
    <div class="measure"><div class="big">8 → 29 of 29</div><p>programs of the console's 64-lane waves give the console's values, lane by lane, on an RTX 3050 through the whole translation (21 were wrong or refused before), and 22 of the 26 wave64 tests of #68 and #128 pass (10 before). The fork's own 916 tests still pass.</p></div>
    <div class="patches">${OURS.map(p => `<div class="patch"><div class="t">${esc(p.t)}${p.tag === 'fix' ? '<span class="tag fix">bug fix</span>' : ''}</div><p>${esc(p.d)}</p></div>`).join('')}</div></div>`;
  h += `<div class="group"><div class="group-head"><h2>Added here: 8 GB cards with 16 GB of RAM</h2><span>by God489 ·</span><a href="https://github.com/God489/VIctory-Launcher">this repository</a></div>
    <div class="measure"><div class="big">5 of 6 → 0 of 3</div><p>runs of the prologue that ended by themselves within three minutes on an RTX 3050 (8 GB) with 16 GB of RAM, before and after these fixes. There the PC's memory shared with the card has a budget of 7.4 GB, and copies of the title's memory filled it at the world's first frame; with these fixes it peaked at 6.4 to 7.0 GB, and frames cost the same: 88 to 130 ms per thousand draws, 118 before.</p></div>
    <div class="patches">${SMALL.map(p => `<div class="patch"><div class="t">${esc(p.t)}${p.tag === 'fix' ? '<span class="tag fix">bug fix</span>' : ''}</div><p>${esc(p.d)}</p></div>`).join('')}</div></div>`;
  h += `<div class="group"><div class="group-head"><h2>Added here: NVIDIA and 32 GB of RAM</h2><span>by codec_d ·</span><a href="https://github.com/DoubleGhosty/cool_fox">cool_fox</a></div>
    <div class="measure"><div class="big">54 ms</div><p>median frame time over 120 heavy intervals of the opening street on an RTX 5090 with 32 GB of RAM, as its author measured with the Limited memory profile, with no out-of-memory error at a 34.4 GB peak (the build before its pool back-off ran out). Its earlier build took 64.2 ms where this took 54.8, in one directional pair of runs. Merged on the same base; every test here passes with it, two of the base's updated for its new default.</p></div>
    <div class="patches">${COOLFOX.map(p => `<div class="patch"><div class="t">${esc(p.t)}${p.tag === 'fix' ? '<span class="tag fix">bug fix</span>' : ''}</div><p>${esc(p.d)}</p></div>`).join('')}</div></div>`;
  h += `<div class="group"><div class="group-head"><h2>Upstream</h2><span>by Marvisak and contributors ·</span><a href="https://github.com/Marvisak/VIctory">VIctory</a></div>
    <div class="card"><p class="muted" style="margin:0">The title's x86-64 code runs unchanged inside VIctory's process; VIctory supplies the platform underneath it and converts the title's data, ahead of time where it can and on first use where it must (shaders).</p></div></div>`;
  $('#insideList').innerHTML = h;
  $('#buildChip').textContent = 'build ' + (i.victoryCommit || '—');
  $('#aboutVer').textContent = i.launcher || '';
  $('#aboutFolders').innerHTML = [
    ['Launcher settings', i.home, 'logs'], ['VIctory data', i.dataDir, 'data'], ['VIctory cache', i.cacheDir, 'cache'], ['Unpacked runtime', i.runtime, 'runtime'],
  ].map(([k, v, w]) => `<span>${k}</span><code>${esc(v || '—')}</code><button class="btn ghost sm" data-open="${w}">Open</button>`).join('');
}

// ---------------- logs ----------------
const MAX_LINES = 5000;
let logs = [], unread = 0;
const con = $('#console');
function lineEl(l) {
  const d = document.createElement('div');
  let c = 'l';
  if (l.t.startsWith('VIctory Launcher:')) c += ' me';
  else if (l.e && /error|panick|fail/i.test(l.t) || /\bERROR\b/.test(l.t)) c += ' err';
  else if (/\bWARN\b/.test(l.t)) c += ' warn';
  d.className = c;
  d.textContent = l.t;
  return d;
}
function visible(l) {
  const f = $('#logFilter').value.toLowerCase();
  if ($('#logErrors').checked && !(l.e || /ERROR|WARN|panick/i.test(l.t))) return false;
  return !f || l.t.toLowerCase().includes(f);
}
function scrollLog(force) { if (force || $('#logFollow').checked) con.scrollTop = con.scrollHeight; }
function redrawLog() {
  con.textContent = '';
  const frag = document.createDocumentFragment();
  logs.filter(visible).slice(-MAX_LINES).forEach(l => frag.appendChild(lineEl(l)));
  if (!frag.childNodes.length) con.innerHTML = '<div class="empty">Nothing yet. Start the game to see its log here.</div>';
  else con.appendChild(frag);
  scrollLog(true);
}
handlers.log = batch => {
  const empty = con.querySelector('.empty');
  if (empty) empty.remove();
  logs.push(...batch);
  if (logs.length > MAX_LINES * 2) logs = logs.slice(-MAX_LINES);
  const frag = document.createDocumentFragment();
  batch.filter(visible).forEach(l => frag.appendChild(lineEl(l)));
  con.appendChild(frag);
  while (con.childNodes.length > MAX_LINES) con.firstChild.remove();
  scrollLog();
  if (!$('#page-logs').classList.contains('active')) {
    const errs = batch.filter(l => l.e && /error|panick/i.test(l.t)).length;
    if (errs) { unread += errs; $('#logBadge').textContent = unread > 99 ? '99+' : unread; $('#logBadge').hidden = false; }
  }
};
$('#logFilter').addEventListener('input', redrawLog);
$('#logErrors').addEventListener('change', redrawLog);
$('#btnCopyLog').onclick = () => send('copy', { text: logs.filter(visible).map(l => l.t).join('\r\n') }).then(() => toast('Log copied.', 'ok'));
$('#btnSaveLog').onclick = () => send('saveLog', { text: logs.map(l => l.t).join('\r\n') }).then(ok => ok && toast('Log saved.', 'ok'));
$('#btnClearLog').onclick = () => { logs = []; redrawLog(); };

// ---------------- start ----------------
async function init() {
  try {
    const r = await call('init');
    S.settings = r.settings || {};
    S.info = r.info || {};
    S.game = r.game;
    renderGame();
    renderInside();
    syncControls();
    applyState({ running: r.running });
  } catch (e) { toast('The launcher could not start: ' + e.message, 'bad'); }
}

// ---------------- preview outside the launcher (no host) ----------------
var Mock = {
  call(cmd, args) {
    const ok = v => Promise.resolve(v);
    if (cmd === 'init') {
      setTimeout(() => handlers.system({ cpu: 'AMD Ryzen 7 7800X3D 8-Core Processor', cores: 16, avx2: true, ramTotal: 32 * 1073741824, commitFree: 41 * 1073741824, commitLimit: 52 * 1073741824,
        gpu: { name: 'NVIDIA GeForce RTX 4070 Ti SUPER', vendor: 'NVIDIA', vram: 16 * 1073741824, d3d12: true, waveMin: 32, waveMax: 32, shaderModel: 0x68, meshTier: 10 } }), 300);
      return ok({ settings: {}, info: { launcher: '1.1.0', victoryCommit: 'e630b18', baseCommit: '0a43566', fixCommit: 'e630b18', built: '2026-11-26', dataDir: 'C:\\Users\\you\\AppData\\Local\\victory\\data', cacheDir: 'C:\\Users\\you\\AppData\\Local\\victory\\cache', home: 'C:\\Users\\you\\AppData\\Local\\VIctory Launcher', runtime: '…\\runtime\\1a2b3c', validator: '' },
        game: { found: true, exe: 'D:\\Games\\Series\\game_scarlett_master.exe', exeName: 'game_scarlett_master.exe', title: 'Your game', version: '1.0.2.1', config: true, appdata0: true, rpfCount: 38 }, running: false });
    }
    if (cmd === 'play') { setTimeout(() => handlers.state({ running: true, started: Date.now(), caption: ' | VIctory 4.6 fps 217.4 ms/frame 9120 draws/frame' }), 400); return ok({ running: true, started: Date.now() }); }
    if (cmd === 'stop') { setTimeout(() => handlers.exited({ code: 0, seconds: 75 }), 300); return ok(true); }
    return ok(null);
  },
};

syncControls();
init();
