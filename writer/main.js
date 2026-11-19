'use strict';
const { app, BrowserWindow, ipcMain, dialog, Menu, shell } = require('electron');
const path = require('path');
const fs = require('fs');

function loadBridge(options) {
  for (const rel of ['../redstar-shared', '../shared']) {
    try { return require(path.join(__dirname, rel, 'shell-bridge.js')).createBridge(options); } catch { /* try next */ }
  }
  return { attach() {}, raise() {}, onTheme() {}, close() {}, theme: { mode: 'dark', accent: '#c4122f' } };
}

function arg(name) {
  const hit = process.argv.find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.slice(name.length + 3) : null;
}
function fileArgs(argv) {
  const f = argv.find((a) => a.startsWith('--file='));
  return f ? [f.slice(7)] : [];
}

let win = null;
const bridge = loadBridge({ appId: 'writer', name: 'Red Star Writer' });
const recoveryFile = () => path.join(app.getPath('userData'), 'recovery.json');
const recentFile = () => path.join(app.getPath('userData'), 'recent.json');

if (!app.requestSingleInstanceLock()) { app.quit(); }
else {
  app.on('second-instance', (_e, argv) => {
    if (win) { if (win.isMinimized()) win.restore(); win.focus(); bridge.raise && bridge.raise(); }
    const files = fileArgs(argv);
    if (win && files.length) win.webContents.send('open-files', files);
  });

  app.whenReady().then(() => {
    Menu.setApplicationMenu(null);
    win = new BrowserWindow({
      width: 1180, height: 820, minWidth: 720, minHeight: 480,
      title: 'Red Star Writer', backgroundColor: '#1b1b1f',
      icon: path.join(__dirname, 'icon.png'), show: false,
      webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, nodeIntegration: false, spellcheck: true },
    });
    bridge.attach(win);
    win.once('ready-to-show', () => win.show());
    win.loadFile(path.join(__dirname, 'index.html'), {
      query: { theme: arg('redstar-theme') || 'dark', accent: arg('redstar-accent') || '', files: JSON.stringify(fileArgs(process.argv)) },
    });
    win.webContents.setWindowOpenHandler(({ url }) => { if (/^https?:/.test(url)) shell.openExternal(url); return { action: 'deny' }; });

    let confirmed = false;
    win.on('close', (e) => {
      if (confirmed) return;
      e.preventDefault();
      win.webContents.send('request-close');
    });
    ipcMain.on('close-confirmed', () => { confirmed = true; win.close(); });
    bridge.onTheme((t) => { if (win && !win.isDestroyed()) win.webContents.send('theme', t); });
  });

  app.on('window-all-closed', () => { try { bridge.close(); } catch { /* ignore */ } app.quit(); });
}

ipcMain.handle('app:theme', () => bridge.theme);

const FILTERS = [
  { name: 'Word Document', extensions: ['docx'] },
  { name: 'Web Page', extensions: ['html', 'htm'] },
  { name: 'Plain Text', extensions: ['txt'] },
  { name: 'Markdown', extensions: ['md'] },
];
const OPENABLE = [{ name: 'Documents', extensions: ['docx', 'html', 'htm', 'txt', 'md', 'rtf', 'log', 'csv'] }, { name: 'All files', extensions: ['*'] }];

function readAny(p) {
  const ext = path.extname(p).toLowerCase().slice(1);
  const buf = fs.readFileSync(p);
  if (ext === 'docx') return { path: p, name: path.basename(p), ext, base64: buf.toString('base64') };
  return { path: p, name: path.basename(p), ext, text: buf.toString('utf8') };
}
function pushRecent(p) {
  try {
    let list = [];
    try { list = JSON.parse(fs.readFileSync(recentFile(), 'utf8')); } catch { /* none */ }
    list = [p, ...list.filter((x) => x !== p)].slice(0, 15);
    fs.writeFileSync(recentFile(), JSON.stringify(list));
  } catch { /* ignore */ }
}

ipcMain.handle('file:open-dialog', async () => {
  const r = await dialog.showOpenDialog(win, { properties: ['openFile', 'multiSelections'], filters: OPENABLE });
  if (r.canceled) return [];
  return r.filePaths.map((p) => { try { const d = readAny(p); pushRecent(p); return d; } catch (e) { return { path: p, error: String(e.message || e) }; } });
});
ipcMain.handle('file:read', (_e, p) => { try { const d = readAny(p); pushRecent(p); return d; } catch (e) { return { path: p, error: String(e.message || e) }; } });
ipcMain.handle('file:save-dialog', async (_e, suggested) => {
  const r = await dialog.showSaveDialog(win, { defaultPath: suggested || 'Untitled.docx', filters: FILTERS });
  return r.canceled ? null : r.filePath;
});
ipcMain.handle('file:write', (_e, p, payload) => {
  try {
    if (payload.base64 !== undefined) fs.writeFileSync(p, Buffer.from(payload.base64, 'base64'));
    else fs.writeFileSync(p, payload.text, 'utf8');
    pushRecent(p);
    return { ok: true };
  } catch (e) { return { ok: false, error: String(e.message || e) }; }
});
ipcMain.handle('file:recent', () => { try { return JSON.parse(fs.readFileSync(recentFile(), 'utf8')).filter((p) => fs.existsSync(p)); } catch { return []; } });

ipcMain.handle('pdf:export', async (_e, opts) => {
  const r = await dialog.showSaveDialog(win, { defaultPath: (opts && opts.name) || 'Document.pdf', filters: [{ name: 'PDF', extensions: ['pdf'] }] });
  if (r.canceled) return { ok: false, canceled: true };
  try {
    const data = await win.webContents.printToPDF({
      pageSize: (opts && opts.page) || 'A4', printBackground: true, preferCSSPageSize: true,
      margins: { marginType: 'none' },
    });
    fs.writeFileSync(r.filePath, data);
    return { ok: true, path: r.filePath };
  } catch (e) { return { ok: false, error: String(e.message || e) }; }
});
ipcMain.handle('print', () => new Promise((res) => win.webContents.print({ printBackground: true }, (ok, why) => res({ ok, why }))));

ipcMain.handle('recovery:save', (_e, data) => { try { fs.writeFileSync(recoveryFile(), JSON.stringify(data)); return true; } catch { return false; } });
ipcMain.handle('recovery:load', () => { try { return JSON.parse(fs.readFileSync(recoveryFile(), 'utf8')); } catch { return null; } });
ipcMain.handle('recovery:clear', () => { try { fs.unlinkSync(recoveryFile()); } catch { /* none */ } return true; });

ipcMain.handle('confirm', async (_e, o) => {
  const r = await dialog.showMessageBox(win, { type: 'question', buttons: o.buttons, defaultId: 0, cancelId: o.buttons.length - 1, message: o.message, detail: o.detail || '', noLink: true });
  return r.response;
});
ipcMain.handle('spell:suggest', () => []);
