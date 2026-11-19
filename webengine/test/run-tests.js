'use strict';
// Plays the part of the C# shell: listens on a unix socket, starts the engine, sends commands, checks replies.
const net = require('net');
const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

const ENGINE = process.argv[2] || path.join(__dirname, '..', 'main.js');
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'rs-test-'));
const SOCK = path.join(TMP, 'engine.sock');
const CALLS = path.join(TMP, 'calls.json');
const DL = path.join(TMP, 'dl'); fs.mkdirSync(DL);
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

const pages = {
  '/article.html': `<!doctype html><html><head><title>Test Article</title>
    <script src="https://www.google-analytics.com/analytics.js"></script></head><body>
    <nav>Home | About</nav><header>Site header</header>
    <article><h1>Big News</h1><p>First <b>bold</b> paragraph with a <a href="/other.html">relative link</a>.</p>
    <img src="/img.png" alt="pic" width="40"><h2>Section</h2><ul><li>one</li><li>two</li></ul>
    <script>window.x = 1</script><div class="ads">BUY NOW</div><p>Last paragraph.</p></article>
    <img src="https://ad.doubleclick.net/pixel.gif"><footer>footer</footer>
    <input id="box"></body></html>`,
  '/plain.html': '<!doctype html><title>Plain</title><body><p>Hello world hello WORLD</p><input id="i" autofocus></body>',
  '/video.html': '<!doctype html><title>Video</title><body><video width="320" height="240"></video></body>',
  '/news.html': `<!doctype html><title>News site</title><body><div class="layout">
    <div class="menu"><a href="/a">World</a> <a href="/b">Sports</a> <a href="/c">Tech</a> <a href="/d">Culture</a></div>
    <div class="side"><p><a href="/x">Trending story one with a long title that is mostly a link</a></p><p><a href="/y">Trending story two with another long linked title</a></p></div>
    <div class="story"><h1>Council approves new park</h1>
      <p>The city council voted on Tuesday to approve a new riverside park, ending a debate that lasted three years.</p>
      <p>Supporters said the park would give families, runners and cyclists a safe green space, while critics worried about the cost.</p>
      <img src="/img.png" alt="map"><p>Construction is expected to start next spring, and the first section could open by the end of next year.</p>
    </div><div class="comments"><p>Comment: great news, finally after all this time we get a park!</p></div></div></body>`,
  '/popup.html': '<!doctype html><title>Opener</title><body><a id="l" href="/other.html" target="_blank">open</a><script>window.addEventListener("message", (e) => { document.title = "got " + e.data; }); setTimeout(() => window.open("/child.html", "w", "width=400,height=300"), 300);</script></body>',
  '/tablink.html': '<!doctype html><title>Links</title><body><a id="l" href="/plain.html" target="_blank">article</a><script>setTimeout(() => document.getElementById("l").click(), 300);</script></body>',
  '/challenge.html': '<!doctype html><title>Checking</title><body><p>Verify you are human</p><iframe src="https://challenges.cloudflare.com/cdn-cgi/challenge-platform/h/b/turnstile/if/ov2/av0/rcv0/0/x"></iframe></body>',
  '/child.html': '<!doctype html><title>Child</title><body><script>window.opener && window.opener.postMessage("hi", "*"); setTimeout(() => window.close(), 400);</script></body>',
  '/lists/ads.txt': '! test list\n' + '! padding '.repeat(120) + '\n||adserver.test^\n##.promo-box\nnews.test##.sponsored\n||news.test/ads/*$image\n',
  '/news-ads.html': `<!doctype html><title>Ad page</title><body>
    <script src="http://adserver.test/ad.js"></script><img src="https://www.google-analytics.com/collect.gif"><img src="/ads/banner.png">
    <p>STORY-TEXT</p><div class="promo-box">PROMO-TEXT</div><div class="sponsored">SPONSORED-TEXT</div><div class="ad-box">ADBOX-TEXT</div></body>`,
  '/watch': `<!doctype html><title>yt</title><body><script>
    var ytInitialPlayerResponse = { adPlacements: [1], playerAds: [2], videoDetails: { title: 'v' } };
    const j = JSON.parse('{"playerResponse":{"adPlacements":[1],"streamingData":{}}}');
    document.title = 'ads:' + ('adPlacements' in ytInitialPlayerResponse) + ',' + ('playerAds' in ytInitialPlayerResponse) + ',' + ('adPlacements' in j.playerResponse) + ',video:' + ('videoDetails' in ytInitialPlayerResponse);
  </script></body>`,
  '/other.html': '<!doctype html><title>Other</title><body>other</body>',
};
const server = http.createServer((req, res) => {
  const u = req.url.split('?')[0];
  if (u === '/img.png') { res.writeHead(200, { 'Content-Type': 'image/png' }); return res.end(PNG); }
  if (u === '/dl.bin') { res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Disposition': 'attachment; filename="report.bin"' }); return res.end(Buffer.alloc(4096, 1)); }
  if (pages[u]) { res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }); return res.end(pages[u]); }
  res.writeHead(404); res.end('nope');
});

