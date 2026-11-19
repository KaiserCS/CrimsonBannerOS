'use strict';
// Red Star Online ad & tracker blocker.
// Understands the Adblock Plus / EasyList filter syntax that EasyList, EasyPrivacy and Peter Lowe's list use:
//   network rules   ||ads.example.com^   /banner/*/ad.   @@||good.com^$script   $third-party,domain=a.com|~b.com
//   cosmetic rules  ##.ad-box   example.com##.sidebar-ad   example.com#@#.ad-box
// Network rules are indexed by host and by a "token" (a word from the URL), so a request is checked against
// only a handful of rules even with 100 000 rules loaded. Pure Node: no Electron APIs here, so it can be tested.

const TYPE_OF = {             // Electron resourceType → filter option name
  mainFrame: 'document', subFrame: 'subdocument', stylesheet: 'stylesheet', script: 'script', image: 'image',
  font: 'font', object: 'object', xhr: 'xmlhttprequest', fetch: 'xmlhttprequest', ping: 'ping', cspReport: 'other',
  media: 'media', webSocket: 'websocket', websocket: 'websocket', other: 'other', document: 'document',
  eventsource: 'other', manifest: 'other', texttrack: 'media',
};
const TYPES = ['document', 'subdocument', 'stylesheet', 'script', 'image', 'font', 'object', 'xmlhttprequest', 'ping', 'media', 'websocket', 'other'];
const BIT = Object.fromEntries(TYPES.map((t, i) => [t, 1 << i]));
const ALL_BUT_DOC = TYPES.reduce((m, t) => (t === 'document' ? m : m | BIT[t]), 0);
const ALIAS = { xhr: 'xmlhttprequest', css: 'stylesheet', frame: 'subdocument', 'object-subrequest': 'object', doc: 'document' };
// Options that change what a rule does in ways we can't honour: drop those rules rather than over-block.
const SKIP_OPTS = new Set(['csp', 'removeparam', 'queryprune', 'replace', 'header', 'permissions', 'urltransform', 'uritransform',
  'cookie', 'rewrite', 'popup', 'popunder', 'webrtc', 'badfilter', 'denyallow', 'to', 'method', 'strict1p', 'strict3p',
  'ipaddress', 'genericblock', 'specifichide', 'inline-script', 'inline-font', 'jsonprune', 'hls', 'redirect-rule', 'stealth', 'content', 'extension']);
