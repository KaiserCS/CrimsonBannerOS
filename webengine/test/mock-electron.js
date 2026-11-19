'use strict';
// Minimal stand-in for the Electron APIs the Red Star web engine uses, backed by Playwright's Chromium.
// Lets the real webengine/main.js run here: page JS, navigation, request blocking, PDF, downloads and
// clipboard extraction are executed by a real Chromium; only window/paint plumbing is simulated.
const { EventEmitter } = require('events');
// Playwright is loaded only when a test really opens a browser, so the switch-only tests (gpu.test.js)
// run without it installed.
let chromium;
const os = require('os');
const path = require('path');
const fs = require('fs');

process.on('unhandledRejection', (e) => console.error('[mock] unhandled:', String(e).split('\n')[0]));
const log = (...a) => { if (process.env.MOCK_LOG) console.error('[mock]', ...a); };
let browserP = null;
function browser() { return browserP ??= (chromium ??= require('playwright').chromium).launch(); }
const DL_DIR = process.env.MOCK_DOWNLOADS || fs.mkdtempSync(path.join(os.tmpdir(), 'rs-dl-'));
const PROXY = process.env.MOCK_PROXY ? JSON.parse(process.env.MOCK_PROXY) : {};
const calls = [];                            // record of side-effect calls, dumped on demand
global.__mockCalls = calls;
process.on('exit', () => { if (process.env.MOCK_CALLS) fs.writeFileSync(process.env.MOCK_CALLS, JSON.stringify(calls.concat([...sessions.values()].map((x) => ['session', x.name, x.cleared])))); });

const app = new EventEmitter();
app.disableHardwareAcceleration = () => calls.push(['disableHardwareAcceleration']);
app.commandLine = { appendSwitch: (...a) => calls.push(['appendSwitch', ...a]) };
app.getGPUFeatureStatus = () => ({ gpu_compositing: 'disabled_software', rasterization: 'disabled_software', video_decode: 'disabled_software' });
app.getGPUInfo = () => Promise.resolve({ gpuDevice: [] });
const USER_DATA = process.env.MOCK_USERDATA || fs.mkdtempSync(path.join(os.tmpdir(), 'rs-ud-'));
app.getPath = (n) => (n === 'downloads' ? DL_DIR : n === 'userData' ? USER_DATA : os.tmpdir());
app.whenReady = () => browser().then(() => undefined);
app.quit = () => { app.emit('before-quit'); browser().then((b) => b.close()).finally(() => process.exit(0)); };

const nativeTheme = { themeSource: 'system' };
const shell = { openExternal: () => Promise.resolve() };

const sessions = new Map();
function mockImage(w, h, buf) {
  return {
    getSize: () => ({ width: w, height: h }),
    toBitmap: () => buf,
    crop: (r) => mockImage(r.width, r.height, (() => {
      const out = Buffer.alloc(r.width * r.height * 4);
      for (let y = 0; y < r.height; y++) buf.copy(out, y * r.width * 4, ((r.y + y) * w + r.x) * 4, ((r.y + y) * w + r.x + r.width) * 4);
      return out;
    })()),
  };
}

