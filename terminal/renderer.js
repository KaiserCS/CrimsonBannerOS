'use strict';
const stage = document.getElementById('stage');
const tablist = document.getElementById('tablist');
const message = document.getElementById('message');

const tabs = [];                       // { id, term, fit, pane, tabEl, labelEl, title }
const pendingData = new Map();         // output that arrives before its tab is registered
let active = null;
let theme = { dark: true, accent: '#0078D4' };
let fontSize = Number(localStorage.getItem('rs.fontSize')) || 14;
let initial = { cwd: null, ok: true, resize: true };

// ------------------------------------------------------------------ theme

const ANSI_DARK = {
  black: '#1C1C1C', red: '#F4707C', green: '#7BD88F', yellow: '#F2C811', blue: '#5AA9F5', magenta: '#C792EA',
  cyan: '#56D4DD', white: '#D8D8D8', brightBlack: '#6B6B6B', brightRed: '#FF99A4', brightGreen: '#A5E8B5',
  brightYellow: '#FCE66D', brightBlue: '#8CC4FF', brightMagenta: '#DDB3F5', brightCyan: '#8FE8EE', brightWhite: '#FFFFFF',
};
const ANSI_LIGHT = {
  black: '#1A1A1A', red: '#C50F1F', green: '#107C10', yellow: '#946B00', blue: '#0050B3', magenta: '#881798',
  cyan: '#0E7C86', white: '#8A8A8A', brightBlack: '#5F5F5F', brightRed: '#E81123', brightGreen: '#13A10E',
  brightYellow: '#B88700', brightBlue: '#0078D4', brightMagenta: '#B146C2', brightCyan: '#2AA7B3', brightWhite: '#C8C8C8',
};

function xtermTheme() {
  const dark = theme.dark;
  return {
    ...(dark ? ANSI_DARK : ANSI_LIGHT),
    background: dark ? '#1C1C1C' : '#FFFFFF',
    foreground: dark ? '#E6E6E6' : '#1A1A1A',
    cursor: theme.accent,
    cursorAccent: dark ? '#1C1C1C' : '#FFFFFF',
    selectionBackground: theme.accent + '66',
  };
}

function applyTheme(t) {
  if (t) theme = { dark: !!t.dark, accent: t.accent || theme.accent };
  document.documentElement.dataset.theme = theme.dark ? 'dark' : 'light';
  document.documentElement.style.setProperty('--accent', theme.accent);
  for (const tab of tabs) tab.term.options.theme = xtermTheme();
}

// ------------------------------------------------------------------ tabs

function showMessage(html) {
  message.innerHTML = html;
  message.hidden = false;
}

function updateTitle() {
  document.title = (active && active.title ? active.title : 'Terminal') + ' - Red Star Terminal';
}

function select(tab) {
  active = tab;
  for (const t of tabs) {
    t.pane.classList.toggle('active', t === tab);
    t.tabEl.classList.toggle('active', t === tab);
  }
  if (tab) {
    requestAnimationFrame(() => { tab.fit.fit(); tab.term.focus(); });
    tab.tabEl.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }
  updateTitle();
}

function setTitle(tab, title) {
  tab.title = title || 'Terminal';
  tab.labelEl.textContent = tab.title;
  tab.tabEl.title = tab.title;
  if (tab === active) updateTitle();
}

async function copySelection(term) {
  const text = term.getSelection();
  if (text) { window.redstar.writeClipboard(text); term.clearSelection(); }
  return !!text;
}

async function pasteInto(term) {
  const text = await window.redstar.readClipboard();
  if (text) term.paste(text);
}