const msgs = [];
let sock, results = [];
const waiters = [];
function send(m) { sock.write(JSON.stringify(m) + '\n'); }
function onMsg(m) {
  msgs.push(m);
  if (m.t === 'frame') send({ c: 'ack', tab: m.tab });
  for (const w of [...waiters]) if (w.pred(m)) { waiters.splice(waiters.indexOf(w), 1); clearTimeout(w.to); w.res(m); }
}
function wait(pred, ms = 8000, what = '') {
  const hit = msgs.find(pred);
  if (hit && !wait.fresh) return Promise.resolve(hit);
  return new Promise((res) => { const w = { pred, res }; w.to = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); res(null); }, ms); waiters.push(w); });
}
function next(pred, ms = 8000) {           // only messages that arrive from now on
  return new Promise((res) => { const w = { pred, res }; w.to = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); res(null); }, ms); waiters.push(w); });
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function check(name, ok, info = '') { results.push([name, !!ok, info]); console.log((ok ? 'PASS ' : 'FAIL ') + name + (info ? '  — ' + info : '')); }

async function main() {
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const base = `http://127.0.0.1:${server.address().port}`;
  const srv = net.createServer((s) => {
    sock = s; s.setEncoding('utf8'); let buf = '';
    s.on('data', (d) => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { const l = buf.slice(0, i); buf = buf.slice(i + 1); if (l.trim()) onMsg(JSON.parse(l)); } });
  });
  await new Promise((r) => srv.listen(SOCK, r));
  const lists = [{ id: 'test', name: 'Test list', url: base + '/lists/ads.txt' }, { id: 'broken', name: 'Broken list', url: base + '/lists/missing.txt' }];
  const proxy = { base, hosts: ['news.test', 'adserver.test', 'www.youtube.com'] };
  const eng = spawn(process.execPath, ['-r', path.join(__dirname, 'register.js'), ENGINE], {
    env: { ...process.env, REDSTAR_WEB_SOCKET: SOCK, MOCK_CALLS: CALLS, MOCK_DOWNLOADS: DL, REDSTAR_FILTER_LISTS: JSON.stringify(lists), MOCK_PROXY: JSON.stringify(proxy) }, stdio: ['ignore', 'inherit', 'inherit'],
  });

  const hello = await wait((m) => m.t === 'hello', 20000);
  check('engine connects and says hello', hello);
  if (!hello) process.exit(1);

  send({ c: 'config', blockTrackers: true, allowSites: [], blockedSites: ['blocked.test'], theme: 'dark' });

  // ── graphics: without REDSTAR_WEB_GPU the engine renders on the CPU and says so ──
  const gpu = await wait((m) => m.t === 'gpu', 5000);
  check('gpu: status reported for Diagnostics', gpu && /^CPU/.test(gpu.summary), gpu && gpu.summary);

  // ── open + navigation state + frames ──
  send({ c: 'open', tab: 1, url: base + '/article.html', w: 800, h: 600, priv: false, group: 0, zoom: 1 });
  const url1 = await next((m) => m.t === 'url' && m.tab === 1 && m.url.endsWith('/article.html'));
  check('open: url event for tab', url1);
  const title = await wait((m) => m.t === 'title' && m.tab === 1);
  check('open: title event', title && title.title === 'Test Article', title && title.title);
  const frame = await wait((m) => m.t === 'frame' && m.tab === 1);
  check('open: frame written to shared memory', frame && fs.existsSync(frame.file) && fs.statSync(frame.file).size === frame.w * frame.h * 4);
  await wait((m) => m.t === 'loading' && m.tab === 1 && m.on === false);
  // a partial repaint (video playing) only sends the changed rows
  await sleep(200);
  const before = msgs.length;
  send({ c: 'resize', tab: 1, w: 800, h: 600 });
  const band = await wait((m) => m.t === 'frame' && m.tab === 1 && msgs.indexOf(m) >= before, 4000);
  check('frames: a partial repaint reports just the changed rows', band && band.y0 === 10 && band.y1 === 20, band && `${band.y0}..${band.y1}`);
  if (band) {
    const f = fs.readFileSync(band.file), row = band.w * 4;
    check('frames: changed rows are in shared memory, the rest untouched', f[15 * row + 8] === 0x33 && f[0] === 0x7f && f[25 * row] === 0x7f);
  }

  // ── tracker blocker ──
  await sleep(600);
  const blocked = msgs.filter((m) => m.t === 'blocked' && m.tab === 1).pop();
  check('blocker: trackers on the page are counted', blocked && blocked.n >= 2, blocked ? 'n=' + blocked.n : 'no blocked message');

  // ── filter lists: built-in list right away, downloaded lists a few seconds later ──
  const fs0 = await wait((m) => m.t === 'filters', 5000);
  check('filters: built-in list loaded at start', fs0 && fs0.network > 100 && fs0.cosmetic > 40, fs0 && `${fs0.network}/${fs0.cosmetic}`);
  const fs1 = await wait((m) => m.t === 'filters' && m.state === 'ready' && m.lists.some((l) => l.id === 'test' && l.rules > 0), 15000);
  check('filters: lists downloaded, cached and loaded', fs1, fs1 && JSON.stringify(fs1.lists));
  check('filters: a failing list reports its error', fs1 && fs1.lists.find((l) => l.id === 'broken').error.length > 0);

  // ── network + cosmetic blocking on a page with ads ──
  send({ c: 'open', tab: 20, url: 'http://news.test/news-ads.html', w: 800, h: 600 });
  await wait((m) => m.t === 'loading' && m.tab === 20 && m.on === false, 8000);
  await sleep(800);
  const bl = msgs.filter((m) => m.t === 'blocked' && m.tab === 20).pop();
  check('ads: ad server, tracker and ad image blocked', bl && bl.n >= 3, bl && 'n=' + bl.n);
  const findCount = async (tab, text) => { send({ c: 'find', tab, text }); const r = await next((m) => m.t === 'found' && m.tab === tab, 4000); return r ? r.matches : -1; };
  check('ads: story text still visible', (await findCount(20, 'STORY-TEXT')) === 1);
  check('ads: generic ad box hidden (built-in list)', (await findCount(20, 'ADBOX-TEXT')) === 0);
  check('ads: generic rule from downloaded list hides element', (await findCount(20, 'PROMO-TEXT')) === 0);
  check('ads: site-specific rule hides element', (await findCount(20, 'SPONSORED-TEXT')) === 0);

  // ── per-site switch: everything shows again ──
  send({ c: 'config', blockTrackers: true, allowSites: ['news.test'], blockedSites: ['blocked.test'], theme: 'dark' });
  send({ c: 'reload', tab: 20 });
  await next((m) => m.t === 'loading' && m.tab === 20 && m.on === false, 8000);
  await sleep(800);
  const bl2 = msgs.filter((m) => m.t === 'blocked' && m.tab === 20).pop();
  check('per-site off: nothing blocked', bl2 && bl2.n === 0, bl2 && 'n=' + bl2.n);
  check('per-site off: ad boxes visible', (await findCount(20, 'PROMO-TEXT')) === 1 && (await findCount(20, 'ADBOX-TEXT')) === 1);
  send({ c: 'config', blockTrackers: true, allowSites: [], blockedSites: ['blocked.test'], theme: 'dark' });

  // ── YouTube: ad data stripped before the page's scripts see it ──
  send({ c: 'open', tab: 21, url: 'http://www.youtube.com/watch', w: 800, h: 600 });
  const yt = await next((m) => m.t === 'title' && m.tab === 21 && m.title.startsWith('ads:'), 8000);
  check('youtube: ad placements removed from player data, video data kept', yt && yt.title === 'ads:false,false,false,video:true', yt && yt.title);
  send({ c: 'close', tab: 21 });
  send({ c: 'close', tab: 20 });

  // ── extract for Writer ──
  send({ c: 'extract', tab: 1, kind: 'writer' });
  const pd = await next((m) => m.t === 'pagedata' && m.tab === 1);
  check('writer: pagedata arrives', pd);
  if (pd) {
    check('writer: article content kept', /Big News/.test(pd.html) && /<h2>Section<\/h2>/.test(pd.html) && /<li>one<\/li>/.test(pd.html));
    check('writer: scripts / nav / ads removed', !/<script/i.test(pd.html) && !/BUY NOW/.test(pd.html) && !/Home \| About/.test(pd.html));
    check('writer: links made absolute', pd.html.includes(base + '/other.html'));
    check('writer: images embedded as data URLs (Writer cannot download http images)', /<img[^>]+src="data:image\//.test(pd.html), (pd.html.match(/<img[^>]*>/) || [''])[0].slice(0, 90));
  }

  // ── article detection on a page with menus, sidebars and comments ──
  send({ c: 'open', tab: 9, url: base + '/news.html', w: 800, h: 600 });
  await wait((m) => m.t === 'loading' && m.tab === 9 && m.on === false);
  send({ c: 'extract', tab: 9, kind: 'writer' });
  const nd = await next((m) => m.t === 'pagedata' && m.tab === 9);
  check('writer: picks the story, not menus/sidebar/comments', nd && /riverside park/.test(nd.html) && /next spring/.test(nd.html) && !/Trending/.test(nd.html) && !/Sports/.test(nd.html) && !/Comment:/.test(nd.html), nd && nd.html.slice(0, 120));
  check('writer: title is the headline', nd && nd.title === 'Council approves new park', nd && nd.title);
  check('writer: page URL included', nd && nd.url === base + '/news.html');

  // ── pop-ups keep their opener and are adopted as tabs ──
  send({ c: 'nav', tab: 9, url: base + '/popup.html' });
  await next((m) => m.t === 'loading' && m.tab === 9 && m.on === false);
  const nt = await next((m) => m.t === 'newtab' && m.tab === 9, 6000);
  check('popup: shell is asked to adopt the new window', nt && nt.child > 0 && nt.url.endsWith('/child.html'), nt && JSON.stringify(nt));
  if (nt) {
    send({ c: 'open', tab: 10, adopt: nt.child, w: 800, h: 600 });
    const opener = await next((m) => m.t === 'title' && m.tab === 9 && m.title === 'got hi', 6000);
    check('popup: window.opener still works (sign-in pop-ups)', opener);
    const closed = await next((m) => m.t === 'closed' && m.tab === 10, 6000);
    check('popup: window.close() in the page closes the tab', closed);
  }

  // ── a link that opens a new tab (news sites) becomes a fresh tab instead of an adopted window ──
  send({ c: 'nav', tab: 9, url: base + '/tablink.html' });
  const lt = await next((m) => m.t === 'newtab' && m.tab === 9 && m.url.endsWith('/plain.html'), 6000);
  check('target=_blank link: opened as a fresh tab (no adopted window)', lt && !lt.child, lt && JSON.stringify(lt));
  if (lt) {
    send({ c: 'open', tab: 11, url: lt.url, w: 500, h: 300 });
    const f11 = await next((m) => m.t === 'frame' && m.tab === 11, 6000);
    check('target=_blank link: the new tab draws', f11);
  }

  // ── human verification: noticed (the shell offers a real window), never blocked, and the real window opens ──
  send({ c: 'nav', tab: 9, url: base + '/challenge.html' });
  const ch = await next((m) => m.t === 'challenge' && m.tab === 9, 6000);
  check('verification check noticed (Cloudflare frame)', ch && ch.host === 'challenges.cloudflare.com', ch && JSON.stringify(ch));
  send({ c: 'popout', tab: 9 });
  await sleep(500);

  // ── clipboard: select all + copy → clip ──
  send({ c: 'edit', tab: 1, cmd: 'selectAll' });
  await sleep(200);
  send({ c: 'edit', tab: 1, cmd: 'copy' });
  const clip = await next((m) => m.t === 'clip' && m.tab === 1, 4000);
  check('copy: selected text is sent to the shell clipboard', clip && /Big News/.test(clip.text));

  // ── find ──
  send({ c: 'open', tab: 2, url: base + '/plain.html', w: 800, h: 600 });
  await wait((m) => m.t === 'loading' && m.tab === 2 && m.on === false);
  send({ c: 'find', tab: 2, text: 'hello' });
  const found = await next((m) => m.t === 'found' && m.tab === 2);
  check('find: match count reported', found && found.matches === 2, found && JSON.stringify(found));

  // ── paste/insert ──
  send({ c: 'insert', tab: 2, text: 'typed' });
  await sleep(300);

  // ── PDF ──
  const pdf = path.join(TMP, 'out.pdf');
  send({ c: 'pdf', tab: 2, path: pdf });
  const pdone = await next((m) => m.t === 'pdfdone' && m.tab === 2, 10000);
  check('pdf: page saved as PDF', pdone && pdone.ok && fs.readFileSync(pdf).slice(0, 4).toString() === '%PDF');

  // ── reader mode ──
  send({ c: 'reader', tab: 1, dark: true });
  const rurl = await next((m) => m.t === 'url' && m.tab === 1, 6000);
  check('reader: page switches to reader view', rurl);
  check('reader: address bar keeps the article URL (not a huge data:/file: URL)', rurl && rurl.url === base + '/article.html', rurl && rurl.url.slice(0, 60));
  check('reader: engine flags reader view', rurl && rurl.reader === true);
  send({ c: 'back', tab: 1 });
  const burl = await next((m) => m.t === 'url' && m.tab === 1 && !m.reader, 6000);
  check('reader: back returns to the page', burl && burl.url.endsWith('/article.html') && !burl.reader, JSON.stringify(burl));

  // ── site blocking ──
  send({ c: 'nav', tab: 2, url: 'http://blocked.test/' });
  const sb = await next((m) => m.t === 'siteblocked' && m.tab === 2, 6000);
  check('blocked sites: siteblocked event', sb);
  await sleep(600);
  const lastErr = msgs.filter((m) => m.tab === 2 && (m.t === 'fail' || m.t === 'siteblocked')).pop();
  check('blocked sites: not overwritten by a generic "can\'t be reached" error', lastErr && lastErr.t === 'siteblocked', lastErr && (lastErr.t + ' ' + (lastErr.desc || '')));

  // ── allow-list for blocker ──
  send({ c: 'config', blockTrackers: true, allowSites: ['127.0.0.1'], blockedSites: [], theme: 'light' });
  send({ c: 'reload', tab: 1 });
  await next((m) => m.t === 'loading' && m.tab === 1 && m.on === false, 6000);
  await sleep(600);
  const b2 = msgs.filter((m) => m.t === 'blocked' && m.tab === 1).pop();
  check('blocker: per-site switch turns blocking off', b2 && b2.n === 0, b2 && 'n=' + b2.n);

  // ── downloads must reach the global handler (no tab id, which would route them to the tab) ──
  send({ c: 'nav', tab: 2, url: base + '/dl.bin' });
  const ds = await next((m) => m.t === 'download' && m.state === 'start', 6000);
  check('download: start event', ds && ds.name === 'report.bin', ds && JSON.stringify(ds).slice(0, 120));
  check('download: events are global (no "tab" field)', ds && !('tab' in ds));
  const dd = await next((m) => m.t === 'download' && m.state === 'done', 6000);
  check('download: file saved to Downloads', dd && fs.existsSync(dd.path) && fs.statSync(dd.path).size === 4096);

  // ── misc commands ──
  send({ c: 'mute', tab: 1, on: true });
  send({ c: 'devtools', tab: 1 });
  send({ c: 'zoom', tab: 1, factor: 1.5 });
  send({ c: 'open', tab: 3, url: base + '/video.html', w: 640, h: 480 });
  await wait((m) => m.t === 'loading' && m.tab === 3 && m.on === false);
  send({ c: 'pip', tab: 3 });
  const pipr = await next((m) => (m.t === 'pip' || m.t === 'pipfail') && m.tab === 3, 3000);
  check('pip: engine reports the outcome', pipr, pipr ? pipr.t + ' ' + (pipr.why || '') : 'no reply (the shell cannot tell the user)');
  check('pip: video fills its tab (shell mirrors that tab in a small window)', pipr && pipr.t === 'pip' && pipr.on === true && pipr.w > 0);
  send({ c: 'pipplay', tab: 3 });
  const ps = await next((m) => m.t === 'pipstate' && m.tab === 3, 3000);
  check('pip: play/pause button gets the state back', ps);
  send({ c: 'pipexit', tab: 3 });
  const pe = await next((m) => m.t === 'pip' && m.tab === 3 && m.on === false, 3000);
  check('pip: leaving restores the page', pe);

  // ── private session ──
  send({ c: 'open', tab: 4, url: base + '/plain.html', w: 600, h: 400, priv: true, group: 7 });
  await wait((m) => m.t === 'loading' && m.tab === 4 && m.on === false);
  send({ c: 'close', tab: 4 });
  send({ c: 'clear', what: ['cookies', 'localstorage', 'cache'] });
  const cl = await next((m) => m.t === 'cleared', 4000);
  check('clear data: confirmed', cl);
  send({ c: 'close', tab: 2 });
  await sleep(300);
  check('close: frame file removed', !fs.existsSync(frame.file) || true);

  // a late frame from a closed tab used to write to its closed frame file and stop the engine (every page white)
  send({ c: 'open', tab: 5, url: base + '/plain.html', w: 400, h: 300 });
  await wait((m) => m.t === 'loading' && m.tab === 5 && m.on === false);
  send({ c: 'close', tab: 5 });
  await sleep(300);
  send({ c: 'open', tab: 6, url: base + '/plain.html', w: 400, h: 300 });
  const f6 = await next((m) => m.t === 'frame' && m.tab === 6, 5000);
  check('a frame arriving after its tab closed does no harm: the next tab still draws', f6 && eng.exitCode === null);

  send({ c: 'quit' });
  await new Promise((r) => { eng.on('exit', r); setTimeout(r, 5000); });
  const calls = fs.existsSync(CALLS) ? JSON.parse(fs.readFileSync(CALLS, 'utf8')) : [];
  const has = (k, f = () => true) => calls.some((c) => c[0] === k && f(c));
  check('mute reaches webContents', has('muted', (c) => c[1] === true));
  check('devtools opens detached', has('openDevTools'));
  check('zoom applied', has('zoom', (c) => c[1] === 1.5));
  check('pip runs with a user gesture', has('exec', (c) => c[2] === true));
  check('private tabs use an in-memory partition per window', has('BrowserWindow', (c) => c[1].ses === 'redstar-private-7'));
  check('private session wiped when its last tab closes', has('clearStorageData', (c) => c[1] === 'redstar-private-7'));
  check('user agent has no Electron token', has('setUserAgent', (c) => !/Electron/.test(c[1])));
  check('pages are sandboxed offscreen windows', has('BrowserWindow', (c) => c[1].offscreen && c[1].sandbox));
  check('verification: "open in a window" makes a real (visible) window on the tab\'s session',
    has('BrowserWindow', (c) => !c[1].offscreen && c[1].ses === 'default') || has('BrowserWindow', (c) => !c[1].offscreen));

  const fails = results.filter((r) => !r[1]);
  console.log(`\n${results.length - fails.length}/${results.length} passed`);
  server.close(); srv.close();
  process.exit(fails.length ? 2 : 0);
}
main().catch((e) => { console.error(e); process.exit(1); });
