'use strict';
// Red Star Web Engine: renders web pages offscreen and streams them to the native Red Star Online window.
//  - C# opens a unix socket and starts this process with REDSTAR_WEB_SOCKET set.
//  - Commands arrive as JSON lines; events leave as JSON lines.
//  - Every tab is a hidden offscreen BrowserWindow. Frames are written (BGRA) to a file in /dev/shm and
//    announced with a small "frame" message; the C# side reads the file and draws it in an Avalonia control.
const electron = require('electron');
const { app, BrowserWindow, session, shell, nativeTheme } = electron;
const { FilterEngine } = require('./adblock');
const net = require('net');
const fs = require('fs');
const os = require('os');
const path = require('path');

// GPU or CPU. Red Star sets REDSTAR_WEB_GPU=1 when the display gives apps GPU access (Settings → Diagnostics).
// On the GPU, pages are rasterised and composited there and video is decoded in hardware (VA-API: Intel, AMD,
// and NVIDIA through nvidia-vaapi-driver); frames still come back as bitmaps for the offscreen tabs.
// If the GPU process keeps crashing, the next start uses the CPU (the counter resets after a day).
const gpuCrashFile = path.join(app.getPath('userData'), 'gpu-crashes.json');
function gpuCrashes() {
  try { const j = JSON.parse(fs.readFileSync(gpuCrashFile, 'utf8')); return Date.now() - j.at < 86400000 ? j.n : 0; } catch (_e) { return 0; }
}
const wantGpu = process.env.REDSTAR_WEB_GPU === '1';
const useGpu = wantGpu && gpuCrashes() < 3;
if (useGpu) {
  app.commandLine.appendSwitch('ignore-gpu-blocklist');
  app.commandLine.appendSwitch('enable-gpu-rasterization');
  app.commandLine.appendSwitch('enable-zero-copy');
  app.commandLine.appendSwitch('enable-features',
    'VaapiVideoDecoder,VaapiVideoDecodeLinuxGL,AcceleratedVideoDecodeLinuxGL,AcceleratedVideoDecodeLinuxZeroCopyGL,VaapiIgnoreDriverChecks,CanvasOopRasterization');
} else {
  app.disableHardwareAcceleration();        // CPU rendering: always works
}
app.commandLine.appendSwitch('disable-features', 'Translate,MediaRouter,UseChromeOSDirectVideoDecoder');
app.on('child-process-gone', (_e, details) => {
  log(`${details.type} process gone: ${details.reason} (exit ${details.exitCode})`);
  if (!details || details.type !== 'GPU' || details.reason === 'clean-exit') return;
  try { fs.writeFileSync(gpuCrashFile, JSON.stringify({ n: gpuCrashes() + 1, at: Date.now() })); } catch (_e) { /* ignore */ }
  sendGpuStatus();
});

// What actually runs on the GPU, in words, for Settings → Diagnostics.
function sendGpuStatus() {
  try {
    const st = app.getGPUFeatureStatus() || {};
    const on = (k) => typeof st[k] === 'string' && st[k].startsWith('enabled');
    let summary;
    if (!useGpu) summary = wantGpu ? 'CPU (the GPU crashed repeatedly; trying again tomorrow)' : 'CPU (GPU not available or switched off)';
    else {
      const parts = [];
      parts.push(on('gpu_compositing') ? 'pages: GPU' : 'pages: CPU');
      parts.push(on('rasterization') ? 'drawing: GPU' : 'drawing: CPU');
      parts.push(on('video_decode') ? 'video decoding: GPU (VA-API)' : 'video decoding: CPU');
      summary = parts.join(', ');
    }
    app.getGPUInfo('basic').then((info) => {
      const dev = info && info.gpuDevice && info.gpuDevice.find((g) => g.active) || (info && info.gpuDevice && info.gpuDevice[0]);
      const name = dev ? (dev.driverVendor || dev.vendorString || '') + (dev.deviceString ? ' ' + dev.deviceString : '') : '';
      send({ t: 'gpu', summary: summary + (useGpu && name.trim() ? ` — ${name.trim()}` : ''), status: st });
    }).catch(() => send({ t: 'gpu', summary, status: st }));
  } catch (_e) { /* ignore */ }
}
if (process.env.REDSTAR_REDUCE_MOTION === '1') app.commandLine.appendSwitch('force-prefers-reduced-motion');

const sockPath = process.env.REDSTAR_WEB_SOCKET;

// Errors go to Red Star's browser log instead of Electron's "A JavaScript error occurred" dialog, which leaves the
// engine stopped (every page white) until Red Star restarts it.
function log(msg) {
  try { process.stderr.write('webengine: ' + msg + '\n'); } catch (_e) { /* ignore */ }
  // Red Star passes its browser.log (REDSTAR_WEB_LOG): the engine's own lines go there too, marked "engine:"
  const f = process.env.REDSTAR_WEB_LOG;
  if (!f) return;
  const d = new Date(), p2 = (n) => String(n).padStart(2, '0');
  const stamp = `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())} ${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`;
  try { fs.appendFileSync(f, `${stamp} engine: ${msg}\n`); } catch (_e) { /* ignore */ }
}
log(`started: Electron ${process.versions.electron}, Chromium ${process.versions.chrome}, platform ${process.env.REDSTAR_WEB_PLATFORM || '?'}, ` +
  `WAYLAND_DISPLAY=${process.env.WAYLAND_DISPLAY || '-'} DISPLAY=${process.env.DISPLAY || '-'}, GPU ${process.env.REDSTAR_WEB_GPU === '1' ? 'wanted' : 'off'}`);
process.on('uncaughtException', (e) => log('uncaught: ' + (e && e.stack || e)));
process.on('unhandledRejection', (e) => log('unhandled: ' + (e && e.stack || e)));
const tabs = new Map();                     // id -> { win, wc, fd, file, w, h, pending, dirty, priv }
let sock = null;
let buffer = '';

function send(msg) {
  if (!sock || sock.destroyed) return;
  try { sock.write(JSON.stringify(msg) + '\n'); } catch (_e) { /* socket closing */ }
}

function shmDir() {
  try { fs.accessSync('/dev/shm', fs.constants.W_OK); return '/dev/shm'; } catch (_e) { return os.tmpdir(); }
}

function cleanUA(wc) {
  return wc.userAgent.replace(/\sElectron\/\S+/i, '').replace(/\sredstar-webengine\/\S+/i, '');
}

