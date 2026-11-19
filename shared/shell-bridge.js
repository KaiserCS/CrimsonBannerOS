'use strict';
/**
 * Red Star shell bridge (Electron side).
 *
 * The Red Star OS shell listens on a Unix socket (path in $REDSTAR_SHELL_SOCKET) and speaks
 * newline-delimited JSON. An Electron app that creates a bridge gets:
 *   - a taskbar button (title, focused / minimised state, click to focus / minimise,
 *     right-click Snap / Close) exactly like the native Red Star apps,
 *   - optional icons in the shell's app tray (with a menu),
 *   - toast notifications in the shell's notification centre,
 *   - the shell's theme (dark/light + accent colour) so the app can match it.
 * Without the environment variable (e.g. `npm start` during development) every method is a no-op,
 * so the apps keep working stand-alone.
 *
 * Messages, app -> shell:
 *   {t:'hello', app, name, pid}
 *   {t:'window', title, focused, minimized, maximized, visible}
 *   {t:'tray', id, tooltip, glyph, icon (PNG data URL), menu:[{id,label}|{sep:true}]}   (upsert)
 *   {t:'tray', id, remove:true}
 *   {t:'notify', title, body}
 *   {t:'bye'}
 * Messages, shell -> app:
 *   {t:'welcome', dark, accent}        {t:'theme', dark, accent}
 *   {t:'cmd', cmd:'focus'|'minimize'|'restore'|'close'|'quit'}
 *   {t:'bounds', x, y, w, h}           (physical pixels; used for Snap Left / Right / Fill)
 *   {t:'traymenu', tray, item}         {t:'trayclick', tray}
 */
const fs = require('fs');
const net = require('net');

const DEFAULT_THEME = { dark: true, accent: '#0078D4' };

function electronApi() {
  try { return require('electron'); } catch (_e) { return {}; }
}

