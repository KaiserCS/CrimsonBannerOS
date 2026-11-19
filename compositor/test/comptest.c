/*
 * comptest — behaviour checks for redstar-comp, run as its client on wlroots' virtual screen:
 *   WLR_BACKENDS=headless WLR_RENDERER=pixman redstar-comp -- comptest
 * Talks X11 through redstar-comp's Xwayland, like the Red Star shell does, and checks the parts the shell relies on.
 * Exit code 0 = every check passed (redstar-comp exits with its client's code).
 */
#include <X11/Xatom.h>
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static Display *d;
static Window root;
static int failed, passed;

static void check(int ok, const char *what)
{
	printf("%s %s\n", ok ? "PASS" : "FAIL", what);
	if (ok) passed++; else failed++;
	fflush(stdout);
}

static Atom A(const char *n) { return XInternAtom(d, n, False); }

static void pump(int ms)
{
	struct timespec t = { 0, 20 * 1000000L };
	for (int i = 0; i < ms / 20; i++) {
		while (XPending(d)) { XEvent e; XNextEvent(d, &e); }
		nanosleep(&t, NULL);
	}
}

static int get_cardinals(Window w, Atom prop, long *out, int max)
{
	Atom type; int fmt; unsigned long n, after; unsigned char *data = NULL;
	if (XGetWindowProperty(d, w, prop, 0, max, False, XA_CARDINAL, &type, &fmt, &n, &after, &data) != Success || !data)
		return 0;
	int cnt = (int)n < max ? (int)n : max;
	for (int i = 0; i < cnt; i++) out[i] = ((long *)data)[i];
	XFree(data);
	return cnt;
}

/* waits (up to ~3 s) until the property has at least n values */
static int wait_cardinals(Window w, Atom prop, long *out, int n)
{
	for (int i = 0; i < 150; i++) {
		if (get_cardinals(w, prop, out, n) >= n) return 1;
		pump(20);
	}
	return 0;
}

static void where(Window w, int *x, int *y, unsigned *ww, unsigned *hh)
{
	Window r, c; int gx, gy; unsigned bw, dep;
	XGetGeometry(d, w, &r, &gx, &gy, ww, hh, &bw, &dep);
	XTranslateCoordinates(d, w, root, 0, 0, x, y, &c);
}

static Window make(int x, int y, int w, int h, const char *type, int mapped)
{
	XSetWindowAttributes a = { .background_pixel = 0x336699, .event_mask = StructureNotifyMask | PropertyChangeMask };
	Window win = XCreateWindow(d, root, x, y, w, h, 0, CopyFromParent, InputOutput, CopyFromParent, CWBackPixel | CWEventMask, &a);
	Atom wmdel = A("WM_DELETE_WINDOW");
	XSetWMProtocols(d, win, &wmdel, 1);
	XStoreName(d, win, "comptest");
	if (type) {
		Atom t = A(type);
		XChangeProperty(d, win, A("_NET_WM_WINDOW_TYPE"), XA_ATOM, 32, PropModeReplace, (unsigned char *)&t, 1);
	}
	if (mapped) XMapWindow(d, win);
	XFlush(d);
	return win;
}

static void client_message(Window w, Atom type, long a0, long a1, long a2, long a3, long a4)
{
	XEvent e = {0};
	e.xclient.type = ClientMessage;
	e.xclient.window = w;
	e.xclient.message_type = type;
	e.xclient.format = 32;
	e.xclient.data.l[0] = a0; e.xclient.data.l[1] = a1; e.xclient.data.l[2] = a2;
	e.xclient.data.l[3] = a3; e.xclient.data.l[4] = a4;
	XSendEvent(d, root, False, SubstructureRedirectMask | SubstructureNotifyMask, &e);
	XFlush(d);
}

