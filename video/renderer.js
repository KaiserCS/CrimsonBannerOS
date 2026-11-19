'use strict';
const { formatTime, srtToVtt, clamp } = window.Helpers;

const $ = (id) => document.getElementById(id);
const video = $('video');
const seek = $('seek');
const volume = $('volume');
const timeLabel = $('time');
const toastEl = $('toast');

let playlist = [];
let index = -1;
let repeat = 'off';            // off | all | one
let seeking = false;
let idleTimer = null;
let lastSaved = 0;
let resumeFor = null;          // item whose saved position should be restored once metadata is known
let subtitleUrl = null;

// ------------------------------------------------------------------ small utilities

function toast(text, ms = 1800) {
  toastEl.textContent = text;
  toastEl.hidden = false;
  clearTimeout(toast.t);
  toast.t = setTimeout(() => { toastEl.hidden = true; }, ms);
}

function store(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch (_e) { /* ignore */ } }
function load(key, fallback) {
  try { const v = localStorage.getItem(key); return v === null ? fallback : JSON.parse(v); } catch (_e) { return fallback; }
}

// ------------------------------------------------------------------ resume positions

function savePosition(force) {
  const item = playlist[index];
  if (!item || !isFinite(video.duration)) return;
  const now = Date.now();
  if (!force && now - lastSaved < 5000) return;
  lastSaved = now;

  const map = load('rs.positions', {});
  const nearEnd = video.currentTime > video.duration - 10;
  if (video.currentTime < 5 || nearEnd) delete map[item.path];
  else map[item.path] = { t: video.currentTime, at: now };

  const keys = Object.keys(map);
  if (keys.length > 200) {
    keys.sort((a, b) => map[a].at - map[b].at).slice(0, keys.length - 200).forEach((k) => delete map[k]);
  }
  store('rs.positions', map);
}

// ------------------------------------------------------------------ playlist

function renderPlaylist() {
  const ul = $('items');
  ul.textContent = '';
  playlist.forEach((item, i) => {
    const li = document.createElement('li');
    if (i === index) li.className = 'active';
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = item.name;
    name.title = item.path;
    const rm = document.createElement('button');
    rm.textContent = '\u2715';
    rm.title = 'Remove';
    rm.addEventListener('click', (e) => { e.stopPropagation(); removeItem(i); });
    li.addEventListener('click', () => playIndex(i));
    li.append(name, rm);
    ul.appendChild(li);
  });
  $('prevBtn').disabled = playlist.length === 0;
  $('nextBtn').disabled = playlist.length < 2 && repeat !== 'all';
}

function addItems(items, playNow) {
  if (!items || !items.length) return;
  let firstNew = -1;
  for (const it of items) {
    if (playlist.some((p) => p.path === it.path)) continue;
    playlist.push(it);
    if (firstNew < 0) firstNew = playlist.length - 1;
  }
  if (firstNew < 0) {
    // Everything was already queued: just jump to the first of them.
    if (playNow) playIndex(playlist.findIndex((p) => p.path === items[0].path));
  } else if (playNow || index === -1) {
    playIndex(firstNew);
  }
  renderPlaylist();
}

function removeItem(i) {
  const wasCurrent = i === index;
  playlist.splice(i, 1);
  if (wasCurrent) {
    video.removeAttribute('src');
    video.load();
    index = -1;
    if (playlist.length) playIndex(Math.min(i, playlist.length - 1));
    else showEmpty();
  } else if (i < index) {
    index -= 1;
  }
  renderPlaylist();
}

function showEmpty() {
  $('empty').hidden = false;
  document.title = 'Red Star Video';
  updatePlayButton();
}

// ------------------------------------------------------------------ playback