class Session extends EventEmitter {
  constructor(name) {
    super();
    this.name = name;
    this._before = null;
    this.webRequest = { onBeforeRequest: (fn) => { this._before = fn; } };
    this.cleared = [];
    this._ctxP = null;
  }
  setPermissionRequestHandler(fn) { this._perm = fn; }
  fetch(u, o) { return fetch(u, o); }
  clearStorageData(o) { this.cleared.push(['storage', o]); calls.push(['clearStorageData', this.name, o]); return Promise.resolve(); }
  clearCache() { this.cleared.push(['cache']); calls.push(['clearCache', this.name]); return Promise.resolve(); }
  ctx() {
    return this._ctxP ??= browser().then(async (b) => {
      const ctx = await b.newContext({ acceptDownloads: true, ignoreHTTPSErrors: false });
      await ctx.route('**/*', (route, req) => this._route(route, req));
      return ctx;
    });
  }
  _route(route, req) {
    let frame = null;
    try { frame = req.frame(); } catch (_e) { /* pop-up's first request: no frame yet */ }
    const page = frame && frame.page();
    const wc = page && page.__wc;
    const isMain = req.isNavigationRequest() && (!frame || frame === page.mainFrame());
    const details = { url: req.url(), resourceType: isMain ? 'mainFrame' : (req.resourceType() === 'document' ? 'subFrame' : req.resourceType()), webContents: wc };
    const go = (r) => {
      if (r && r.cancel) { log('cancel', req.url()); return route.abort('blockedbyclient'); }
      let host = '';
      try { host = new URL(req.url()).hostname; } catch (_e) { /* data: etc */ }
      if (host === '127.0.0.1' || host === 'localhost' || !/^https?:/.test(req.url())) return route.continue();
      // hosts the tests "own": served by the local test server, so cross-site behaviour is real
      if (PROXY.base && PROXY.hosts.includes(host)) {
        const u = new URL(req.url());
        return route.fetch({ url: PROXY.base + u.pathname + u.search }).then((resp) => route.fulfill({ response: resp })).catch(() => route.abort());
      }
      // everything "on the internet" is faked so tests never need the network
      return route.fulfill({ status: 200, contentType: 'text/plain', body: 'fake ' + req.url() });
    };
    if (this._before) this._before(details, go); else go({});
  }
}
const session = { fromPartition: (n) => { if (!sessions.has(n)) sessions.set(n, new Session(n)); return sessions.get(n); } };
global.__sessions = sessions;

