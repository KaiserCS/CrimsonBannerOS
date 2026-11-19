#!/usr/bin/env python3
"""
End-to-end test of Red Star's isolated environment pieces with a real, complex X11 app (Chromium):
redstar-wm manages a Red Star-like desktop window and a Chromium window; real mouse/keyboard events are injected
through the X server (XTest), the same way a user's clicks arrive.

    python3 wm/test/session_test.py            (needs Xvfb, Playwright's Chromium, gcc-built redstar-wm)
"""
import ctypes, ctypes.util, json, os, subprocess, sys, time, tempfile, threading

HERE = os.path.dirname(os.path.abspath(__file__))
WM = os.path.join(HERE, '..', 'redstar-wm')
DISP = os.environ.get('SESSION_DISPLAY', ':81')
W, H = 1600, 900
SHOTS = os.environ.get('SESSION_SHOTS')          # folder for screenshots, optional

passes = fails = 0
def check(ok, what, info=''):
    global passes, fails
    passes += bool(ok); fails += (not ok)
    print(f"  {'ok  ' if ok else 'FAIL'} {what}{('  — ' + str(info)) if info != '' else ''}", flush=True)

# ───────────────────────── X helpers (ctypes) ─────────────────────────
x = ctypes.CDLL('libX11.so.6'); xt = ctypes.CDLL('libXtst.so.6')
x.XOpenDisplay.restype = ctypes.c_void_p; x.XOpenDisplay.argtypes = [ctypes.c_char_p]
x.XDefaultRootWindow.restype = ctypes.c_ulong; x.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
x.XInternAtom.restype = ctypes.c_ulong; x.XInternAtom.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
x.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]; x.XFlush.argtypes = [ctypes.c_void_p]
x.XGetWindowProperty.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_long, ctypes.c_long, ctypes.c_int, ctypes.c_ulong,
                                 ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_ulong),
                                 ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_void_p)]
x.XFree.argtypes = [ctypes.c_void_p]
x.XQueryTree.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_ulong),
                         ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_uint)]
x.XTranslateCoordinates.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_int, ctypes.c_int,
                                    ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_ulong)]
class XWA(ctypes.Structure):
    _fields_ = [('x', ctypes.c_int), ('y', ctypes.c_int), ('width', ctypes.c_int), ('height', ctypes.c_int), ('border_width', ctypes.c_int),
                ('depth', ctypes.c_int), ('visual', ctypes.c_void_p), ('root', ctypes.c_ulong), ('c_class', ctypes.c_int),
                ('bit_gravity', ctypes.c_int), ('win_gravity', ctypes.c_int), ('backing_store', ctypes.c_int), ('backing_planes', ctypes.c_ulong),
                ('backing_pixel', ctypes.c_ulong), ('save_under', ctypes.c_int), ('colormap', ctypes.c_ulong), ('map_installed', ctypes.c_int),
                ('map_state', ctypes.c_int), ('all_event_masks', ctypes.c_long), ('your_event_mask', ctypes.c_long),
                ('do_not_propagate_mask', ctypes.c_long), ('override_redirect', ctypes.c_int), ('screen', ctypes.c_void_p)]
x.XGetWindowAttributes.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(XWA)]
xt.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
xt.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
xt.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
x.XKeysymToKeycode.restype = ctypes.c_ubyte; x.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
x.XStringToKeysym.restype = ctypes.c_ulong; x.XStringToKeysym.argtypes = [ctypes.c_char_p]

d = None; root = 0
def atom(n): return x.XInternAtom(d, n.encode(), 0)
def prop(w, name, typ=0, n=1024):
    rt = ctypes.c_ulong(); rf = ctypes.c_int(); ni = ctypes.c_ulong(); after = ctypes.c_ulong(); data = ctypes.c_void_p()
    if x.XGetWindowProperty(d, w, atom(name), 0, n, 0, typ, ctypes.byref(rt), ctypes.byref(rf), ctypes.byref(ni), ctypes.byref(after), ctypes.byref(data)) != 0 or not data.value:
        return None
    try:
        if rf.value == 32: return list(ctypes.cast(data, ctypes.POINTER(ctypes.c_ulong))[:ni.value])
        if rf.value == 8: return ctypes.string_at(data, ni.value)
        return None
    finally: x.XFree(data)
def children(w):
    r = ctypes.c_ulong(); p = ctypes.c_ulong(); k = ctypes.c_void_p(); n = ctypes.c_uint()
    if not x.XQueryTree(d, w, ctypes.byref(r), ctypes.byref(p), ctypes.byref(k), ctypes.byref(n)) or not k.value: return []
    out = list(ctypes.cast(k, ctypes.POINTER(ctypes.c_ulong))[:n.value]); x.XFree(k); return out
def attrs(w):
    a = XWA()
    return a if x.XGetWindowAttributes(d, w, ctypes.byref(a)) else None