async function playIndex(i) {
  if (i < 0 || i >= playlist.length) return;
  savePosition(true);
  index = i;
  const item = playlist[i];

  $('empty').hidden = true;
  $('error').hidden = true;
  document.title = item.name + ' - Red Star Video';

  clearSubtitles();
  resumeFor = item;
  video.defaultPlaybackRate = parseFloat($('speed').value);   // loading a new source resets playbackRate to this
  video.src = item.url;
  video.playbackRate = parseFloat($('speed').value);
  renderPlaylist();

  if (item.subtitle) {
    const sub = await window.redstar.readSubtitle(item.subtitle);
    if (sub && playlist[index] === item) setSubtitles(sub, false);
  }
  video.play().catch(() => { /* autoplay may be blocked until metadata is ready */ });
}

function togglePlay() {
  if (!video.getAttribute('src')) { openFiles(); return; }
  if (video.paused) video.play().catch(() => {});
  else video.pause();
}

function seekBy(delta) {
  if (!isFinite(video.duration)) return;
  video.currentTime = clamp(video.currentTime + delta, 0, video.duration);
  toast(formatTime(video.currentTime) + ' / ' + formatTime(video.duration), 900);
}

function next() {
  if (index + 1 < playlist.length) playIndex(index + 1);
  else if (repeat === 'all' && playlist.length) playIndex(0);
}

function prev() {
  if (video.currentTime > 3 || index <= 0) video.currentTime = 0;
  else playIndex(index - 1);
}

function setVolume(v) {
  v = clamp(v, 0, 1);
  video.volume = v;
  video.muted = v === 0;
  volume.value = Math.round(v * 100);
  store('rs.volume', v);
  updateMuteButton();
}

function toggleMute() {
  video.muted = !video.muted;
  store('rs.muted', video.muted);
  updateMuteButton();
}

function updateMuteButton() {
  $('muteBtn').textContent = video.muted || video.volume === 0 ? '\uD83D\uDD07' : (video.volume < 0.5 ? '\uD83D\uDD09' : '\uD83D\uDD0A');
}

function updatePlayButton() {
  $('playBtn').textContent = video.paused ? '\u25B6' : '\u23F8';
  reportMedia();
}

// Tells the shell what is playing so its app tray can show controls.
function reportMedia() {
  const item = playlist[index];
  const hasMedia = !!item && !!video.getAttribute('src');
  window.redstar.mediaState({ title: hasMedia ? item.name : null, playing: hasMedia && !video.paused });
}

function changeSpeed(step) {
  const select = $('speed');
  const i = clamp(select.selectedIndex + step, 0, select.options.length - 1);
  select.selectedIndex = i;
  applySpeed();
}

function applySpeed() {
  const rate = parseFloat($('speed').value);
  video.defaultPlaybackRate = rate;
  video.playbackRate = rate;
  store('rs.speed', rate);
  toast('Speed ' + rate + 'x', 900);
}

function cycleRepeat() {
  repeat = repeat === 'off' ? 'all' : repeat === 'all' ? 'one' : 'off';
  const btn = $('repeatBtn');
  btn.classList.toggle('on', repeat !== 'off');
  btn.textContent = repeat === 'one' ? '\uD83D\uDD02' : '\uD83D\uDD01';
  toast('Repeat: ' + repeat);
  renderPlaylist();
}

function toggleFullscreen() {
  if (document.fullscreenElement) document.exitFullscreen();
  else document.documentElement.requestFullscreen().catch(() => {});
}

function togglePlaylist() {
  const panel = $('playlist');
  panel.hidden = !panel.hidden;
  $('listBtn').classList.toggle('on', !panel.hidden);
}

async function openFiles() {
  const items = await window.redstar.openDialog();
  addItems(items, true);
}

// ------------------------------------------------------------------ subtitles

function clearSubtitles() {
  video.querySelectorAll('track').forEach((t) => t.remove());
  if (subtitleUrl) { URL.revokeObjectURL(subtitleUrl); subtitleUrl = null; }
  $('subBtn').classList.remove('on');
}