int main(void)
{
	for (int i = 0; i < 100 && !(d = XOpenDisplay(NULL)); i++) usleep(100000);
	if (!d) { printf("FAIL could not open the X display (Xwayland)\n"); return 1; }
	root = DefaultRootWindow(d);
	int sw = DisplayWidth(d, DefaultScreen(d)), sh = DisplayHeight(d, DefaultScreen(d));
	printf("screen %dx%d\n", sw, sh);
	pump(300);

	/* a window manager and a compositing manager are present */
	check(XGetSelectionOwner(d, A("WM_S0")) != None, "a window manager owns WM_S0");
	check(XGetSelectionOwner(d, A("_NET_WM_CM_S0")) != None, "a compositing manager owns _NET_WM_CM_S0 (see-through windows work)");

	/* a dock with a bottom strut shrinks the work area (Red Star's taskbar) */
	Window dock = make(0, sh - 48, sw, 48, "_NET_WM_WINDOW_TYPE_DOCK", 0);
	long strut[12] = { 0, 0, 0, 48, 0, 0, 0, 0, 0, 0, 0, sw - 1 };
	XChangeProperty(d, dock, A("_NET_WM_STRUT_PARTIAL"), XA_CARDINAL, 32, PropModeReplace, (unsigned char *)strut, 12);
	XMapWindow(d, dock);
	XFlush(d);
	long wa[4] = {0};
	int got = 0;
	for (int i = 0; i < 150; i++) {
		if (get_cardinals(root, A("_NET_WORKAREA"), wa, 4) >= 4 && wa[3] == sh - 48) { got = 1; break; }
		pump(20);
	}
	check(got, "_NET_WORKAREA leaves out the taskbar's strut");
	int dx, dy; unsigned dw, dh;
	where(dock, &dx, &dy, &dw, &dh);
	check(dx == 0 && dy == sh - 48, "the dock stays where it asked to be (no title bar)");

	/* a normal window: framed, title bar extents published */
	Window w = make(0, 0, 400, 300, NULL, 1);
	long ext[4] = {0};
	check(wait_cardinals(w, A("_NET_FRAME_EXTENTS"), ext, 4) && ext[2] >= 20 && ext[0] >= 1,
		"a normal window gets a title bar (_NET_FRAME_EXTENTS)");
	pump(200);
	int x, y; unsigned ww, hh;
	where(w, &x, &y, &ww, &hh);
	check(ww == 400 && hh == 300, "it keeps the size it asked for");
	check(y - ext[2] >= 0 && y + (int)hh <= sh - 48 + 1, "it's placed inside the work area, title bar on screen");
	check(abs((x + (int)ww / 2) - sw / 2) <= 2, "a new window without a position is centred");

	/* the shell's snapping: _NET_MOVERESIZE_WINDOW with x/y/w/h (gravity static) */
	client_message(w, A("_NET_MOVERESIZE_WINDOW"), (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11) | 1, 0, 0, sw / 2, 200);
	int ok = 0;
	for (int i = 0; i < 150 && !ok; i++) {
		pump(20);
		where(w, &x, &y, &ww, &hh);
		ok = (int)ww == sw / 2 && hh == 200;
	}
	check(ok, "_NET_MOVERESIZE_WINDOW resizes (Super+arrow snapping)");
	check(x - ext[0] <= 1 && y - ext[2] <= 1, "_NET_MOVERESIZE_WINDOW places the frame at the given corner");

	/* maximise through _NET_WM_STATE: fills the work area under the title bar */
	client_message(w, A("_NET_WM_STATE"), 1, A("_NET_WM_STATE_MAXIMIZED_VERT"), A("_NET_WM_STATE_MAXIMIZED_HORZ"), 1, 0);
	ok = 0;
	for (int i = 0; i < 150 && !ok; i++) {
		pump(20);
		where(w, &x, &y, &ww, &hh);
		ok = (int)ww == sw && (int)(y + hh) == sh - 48;
	}
	check(ok, "maximising fills the work area, above the taskbar");
	client_message(w, A("_NET_WM_STATE"), 0, A("_NET_WM_STATE_MAXIMIZED_VERT"), A("_NET_WM_STATE_MAXIMIZED_HORZ"), 1, 0);
	ok = 0;
	for (int i = 0; i < 150 && !ok; i++) {
		pump(20);
		where(w, &x, &y, &ww, &hh);
		ok = ((int)ww == sw / 2 && hh == 200) || (ww == 400 && hh == 300);   /* the snapped or the original size */
	}
	check(ok, "restoring returns to the size before");

	/* a client list for the taskbar */
	Atom type; int fmt; unsigned long n = 0, after; unsigned char *data = NULL;
	int listed = 0;
	for (int i = 0; i < 100 && !listed; i++) {
		pump(20);
		if (XGetWindowProperty(d, root, A("_NET_CLIENT_LIST"), 0, 64, False, XA_WINDOW, &type, &fmt, &n, &after, &data) == Success && data) {
			for (unsigned long k = 0; k < n; k++) if (((Window *)data)[k] == w) listed = 1;
			XFree(data);
			data = NULL;
		}
	}
	check(listed, "_NET_CLIENT_LIST lists the window (the taskbar's button)");

	/* minimise (XIconifyWindow) sets _NET_WM_STATE_HIDDEN */
	XIconifyWindow(d, w, DefaultScreen(d));
	XFlush(d);
	int hidden = 0;
	for (int i = 0; i < 100 && !hidden; i++) {
		pump(20);
		if (XGetWindowProperty(d, w, A("_NET_WM_STATE"), 0, 16, False, XA_ATOM, &type, &fmt, &n, &after, &data) == Success && data) {
			for (unsigned long k = 0; k < n; k++) if (((Atom *)data)[k] == A("_NET_WM_STATE_HIDDEN")) hidden = 1;
			XFree(data);
			data = NULL;
		}
	}
	check(hidden, "minimising marks the window hidden");
	client_message(w, A("_NET_ACTIVE_WINDOW"), 2, CurrentTime, 0, 0, 0);
	pump(300);

	/* _NET_CLOSE_WINDOW (the taskbar's Close) turns into WM_DELETE_WINDOW */
	client_message(w, A("_NET_CLOSE_WINDOW"), CurrentTime, 2, 0, 0, 0);
	int deleted = 0;
	for (int i = 0; i < 150 && !deleted; i++) {
		while (XPending(d)) {
			XEvent e;
			XNextEvent(d, &e);
			if (e.type == ClientMessage && (Atom)e.xclient.data.l[0] == A("WM_DELETE_WINDOW") && e.xclient.window == w) deleted = 1;
		}
		usleep(20000);
	}
	check(deleted, "_NET_CLOSE_WINDOW asks the window to close (WM_DELETE_WINDOW)");

	/* the desktop window: full screen, no title bar */
	Window desk = make(100, 100, 300, 200, "_NET_WM_WINDOW_TYPE_DESKTOP", 1);
	ok = 0;
	for (int i = 0; i < 150 && !ok; i++) {
		pump(20);
		where(desk, &x, &y, &ww, &hh);
		ok = x == 0 && y == 0 && (int)ww == sw && (int)hh == sh;
	}
	check(ok, "the desktop window covers the screen");
	long dext[4] = { -1, -1, -1, -1 };
	get_cardinals(desk, A("_NET_FRAME_EXTENTS"), dext, 4);
	check(dext[2] <= 0, "the desktop window has no title bar");

	/* an undecorated window (Motif hints, like Red Star's popups) gets no frame */
	Window bare = make(50, 50, 200, 100, NULL, 0);
	long motif[5] = { 2, 0, 0, 0, 0 };          /* flags = decorations, decorations = none */
	Atom mh = A("_MOTIF_WM_HINTS");
	XChangeProperty(d, bare, mh, mh, 32, PropModeReplace, (unsigned char *)motif, 5);
	XMapWindow(d, bare);
	XFlush(d);
	pump(400);
	long bext[4] = { -1, -1, -1, -1 };
	get_cardinals(bare, A("_NET_FRAME_EXTENTS"), bext, 4);
	check(bext[2] <= 0, "a window that asks for no decorations gets no title bar");

	printf("%d passed, %d failed\n", passed, failed);
	XCloseDisplay(d);
	return failed ? 1 : 0;
}
