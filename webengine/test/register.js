// node -r ./register.js <engine main.js>  → require('electron') resolves to the mock
const Module = require('module');
const path = require('path');
const orig = Module._resolveFilename;
Module._resolveFilename = function (req, ...rest) {
  if (req === 'electron') return path.join(__dirname, 'mock-electron.js');
  return orig.call(this, req, ...rest);
};