function setSubtitles(sub, announce = true) {
  clearSubtitles();
  subtitleUrl = URL.createObjectURL(new Blob([srtToVtt(sub.text)], { type: 'text/vtt' }));
  const track = document.createElement('track');
  track.kind = 'subtitles';
  track.label = sub.name;
  track.srclang = 'en';
  track.src = subtitleUrl;
  track.default = true;
  video.appendChild(track);
  track.addEventListener('load', () => { track.track.mode = 'showing'; });
  track.track.mode = 'showing';
  $('subBtn').classList.add('on');
  if (announce) toast('Subtitles: ' + sub.name);
}

async function loadSubtitles() {
  const sub = await window.redstar.openSubtitleDialog();
  if (sub) setSubtitles(sub);
}

function toggleSubtitles() {
  const track = video.textTracks[0];
  if (!track) { toast('No subtitles loaded (press S to load)'); return; }
  track.mode = track.mode === 'showing' ? 'hidden' : 'showing';
  $('subBtn').classList.toggle('on', track.mode === 'showing');
  toast('Subtitles ' + (track.mode === 'showing' ? 'on' : 'off'), 900);
}

// ------------------------------------------------------------------ video events

video.addEventListener('loadedmetadata', () => {
  if (!resumeFor) return;
  const saved = load('rs.positions', {})[resumeFor.path];
  resumeFor = null;
  if (saved && saved.t > 5 && saved.t < video.duration - 10) {
    video.currentTime = saved.t;
    toast('Resumed at ' + formatTime(saved.t));
  }
});

video.addEventListener('timeupdate', () => {
  if (!seeking && isFinite(video.duration) && video.duration > 0) {
    seek.value = Math.round((video.currentTime / video.duration) * 1000);
  }
  timeLabel.textContent = formatTime(video.currentTime) + ' / ' + formatTime(video.duration);
  savePosition(false);
});

video.addEventListener('play', () => { updatePlayButton(); bumpIdle(); });
video.addEventListener('pause', () => { updatePlayButton(); savePosition(true); document.body.classList.remove('idle'); });

video.addEventListener('ended', () => {
  if (repeat === 'one') { video.currentTime = 0; video.play().catch(() => {}); return; }
  const map = load('rs.positions', {});
  if (playlist[index]) { delete map[playlist[index].path]; store('rs.positions', map); }
  if (index + 1 < playlist.length) playIndex(index + 1);
  else if (repeat === 'all' && playlist.length) playIndex(0);
  else updatePlayButton();
});

video.addEventListener('error', () => {
  const item = playlist[index];
  if (!item || !video.getAttribute('src')) return;
  const code = video.error ? video.error.code : 0;
  const reason = code === 4
    ? 'Its format or codec is not supported by the built-in player.'
    : 'The file could not be read or decoded.';
  $('errorText').textContent = 'Cannot play "' + item.name + '". ' + reason;
  $('error').hidden = false;
});

video.addEventListener('click', togglePlay);
video.addEventListener('dblclick', toggleFullscreen);

// ------------------------------------------------------------------ controls

seek.addEventListener('input', () => {
  seeking = true;
  if (isFinite(video.duration)) {
    timeLabel.textContent = formatTime((seek.value / 1000) * video.duration) + ' / ' + formatTime(video.duration);
  }
});
seek.addEventListener('change', () => {
  if (isFinite(video.duration)) video.currentTime = (seek.value / 1000) * video.duration;
  seeking = false;
});

volume.addEventListener('input', () => setVolume(volume.value / 100));

$('playBtn').addEventListener('click', togglePlay);
$('prevBtn').addEventListener('click', prev);
$('nextBtn').addEventListener('click', next);
$('muteBtn').addEventListener('click', toggleMute);
$('speed').addEventListener('change', applySpeed);
$('subBtn').addEventListener('click', loadSubtitles);
$('repeatBtn').addEventListener('click', cycleRepeat);
$('listBtn').addEventListener('click', togglePlaylist);
$('fullBtn').addEventListener('click', toggleFullscreen);
$('openBtn').addEventListener('click', openFiles);
$('plOpen').addEventListener('click', openFiles);
$('plClear').addEventListener('click', () => {
  playlist = [];
  index = -1;
  video.removeAttribute('src');
  video.load();
  clearSubtitles();
  $('error').hidden = true;
  showEmpty();
  renderPlaylist();
});
$('externalBtn').addEventListener('click', () => {
  const item = playlist[index];
  if (item) window.redstar.openExternal(item.path);
});