async function newTab(cwd) {
  const term = new window.Terminal({
    fontFamily: '"JetBrains Mono", "Cascadia Mono", "DejaVu Sans Mono", "Liberation Mono", monospace',
    fontSize,
    cursorBlink: true,
    scrollback: 5000,
    allowProposedApi: false,
    theme: xtermTheme(),
  });
  const fit = new window.FitAddon.FitAddon();
  term.loadAddon(fit);

  const pane = document.createElement('div');
  pane.className = 'pane';
  stage.appendChild(pane);
  term.open(pane);

  const tabEl = document.createElement('div');
  tabEl.className = 'tab';
  const labelEl = document.createElement('span');
  labelEl.className = 'label';
  const closeBtn = document.createElement('button');
  closeBtn.className = 'close';
  closeBtn.textContent = '×';
  closeBtn.title = 'Close tab (Ctrl+Shift+W)';
  tabEl.append(labelEl, closeBtn);
  tablist.appendChild(tabEl);

  const tab = { id: 0, term, fit, pane, tabEl, labelEl, title: 'Terminal' };
  tabs.push(tab);
  setTitle(tab, 'Terminal');
  select(tab);

  // Keyboard shortcuts that belong to the terminal window, not to the shell.
  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    const ctrl = e.ctrlKey && !e.altKey && !e.metaKey;
    const key = e.key.toLowerCase();
    if (ctrl && e.shiftKey && key === 'c') { copySelection(term); return false; }
    if (ctrl && e.shiftKey && key === 'v') { pasteInto(term); return false; }
    if (ctrl && e.shiftKey && key === 't') { newTab(null); return false; }
    if (ctrl && e.shiftKey && key === 'w') { closeTab(tab); return false; }
    if (ctrl && (e.key === '=' || e.key === '+')) { zoom(1); return false; }
    if (ctrl && e.key === '-') { zoom(-1); return false; }
    if (ctrl && e.key === '0') { zoom(0); return false; }
    if (ctrl && e.key === 'PageDown') { cycle(1); return false; }
    if (ctrl && e.key === 'PageUp') { cycle(-1); return false; }
    if (e.altKey && !e.ctrlKey && /^[1-9]$/.test(e.key)) { const t = tabs[Number(e.key) - 1]; if (t) select(t); return false; }
    return true;
  });

  // Right click: copy when something is selected, otherwise paste.
  pane.addEventListener('contextmenu', async (e) => {
    e.preventDefault();
    if (!(await copySelection(term))) await pasteInto(term);
  });

  term.onTitleChange((t) => setTitle(tab, t));
  tabEl.addEventListener('click', (e) => { if (e.target !== closeBtn) select(tab); });
  closeBtn.addEventListener('click', () => closeTab(tab));

  const dims = fit.proposeDimensions() || { cols: 80, rows: 24 };
  const res = await window.redstar.create(dims.cols, dims.rows, cwd || initial.cwd);
  if (!res || !res.id) {
    term.write('\r\n' + ((res && res.error) || 'Could not start a shell.') + '\r\n');
    return tab;
  }
  tab.id = res.id;

  term.onData((d) => window.redstar.input(tab.id, d));
  term.onResize(({ cols, rows }) => window.redstar.resize(tab.id, cols, rows));

  const early = pendingData.get(tab.id);
  if (early) { pendingData.delete(tab.id); for (const chunk of early) term.write(chunk); }
  return tab;
}

function closeTab(tab) {
  const i = tabs.indexOf(tab);
  if (i < 0) return;
  if (tab.id) window.redstar.kill(tab.id);
  tabs.splice(i, 1);
  tab.term.dispose();
  tab.pane.remove();
  tab.tabEl.remove();
  if (tabs.length === 0) { window.redstar.quit(); return; }
  if (active === tab) select(tabs[Math.min(i, tabs.length - 1)]);
}

function cycle(step) {
  if (tabs.length < 2 || !active) return;
  select(tabs[(tabs.indexOf(active) + step + tabs.length) % tabs.length]);
}

function zoom(step) {
  fontSize = step === 0 ? 14 : Math.min(32, Math.max(8, fontSize + step));
  localStorage.setItem('rs.fontSize', String(fontSize));
  for (const t of tabs) t.term.options.fontSize = fontSize;
  if (active) requestAnimationFrame(() => active.fit.fit());
}

// ------------------------------------------------------------------ events from the main process

window.redstar.onData((id, data) => {
  const tab = tabs.find((t) => t.id === id);
  if (tab) tab.term.write(data);
  else {
    if (!pendingData.has(id)) pendingData.set(id, []);
    pendingData.get(id).push(data);
  }
});

window.redstar.onExit((id) => {
  const tab = tabs.find((t) => t.id === id);
  if (!tab) return;
  tab.id = 0;                          // already gone: do not try to kill it again
  closeTab(tab);                       // the shell ended (exit, Ctrl+D): close its tab
});

window.redstar.onNewTab((cwd) => { newTab(cwd); });
window.redstar.onTheme(applyTheme);

document.getElementById('newtab').addEventListener('click', () => newTab(null));
stage.addEventListener('mousedown', () => { if (active) setTimeout(() => active.term.focus(), 0); });

let resizeTimer = null;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { if (active) active.fit.fit(); }, 30);
});

// ------------------------------------------------------------------ start

(async function init() {
  if (typeof window.Terminal !== 'function' || !window.FitAddon) {
    showMessage('<div><h2>Terminal components are missing</h2>' +
      '<p>Run <code>npm run setup</code> inside the <code>terminal</code> folder (needs network once),<br>' +
      'or rebuild the AppImage.</p></div>');
    return;
  }
  try { applyTheme(await window.redstar.theme()); } catch (_e) { /* keep defaults */ }
  initial = await window.redstar.initial();
  if (!initial.ok) {
    showMessage('<div><h2>No way to start a shell</h2><p>Install <code>python3</code> (or <code>util-linux</code>\'s <code>script</code>).</p></div>');
    return;
  }
  await newTab(null);
})();