// Private windows share one in-memory session per window group; it is wiped when the last private tab closes.
function sessionFor(priv, group) {
  return priv ? session.fromPartition('redstar-private-' + (group || 0)) : session.fromPartition('persist:redstar-online');
}

// ── settings pushed from the shell ──
const cfg = { blockTrackers: true, allowSites: [], blockedSites: [] };
const allowedCerts = new Set();
function hostOf(u) { try { return new URL(u).hostname.toLowerCase(); } catch (_e) { return ''; } }
function hostMatch(host, list) { return list.some((d) => host === d || host.endsWith('.' + d)); }

// Human-verification checks (Cloudflare, hCaptcha, reCAPTCHA, GeeTest, Arkose, DataDome, HUMAN). They are never
// blocked by the ad blocker, and when one appears the shell offers to open the page in a real window: these
// checks run in frames from another site, which take input and redraw less reliably in an offscreen page.
const CHALLENGE_HOSTS = ['challenges.cloudflare.com', 'hcaptcha.com', 'recaptcha.net', 'geetest.com', 'geevisit.com',
  'arkoselabs.com', 'funcaptcha.com', 'captcha-delivery.com', 'perimeterx.net', 'px-cdn.net', 'px-cloud.net'];
function isChallenge(url) {
  return hostMatch(hostOf(url), CHALLENGE_HOSTS) || /^https:\/\/(www\.)?(google\.com|gstatic\.com)\/recaptcha\//i.test(url);
}

// ── ad & tracker blocking (EasyList syntax, see adblock.js) ──
const FILTER_LISTS = process.env.REDSTAR_FILTER_LISTS ? JSON.parse(process.env.REDSTAR_FILTER_LISTS) : [
  { id: 'easylist', name: 'EasyList (ads)', url: 'https://easylist.to/easylist/easylist.txt' },
  { id: 'easyprivacy', name: 'EasyPrivacy (trackers)', url: 'https://easylist.to/easylist/easyprivacy.txt' },
  { id: 'pgl', name: "Peter Lowe's ad servers", url: 'https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext' },
];
const FILTER_MAX_AGE = 4 * 24 * 3600 * 1000;
let filters = new FilterEngine();
const filterState = { state: 'starting', lists: [], updating: false };
function filterDir() { const d = path.join(app.getPath('userData'), 'filters'); fs.mkdirSync(d, { recursive: true }); return d; }

function sendFilterStatus() {
  const st = filters.stats();
  send({ t: 'filters', state: filterState.state, network: st.network, cosmetic: st.cosmetic, lists: filterState.lists });
}

/** Rebuilds the engine from the built-in list plus every cached download. */
function loadFilters() {
  const fe = new FilterEngine();
  try { fe.add(fs.readFileSync(path.join(__dirname, 'filters-builtin.txt'), 'utf8')); } catch (_e) { /* packaged without it */ }
  filterState.lists = FILTER_LISTS.map((l) => {
    const f = path.join(filterDir(), l.id + '.txt');
    const info = { id: l.id, name: l.name, rules: 0, updated: 0, error: (filterState.lists.find((x) => x.id === l.id) || {}).error || '' };
    try {
      const st = fs.statSync(f);
      info.rules = fe.add(fs.readFileSync(f, 'utf8'));
      info.updated = st.mtimeMs;
    } catch (_e) { /* not downloaded yet */ }
    return info;
  });
  filters = fe;
  filterState.state = filterState.updating ? 'updating' : 'ready';
  sendFilterStatus();
}

async function fetchText(url) {
  const f = electron.net && electron.net.fetch ? electron.net.fetch.bind(electron.net) : fetch;
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), 60000);
  try {
    const r = await f(url, { signal: ctl.signal, headers: { 'Cache-Control': 'no-cache' } });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return await r.text();
  } finally { clearTimeout(timer); }
}

/** Downloads lists that are missing or older than 4 days (or all of them when forced). */
async function updateFilters(force) {
  if (filterState.updating) return;
  const stale = FILTER_LISTS.filter((l) => {
    try { return force || Date.now() - fs.statSync(path.join(filterDir(), l.id + '.txt')).mtimeMs > FILTER_MAX_AGE; } catch (_e) { return true; }
  });
  if (!stale.length) return;
  filterState.updating = true; filterState.state = 'updating'; sendFilterStatus();
  let changed = false;
  await Promise.all(stale.map(async (l) => {
    const info = filterState.lists.find((x) => x.id === l.id);
    try {
      const text = await fetchText(l.url);
      if (text.length < 1000 || /<html/i.test(text.slice(0, 500))) throw new Error('not a filter list');
      const f = path.join(filterDir(), l.id + '.txt');
      fs.writeFileSync(f + '.tmp', text);
      fs.renameSync(f + '.tmp', f);
      if (info) info.error = '';
      changed = true;
    } catch (e) { if (info) info.error = String(e && e.message || e); }
  }));
  filterState.updating = false;
  if (changed) loadFilters(); else { filterState.state = 'ready'; sendFilterStatus(); }
}

function blockerOn(topUrl) {
  if (!cfg.blockTrackers) return false;
  const top = hostOf(topUrl);
  return !hostMatch(top, cfg.allowSites);
}

function countBlocked(t) {
  t[1].blocked = (t[1].blocked || 0) + 1;
  if (!t[1].bt) t[1].bt = setTimeout(() => { t[1].bt = null; send({ t: 'blocked', tab: t[0], n: t[1].blocked }); }, 300);
}

function hookBlocking(ses) {
  ses.webRequest.onBeforeRequest((d, cb) => {
    const wc = d.webContents;
    const host = hostOf(d.url);
    if (d.resourceType === 'mainFrame') {
      if (cfg.blockedSites.length && hostMatch(host, cfg.blockedSites)) {
        const t = wc && [...tabs.entries()].find(([, x]) => x.wc === wc);
        if (t) { t[1].siteBlocked = d.url; send({ t: 'siteblocked', tab: t[0], url: d.url }); }
        return cb({ cancel: true });
      }
      return cb({});
    }
    if (isChallenge(d.url)) {
      const t = wc && [...tabs.entries()].find(([, x]) => x.wc === wc);
      if (t && d.resourceType === 'subFrame' && !t[1].challengeSent) {
        t[1].challengeSent = true;
        log(`tab ${t[0]}: human verification (${hostOf(d.url)})`);
        send({ t: 'challenge', tab: t[0], host: hostOf(d.url) });
      }
      return cb({});
    }
    if (wc && !wc.isDestroyed?.()) {
      const t = [...tabs.entries()].find(([, x]) => x.wc === wc);
      const top = (t && t[1].readerOf && wc.getURL().startsWith('file:')) ? t[1].readerOf : wc.getURL();
      if (blockerOn(top) && filters.match(d.url, d.resourceType, top)) {
        if (t) countBlocked(t);
        return cb({ cancel: true });
      }
    }
    return cb({});
  });
}

