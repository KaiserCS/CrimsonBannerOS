/*
 * Behaviour tests for redstar-wm. Run with: wm/test/run-tests.sh (starts Xvfb + the window manager).
 * Each test acts like a real app or like the Red Star shell and checks what the window manager did.
 */
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <X11/Xutil.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern int XTestFakeMotionEvent(Display *, int, int, int, unsigned long);
extern int XTestFakeButtonEvent(Display *, unsigned int, Bool, unsigned long);

static Display *d;
static Window root;
static int SW, SH, fails, passes;

#define CHECK(cond, ...) do { if (cond) { passes++; printf("  ok   "); } else { fails++; printf("  FAIL "); } printf(__VA_ARGS__); printf("\n"); } while (0)

static Atom at(const char *n) { return XInternAtom(d, n, False); }
static void settle(void) { XSync(d, False); usleep(150000); XSync(d, False); }

static long *cards(Window w, const char *prop, Atom type, unsigned long *n)
{
    Atom rt; int rf; unsigned long after; unsigned char *data = NULL;
    *n = 0;
    if (XGetWindowProperty(d, w, at(prop), 0, 1024, False, type, &rt, &rf, n, &after, &data) != Success || !data) { *n = 0; return NULL; }
    return (long *)data;
}
static long card(Window w, const char *prop, Atom type, int i)
{
    unsigned long n; long *v = cards(w, prop, type, &n); long r = (v && (unsigned long)i < n) ? v[i] : -12345;
    if (v) XFree(v);
    return r;
}
static int has_atom(Window w, const char *prop, const char *a)
{
    unsigned long n; long *v = cards(w, prop, XA_ATOM, &n); int f = 0;
    for (unsigned long i = 0; v && i < n; i++) if ((Atom)v[i] == at(a)) f = 1;
    if (v) XFree(v);
    return f;
}
static int in_list(const char *prop, Window w)
{
    unsigned long n; long *v = cards(root, prop, XA_WINDOW, &n); int f = 0;
    for (unsigned long i = 0; v && i < n; i++) if ((Window)v[i] == w) f = 1;
    if (v) XFree(v);
    return f;
}
static Window parent_of(Window w)
{
    Window r, p, *k; unsigned int n;
    if (!XQueryTree(d, w, &r, &p, &k, &n)) return 0;
    if (k) XFree(k);
    return p;
}
static void root_geom(Window w, int *x, int *y, int *wd, int *ht)
{
    XWindowAttributes a; Window ch;
    XGetWindowAttributes(d, w, &a);
    XTranslateCoordinates(d, w, root, 0, 0, x, y, &ch);
    *wd = a.width; *ht = a.height;
}
static int viewable(Window w) { XWindowAttributes a; return XGetWindowAttributes(d, w, &a) && a.map_state == IsViewable; }

static Window make(const char *title, int x, int y, int w, int h)
{
    Window win = XCreateSimpleWindow(d, root, x, y, w, h, 0, 0, 0xe8e8e8);
    XStoreName(d, win, title);
    Atom del = at("WM_DELETE_WINDOW");
    XSetWMProtocols(d, win, &del, 1);
    XSelectInput(d, win, StructureNotifyMask | PropertyChangeMask);
    return win;
}
static void msg(Window w, const char *type, long a, long b, long c, long e, long f)
{
    XEvent ev = {0};
    ev.xclient.type = ClientMessage; ev.xclient.window = w; ev.xclient.message_type = at(type); ev.xclient.format = 32;
    ev.xclient.data.l[0] = a; ev.xclient.data.l[1] = b; ev.xclient.data.l[2] = c; ev.xclient.data.l[3] = e; ev.xclient.data.l[4] = f;
    XSendEvent(d, root, False, SubstructureRedirectMask | SubstructureNotifyMask, &ev);
    settle();
}
static void set_type(Window w, const char *type)
{
    Atom t = at(type);
    XChangeProperty(d, w, at("_NET_WM_WINDOW_TYPE"), XA_ATOM, 32, PropModeReplace, (unsigned char *)&t, 1);
}
static void click_drag(int x1, int y1, int x2, int y2)
{
    XTestFakeMotionEvent(d, -1, x1, y1, 0); settle();
    XTestFakeButtonEvent(d, 1, True, 0); settle();
    XTestFakeMotionEvent(d, -1, (x1 + x2) / 2, (y1 + y2) / 2, 0); settle();
    XTestFakeMotionEvent(d, -1, x2, y2, 0); settle();
    XTestFakeButtonEvent(d, 1, False, 0); settle();
}
static int got_delete(Window w)
{
    XEvent ev;
    while (XCheckTypedWindowEvent(d, w, ClientMessage, &ev))
        if ((Atom)ev.xclient.data.l[0] == at("WM_DELETE_WINDOW")) return 1;
    return 0;
}

