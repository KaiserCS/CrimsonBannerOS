'use strict';
// GPU switches of the web engine (no browser needed): node webengine/test/gpu.test.js
// Starts main.js with the Electron stand-in three times and looks at which switches it set.
const { spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const ENGINE = path.join(__dirname, '..', 'main.js');
let pass = 0, fail = 0;
function check(name, ok, info) { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${info ? '  — ' + info : ''}`); ok ? pass++ : fail++; }

function run(env, crashes) {
  const ud = fs.mkdtempSync(path.join(os.tmpdir(), 'rs-gpu-'));
  if (crashes) fs.writeFileSync(path.join(ud, 'gpu-crashes.json'), JSON.stringify({ n: crashes, at: Date.now() }));
  const callsFile = path.join(ud, 'calls.json');
  spawnSync(process.execPath, ['-r', path.join(__dirname, 'register.js'), '-e',
    `process.env.REDSTAR_WEB_SOCKET='/nonexistent/sock'; require(${JSON.stringify(ENGINE)}); setTimeout(() => process.exit(0), 300);`], {
    env: { ...process.env, ...env, MOCK_CALLS: callsFile, MOCK_USERDATA: ud, MOCK_NO_BROWSER: '1' }, timeout: 20000, stdio: 'ignore',
  });
  const calls = fs.existsSync(callsFile) ? JSON.parse(fs.readFileSync(callsFile, 'utf8')) : [];
  const switches = calls.filter((c) => c[0] === 'appendSwitch').map((c) => c.slice(1).join('='));
  return { cpu: calls.some((c) => c[0] === 'disableHardwareAcceleration'), switches };
}

const off = run({ REDSTAR_WEB_GPU: '0' });
check('no GPU: hardware acceleration is switched off', off.cpu);
check('no GPU: no VA-API switches', !off.switches.some((s) => s.includes('Vaapi')));

const on = run({ REDSTAR_WEB_GPU: '1' });
check('GPU: hardware acceleration stays on', !on.cpu);
check('GPU: hardware video decoding (VA-API) is enabled', on.switches.some((s) => s.startsWith('enable-features=') && s.includes('VaapiVideoDecoder')), on.switches.join(' '));
check('GPU: GPU rasterisation is enabled', on.switches.includes('enable-gpu-rasterization'));

const crashed = run({ REDSTAR_WEB_GPU: '1' }, 3);
check('GPU that crashed 3 times today: back to the CPU', crashed.cpu);

console.log(`\n${pass}/${pass + fail} passed`);
process.exit(fail ? 1 : 0);