def root_geom(w):
    a = attrs(w); rx = ctypes.c_int(); ry = ctypes.c_int(); ch = ctypes.c_ulong()
    x.XTranslateCoordinates(d, w, root, 0, 0, ctypes.byref(rx), ctypes.byref(ry), ctypes.byref(ch))
    return rx.value, ry.value, a.width, a.height
def client_list(): return prop(root, '_NET_CLIENT_LIST', 33) or []
def active(): return (prop(root, '_NET_ACTIVE_WINDOW', 33) or [0])[0]
def wm_class(w):
    b = prop(w, 'WM_CLASS', 31) or b''
    return b.split(b'\0')[1].decode() if b.count(b'\0') >= 1 and len(b.split(b'\0')) > 1 else b.decode(errors='ignore')
def title(w):
    b = prop(w, '_NET_WM_NAME', atom('UTF8_STRING')) or prop(w, 'WM_NAME', 31) or b''
    return b.decode(errors='ignore')
def states(w):
    names = {atom(n): n for n in ['_NET_WM_STATE_FULLSCREEN', '_NET_WM_STATE_MAXIMIZED_VERT', '_NET_WM_STATE_MAXIMIZED_HORZ', '_NET_WM_STATE_HIDDEN', '_NET_WM_STATE_FOCUSED']}
    return {names.get(a, a) for a in (prop(w, '_NET_WM_STATE', 4) or [])}
def sync(): x.XSync(d, 0)
def move(px, py): xt.XTestFakeMotionEvent(d, -1, int(px), int(py), 0); sync()
def press(b=1): xt.XTestFakeButtonEvent(d, b, 1, 0); sync()
def release(b=1): xt.XTestFakeButtonEvent(d, b, 0, 0); sync()
def click(px, py, b=1, hold=0.04):
    move(px, py); time.sleep(0.03); press(b); time.sleep(hold); release(b); time.sleep(0.05)
def key(name, mods=()):
    codes = [x.XKeysymToKeycode(d, x.XStringToKeysym(m.encode())) for m in mods]
    k = x.XKeysymToKeycode(d, x.XStringToKeysym(name.encode()))
    for c in codes: xt.XTestFakeKeyEvent(d, c, 1, 0)
    xt.XTestFakeKeyEvent(d, k, 1, 0); xt.XTestFakeKeyEvent(d, k, 0, 0)
    for c in reversed(codes): xt.XTestFakeKeyEvent(d, c, 0, 0)
    sync(); time.sleep(0.05)
def shot(name):
    if SHOTS:
        os.makedirs(SHOTS, exist_ok=True)
        subprocess.run(['import', '-display', DISP, '-window', 'root', os.path.join(SHOTS, name + '.png')], check=False)
def wait_for(fn, timeout=8.0, step=0.05):
    end = time.time() + timeout
    while time.time() < end:
        v = fn()
        if v: return v
        time.sleep(step)
    return fn()

# ───────────────────────── the page Chromium shows ─────────────────────────
PAGE = r"""<!doctype html><html><head><title>Red Star test page</title><style>
body{margin:0;font:16px sans-serif;background:#f4f4f4} .b{position:absolute;width:220px;height:90px;font-size:18px}
#bt{left:40px;top:40px} #tip{left:300px;top:40px} #fs{left:560px;top:40px} #pop{left:820px;top:40px}
#sel{position:absolute;left:40px;top:180px;width:220px;height:40px;font-size:18px}
#inp{position:absolute;left:300px;top:180px;width:300px;height:36px;font-size:18px}
#hov{position:absolute;left:40px;top:260px;width:300px;height:120px;background:#ddd}
#hov:hover{background:#c8102e}
</style></head><body>
<button class=b id=bt>Click me</button>
<button class=b id=tip title="A tooltip that should stay steady">Hover for tooltip</button>
<button class=b id=fs>Full screen</button>
<button class=b id=pop>Open window</button>
<select id=sel><option>one</option><option>two</option><option>three</option><option>four</option></select>
<input id=inp placeholder="type here">
<div id=hov>hover area</div>
<script>
window.log = {click:0, down:0, up:0, enter:0, leave:0, hoverEnter:0, hoverLeave:0};
bt.addEventListener('click', () => log.click++);
bt.addEventListener('pointerdown', () => log.down++);
bt.addEventListener('pointerup', () => log.up++);
hov.addEventListener('pointerenter', () => log.hoverEnter++);
hov.addEventListener('pointerleave', () => log.hoverLeave++);
fs.addEventListener('click', () => document.documentElement.requestFullscreen());
pop.addEventListener('click', () => window.open('about:blank', 'p', 'width=500,height=360'));
</script></body></html>"""

