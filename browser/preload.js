'use strict';
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('redstar', {
  onOpenUrl: (callback) => ipcRenderer.on('open-url', (_event, url) => callback(url)),
  theme: () => ipcRenderer.invoke('app:theme'),
  onTheme: (callback) => ipcRenderer.on('theme', (_event, theme) => callback(theme)),
});
