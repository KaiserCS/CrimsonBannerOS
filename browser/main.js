'use strict';
const { app, BrowserWindow, Menu, ipcMain } = require('electron');
const path = require('path');

const HOME_URL = 'https://duckduckgo.com';
let win = null;

// Shell integration (taskbar button, theme). Packaged: ../redstar-shared, source tree: ../shared.
function loadBridge() {
  for (const dir of [path.join(__dirname, '..', 'redstar-shared'), path.join(__dirname, '..', 'shared')]) {
    try {
      return require(path.join(dir, 'shell-bridge.js')).createBridge({ appId: 'online', name: 'Red Star Online' });
    } catch (_e) { /* try the next location */ }
  }
  return { attach() {}, raise() {}, setTray() {}, removeTray() {}, notify() {}, onTheme() {}, close() {},
           theme: { dark: true, accent: '#0078D4' } };
}

function urlFromArgs(argv) {
  return argv.find((a) => /^https?:\/\//i.test(a)) || null;
}

let bridge = null;

function createWindow(startUrl) {
  win = new BrowserWindow({
    width: 1200,
    height: 800,
    title: 'Red Star Online',
    backgroundColor: bridge.theme.dark ? '#202020' : '#F3F3F3',
    icon: path.join(__dirname, 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      webviewTag: true,
    },
  });
  Menu.setApplicationMenu(null);
  win.loadFile(path.join(__dirname, 'index.html'), {
    query: { url: startUrl || HOME_URL, home: HOME_URL },
  });
  win.on('closed', () => { win = null; });
  bridge.attach(win);
}

ipcMain.handle('app:theme', () => bridge.theme);

// Only one browser at a time: a second launch just navigates the running one.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  bridge = loadBridge();
  bridge.onTheme((theme) => { if (win) win.webContents.send('theme', theme); });

  app.on('second-instance', (_event, argv) => {
    if (!win) return;
    if (win.isMinimized()) win.restore();
    win.focus();
    bridge.raise();                      // also lifts it above the full-screen shell when integrated
    const url = urlFromArgs(argv);
    if (url) win.webContents.send('open-url', url);
  });

  // Links that want a new window (target=_blank, window.open) load in the same tab instead.
  app.on('web-contents-created', (_event, contents) => {
    if (contents.getType() === 'webview') {
      contents.setWindowOpenHandler(({ url }) => {
        if (win) win.webContents.send('open-url', url);
        return { action: 'deny' };
      });
    }
  });

  app.whenReady().then(() => createWindow(urlFromArgs(process.argv)));
  app.on('window-all-closed', () => { bridge.close(); app.quit(); });
}
