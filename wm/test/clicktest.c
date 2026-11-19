/*
 * Click delivery through redstar-wm, the way Avalonia receives input (XInput 2.2).
 *   clicktest client <name> [desktop]   — a window that counts XI2 button presses/releases into _CLICKS
 *   clicktest drive                       — clicks the clients with XTest and checks every click arrives once
 * Run by wm/test/run-tests.sh.
 */
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <X11/Xutil.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/time.h>
#include <unistd.h>

/* ── the bits of XInput2 we need (no headers on every machine) ── */
typedef struct { int deviceid; int mask_len; unsigned char *mask; } XIEventMask;
extern int XISelectEvents(Display *, Window, XIEventMask *, int);
extern int XIQueryVersion(Display *, int *, int *);
typedef struct { int type; unsigned long serial; Bool send_event; Display *display; int extension; int evtype; Time time;
                 int deviceid, sourceid, detail; } XIDeviceEventHead;
#define XIAllMasterDevices 1
#define XI_ButtonPress 4
#define XI_ButtonRelease 5
extern int XTestFakeMotionEvent(Display *, int, int, int, unsigned long);
extern int XTestFakeButtonEvent(Display *, unsigned int, Bool, unsigned long);

static double now_ms(void) { struct timeval t; gettimeofday(&t, NULL); return t.tv_sec * 1000.0 + t.tv_usec / 1000.0; }

static int client(const char *name, int desktop)
{
    Display *d = XOpenDisplay(NULL);
    int op, ev, er, maj = 2, min = 2;
    if (!XQueryExtension(d, "XInputExtension", &op, &ev, &er) || XIQueryVersion(d, &maj, &min) != Success) { fprintf(stderr, "no XI2\n"); return 2; }
    Window root = DefaultRootWindow(d);
    Window w = XCreateSimpleWindow(d, root, 0, 0, desktop ? 400 : 500, desktop ? 300 : 350, 0, 0, 0xdddddd);
    XStoreName(d, w, name);
    if (!desktop) {             /* app-a top-left, app-b overlapping it lower right */
        XSizeHints h = {0}; h.flags = USPosition;
        h.x = !strcmp(name, "app-a") ? 100 : 400; h.y = !strcmp(name, "app-a") ? 100 : 250;
        XSetWMNormalHints(d, w, &h);
        XMoveWindow(d, w, h.x, h.y);
    }
    if (desktop) {
        Atom t = XInternAtom(d, "_NET_WM_WINDOW_TYPE_DESKTOP", False);
        XChangeProperty(d, w, XInternAtom(d, "_NET_WM_WINDOW_TYPE", False), XA_ATOM, 32, PropModeReplace, (unsigned char *)&t, 1);
    }
    unsigned char m[4] = {0};
    m[XI_ButtonPress >> 3] |= 1 << (XI_ButtonPress & 7);
    m[XI_ButtonRelease >> 3] |= 1 << (XI_ButtonRelease & 7);
    XIEventMask mask = { XIAllMasterDevices, sizeof m, m };
    XISelectEvents(d, w, &mask, 1);
    XSelectInput(d, w, StructureNotifyMask);
    XMapWindow(d, w);
    Atom clicks = XInternAtom(d, "_CLICKS", False);
    long counts[4] = { 0, 0, 0, 0 };   /* presses (button 1), releases (button 1), wheel presses, last press time */
    XChangeProperty(d, w, clicks, XA_CARDINAL, 32, PropModeReplace, (unsigned char *)counts, 4);
    XFlush(d);
    for (;;) {
        XEvent e;
        XNextEvent(d, &e);
        if (e.type == GenericEvent && e.xcookie.extension == op && XGetEventData(d, &e.xcookie)) {
            XIDeviceEventHead *de = e.xcookie.data;
            if (de->evtype == XI_ButtonPress && de->detail == 1) counts[0]++;
            if (de->evtype == XI_ButtonRelease && de->detail == 1) counts[1]++;
            if (de->evtype == XI_ButtonPress && (de->detail == 4 || de->detail == 5)) counts[2]++;
            counts[3] = (long)(now_ms()) % 100000000;
            XFreeEventData(d, &e.xcookie);
            XChangeProperty(d, w, clicks, XA_CARDINAL, 32, PropModeReplace, (unsigned char *)counts, 4);
            XFlush(d);
        }
    }
}

/* ── driver ── */
static Display *d;
static Window root;
static int fails, passes;
#define CHECK(c, ...) do { if (c) { passes++; printf("  ok   "); } else { fails++; printf("  FAIL "); } printf(__VA_ARGS__); printf("\n"); } while (0)

