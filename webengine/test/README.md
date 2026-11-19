# Web engine tests

`run-tests.js` plays the part of the shell: it starts `../main.js` with a stand-in for Electron
(`mock-electron.js`, backed by Playwright's Chromium), sends the same JSON commands the C# side sends
and checks the replies. It covers navigation, frames, the tracker blocker and per-site switch, blocked
sites, copy, find, PDF, reader mode, Open-in-Writer extraction (article detection, image inlining),
ad blocking (list download, network rules, element hiding, per-site switch, YouTube ad data), pop-up adoption with `window.opener`, `window.close()`, downloads, picture-in-picture, private sessions
and clearing data.

    npm i -g playwright && npx playwright install chromium   # once
    node webengine/test/adblock.test.js   # filter engine (no browser needed)
    node webengine/test/run-tests.js

These files are not packaged into the AppImage.
