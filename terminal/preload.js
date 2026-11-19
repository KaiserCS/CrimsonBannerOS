'use strict';
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('redstar', {
  initial: () => ipcRenderer.invoke('app:initial'),
  create: (cols, rows, cwd) => ipcRenderer.invoke('term:create', cols, rows, cwd),
  input: (id, data) => ipcRenderer.send('term:input', id, data),
  resize: (id, cols, rows) => ipcRenderer.send('term:resize', id, cols, rows),
  kill: (id) => ipcRenderer.send('term:kill', id),
  onData: (cb) => ipcRenderer.on('term:data', (_e, id, data) => cb(id, data)),
  onExit: (cb) => ipcRenderer.on('term:exit', (_e, id, code) => cb(id, code)),
  onNewTab: (cb) => ipcRenderer.on('new-tab', (_e, cwd) => cb(cwd)),
  readClipboard: () => ipcRenderer.invoke('clip:read'),
  writeClipboard: (text) => ipcRenderer.send('clip:write', text),
  theme: () => ipcRenderer.invoke('app:theme'),
  onTheme: (cb) => ipcRenderer.on('theme', (_e, theme) => cb(theme)),
  quit: () => ipcRenderer.send('app:quit'),
});