// Hides ad boxes. Inserted as a user stylesheet when the page commits, before it renders.
function injectCosmetic(tab, url) {
  if (!/^https?:/i.test(url) || !blockerOn(url) || tab.cssFor === url) return;
  tab.cssFor = url;
  const css = filters.cosmeticCss(hostOf(url));
  if (css) tab.wc.insertCSS(css, { cssOrigin: 'user' }).catch(() => {});
}

// YouTube serves video ads from its own servers, so blocking requests isn't enough: this script runs in every
// YouTube page before the page's own scripts and strips the ad entries from the player data (the same approach
// as uBlock Origin's json-prune), and skips any ad that still slips through.
function youtubeScript() {
  return `(() => {
  const ON = ${JSON.stringify(cfg.blockTrackers)}, ALLOW = ${JSON.stringify(cfg.allowSites)};
  const H = location.hostname;
  if (!ON || !/(^|\\.)youtube(-nocookie)?\\.com$/.test(H) || ALLOW.some((d) => H === d || H.endsWith('.' + d))) return;
  const KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];
  const prune = (o) => {
    try {
      if (o && typeof o === 'object') {
        for (const k of KEYS) if (k in o) delete o[k];
        if (o.playerResponse && typeof o.playerResponse === 'object') for (const k of KEYS) if (k in o.playerResponse) delete o.playerResponse[k];
      }
    } catch (e) { /* frozen object */ }
    return o;
  };
  JSON.parse = new Proxy(JSON.parse, { apply: (t, th, a) => prune(Reflect.apply(t, th, a)) });
  Response.prototype.json = new Proxy(Response.prototype.json, { apply: (t, th, a) => Reflect.apply(t, th, a).then(prune) });
  let ipr;
  try { Object.defineProperty(window, 'ytInitialPlayerResponse', { configurable: true, get: () => ipr, set: (v) => { ipr = prune(v); } }); } catch (e) { /* defined already */ }
  const skip = () => {
    const p = document.querySelector('.ad-showing');
    if (p) { const v = p.querySelector('video'); if (v && isFinite(v.duration) && v.duration > 0) { v.muted = true; v.currentTime = v.duration; } }
    const b = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button');
    if (b) b.click();
  };
  setInterval(skip, 400);
})();`;
}

async function installPageScripts(tab) {
  const dbg = tab.wc.debugger;
  if (!dbg) return;
  try {
    if (!dbg.isAttached()) dbg.attach('1.3');
    await dbg.sendCommand('Page.enable');
    if (tab.ytScript) await dbg.sendCommand('Page.removeScriptToEvaluateOnNewDocument', { identifier: tab.ytScript }).catch(() => {});
    const r = await dbg.sendCommand('Page.addScriptToEvaluateOnNewDocument', { source: youtubeScript() });
    tab.ytScript = r && r.identifier;
  } catch (_e) { /* DevTools protocol not available: network and CSS blocking still work */ }
}

function hookSession(ses) {
  if (ses.__hooked) return;
  ses.__hooked = true;
  hookBlocking(ses);
  ses.setPermissionRequestHandler((wc, permission, callback, details) => {
    const tab = [...tabs.entries()].find(([, t]) => t.wc === wc);
    if (!tab) return callback(false);
    const reqId = ++permSeq;
    pendingPerms.set(reqId, callback);
    send({ t: 'permission', tab: tab[0], req: reqId, permission, url: (details && details.requestingUrl) || wc.getURL() });
  });
  ses.on('will-download', (_e, item, wc) => {
    const tab = [...tabs.entries()].find(([, t]) => t.wc === wc);
    const dl = ++dlSeq;
    let dir = app.getPath('downloads');
    try { fs.mkdirSync(dir, { recursive: true }); } catch (_e2) { dir = os.homedir(); }
    let target = path.join(dir, item.getFilename() || 'download');
    for (let n = 1; fs.existsSync(target); n++) {
      const p = path.parse(item.getFilename() || 'download');
      target = path.join(dir, `${p.name} (${n})${p.ext}`);
    }
    item.setSavePath(target);
    downloads.set(dl, item);
    send({ t: 'download', id: dl, state: 'start', name: path.basename(target), path: target, url: item.getURL(), total: item.getTotalBytes(), src: tab ? tab[0] : 0 });   // not 'tab': the shell routes those to the tab
    item.on('updated', () => send({ t: 'download', id: dl, state: 'progress', received: item.getReceivedBytes(), total: item.getTotalBytes(), paused: item.isPaused() }));
    item.once('done', (_ev, state) => { downloads.delete(dl); send({ t: 'download', id: dl, state: state === 'completed' ? 'done' : state, path: target }); });
  });
}
let permSeq = 0, dlSeq = 0;
const pendingPerms = new Map();
const downloads = new Map();

const TAB_PREFS = {
  offscreen: true, sandbox: true, contextIsolation: true, nodeIntegration: false, backgroundThrottling: false, spellcheck: false,
  plugins: true,                      // Chromium's built-in PDF viewer
  // page (video) fullscreen happens inside the offscreen page; the shell decides about its own window
  disableHtmlFullscreenWindowResize: true,
};
const pendingPopups = new Map();     // key -> BrowserWindow opened by a page (window.open / target=_blank), waiting for the shell
let popupSeq = 0, authSeq = 0;
const pendingAuth = new Map();

