// SPDX-License-Identifier: GPL-3.0-or-later
// Red Star Online's reader view: picks the main article (paragraph-density scoring, like a small Readability) and
// returns its clean HTML and title as JSON. Scripts, forms, ads and every attribute but links, sources and sizes go.
(function () {
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
  return JSON.stringify({ title: title, html: c.innerHTML });
})()