static Window find_named(const char *name)
{
    Atom rt; int rf; unsigned long n, after; unsigned char *data = NULL;
    XGetWindowProperty(d, root, XInternAtom(d, "_NET_CLIENT_LIST", False), 0, 1024, False, XA_WINDOW, &rt, &rf, &n, &after, &data);
    Window found = 0;
    for (unsigned long i = 0; data && i < n; i++) {
        char *nm = NULL;
        if (XFetchName(d, ((Window *)data)[i], &nm) && nm) { if (!strcmp(nm, name)) found = ((Window *)data)[i]; XFree(nm); }
    }
    if (data) XFree(data);
    return found;
}
static long clicks(Window w, int i)
{
    Atom rt; int rf; unsigned long n, after; unsigned char *data = NULL; long v = -1;
    if (XGetWindowProperty(d, w, XInternAtom(d, "_CLICKS", False), 0, 4, False, XA_CARDINAL, &rt, &rf, &n, &after, &data) == Success && data && n > (unsigned long)i)
        v = ((long *)data)[i];
    if (data) XFree(data);
    return v;
}
static Window active(void)
{
    Atom rt; int rf; unsigned long n, after; unsigned char *data = NULL; Window v = 0;
    if (XGetWindowProperty(d, root, XInternAtom(d, "_NET_ACTIVE_WINDOW", False), 0, 1, False, XA_WINDOW, &rt, &rf, &n, &after, &data) == Success && data && n)
        v = *(Window *)data;
    if (data) XFree(data);
    return v;
}
static void center(Window w, int *x, int *y)
{
    XWindowAttributes a; Window ch;
    XGetWindowAttributes(d, w, &a);
    XTranslateCoordinates(d, w, root, a.width / 2, a.height / 2, x, y, &ch);
}
/* one click; returns ms until the window has seen the press, or -1 */
static double click_at(Window w, int x, int y, unsigned int button, int gap_ms)
{
    long before = clicks(w, button == 1 ? 0 : 2);
    XTestFakeMotionEvent(d, -1, x, y, 0);
    XSync(d, False);
    double t0 = now_ms();
    XTestFakeButtonEvent(d, button, True, 0);
    XSync(d, False);
    double seen = -1;
    for (int i = 0; i < 400; i++) {
        if (clicks(w, button == 1 ? 0 : 2) > before) { seen = now_ms() - t0; break; }
        usleep(500);
    }
    XTestFakeButtonEvent(d, button, False, 0);
    XSync(d, False);
    usleep(gap_ms * 1000);
    return seen;
}

static int drive(void)
{
    d = XOpenDisplay(NULL);
    root = DefaultRootWindow(d);
    Window desk = 0, a = 0, b = 0;
    for (int i = 0; i < 100 && !(desk && a && b); i++) { usleep(50000); desk = find_named("desk"); a = find_named("app-a"); b = find_named("app-b"); }
    CHECK(desk && a && b, "test windows are managed");
    if (!(desk && a && b)) return 1;
    int x, y;
    double worst = 0, total = 0; int n = 0;

    printf("Clicking the window that already has focus\n");
    XRaiseWindow(d, a);
    center(b, &x, &y); click_at(b, x, y, 1, 150);          /* b on top and active */
    long p0 = clicks(b, 0), r0 = clicks(b, 1);
    for (int i = 0; i < 10; i++) { double t = click_at(b, x, y, 1, 120); if (t > worst) worst = t; if (t >= 0) { total += t; n++; } }
    CHECK(clicks(b, 0) - p0 == 10, "10 clicks → %ld presses", clicks(b, 0) - p0);
    CHECK(clicks(b, 1) - r0 == 10, "10 clicks → %ld releases", clicks(b, 1) - r0);

    printf("Clicking a window behind another one (raise + focus + the click itself)\n");
    int ax, ay;
    {
        XWindowAttributes wa; Window ch;
        XGetWindowAttributes(d, a, &wa);
        XTranslateCoordinates(d, a, root, 20, 20, &ax, &ay, &ch);               /* a corner of a that b doesn't cover */
    }
    long pa = clicks(a, 0);
    double t = click_at(a, ax, ay, 1, 200);
    CHECK(clicks(a, 0) - pa == 1, "the first click reaches the app (%ld press)", clicks(a, 0) - pa);
    CHECK(active() == a, "and focuses it");
    if (t > worst) worst = t;

    printf("Double-click (opening an icon)\n");
    long pd = clicks(a, 0);
    click_at(a, ax, ay, 1, 60); click_at(a, ax, ay, 1, 200);
    CHECK(clicks(a, 0) - pd == 2, "both clicks of a double-click arrive (%ld)", clicks(a, 0) - pd);

    printf("The Red Star desktop (taskbar, Start button, icons)\n");
    {
        XWindowAttributes wa; Window ch;
        XGetWindowAttributes(d, desk, &wa);
        XTranslateCoordinates(d, desk, root, wa.width - 20, wa.height - 10, &x, &y, &ch);   /* bottom-right: the taskbar */
    }
    long pk = clicks(desk, 0);
    t = click_at(desk, x, y, 1, 150);
    CHECK(clicks(desk, 0) - pk == 1, "first click on the desktop while an app is active arrives (%ld)", clicks(desk, 0) - pk);
    if (t > worst) worst = t;
    pk = clicks(desk, 0);
    for (int i = 0; i < 5; i++) { double tt = click_at(desk, x, y, 1, 120); if (tt > worst) worst = tt; if (tt >= 0) { total += tt; n++; } }
    CHECK(clicks(desk, 0) - pk == 5, "5 more clicks → %ld", clicks(desk, 0) - pk);
    click_at(b, 0, 0, 1, 0);   /* no-op to keep the pointer state clean */

    printf("Scrolling\n");
    center(b, &x, &y);
    click_at(b, x, y, 1, 100);
    long pw = clicks(b, 2);
    for (int i = 0; i < 10; i++) click_at(b, x, y, 5, 20);
    CHECK(clicks(b, 2) - pw == 10, "10 wheel steps arrive (%ld)", clicks(b, 2) - pw);

    printf("Latency\n");
    CHECK(worst < 40, "slowest click reached the app in %.1f ms (average %.1f ms)", worst, n ? total / n : 0);

    printf("\n%d passed, %d failed\n", passes, fails);
    return fails ? 1 : 0;
}

int main(int argc, char **argv)
{
    if (argc >= 3 && !strcmp(argv[1], "client")) return client(argv[2], argc >= 4 && !strcmp(argv[3], "desktop"));
    if (argc >= 2 && !strcmp(argv[1], "drive")) return drive();
    fprintf(stderr, "usage: clicktest client <name> [desktop] | clicktest drive\n");
    return 2;
}