function openTab(m) {
  const id = m.tab;
  const w = Math.max(100, m.w | 0), h = Math.max(100, m.h | 0);
  let win = m.adopt ? pendingPopups.get(m.adopt) : null;
  const adopted = !!win;
  if (win) {
    pendingPopups.delete(m.adopt);
    clearTimeout(win.__expire);
    win.setContentSize(w, h);
  } else {
    const ses = sessionFor(!!m.priv, m.group);
    hookSession(ses);
    win = new BrowserWindow({ show: false, width: w, height: h, useContentSize: true, frame: false, webPreferences: { ...TAB_PREFS, session: ses } });
  }
  hookSession(win.webContents.session || sessionFor(!!m.priv, m.group));
  setupTab(id, win, m, w, h);
  if (adopted) {
    win.webContents.invalidate();
    navState(id, tabs.get(id));
    // safety net: an adopted window that hasn't drawn after 5 s is replaced by a fresh one on the same page
    const first = tabs.get(id);
    setTimeout(() => {
      if (tabs.get(id) !== first || first.loggedFrame) return;
      let url = '';
      try { url = win.webContents.getURL(); } catch (_e) { /* gone */ }
      log(`tab ${id}: the pop-up window it was opened from didn't draw; opening ${url || 'it'} in a fresh window`);
      const fresh = new BrowserWindow({ show: false, width: first.w, height: first.h, useContentSize: true, frame: false,
        webPreferences: { ...TAB_PREFS, session: win.webContents.session } });
      first.closed = true;
      try { if (first.fd !== null) fs.closeSync(first.fd); } catch (_e) { /* ignore */ }
      first.fd = null;
      setupTab(id, fresh, m, first.w, first.h);
      try { win.destroy(); } catch (_e) { /* ignore */ }
      if (url && url !== 'about:blank') fresh.webContents.loadURL(url).catch(() => {});
    }, 5000);
  }
  else if (m.url) win.webContents.loadURL(m.url).catch(() => {});
}

function setupTab(id, win, m, w, h) {
  const wc = win.webContents;
  const tab = { win, wc, fd: null, file: null, w, h, pending: false, dirty: false, priv: !!m.priv, group: m.group || 0, blocked: 0, readerOf: null, readerFile: null, pip: false };
  tabs.set(id, tab);
  wc.setFrameRate(60);
  wc.setUserAgent(cleanUA(wc));
  if (m.zoom) wc.setZoomFactor(m.zoom);

  // a window adopted from a pop-up still has the paint handler of its old tab: only this tab's may draw
  wc.removeAllListeners('paint');
  wc.on('paint', (_e, dirty, image) => onPaint(id, tab, image, dirty));
  installPageScripts(tab);
  wc.on('page-title-updated', (_e, title) => send({ t: 'title', tab: id, title }));
  wc.on('did-start-loading', () => send({ t: 'loading', tab: id, on: true }));
  wc.on('did-start-navigation', (e, url, inPlace, isMain) => {
    // Electron 33 passes the details on the event object; the positional arguments are deprecated
    const main = e && typeof e.isMainFrame === 'boolean' ? e.isMainFrame : isMain;
    const same = e && typeof e.isSameDocument === 'boolean' ? e.isSameDocument : inPlace;
    if (main && !same) { tab.challengeSent = false; tab.blocked = 0; tab.siteBlocked = null; tab.cssFor = null; send({ t: 'blocked', tab: id, n: 0 }); }
  });
  wc.on('audio-state-changed', (e) => send({ t: 'audio', tab: id, on: !!(e && e.audible) }));
  wc.on('did-stop-loading', () => { send({ t: 'loading', tab: id, on: false }); navState(id, tab); });
  wc.on('did-navigate', (_e, url) => { navState(id, tab, url); injectCosmetic(tab, url); });
  wc.on('dom-ready', () => injectCosmetic(tab, wc.getURL()));
  wc.on('did-navigate-in-page', (_e, url, isMain) => { if (isMain) navState(id, tab, url); });
  wc.on('page-favicon-updated', (_e, favs) => send({ t: 'favicon', tab: id, url: favs && favs[0] || '' }));
  wc.on('cursor-changed', (_e, type) => send({ t: 'cursor', tab: id, type }));
  wc.on('update-target-url', (_e, url) => send({ t: 'status', tab: id, url }));
  wc.on('did-fail-load', (_e, code, desc, url, isMain) => {
    if (!isMain || code === -3) return;                                     // -3: aborted by a newer navigation
    if (code === -20 && tab.siteBlocked) return;                            // our own blocked-site cancel: 'siteblocked' was sent
    send({ t: 'fail', tab: id, code, desc, url });
  });
  wc.on('render-process-gone', (_e, d) => log(`tab ${id}: page process gone: ${d.reason}`));
  wc.on('did-fail-load', (_e, code, desc, url, isMain) => { if (isMain && code !== -3) log(`tab ${id}: ${url} failed: ${desc} (${code})`); });
  wc.on('render-process-gone', (_e, d) => send({ t: 'fail', tab: id, code: -1000, desc: 'The page crashed (' + d.reason + ')', url: wc.getURL() }));
  wc.on('found-in-page', (_e, r) => send({ t: 'found', tab: id, active: r.activeMatchOrdinal, matches: r.matches }));
  wc.on('context-menu', (_e, p) => send({
    t: 'context', tab: id, x: p.x, y: p.y, link: p.linkURL || '', src: p.srcURL || '', media: p.mediaType || 'none',
    sel: p.selectionText || '', editable: !!p.isEditable, page: p.pageURL || '',
  }));
  wc.on('certificate-error', (e, url, _error, _cert, callback) => {
    if (allowedCerts.has(hostOf(url))) { e.preventDefault(); callback(true); } else { callback(false); }
  });
  wc.on('login', (e, _details, authInfo, callback) => {
    e.preventDefault();
    const req = ++authSeq;
    pendingAuth.set(req, callback);
    send({ t: 'auth', tab: id, req, host: authInfo.host, realm: authInfo.realm || '', proxy: !!authInfo.isProxy });
  });
  wc.on('enter-html-full-screen', () => { if (!tab.pip) send({ t: 'fullscreen', tab: id, on: true }); });
  wc.on('leave-html-full-screen', () => { if (tab.pip) { tab.pip = false; send({ t: 'pip', tab: id, on: false }); } else send({ t: 'fullscreen', tab: id, on: false }); });
  // Links that open new windows keep their opener (sign-in pop-ups need it): the window is created offscreen
  // and the shell adopts it as a new tab.
  wc.setWindowOpenHandler(({ url, disposition, postBody }) => {
    if (!/^(https?:|about:blank)/i.test(url)) { send({ t: 'newtab', tab: id, url, disposition }); return { action: 'deny' }; }
    // A link that opens a tab (target=_blank, middle-click; news sites open every article this way) doesn't need its
    // opener: it becomes a fresh tab, which always draws — windows Chromium creates for a page don't reliably paint
    // offscreen. Pop-up windows (window.open with a size: sign-in, payment) and form posts are adopted with their opener.
    if (/^https?:/i.test(url) && !postBody && (disposition === 'foreground-tab' || disposition === 'background-tab')) {
      send({ t: 'newtab', tab: id, url, disposition });
      return { action: 'deny' };
    }
    return {
      action: 'allow', outlivesOpener: true,
      overrideBrowserWindowOptions: { show: false, width: tab.w, height: tab.h, useContentSize: true, frame: false, webPreferences: { ...TAB_PREFS } },
    };
  });
  wc.on('did-create-window', (child, details) => {
    const key = ++popupSeq;
    pendingPopups.set(key, child);
    child.__expire = setTimeout(() => { if (pendingPopups.delete(key)) { try { child.destroy(); } catch (_e) { /* gone */ } } }, 15000);
    send({ t: 'newtab', tab: id, url: details.url, disposition: details.disposition || 'foreground-tab', child: key });
  });
  win.on('closed', () => { if (tabs.get(id) === tab) { cleanupTab(id, tab); send({ t: 'closed', tab: id }); } });
  wc.on('will-prevent-unload', (e) => e.preventDefault());
}