/** Reads --redstar-theme=dark|light and --redstar-accent=#rrggbb passed by the shell. */
function parseTheme(argv) {
  const theme = { ...DEFAULT_THEME };
  for (const a of argv || []) {
    let m = /^--redstar-theme=(dark|light)$/.exec(a);
    if (m) theme.dark = m[1] === 'dark';
    m = /^--redstar-accent=(#[0-9a-fA-F]{6})$/.exec(a);
    if (m) theme.accent = m[1];
  }
  return theme;
}

function createBridge(options) {
  const opts = options || {};
  const socketPath = process.env.REDSTAR_SHELL_SOCKET || '';
  const state = {
    sock: null,
    connected: false,
    buffer: '',
    win: null,
    reportTimer: null,
    reconnectTimer: null,
    titleHint: null,
    closed: false,
    tray: new Map(),
    theme: parseTheme(process.argv),
    themeHandlers: [],
  };

  // ------------------------------------------------------------ transport

  function send(msg) {
    if (!state.connected || !state.sock) return;
    try { state.sock.write(JSON.stringify(msg) + '\n'); } catch (_e) { /* socket is going away */ }
  }

  function connect() {
    if (!socketPath || state.closed) return;
    const sock = net.connect(socketPath);
    state.sock = sock;
    sock.setEncoding('utf8');

    sock.on('connect', () => {
      state.connected = true;
      send({ t: 'hello', app: opts.appId, name: opts.name, pid: process.pid });
      reportWindow();                                       // (re)announce everything after a reconnect
      for (const entry of state.tray.values()) send(entry);
    });
    sock.on('data', (chunk) => {
      state.buffer += chunk;
      let nl;
      while ((nl = state.buffer.indexOf('\n')) >= 0) {
        const line = state.buffer.slice(0, nl).trim();
        state.buffer = state.buffer.slice(nl + 1);
        if (!line) continue;
        try { handle(JSON.parse(line)); } catch (_e) { /* ignore malformed line */ }
      }
    });
    sock.on('error', () => { /* handled by 'close' */ });
    sock.on('close', () => {
      state.connected = false;
      state.sock = null;
      state.buffer = '';
      // The shell may have been restarted; keep trying while its socket file still exists.
      if (!state.closed && !state.reconnectTimer) {
        state.reconnectTimer = setTimeout(() => {
          state.reconnectTimer = null;
          if (fs.existsSync(socketPath)) connect();
        }, 2000);
        if (state.reconnectTimer.unref) state.reconnectTimer.unref();
      }
    });
  }

  // ------------------------------------------------------------ window handling

  function alive() {
    return state.win && !state.win.isDestroyed();
  }

  function reportWindow() {
    if (!alive()) return;
    const w = state.win;
    send({
      t: 'window',
      title: state.titleHint || w.getTitle() || opts.name,
      focused: w.isFocused(),
      minimized: w.isMinimized(),
      maximized: w.isMaximized(),
      visible: w.isVisible(),
    });
  }

  function scheduleReport() {
    if (state.reportTimer) return;
    state.reportTimer = setTimeout(() => {
      state.reportTimer = null;
      reportWindow();
    }, 40);
  }

  /** Brings the window to the front (the always-on-top flip also lifts it above a full-screen shell). */
  function raise() {
    if (!alive()) return;
    const w = state.win;
    if (w.isMinimized()) w.restore();
    w.show();
    w.setAlwaysOnTop(true);
    w.focus();
    if (w.moveTop) w.moveTop();
    setTimeout(() => { if (alive()) w.setAlwaysOnTop(false); }, 120);
  }

  function setBounds(b) {
    if (!alive()) return;
    const w = state.win;
    const { screen } = electronApi();
    const f = (screen && screen.getPrimaryDisplay().scaleFactor) || 1;   // shell sends pixels, Electron wants DIPs
    if (w.isMinimized()) w.restore();
    if (w.isMaximized()) w.unmaximize();
    w.setBounds({
      x: Math.round(b.x / f), y: Math.round(b.y / f),
      width: Math.max(200, Math.round(b.w / f)), height: Math.max(120, Math.round(b.h / f)),
    });
    raise();
  }

  function runCommand(cmd) {
    const { app } = electronApi();
    switch (cmd) {
      case 'focus': raise(); break;
      case 'minimize': if (alive()) state.win.minimize(); break;
      case 'restore': if (alive()) { state.win.restore(); raise(); } break;
      case 'close': if (alive()) state.win.close(); break;
      case 'quit': state.closed = true; if (app) app.quit(); break;
      default: break;
    }
  }

  function handle(msg) {
    switch (msg.t) {
      case 'welcome':
      case 'theme':
        state.theme = { dark: !!msg.dark, accent: msg.accent || state.theme.accent };
        for (const cb of state.themeHandlers) cb(state.theme);
        break;
      case 'cmd': runCommand(msg.cmd); break;
      case 'bounds': setBounds(msg); break;
      case 'traymenu': if (opts.onTrayMenu) opts.onTrayMenu(msg.tray, msg.item); break;
      case 'trayclick': if (opts.onTrayClick) opts.onTrayClick(msg.tray); else raise(); break;
      default: break;
    }
  }

  // ------------------------------------------------------------ public API

  const api = {
    /** True when the shell is reachable (useful for tests and diagnostics). */
    get connected() { return state.connected; },
    get theme() { return state.theme; },

    onTheme(cb) { state.themeHandlers.push(cb); },

    /** Call once the BrowserWindow exists. */
    attach(win) {
      state.win = win;
      for (const ev of ['focus', 'blur', 'minimize', 'restore', 'maximize', 'unmaximize', 'show', 'hide']) {
        win.on(ev, scheduleReport);
      }
      win.on('page-title-updated', (_e, title) => { state.titleHint = title; scheduleReport(); });
      win.on('closed', () => { send({ t: 'bye' }); state.win = null; });
      scheduleReport();
    },

    raise,

    /**
     * Shows (or updates) an icon in the shell's app tray.
     * { id?, tooltip, glyph?, icon? (nativeImage), menu?: [{id,label}|{sep:true}] }
     */
    setTray(t) {
      const id = t.id || 'main';
      let icon = null;
      if (t.icon && typeof t.icon.resize === 'function') {
        try { icon = 'data:image/png;base64,' + t.icon.resize({ width: 32, height: 32 }).toPNG().toString('base64'); }
        catch (_e) { icon = null; }
      }
      const entry = { t: 'tray', id, tooltip: t.tooltip || opts.name, glyph: t.glyph || null, icon, menu: t.menu || [] };
      state.tray.set(id, entry);
      send(entry);
    },

    removeTray(id) {
      const key = id || 'main';
      if (!state.tray.delete(key)) return;
      send({ t: 'tray', id: key, remove: true });
    },

    notify(title, body) { send({ t: 'notify', title, body }); },

    /** Test helper / shutdown: stop reconnecting and close the socket. */
    close() {
      state.closed = true;
      if (state.reconnectTimer) clearTimeout(state.reconnectTimer);
      if (state.sock) state.sock.destroy();
    },
  };

  connect();
  return api;
}

module.exports = { createBridge, parseTheme, DEFAULT_THEME };
