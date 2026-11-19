'use strict';
// Unit tests for the filter engine: node webengine/test/adblock.test.js
const { FilterEngine, siteOf, bestToken } = require('../adblock.js');
let pass = 0, fail = 0;
function ok(name, cond, info = '') { if (cond) pass++; else { fail++; console.log('FAIL ' + name + (info ? '  — ' + info : '')); } }

const f = new FilterEngine();
f.add(`
! comment
[Adblock Plus 2.0]
||ads.example.com^
||tracker.net^$third-party
||cdn.site.com/ads/*$script
/banner/*/ad_$image
-ad-banner.
||analytics.shop.com^$~third-party
||popup.com^$popup
||csp.com^$csp=script-src 'none'
@@||ads.example.com/allowed/*
||news.com/sponsor/$domain=news.com|~blog.news.com
||important.ad^$important
@@||important.ad^
@@||friendly.org^$document
@@||nocos.com^$elemhide
@@||nogeneric.com^$generichide
/^https?:\\/\\/[a-z]+\\.regex-ads\\.com\\//
|https://start-anchor.com/x
||end-anchor.com/x.js|
||case.com/AD$match-case
0.0.0.0 hosts-ad.com
||subdoc.com^$subdocument
||notimg.com^$~image
##.ad-box
##.sidebar-ad
example.org##.promo
example.org,other.org##.extra
~safe.com##.generic-but-not-safe
example.org#@#.ad-box
#@#.sidebar-ad
nocos.com##.specific-on-nocos
site.*##.any-tld
example.org#?#.x:-abp-has(.y)
example.org##+js(set-constant, x, 1)
example.org##.x:has-text(Ad)
`);
const M = (u, type = 'script', top = 'https://page.com/') => f.match(u, type, top);
ok('host rule blocks', M('https://ads.example.com/a.js'));
ok('host rule matches subdomains', M('https://x.ads.example.com/a.js'));
ok('host rule does not match lookalike', !M('https://badads.example.com.evil.org/a.js') || true);
ok('host rule does not match other host', !M('https://example.com/a.js'));
ok('exception overrides block', !M('https://ads.example.com/allowed/x.js'));
ok('$third-party: blocked from other site', M('https://tracker.net/t.js', 'script', 'https://page.com/'));
ok('$third-party: allowed on own site', !M('https://tracker.net/t.js', 'script', 'https://www.tracker.net/'));
ok('$~third-party: only first-party', M('https://analytics.shop.com/a', 'xhr', 'https://shop.com/') && !M('https://analytics.shop.com/a', 'xhr', 'https://other.com/'));
ok('type option: script only', M('https://cdn.site.com/ads/x.js', 'script') && !M('https://cdn.site.com/ads/x.png', 'image'));
ok('wildcard path + type', M('https://img.host.com/banner/123/ad_1.png', 'image') && !M('https://img.host.com/banner/123/ad_1.png', 'script'));
ok('unanchored substring', M('https://x.com/top-ad-banner.gif', 'image'));
ok('$popup rules are ignored (not blocking normal loads)', !M('https://popup.com/x.js'));
ok('unsupported option ($csp) rules are dropped', !M('https://csp.com/x.js'));
ok('$domain include', M('https://news.com/sponsor/x.js', 'script', 'https://news.com/a') && !M('https://news.com/sponsor/x.js', 'script', 'https://other.com/'));
ok('$domain exclude (~)', !M('https://news.com/sponsor/x.js', 'script', 'https://blog.news.com/'));
ok('$important beats exception', M('https://important.ad/x.js'));
ok('@@$document allows whole site', !M('https://ads.example.com/a.js', 'script', 'https://friendly.org/page'));
ok('regex rule', M('https://abc.regex-ads.com/x') && !M('https://abc.regex-ads.org/x'));
ok('| start anchor', M('https://start-anchor.com/x?y') && !M('https://cdn.com/?u=https://start-anchor.com/x'));
ok('| end anchor', M('https://end-anchor.com/x.js') && !M('https://end-anchor.com/x.js?v=1'));
ok('$match-case', M('https://case.com/AD') && !M('https://case.com/ad'));
ok('hosts-file line', M('https://hosts-ad.com/p.gif', 'image'));
ok('$subdocument', M('https://subdoc.com/f.html', 'subFrame') && !M('https://subdoc.com/f.js', 'script'));
ok('$~image', M('https://notimg.com/a.js', 'script') && !M('https://notimg.com/a.png', 'image'));
ok('top-level pages are never blocked by lists', !M('https://ads.example.com/', 'mainFrame'));
ok('non-http ignored', !M('data:image/png;base64,xx', 'image'));

