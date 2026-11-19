#!/usr/bin/env python3
"""CI helper: turns .NET test results (TRX) into a readable job summary, and builds an index page for screenshots.

    summary.py tests  <TestResults dir>      → markdown on stdout (append to $GITHUB_STEP_SUMMARY)
    summary.py shots  <screenshots dir>      → writes index.html there, markdown on stdout
"""
import glob, html, os, sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def tests(folder):
    passed = failed = skipped = 0
    failures = []
    for trx in glob.glob(os.path.join(folder, '**', '*.trx'), recursive=True):
        root = ET.parse(trx).getroot()
        for r in root.iterfind('.//t:UnitTestResult', NS):
            outcome = r.get('outcome')
            if outcome == 'Passed': passed += 1
            elif outcome == 'Failed':
                failed += 1
                msg = r.find('.//t:Message', NS)
                failures.append((r.get('testName'), (msg.text or '').strip() if msg is not None else ''))
            else: skipped += 1
    icon = '✅' if failed == 0 and passed > 0 else '❌'
    out = [f'## {icon} C# tests: {passed} passed, {failed} failed, {skipped} skipped', '']
    for name, msg in failures[:40]:
        first = msg.splitlines()[0] if msg else ''
        out.append(f'<details><summary><b>{html.escape(name)}</b> — {html.escape(first[:200])}</summary>\n\n```\n{msg[:4000]}\n```\n</details>')
    if passed + failed == 0: out.append('No test results were found — the build or the test run itself failed; see the log.')
    print('\n'.join(out))


def shots(folder):
    pngs = sorted(glob.glob(os.path.join(folder, '**', '*.png'), recursive=True))
    rel = [os.path.relpath(p, folder) for p in pngs]
    cards = '\n'.join(
        f'<figure><a href="{html.escape(r)}"><img loading="lazy" src="{html.escape(r)}"></a><figcaption>{html.escape(os.path.splitext(r)[0])}</figcaption></figure>'
        for r in rel)
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, 'index.html'), 'w') as f:
        f.write(f'''<!doctype html><meta charset="utf-8"><title>Red Star OS screenshots</title>
<style>body{{font:14px system-ui,sans-serif;margin:24px;background:#f4f5f7;color:#1a202c}}
main{{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:18px}}
figure{{margin:0;background:#fff;border-radius:10px;padding:10px;box-shadow:0 1px 3px #0002}}
img{{width:100%;border-radius:6px;border:1px solid #e2e8f0}}figcaption{{margin-top:6px;font-weight:600}}</style>
<h1>Red Star OS — {len(rel)} screenshots</h1><main>{cards}</main>''')
    print(f'## 🖼️ {len(rel)} screenshots')
    print('Download the **screenshots** artifact and open `index.html` to see every window side by side.')


if __name__ == '__main__':
    {'tests': tests, 'shots': shots}[sys.argv[1]](sys.argv[2])
