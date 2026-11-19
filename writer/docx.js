'use strict';
/**
 * Minimal .docx import/export for Red Star Writer (renderer side, no dependencies).
 *   RSDocx.exportDocx(editorElement, { page: 'A4'|'Letter', pageNumbers: bool }) -> Promise<Uint8Array>
 *   RSDocx.importDocx(Uint8Array) -> Promise<string>   (HTML)
 * A .docx is a ZIP of XML parts; zip + deflate use the browser's CompressionStream / DecompressionStream.
 */
(function () {
  const enc = new TextEncoder();
  const dec = new TextDecoder();

  // ------------------------------------------------------------------ zip
  const CRC = (() => {
    const t = new Uint32Array(256);
    for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; }
    return t;
  })();
  function crc32(buf) { let c = 0xFFFFFFFF; for (let i = 0; i < buf.length; i++) c = CRC[(c ^ buf[i]) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; }
  function concat(parts) {
    let n = 0; for (const p of parts) n += p.length;
    const out = new Uint8Array(n); let o = 0;
    for (const p of parts) { out.set(p, o); o += p.length; }
    return out;
  }
  async function pipe(stream, data) {
    const w = stream.writable.getWriter(); w.write(data); w.close();
    return new Uint8Array(await new Response(stream.readable).arrayBuffer());
  }
  const deflateRaw = (d) => pipe(new CompressionStream('deflate-raw'), d);
  const inflateRaw = (d) => pipe(new DecompressionStream('deflate-raw'), d);

  async function makeZip(files) {
    const chunks = [], central = []; let offset = 0;
    for (const f of files) {
      const name = enc.encode(f.name), crc = crc32(f.data);
      let comp = await deflateRaw(f.data), method = 8;
      if (comp.length >= f.data.length) { comp = f.data; method = 0; }
      const lh = new DataView(new ArrayBuffer(30));
      lh.setUint32(0, 0x04034b50, true); lh.setUint16(4, 20, true); lh.setUint16(6, 0x0800, true); lh.setUint16(8, method, true);
      lh.setUint16(12, 0x21, true); lh.setUint32(14, crc, true); lh.setUint32(18, comp.length, true); lh.setUint32(22, f.data.length, true);
      lh.setUint16(26, name.length, true);
      chunks.push(new Uint8Array(lh.buffer), name, comp);
      const ch = new DataView(new ArrayBuffer(46));
      ch.setUint32(0, 0x02014b50, true); ch.setUint16(4, 20, true); ch.setUint16(6, 20, true); ch.setUint16(8, 0x0800, true);
      ch.setUint16(10, method, true); ch.setUint16(14, 0x21, true); ch.setUint32(16, crc, true); ch.setUint32(20, comp.length, true);
      ch.setUint32(24, f.data.length, true); ch.setUint16(28, name.length, true); ch.setUint32(42, offset, true);
      central.push(new Uint8Array(ch.buffer), name);
      offset += 30 + name.length + comp.length;
    }
    let cd = 0; for (const c of central) cd += c.length;
    const end = new DataView(new ArrayBuffer(22));
    end.setUint32(0, 0x06054b50, true); end.setUint16(8, files.length, true); end.setUint16(10, files.length, true);
    end.setUint32(12, cd, true); end.setUint32(16, offset, true);
    return concat([...chunks, ...central, new Uint8Array(end.buffer)]);
  }

  function readZip(bytes) {
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let e = bytes.length - 22;
    while (e >= 0 && dv.getUint32(e, true) !== 0x06054b50) e--;
    if (e < 0) throw new Error('This is not a .docx (zip) file.');
    const count = dv.getUint16(e + 10, true);
    let p = dv.getUint32(e + 16, true);
    const files = {};
    for (let i = 0; i < count; i++) {
      if (dv.getUint32(p, true) !== 0x02014b50) break;
      const method = dv.getUint16(p + 10, true), csize = dv.getUint32(p + 20, true);
      const nlen = dv.getUint16(p + 28, true), xlen = dv.getUint16(p + 30, true), clen = dv.getUint16(p + 32, true);
      const lho = dv.getUint32(p + 42, true);
      const name = dec.decode(bytes.subarray(p + 46, p + 46 + nlen));
      const ln = dv.getUint16(lho + 26, true), lx = dv.getUint16(lho + 28, true);
      const start = lho + 30 + ln + lx;
      files[name] = { method, raw: bytes.subarray(start, start + csize) };
      p += 46 + nlen + xlen + clen;
    }
    return files;
  }
  async function entryBytes(entry) { return entry.method === 0 ? entry.raw : inflateRaw(entry.raw); }

  function b64ToBytes(b64) { const s = atob(b64); const u = new Uint8Array(s.length); for (let i = 0; i < s.length; i++) u[i] = s.charCodeAt(i); return u; }
  function bytesToB64(u) { let s = ''; for (let i = 0; i < u.length; i += 0x8000) s += String.fromCharCode.apply(null, u.subarray(i, i + 0x8000)); return btoa(s); }

  // ------------------------------------------------------------------ export
  const NS = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" ' +
    'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" ' +
    'xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" ' +
    'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" ' +
    'xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"';
  const XML = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n';
  const esc = (s) => String(s).replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const BLOCKS = new Set(['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'UL', 'OL', 'TABLE', 'BLOCKQUOTE', 'PRE', 'HR']);
  const hex2 = (n) => Math.max(0, Math.min(255, n | 0)).toString(16).padStart(2, '0').toUpperCase();

  function cssColor(value) {
    const m = /rgba?\(\s*(\d+)[ ,]+(\d+)[ ,]+(\d+)(?:[ ,/]+([\d.]+))?/.exec(value || '');
    if (!m) return null;
    if (m[4] !== undefined && parseFloat(m[4]) === 0) return null;
    return hex2(+m[1]) + hex2(+m[2]) + hex2(+m[3]);
  }
  function fontName(family) {
    const first = (family || '').split(',')[0].trim().replace(/^["']|["']$/g, '');
    if (/^monospace$/i.test(first)) return 'Courier New';
    if (/^sans-serif$/i.test(first)) return 'Arial';
    if (/^serif$/i.test(first)) return 'Times New Roman';
    return first || 'Arial';
  }

  function runProps(el, extra) {
    const cs = getComputedStyle(el);
    let x = '';
    x += `<w:rFonts w:ascii="${esc(fontName(cs.fontFamily))}" w:hAnsi="${esc(fontName(cs.fontFamily))}" w:cs="${esc(fontName(cs.fontFamily))}"/>`;
    if (parseInt(cs.fontWeight, 10) >= 600) x += '<w:b/>';
    if (cs.fontStyle === 'italic') x += '<w:i/>';
    const deco = cs.textDecorationLine || '';
    if (deco.includes('line-through')) x += '<w:strike/>';
    const col = cssColor(cs.color);
    if (col && col !== '000000' && col !== '111111') x += `<w:color w:val="${col}"/>`;
    x += `<w:sz w:val="${Math.max(2, Math.round(parseFloat(cs.fontSize) * 1.5))}"/>`;
    if (deco.includes('underline')) x += '<w:u w:val="single"/>';
    const bg = cssColor(cs.backgroundColor);
    if (bg) x += `<w:shd w:val="clear" w:color="auto" w:fill="${bg}"/>`;
    if (cs.verticalAlign === 'super') x += '<w:vertAlign w:val="superscript"/>';
    else if (cs.verticalAlign === 'sub') x += '<w:vertAlign w:val="subscript"/>';
    return `<w:rPr>${extra || ''}${x}</w:rPr>`;
  }

  class Exporter {
    constructor() { this.rels = []; this.media = []; this.nums = []; this.nextRel = 10; this.nextDoc = 1; }

    run(text, el) {
      const parts = text.split('\t');
      let inner = '';
      parts.forEach((p, i) => { if (i) inner += '<w:tab/>'; if (p) inner += `<w:t xml:space="preserve">${esc(p)}</w:t>`; });
      return inner ? `<w:r>${runProps(el)}${inner}</w:r>` : '';
    }

    image(img) {
      const m = /^data:image\/(png|jpe?g|gif|bmp);base64,(.*)$/i.exec(img.getAttribute('src') || '');
      if (!m) return '';
      const ext = m[1].toLowerCase().replace('jpeg', 'jpg');
      const id = this.nextDoc++;
      const name = `image${id}.${ext}`;
      this.media.push({ name: 'word/media/' + name, data: b64ToBytes(m[2]) });
      const rid = 'rId' + this.nextRel++;
      this.rels.push(`<Relationship Id="${rid}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/${name}"/>`);
      const w = Math.max(1, Math.round((img.width || img.naturalWidth || 200) * 9525));
      const h = Math.max(1, Math.round((img.height || img.naturalHeight || 150) * 9525));
      return `<w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="${w}" cy="${h}"/><wp:docPr id="${id}" name="Picture ${id}"/>` +
        `<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="${id}" name="${name}"/><pic:cNvPicPr/></pic:nvPicPr>` +
        `<pic:blipFill><a:blip r:embed="${rid}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="${w}" cy="${h}"/></a:xfrm>` +
        `<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>`;
    }

    inline(node) {
      if (node.nodeType === 3) return this.run(node.textContent.replace(/ /g, ' ').replace(/[\r\n]+/g, ' '), node.parentElement);
      if (node.nodeType !== 1) return '';
      const tag = node.tagName;
      if (tag === 'BR') return '<w:r><w:br/></w:r>';
      if (tag === 'IMG') return this.image(node);
      if (tag === 'A' && node.getAttribute('href')) {
        const rid = 'rId' + this.nextRel++;
        this.rels.push(`<Relationship Id="${rid}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="${esc(node.getAttribute('href'))}" TargetMode="External"/>`);
        let inner = ''; for (const c of node.childNodes) inner += this.inline(c);
        return `<w:hyperlink r:id="${rid}">${inner}</w:hyperlink>`;
      }
      let out = ''; for (const c of node.childNodes) out += this.inline(c);
      return out;
    }

    paraProps(el, opts) {
      const cs = getComputedStyle(el);
      let x = '';
      const tag = el.tagName;
      const h = /^H([1-6])$/.exec(tag);
      if (h) x += `<w:pStyle w:val="Heading${Math.min(3, +h[1])}"/>`;
      if (opts.numId) x += `<w:numPr><w:ilvl w:val="${opts.ilvl || 0}"/><w:numId w:val="${opts.numId}"/></w:numPr>`;
      if (opts.rule) x += '<w:pBdr><w:bottom w:val="single" w:sz="6" w:space="1" w:color="888888"/></w:pBdr>';
      const sp = [];
      const before = Math.round(parseFloat(cs.marginTop) * 15) || 0, after = Math.round(parseFloat(cs.marginBottom) * 15) || 0;
      if (before) sp.push(`w:before="${before}"`);
      if (after) sp.push(`w:after="${after}"`);
      const lh = parseFloat(cs.lineHeight), fs = parseFloat(cs.fontSize);
      if (lh && fs && cs.lineHeight !== 'normal') sp.push(`w:line="${Math.round(lh / fs * 240)}" w:lineRule="auto"`);
      if (sp.length) x += `<w:spacing ${sp.join(' ')}/>`;
      if (!opts.numId && el.tagName !== 'TD') {
        const left = Math.round((parseFloat(cs.marginLeft) + parseFloat(cs.paddingLeft)) * 15) || 0;
        if (left > 0) x += `<w:ind w:left="${left}"/>`;
      }
      const ta = cs.textAlign;
      if (ta === 'center') x += '<w:jc w:val="center"/>';
      else if (ta === 'right' || ta === 'end') x += '<w:jc w:val="right"/>';
      else if (ta === 'justify') x += '<w:jc w:val="both"/>';
      return x ? `<w:pPr>${x}</w:pPr>` : '';
    }

    /** Converts the children of a block container: inline runs become paragraphs, block children recurse. */
    container(el, opts) {
      let out = '', buf = [];
      const flush = () => {
        if (!buf.length) return;
        let inner = '';
        for (const n of buf) inner += this.inline(n);
        const meaningful = buf.some((n) => n.nodeType === 1 || (n.textContent || '').trim());
        if (meaningful || inner) out += `<w:p>${this.paraProps(el, opts)}${inner}</w:p>`;
        buf = [];
      };
      for (const n of el.childNodes) {
        if (n.nodeType === 1 && BLOCKS.has(n.tagName)) { flush(); out += this.block(n, opts); }
        else buf.push(n);
      }
      flush();
      return out;
    }

    block(el, opts) {
      opts = opts || {};
      const tag = el.tagName;
      if (el.classList && el.classList.contains('pb')) return '<w:p><w:r><w:br w:type="page"/></w:r></w:p>';
      if (tag === 'HR') return `<w:p>${this.paraProps(el, { rule: true })}</w:p>`;
      if (tag === 'UL' || tag === 'OL') return this.list(el, opts.depth === undefined ? 0 : opts.depth + 1, opts.depth !== undefined);
      if (tag === 'TABLE') return this.table(el);
      const o = { ...opts, numId: undefined };
      const inner = this.container(el, o);
      return inner || (el.childNodes.length === 0 ? `<w:p>${this.paraProps(el, o)}</w:p>` : '');
    }

    list(el, depth, nested) {
      const bullet = el.tagName === 'UL';
      let numId;
      if (!nested || !this._lastNum) {
        numId = this.nums.length + 1;
        this.nums.push({ numId, bullet });
      } else numId = this._lastNum.numId;
      const saved = this._lastNum;
      this._lastNum = { numId };
      let out = '';
      for (const li of el.children) {
        if (li.tagName !== 'LI') continue;
        let buf = [];
        const flush = () => {
          if (!buf.length) return;
          let inner = ''; for (const n of buf) inner += this.inline(n);
          out += `<w:p>${this.paraProps(li, { numId, ilvl: Math.min(8, depth) })}${inner}</w:p>`;
          buf = [];
        };
        let any = false;
        for (const n of li.childNodes) {
          if (n.nodeType === 1 && (n.tagName === 'UL' || n.tagName === 'OL')) { flush(); any = true; out += this.list(n, depth + 1, true); }
          else if (n.nodeType === 1 && BLOCKS.has(n.tagName)) { flush(); any = true; out += `<w:p>${this.paraProps(li, { numId, ilvl: depth })}${[...n.childNodes].map((c) => this.inline(c)).join('')}</w:p>`; }
          else buf.push(n);
        }
        flush();
        if (!any && !li.childNodes.length) out += `<w:p>${this.paraProps(li, { numId, ilvl: depth })}</w:p>`;
      }
      this._lastNum = saved;
      return out;
    }

    table(tbl) {
      const rows = [...tbl.querySelectorAll(':scope > tbody > tr, :scope > thead > tr, :scope > tr')];
      if (!rows.length) return '';
      const cols = Math.max(1, ...rows.map((r) => [...r.children].reduce((n, c) => n + (c.colSpan || 1), 0)));
      const w = Math.floor(9000 / cols);
      let x = `<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblBorders>` +
        ['top', 'left', 'bottom', 'right', 'insideH', 'insideV'].map((s) => `<w:${s} w:val="single" w:sz="4" w:space="0" w:color="999999"/>`).join('') +
        `</w:tblBorders></w:tblPr><w:tblGrid>${Array.from({ length: cols }, () => `<w:gridCol w:w="${w}"/>`).join('')}</w:tblGrid>`;
      for (const tr of rows) {
        x += '<w:tr>';
        for (const td of tr.children) {
          const span = td.colSpan || 1;
          let inner = this.container(td, {});
          if (!inner) inner = '<w:p/>';
          const bg = cssColor(getComputedStyle(td).backgroundColor);
          x += `<w:tc><w:tcPr><w:tcW w:w="${w * span}" w:type="dxa"/>${span > 1 ? `<w:gridSpan w:val="${span}"/>` : ''}` +
            `${bg ? `<w:shd w:val="clear" w:color="auto" w:fill="${bg}"/>` : ''}</w:tcPr>${inner}</w:tc>`;
        }
        x += '</w:tr>';
      }
      return x + '</w:tbl><w:p/>';
    }
  }

  function lvl(i, bullet) {
    const left = 720 * (i + 1);
    return `<w:lvl w:ilvl="${i}"><w:start w:val="1"/><w:numFmt w:val="${bullet ? 'bullet' : 'decimal'}"/>` +
      `<w:lvlText w:val="${bullet ? '•' : '%' + (i + 1) + '.'}"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="${left}" w:hanging="360"/></w:pPr>` +
      `${bullet ? '<w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial"/></w:rPr>' : ''}</w:lvl>`;
  }

  async function exportDocx(editor, options) {
    const opts = options || {};
    const ex = new Exporter();
    const body = ex.container(editor, {});
    const letter = opts.page === 'Letter';
    const pgW = letter ? 12240 : 11906, pgH = letter ? 15840 : 16838;
    const footerRel = opts.pageNumbers ? '<w:footerReference w:type="default" r:id="rId3"/>' : '';
    const doc = `${XML}<w:document ${NS}><w:body>${body || '<w:p/>'}<w:sectPr>${footerRel}<w:pgSz w:w="${pgW}" w:h="${pgH}"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr></w:body></w:document>`;

    const numbering = `${XML}<w:numbering ${NS}>` +
      `<w:abstractNum w:abstractNumId="1"><w:multiLevelType w:val="hybridMultilevel"/>${Array.from({ length: 9 }, (_, i) => lvl(i, true)).join('')}</w:abstractNum>` +
      `<w:abstractNum w:abstractNumId="2"><w:multiLevelType w:val="hybridMultilevel"/>${Array.from({ length: 9 }, (_, i) => lvl(i, false)).join('')}</w:abstractNum>` +
      ex.nums.map((n) => `<w:num w:numId="${n.numId}"><w:abstractNumId w:val="${n.bullet ? 1 : 2}"/>${n.bullet ? '' : '<w:lvlOverride w:ilvl="0"><w:startOverride w:val="1"/></w:lvlOverride>'}</w:num>`).join('') +
      `</w:numbering>`;

    const styles = `${XML}<w:styles ${NS}><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial" w:cs="Arial"/><w:sz w:val="22"/></w:rPr></w:rPrDefault>` +
      `<w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="259" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>` +
      `<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>` +
      [['Heading1', 'heading 1', 32], ['Heading2', 'heading 2', 26], ['Heading3', 'heading 3', 24]].map(([id, nm, sz]) =>
        `<w:style w:type="paragraph" w:styleId="${id}"><w:name w:val="${nm}"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/></w:pPr><w:rPr><w:b/><w:sz w:val="${sz}"/></w:rPr></w:style>`).join('') +
      `</w:styles>`;

    const footer = `${XML}<w:ftr ${NS}><w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p></w:ftr>`;

    const docRels = `${XML}<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">` +
      `<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>` +
      `<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/>` +
      (opts.pageNumbers ? `<Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/>` : '') +
      ex.rels.join('') + `</Relationships>`;

    const types = `${XML}<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">` +
      `<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>` +
      `<Default Extension="png" ContentType="image/png"/><Default Extension="jpg" ContentType="image/jpeg"/><Default Extension="gif" ContentType="image/gif"/><Default Extension="bmp" ContentType="image/bmp"/>` +
      `<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>` +
      `<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>` +
      `<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>` +
      (opts.pageNumbers ? `<Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/>` : '') +
      `</Types>`;

    const rels = `${XML}<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>`;

    const files = [
      { name: '[Content_Types].xml', data: enc.encode(types) },
      { name: '_rels/.rels', data: enc.encode(rels) },
      { name: 'word/document.xml', data: enc.encode(doc) },
      { name: 'word/styles.xml', data: enc.encode(styles) },
      { name: 'word/numbering.xml', data: enc.encode(numbering) },
      { name: 'word/_rels/document.xml.rels', data: enc.encode(docRels) },
    ];
    if (opts.pageNumbers) files.push({ name: 'word/footer1.xml', data: enc.encode(footer) });
    for (const m of ex.media) files.push(m);
    return makeZip(files);
  }

  // ------------------------------------------------------------------ import
  const W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main';
  const R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships';
  const q = (el, name) => [...el.children].find((c) => c.localName === name);
  const all = (el, name) => [...el.getElementsByTagNameNS(W, name)];
  const wattr = (el, name) => (el ? el.getAttributeNS(W, name) || el.getAttribute('w:' + name) : null);
  const ehtml = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

  async function importDocx(bytes) {
    const zip = readZip(bytes);
    if (!zip['word/document.xml']) throw new Error('word/document.xml is missing: not a Word document.');
    const parse = async (name) => zip[name] ? new DOMParser().parseFromString(dec.decode(await entryBytes(zip[name])), 'application/xml') : null;
    const docXml = await parse('word/document.xml');
    const relXml = await parse('word/_rels/document.xml.rels');
    const numXml = await parse('word/numbering.xml');

    const rels = {};
    if (relXml) for (const r of relXml.getElementsByTagName('Relationship')) rels[r.getAttribute('Id')] = { target: r.getAttribute('Target'), type: r.getAttribute('Type') };

    // numbering: numId -> abstract -> level -> bullet?
    const abstracts = {}, numToAbs = {};
    if (numXml) {
      for (const a of all(numXml.documentElement, 'abstractNum')) {
        const id = wattr(a, 'abstractNumId'); abstracts[id] = {};
        for (const l of all(a, 'lvl')) abstracts[id][wattr(l, 'ilvl')] = wattr(q(l, 'numFmt'), 'val');
      }
      for (const n of all(numXml.documentElement, 'num')) numToAbs[wattr(n, 'numId')] = wattr(q(n, 'abstractNumId'), 'val');
    }
    const isBullet = (numId, ilvl) => {
      const fmt = (abstracts[numToAbs[numId]] || {})[ilvl];
      return !fmt || fmt === 'bullet' || fmt === 'none';
    };

    const imageCache = {};
    async function imageHtml(drawing) {
      const blip = drawing.getElementsByTagNameNS('http://schemas.openxmlformats.org/drawingml/2006/main', 'blip')[0];
      if (!blip) return '';
      const rid = blip.getAttributeNS(R, 'embed') || blip.getAttribute('r:embed');
      const rel = rels[rid];
      if (!rel) return '';
      const name = 'word/' + rel.target.replace(/^\//, '').replace(/^word\//, '');
      if (!zip[name]) return '';
      if (!imageCache[name]) {
        const ext = name.split('.').pop().toLowerCase();
        const mime = ext === 'png' ? 'image/png' : ext === 'gif' ? 'image/gif' : ext === 'bmp' ? 'image/bmp' : (ext === 'jpg' || ext === 'jpeg') ? 'image/jpeg' : null;
        if (!mime) return '';
        imageCache[name] = `data:${mime};base64,${bytesToB64(await entryBytes(zip[name]))}`;
      }
      const ext = drawing.getElementsByTagNameNS('http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing', 'extent')[0];
      let size = '';
      if (ext) size = ` width="${Math.round((+ext.getAttribute('cx')) / 9525)}" height="${Math.round((+ext.getAttribute('cy')) / 9525)}"`;
      return `<img src="${imageCache[name]}"${size}>`;
    }

    async function runHtml(r) {
      const rPr = q(r, 'rPr');
      let style = '';
      let open = '', close = '';
      if (rPr) {
        const on = (n) => { const e = q(rPr, n); return e && wattr(e, 'val') !== '0' && wattr(e, 'val') !== 'false'; };
        if (on('b')) { open += '<b>'; close = '</b>' + close; }
        if (on('i')) { open += '<i>'; close = '</i>' + close; }
        if (q(rPr, 'u') && wattr(q(rPr, 'u'), 'val') !== 'none') { open += '<u>'; close = '</u>' + close; }
        if (on('strike')) { open += '<s>'; close = '</s>' + close; }
        const va = q(rPr, 'vertAlign') && wattr(q(rPr, 'vertAlign'), 'val');
        if (va === 'superscript') { open += '<sup>'; close = '</sup>' + close; }
        if (va === 'subscript') { open += '<sub>'; close = '</sub>' + close; }
        const color = q(rPr, 'color') && wattr(q(rPr, 'color'), 'val');
        if (color && /^[0-9a-f]{6}$/i.test(color)) style += `color:#${color};`;
        const sz = q(rPr, 'sz') && wattr(q(rPr, 'sz'), 'val');
        if (sz) style += `font-size:${(+sz) / 2}pt;`;
        const fonts = q(rPr, 'rFonts') && (wattr(q(rPr, 'rFonts'), 'ascii') || wattr(q(rPr, 'rFonts'), 'hAnsi'));
        if (fonts) style += `font-family:'${ehtml(fonts)}';`;
        const shd = q(rPr, 'shd') && wattr(q(rPr, 'shd'), 'fill');
        if (shd && /^[0-9a-f]{6}$/i.test(shd)) style += `background-color:#${shd};`;
        const hl = q(rPr, 'highlight') && wattr(q(rPr, 'highlight'), 'val');
        if (hl && hl !== 'none') style += `background-color:${hl};`;
      }
      let inner = '';
      for (const c of r.children) {
        if (c.localName === 't') inner += ehtml(c.textContent);
        else if (c.localName === 'tab') inner += '&emsp;';
        else if (c.localName === 'br') inner += wattr(c, 'type') === 'page' ? '<div class="pb"></div>' : '<br>';
        else if (c.localName === 'drawing' || c.localName === 'pict') inner += await imageHtml(c);
      }
      if (!inner) return '';
      const span = style ? `<span style="${style}">${inner}</span>` : inner;
      return open + span + close;
    }

    async function inlineHtml(p) {
      let out = '';
      for (const c of p.children) {
        if (c.localName === 'r') out += await runHtml(c);
        else if (c.localName === 'hyperlink') {
          let inner = ''; for (const r of c.children) if (r.localName === 'r') inner += await runHtml(r);
          const rid = c.getAttributeNS(R, 'id') || c.getAttribute('r:id');
          const href = rid && rels[rid] ? rels[rid].target : '';
          out += href ? `<a href="${ehtml(href)}">${inner}</a>` : inner;
        }
      }
      return out;
    }

    async function paraHtml(p) {
      const pPr = q(p, 'pPr');
      let tag = 'p', style = '';
      let numId = null, ilvl = 0;
      if (pPr) {
        const ps = q(pPr, 'pStyle') && wattr(q(pPr, 'pStyle'), 'val');
        const h = ps && /^(?:Heading|heading)\s?(\d)$/.exec(ps);
        if (h) tag = 'h' + Math.min(6, +h[1]);
        else if (ps === 'Title') tag = 'h1';
        const np = q(pPr, 'numPr');
        if (np) { numId = wattr(q(np, 'numId'), 'val'); ilvl = +(wattr(q(np, 'ilvl'), 'val') || 0); if (numId === '0') numId = null; }
        const jc = q(pPr, 'jc') && wattr(q(pPr, 'jc'), 'val');
        if (jc) style += `text-align:${jc === 'both' ? 'justify' : jc === 'center' ? 'center' : jc === 'right' || jc === 'end' ? 'right' : 'left'};`;
        const ind = q(pPr, 'ind') && wattr(q(pPr, 'ind'), 'left');
        if (ind && !numId && +ind > 0) style += `margin-left:${Math.round(+ind / 15)}px;`;
        const sp = q(pPr, 'spacing');
        if (sp) {
          const line = wattr(sp, 'line');
          if (line && wattr(sp, 'lineRule') !== 'exact') style += `line-height:${(+line / 240).toFixed(2)};`;
        }
        if (q(pPr, 'pBdr') && !(await inlineHtml(p))) return { hr: true };
      }
      const inner = await inlineHtml(p);
      return { tag, style, inner: inner || '<br>', numId, ilvl };
    }

    async function blocksHtml(parent) {
      let out = '';
      let list = null;   // { tag, numId, items }
      const closeList = () => { if (list) { out += `<${list.tag}>${list.items.join('')}</${list.tag}>`; list = null; } };
      for (const el of parent.children) {
        if (el.localName === 'p') {
          const p = await paraHtml(el);
          if (p.hr) { closeList(); out += '<hr>'; continue; }
          if (p.numId) {
            const tag = isBullet(p.numId, String(p.ilvl)) ? 'ul' : 'ol';
            if (!list || list.numId !== p.numId || list.tag !== tag) { closeList(); list = { tag, numId: p.numId, items: [] }; }
            const pad = p.ilvl ? ` style="margin-left:${p.ilvl * 24}px"` : '';
            list.items.push(`<li${pad}>${p.inner}</li>`);
          } else {
            closeList();
            out += `<${p.tag}${p.style ? ` style="${p.style}"` : ''}>${p.inner}</${p.tag}>`;
          }
        } else if (el.localName === 'tbl') {
          closeList();
          let t = '<table>';
          for (const tr of el.children) {
            if (tr.localName !== 'tr') continue;
            t += '<tr>';
            for (const tc of tr.children) {
              if (tc.localName !== 'tc') continue;
              const tcPr = q(tc, 'tcPr');
              const span = tcPr && q(tcPr, 'gridSpan') ? wattr(q(tcPr, 'gridSpan'), 'val') : null;
              const fill = tcPr && q(tcPr, 'shd') ? wattr(q(tcPr, 'shd'), 'fill') : null;
              const st = fill && /^[0-9a-f]{6}$/i.test(fill) ? ` style="background-color:#${fill}"` : '';
              t += `<td${span ? ` colspan="${span}"` : ''}${st}>${(await blocksHtml(tc)) || '<br>'}</td>`;
            }
            t += '</tr>';
          }
          out += t + '</table><p><br></p>';
        } else if (el.localName === 'sdt') {
          const content = q(el, 'sdtContent');
          if (content) out += await blocksHtml(content);
        }
      }
      closeList();
      return out;
    }

    const body = q(docXml.documentElement, 'body');
    return (await blocksHtml(body)) || '<p><br></p>';
  }

  window.RSDocx = { exportDocx, importDocx };
})();
