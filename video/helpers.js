'use strict';
// Pure helpers (usable in the page and in Node tests).
(function (root) {
  function formatTime(sec) {
    if (!isFinite(sec) || sec < 0) sec = 0;
    sec = Math.floor(sec);
    const h = Math.floor(sec / 3600);
    const m = Math.floor((sec % 3600) / 60);
    const s = sec % 60;
    const mm = h > 0 ? String(m).padStart(2, '0') : String(m);
    return (h > 0 ? h + ':' : '') + mm + ':' + String(s).padStart(2, '0');
  }

  // SRT -> WebVTT (the <track> element only understands WebVTT).
  function srtToVtt(text) {
    text = String(text).replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n');
    if (/^WEBVTT/.test(text)) return text;
    return 'WEBVTT\n\n' + text.replace(/(\d{2}:\d{2}:\d{2}),(\d{3})/g, '$1.$2');
  }

  function clamp(v, lo, hi) { return Math.min(hi, Math.max(lo, v)); }

  const api = { formatTime, srtToVtt, clamp };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.Helpers = api;
})(typeof window !== 'undefined' ? window : this);