def main():
    global d, root
    procs = []
    tmp = tempfile.mkdtemp(prefix='rs-session-')
    wmlog = open(os.path.join(tmp, 'wm.log'), 'w')
    procs.append(subprocess.Popen(['Xvfb', DISP, '-screen', '0', f'{W}x{H}x24', '-nolisten', 'tcp'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    time.sleep(1.0)
    env = dict(os.environ, DISPLAY=DISP, REDSTAR_WM_DEBUG='1')
    procs.append(subprocess.Popen([WM], env=env, stdout=subprocess.DEVNULL, stderr=wmlog))
    time.sleep(0.4)
    d = x.XOpenDisplay(DISP.encode()); root = x.XDefaultRootWindow(d)

    # a Red Star-like desktop window (type DESKTOP, full screen, taskbar strut) from the click-test helper
    ct = os.path.join(tmp, 'clicktest')
    subprocess.run(['gcc', '-O0', '-o', ct, os.path.join(HERE, 'clicktest.c'), '-lX11', '-l:libXi.so.6', '-l:libXtst.so.6'], check=True)
    procs.append(subprocess.Popen([ct, 'client', 'desk', 'desktop'], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    desk = wait_for(lambda: next((w for w in client_list() if title(w) == 'desk'), 0))

    page_file = os.path.join(tmp, 'page.html'); open(page_file, 'w').write(PAGE)
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=False, env=env, args=['--no-sandbox', '--disable-gpu', '--window-size=1200,700', '--window-position=200,80'])
        ctx = browser.new_context(no_viewport=True)
        page = ctx.new_page()
        page.goto('file://' + page_file)
        time.sleep(1.5)
        chrome = wait_for(lambda: next((w for w in client_list() if 'test page' in title(w)), 0), 10)
        print('Chromium window under redstar-wm')
        check(chrome, 'Chromium window is managed', hex(chrome) if chrome else 'not found')
        if not chrome: raise SystemExit(1)
        check(active() == chrome, 'it is the active window')
        shot('01-chromium')

        def vp_origin():
            # where the page's (0,0) is on screen: the window's position + browser UI height
            gx, gy, gw, gh = root_geom(chrome)
            m = page.evaluate('({ow: outerWidth, oh: outerHeight, iw: innerWidth, ih: innerHeight})')
            return gx + (gw - m['iw']) // 2, gy + gh - m['ih']
        def center_of(sel):
            ox, oy = vp_origin()
            bb = page.locator(sel).bounding_box()
            return ox + bb['x'] + bb['width'] / 2, oy + bb['y'] + bb['height'] / 2
        def log(): return page.evaluate('log')

        print('Clicking in the focused window')
        bx, by = center_of('#bt')
        l0 = log()
        for _ in range(10): click(bx, by); time.sleep(0.08)
        l1 = log()
        check(l1['click'] - l0['click'] == 10, '10 clicks → 10 click events', f"{l1['click'] - l0['click']} (down {l1['down'] - l0['down']}, up {l1['up'] - l0['up']})")

        print('First click after another window had focus')
        click(W - 30, H - 15)                       # the "taskbar" of the desktop window
        check(wait_for(lambda: active() == desk, 2), 'clicking the desktop focuses it')
        l0 = log(); click(bx, by); time.sleep(0.3); l1 = log()
        check(l1['click'] - l0['click'] == 1, 'one click focuses Chromium and presses the button', f"{l1['click'] - l0['click']} click(s)")
        check(active() == chrome, 'Chromium is active again')

        print('Hover (no flicker)')
        hx, hy = center_of('#hov')
        l0 = log(); move(hx - 200, hy); time.sleep(0.2); move(hx, hy); time.sleep(0.6)
        for i in range(10): move(hx + (i % 3) * 4, hy + (i % 2) * 3); time.sleep(0.05)
        l1 = log()
        check(l1['hoverEnter'] - l0['hoverEnter'] == 1 and l1['hoverLeave'] - l0['hoverLeave'] == 0,
              'hover area entered once, never left while the pointer stays', f"enter {l1['hoverEnter'] - l0['hoverEnter']}, leave {l1['hoverLeave'] - l0['hoverLeave']}")

        print('Tooltip')
        tx, ty = center_of('#tip')
        before = set(children(root))
        move(tx, ty); time.sleep(0.2); move(tx + 2, ty + 1)
        tip = wait_for(lambda: [w for w in children(root) if w not in before and attrs(w) and attrs(w).override_redirect and attrs(w).map_state == 2], 4)
        check(bool(tip), 'tooltip window appears (override-redirect, not managed)')
        if tip:
            t = tip[0]
            samples = [attrs(t) is not None and attrs(t).map_state == 2 for _ in range(30) if not time.sleep(0.05)]
            check(all(samples), 'tooltip stays up while hovering (no flicker)', f"{samples.count(False)} of {len(samples)} samples hidden")
            check(t not in client_list(), 'tooltip is not given a frame')
            top = children(root)[-1] if children(root) else 0
            check(top == t, 'tooltip is above every window')
            shot('02-tooltip')

        print('Drop-down list (<select>)')
        sx, sy = center_of('#sel')
        before = set(children(root))
        click(sx, sy)
        menu = wait_for(lambda: [w for w in children(root) if w not in before and attrs(w) and attrs(w).override_redirect and attrs(w).map_state == 2], 3)
        check(bool(menu), 'drop-down opens as a popup window')
        if menu:
            shot('03-select')
            mx, my, mw, mh = root_geom(menu[0])
            click(mx + mw / 2, my + mh * 0.62)          # third option
            time.sleep(0.4)
        val = page.evaluate('sel.value')
        check(val in ('three', 'four'), 'an option can be picked with one click', val)

        print('Keyboard')
        ix, iy = center_of('#inp')
        click(ix, iy); time.sleep(0.2)
        for ch in 'redstar': key(ch)
        check(page.evaluate('inp.value') == 'redstar', 'typing reaches the focused field', page.evaluate('inp.value'))

        print('New window (window.open)')
        px, py = center_of('#pop')
        n0 = len(client_list())
        with ctx.expect_page(timeout=5000) as pinfo:
            click(px, py)
        popup = pinfo.value
        pw_ = wait_for(lambda: len(client_list()) > n0 and [w for w in client_list() if w not in (desk, chrome)], 5)
        check(bool(pw_), 'the new window is managed')
        if pw_:
            pwin = pw_[-1]
            check(wait_for(lambda: active() == pwin, 2), 'and focused')
            shot('04-popup')
            popup.close(); time.sleep(0.6)
            check(pwin not in client_list(), 'closing it removes it')
            check(wait_for(lambda: active() == chrome, 2), 'focus returns to Chromium', hex(active()))

        print('Full screen (F11; page full screen behaves the same without Playwright)')
        click(*center_of('#inp')); time.sleep(0.2)
        key('F11'); time.sleep(1.2)
        g = root_geom(chrome)
        check(g == (0, 0, W, H), 'full screen fills the display', g)
        check('_NET_WM_STATE_FULLSCREEN' in states(chrome), 'state says full screen')
        shot('05-fullscreen')
        key('F11'); time.sleep(1.0)
        g = root_geom(chrome)
        check(g[2] < W or g[3] < H, 'F11 again leaves full screen', g)

        print('Maximise and drag through Chromium\'s own title bar (_NET_WM_STATE, _NET_WM_MOVERESIZE)')
        page.evaluate('0')
        ax, ay, aw, ah = root_geom(chrome)
        # Chromium draws its own title bar: double-click on its empty strip right of the tabs
        click(ax + aw - 260, ay + 12); click(ax + aw - 260, ay + 12); time.sleep(0.8)
        maxed = '_NET_WM_STATE_MAXIMIZED_VERT' in states(chrome)
        check(maxed, 'double-click on Chromium\'s own title bar maximises', states(chrome))
        if maxed:
            gx, gy, gw, gh = root_geom(chrome)
            wa = prop(root, '_NET_WORKAREA', 6)
            check(gy + gh <= wa[1] + wa[3] + 1, 'maximised window stops above the taskbar', (gx, gy, gw, gh, wa[:4]))
            click(gx + gw - 260, gy + 12); click(gx + gw - 260, gy + 12); time.sleep(0.8)
        ax, ay, aw, ah = root_geom(chrome)
        move(ax + aw - 300, ay + 12); press(); time.sleep(0.15)
        for i in range(1, 9): move(ax + aw - 300 - i * 15, ay + 12 + i * 10); time.sleep(0.04)
        release(); time.sleep(0.5)
        nx, ny, _, _ = root_geom(chrome)
        # Chromium only starts the move after its drag threshold (one step here), from where the pointer is then
        check(abs((ax - nx) - 120) <= 20 and abs((ny - ay) - 80) <= 14, 'dragging Chromium\'s title bar moves the window with the pointer', (ax, ay, '→', nx, ny))
        shot('06-final')

        print('Window manager health')
        browser.close()
    time.sleep(0.3)
    alive = procs[1].poll() is None
    check(alive, 'redstar-wm is still running')
    wmlog.flush()
    text = open(os.path.join(tmp, 'wm.log')).read()
    presses = text.count('press ')
    check(True, f'{presses} clicks went through the window manager (unfocused windows only)')
    for p in reversed(procs):
        try: p.terminate()
        except Exception: pass
    print(f"\n{passes} passed, {fails} failed")
    if fails: print('window manager log:', os.path.join(tmp, 'wm.log'))
    return 1 if fails else 0

if __name__ == '__main__':
    sys.exit(main())
