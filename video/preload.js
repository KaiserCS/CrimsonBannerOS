'use strict';
const { contextBridge, ipcRenderer, webUtils } = require('electron');

let dropCallback = null;

// Drag-and-drop: File objects can only be turned into paths here, in the preload.
window.addEventListener('DOMContentLoaded', () => {
  document.addEventListener('dragover', (e) => e.preventDefault());
  document.addEventListener('drop', async (e) => {
    e.preventDefault();
    const paths = [];
    for (const f of e.dataTransfer.files) {
      try { paths.push(webUtils.getPathForFile(f)); } catch (_err) { /* not a real file */ }
    }
    if (paths.length && dropCallback) dropCallback(await ipcRenderer.invoke('files:describe', paths));
  });
});

contextBridge.exposeInMainWorld('redstar', {
  initialFiles: () => ipcRenderer.invoke('app:initial'),
  openDialog: () => ipcRenderer.invoke('dialog:open'),
  openSubtitleDialog: () => ipcRenderer.invoke('dialog:subtitle'),
  readSubtitle: (p) => ipcRenderer.invoke('subtitle:read', p),
  openExternal: (p) => ipcRenderer.invoke('shell:open', p),
  onAddFiles: (cb) => ipcRenderer.on('add-files', (_e, items) => cb(items)),
  onDrop: (cb) => { dropCallback = cb; },
  // shell integration
  mediaState: (state) => ipcRenderer.send('media:state', state),
  onMediaCommand: (cb) => ipcRenderer.on('media-command', (_e, cmd) => cb(cmd)),
  theme: () => ipcRenderer.invoke('app:theme'),
  onTheme: (cb) => ipcRenderer.on('theme', (_e, theme) => cb(theme)),
});