function navState(id, tab, url) {
  if (!tab) return;
  const h = tab.wc.navigationHistory;
  let u = url || tab.wc.getURL();
  const reader = !!(tab.readerFile && u.startsWith('file:') && u.includes(path.basename(tab.readerFile)));
  if (reader) u = tab.readerOf;                                    // the address bar shows the article, not the temp file
  send({
    t: 'url', tab: id, url: u, reader,
    back: h ? h.canGoBack() : tab.wc.canGoBack(), fwd: h ? h.canGoForward() : tab.wc.canGoForward(),
  });
}

// Frames go to Red Star through a file in /dev/shm (shared memory) holding the whole page as BGRA. Only the rows
// that changed are copied in (a playing video changes a band of the page, not all of it), and Red Star is told
// which rows to read. While Red Star is still busy with the previous frame, the changed rows are collected.
function onPaint(id, tab, image, dirty) {
  if (tab.closed || tabs.get(id) !== tab) return;      // a paint that arrives after the tab was closed or replaced
  try { paintFrame(id, tab, image, dirty); }
  catch (e) {
    // e.g. the frame file went away (/dev/shm cleaned up): start a fresh one with the next frame
    log('frame for tab ' + id + ' failed: ' + (e && e.message));
    try { if (tab.fd !== null) fs.closeSync(tab.fd); } catch (_e) { /* ignore */ }
    tab.fd = null;
  }
}

function paintFrame(id, tab, image, dirty) {
  const size = image.getSize();
  if (!tab.loggedFrame) { tab.loggedFrame = true; log(`tab ${id}: first frame ${size.width}x${size.height}`); }
  const W = size.width, H = size.height;
  if (!W || !H) return;
  const row = W * 4, bytes = row * H;
  if (!tab.file) tab.file = path.join(shmDir(), `redstar-web-${process.pid}-${id}.bgra`);
  let y0 = 0, y1 = H;
  const resized = tab.fd === null || tab.fw !== W || tab.fh !== H;
  if (resized) {
    if (tab.fd !== null) { try { fs.closeSync(tab.fd); } catch (_e) { /* ignore */ } }
    tab.fd = fs.openSync(tab.file, 'w+');
    try { fs.ftruncateSync(tab.fd, bytes); } catch (_e) { /* ignore */ }
    tab.fw = W; tab.fh = H;
    tab.uy0 = 0; tab.uy1 = H;                         // a new size: Red Star needs every row
  } else if (dirty && dirty.height > 0 && dirty.width > 0) {
    y0 = Math.max(0, Math.min(H, Math.floor(dirty.y)));
    y1 = Math.max(y0, Math.min(H, Math.ceil(dirty.y + dirty.height)));
  }
  if (y1 <= y0) return;
  if (y0 === 0 && y1 === H) {
    const buf = (image.toBitmap || image.getBitmap).call(image);
    fs.writeSync(tab.fd, buf, 0, Math.min(buf.length, bytes), 0);
  } else {
    // copy just the changed band (full-width rows, so it is one contiguous write)
    const band = image.crop({ x: 0, y: y0, width: W, height: y1 - y0 });
    const bs = band.getSize();
    const buf = band.toBitmap();
    if (bs.width === W && buf.length >= row * (y1 - y0)) fs.writeSync(tab.fd, buf, 0, row * (y1 - y0), y0 * row);
    else { const full = image.toBitmap(); fs.writeSync(tab.fd, full, 0, Math.min(full.length, bytes), 0); y0 = 0; y1 = H; }
  }
  tab.uy0 = tab.uy0 === undefined || tab.uy0 === null ? y0 : Math.min(tab.uy0, y0);
  tab.uy1 = tab.uy1 === undefined || tab.uy1 === null ? y1 : Math.max(tab.uy1, y1);
  tab.seq = (tab.seq || 0) + 1;
  if (tab.pending) { tab.dirty = true; return; }       // Red Star hasn't taken the last frame yet
  sendFrame(id, tab);
}

function sendFrame(id, tab) {
  tab.pending = true;
  send({ t: 'frame', tab: id, file: tab.file, w: tab.fw, h: tab.fh, seq: tab.seq, y0: tab.uy0 || 0, y1: tab.uy1 === undefined || tab.uy1 === null ? tab.fh : tab.uy1 });
  tab.uy0 = null; tab.uy1 = null;
}

function cleanupTab(id, tab) {
  tabs.delete(id);
  tab.closed = true;
  // forget the descriptor once closed: the number gets reused for other files, and a late paint must never write there
  try { if (tab.fd !== null) fs.closeSync(tab.fd); } catch (_e) { /* ignore */ }
  tab.fd = null;
  try { if (tab.file) fs.unlinkSync(tab.file); } catch (_e) { /* ignore */ }
  try { if (tab.readerFile) fs.unlinkSync(tab.readerFile); } catch (_e) { /* ignore */ }
  if (tab.priv && ![...tabs.values()].some((t) => t.priv && t.group === tab.group)) {
    try { const ps = session.fromPartition('redstar-private-' + tab.group); ps.clearStorageData(); ps.clearCache(); } catch (_e) { /* ignore */ }
  }
}