int main(void)
{
    d = XOpenDisplay(NULL);
    if (!d) { printf("no display\n"); return 2; }
    root = DefaultRootWindow(d);
    SW = DisplayWidth(d, DefaultScreen(d)); SH = DisplayHeight(d, DefaultScreen(d));
    int x, y, w, h;
    const long TH = 32, BW = 3;

    printf("Window manager is present\n");
    /* the test may start before redstar-wm has set up its root properties: give it up to 5 s */
    Window chk = 0;
    for (int i = 0; i < 100; i++) {
        chk = (Window)card(root, "_NET_SUPPORTING_WM_CHECK", XA_WINDOW, 0);
        if (chk && chk != (Window)-12345 && has_atom(root, "_NET_SUPPORTED", "_NET_WM_MOVERESIZE")) break;
        usleep(50000);
    }
    CHECK(chk && chk != (Window)-12345, "_NET_SUPPORTING_WM_CHECK is set");
    CHECK(card(root, "_NET_NUMBER_OF_DESKTOPS", XA_CARDINAL, 0) == 1, "a single workspace");
    CHECK(has_atom(root, "_NET_SUPPORTED", "_NET_WM_MOVERESIZE"), "supports _NET_WM_MOVERESIZE");

    printf("Red Star desktop (type DESKTOP, small size requested, taskbar strut)\n");
    Window desk = make("Red Star OS", 0, 0, SW / 2, SH / 2);
    set_type(desk, "_NET_WM_WINDOW_TYPE_DESKTOP");
    { Atom f = at("_NET_WM_STATE_FULLSCREEN");      /* Red Star's desktop window asks for full screen */
      XChangeProperty(d, desk, at("_NET_WM_STATE"), XA_ATOM, 32, PropModeReplace, (unsigned char *)&f, 1); }
    XMapWindow(d, desk); settle();
    CHECK(has_atom(desk, "_NET_WM_STATE", "_NET_WM_STATE_FULLSCREEN"), "its full-screen request is confirmed");
    root_geom(desk, &x, &y, &w, &h);
    CHECK(x == 0 && y == 0 && w == SW && h == SH, "desktop fills the screen (%d,%d %dx%d)", x, y, w, h);
    CHECK(card(desk, "_NET_FRAME_EXTENTS", XA_CARDINAL, 2) == 0, "desktop has no title bar");
    long strut[12] = { 0, 0, 0, 48, 0, 0, 0, 0, 0, 0, 0, SW - 1 };
    XChangeProperty(d, desk, at("_NET_WM_STRUT_PARTIAL"), XA_CARDINAL, 32, PropModeReplace, (unsigned char *)strut, 12);
    settle();
    CHECK(card(root, "_NET_WORKAREA", XA_CARDINAL, 3) == SH - 48, "work area leaves 48 px for the taskbar (%ld)", card(root, "_NET_WORKAREA", XA_CARDINAL, 3));

    printf("Avalonia-style full-screen window (state set before mapping) — the '1/4 of the screen' case\n");
    Window fs = make("Fullscreen app", 0, 0, SW / 2, SH / 2);
    Atom fsa = at("_NET_WM_STATE_FULLSCREEN");
    XChangeProperty(d, fs, at("_NET_WM_STATE"), XA_ATOM, 32, PropModeReplace, (unsigned char *)&fsa, 1);
    XMapWindow(d, fs); settle();
    root_geom(fs, &x, &y, &w, &h);
    CHECK(x == 0 && y == 0 && w == SW && h == SH, "fills the screen (%d,%d %dx%d)", x, y, w, h);
    XDestroyWindow(d, fs); settle();
    CHECK(!in_list("_NET_CLIENT_LIST", fs), "destroyed window leaves the client list");

    printf("Normal window\n");
    Window a = make("Red Star Writer — report.docx", 0, 0, 640, 400);
    XMapWindow(d, a); settle();
    Window fa = parent_of(a);
    CHECK(fa != root, "is reparented into a frame");
    CHECK(card(a, "_NET_FRAME_EXTENTS", XA_CARDINAL, 2) == TH && card(a, "_NET_FRAME_EXTENTS", XA_CARDINAL, 0) == BW, "frame extents %ld/%ld", card(a, "_NET_FRAME_EXTENTS", XA_CARDINAL, 0), card(a, "_NET_FRAME_EXTENTS", XA_CARDINAL, 2));
    root_geom(a, &x, &y, &w, &h);
    CHECK(w == 640 && h == 400, "keeps its size (%dx%d)", w, h);
    CHECK(abs(x + w / 2 - SW / 2) < 8, "centred horizontally (x=%d)", x);
    CHECK(in_list("_NET_CLIENT_LIST", a), "in _NET_CLIENT_LIST");
    CHECK((Window)card(root, "_NET_ACTIVE_WINDOW", XA_WINDOW, 0) == a, "becomes the active window");
    CHECK(card(a, "WM_STATE", at("WM_STATE"), 0) == NormalState, "WM_STATE normal");
    CHECK(card(fa, "_REDSTAR_SHADOW", XA_CARDINAL, 0) == 1, "its frame asks the compositor for a shadow");
    CHECK(card(parent_of(desk), "_REDSTAR_SHADOW", XA_CARDINAL, 0) != 1, "the desktop gets no shadow");

    printf("Positioned window (Avalonia sets USPosition: frame goes at the requested point)\n");
    Window b = make("Files", 100, 120, 500, 300);
    XSizeHints sh = {0}; sh.flags = USPosition; sh.x = 100; sh.y = 120;
    XSetWMNormalHints(d, b, &sh);
    XMapWindow(d, b); settle();
    root_geom(parent_of(b), &x, &y, &w, &h);
    CHECK(x == 100 && y == 120, "frame at 100,120 (%d,%d)", x, y);
    CHECK((Window)card(root, "_NET_ACTIVE_WINDOW", XA_WINDOW, 0) == b, "new window is active");

    printf("Maximise / restore (_NET_WM_STATE)\n");
    msg(a, "_NET_WM_STATE", 1, at("_NET_WM_STATE_MAXIMIZED_VERT"), at("_NET_WM_STATE_MAXIMIZED_HORZ"), 1, 0);
    root_geom(a, &x, &y, &w, &h);
    CHECK(x == 0 && y == TH && w == SW && h == SH - 48 - TH, "maximised above the taskbar (%d,%d %dx%d)", x, y, w, h);
    CHECK(has_atom(a, "_NET_WM_STATE", "_NET_WM_STATE_MAXIMIZED_VERT"), "state says maximised");
    CHECK(card(parent_of(a), "_REDSTAR_SHADOW", XA_CARDINAL, 0) != 1, "no shadow while maximised");
    msg(a, "_NET_WM_STATE", 0, at("_NET_WM_STATE_MAXIMIZED_VERT"), at("_NET_WM_STATE_MAXIMIZED_HORZ"), 1, 0);
    root_geom(a, &x, &y, &w, &h);
    CHECK(w == 640 && h == 400, "restored to 640x400 (%dx%d)", w, h);
    CHECK(card(parent_of(a), "_REDSTAR_SHADOW", XA_CARDINAL, 0) == 1, "shadow back after restoring");

    printf("Full screen and back\n");
    msg(a, "_NET_WM_STATE", 2, at("_NET_WM_STATE_FULLSCREEN"), 0, 1, 0);
    root_geom(a, &x, &y, &w, &h);
    CHECK(x == 0 && y == 0 && w == SW && h == SH, "full screen (%d,%d %dx%d)", x, y, w, h);
    CHECK(card(parent_of(a), "_REDSTAR_SHADOW", XA_CARDINAL, 0) != 1, "no shadow in full screen");
    msg(a, "_NET_WM_STATE", 2, at("_NET_WM_STATE_FULLSCREEN"), 0, 1, 0);
    root_geom(a, &x, &y, &w, &h);
    CHECK(w == 640 && h == 400, "back to 640x400");

    printf("Minimise (XIconifyWindow) and restore (_NET_ACTIVE_WINDOW from the taskbar)\n");
    XIconifyWindow(d, a, DefaultScreen(d)); settle();
    CHECK(!viewable(a), "hidden");
    CHECK(has_atom(a, "_NET_WM_STATE", "_NET_WM_STATE_HIDDEN"), "_NET_WM_STATE_HIDDEN");
    CHECK(card(a, "WM_STATE", at("WM_STATE"), 0) == IconicState, "WM_STATE iconic");
    CHECK(in_list("_NET_CLIENT_LIST", a), "still in the client list (taskbar)");
    msg(a, "_NET_ACTIVE_WINDOW", 2, CurrentTime, 0, 0, 0);
    CHECK(viewable(a), "shown again");
    CHECK((Window)card(root, "_NET_ACTIVE_WINDOW", XA_WINDOW, 0) == a, "and active");

    printf("Workspace switching is off\n");
    msg(root, "_NET_NUMBER_OF_DESKTOPS", 4, 0, 0, 0, 0);
    msg(root, "_NET_CURRENT_DESKTOP", 1, CurrentTime, 0, 0, 0);
    CHECK(card(root, "_NET_NUMBER_OF_DESKTOPS", XA_CARDINAL, 0) == 1 && card(root, "_NET_CURRENT_DESKTOP", XA_CARDINAL, 0) == 0, "requests for more workspaces or switching are ignored");
    CHECK(viewable(a) && viewable(b) && viewable(desk), "every window stays visible");
    msg(b, "_NET_WM_DESKTOP", 2, 2, 0, 0, 0);
    CHECK(viewable(b) && card(b, "_NET_WM_DESKTOP", XA_CARDINAL, 0) == 0, "a window can't be sent to another workspace");

    printf("Snapping (_NET_MOVERESIZE_WINDOW, as Super+Left does)\n");
    msg(a, "_NET_MOVERESIZE_WINDOW", 1 | (0xF << 8) | (2 << 12), 0, 0, SW / 2 - 2 * card(a, "_NET_FRAME_EXTENTS", XA_CARDINAL, 0),
        SH - 48 - card(a, "_NET_FRAME_EXTENTS", XA_CARDINAL, 2) - card(a, "_NET_FRAME_EXTENTS", XA_CARDINAL, 3));
    root_geom(parent_of(a), &x, &y, &w, &h);
    CHECK(x == 0 && y == 0 && w == SW / 2 && h == SH - 48, "exactly the left half of the work area (%d,%d %dx%d)", x, y, w, h);

    printf("Mouse: drag the title bar, close button\n");
    XRaiseWindow(d, a);
    msg(a, "_NET_ACTIVE_WINDOW", 2, CurrentTime, 0, 0, 0);
    msg(a, "_NET_MOVERESIZE_WINDOW", 1 | (0xF << 8), 200, 150, 640, 400);
    int fx, fy, fw, fh;
    root_geom(parent_of(a), &fx, &fy, &fw, &fh);
    click_drag(fx + 120, fy + 15, fx + 320, fy + 115);
    root_geom(parent_of(a), &x, &y, &w, &h);
    CHECK(x == fx + 200 && y == fy + 100, "moved by 200,100 (%d,%d → %d,%d)", fx, fy, x, y);
    root_geom(parent_of(a), &fx, &fy, &fw, &fh);
    click_drag(fx + fw - 2, fy + fh - 2, fx + fw + 98, fy + fh + 48);
    root_geom(a, &x, &y, &w, &h);
    CHECK(w == 740 && h == 450, "resized from the corner to 740x450 (%dx%d)", w, h);
    root_geom(parent_of(a), &fx, &fy, &fw, &fh);
    /* double-click the title bar */
    XTestFakeMotionEvent(d, -1, fx + 150, fy + 12, 0); settle();
    for (int i = 0; i < 2; i++) { XTestFakeButtonEvent(d, 1, True, 0); XSync(d, False); usleep(40000); XTestFakeButtonEvent(d, 1, False, 0); XSync(d, False); usleep(60000); }
    settle();
    CHECK(has_atom(a, "_NET_WM_STATE", "_NET_WM_STATE_MAXIMIZED_HORZ"), "double-click maximises");
    root_geom(parent_of(a), &fx, &fy, &fw, &fh);
    XTestFakeMotionEvent(d, -1, fx + fw - 10, fy + 12, 0); settle();
    XTestFakeButtonEvent(d, 1, True, 0); settle(); XTestFakeButtonEvent(d, 1, False, 0); settle();
    CHECK(got_delete(a), "close button sends WM_DELETE_WINDOW");

    printf("App's own title bar (_NET_WM_MOVERESIZE while the button is held)\n");
    msg(b, "_NET_ACTIVE_WINDOW", 2, CurrentTime, 0, 0, 0);
    root_geom(b, &fx, &fy, &fw, &fh);
    XTestFakeMotionEvent(d, -1, fx + 50, fy + 20, 0); settle();
    XTestFakeButtonEvent(d, 1, True, 0); settle();
    XUngrabPointer(d, CurrentTime);
    XEvent ev = {0};
    ev.xclient.type = ClientMessage; ev.xclient.window = b; ev.xclient.message_type = at("_NET_WM_MOVERESIZE"); ev.xclient.format = 32;
    ev.xclient.data.l[0] = fx + 50; ev.xclient.data.l[1] = fy + 20; ev.xclient.data.l[2] = 8; ev.xclient.data.l[3] = 1; ev.xclient.data.l[4] = 1;
    XSendEvent(d, root, False, SubstructureRedirectMask | SubstructureNotifyMask, &ev); settle();
    XTestFakeMotionEvent(d, -1, fx + 130, fy + 60, 0); settle();
    XTestFakeMotionEvent(d, -1, fx + 150, fy + 80, 0); settle();
    XTestFakeButtonEvent(d, 1, False, 0); settle();
    root_geom(b, &x, &y, &w, &h);
    CHECK(x == fx + 100 && y == fy + 60, "moved with the pointer (%d,%d → %d,%d)", fx, fy, x, y);

    printf("Snapping (dragged to an edge or a corner)\n");
    {
        long wa0 = card(root, "_NET_WORKAREA", XA_CARDINAL, 0), wa1 = card(root, "_NET_WORKAREA", XA_CARDINAL, 1);
        long wa2 = card(root, "_NET_WORKAREA", XA_CARDINAL, 2), wa3 = card(root, "_NET_WORKAREA", XA_CARDINAL, 3);
        long accent = 0x1E6FD9;
        XChangeProperty(d, root, at("_REDSTAR_ACCENT"), XA_CARDINAL, 32, PropModeReplace, (unsigned char *)&accent, 1);
        settle();
        msg(b, "_NET_ACTIVE_WINDOW", 2, CurrentTime, 0, 0, 0);
        root_geom(parent_of(b), &fx, &fy, &fw, &fh);
        /* hold the window at the left edge: a preview appears (override-redirect windows over the others) */
        Window r0, p0, *k0; unsigned int n0 = 0, n1 = 0;
        XQueryTree(d, root, &r0, &p0, &k0, &n0); if (k0) XFree(k0);
        XTestFakeMotionEvent(d, -1, fx + 60, fy + 12, 0); settle();
        XTestFakeButtonEvent(d, 1, True, 0); settle();
        XTestFakeMotionEvent(d, -1, (int)wa0 + 200, (int)wa1 + 300, 0); settle();
        XTestFakeMotionEvent(d, -1, (int)wa0, (int)wa1 + 300, 0); settle();
        XQueryTree(d, root, &r0, &p0, &k0, &n1); if (k0) XFree(k0);
        CHECK(n1 >= n0 + 4, "a preview shows while it's held at the edge (%u → %u windows)", n0, n1);
        XTestFakeButtonEvent(d, 1, False, 0); settle();
        XQueryTree(d, root, &r0, &p0, &k0, &n1); if (k0) XFree(k0);
        CHECK(n1 == n0, "and goes when it's dropped (%u windows)", n1);
        root_geom(parent_of(b), &x, &y, &w, &h);
        CHECK(x == wa0 && y == wa1 && w == wa2 / 2 && h == wa3, "dropped at the left edge: the left half (%d,%d %dx%d)", x, y, w, h);
        CHECK(card(root, "_REDSTAR_SNAPPED", XA_CARDINAL, 0) == (long)b && card(root, "_REDSTAR_SNAPPED", XA_CARDINAL, 1) == 1,
              "the shell is told (for Snap Assist)");
        /* from the half to the top right corner: a quarter */
        click_drag(x + 80, y + 12, (int)(wa0 + wa2 - 1), (int)wa1);
        root_geom(parent_of(b), &x, &y, &w, &h);
        CHECK(x == wa0 + wa2 / 2 && y == wa1 && w == wa2 - wa2 / 2 && h == wa3 / 2, "dropped in the top right corner: that quarter (%d,%d %dx%d)", x, y, w, h);
        CHECK(card(root, "_REDSTAR_SNAPPED", XA_CARDINAL, 1) == 5, "a quarter is told too");
        /* dragged away: back to its own size */
        click_drag(x + 80, y + 12, x - 300, y + 200);
        root_geom(b, &x, &y, &w, &h);
        CHECK(w == fw - 2 * 3 || w > 100, "dragged away from the corner, it has its own size again (%dx%d)", w, h);
    }

    printf("Special windows\n");
    Window nd = make("Popup without decorations", 300, 300, 200, 100);
    long mot[5] = { 2, 0, 0, 0, 0 };
    XChangeProperty(d, nd, at("_MOTIF_WM_HINTS"), at("_MOTIF_WM_HINTS"), 32, PropModeReplace, (unsigned char *)mot, 5);
    XMapWindow(d, nd); settle();
    CHECK(card(nd, "_NET_FRAME_EXTENTS", XA_CARDINAL, 2) == 0, "Motif 'no decorations' → no title bar");
    CHECK(card(parent_of(nd), "_REDSTAR_SHADOW", XA_CARDINAL, 0) != 1, "and no shadow (it may draw its own see-through edges)");
    XSetWindowAttributes swa; swa.override_redirect = True;
    Window ov = XCreateWindow(d, root, 10, 10, 50, 50, 0, CopyFromParent, InputOutput, CopyFromParent, CWOverrideRedirect, &swa);
    XMapWindow(d, ov); settle();
    CHECK(parent_of(ov) == root && !in_list("_NET_CLIENT_LIST", ov), "menus/tooltips (override-redirect) are left alone");
    msg(b, "_NET_ACTIVE_WINDOW", 2, CurrentTime, 0, 0, 0);       /* raising a window must not cover an open popup */
    {
        Window r2, p2, *k2; unsigned int n2; int top_is_popup = 0;
        if (XQueryTree(d, root, &r2, &p2, &k2, &n2) && n2) { top_is_popup = k2[n2 - 1] == ov; XFree(k2); }
        CHECK(top_is_popup, "an open popup stays above a window that gets raised");
    }
    Window dlg = make("Save changes?", 0, 0, 300, 150);
    XSetTransientForHint(d, dlg, b);
    XMapWindow(d, dlg); settle();
    int bx, by, bw2, bh2;
    root_geom(b, &bx, &by, &bw2, &bh2);
    root_geom(dlg, &x, &y, &w, &h);
    CHECK(abs((x + w / 2) - (bx + bw2 / 2)) < 6 && abs((y + h / 2) - (by + bh2 / 2)) < 6, "dialog centred on its window");

    printf("New windows\n");
    {
        msg(b, "_NET_ACTIVE_WINDOW", 2, CurrentTime, 0, 0, 0);
        Window quiet = make("Alt+Tab panel", 600, 300, 300, 120);
        long zero = 0;
        XChangeProperty(d, quiet, at("_NET_WM_USER_TIME"), XA_CARDINAL, 32, PropModeReplace, (unsigned char *)&zero, 1);
        XMapWindow(d, quiet); settle();
        CHECK((Window)card(root, "_NET_ACTIVE_WINDOW", XA_WINDOW, 0) == b, "a window shown without activation (_NET_WM_USER_TIME 0) doesn't take focus");
        XDestroyWindow(d, quiet); settle();
        Window n1 = make("cascade 1", 0, 0, 400, 300), n2 = make("cascade 2", 0, 0, 400, 300);
        XMapWindow(d, n1); settle(); XMapWindow(d, n2); settle();
        int x1, y1, x2, y2, ww, hh;
        root_geom(n1, &x1, &y1, &ww, &hh); root_geom(n2, &x2, &y2, &ww, &hh);
        CHECK(x2 - x1 == TH && y2 - y1 == TH, "a second window that would land on the first is cascaded (%d,%d → %d,%d)", x1, y1, x2, y2);
        XDestroyWindow(d, n1); XDestroyWindow(d, n2); settle();
    }

    printf("Stacking\n");
    unsigned long n; long *st = cards(root, "_NET_CLIENT_LIST_STACKING", XA_WINDOW, &n);
    CHECK(st && n > 0 && (Window)st[0] == desk, "desktop is at the bottom of the stack");
    if (st) XFree(st);
    msg(desk, "_NET_ACTIVE_WINDOW", 2, CurrentTime, 0, 0, 0);     /* clicking the taskbar focuses the desktop */
    st = cards(root, "_NET_CLIENT_LIST_STACKING", XA_WINDOW, &n);
    CHECK(st && n > 0 && (Window)st[0] == desk, "a focused desktop still stays at the bottom");
    if (st) XFree(st);

    printf("Withdrawing a window\n");
    XUnmapWindow(d, dlg); settle();
    CHECK(!in_list("_NET_CLIENT_LIST", dlg) && parent_of(dlg) == root, "unmapped window is released");

    printf("\n%d passed, %d failed\n", passes, fails);
    if (getenv("WMTEST_HOLD")) {   /* leave the windows up for a screenshot */
        msg(a, "_NET_WM_STATE", 0, at("_NET_WM_STATE_MAXIMIZED_VERT"), at("_NET_WM_STATE_MAXIMIZED_HORZ"), 1, 0);
        msg(a, "_NET_MOVERESIZE_WINDOW", 1 | (0xF << 8), 120, 90, 700, 420);
        msg(b, "_NET_ACTIVE_WINDOW", 2, CurrentTime, 0, 0, 0);
        XSync(d, False); usleep(300000);
        FILE *f = fopen("/tmp/wmtest-holding", "w"); if (f) fclose(f);
        sleep(atoi(getenv("WMTEST_HOLD")));
    }
    return fails ? 1 : 0;
}
