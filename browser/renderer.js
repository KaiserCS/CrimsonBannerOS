'use strict';
const view = document.getElementById('view');
const urlBox = document.getElementById('url');
const backBtn = document.getElementById('back');
const forwardBtn = document.getElementById('forward');
const reloadBtn = document.getElementById('reload');
const homeBtn = document.getElementById('home');
const errorBar = document.getElementById('error');

const params = new URLSearchParams(location.search);
const HOME = params.get('home') || 'about:blank';
let ready = false;
let loading = false;

function normalize(text) {
  text = text.trim();
  if (!text) return null;
  if (/^https?:\/\//i.test(text)) return text;
  if (/^[^\s]+\.[^\s]{2,}(\/.*)?$/.test(text) && !text.includes(' ')) return 'https://' + text;
  return 'https://duckduckgo.com/?q=' + encodeURIComponent(text);
}

function go(text) {
  const url = normalize(text);
  if (!url) return;
  errorBar.style.display = 'none';
  view.loadURL(url);
}

function updateButtons() {
  if (!ready) return;
  backBtn.disabled = !view.canGoBack();
  forwardBtn.disabled = !view.canGoForward();
  reloadBtn.innerHTML = loading ? '&#10005;' : '&#10227;';
  reloadBtn.title = loading ? 'Stop' : 'Reload (F5)';
}

view.addEventListener('dom-ready', () => { ready = true; updateButtons(); });
view.addEventListener('did-start-loading', () => { loading = true; document.body.classList.add('loading'); updateButtons(); });
view.addEventListener('did-stop-loading', () => { loading = false; document.body.classList.remove('loading'); updateButtons(); });
view.addEventListener('did-navigate', (e) => { urlBox.value = e.url; updateButtons(); });
view.addEventListener('did-navigate-in-page', (e) => { if (e.isMainFrame) urlBox.value = e.url; updateButtons(); });
view.addEventListener('page-title-updated', (e) => { document.title = e.title + ' - Red Star Online'; });
view.addEventListener('did-fail-load', (e) => {
  // -3 means the load was aborted by another navigation; ignore it.
  if (!e.isMainFrame || e.errorCode === -3) return;
  errorBar.textContent = 'Could not load ' + e.validatedURL + ' (' + e.errorDescription + ')';
  errorBar.style.display = 'block';
});

backBtn.addEventListener('click', () => view.goBack());
forwardBtn.addEventListener('click', () => view.goForward());
reloadBtn.addEventListener('click', () => (loading ? view.stop() : view.reload()));
homeBtn.addEventListener('click', () => go(HOME));
urlBox.addEventListener('keydown', (e) => { if (e.key === 'Enter') { go(urlBox.value); view.focus(); } });
urlBox.addEventListener('focus', () => urlBox.select());

document.addEventListener('keydown', (e) => {
  if (e.ctrlKey && e.key.toLowerCase() === 'l') { e.preventDefault(); urlBox.focus(); }
  else if (e.key === 'F5' || (e.ctrlKey && e.key.toLowerCase() === 'r')) { e.preventDefault(); view.reload(); }
  else if (e.altKey && e.key === 'ArrowLeft') { view.goBack(); }
  else if (e.altKey && e.key === 'ArrowRight') { view.goForward(); }
});

window.redstar.onOpenUrl((url) => go(url));

// Follow the shell's light/dark theme and accent colour.
function applyTheme(t) {
  if (!t) return;
  document.documentElement.dataset.theme = t.dark ? 'dark' : 'light';
  if (t.accent) document.documentElement.style.setProperty('--accent', t.accent);
}
window.redstar.theme().then(applyTheme).catch(() => {});
window.redstar.onTheme(applyTheme);

go(params.get('url') || HOME);