// The page in a real (visible) window sharing the tab's session: input goes straight to Chromium there, so
// verification sliders and similar widgets work. Closing it reloads the tab, already verified (same cookies).
function popOut(id, tab) {
  let url = '';
  try { url = tab.wc.getURL(); } catch (_e) { return; }
  if (!/^https?:/i.test(url)) return;
  if (tab.popout && !tab.popout.isDestroyed()) { tab.popout.focus(); return; }
  const win = new BrowserWindow({
    width: Math.max(800, tab.w), height: Math.max(600, tab.h), title: 'Red Star Online', autoHideMenuBar: true,
    webPreferences: { session: tab.wc.session, sandbox: true, contextIsolation: true, nodeIntegration: false, spellcheck: false },
  });
  tab.popout = win;
  try { win.setMenuBarVisibility(false); } catch (_e) { /* ignore */ }
  try { win.webContents.setUserAgent(cleanUA(win.webContents)); } catch (_e) { /* ignore */ }
  win.loadURL(url).catch(() => {});
  log(`tab ${id}: opened ${hostOf(url)} in a separate window`);
  win.on('closed', () => {
    tab.popout = null;
    if (tabs.get(id) === tab) { try { tab.wc.reload(); } catch (_e) { /* gone */ } }
    send({ t: 'popoutclosed', tab: id });
  });
}

function closeTab(id) {
  const tab = tabs.get(id);
  if (!tab) return;
  cleanupTab(id, tab);
  try { tab.win.destroy(); } catch (_e) { /* ignore */ }
}

const SELECTION_JS = `(function () {
  const a = document.activeElement;
  if (a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA') && typeof a.selectionStart === 'number' && a.selectionStart !== a.selectionEnd)
    return a.value.substring(a.selectionStart, a.selectionEnd);
  return String(window.getSelection());
})()`;
// Picks the main article (paragraph-density scoring, like a small Readability) and returns clean HTML.
const EXTRACT_JS = `(function () {
  const SKIP = 'script,style,noscript,iframe,form,nav,aside,footer,header,button,svg,canvas,select,input,textarea,[aria-hidden=true],[hidden],.ad,.ads,.advert,.advertisement,.sidebar,.comments,.share,.social,.related,.newsletter,.cookie,[role=navigation],[role=banner],[role=contentinfo],[role=complementary]';
  const scores = new Map();
  const add = (el, n) => { if (el && el.nodeType === 1) scores.set(el, (scores.get(el) || 0) + n); };
  for (const p of document.querySelectorAll('p,pre,td,blockquote,li')) {
    if (p.closest(SKIP)) continue;
    const len = (p.innerText || '').trim().length;
    if (len < 25) continue;
    const s = 1 + Math.min(3, Math.floor(len / 100)) + (p.innerText.split(',').length - 1) * 0.3;
    add(p.parentElement, s);
    add(p.parentElement && p.parentElement.parentElement, s / 2);
  }
  let best = null, top = 0;
  for (const [el, sc] of scores) {
    const links = [...el.querySelectorAll('a')].reduce((n, a) => n + (a.innerText || '').length, 0);
    const text = (el.innerText || '').length || 1;
    const adj = sc * (1 - Math.min(0.9, links / text));
    if (adj > top) { top = adj; best = el; }
  }
  const pick = (top >= 3 && best) || document.querySelector('article') || document.querySelector('main') || document.querySelector('[role=main]') || document.body;
  const c = pick.cloneNode(true);
  c.querySelectorAll(SKIP).forEach((n) => n.remove());
  c.querySelectorAll('picture source,video,audio').forEach((n) => n.remove());
  c.querySelectorAll('img').forEach((img) => {
    const src = img.currentSrc || img.getAttribute('src') || img.getAttribute('data-src') || '';
    if (!src) { img.remove(); return; }
    try { img.setAttribute('src', new URL(src, location.href).href); } catch (e) { img.remove(); }
  });
  c.querySelectorAll('*').forEach((n) => { [...n.attributes].forEach((a) => { if (!['href', 'src', 'alt', 'colspan', 'rowspan', 'width', 'height'].includes(a.name)) n.removeAttribute(a.name); }); });
  c.querySelectorAll('a[href]').forEach((a) => { try { a.setAttribute('href', new URL(a.getAttribute('href'), location.href).href); } catch (e) { a.removeAttribute('href'); } });
  const h1 = document.querySelector('h1');
  const title = (h1 && h1.innerText.trim()) || document.title;
  return { title, html: c.innerHTML, text: c.innerText || c.textContent || '' };
})()`;

// Writer can't download pictures, so the engine fetches them (through the page's session) and inlines them.
async function inlineImages(ses, html) {
  const srcs = [...new Set([...html.matchAll(/<img[^>]+src="(https?:[^"]+)"/gi)].map((m) => m[1].replace(/&amp;/g, '&')))].slice(0, 30);
  let total = 0;
  const map = new Map();
  await Promise.all(srcs.map(async (u) => {
    try {
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 8000);
      const r = await (ses.fetch ? ses.fetch(u, { signal: ctl.signal }) : fetch(u, { signal: ctl.signal }));
      clearTimeout(timer);
      if (!r.ok) return;
      const type = (r.headers.get('content-type') || '').split(';')[0].trim();
      if (!/^image\/(png|jpe?g|gif|webp|bmp)$/.test(type)) return;
      const buf = Buffer.from(await r.arrayBuffer());
      if (buf.length > 4 * 1024 * 1024 || total + buf.length > 20 * 1024 * 1024) return;
      total += buf.length;
      map.set(u, `data:${type};base64,${buf.toString('base64')}`);
    } catch (_e) { /* skip this picture */ }
  }));
  return html.replace(/(<img[^>]+src=")(https?:[^"]+)(")/gi, (all, a, u, b) => {
    const d = map.get(u.replace(/&amp;/g, '&'));
    return d ? a + d + b : '';                      // pictures that couldn't be fetched are dropped
  });
}

