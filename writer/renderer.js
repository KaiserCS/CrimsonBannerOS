'use strict';
(() => {
  const $ = (s) => document.querySelector(s);
  const ed = $('#editor'), scroller = $('#scroller');
  const tabsEl = $('#tabs');
  let tabs = [], cur = null, nextId = 1, zoom = 100, quitting = false;

  // ------------------------------------------------------------ helpers
  const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  let toastT;
  function toast(msg) { const t = $('#toast'); t.textContent = msg; t.hidden = false; clearTimeout(toastT); toastT = setTimeout(() => (t.hidden = true), 2600); }
  const extOf = (p) => (p && p.includes('.') ? p.split('.').pop().toLowerCase() : '');
  const baseName = (p) => p.split(/[\\/]/).pop();

  function sanitize(html) {
    const doc = new DOMParser().parseFromString(html, 'text/html');
    doc.querySelectorAll('script,iframe,object,embed,link,meta,style,base,form').forEach((n) => n.remove());
    doc.querySelectorAll('*').forEach((n) => {
      for (const a of [...n.attributes]) {
        if (/^on/i.test(a.name) || (/^(href|src)$/i.test(a.name) && /^\s*javascript:/i.test(a.value))) n.removeAttribute(a.name);
      }
    });
    return doc.body.innerHTML;
  }
  function textToHtml(t) {
    return t.replace(/\r\n?/g, '\n').split('\n').map((l) => `<p>${l ? esc(l).replace(/\t/g, '&emsp;') : '<br>'}</p>`).join('');
  }
  function mdToHtml(md) {
    const inline = (s) => esc(s).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>')
      .replace(/(^|[^*])\*([^*]+)\*/g, '$1<i>$2</i>').replace(/\[([^\]]+)\]\((https?:[^)]+)\)/g, '<a href="$2">$1</a>');
    const out = []; let list = null, code = false, buf = [];
    const close = () => { if (list) { out.push(`</${list}>`); list = null; } };
    for (const line of md.replace(/\r\n?/g, '\n').split('\n')) {
      if (/^```/.test(line)) { if (code) { out.push(`<pre>${esc(buf.join('\n'))}</pre>`); buf = []; } code = !code; continue; }
      if (code) { buf.push(line); continue; }
      let m;
      if ((m = /^(#{1,6})\s+(.*)$/.exec(line))) { close(); out.push(`<h${m[1].length}>${inline(m[2])}</h${m[1].length}>`); }
      else if ((m = /^\s*[-*+]\s+(.*)$/.exec(line))) { if (list !== 'ul') { close(); out.push('<ul>'); list = 'ul'; } out.push(`<li>${inline(m[1])}</li>`); }
      else if ((m = /^\s*\d+\.\s+(.*)$/.exec(line))) { if (list !== 'ol') { close(); out.push('<ol>'); list = 'ol'; } out.push(`<li>${inline(m[1])}</li>`); }
      else if ((m = /^>\s?(.*)$/.exec(line))) { close(); out.push(`<blockquote>${inline(m[1])}</blockquote>`); }
      else if (/^---+$/.test(line)) { close(); out.push('<hr>'); }
      else if (!line.trim()) { close(); }
      else { close(); out.push(`<p>${inline(line)}</p>`); }
    }
    close();
    return out.join('') || '<p><br></p>';
  }
  function htmlToMd(root) {
    let out = '';
    const inl = (n) => {
      if (n.nodeType === 3) return n.textContent;
      if (n.nodeType !== 1) return '';
      const inner = [...n.childNodes].map(inl).join('');
      switch (n.tagName) {
        case 'B': case 'STRONG': return `**${inner}**`;
        case 'I': case 'EM': return `*${inner}*`;
        case 'CODE': return `\`${inner}\``;
        case 'A': return `[${inner}](${n.getAttribute('href') || ''})`;
        case 'BR': return '\n';
        default: return inner;
      }
    };
    for (const n of root.children) {
      const t = n.tagName;
      if (/^H[1-6]$/.test(t)) out += '#'.repeat(+t[1]) + ' ' + inl(n) + '\n\n';
      else if (t === 'UL' || t === 'OL') { let i = 1; for (const li of n.children) out += (t === 'UL' ? '- ' : `${i++}. `) + inl(li) + '\n'; out += '\n'; }
      else if (t === 'BLOCKQUOTE') out += '> ' + inl(n) + '\n\n';
      else if (t === 'PRE') out += '```\n' + n.textContent + '\n```\n\n';
      else if (t === 'HR') out += '---\n\n';
      else out += inl(n) + '\n\n';
    }
    return out.trim() + '\n';
  }

  // ------------------------------------------------------------ tabs
  function newTab(o) {
    const t = { id: nextId++, name: 'Untitled', path: null, html: '<p><br></p>', dirty: false, scroll: 0, caret: null, ...o };
    tabs.push(t); return t;
  }
  function captureState() {
    if (!cur) return;
    cur.html = ed.innerHTML; cur.scroll = scroller.scrollTop; cur.caret = getCaret();
  }
  function switchTo(t) {
    if (cur === t) return;
    captureState();
    cur = t; ed.innerHTML = t.html;
    renderTabs(); updateStats(); updateSaved();
    document.title = (t.dirty ? '• ' : '') + t.name + ' - Red Star Writer';
    requestAnimationFrame(() => { scroller.scrollTop = t.scroll || 0; ed.focus(); setCaret(t.caret); });
  }
  function renderTabs() {
    tabsEl.innerHTML = '';
    for (const t of tabs) {
      const el = document.createElement('div');
      el.className = 'tab' + (t === cur ? ' active' : '') + (t.dirty ? ' dirty' : '');
      el.title = t.path || t.name;
      el.innerHTML = `<span class="dot"></span><span class="nm">${esc(t.name)}</span><span class="x" title="Close">✕</span>`;
      el.onmousedown = (e) => { if (e.button === 1) { e.preventDefault(); closeTab(t); } };
      el.onclick = (e) => { if (e.target.classList.contains('x')) closeTab(t); else switchTo(t); };
      tabsEl.appendChild(el);
    }
    document.title = cur ? (cur.dirty ? '• ' : '') + cur.name + ' - Red Star Writer' : 'Red Star Writer';
  }
  function markDirty() { if (cur && !cur.dirty) { cur.dirty = true; renderTabs(); } updateSaved(); }
  function updateSaved() { $('#saved').textContent = cur ? (cur.dirty ? 'Unsaved changes' : (cur.path ? 'Saved' : '')) : ''; }

  async function closeTab(t) {
    if (t === cur) captureState();
    if (t.dirty) {
      const r = await window.writer.confirm({ message: `Save changes to "${t.name}"?`, detail: 'Your changes will be lost if you do not save them.', buttons: ['Save', "Don't save", 'Cancel'] });
      if (r === 2) return false;
      if (r === 0 && !(await saveTab(t, false))) return false;
    }
    const i = tabs.indexOf(t); tabs.splice(i, 1);
    if (!tabs.length) { cur = null; const n = newTab(); switchTo(n); }
    else if (t === cur) { cur = null; switchTo(tabs[Math.min(i, tabs.length - 1)]); }
    else renderTabs();
    return true;
  }

  // caret persistence (character offsets inside the editor)
  function offsetOf(node, off) {
    const r = document.createRange(); r.selectNodeContents(ed); r.setEnd(node, off); return r.toString().length;
  }
  function getCaret() {
    const s = getSelection();
    if (!s.rangeCount || !ed.contains(s.anchorNode)) return null;
    return { a: offsetOf(s.anchorNode, s.anchorOffset), f: offsetOf(s.focusNode, s.focusOffset) };
  }
  function pointAt(n) {
    const w = document.createTreeWalker(ed, NodeFilter.SHOW_TEXT); let node, acc = 0, last = null;
    while ((node = w.nextNode())) { const l = node.textContent.length; if (acc + l >= n) return [node, n - acc]; acc += l; last = node; }
    return last ? [last, last.textContent.length] : [ed, 0];
  }
  function setCaret(c) {
    if (!c) return;
    try { const [an, ao] = pointAt(c.a), [fn, fo] = pointAt(c.f); const s = getSelection(); s.setBaseAndExtent(an, ao, fn, fo); } catch { /* ignore */ }
  }

  // ------------------------------------------------------------ file ops
  async function openData(d) {
    if (d.error) { toast('Could not open ' + baseName(d.path) + ': ' + d.error); return; }
    const existing = tabs.find((t) => t.path === d.path);
    if (existing) { switchTo(existing); return; }
    let html;
    try {
      if (d.ext === 'docx') { const bin = Uint8Array.from(atob(d.base64), (c) => c.charCodeAt(0)); html = sanitize(await window.RSDocx.importDocx(bin)); }
      else if (d.ext === 'html' || d.ext === 'htm') html = sanitize(d.text) || '<p><br></p>';
      else if (d.ext === 'md') html = mdToHtml(d.text);
      else html = textToHtml(d.text);
    } catch (e) { toast('Could not read ' + d.name + ': ' + e.message); return; }
    const onlyBlank = tabs.length === 1 && !tabs[0].dirty && !tabs[0].path && ed.textContent.trim() === '';
    const t = newTab({ name: d.name, path: d.path, html });
    if (onlyBlank) { tabs.splice(tabs.indexOf(tabs[0]), 1); cur = null; }
    switchTo(t);
  }
  async function openDialog() { for (const d of await window.writer.openDialog()) await openData(d); }
  async function openPaths(paths) { for (const p of paths) await openData(await window.writer.readFile(p)); }

  async function serialize(t, ext) {
    const holder = t === cur ? ed : (() => { const h = document.createElement('div'); h.innerHTML = t.html; return h; })();
    if (ext === 'docx') {
      const wasOff = holder !== ed;
      if (wasOff) { holder.style.cssText = 'position:absolute;left:-9999px;width:600px'; holder.className = ''; document.body.appendChild(holder); }
      try {
        const bytes = await window.RSDocx.exportDocx(holder, { page: $('#paper').value, pageNumbers: $('#pageNums').checked });
        let s = ''; for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
        return { base64: btoa(s) };
      } finally { if (wasOff) holder.remove(); }
    }
    if (ext === 'html' || ext === 'htm') return { text: `<!doctype html>\n<html><head><meta charset="utf-8"><title>${esc(t.name)}</title><style>body{font-family:Arial,sans-serif;max-width:800px;margin:40px auto;line-height:1.5}table{border-collapse:collapse}td{border:1px solid #999;padding:4px 8px}</style></head><body>\n${holder.innerHTML}\n</body></html>` };
    if (ext === 'md') return { text: htmlToMd(holder) };
    const plain = (holder === ed ? holder.innerText : holder.textContent).replace(/\u00a0/g, ' ');
    return { text: plain.replace(/\n{3,}/g, '\n\n').trimEnd() + '\n' };
  }
  async function saveTab(t, forceAs) {
    if (t === cur) captureState();
    let p = t.path;
    if (!p || forceAs) {
      const base = t.name.replace(/\.[^.]+$/, '') || 'Untitled';
      p = await window.writer.saveDialog(base + '.docx');
      if (!p) return false;
      if (!extOf(p)) p += '.docx';
    }
    const ext = extOf(p);
    if (ext === 'txt' && !forceAs && !t.warnedTxt && /<(b|i|u|table|img|h\d|ul|ol)\b|style=/.test(t.html)) {
      const r = await window.writer.confirm({ message: 'Plain text drops all formatting.', detail: 'Save as .docx to keep fonts, tables and pictures.', buttons: ['Save as text anyway', 'Cancel'] });
      if (r !== 0) return false; t.warnedTxt = true;
    }
    let payload;
    try { payload = await serialize(t, ext); } catch (e) { toast('Could not prepare file: ' + e.message); return false; }
    const r = await window.writer.writeFile(p, payload);
    if (!r.ok) { toast('Save failed: ' + r.error); return false; }
    t.path = p; t.name = baseName(p); t.dirty = false;
    renderTabs(); updateSaved(); toast('Saved ' + t.name);
    return true;
  }

  // ------------------------------------------------------------ recovery
  async function writeRecovery() {
    if (quitting) return;
    captureState();
    const dirty = tabs.filter((t) => t.dirty);
    if (!dirty.length) { await window.writer.clearRecovery(); return; }
    await window.writer.saveRecovery({ tabs: dirty.map((t) => ({ name: t.name, path: t.path, html: t.html })) });
  }
  setInterval(writeRecovery, 4000);

  // ------------------------------------------------------------ formatting
  const ex = (cmd, val) => { ed.focus(); document.execCommand(cmd, false, val); };
  function applyFontSize(pt) {
    ed.focus();
    document.execCommand('styleWithCSS', false, false);
    document.execCommand('fontSize', false, '7');
    for (const f of ed.querySelectorAll('font[size="7"]')) { const s = document.createElement('span'); s.style.fontSize = pt + 'pt'; s.innerHTML = f.innerHTML; f.replaceWith(s); }
    document.execCommand('styleWithCSS', false, true);
    markDirty(); updateStats();
  }
  function setLineSpacing(v) {
    const s = getSelection(); if (!s.rangeCount || !v) return;
    const blocks = new Set();
    const r = s.getRangeAt(0);
    const climb = (n) => { while (n && n !== ed) { if (n.nodeType === 1 && /^(P|DIV|H[1-6]|LI|BLOCKQUOTE|PRE)$/.test(n.tagName)) return n; n = n.parentNode; } return null; };
    const w = document.createTreeWalker(ed, NodeFilter.SHOW_ELEMENT);
    let n; while ((n = w.nextNode())) if (/^(P|DIV|H[1-6]|LI|BLOCKQUOTE|PRE)$/.test(n.tagName) && r.intersectsNode(n)) blocks.add(n);
    const a = climb(r.startContainer); if (a) blocks.add(a);
    blocks.forEach((b) => (b.style.lineHeight = v)); markDirty();
  }
  function insertHtml(h) { ed.focus(); document.execCommand('insertHTML', false, h); }

  function refreshToolbar() {
    for (const b of document.querySelectorAll('[data-ex]')) {
      const c = b.dataset.ex; if (!/^(bold|italic|underline|strikeThrough|superscript|subscript|justify\w+|insertUnorderedList|insertOrderedList)$/.test(c)) continue;
      let on = false; try { on = document.queryCommandState(c); } catch { /* ignore */ }
      b.classList.toggle('on', on);
    }
    const s = getSelection();
    if (s.rangeCount && ed.contains(s.anchorNode)) {
      const el = s.anchorNode.nodeType === 1 ? s.anchorNode : s.anchorNode.parentElement;
      const cs = getComputedStyle(el);
      const pt = Math.round(parseFloat(cs.fontSize) * 0.75);
      const sz = $('#size'); if (![...sz.options].some((o) => +o.value === pt)) { const o = new Option(pt, pt); sz.add(o); } sz.value = pt;
      const fam = cs.fontFamily.split(',')[0].replace(/["']/g, '').trim();
      const f = $('#font'); if (![...f.options].some((o) => o.value === fam)) f.add(new Option(fam, fam)); f.value = fam;
      const blk = el.closest('h1,h2,h3,blockquote,pre'); $('#style').value = blk ? blk.tagName.toLowerCase() : 'p';
    }
  }
  document.addEventListener('selectionchange', refreshToolbar);

  document.querySelectorAll('[data-ex]').forEach((b) => {
    b.addEventListener('mousedown', (e) => e.preventDefault());
    b.addEventListener('click', () => { ex(b.dataset.ex); markDirty(); });
  });
  $('#style').onchange = (e) => { ex('formatBlock', e.target.value === 'p' ? 'p' : e.target.value); markDirty(); };
  $('#font').onchange = (e) => { ex('fontName', e.target.value); markDirty(); };
  $('#size').onchange = (e) => applyFontSize(+e.target.value);
  $('#spacing').onchange = (e) => { setLineSpacing(e.target.value); e.target.value = ''; };
  $('#fore').addEventListener('input', (e) => { ex('foreColor', e.target.value); markDirty(); });
  $('#back').addEventListener('input', (e) => { ex('hiliteColor', e.target.value); markDirty(); });

  // ------------------------------------------------------------ commands
  // Electron has no window.prompt(), so use a small inline dialog.
  function ask(label, def) {
    return new Promise((resolve) => {
      const range = getSelection().rangeCount ? getSelection().getRangeAt(0).cloneRange() : null;
      const back = document.createElement('div');
      back.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.45);display:flex;align-items:center;justify-content:center;z-index:99';
      back.innerHTML = '<div style="background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:16px;min-width:320px;display:flex;flex-direction:column;gap:10px"><label></label><input type="text" style="width:100%"><div style="display:flex;gap:8px;justify-content:flex-end"><button class="ok on">OK</button><button class="no">Cancel</button></div></div>';
      back.querySelector('label').textContent = label;
      const inp = back.querySelector('input'); inp.value = def || '';
      const done = (v) => { back.remove(); ed.focus(); if (range) { const s = getSelection(); s.removeAllRanges(); s.addRange(range); } resolve(v); };
      back.querySelector('.ok').onclick = () => done(inp.value);
      back.querySelector('.no').onclick = () => done(null);
      inp.onkeydown = (e) => { if (e.key === 'Enter') done(inp.value); else if (e.key === 'Escape') done(null); e.stopPropagation(); };
      document.body.appendChild(back); inp.focus(); inp.select();
    });
  }
  async function insertLink() {
    const sel = getSelection().toString();
    const url = await ask('Link address', 'https://');
    if (!url) return;
    if (sel) ex('createLink', url); else insertHtml(`<a href="${esc(url)}">${esc(url)}</a>`);
    markDirty();
  }
  async function insertTable() {
    const rc = await ask('Table size (rows x columns)', '3x3'); if (!rc) return;
    const m = /(\d+)\s*[x×,]\s*(\d+)/i.exec(rc); if (!m) return;
    const r = Math.min(50, +m[1]), c = Math.min(20, +m[2]);
    insertHtml('<table>' + ('<tr>' + '<td><br></td>'.repeat(c) + '</tr>').repeat(r) + '</table><p><br></p>'); markDirty();
  }
  function insertImage(file) {
    const rd = new FileReader();
    rd.onload = () => { insertHtml(`<img src="${rd.result}">`); markDirty(); };
    rd.readAsDataURL(file);
  }
  const commands = {
    new: () => { switchTo(newTab()); },
    open: openDialog,
    save: () => cur && saveTab(cur, false),
    saveas: () => cur && saveTab(cur, true),
    pdf: async () => {
      captureState();
      const r = await window.writer.exportPdf({ name: (cur.name.replace(/\.[^.]+$/, '') || 'Document') + '.pdf', page: $('#paper').value });
      if (r.ok) toast('Exported PDF'); else if (!r.canceled) toast('PDF failed: ' + r.error);
    },
    print: () => window.writer.print(),
    link: insertLink,
    image: () => $('#imgPick').click(),
    table: insertTable,
    hr: () => { insertHtml('<hr><p><br></p>'); markDirty(); },
    pagebreak: () => { insertHtml('<div class="pb" contenteditable="false"></div><p><br></p>'); markDirty(); },
    find: () => toggleFind(true),
    view: () => { document.body.classList.toggle('web'); $('#viewBtn').classList.toggle('on', document.body.classList.contains('web')); },
  };
  document.querySelectorAll('[data-cmd]').forEach((b) => { b.onclick = () => commands[b.dataset.cmd](); });
  $('#newTab').onclick = commands.new;
  $('#imgPick').onchange = (e) => { if (e.target.files[0]) insertImage(e.target.files[0]); e.target.value = ''; };

  // paper size drives the print stylesheet and page view
  function applyPaper() {
    const p = $('#paper').value;
    document.body.classList.toggle('letter', p === 'Letter');
    $('#pageStyle').textContent = `@page { size: ${p === 'Letter' ? 'letter' : 'A4'}; margin: 25.4mm; }`;
  }
  $('#paper').onchange = applyPaper;

  // ------------------------------------------------------------ zoom
  function setZoom(z) {
    zoom = Math.max(50, Math.min(250, Math.round(z)));
    $('#paperwrap').style.zoom = zoom / 100; $('#zoom').value = zoom; $('#zoomLbl').textContent = zoom + '%';
  }
  $('#zoom').oninput = (e) => setZoom(+e.target.value);
  $('#zoomIn').onclick = () => setZoom(zoom + 10); $('#zoomOut').onclick = () => setZoom(zoom - 10);
  scroller.addEventListener('wheel', (e) => { if (e.ctrlKey) { e.preventDefault(); setZoom(zoom + (e.deltaY < 0 ? 10 : -10)); } }, { passive: false });

  // ------------------------------------------------------------ stats
  let statT;
  function updateStats() {
    const t = ed.innerText.replace(/​/g, '');
    const words = (t.trim().match(/\S+/g) || []).length;
    $('#words').textContent = words + (words === 1 ? ' word' : ' words');
    $('#chars').textContent = t.replace(/\n/g, '').length + ' characters';
  }
  ed.addEventListener('input', () => { markDirty(); clearTimeout(statT); statT = setTimeout(updateStats, 150); });
  ed.addEventListener('paste', (e) => {
    const items = e.clipboardData && e.clipboardData.files;
    if (items && items.length && items[0].type.startsWith('image/')) { e.preventDefault(); insertImage(items[0]); return; }
    const html = e.clipboardData && e.clipboardData.getData('text/html');
    if (html) { e.preventDefault(); insertHtml(sanitize(html)); markDirty(); }
  });
  ed.addEventListener('drop', async (e) => {
    const f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
    if (f && f.type.startsWith('image/')) { e.preventDefault(); insertImage(f); }
  });

  // ------------------------------------------------------------ find / replace
  let hits = [], hitIdx = -1;
  function clearHighlights() { if (window.CSS && CSS.highlights) { CSS.highlights.delete('find'); CSS.highlights.delete('findcur'); } }
  function runFind(keep) {
    clearHighlights(); hits = [];
    const q = $('#findIn').value;
    if (!q) { $('#findCount').textContent = ''; return; }
    const cs = $('#findCase').checked, needle = cs ? q : q.toLowerCase();
    const w = document.createTreeWalker(ed, NodeFilter.SHOW_TEXT); let n;
    while ((n = w.nextNode())) {
      const txt = cs ? n.textContent : n.textContent.toLowerCase(); let i = 0;
      while ((i = txt.indexOf(needle, i)) >= 0) { const r = document.createRange(); r.setStart(n, i); r.setEnd(n, i + needle.length); hits.push(r); i += needle.length; }
    }
    if (!keep || hitIdx >= hits.length) hitIdx = hits.length ? 0 : -1;
    paintHits();
  }
  function paintHits() {
    if (window.CSS && CSS.highlights && window.Highlight) {
      CSS.highlights.set('find', new Highlight(...hits));
      if (hits[hitIdx]) CSS.highlights.set('findcur', new Highlight(hits[hitIdx]));
    }
    $('#findCount').textContent = hits.length ? `${hitIdx + 1} / ${hits.length}` : 'No matches';
    if (hits[hitIdx]) { const r = hits[hitIdx].getBoundingClientRect(), s = scroller.getBoundingClientRect(); if (r.top < s.top + 40 || r.bottom > s.bottom - 40) scroller.scrollTop += r.top - s.top - s.height / 3; }
  }
  function step(d) { if (!hits.length) return runFind(); hitIdx = (hitIdx + d + hits.length) % hits.length; paintHits(); }
  function replaceAt(i, text) { const r = hits[i]; r.deleteContents(); r.insertNode(document.createTextNode(text)); }
  function toggleFind(show) {
    const b = $('#findbar'); b.hidden = !show;
    if (show) { const s = getSelection().toString(); if (s && !s.includes('\n')) $('#findIn').value = s; $('#findIn').focus(); $('#findIn').select(); runFind(); }
    else { clearHighlights(); ed.focus(); }
  }
  $('#findIn').oninput = () => runFind(); $('#findCase').onchange = () => runFind();
  $('#findIn').onkeydown = (e) => { if (e.key === 'Enter') step(e.shiftKey ? -1 : 1); if (e.key === 'Escape') toggleFind(false); };
  $('#findNext').onclick = () => step(1); $('#findPrev').onclick = () => step(-1); $('#findClose').onclick = () => toggleFind(false);
  $('#replOne').onclick = () => { if (hitIdx < 0) return runFind(); replaceAt(hitIdx, $('#replIn').value); markDirty(); runFind(true); };
  $('#replAll').onclick = () => { runFind(); const n = hits.length; for (let i = hits.length - 1; i >= 0; i--) replaceAt(i, $('#replIn').value); if (n) markDirty(); runFind(); toast(`Replaced ${n}`); };

  // ------------------------------------------------------------ keys
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Tab' && !e.ctrlKey && e.target === ed) {
      const li = getSelection().anchorNode && (getSelection().anchorNode.nodeType === 1 ? getSelection().anchorNode : getSelection().anchorNode.parentElement).closest('li');
      e.preventDefault(); if (li) ex(e.shiftKey ? 'outdent' : 'indent'); else ex('insertText', '\t'); return;
    }
    if (!e.ctrlKey && !e.metaKey) return;
    const k = e.key.toLowerCase();
    const run = (f) => { e.preventDefault(); f(); };
    if (k === 's') run(() => commands[e.shiftKey ? 'saveas' : 'save']());
    else if (k === 'o') run(commands.open);
    else if (k === 'n') run(commands.new);
    else if (k === 'w') run(() => closeTab(cur));
    else if (k === 'p') run(commands.print);
    else if (k === 'f' || k === 'h') run(() => toggleFind(true));
    else if (k === 'k') run(insertLink);
    else if (k === '=' || k === '+') run(() => setZoom(zoom + 10));
    else if (k === '-') run(() => setZoom(zoom - 10));
    else if (k === '0') run(() => setZoom(100));
    else if (k === 'tab') run(() => { const i = tabs.indexOf(cur); switchTo(tabs[(i + (e.shiftKey ? -1 : 1) + tabs.length) % tabs.length]); });
  });

  // ------------------------------------------------------------ theme + lifecycle
  function applyTheme(t) {
    if (!t) return;
    const mode = typeof t === 'string' ? t : (t.mode || t.theme || 'dark');
    document.documentElement.dataset.theme = /light/i.test(mode) ? 'light' : 'dark';
    const acc = t.accent; if (acc && /^#[0-9a-f]{3,8}$/i.test(acc)) document.documentElement.style.setProperty('--accent', acc);
  }
  window.writer.onTheme(applyTheme);
  window.writer.onOpenFiles(openPaths);
  window.writer.onRequestClose(async () => {
    captureState();
    const dirty = tabs.filter((t) => t.dirty);
    if (dirty.length) {
      const r = await window.writer.confirm({ message: `Save changes to ${dirty.length} document${dirty.length > 1 ? 's' : ''}?`, detail: dirty.map((t) => t.name).join(', '), buttons: ['Save all', "Don't save", 'Cancel'] });
      if (r === 2) return;
      if (r === 0) for (const t of dirty) if (!(await saveTab(t, false))) return;
    }
    quitting = true; await window.writer.clearRecovery(); window.writer.closeConfirmed();
  });

  (async function init() {
    const q = new URLSearchParams(location.search);
    applyTheme({ mode: q.get('theme') || 'dark', accent: q.get('accent') || '' });
    try { applyTheme(await window.writer.theme()); } catch { /* ignore */ }
    try { document.execCommand('defaultParagraphSeparator', false, 'p'); document.execCommand('styleWithCSS', false, true); } catch { /* ignore */ }
    applyPaper(); setZoom(100);
    switchTo(newTab());
    let files = []; try { files = JSON.parse(q.get('files') || '[]'); } catch { /* ignore */ }
    if (files.length) { await openPaths(files); return; }
    const rec = await window.writer.loadRecovery();
    if (rec && rec.tabs && rec.tabs.length) {
      const r = await window.writer.confirm({ message: 'Red Star Writer closed unexpectedly.', detail: `Recover ${rec.tabs.length} unsaved document${rec.tabs.length > 1 ? 's' : ''}?`, buttons: ['Recover', 'Discard'] });
      if (r === 0) {
        const blank = cur;
        for (const x of rec.tabs) { const t = newTab({ name: x.name, path: x.path, html: x.html, dirty: true }); switchTo(t); }
        tabs.splice(tabs.indexOf(blank), 1); renderTabs();
      } else await window.writer.clearRecovery();
    }
  })();
})();
