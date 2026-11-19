'use strict';
const { contextBridge, ipcRenderer } = require('electron');
const inv = (c, ...a) => ipcRenderer.invoke(c, ...a);
contextBridge.exposeInMainWorld('writer', {
  theme: () => inv('app:theme'),
  onTheme: (cb) => ipcRenderer.on('theme', (_e, t) => cb(t)),
  onOpenFiles: (cb) => ipcRenderer.on('open-files', (_e, f) => cb(f)),
  onRequestClose: (cb) => ipcRenderer.on('request-close', () => cb()),
  closeConfirmed: () => ipcRenderer.send('close-confirmed'),
  openDialog: () => inv('file:open-dialog'),
  readFile: (p) => inv('file:read', p),
  saveDialog: (n) => inv('file:save-dialog', n),
  writeFile: (p, payload) => inv('file:write', p, payload),
  recent: () => inv('file:recent'),
  exportPdf: (o) => inv('pdf:export', o),
  print: () => inv('print'),
  saveRecovery: (d) => inv('recovery:save', d),
  loadRecovery: () => inv('recovery:load'),
  clearRecovery: () => inv('recovery:clear'),
  confirm: (o) => inv('confirm', o),
});