// Picture-in-picture: the video is put into element fullscreen inside its (offscreen) tab, and the shell shows
// that tab's frames in a small always-on-top window. Works the same on X11 and Wayland.
const PIP_ENTER_JS = `(async function () {
  const vs = [...document.querySelectorAll('video')];
  if (!vs.length) return { ok: false, why: 'novideo' };
  const v = vs.filter((x) => !x.paused).sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0]
         || vs.sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0];
  window.__rsPip = v;
  try { if (document.fullscreenElement !== v) await v.requestFullscreen(); }
  catch (e) {
    window.__rsPipStyle = v.getAttribute('style');
    v.style.cssText += ';position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;z-index:2147483647!important;background:#000!important;object-fit:contain!important';
  }
  return { ok: true, w: v.videoWidth || v.clientWidth, h: v.videoHeight || v.clientHeight, paused: v.paused };
})()`;
const PIP_EXIT_JS = `(function () {
  const v = window.__rsPip;
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  if (v && window.__rsPipStyle !== undefined) { if (window.__rsPipStyle === null) v.removeAttribute('style'); else v.setAttribute('style', window.__rsPipStyle); delete window.__rsPipStyle; }
  window.__rsPip = null;
  return true;
})()`;
const PIP_PLAY_JS = `(function () {
  const v = window.__rsPip || document.querySelector('video');
  if (!v) return null;
  if (v.paused) v.play().catch(() => {}); else v.pause();
  return v.paused;
})()`;
function esc(t) { return String(t).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
function readerPage(title, body, dark, src) {
  const bg = dark ? '#1b1b1f' : '#fbfbf8', fg = dark ? '#e4e4e8' : '#222';
  return `<!doctype html><meta charset="utf-8"><title>${esc(title)}</title><style>
body{background:${bg};color:${fg};font:19px/1.7 Georgia,'Noto Serif',serif;margin:0}
main{max-width:720px;margin:0 auto;padding:48px 24px 96px}h1{font:700 32px/1.25 system-ui,sans-serif;margin:0 0 24px}
img{max-width:100%;height:auto;border-radius:6px}a{color:${dark ? '#8ab4ff' : '#1a56c4'}}pre,code{font-size:15px;white-space:pre-wrap}
blockquote{border-left:3px solid #8884;margin-left:0;padding-left:16px;color:#888}table{border-collapse:collapse}td,th{border:1px solid #8884;padding:4px 8px}
</style><base href="${esc(src || '')}"><main><p style="font:13px system-ui;opacity:.6">${esc(hostOf(src || ''))} · Reader view</p><h1>${esc(title)}</h1>${body}</main>`;
}
const MODS = { shift: 'shift', ctrl: 'control', alt: 'alt', meta: 'meta', left: 'leftButtonDown', middle: 'middleButtonDown', right: 'rightButtonDown' };
function mods(list) { return (list || []).map((k) => MODS[k]).filter(Boolean); }

function handle(m) {
  const tab = m.tab !== undefined ? tabs.get(m.tab) : null;
  switch (m.c) {
    case 'open': return openTab(m);
    case 'close': return closeTab(m.tab);
    case 'ack':
      if (tab) {
        tab.pending = false;
        if (tab.dirty) { tab.dirty = false; sendFrame(m.tab, tab); }
      }
      return undefined;
    case 'nav': if (tab) tab.wc.loadURL(m.url).catch(() => {}); return undefined;
    case 'back': if (tab) (tab.wc.navigationHistory ? tab.wc.navigationHistory.goBack() : tab.wc.goBack()); return undefined;
    case 'forward': if (tab) (tab.wc.navigationHistory ? tab.wc.navigationHistory.goForward() : tab.wc.goForward()); return undefined;
    case 'reload': if (tab) (m.hard ? tab.wc.reloadIgnoringCache() : tab.wc.reload()); return undefined;
    case 'stop': if (tab) tab.wc.stop(); return undefined;
    case 'popout': if (tab) popOut(m.tab, tab); return undefined;
    case 'resize':
      if (tab) {
        tab.win.setContentSize(Math.max(100, m.w | 0), Math.max(100, m.h | 0));
        tab.wc.invalidate();
      }
      return undefined;
    case 'focus': if (tab) { tab.wc.focus(); } return undefined;
    case 'mouse':
      if (tab) tab.wc.sendInputEvent({
        type: m.type, x: m.x | 0, y: m.y | 0, button: m.button || 'left', clickCount: m.clicks || 1, modifiers: mods(m.mods),
      });
      return undefined;
    case 'wheel':
      if (tab) tab.wc.sendInputEvent({
        type: 'mouseWheel', x: m.x | 0, y: m.y | 0, deltaX: m.dx, deltaY: m.dy,
        wheelTicksX: m.dx / 120, wheelTicksY: m.dy / 120, canScroll: true, modifiers: mods(m.mods),
      });
      return undefined;
    case 'key':
      if (tab) tab.wc.sendInputEvent({ type: m.type, keyCode: m.key, modifiers: mods(m.mods) });
      return undefined;
    case 'char':
      if (tab) tab.wc.sendInputEvent({ type: 'char', keyCode: m.text });
      return undefined;
    case 'insert': if (tab && m.text) tab.wc.insertText(m.text); return undefined;
    case 'edit':
      if (tab && (m.cmd === 'copy' || m.cmd === 'cut')) {
        // The hidden engine can't reliably own the system clipboard, so the text goes to the shell instead.
        tab.wc.executeJavaScript(SELECTION_JS).then((text) => {
          if (text) send({ t: 'clip', tab: m.tab, text });
          if (text && m.cmd === 'cut') tab.wc.cut();
        }).catch(() => {});
      } else if (tab) {
        const wc = tab.wc;
        ({ copy: () => wc.copy(), cut: () => wc.cut(), paste: () => wc.paste(), selectAll: () => wc.selectAll(), undo: () => wc.undo(), redo: () => wc.redo(),
           pasteMatch: () => wc.pasteAndMatchStyle(), delete: () => wc.delete() }[m.cmd] || (() => {}))();
      }
      return undefined;
    case 'zoom': if (tab) tab.wc.setZoomFactor(Math.max(0.25, Math.min(5, m.factor))); return undefined;
    case 'find':
      if (tab) {
        if (!m.text) tab.wc.stopFindInPage('clearSelection');
        else tab.wc.findInPage(m.text, { forward: m.forward !== false, findNext: !!m.next, matchCase: !!m.matchCase });
      }
      return undefined;
    case 'stopfind': if (tab) tab.wc.stopFindInPage('clearSelection'); return undefined;
    case 'perm': { const cb = pendingPerms.get(m.req); if (cb) { pendingPerms.delete(m.req); cb(!!m.allow); } return undefined; }
    case 'dlcancel': { const it = downloads.get(m.id); if (it) it.cancel(); return undefined; }
    case 'dlpause': { const it = downloads.get(m.id); if (it) { if (it.isPaused()) it.resume(); else it.pause(); } return undefined; }
    case 'clear': {
      const ses = session.fromPartition('persist:redstar-online');
      const what = m.what || ['cookies', 'localstorage', 'cache'];
      const storages = what.filter((w) => w !== 'cache');
      return Promise.all([
        what.includes('cache') ? ses.clearCache() : null,
        storages.length ? ses.clearStorageData({ storages }) : null,
      ]).then(() => send({ t: 'cleared' }));
    }
    case 'config':
      cfg.blockTrackers = m.blockTrackers !== false;
      cfg.allowSites = (m.allowSites || []).map((x) => String(x).toLowerCase());
      cfg.blockedSites = (m.blockedSites || []).map((x) => String(x).trim().toLowerCase().replace(/^https?:\/\//, '').replace(/\/.*$/, '')).filter(Boolean);
      if (m.theme) nativeTheme.themeSource = m.theme;          // 'dark' | 'light' | 'system'
      {
        const key = JSON.stringify([cfg.blockTrackers, cfg.allowSites]);
        if (key !== cfg.scriptKey) { cfg.scriptKey = key; for (const t of tabs.values()) installPageScripts(t); }
      }
      return undefined;
    case 'updatefilters': updateFilters(true); return undefined;
    case 'filterstatus': sendFilterStatus(); return undefined;
    case 'allowcert': allowedCerts.add(String(m.host).toLowerCase()); return undefined;
    case 'mute': if (tab) tab.wc.setAudioMuted(!!m.on); return undefined;
    case 'devtools': if (tab) { log(`tab ${m.tab}: developer tools ${tab.wc.isDevToolsOpened() ? 'closed' : 'opened'}`); if (tab.wc.isDevToolsOpened()) tab.wc.closeDevTools(); else tab.wc.openDevTools({ mode: 'detach' }); } return undefined;
    case 'pip':
      if (tab) {
        tab.pip = true;
        tab.wc.executeJavaScript(PIP_ENTER_JS, true).then((r) => {
          if (r && r.ok) send({ t: 'pip', tab: m.tab, on: true, w: r.w, h: r.h, paused: !!r.paused });
          else { tab.pip = false; send({ t: 'pipfail', tab: m.tab, why: (r && r.why) || 'error' }); }
        }).catch((e) => { tab.pip = false; send({ t: 'pipfail', tab: m.tab, why: String(e && e.message || e) }); });
      }
      return undefined;
    case 'pipexit':
      if (tab) { tab.wc.executeJavaScript(PIP_EXIT_JS, true).catch(() => {}).finally(() => { if (tab.pip) { tab.pip = false; send({ t: 'pip', tab: m.tab, on: false }); } }); }
      return undefined;
    case 'pipplay':
      if (tab) tab.wc.executeJavaScript(PIP_PLAY_JS, true).then((paused) => send({ t: 'pipstate', tab: m.tab, paused: !!paused })).catch(() => {});
      return undefined;
    case 'exitfullscreen':
      if (tab) tab.wc.executeJavaScript('document.fullscreenElement ? document.exitFullscreen().then(() => true) : false', true).catch(() => {});
      return undefined;
    case 'authreply': {
      const cb = pendingAuth.get(m.req);
      if (cb) { pendingAuth.delete(m.req); if (m.cancel) cb(); else cb(String(m.user || ''), String(m.pass || '')); }
      return undefined;
    }
    case 'extract':
      if (tab) {
        tab.wc.executeJavaScript(EXTRACT_JS)
          .then(async (r) => {
            let html = r.html || '';
            if (m.kind === 'writer') html = await inlineImages(tab.wc.session, html);
            send({ t: 'pagedata', tab: m.tab, kind: m.kind || 'writer', title: r.title || '', html, text: r.text || '', url: tab.readerOf || tab.wc.getURL() });
          })
          .catch((e) => send({ t: 'pagedata', tab: m.tab, kind: m.kind || 'writer', title: '', html: '', text: '', error: String(e && e.message || e) }));
      }
      return undefined;
    case 'reader':
      if (tab) {
        if (tab.readerFile && tab.wc.getURL().startsWith('file:')) {            // already in reader view: go back to the page
          const h = tab.wc.navigationHistory;
          if (h && h.canGoBack()) h.goBack(); else tab.wc.loadURL(tab.readerOf).catch(() => {});
          return undefined;
        }
        const original = tab.wc.getURL();
        tab.wc.executeJavaScript(EXTRACT_JS).then((r) => {
          const file = tab.readerFile || path.join(os.tmpdir(), `redstar-reader-${process.pid}-${m.tab}.html`);
          fs.writeFileSync(file, readerPage(r.title, r.html, !!m.dark, original));
          tab.readerFile = file;
          tab.readerOf = original;
          return tab.wc.loadFile(file);
        }).catch((e) => send({ t: 'error', tab: m.tab, msg: 'Reader mode failed: ' + String(e && e.message || e) }));
      }
      return undefined;
    case 'pdf':
      if (tab) tab.wc.printToPDF({ printBackground: true, pageSize: 'A4' })
        .then((buf) => { fs.writeFileSync(m.path, buf); send({ t: 'pdfdone', tab: m.tab, path: m.path, ok: true }); })
        .catch(() => send({ t: 'pdfdone', tab: m.tab, path: m.path, ok: false }));
      return undefined;
    case 'quit': return app.quit();
    default: return undefined;
  }
}

function connect() {
  sock = net.createConnection(sockPath);
  sock.setEncoding('utf8');
  sock.on('connect', () => {
    send({ t: 'hello', pid: process.pid, version: process.versions.chrome });
    setTimeout(sendGpuStatus, 1500);         // after the GPU process has started
    loadFilters();
    setTimeout(() => updateFilters(false), 3000);
    setInterval(() => updateFilters(false), 6 * 3600 * 1000);
  });
  sock.on('data', (chunk) => {
    buffer += chunk;
    let i;
    while ((i = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, i);
      buffer = buffer.slice(i + 1);
      if (!line.trim()) continue;
      try { handle(JSON.parse(line)); } catch (e) { send({ t: 'error', msg: String(e && e.message || e) }); }
    }
  });
  sock.on('close', () => app.quit());
  sock.on('error', () => app.quit());
}

if (!sockPath) { console.error('REDSTAR_WEB_SOCKET is not set'); app.quit(); } else {
  app.on('window-all-closed', () => { /* keep running with zero tabs */ });
  app.whenReady().then(connect);
  app.on('before-quit', () => { for (const id of [...tabs.keys()]) closeTab(id); });
}