// Hide the controls and cursor while playing and the mouse is still.
function bumpIdle() {
  document.body.classList.remove('idle');
  clearTimeout(idleTimer);
  idleTimer = setTimeout(() => {
    if (!video.paused) document.body.classList.add('idle');
  }, 2500);
}
document.addEventListener('mousemove', bumpIdle);

// ------------------------------------------------------------------ keyboard

document.addEventListener('keydown', (e) => {
  if (e.target && (e.target.tagName === 'SELECT')) return;
  const ctrl = e.ctrlKey || e.metaKey;
  const key = e.key;
  let handled = true;

  if (key === ' ' || key.toLowerCase() === 'k') togglePlay();
  else if (key === 'ArrowLeft') seekBy(e.shiftKey ? -30 : -5);
  else if (key === 'ArrowRight') seekBy(e.shiftKey ? 30 : 5);
  else if (key === 'ArrowUp') { setVolume(video.volume + 0.05); toast('Volume ' + Math.round(video.volume * 100) + '%', 700); }
  else if (key === 'ArrowDown') { setVolume(video.volume - 0.05); toast('Volume ' + Math.round(video.volume * 100) + '%', 700); }
  else if (key.toLowerCase() === 'm') toggleMute();
  else if (key.toLowerCase() === 'f') toggleFullscreen();
  else if (key === 'Escape' && document.fullscreenElement) document.exitFullscreen();
  else if (key === ',') changeSpeed(-1);
  else if (key === '.') changeSpeed(1);
  else if (key === 'PageDown') next();
  else if (key === 'PageUp') prev();
  else if (key.toLowerCase() === 'o' || (ctrl && key.toLowerCase() === 'o')) openFiles();
  else if (key.toLowerCase() === 's') loadSubtitles();
  else if (key.toLowerCase() === 'v') toggleSubtitles();
  else if (key.toLowerCase() === 'l') togglePlaylist();
  else if (key.toLowerCase() === 'r') cycleRepeat();
  else if (/^[0-9]$/.test(key) && isFinite(video.duration)) video.currentTime = video.duration * (Number(key) / 10);
  else handled = false;

  if (handled) { e.preventDefault(); bumpIdle(); }
});

// ------------------------------------------------------------------ files from outside

window.redstar.onMediaCommand((cmd) => {
  if (cmd === 'playpause') togglePlay();
  else if (cmd === 'next') next();
  else if (cmd === 'prev') prev();
});

// Follow the shell's accent colour (the player itself stays dark, like most video players).
function applyTheme(t) {
  if (t && t.accent) document.documentElement.style.setProperty('--accent', t.accent);
}
window.redstar.theme().then(applyTheme).catch(() => {});
window.redstar.onTheme(applyTheme);

window.redstar.onDrop((items) => addItems(items, true));
window.redstar.onAddFiles((items) => addItems(items, true));
document.addEventListener('dragenter', () => document.body.classList.add('drag'));
document.addEventListener('dragleave', (e) => { if (!e.relatedTarget) document.body.classList.remove('drag'); });
document.addEventListener('drop', () => document.body.classList.remove('drag'));

// ------------------------------------------------------------------ start

(async function init() {
  setVolume(load('rs.volume', 1));
  video.muted = load('rs.muted', false);
  updateMuteButton();

  const savedSpeed = load('rs.speed', 1);
  const speedSelect = $('speed');
  for (let i = 0; i < speedSelect.options.length; i++) {
    if (parseFloat(speedSelect.options[i].value) === savedSpeed) speedSelect.selectedIndex = i;
  }

  updatePlayButton();
  renderPlaylist();
  addItems(await window.redstar.initialFiles(), true);
})();