class WebContents extends EventEmitter {
  constructor(win, ses, existing) {
    super();
    this.win = win; this.ses = ses;
    this.userAgent = 'Mozilla/5.0 Chrome/130.0 Electron/33.2.0 Safari/537.36';
    this._url = ''; this._zoom = 1; this._muted = false; this._dev = false; this._hist = []; this._idx = -1;
    this.session = ses;
    this.page = (existing ? Promise.resolve(existing) : ses.ctx().then((c) => c.newPage())).then((p) => {
      // Chromium tells Electron how a new window was asked for: a link with target=_blank is a "foreground-tab",
      // window.open with a size a "new-window". Playwright doesn't say, so the page remembers its last such link.
      p.addInitScript(() => document.addEventListener('click', (e) => {
        const a = e.target && e.target.closest && e.target.closest('a[target=_blank]');
        if (a) window.__rsTabLink = a.href;
      }, true)).catch(() => {});
      p.__wc = this;
      p.on('framenavigated', (f) => {
        if (f !== p.mainFrame()) return;
        const u = f.url();
        if (this._navFrom !== 'hist') { this._hist = this._hist.slice(0, this._idx + 1); this._hist.push(u); this._idx = this._hist.length - 1; }
        this._navFrom = null;
        this._url = u;
        this.emit('did-navigate', {}, u);
      });
      p.on('load', async () => {
        try { const t = await p.title(); this.emit('page-title-updated', {}, t); } catch (_e) { /* closed */ }
        this.emit('did-stop-loading', {});
        this._paint();
      });
      p.on('requestfailed', (r) => {
        if (r.isNavigationRequest() && r.frame() === p.mainFrame()) {
          const txt = (r.failure() && r.failure().errorText) || '';
          const code = { 'net::ERR_BLOCKED_BY_CLIENT': -20, 'net::ERR_CERT_AUTHORITY_INVALID': -202, 'net::ERR_ABORTED': -3 }[txt] || -105;
          this.emit('did-fail-load', {}, code, txt, r.url(), true);
        }
      });
      p.on('download', (d) => this._download(d));
      p.on('domcontentloaded', () => this.emit('dom-ready', {}));
      let lastTitle = null;
      const poll = setInterval(async () => {
        try { const t = await p.title(); if (t !== lastTitle) { lastTitle = t; this.emit('page-title-updated', {}, t); } } catch (_e) { clearInterval(poll); }
      }, 250);
      p.on('close', () => { if (!this._destroyed) win.emit('closed'); });
      p.on('popup', async (pop) => {
        const link = await p.evaluate(() => window.__rsTabLink || null).catch(() => null);
        const disposition = link && link === pop.url() ? 'foreground-tab' : 'new-window';
        const r = this._open ? this._open({ url: pop.url(), disposition }) : { action: 'allow' };
        if (r.action !== 'allow') { pop.close(); return; }
        const child = new BrowserWindow({ ...(r.overrideBrowserWindowOptions || {}), webPreferences: { ...((r.overrideBrowserWindowOptions || {}).webPreferences || {}), session: ses }, __page: pop });
        this.emit('did-create-window', child, { url: pop.url(), disposition });
      });
      if (existing) setTimeout(() => { this._url = p.url(); this._hist = [p.url()]; this._idx = 0; }, 0);
      return p;
    });
    const scripts = new Map(); let sid = 0;
    this.debugger = {
      _att: false,
      isAttached: () => this.debugger._att,
      attach: () => { this.debugger._att = true; },
      sendCommand: (cmd, params) => {
        calls.push(['cdp', cmd]);
        if (cmd === 'Page.addScriptToEvaluateOnNewDocument') { const id = String(++sid); scripts.set(id, params.source); return this.page.then((p) => p.addInitScript(params.source)).then(() => ({ identifier: id })); }
        return Promise.resolve({});
      },
    };
    this.navigationHistory = {
      canGoBack: () => this._idx > 0, canGoForward: () => this._idx < this._hist.length - 1,
      goBack: () => { if (this._idx > 0) { this._idx--; this._navFrom = 'hist'; this.page.then((p) => p.goBack().catch(() => new Promise((r) => setTimeout(r, 400)).then(() => p.goBack()))); } },
      goForward: () => { if (this._idx < this._hist.length - 1) { this._idx++; this._navFrom = 'hist'; this.page.then((p) => p.goForward()); } },
    };
  }
  _paint() {
    const [w, h] = this.win._size;
    const buf = Buffer.alloc(w * h * 4, 0x7f);
    this._painted = `${w}x${h}`;
    this.emit('paint', {}, { x: 0, y: 0, width: w, height: h }, mockImage(w, h, buf));
  }
  // Like a playing video: only rows 10..19 change (value 0x33), and Electron reports just that rectangle.
  _paintBand() {
    const [w, h] = this.win._size;
    const buf = Buffer.alloc(w * h * 4, 0x7f);
    buf.fill(0x33, 10 * w * 4, 20 * w * 4);
    this.emit('paint', {}, { x: 40, y: 10, width: 200, height: 10 }, mockImage(w, h, buf));
  }
  _download(d) {
    const item = new EventEmitter();
    let save = null, done = false;
    item.getFilename = () => d.suggestedFilename();
    item.setSavePath = (p) => { save = p; };
    item.getURL = () => d.url();
    item.getTotalBytes = () => 0;
    item.getReceivedBytes = () => (done && save && fs.existsSync(save) ? fs.statSync(save).size : 0);
    item.isPaused = () => false;
    item.cancel = () => { d.cancel(); item.emit('done', {}, 'cancelled'); };
    this.ses.emit('will-download', {}, item, this);
    setTimeout(async () => {
      try { await d.saveAs(save); done = true; item.emit('updated'); item.emit('done', {}, 'completed'); }
      catch (_e) { item.emit('done', {}, 'interrupted'); }
    }, 50);
  }
  setFrameRate() {}
  isDestroyed() { return !!this._destroyed; }
  insertCSS(css) {
    calls.push(['insertCSS', css.length]);
    return this.page.then((p) => p.evaluate((c) => { const st = document.createElement('style'); st.textContent = c; (document.head || document.documentElement).appendChild(st); }, css)).then(() => 'key').catch(() => 'key');
  }
  setUserAgent(ua) { this.userAgent = ua; calls.push(['setUserAgent', ua]); }
  setZoomFactor(z) { this._zoom = z; calls.push(['zoom', z]); }
  getZoomFactor() { return this._zoom; }
  getURL() { return this._url; }
  loadURL(u) {
    this.emit('did-start-loading', {});
    this.emit('did-start-navigation', {}, u, false, true);
    return this.page.then((p) => p.goto(u).then(() => undefined));
  }
  loadFile(f) { return this.loadURL('file://' + f); }
  reload() { this.emit('did-start-loading', {}); this.emit('did-start-navigation', {}, this._url, false, true); this.page.then((p) => p.reload()); }
  reloadIgnoringCache() { this.reload(); }
  stop() {}
  canGoBack() { return this.navigationHistory.canGoBack(); }
  canGoForward() { return this.navigationHistory.canGoForward(); }
  executeJavaScript(js, gesture) { calls.push(['exec', js.length, !!gesture]); return this.page.then((p) => p.evaluate(js)); }
  insertText(t) { calls.push(['insertText', t]); return this.page.then((p) => p.keyboard.insertText(t)); }
  sendInputEvent(e) { calls.push(['input', e.type]); }
  focus() { calls.push(['focus']); }
  invalidate() { if (this._painted === `${this.win._size[0]}x${this.win._size[1]}`) this._paintBand(); else this._paint(); }
  copy() { calls.push(['copy']); } cut() { calls.push(['cut']); } paste() {} selectAll() { this.page.then((p) => p.evaluate('document.execCommand("selectAll")')); }
  undo() {} redo() {} pasteAndMatchStyle() {} delete() {}
  findInPage(text) {
    this.page.then((p) => p.evaluate((t) => (document.body.innerText.match(new RegExp(t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'gi')) || []).length, text))
      .then((n) => this.emit('found-in-page', {}, { activeMatchOrdinal: n ? 1 : 0, matches: n }));
  }
  stopFindInPage() {}
  setAudioMuted(m) { this._muted = m; calls.push(['muted', m]); }
  isAudioMuted() { return this._muted; }
  isDevToolsOpened() { return this._dev; }
  openDevTools(o) { this._dev = true; calls.push(['openDevTools', o]); }
  closeDevTools() { this._dev = false; calls.push(['closeDevTools']); }
  printToPDF(o) { calls.push(['printToPDF', o]); return this.page.then((p) => p.pdf({ format: (o && o.pageSize) || 'A4', printBackground: !!(o && o.printBackground) })); }
  setWindowOpenHandler(fn) { this._open = fn; }
}

class BrowserWindow extends EventEmitter {
  constructor(o) {
    super();
    this._size = [o.width || 800, o.height || 600];
    const ses = o.webPreferences.session;
    this.webContents = new WebContents(this, ses, o.__page);
    calls.push(['BrowserWindow', { offscreen: o.webPreferences.offscreen, sandbox: o.webPreferences.sandbox, ses: ses.name }]);
  }
  setContentSize(w, h) { this._size = [w, h]; }
  loadURL(u) { return this.webContents.loadURL(u); }
  isDestroyed() { return !!this.webContents._destroyed; }
  focus() {}
  setMenuBarVisibility() {}
  close() { this.destroy(); this.emit('closed'); }
  destroy() {
    // like Chromium, a frame that was already on its way can still arrive after the window is gone
    const wc = this.webContents;
    if (!wc._destroyed) setImmediate(() => wc._paintBand());
    wc._destroyed = true;
    wc.page.then((p) => p.close()).catch(() => {});
  }
}

module.exports = { app, BrowserWindow, session, shell, nativeTheme, net: { fetch: (u, o) => fetch(u, o) } };
