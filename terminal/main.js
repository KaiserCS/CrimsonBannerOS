'use strict';
const { app, BrowserWindow, Menu, ipcMain, clipboard } = require('electron');
const { spawn, spawnSync } = require('child_process');
const { StringDecoder } = require('string_decoder');
const path = require('path');
const fs = require('fs');
const os = require('os');

let win = null;
let bridge = null;
let nextId = 1;
const sessions = new Map();                       // id -> { proc, ctl }

// Shell integration (taskbar button, theme). Packaged: ../redstar-shared, source tree: ../shared.
function loadBridge(options) {
  for (const dir of [path.join(__dirname, '..', 'redstar-shared'), path.join(__dirname, '..', 'shared')]) {
    try { return require(path.join(dir, 'shell-bridge.js')).createBridge(options); } catch (_e) { /* next */ }
  }
  return { attach() {}, raise() {}, setTray() {}, removeTray() {}, notify() {}, onTheme() {}, close() {},
           theme: { dark: true, accent: '#0078D4' } };
}

// ---------------------------------------------------------------- shells

function has(cmd) {
  const r = spawnSync('which', [cmd], { stdio: 'ignore' });
  return r.status === 0;
}

const HELPER = path.join(__dirname, 'ptyhelper.py');
const USE_PYTHON = has('python3') && fs.existsSync(HELPER);
const USE_SCRIPT = !USE_PYTHON && has('script');

function userShell() {
  const sh = process.env.SHELL;
  return sh && fs.existsSync(sh) ? sh : (fs.existsSync('/bin/bash') ? '/bin/bash' : '/bin/sh');
}

function validDir(dir) {
  try { return dir && fs.statSync(dir).isDirectory() ? dir : null; } catch (_e) { return null; }
}

function cwdFromArgs(argv) {
  const a = argv.find((x) => x.startsWith('--cwd='));
  return a ? validDir(a.slice(6)) : null;
}

function startSession(cols, rows, cwd) {
  const id = nextId++;
  const dir = validDir(cwd) || os.homedir();
  const shell = userShell();
  const env = { ...process.env };
  delete env.ELECTRON_RUN_AS_NODE;
  delete env.REDSTAR_SHELL_SOCKET;

  let proc;
  let ctl = null;
  if (USE_PYTHON) {
    proc = spawn('python3', [HELPER, String(cols), String(rows), dir, shell],
      { stdio: ['pipe', 'pipe', 'ignore', 'pipe'], env: { ...env, REDSTAR_CTL_FD: '3' } });
    ctl = proc.stdio[3];
    ctl.on('error', () => {});
  } else if (USE_SCRIPT) {
    // Fallback without python: util-linux "script" allocates the pty. No live resize in this mode.
    proc = spawn('script', ['-qfc', `stty cols ${cols} rows ${rows}; exec "${shell}"`, '/dev/null'],
      { cwd: dir, stdio: ['pipe', 'pipe', 'ignore'], env: { ...env, TERM: 'xterm-256color', COLORTERM: 'truecolor' } });
  } else {
    return { id: 0, error: 'Neither python3 nor script (util-linux) was found, so a shell cannot be started.' };
  }

  const decoder = new StringDecoder('utf8');
  const send = (channel, ...args) => { if (win && !win.isDestroyed()) win.webContents.send(channel, ...args); };
  proc.stdout.on('data', (d) => { const s = decoder.write(d); if (s) send('term:data', id, s); });
  proc.stdin.on('error', () => {});
  proc.on('error', (e) => send('term:data', id, `\r\n[Could not start the shell: ${e.message}]\r\n`));
  proc.on('exit', (code) => { sessions.delete(id); send('term:exit', id, code === null ? 1 : code); });

  sessions.set(id, { proc, ctl });
  return { id };
}

function killAll() {
  for (const { proc } of sessions.values()) { try { proc.kill('SIGHUP'); } catch (_e) { /* gone */ } }
}

ipcMain.handle('term:create', (_e, cols, rows, cwd) =>
  startSession(Math.max(2, cols | 0), Math.max(2, rows | 0), typeof cwd === 'string' ? cwd : null));
ipcMain.on('term:input', (_e, id, data) => {
  const s = sessions.get(id);
  if (s && typeof data === 'string') s.proc.stdin.write(data);
});
ipcMain.on('term:resize', (_e, id, cols, rows) => {
  const s = sessions.get(id);
  if (s && s.ctl && cols > 1 && rows > 1) s.ctl.write(`R ${cols | 0} ${rows | 0}\n`);
});
ipcMain.on('term:kill', (_e, id) => {
  const s = sessions.get(id);
  if (s) { try { s.proc.kill('SIGHUP'); } catch (_e2) { /* gone */ } }
});
ipcMain.handle('clip:read', () => clipboard.readText());
ipcMain.on('clip:write', (_e, text) => { if (typeof text === 'string') clipboard.writeText(text); });
ipcMain.handle('app:theme', () => bridge.theme);
ipcMain.handle('app:initial', () => ({ cwd: cwdFromArgs(process.argv) || os.homedir(), ok: USE_PYTHON || USE_SCRIPT, resize: USE_PYTHON }));
ipcMain.on('app:quit', () => { if (win) win.close(); });

// ---------------------------------------------------------------- window

function createWindow() {
  win = new BrowserWindow({
    width: 960,
    height: 600,
    minWidth: 360,
    minHeight: 220,
    title: 'Red Star Terminal',
    backgroundColor: bridge.theme.dark ? '#1C1C1C' : '#FFFFFF',
    icon: path.join(__dirname, 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });
  Menu.setApplicationMenu(null);
  win.loadFile(path.join(__dirname, 'index.html'));
  win.on('closed', () => { win = null; killAll(); });
  bridge.attach(win);
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  bridge = loadBridge({ appId: 'terminal', name: 'Red Star Terminal' });
  bridge.onTheme((theme) => { if (win) win.webContents.send('theme', theme); });

  // Launching Terminal again (e.g. "Open Terminal" on a folder) opens a new tab in the running window.
  app.on('second-instance', (_event, argv) => {
    if (!win) return;
    if (win.isMinimized()) win.restore();
    win.focus();
    bridge.raise();
    win.webContents.send('new-tab', cwdFromArgs(argv));
  });

  app.whenReady().then(createWindow);
  app.on('window-all-closed', () => { killAll(); bridge.close(); app.quit(); });
}