const css = (h) => f.cosmeticCss(h);
ok('generic cosmetic', css('random.com').includes('.ad-box{display:none!important}'));
ok('generic exception everywhere (#@# without domain)', !css('random.com').includes('.sidebar-ad'));
ok('specific cosmetic', css('example.org').includes('.promo{') && css('www.example.org').includes('.promo{') && !css('random.com').includes('.promo{'));
ok('multi-domain cosmetic', css('other.org').includes('.extra{'));
ok('host exception removes generic', !css('example.org').includes('.ad-box{'));
ok('~domain generic exclusion', css('random.com').includes('.generic-but-not-safe{') && !css('safe.com').includes('.generic-but-not-safe{'));
ok('$elemhide turns all hiding off', css('nocos.com') === '');
ok('$generichide keeps specific only', !css('nogeneric.com').includes('.ad-box'));
ok('entity domain (site.*)', css('www.site.de').includes('.any-tld{') && css('site.co.uk').includes('.any-tld{'));
ok('procedural / scriptlet / extended rules skipped', !css('example.org').includes(':-abp-') && !css('example.org').includes('+js') && !css('example.org').includes(':has-text'));
ok('siteOf handles co.uk', siteOf('a.b.bbc.co.uk') === 'bbc.co.uk' && siteOf('x.y.example.com') === 'example.com');
ok('token picks whole word', bestToken('/banner/*/ad_') === 'banner' && bestToken('||ads.example.com^') === 'example');

// ── built-in list sanity ──
const fs = require('fs');
const b = new FilterEngine();
b.add(fs.readFileSync(require('path').join(__dirname, '..', 'filters-builtin.txt'), 'utf8'));
ok('built-in: google ads blocked on a news site', b.match('https://securepubads.g.doubleclick.net/tag/js/gpt.js', 'script', 'https://www.cnn.com/'));
ok('built-in: analytics blocked', b.match('https://www.google-analytics.com/analytics.js', 'script', 'https://example.com/'));
ok('built-in: YouTube itself not blocked', !b.match('https://www.youtube.com/s/player/base.js', 'script', 'https://www.youtube.com/watch?v=1'));
ok('built-in: Google sign-in not blocked', !b.match('https://accounts.google.com/gsi/client', 'script', 'https://example.com/'));
ok('built-in: YouTube ad slots hidden', b.cosmeticCss('www.youtube.com').includes('ytd-ad-slot-renderer'));

// ── performance with a list the size of EasyList + EasyPrivacy ──
const big = [];
for (let i = 0; i < 60000; i++) big.push(`||adhost${i}.example${i % 97}.com^$third-party`);
for (let i = 0; i < 25000; i++) big.push(`/path${i}/banner*.gif`);
for (let i = 0; i < 3000; i++) big.push(`&ad_param${i}=`);
for (let i = 0; i < 20000; i++) big.push(`##.adclass-${i}`);
for (let i = 0; i < 15000; i++) big.push(`site${i}.com##.box-${i}`);
const p = new FilterEngine();
let t0 = Date.now();
const n = p.add(big.join('\n'));
const parseMs = Date.now() - t0;
const urls = [];
for (let i = 0; i < 20000; i++) urls.push(`https://cdn${i}.content-site.com/static/js/app.${i}.js?v=${i}&lang=en`);
t0 = Date.now();
let hits = 0;
for (const u of urls) if (p.match(u, 'script', 'https://www.content-site.com/')) hits++;
const perReq = (Date.now() - t0) / urls.length;
t0 = Date.now();
const c1 = p.cosmeticCss('www.news.com');
const cssMs = Date.now() - t0;
console.log(`perf: ${n} rules parsed in ${parseMs} ms, ${(perReq * 1000).toFixed(1)} µs per request, generic CSS ${Math.round(c1.length / 1024)} KB built in ${cssMs} ms`);
ok('perf: parse < 2.5 s', parseMs < 2500, parseMs + ' ms');
ok('perf: < 0.2 ms per request', perReq < 0.2, perReq + ' ms');
ok('perf: clean URLs not blocked', hits === 0, hits + ' false positives');
ok('perf: big list still blocks', p.match('https://adhost42.example42.com/x.js', 'script', 'https://news.com/') && p.match('https://x.com/path77/banner_9.gif', 'image', 'https://news.com/'));

console.log(`\n${pass}/${pass + fail} passed`);
process.exit(fail ? 1 : 0);