const PROCEDURAL = /:(-abp-|has-text|matches-css|matches-attr|matches-prop|matches-path|matches-media|xpath|upward|remove|style|min-text-length|watch-attr|others|if\(|if-not|nth-ancestor|contains|properties|shadow|remove-attr|remove-class)/;
const SECOND_LEVEL = new Set(['co.uk', 'org.uk', 'ac.uk', 'gov.uk', 'me.uk', 'com.au', 'net.au', 'org.au', 'co.nz', 'co.jp', 'ne.jp', 'or.jp',
  'com.br', 'com.cn', 'com.mx', 'co.za', 'com.tr', 'co.in', 'co.kr', 'com.ar', 'com.sg', 'com.hk', 'com.tw', 'co.id', 'com.my', 'com.ph',
  'com.vn', 'com.ua', 'co.il', 'com.pl', 'com.es', 'com.sa', 'com.eg', 'com.pk', 'com.ng', 'co.th']);

function hostOf(u) {
  const m = /^[a-z][a-z0-9+.-]*:\/\/(?:[^@/?#]*@)?(\[[^\]]+\]|[^:/?#]+)/i.exec(u);
  return m ? m[1].toLowerCase() : '';
}
function siteOf(host) {               // registrable domain (approximate public-suffix handling)
  if (!host || /^\d+\.\d+\.\d+\.\d+$/.test(host) || host.startsWith('[')) return host;
  const p = host.split('.');
  if (p.length <= 2) return host;
  const last2 = p.slice(-2).join('.');
  return SECOND_LEVEL.has(last2) ? p.slice(-3).join('.') : last2;
}
function domainMatch(host, d) {
  if (d.endsWith('.*')) {             // "example.*" = example with any top-level domain
    const base = d.slice(0, -2);
    const s = siteOf(host);
    return s.split('.')[0] === base.split('.').pop() && (host === s || host.endsWith('.' + s)) && (base.includes('.') ? host.includes(base) : true);
  }
  return host === d || host.endsWith('.' + d);
}

function patternToRegex(p, matchCase) {
  let start = '', end = '', s = '';
  if (p.startsWith('||')) { start = '^[a-z][a-z0-9+.-]*:\\/+(?:[^\\/?#]*\\.)?'; p = p.slice(2); }
  else if (p.startsWith('|')) { start = '^'; p = p.slice(1); }
  if (p.endsWith('|')) { end = '$'; p = p.slice(0, -1); }
  for (const ch of p) {
    if (ch === '*') s += '.*';
    else if (ch === '^') s += '(?:[^a-zA-Z0-9_\\-.%]|$)';
    else s += ch.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&');
  }
  return new RegExp(start + s.replace(/^(\.\*)+|(\.\*)+$/g, '') + end, matchCase ? '' : 'i');
}

const TOKEN_CHAR = /[a-z0-9%]/;
// A word of the pattern that must appear as a whole word in matching URLs (used to index the rule).
function bestToken(p) {
  let body = p, anchoredStart = false, anchoredEnd = false;
  if (body.startsWith('||')) { body = body.slice(2); anchoredStart = true; } else if (body.startsWith('|')) { body = body.slice(1); anchoredStart = true; }
  if (body.endsWith('|')) { body = body.slice(0, -1); anchoredEnd = true; }
  body = body.toLowerCase();
  let best = '';
  const re = /[a-z0-9%]+/g;
  let m;
  while ((m = re.exec(body))) {
    const a = m.index, b = a + m[0].length;
    const before = a === 0 ? (anchoredStart ? 'sep' : '') : body[a - 1];
    const after = b === body.length ? (anchoredEnd ? 'sep' : '') : body[b];
    if (before === '' || before === '*' || after === '' || after === '*') continue;   // could be part of a longer word
    if (m[0].length > best.length && m[0].length >= 2) best = m[0];
  }
  return best || null;
}

function parseOptions(str) {
  const r = { types: 0, notTypes: 0, party: 0, inc: null, exc: null, important: false, matchCase: false, docAllow: false, hideAllow: 0 };
  for (let o of str.split(',')) {
    o = o.trim();
    if (!o) continue;
    let neg = false;
    if (o.startsWith('~')) { neg = true; o = o.slice(1); }
    const [k0, v] = o.split(/=(.*)/s);
    const k = ALIAS[k0] || k0;
    if (k === 'third-party' || k === '3p') r.party = neg ? 1 : 2;
    else if (k === 'first-party' || k === '1p') r.party = neg ? 2 : 1;
    else if (k === 'domain' || k === 'from') {
      for (let d of (v || '').split('|')) {
        d = d.trim().toLowerCase();
        if (!d) continue;
        if (d.startsWith('~')) (r.exc ??= []).push(d.slice(1)); else (r.inc ??= []).push(d);
      }
    } else if (k === 'important') r.important = true;
    else if (k === 'match-case') r.matchCase = true;
    else if (k === 'all') r.types |= ALL_BUT_DOC | BIT.document;
    else if (k === 'document') { if (neg) r.notTypes |= BIT.document; else { r.types |= BIT.document; r.docAllow = true; } }
    else if (k === 'elemhide' || k === 'ehide') r.hideAllow |= 3;
    else if (k === 'generichide' || k === 'ghide') r.hideAllow |= 1;
    else if (k === 'redirect' || k === 'rewrite-to') { /* we block instead of redirecting */ }
    else if (BIT[k] !== undefined) { if (neg) r.notTypes |= BIT[k]; else r.types |= BIT[k]; }
    else if (SKIP_OPTS.has(k) || k === 'empty' || k === 'mp4') return null;
    else return null;   // unknown option: be safe
  }
  return r;
}

class FilterEngine {
  constructor() { this.clear(); }

  clear() {
    this.hostRules = new Map();      // host -> [rule]
    this.tokenRules = new Map();     // token -> [rule]
    this.generic = [];               // rules with no usable index
    this.hostExc = new Map(); this.tokenExc = new Map(); this.genericExc = [];
    this.docAllow = [];              // @@||site^$document  — sites fully allowed
    this.hideAllow = [];             // @@||site^$elemhide / $generichide
    this.cosGeneric = new Set();     // ##.selector
    this.cosGenericExcl = new Map(); // selector -> [hosts where it must not apply]  (~a.com##.x)
    this.cosSpecific = new Map();    // host -> Set(selector)
    this.cosExc = new Map();         // host -> Set(selector)   (a.com#@#.x)
    this.networkCount = 0; this.cosmeticCount = 0;
    this._cssCache = new Map(); this._genericCss = null;
  }

  /** Adds a filter list (text). Returns the number of rules taken from it. */
  add(text) {
    let n = 0;
    for (let line of text.split(/\r?\n/)) {
      line = line.trim();
      if (!line || line[0] === '!' || line[0] === '[') continue;
      try { if (this.addLine(line)) n++; } catch (_e) { /* bad rule: skip */ }
    }
    this._cssCache.clear(); this._genericCss = null;
    return n;
  }

  addLine(line) {
    // ── cosmetic ──
    const ci = line.search(/#[@$?%]{0,2}#/);
    if (ci >= 0 && !/^\/.*\/$/.test(line)) {
      const sep = /#[@$?%]{0,2}#/.exec(line.slice(ci))[0];
      if (sep !== '##' && sep !== '#@#') return false;              // extended / scriptlet / CSS-injection syntaxes
      const domains = line.slice(0, ci).toLowerCase();
      const sel = line.slice(ci + sep.length).trim();
      if (!sel || sel.startsWith('+js(') || sel.startsWith('^') || PROCEDURAL.test(sel) || sel.includes('{')) return false;
      const ds = domains ? domains.split(',').map((d) => d.trim()).filter(Boolean) : [];
      const inc = ds.filter((d) => !d.startsWith('~'));
      const exc = ds.filter((d) => d.startsWith('~')).map((d) => d.slice(1));
      if (sep === '#@#') {
        if (!inc.length) { this.cosGeneric.delete(sel); return true; }
        for (const d of inc) { if (!this.cosExc.has(d)) this.cosExc.set(d, new Set()); this.cosExc.get(d).add(sel); }
        return true;
      }
      if (!inc.length) {
        this.cosGeneric.add(sel);
        if (exc.length) this.cosGenericExcl.set(sel, (this.cosGenericExcl.get(sel) || []).concat(exc));
      } else {
        for (const d of inc) { if (!this.cosSpecific.has(d)) this.cosSpecific.set(d, new Set()); this.cosSpecific.get(d).add(sel); }
        if (exc.length) for (const d of exc) { if (!this.cosExc.has(d)) this.cosExc.set(d, new Set()); this.cosExc.get(d).add(sel); }
      }
      this.cosmeticCount++;
      return true;
    }

    // ── hosts-file lines (0.0.0.0 ads.example.com) ──
    const hm = /^(?:0\.0\.0\.0|127\.0\.0\.1)\s+([a-z0-9.-]+)\s*(?:#.*)?$/i.exec(line);
    if (hm) { if (hm[1] === 'localhost' || hm[1] === '0.0.0.0') return false; line = '||' + hm[1].toLowerCase() + '^'; }

    // ── network ──
    let exc = false;
    if (line.startsWith('@@')) { exc = true; line = line.slice(2); }
    let pattern = line, opts = '';
    if (!/^\/.*\/$/.test(line)) {
      const di = line.lastIndexOf('$');
      if (di >= 0 && !/[/^*|]/.test(line.slice(di + 1).split(',')[0].split('=')[0])) { pattern = line.slice(0, di); opts = line.slice(di + 1); }
    }
    const o = parseOptions(opts);
    if (!o) return false;
    if (exc && o.docAllow && /^\|\|[a-z0-9.-]+\^?$/.test(pattern)) { this.docAllow.push(pattern.replace(/^\|\||\^$/g, '')); return true; }
    if (exc && o.hideAllow && /^\|\|[a-z0-9.-]+\^?$/.test(pattern)) { this.hideAllow.push({ host: pattern.replace(/^\|\||\^$/g, ''), mode: o.hideAllow }); return true; }
    if (o.hideAllow) return false;
    const rule = { exc, types: o.types || ALL_BUT_DOC, notTypes: o.notTypes, party: o.party, inc: o.inc, exc2: o.exc, important: o.important, raw: line };
    if (rule.types & BIT.document && !exc) rule.types &= ~BIT.document;   // never block whole pages from lists
    if (!rule.types) return false;

    const hostOnly = /^\|\|([a-z0-9.-]+)\^?\|?$/i.exec(pattern);
    if (hostOnly) {
      rule.host = hostOnly[1].toLowerCase();
      this._put(exc ? this.hostExc : this.hostRules, rule.host, rule);
    } else if (/^\/.*\/$/.test(pattern)) {
      if (pattern.length > 300) return false;
      rule.re = new RegExp(pattern.slice(1, -1), o.matchCase ? '' : 'i');
      (exc ? this.genericExc : this.generic).push(rule);
    } else {
      if (pattern === '' || pattern === '*' || pattern === '|' || pattern === '||') { if (!o.inc) return false; pattern = '*'; }
      rule.pattern = pattern; rule.matchCase = o.matchCase;
      const tok = pattern === '*' ? null : bestToken(pattern);
      if (tok) this._put(exc ? this.tokenExc : this.tokenRules, tok, rule);
      else (exc ? this.genericExc : this.generic).push(rule);
    }
    this.networkCount++;
    return true;
  }

  _put(map, key, rule) { const a = map.get(key); if (a) a.push(rule); else map.set(key, [rule]); }

  _ok(rule, url, typeBit, host, top, third) {
    if (!(rule.types & typeBit) || (rule.notTypes & typeBit)) return false;
    if (rule.party === 1 && third) return false;
    if (rule.party === 2 && !third) return false;
    if (rule.inc && !rule.inc.some((d) => domainMatch(top, d))) return false;
    if (rule.exc2 && rule.exc2.some((d) => domainMatch(top, d))) return false;
    if (rule.host) return host === rule.host || host.endsWith('.' + rule.host);
    if (!rule.re) rule.re = patternToRegex(rule.pattern, rule.matchCase);
    return rule.re.test(url);
  }

  _find(hostMap, tokMap, generic, url, typeBit, host, top, third, tokens, importantOnly) {
    for (let h = host; h; ) {
      const a = hostMap.get(h);
      if (a) for (const r of a) if ((!importantOnly || r.important) && this._ok(r, url, typeBit, host, top, third)) return r;
      const i = h.indexOf('.');
      h = i < 0 ? '' : h.slice(i + 1);
    }
    for (const t of tokens) {
      const a = tokMap.get(t);
      if (a) for (const r of a) if ((!importantOnly || r.important) && this._ok(r, url, typeBit, host, top, third)) return r;
    }
    for (const r of generic) if ((!importantOnly || r.important) && this._ok(r, url, typeBit, host, top, third)) return r;
    return null;
  }

  /** Is the whole site allowed (@@||site^$document)? */
  siteAllowed(topHost) { return this.docAllow.some((d) => domainMatch(topHost, d)); }

  /**
   * Should this request be blocked?  url: request URL; type: Electron resourceType; topUrl: the page's URL.
   * Returns the matching rule's text, or null.
   */
  match(url, type, topUrl) {
    if (!/^(https?|wss?):/i.test(url)) return null;
    const typeName = TYPE_OF[type] || 'other';
    if (typeName === 'document') return null;
    const typeBit = BIT[typeName];
    const host = hostOf(url);
    const top = hostOf(topUrl || '') || host;
    if (this.docAllow.length && this.siteAllowed(top)) return null;
    const third = siteOf(host) !== siteOf(top);
    const tokens = new Set(url.toLowerCase().match(/[a-z0-9%]{2,}/g) || []);
    const imp = this._find(this.hostRules, this.tokenRules, this.generic, url, typeBit, host, top, third, tokens, true);
    if (imp) return imp.raw;
    const hit = this._find(this.hostRules, this.tokenRules, this.generic, url, typeBit, host, top, third, tokens, false);
    if (!hit) return null;
    if (this._find(this.hostExc, this.tokenExc, this.genericExc, url, typeBit, host, top, third, tokens, false)) return null;
    return hit.raw;
  }

  /** CSS that hides ad boxes on a page of this host. */
  cosmeticCss(host) {
    host = (host || '').toLowerCase();
    if (this._cssCache.has(host)) return this._cssCache.get(host);
    let mode = 0;
    for (const h of this.hideAllow) if (domainMatch(host, h.host)) mode |= h.mode;
    const excluded = new Set();
    const specific = [];
    for (let h = host; h; ) {
      const e = this.cosExc.get(h); if (e) for (const s of e) excluded.add(s);
      const sp = this.cosSpecific.get(h); if (sp) for (const s of sp) specific.push(s);
      const i = h.indexOf('.');
      h = i < 0 ? '' : h.slice(i + 1);
    }
    // "example.*" entries
    for (const [d, set] of this.cosSpecific) if (d.endsWith('.*') && domainMatch(host, d)) for (const s of set) specific.push(s);
    const rule = (s) => s + '{display:none!important}';
    let css = '';
    if (!(mode & 2)) {
      const sp = specific.filter((s) => !excluded.has(s)).map(rule).join('\n');
      if (!(mode & 1)) {
        let generic;
        let needsFilter = excluded.size > 0;
        if (!needsFilter) for (const [, hosts] of this.cosGenericExcl) if (hosts.some((d) => domainMatch(host, d))) { needsFilter = true; break; }
        if (!needsFilter) generic = this._genericCss ??= [...this.cosGeneric].map(rule).join('\n');
        else generic = [...this.cosGeneric].filter((s) => !excluded.has(s) && !(this.cosGenericExcl.get(s) || []).some((d) => domainMatch(host, d))).map(rule).join('\n');
        css = generic + (sp ? '\n' + sp : '');
      } else css = sp;
    }
    if (this._cssCache.size > 300) this._cssCache.clear();
    this._cssCache.set(host, css);
    return css;
  }

  stats() { return { network: this.networkCount, cosmetic: this.cosmeticCount }; }
}

module.exports = { FilterEngine, hostOf, siteOf, patternToRegex, bestToken };
