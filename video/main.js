'use strict';
const { app, BrowserWindow, Menu, dialog, ipcMain, shell } = require('electron');
const path = require('path');
const fs = require('fs');
const { pathToFileURL } = require('url');

const MEDIA_EXT = [
  'mp4', 'm4v', 'mkv', 'webm', 'ogv', 'ogg', 'mov', 'avi', 'mpg', 'mpeg', 'wmv', 'flv', '3gp',
  'mp3', 'flac', 'wav', 'm4a', 'opus', 'aac',
];

let win = null;
let bridge = null;

// Shell integration (taskbar button, tray icon, theme). Packaged: ../redstar-shared, source tree: ../shared.
function loadBridge(options) {
  for (const dir of [path.join(__dirname, '..', 'redstar-shared'), path.join(__dirname, '..', 'shared')]) {
    try { return require(path.join(dir, 'shell-bridge.js')).createBridge(options); } catch (_e) { /* next */ }
  }
  return { attach() {}, raise() {}, setTray() {}, removeTray() {}, notify() {}, onTheme() {}, close() {},
           theme: { dark: true, accent: '#0078D4' } };
}

function isFile(p) {
  try { return fs.statSync(p).isFile(); } catch (_e) { return false; }
}

function subtitleFor(p) {
  const base = p.slice(0, p.length - path.extname(p).length);
  for (const ext of ['.vtt', '.srt']) {
    if (isFile(base + ext)) return base + ext;
  }
  return null;
}

function describe(paths) {
  return paths.filter(isFile).map((p) => ({
    path: p,
    name: path.basename(p),
    url: pathToFileURL(p).href,
    subtitle: subtitleFor(p),
  }));
}

function pathsFromArgs(argv) {
  return argv.slice(1)
    .filter((a) => !a.startsWith('-'))
    .map((a) => path.resolve(a))
    .filter(isFile);
}

function readSubtitle(p) {
  try {
    if (typeof p !== 'string' || !/\.(srt|vtt)$/i.test(p)) return null;
    return { name: path.basename(p), text: fs.readFileSync(p, 'utf8') };
  } catch (_e) {
    return null;
  }
}

const initialFiles = pathsFromArgs(process.argv);

function createWindow() {
  win = new BrowserWindow({
    width: 1000,
    height: 640,
    minWidth: 480,
    minHeight: 320,
    title: 'Red Star Video',
    backgroundColor: '#000000',
    icon: path.join(__dirname, 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });
  Menu.setApplicationMenu(null);
  win.loadFile(path.join(__dirname, 'index.html'));
  win.on('closed', () => { win = null; });
  bridge.attach(win);
}

// ---- app tray icon: shows what is playing and offers play / pause / previous / next -------------
function updateTray(s) {
  if (!s || !s.title) { bridge.removeTray('media'); return; }
  bridge.setTray({
    id: 'media',
    glyph: s.playing ? '\u25B6' : '\u23F8',
    tooltip: (s.playing ? 'Playing: ' : 'Paused: ') + s.title,
    menu: [
      { id: 'playpause', label: s.playing ? 'Pause' : 'Play' },
      { id: 'prev', label: 'Previous' },
      { id: 'next', label: 'Next' },
      { sep: true },
      { id: 'show', label: 'Show Red Star Video' },
      { id: 'close', label: 'Close' },
    ],
  });
}

function onTrayMenu(_tray, item) {
  if (item === 'show') bridge.raise();
  else if (item === 'close') { if (win) win.close(); }
  else if (win) win.webContents.send('media-command', item);
}

ipcMain.on('media:state', (_e, s) => updateTray(s));
ipcMain.handle('app:theme', () => bridge.theme);

ipcMain.handle('app:initial', () => describe(initialFiles));
ipcMain.handle('files:describe', (_e, paths) =>
  describe(Array.isArray(paths) ? paths.filter((p) => typeof p === 'string') : []));

ipcMain.handle('dialog:open', async () => {
  const r = await dialog.showOpenDialog(win, {
    title: 'Open video or audio',
    properties: ['openFile', 'multiSelections'],
    filters: [
      { name: 'Video and audio', extensions: MEDIA_EXT },
      { name: 'All files', extensions: ['*'] },
    ],
  });
  return r.canceled ? [] : describe(r.filePaths);
});

ipcMain.handle('dialog:subtitle', async () => {
  const r = await dialog.showOpenDialog(win, {
    title: 'Load subtitles',
    properties: ['openFile'],
    filters: [{ name: 'Subtitles', extensions: ['srt', 'vtt'] }],
  });
  if (r.canceled || !r.filePaths[0]) return null;
  return readSubtitle(r.filePaths[0]);
});

ipcMain.handle('subtitle:read', (_e, p) => readSubtitle(p));

ipcMain.handle('shell:open', async (_e, p) => {
  if (typeof p === 'string' && isFile(p)) await shell.openPath(p);
});

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  bridge = loadBridge({ appId: 'video', name: 'Red Star Video', onTrayMenu });
  bridge.onTheme((theme) => { if (win) win.webContents.send('theme', theme); });

  // Opening another file while the player is running adds it to the playlist and plays it.
  app.on('second-instance', (_event, argv) => {
    if (!win) return;
    if (win.isMinimized()) win.restore();
    win.focus();
    bridge.raise();
    const items = describe(pathsFromArgs(argv));
    if (items.length) win.webContents.send('add-files', items);
  });
  app.whenReady().then(createWindow);
  app.on('window-all-closed', () => { bridge.close(); app.quit(); });
}
