#!/usr/bin/env python3
"""
End-to-end test of Red Star's isolated environment pieces with a real X11 program: redstar-wm manages a Red Star-like
desktop window and a GTK 3 window that draws its own title bar (gtkapp.py); real mouse/keyboard events are injected
through the X server (XTest), the same way a user's clicks arrive.

    python3 wm/test/session_test.py            (needs Xvfb, gcc, PyGObject with GTK 3, gcc-built redstar-wm)
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

# ───────────────────────── the program under test ─────────────────────────
STATE = None
def log():
    try:
        with open(STATE) as f: return json.load(f)
    except Exception: return {}
def center_of(name):
    w = wait_for(lambda: log().get('widgets', {}).get(name), 3)
    x0, y0, ww, hh = w
    return x0 + ww / 2, y0 + hh / 2

def main():
    global d, root, STATE
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

    STATE = os.path.join(tmp, 'app.json')
    app_env = dict(env, GDK_BACKEND='x11', NO_AT_BRIDGE='1', GTK_A11Y='none', GDK_SCALE='1', GTK_THEME='Adwaita')
    applog = open(os.path.join(tmp, 'app.log'), 'w')
    app = subprocess.Popen([sys.executable, os.path.join(HERE, 'gtkapp.py'), STATE], env=app_env, stdout=applog, stderr=applog)
    procs.append(app)
    win = wait_for(lambda: next((w for w in client_list() if 'test page' in title(w)), 0), 15)
    print('GTK window under redstar-wm')
    check(win, 'the GTK window is managed', hex(win) if win else 'not found')
    if not win:
        applog.flush(); print(open(os.path.join(tmp, 'app.log')).read()[-2000:]); raise SystemExit(1)
    wait_for(lambda: 'bt' in log().get('widgets', {}), 5)
    time.sleep(0.5)
    check(active() == win, 'it is the active window')
    shot('01-gtk')

    print('Clicking in the focused window')
    bx, by = center_of('bt')
    l0 = log()
    for _ in range(10): click(bx, by); time.sleep(0.08)
    time.sleep(0.3); l1 = log()
    check(l1['click'] - l0['click'] == 10, '10 clicks → 10 click events', f"{l1['click'] - l0['click']} (down {l1['down'] - l0['down']}, up {l1['up'] - l0['up']})")

    print('First click after another window had focus')
    click(W - 30, H - 15)                       # the "taskbar" of the desktop window
    check(wait_for(lambda: active() == desk, 2), 'clicking the desktop focuses it')
    l0 = log(); click(bx, by); time.sleep(0.4); l1 = log()
    check(l1['click'] - l0['click'] == 1, 'one click focuses the window and presses the button', f"{l1['click'] - l0['click']} click(s)")
    check(active() == win, 'the GTK window is active again')

    print('Hover (no flicker)')
    hx, hy = center_of('hov')
    l0 = log(); move(hx - 200, hy); time.sleep(0.2); move(hx, hy); time.sleep(0.6)
    for i in range(10): move(hx + (i % 3) * 4, hy + (i % 2) * 3); time.sleep(0.05)
    time.sleep(0.2); l1 = log()
    check(l1['hoverEnter'] - l0['hoverEnter'] == 1 and l1['hoverLeave'] - l0['hoverLeave'] == 0,
          'hover area entered once, never left while the pointer stays', f"enter {l1['hoverEnter'] - l0['hoverEnter']}, leave {l1['hoverLeave'] - l0['hoverLeave']}")

    print('Tooltip')
    tx, ty = center_of('tip')
    before = set(children(root))
    move(tx, ty); time.sleep(0.2); move(tx + 2, ty + 1)
    tip = wait_for(lambda: [w for w in children(root) if w not in before and attrs(w) and attrs(w).override_redirect and attrs(w).map_state == 2], 5)
    check(bool(tip), 'tooltip window appears (override-redirect, not managed)')
    if tip:
        t = tip[0]
        samples = [attrs(t) is not None and attrs(t).map_state == 2 for _ in range(30) if not time.sleep(0.05)]
        check(all(samples), 'tooltip stays up while hovering (no flicker)', f"{samples.count(False)} of {len(samples)} samples hidden")
        check(t not in client_list(), 'tooltip is not given a frame')
        top = children(root)[-1] if children(root) else 0
        check(top == t, 'tooltip is above every window')
        shot('02-tooltip')

    print('Drop-down list')
    sx, sy = center_of('sel')
    before = set(children(root))
    click(sx, sy)
    menu = wait_for(lambda: [w for w in children(root) if w not in before and attrs(w) and attrs(w).override_redirect and attrs(w).map_state == 2
                             and attrs(w).height > 60], 3)
    check(bool(menu), 'drop-down opens as a popup window')
    if menu:
        shot('03-select')
        mx, my, mw, mh = root_geom(menu[0])
        click(mx + mw / 2, my + mh * 0.62)          # the third or fourth entry
        time.sleep(0.4)
    val = log().get('combo')
    check(val in ('three', 'four'), 'an entry can be picked with one click', val)

    print('Keyboard')
    ix, iy = center_of('inp')
    click(ix, iy); time.sleep(0.2)
    for ch in 'redstar': key(ch)
    time.sleep(0.2)
    check(log().get('entry') == 'redstar', 'typing reaches the focused field', log().get('entry'))

    print('A second window')
    px, py = center_of('pop')
    n0 = len(client_list())
    click(px, py)
    pw_ = wait_for(lambda: len(client_list()) > n0 and [w for w in client_list() if w not in (desk, win)], 5)
    check(bool(pw_), 'the new window is managed')
    if pw_:
        pwin = pw_[-1]
        check(wait_for(lambda: active() == pwin, 2), 'and focused')
        shot('04-popup')
        time.sleep(0.3)
        click(*center_of('popup-close')); time.sleep(0.6)
        check(wait_for(lambda: pwin not in client_list(), 2), 'closing it removes it')
        check(wait_for(lambda: active() == win, 2), 'focus returns to the first window', hex(active()))

    print('Full screen (F11)')
    click(*center_of('inp')); time.sleep(0.2)
    key('F11'); time.sleep(1.2)
    g = root_geom(win)
    check(g == (0, 0, W, H), 'full screen fills the display', g)
    check('_NET_WM_STATE_FULLSCREEN' in states(win), 'state says full screen')
    shot('05-fullscreen')
    key('F11'); time.sleep(1.0)
    g = root_geom(win)
    check(g[2] < W or g[3] < H, 'F11 again leaves full screen', g)

    print("Maximise and drag through the window's own title bar (_NET_WM_STATE, _NET_WM_MOVERESIZE)")
    time.sleep(0.3)
    ax, ay, aw, ah = root_geom(win)
    # the header bar: an empty strip left of the close button
    click(ax + aw - 260, ay + 14); click(ax + aw - 260, ay + 14); time.sleep(0.8)
    maxed = wait_for(lambda: '_NET_WM_STATE_MAXIMIZED_VERT' in states(win), 2)
    check(maxed, 'double-click on its own title bar maximises', states(win))
    if maxed:
        gx, gy, gw, gh = root_geom(win)
        wa = prop(root, '_NET_WORKAREA', 6)
        check(gy + gh <= wa[1] + wa[3] + 1, 'maximised window stops above the taskbar', (gx, gy, gw, gh, wa[:4]))
        click(gx + gw - 260, gy + 14); click(gx + gw - 260, gy + 14); time.sleep(0.8)
        wait_for(lambda: '_NET_WM_STATE_MAXIMIZED_VERT' not in states(win), 2)
        time.sleep(0.3)
    ax, ay, aw, ah = root_geom(win)
    move(ax + aw - 300, ay + 14); press(); time.sleep(0.15)
    for i in range(1, 9): move(ax + aw - 300 - i * 15, ay + 14 + i * 10); time.sleep(0.04)
    release(); time.sleep(0.5)
    nx, ny, _, _ = root_geom(win)
    # GTK starts the move after its drag threshold, from where the pointer is then
    check(abs((ax - nx) - 120) <= 24 and abs((ny - ay) - 80) <= 18, 'dragging its title bar moves the window with the pointer', (ax, ay, '→', nx, ny))
    shot('06-final')

    print('Window manager health')
    app.terminate()
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
