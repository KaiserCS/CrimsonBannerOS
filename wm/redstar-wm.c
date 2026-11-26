/*
 * redstar-wm — the Red Star OS window manager.
 *
 * Used inside Red Star's own display (keyboard/mouse isolation) and in the Red Star session when no other window
 * manager is installed. Small, Xlib only (titles use Xft when the library is present, loaded at run time):
 *   - Red Star title bars: minimise / maximise / close, double-click to maximise, drag to move,
 *     edges and corners to resize, Alt+drag to move, Alt+right-drag to resize, Alt+F4 to close.
 *   - EWMH for the shell and for apps: _NET_ACTIVE_WINDOW, _NET_CLIENT_LIST(_STACKING), _NET_WM_STATE
 *     (fullscreen, maximised, hidden, above, skip taskbar), _NET_WM_MOVERESIZE (apps' own title bars),
 *     _NET_MOVERESIZE_WINDOW (snapping), _NET_CLOSE_WINDOW, workspaces (_NET_NUMBER_OF_DESKTOPS,
 *     _NET_CURRENT_DESKTOP, _NET_WM_DESKTOP), struts → _NET_WORKAREA, _NET_FRAME_EXTENTS, window types
 *     (desktop at the bottom and full screen, docks and notifications on top, dialogs centred).
 *   - ICCCM: WM_STATE, WM_DELETE_WINDOW, WM_TAKE_FOCUS, WM_CHANGE_STATE (iconify), size hints,
 *     transient windows, Motif "no decorations" hints.
 * Alt+Tab, Super and the other shortcuts are Red Star's own (X11Desktop), so they are not bound here.
 *
 * Build: gcc -O2 -Wall -o redstar-wm redstar-wm.c -lX11 -ldl
 */
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <X11/Xutil.h>
#include <X11/cursorfont.h>
#include <X11/keysym.h>
#include <dlfcn.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* ───────────────────────── minimal Xft, loaded at run time ───────────────────────── */
typedef struct { unsigned short red, green, blue, alpha; } XRColor;
typedef struct { unsigned long pixel; XRColor color; } XftColorT;
typedef struct { int ascent, descent, height, max_advance_width; void *charset, *pattern; } XftFontT;
typedef struct { unsigned short width, height; short x, y, xOff, yOff; } XGlyphInfoT;
static void *(*pXftFontOpenName)(Display *, int, const char *);
static void *(*pXftDrawCreate)(Display *, Drawable, Visual *, Colormap);
static void (*pXftDrawDestroy)(void *);
static int (*pXftColorAllocValue)(Display *, Visual *, Colormap, const XRColor *, XftColorT *);
static void (*pXftDrawStringUtf8)(void *, const XftColorT *, void *, int, int, const unsigned char *, int);
static void (*pXftTextExtentsUtf8)(Display *, void *, const unsigned char *, int, XGlyphInfoT *);

/* ───────────────────────── state ───────────────────────── */
enum { T_NORMAL, T_DIALOG, T_DESKTOP, T_DOCK, T_SPLASH, T_UTILITY, T_NOTIFY, T_MENU };
enum { DRAG_NONE, DRAG_MOVE, DRAG_RESIZE, DRAG_BUTTON };
enum { BTN_NONE = 0, BTN_MIN, BTN_MAX, BTN_CLOSE };

typedef struct Client {
    Window win, frame;
    int x, y, w, h;                 /* client area in root coordinates */
    int sx, sy, sw, sh;             /* saved geometry before maximise / fullscreen */
    int type, decorated, desktop;   /* desktop: 0.. or -1 = all */
    int maximized, fullscreen, minimized, above, skip_taskbar, sticky;
    int snapped;                    /* a half or a quarter (ZONE_*), 0 = none */
    int del_proto, focus_proto, input_hint;
    int min_w, min_h, max_w, max_h;
    int ignore_unmap;
    int desk_full;                  /* the desktop asked for full screen: keep reporting it */
    Window transient_for;
    char title[512];
    struct Client *next;            /* management order */
    struct Client *snext;           /* stacking order, bottom → top */
} Client;

static Display *dpy;
static Window root, check_win;
static int scr, SW, SH;
static double scale = 1.0;
static int TH = 32, BW = 3;         /* title height, border (scaled) */
static Client *clients, *stack, *active;
static void snap_preview_hide(void);
static int ndesk = 1, curdesk = 0;     /* Red Star uses a single workspace */
static int wa_x, wa_y, wa_w, wa_h;  /* work area */
static Cursor cur_normal, cur_move, cur_edge[8];
static GC gc;
static XFontStruct *corefont;
static void *xftfont, *xftdraw;
static XftColorT xft_white, xft_dim;
static int other_wm;
static int debug;                    /* REDSTAR_WM_DEBUG=1: log clicks and focus to stderr (→ session.log) */
#define DBG(...) do { if (debug) { fprintf(stderr, "redstar-wm: " __VA_ARGS__); fputc('\n', stderr); } } while (0)

/* drag */
static int drag_mode = DRAG_NONE, drag_edge, drag_btn, drag_px, drag_py, drag_fx, drag_fy, drag_fw, drag_fh;
static Client *drag_c;
static int hover_btn; static Client *hover_c;
static Time last_click; static Client *last_click_c;

/* colours */
static unsigned long col_title_active, col_title_inactive, col_border, col_accent, col_text, col_text_dim, col_close_hover, col_btn_hover;

/* atoms */
enum {
    WM_PROTOCOLS, WM_DELETE_WINDOW, WM_TAKE_FOCUS, WM_STATE, WM_CHANGE_STATE,
    NET_SUPPORTED, NET_SUPPORTING_WM_CHECK, NET_WM_NAME, UTF8_STRING, NET_CLIENT_LIST, NET_CLIENT_LIST_STACKING,
    NET_ACTIVE_WINDOW, NET_NUMBER_OF_DESKTOPS, NET_CURRENT_DESKTOP, NET_DESKTOP_NAMES, NET_DESKTOP_VIEWPORT, NET_DESKTOP_GEOMETRY,
    NET_WORKAREA, NET_WM_DESKTOP, NET_WM_STATE, NET_WM_STATE_FULLSCREEN, NET_WM_STATE_MAXIMIZED_VERT,
    NET_WM_STATE_MAXIMIZED_HORZ, NET_WM_STATE_HIDDEN, NET_WM_STATE_ABOVE, NET_WM_STATE_SKIP_TASKBAR,
    NET_WM_STATE_SKIP_PAGER, NET_WM_STATE_STICKY, NET_WM_STATE_DEMANDS_ATTENTION, NET_WM_STATE_FOCUSED,
    NET_WM_WINDOW_TYPE, NET_WM_WINDOW_TYPE_NORMAL, NET_WM_WINDOW_TYPE_DIALOG, NET_WM_WINDOW_TYPE_DESKTOP,
    NET_WM_WINDOW_TYPE_DOCK, NET_WM_WINDOW_TYPE_SPLASH, NET_WM_WINDOW_TYPE_UTILITY, NET_WM_WINDOW_TYPE_TOOLBAR,
    NET_WM_WINDOW_TYPE_NOTIFICATION, NET_WM_WINDOW_TYPE_MENU, NET_WM_WINDOW_TYPE_POPUP_MENU,
    NET_WM_WINDOW_TYPE_DROPDOWN_MENU, NET_WM_WINDOW_TYPE_TOOLTIP, NET_WM_WINDOW_TYPE_COMBO, NET_WM_WINDOW_TYPE_DND,
    NET_WM_MOVERESIZE, NET_MOVERESIZE_WINDOW, NET_CLOSE_WINDOW, NET_FRAME_EXTENTS, NET_REQUEST_FRAME_EXTENTS,
    NET_WM_STRUT, NET_WM_STRUT_PARTIAL, NET_WM_ALLOWED_ACTIONS, NET_WM_ACTION_MOVE, NET_WM_ACTION_RESIZE,
    NET_WM_ACTION_MINIMIZE, NET_WM_ACTION_MAXIMIZE_HORZ, NET_WM_ACTION_MAXIMIZE_VERT, NET_WM_ACTION_FULLSCREEN,
    NET_WM_ACTION_CLOSE, NET_WM_ACTION_CHANGE_DESKTOP, NET_WM_PID, MOTIF_WM_HINTS, NET_SHOWING_DESKTOP, NET_WM_USER_TIME, REDSTAR_SHADOW,
    REDSTAR_ACCENT, REDSTAR_SNAPPED, NET_WM_WINDOW_OPACITY, NET_WM_CM_S0, ATOM_COUNT
};
static const char *atom_names[ATOM_COUNT] = {
    "WM_PROTOCOLS", "WM_DELETE_WINDOW", "WM_TAKE_FOCUS", "WM_STATE", "WM_CHANGE_STATE",
    "_NET_SUPPORTED", "_NET_SUPPORTING_WM_CHECK", "_NET_WM_NAME", "UTF8_STRING", "_NET_CLIENT_LIST", "_NET_CLIENT_LIST_STACKING",
    "_NET_ACTIVE_WINDOW", "_NET_NUMBER_OF_DESKTOPS", "_NET_CURRENT_DESKTOP", "_NET_DESKTOP_NAMES", "_NET_DESKTOP_VIEWPORT", "_NET_DESKTOP_GEOMETRY",
    "_NET_WORKAREA", "_NET_WM_DESKTOP", "_NET_WM_STATE", "_NET_WM_STATE_FULLSCREEN", "_NET_WM_STATE_MAXIMIZED_VERT",
    "_NET_WM_STATE_MAXIMIZED_HORZ", "_NET_WM_STATE_HIDDEN", "_NET_WM_STATE_ABOVE", "_NET_WM_STATE_SKIP_TASKBAR",
    "_NET_WM_STATE_SKIP_PAGER", "_NET_WM_STATE_STICKY", "_NET_WM_STATE_DEMANDS_ATTENTION", "_NET_WM_STATE_FOCUSED",
    "_NET_WM_WINDOW_TYPE", "_NET_WM_WINDOW_TYPE_NORMAL", "_NET_WM_WINDOW_TYPE_DIALOG", "_NET_WM_WINDOW_TYPE_DESKTOP",
    "_NET_WM_WINDOW_TYPE_DOCK", "_NET_WM_WINDOW_TYPE_SPLASH", "_NET_WM_WINDOW_TYPE_UTILITY", "_NET_WM_WINDOW_TYPE_TOOLBAR",
    "_NET_WM_WINDOW_TYPE_NOTIFICATION", "_NET_WM_WINDOW_TYPE_MENU", "_NET_WM_WINDOW_TYPE_POPUP_MENU",
    "_NET_WM_WINDOW_TYPE_DROPDOWN_MENU", "_NET_WM_WINDOW_TYPE_TOOLTIP", "_NET_WM_WINDOW_TYPE_COMBO", "_NET_WM_WINDOW_TYPE_DND",
    "_NET_WM_MOVERESIZE", "_NET_MOVERESIZE_WINDOW", "_NET_CLOSE_WINDOW", "_NET_FRAME_EXTENTS", "_NET_REQUEST_FRAME_EXTENTS",
    "_NET_WM_STRUT", "_NET_WM_STRUT_PARTIAL", "_NET_WM_ALLOWED_ACTIONS", "_NET_WM_ACTION_MOVE", "_NET_WM_ACTION_RESIZE",
    "_NET_WM_ACTION_MINIMIZE", "_NET_WM_ACTION_MAXIMIZE_HORZ", "_NET_WM_ACTION_MAXIMIZE_VERT", "_NET_WM_ACTION_FULLSCREEN",
    "_NET_WM_ACTION_CLOSE", "_NET_WM_ACTION_CHANGE_DESKTOP", "_NET_WM_PID", "_MOTIF_WM_HINTS", "_NET_SHOWING_DESKTOP", "_NET_WM_USER_TIME", "_REDSTAR_SHADOW",
    "_REDSTAR_ACCENT", "_REDSTAR_SNAPPED", "_NET_WM_WINDOW_OPACITY", "_NET_WM_CM_S0"
};
static Atom A[ATOM_COUNT];

/* ───────────────────────── helpers ───────────────────────── */
static int xerror(Display *d, XErrorEvent *e)
{
    (void)d;
    if (e->request_code == 2 /* ChangeWindowAttributes */ && e->error_code == BadAccess) { other_wm = 1; return 0; }
    return 0;  /* windows vanish all the time; ignore */
}

static unsigned long rgb(const char *hex)
{
    XColor c;
    Colormap cm = DefaultColormap(dpy, scr);
    if (XParseColor(dpy, cm, hex, &c) && XAllocColor(dpy, cm, &c)) return c.pixel;
    return WhitePixel(dpy, scr);
}

static int S(int v) { return (int)(v * scale + 0.5); }

static Client *find_client(Window w)
{
    for (Client *c = clients; c; c = c->next)
        if (c->win == w || c->frame == w) return c;
    return NULL;
}

static long *get_cardinals(Window w, Atom prop, Atom type, unsigned long *n)
{
    Atom rt; int rf; unsigned long after; unsigned char *data = NULL;
    *n = 0;
    if (XGetWindowProperty(dpy, w, prop, 0, 1024, False, type, &rt, &rf, n, &after, &data) != Success || !data || rf != 32) {
        if (data) XFree(data);
        *n = 0;
        return NULL;
    }
    return (long *)data;
}

static void set_cardinals(Window w, Atom prop, Atom type, long *v, int n)
{
    XChangeProperty(dpy, w, prop, type, 32, PropModeReplace, (unsigned char *)v, n);
}

static int has_proto(Window w, Atom proto)
{
    Atom *p; int n, found = 0;
    if (XGetWMProtocols(dpy, w, &p, &n)) {
        for (int i = 0; i < n; i++) if (p[i] == proto) found = 1;
        XFree(p);
    }
    return found;
}

static void send_proto(Window w, Atom proto)
{
    XEvent e = {0};
    e.xclient.type = ClientMessage;
    e.xclient.window = w;
    e.xclient.message_type = A[WM_PROTOCOLS];
    e.xclient.format = 32;
    e.xclient.data.l[0] = (long)proto;
    e.xclient.data.l[1] = CurrentTime;
    XSendEvent(dpy, w, False, NoEventMask, &e);
}

static void read_title(Client *c)
{
    Atom rt; int rf; unsigned long n, after; unsigned char *data = NULL;
    c->title[0] = 0;
    if (XGetWindowProperty(dpy, c->win, A[NET_WM_NAME], 0, 256, False, A[UTF8_STRING], &rt, &rf, &n, &after, &data) == Success && data && n) {
        snprintf(c->title, sizeof c->title, "%s", (char *)data);
        XFree(data);
        return;
    }
    if (data) XFree(data);
    char *name = NULL;
    if (XFetchName(dpy, c->win, &name) && name) { snprintf(c->title, sizeof c->title, "%s", name); XFree(name); }
}

static void read_type(Client *c)
{
    unsigned long n; long *t = get_cardinals(c->win, A[NET_WM_WINDOW_TYPE], XA_ATOM, &n);
    c->type = c->transient_for ? T_DIALOG : T_NORMAL;
    for (unsigned long i = 0; t && i < n; i++) {
        Atom a = (Atom)t[i];
        if (a == A[NET_WM_WINDOW_TYPE_DESKTOP]) { c->type = T_DESKTOP; break; }
        if (a == A[NET_WM_WINDOW_TYPE_DOCK]) { c->type = T_DOCK; break; }
        if (a == A[NET_WM_WINDOW_TYPE_SPLASH]) { c->type = T_SPLASH; break; }
        if (a == A[NET_WM_WINDOW_TYPE_NOTIFICATION] || a == A[NET_WM_WINDOW_TYPE_TOOLTIP]) { c->type = T_NOTIFY; break; }
        if (a == A[NET_WM_WINDOW_TYPE_MENU] || a == A[NET_WM_WINDOW_TYPE_POPUP_MENU] || a == A[NET_WM_WINDOW_TYPE_DROPDOWN_MENU] ||
            a == A[NET_WM_WINDOW_TYPE_COMBO] || a == A[NET_WM_WINDOW_TYPE_DND]) { c->type = T_MENU; break; }
        if (a == A[NET_WM_WINDOW_TYPE_DIALOG]) { c->type = T_DIALOG; break; }
        if (a == A[NET_WM_WINDOW_TYPE_UTILITY] || a == A[NET_WM_WINDOW_TYPE_TOOLBAR]) { c->type = T_UTILITY; break; }
        if (a == A[NET_WM_WINDOW_TYPE_NORMAL]) { c->type = c->transient_for ? T_DIALOG : T_NORMAL; break; }
    }
    if (t) XFree(t);
}

static int motif_no_decor(Window w)
{
    unsigned long n; long *m = get_cardinals(w, A[MOTIF_WM_HINTS], A[MOTIF_WM_HINTS], &n);
    int none = 0;
    if (m && n >= 3 && (m[0] & 2) && !(m[2] & 1) && !(m[2] & 8)) none = 1;   /* decorations given, no ALL, no TITLE */
    if (m) XFree(m);
    return none;
}

static void read_size_hints(Client *c)
{
    XSizeHints h; long supplied;
    c->min_w = c->min_h = 1; c->max_w = c->max_h = 0;
    if (XGetWMNormalHints(dpy, c->win, &h, &supplied)) {
        if (h.flags & PMinSize) { c->min_w = h.min_width; c->min_h = h.min_height; }
        else if (h.flags & PBaseSize) { c->min_w = h.base_width; c->min_h = h.base_height; }
        if (h.flags & PMaxSize) { c->max_w = h.max_width; c->max_h = h.max_height; }
    }
    if (c->min_w < 1) c->min_w = 1;
    if (c->min_h < 1) c->min_h = 1;
}

static int win_gravity(Window w)
{
    XSizeHints h; long supplied;
    if (XGetWMNormalHints(dpy, w, &h, &supplied) && (h.flags & PWinGravity)) return h.win_gravity;
    return NorthWestGravity;
}

static int deco_top(Client *c) { return c->decorated && !c->fullscreen ? TH : 0; }
static int deco_side(Client *c) { return c->decorated && !c->fullscreen && !c->maximized ? BW : 0; }

/* ───────────────────────── properties the shell reads ───────────────────────── */
static int layer(Client *c);
static void update_client_lists(void)
{
    int n = 0;
    for (Client *c = clients; c; c = c->next) n++;
    Window *list = calloc(n ? n : 1, sizeof(Window)), *slist = calloc(n ? n : 1, sizeof(Window));
    int i = 0;
    /* _NET_CLIENT_LIST: initial mapping order (clients are pushed at the head, so walk backwards) */
    for (Client *c = clients; c; c = c->next) list[n - 1 - i++] = c->win;
    /* _NET_CLIENT_LIST_STACKING: bottom → top exactly as drawn (desktop first, then by layer and raise order) */
    i = 0;
    for (int L = 0; L <= 4; L++)
        for (Client *c = stack; c; c = c->snext) if (layer(c) == L && i < n) slist[i++] = c->win;
    XChangeProperty(dpy, root, A[NET_CLIENT_LIST], XA_WINDOW, 32, PropModeReplace, (unsigned char *)list, n);
    XChangeProperty(dpy, root, A[NET_CLIENT_LIST_STACKING], XA_WINDOW, 32, PropModeReplace, (unsigned char *)slist, i);
    free(list); free(slist);
}

static void set_wm_state(Client *c, long state)
{
    long d[2] = { state, None };
    XChangeProperty(dpy, c->win, A[WM_STATE], A[WM_STATE], 32, PropModeReplace, (unsigned char *)d, 2);
}

static void update_net_state(Client *c)
{
    long s[10]; int n = 0;
    if (c->fullscreen) s[n++] = A[NET_WM_STATE_FULLSCREEN];
    if (c->maximized) { s[n++] = A[NET_WM_STATE_MAXIMIZED_VERT]; s[n++] = A[NET_WM_STATE_MAXIMIZED_HORZ]; }
    if (c->minimized) s[n++] = A[NET_WM_STATE_HIDDEN];
    if (c->above) s[n++] = A[NET_WM_STATE_ABOVE];
    if (c->skip_taskbar) { s[n++] = A[NET_WM_STATE_SKIP_TASKBAR]; s[n++] = A[NET_WM_STATE_SKIP_PAGER]; }
    if (c->sticky) s[n++] = A[NET_WM_STATE_STICKY];
    if (c == active) s[n++] = A[NET_WM_STATE_FOCUSED];
    if (c->type == T_DESKTOP && c->desk_full) s[n++] = A[NET_WM_STATE_FULLSCREEN];
    set_cardinals(c->win, A[NET_WM_STATE], XA_ATOM, s, n);
}

/* _REDSTAR_SHADOW on the frame tells picom which windows get a drop shadow: framed windows that aren't
 * maximised or full screen. Menus, tooltips, the desktop, docks and undecorated windows (which may draw their own
 * transparent, rounded edges) get none, so there's never a shadow box around something see-through. */
static void set_shadow_hint(Client *c)
{
    if (c->decorated && !c->fullscreen && !c->maximized) {
        long on = 1;
        set_cardinals(c->frame, A[REDSTAR_SHADOW], XA_CARDINAL, &on, 1);
    } else XDeleteProperty(dpy, c->frame, A[REDSTAR_SHADOW]);
}

static void set_frame_extents(Client *c)
{
    long e[4] = { deco_side(c), deco_side(c), deco_top(c), deco_side(c) };
    set_cardinals(c->win, A[NET_FRAME_EXTENTS], XA_CARDINAL, e, 4);
}

static void update_workarea(void)
{
    int l = 0, r = 0, t = 0, b = 0;
    for (Client *c = clients; c; c = c->next) {
        unsigned long n; long *s = get_cardinals(c->win, A[NET_WM_STRUT_PARTIAL], XA_CARDINAL, &n);
        if (!s || n < 4) { if (s) XFree(s); s = get_cardinals(c->win, A[NET_WM_STRUT], XA_CARDINAL, &n); }
        if (s && n >= 4) {
            if (s[0] > l) l = s[0];
            if (s[1] > r) r = s[1];
            if (s[2] > t) t = s[2];
            if (s[3] > b) b = s[3];
        }
        if (s) XFree(s);
    }
    wa_x = l; wa_y = t; wa_w = SW - l - r; wa_h = SH - t - b;
    if (wa_w < 200) { wa_x = 0; wa_w = SW; }
    if (wa_h < 200) { wa_y = 0; wa_h = SH; }
    long *w = malloc(sizeof(long) * 4 * ndesk);
    for (int i = 0; i < ndesk; i++) { w[i * 4] = wa_x; w[i * 4 + 1] = wa_y; w[i * 4 + 2] = wa_w; w[i * 4 + 3] = wa_h; }
    set_cardinals(root, A[NET_WORKAREA], XA_CARDINAL, w, 4 * ndesk);
    free(w);
}

static void set_root_desktop_props(void)
{
    long v = ndesk; set_cardinals(root, A[NET_NUMBER_OF_DESKTOPS], XA_CARDINAL, &v, 1);
    v = curdesk; set_cardinals(root, A[NET_CURRENT_DESKTOP], XA_CARDINAL, &v, 1);
    long geo[2] = { SW, SH }; set_cardinals(root, A[NET_DESKTOP_GEOMETRY], XA_CARDINAL, geo, 2);
    long *vp = calloc(2 * ndesk, sizeof(long)); set_cardinals(root, A[NET_DESKTOP_VIEWPORT], XA_CARDINAL, vp, 2 * ndesk); free(vp);
    char names[256]; int len = 0;
    for (int i = 0; i < ndesk; i++) len += snprintf(names + len, sizeof names - len, "Desktop") + 1;
    XChangeProperty(dpy, root, A[NET_DESKTOP_NAMES], A[UTF8_STRING], 8, PropModeReplace, (unsigned char *)names, len);
    v = 0; set_cardinals(root, A[NET_SHOWING_DESKTOP], XA_CARDINAL, &v, 1);
}

/* ───────────────────────── drawing ───────────────────────── */
static void button_rects(Client *c, int fw, int *bw, int *x_min, int *x_max, int *x_close)
{
    (void)c;
    *bw = S(46);
    *x_close = fw - *bw;
    *x_max = *x_close - *bw;
    *x_min = *x_max - *bw;
}

static int frame_width(Client *c) { return c->w + 2 * deco_side(c); }
static int frame_height(Client *c) { return c->h + deco_top(c) + deco_side(c); }

static void draw_star(Drawable d, int cx, int cy, int r, unsigned long col)
{
    XPoint p[10];
    for (int i = 0; i < 10; i++) {
        double a = -3.14159265 / 2 + i * 3.14159265 / 5, rad = (i % 2 == 0) ? r : r * 0.382;
        p[i].x = (short)(cx + rad * __builtin_cos(a));
        p[i].y = (short)(cy + rad * __builtin_sin(a));
    }
    XSetForeground(dpy, gc, col);
    XFillPolygon(dpy, d, gc, p, 10, Nonconvex, CoordModeOrigin);
}

static void draw_frame(Client *c)
{
    if (!c->decorated || c->fullscreen) return;
    int fw = frame_width(c), fh = frame_height(c);
    int isact = (c == active);
    unsigned long bg = isact ? col_title_active : col_title_inactive;
    Pixmap pm = XCreatePixmap(dpy, c->frame, fw, TH, DefaultDepth(dpy, scr));
    XSetForeground(dpy, gc, bg);
    XFillRectangle(dpy, pm, gc, 0, 0, fw, TH);
    if (isact) { XSetForeground(dpy, gc, col_accent); XFillRectangle(dpy, pm, gc, 0, 0, fw, S(2)); }

    int bw, xmin, xmax, xclose;
    button_rects(c, fw, &bw, &xmin, &xmax, &xclose);
    /* hover backgrounds */
    if (hover_c == c && hover_btn) {
        int hx = hover_btn == BTN_MIN ? xmin : hover_btn == BTN_MAX ? xmax : xclose;
        XSetForeground(dpy, gc, hover_btn == BTN_CLOSE ? col_close_hover : col_btn_hover);
        XFillRectangle(dpy, pm, gc, hx, isact ? S(2) : 0, bw, TH - (isact ? S(2) : 0));
    }
    unsigned long fg = isact ? col_text : col_text_dim;
    XSetForeground(dpy, gc, fg);
    XSetLineAttributes(dpy, gc, S(1) < 1 ? 1 : S(1), LineSolid, CapRound, JoinMiter);
    int g = S(10), cy = TH / 2;
    /* minimise: a line */
    XDrawLine(dpy, pm, gc, xmin + bw / 2 - g / 2, cy, xmin + bw / 2 + g / 2, cy);
    /* maximise: a square (two when maximised) */
    if (c->maximized) {
        int o = S(2);
        XDrawRectangle(dpy, pm, gc, xmax + bw / 2 - g / 2 + o, cy - g / 2 - o, g - o, g - o);
        XSetForeground(dpy, gc, (hover_c == c && hover_btn == BTN_MAX) ? col_btn_hover : bg);
        XFillRectangle(dpy, pm, gc, xmax + bw / 2 - g / 2, cy - g / 2 + o, g - o, g - o);
        XSetForeground(dpy, gc, fg);
        XDrawRectangle(dpy, pm, gc, xmax + bw / 2 - g / 2, cy - g / 2 + o, g - o, g - o);
    } else
        XDrawRectangle(dpy, pm, gc, xmax + bw / 2 - g / 2, cy - g / 2, g, g);
    /* close: an X */
    if (hover_c == c && hover_btn == BTN_CLOSE) XSetForeground(dpy, gc, col_text);
    XDrawLine(dpy, pm, gc, xclose + bw / 2 - g / 2, cy - g / 2, xclose + bw / 2 + g / 2, cy + g / 2);
    XDrawLine(dpy, pm, gc, xclose + bw / 2 + g / 2, cy - g / 2, xclose + bw / 2 - g / 2, cy + g / 2);

    /* red star + title */
    int sx = S(16);
    draw_star(pm, sx, cy, S(7), col_accent);
    int tx = sx + S(14), maxw = xmin - tx - S(8);
    if (maxw > 10 && c->title[0]) {
        char buf[600]; snprintf(buf, sizeof buf, "%s", c->title);
        int len = (int)strlen(buf);
        if (xftfont && pXftDrawStringUtf8) {
            XGlyphInfoT gi;
            pXftTextExtentsUtf8(dpy, xftfont, (unsigned char *)buf, len, &gi);
            while (gi.xOff > maxw && len > 0) {        /* shorten at a UTF-8 boundary, add "…" */
                len--;
                while (len > 0 && ((unsigned char)buf[len] & 0xC0) == 0x80) len--;
                memcpy(buf + len, "\xE2\x80\xA6", 4);
                pXftTextExtentsUtf8(dpy, xftfont, (unsigned char *)buf, len + 3, &gi);
            }
            int L = (int)strlen(buf);
            XftFontT *f = xftfont;
            if (!xftdraw) xftdraw = pXftDrawCreate(dpy, pm, DefaultVisual(dpy, scr), DefaultColormap(dpy, scr));
            else { void (*chg)(void *, Drawable) = dlsym(RTLD_DEFAULT, "XftDrawChange"); if (chg) chg(xftdraw, pm); }
            pXftDrawStringUtf8(xftdraw, isact ? &xft_white : &xft_dim, xftfont, tx, cy + (f->ascent - f->descent) / 2, (unsigned char *)buf, L);
        } else if (corefont) {
            /* bitmap fonts are Latin-1: turn UTF-8 into it (dashes, quotes and "…" get ASCII look-alikes) */
            char lat[600]; int o = 0;
            for (int i = 0; buf[i] && o < (int)sizeof lat - 4; ) {
                unsigned char b0 = (unsigned char)buf[i];
                unsigned cp; int k;
                if (b0 < 0x80) { cp = b0; k = 1; }
                else if ((b0 & 0xE0) == 0xC0 && buf[i + 1]) { cp = ((b0 & 0x1F) << 6) | (buf[i + 1] & 0x3F); k = 2; }
                else if ((b0 & 0xF0) == 0xE0 && buf[i + 1] && buf[i + 2]) { cp = ((b0 & 0x0F) << 12) | ((buf[i + 1] & 0x3F) << 6) | (buf[i + 2] & 0x3F); k = 3; }
                else { cp = '?'; k = 1; while (buf[i + k] && ((unsigned char)buf[i + k] & 0xC0) == 0x80) k++; }
                i += k;
                if (cp == 0x2013 || cp == 0x2014) lat[o++] = '-';
                else if (cp == 0x2026) { lat[o++] = '.'; lat[o++] = '.'; lat[o++] = '.'; }
                else if (cp == 0x2018 || cp == 0x2019) lat[o++] = '\'';
                else if (cp == 0x201C || cp == 0x201D) lat[o++] = '"';
                else lat[o++] = cp < 256 ? (char)cp : '?';
            }
            lat[o] = 0;
            memcpy(buf, lat, o + 1);
            len = o;
            while (XTextWidth(corefont, buf, len) > maxw && len > 3) len--;
            XSetFont(dpy, gc, corefont->fid);
            XSetForeground(dpy, gc, fg);
            XDrawString(dpy, pm, gc, tx, cy + (corefont->ascent - corefont->descent) / 2, buf, len);
        }
    }
    XCopyArea(dpy, pm, c->frame, gc, 0, 0, fw, TH, 0, 0);
    XFreePixmap(dpy, pm);
    if (xftdraw) { pXftDrawDestroy(xftdraw); xftdraw = NULL; }
    /* side borders */
    int bwid = deco_side(c);
    if (bwid) {
        XSetForeground(dpy, gc, isact ? col_title_active : col_border);
        XFillRectangle(dpy, c->frame, gc, 0, TH, bwid, fh - TH);
        XFillRectangle(dpy, c->frame, gc, fw - bwid, TH, bwid, fh - TH);
        XFillRectangle(dpy, c->frame, gc, 0, fh - bwid, fw, bwid);
    }
}

/* ───────────────────────── geometry ───────────────────────── */
static void send_configure(Client *c)
{
    XConfigureEvent ce = {0};
    ce.type = ConfigureNotify;
    ce.display = dpy;
    ce.event = c->win;
    ce.window = c->win;
    ce.x = c->x; ce.y = c->y; ce.width = c->w; ce.height = c->h;
    ce.border_width = 0; ce.above = None; ce.override_redirect = False;
    XSendEvent(dpy, c->win, False, StructureNotifyMask, (XEvent *)&ce);
}

static void apply_geometry(Client *c)
{
    if (c->w < 1) c->w = 1;
    if (c->h < 1) c->h = 1;
    int side = deco_side(c), top = deco_top(c);
    XMoveResizeWindow(dpy, c->frame, c->x - side, c->y - top, frame_width(c), frame_height(c));
    XMoveResizeWindow(dpy, c->win, side, top, c->w, c->h);
    set_frame_extents(c);
    set_shadow_hint(c);
    send_configure(c);
    draw_frame(c);
}

static void clamp_size(Client *c)
{
    if (c->w < c->min_w) c->w = c->min_w;
    if (c->h < c->min_h) c->h = c->min_h;
    if (c->max_w > 0 && c->w > c->max_w) c->w = c->max_w;
    if (c->max_h > 0 && c->h > c->max_h) c->h = c->max_h;
}

/* ───────────────────────── stacking ───────────────────────── */
static int layer(Client *c)
{
    if (c->type == T_DESKTOP) return 0;
    if (c->type == T_DOCK || c->type == T_NOTIFY || c->type == T_MENU) return 4;
    if (c->fullscreen && c == active) return 3;
    if (c->above || c->type == T_SPLASH) return 2;
    return 1;
}

static void stack_remove(Client *c)
{
    for (Client **p = &stack; *p; p = &(*p)->snext)
        if (*p == c) { *p = c->snext; c->snext = NULL; return; }
}

static void stack_push_top(Client *c)
{
    stack_remove(c);
    Client **p = &stack;
    while (*p) p = &(*p)->snext;
    *p = c;
}

static void restack(void)
{
    int n = 0;
    for (Client *c = stack; c; c = c->snext) n++;
    if (!n) return;
    Window *w = malloc(sizeof(Window) * n);
    int i = 0;
    for (int L = 4; L >= 0; L--) {           /* XRestackWindows wants top → bottom */
        Client *tmp[1024]; int k = 0;
        for (Client *c = stack; c && k < 1024; c = c->snext) if (layer(c) == L) tmp[k++] = c;
        for (int j = k - 1; j >= 0; j--) w[i++] = tmp[j]->frame;
    }
    XRaiseWindow(dpy, w[0]);
    XRestackWindows(dpy, w, n);
    free(w);
    /* menus, tooltips and other popups (override-redirect) must stay above the window just raised */
    Window r, p, *kids; unsigned int nk;
    if (XQueryTree(dpy, root, &r, &p, &kids, &nk)) {
        for (unsigned int i = 0; i < nk; i++) {           /* bottom → top, so their own order is kept */
            XWindowAttributes wa;
            if (kids[i] == check_win || !XGetWindowAttributes(dpy, kids[i], &wa)) continue;
            if (wa.override_redirect && wa.map_state == IsViewable && !find_client(kids[i])) XRaiseWindow(dpy, kids[i]);
        }
        if (kids) XFree(kids);
    }
    update_client_lists();
}

/* ───────────────────────── focus ───────────────────────── */
static void grab_buttons(Client *c, int focused);
static int visible(Client *c)
{
    return !c->minimized && (c->desktop == -1 || c->sticky || c->desktop == curdesk);
}

static void focus(Client *c)
{
    Client *old = active;
    if (c && (c->type == T_DOCK || c->type == T_NOTIFY)) return;
    active = c;
    if (old != c) {
        /* the focused window gets its clicks straight from the X server; the others are grabbed for click-to-focus */
        if (old && find_client(old->win)) grab_buttons(old, 0);
        if (c) grab_buttons(c, 1);
        DBG("focus %s (0x%lx)", c ? c->title : "(none)", c ? c->win : 0UL);
    }
    if (c) {
        if (c->input_hint) XSetInputFocus(dpy, c->win, RevertToPointerRoot, CurrentTime);
        if (c->focus_proto) send_proto(c->win, A[WM_TAKE_FOCUS]);
        XChangeProperty(dpy, root, A[NET_ACTIVE_WINDOW], XA_WINDOW, 32, PropModeReplace, (unsigned char *)&c->win, 1);
        update_net_state(c);
        draw_frame(c);
    } else {
        XSetInputFocus(dpy, PointerRoot, RevertToPointerRoot, CurrentTime);
        Window none = None;
        XChangeProperty(dpy, root, A[NET_ACTIVE_WINDOW], XA_WINDOW, 32, PropModeReplace, (unsigned char *)&none, 1);
    }
    if (old && old != c && find_client(old->win)) { update_net_state(old); draw_frame(old); }
}

static void focus_next(void)
{
    Client *best = NULL, *desk = NULL;
    for (Client *c = stack; c; c = c->snext) {          /* top-most visible normal window */
        if (!visible(c)) continue;
        if (c->type == T_DESKTOP) { desk = c; continue; }
        if (c->type == T_DOCK || c->type == T_NOTIFY || c->type == T_MENU) continue;
        best = c;
    }
    focus(best ? best : desk);
}

static void raise_client(Client *c)
{
    if (c->type == T_DESKTOP) { restack(); return; }    /* the desktop never comes up */
    stack_push_top(c);
    /* keep its transient dialogs above it */
    for (Client *t = clients; t; t = t->next)
        if (t->transient_for == c->win) stack_push_top(t);
    restack();
}

static void activate(Client *c)
{
    if (!c) return;
    if (c->minimized) {
        c->minimized = 0;
        XMapWindow(dpy, c->win);
        set_wm_state(c, NormalState);
    }
    if (!visible(c) && c->desktop >= 0) {
        /* on another workspace: go there */
        XClientMessageEvent m = {0};
        (void)m;
        int target = c->desktop;
        for (Client *o = clients; o; o = o->next) {
            int was = visible(o);
            int now = !o->minimized && (o->desktop == -1 || o->sticky || o->desktop == target);
            if (was && !now) XUnmapWindow(dpy, o->frame);
        }
        curdesk = target;
        long v = curdesk; set_cardinals(root, A[NET_CURRENT_DESKTOP], XA_CARDINAL, &v, 1);
        for (Client *o = clients; o; o = o->next) if (visible(o)) XMapWindow(dpy, o->frame);
    }
    XMapWindow(dpy, c->frame);
    update_net_state(c);
    raise_client(c);
    focus(c);
}

static void minimize(Client *c)
{
    if (!c || c->type == T_DESKTOP || c->type == T_DOCK) return;
    c->minimized = 1;
    XUnmapWindow(dpy, c->frame);
    c->ignore_unmap++;                  /* the client unmap below is ours, not a withdraw */
    XUnmapWindow(dpy, c->win);
    set_wm_state(c, IconicState);
    update_net_state(c);
    if (active == c) focus_next();
}

static void close_client(Client *c)
{
    if (!c) return;
    if (c->del_proto) send_proto(c->win, A[WM_DELETE_WINDOW]);
    else XKillClient(dpy, c->win);
}

static void set_maximized(Client *c, int on)
{
    if (!c || c->fullscreen || c->type == T_DESKTOP || c->type == T_DOCK) return;
    if (on && !c->maximized) {
        if (!c->snapped) { c->sx = c->x; c->sy = c->y; c->sw = c->w; c->sh = c->h; }
        c->snapped = 0;
        c->maximized = 1;
        c->x = wa_x; c->y = wa_y + deco_top(c); c->w = wa_w; c->h = wa_h - deco_top(c);
    } else if (!on && c->maximized) {
        c->maximized = 0;
        c->x = c->sx; c->y = c->sy; c->w = c->sw; c->h = c->sh;
    } else return;
    apply_geometry(c);
    update_net_state(c);
}

static void set_fullscreen(Client *c, int on)
{
    if (!c || c->type == T_DOCK) return;
    if (c->type == T_DESKTOP) {          /* the desktop always fills the screen: just confirm the state */
        c->fullscreen = 0;
        c->desk_full = on;
        update_net_state(c);
        return;
    }
    if (on && !c->fullscreen) {
        if (!c->maximized) { c->sx = c->x; c->sy = c->y; c->sw = c->w; c->sh = c->h; }
        c->fullscreen = 1;
        c->x = 0; c->y = 0; c->w = SW; c->h = SH;
    } else if (!on && c->fullscreen) {
        c->fullscreen = 0;
        if (c->maximized) { c->x = wa_x; c->y = wa_y + deco_top(c); c->w = wa_w; c->h = wa_h - deco_top(c); }
        else { c->x = c->sx; c->y = c->sy; c->w = c->sw; c->h = c->sh; }
    } else return;
    apply_geometry(c);
    update_net_state(c);
    restack();
}

static void place_desktop(Client *c)
{
    c->x = 0; c->y = 0; c->w = SW; c->h = SH;
}

static void refit_all(void)
{
    for (Client *c = clients; c; c = c->next) {
        if (c->type == T_DESKTOP) place_desktop(c);
        else if (c->fullscreen) { c->x = 0; c->y = 0; c->w = SW; c->h = SH; }
        else if (c->maximized) { c->x = wa_x; c->y = wa_y + deco_top(c); c->w = wa_w; c->h = wa_h - deco_top(c); }
        else continue;
        apply_geometry(c);
    }
}

/* ───────────────────────── workspaces ───────────────────────── */
static void move_to_desktop(Client *c, long d)
{
    if (!c) return;
    if (d == (long)0xFFFFFFFF || d < 0) { c->desktop = -1; c->sticky = 1; }
    else { c->desktop = (int)(d % ndesk); c->sticky = 0; }
    long v = c->desktop < 0 ? (long)0xFFFFFFFF : c->desktop;
    set_cardinals(c->win, A[NET_WM_DESKTOP], XA_CARDINAL, &v, 1);
    if (visible(c)) XMapWindow(dpy, c->frame); else { XUnmapWindow(dpy, c->frame); if (active == c) focus_next(); }
    update_net_state(c);
}

/* ───────────────────────── manage / unmanage ───────────────────────── */
/*
 * Click-to-focus: on windows that aren't focused, buttons 1-3 are grabbed synchronously — the WM raises and focuses
 * the window, then replays the click to it, so the first click both focuses and works. The focused window has no
 * such grab (no round trip through the WM at all), and the scroll wheel is never grabbed.
 * Alt+left / Alt+right are always grabbed (move / resize), with NumLock and CapsLock in any state.
 */
static void grab_buttons(Client *c, int focused)
{
    XUngrabButton(dpy, AnyButton, AnyModifier, c->win);
    if (!focused)
        for (unsigned b = Button1; b <= Button3; b++)
            XGrabButton(dpy, b, AnyModifier, c->win, False, ButtonPressMask, GrabModeSync, GrabModeAsync, None, None);
    if (c->type == T_DESKTOP) return;
    unsigned int locks[4] = { 0, Mod2Mask, LockMask, Mod2Mask | LockMask };
    for (int i = 0; i < 4; i++) {
        XGrabButton(dpy, Button1, Mod1Mask | locks[i], c->win, False, ButtonPressMask, GrabModeSync, GrabModeAsync, None, None);
        XGrabButton(dpy, Button3, Mod1Mask | locks[i], c->win, False, ButtonPressMask, GrabModeSync, GrabModeAsync, None, None);
    }
}

static void manage(Window w, XWindowAttributes *wa, int already_mapped)
{
    if (find_client(w)) return;
    Client *c = calloc(1, sizeof(Client));
    c->win = w;
    c->x = wa->x; c->y = wa->y; c->w = wa->width; c->h = wa->height;
    Window tf = None;
    if (XGetTransientForHint(dpy, w, &tf) && tf != None && tf != root) c->transient_for = tf;
    read_type(c);
    read_title(c);
    read_size_hints(c);
    c->del_proto = has_proto(w, A[WM_DELETE_WINDOW]);
    c->focus_proto = has_proto(w, A[WM_TAKE_FOCUS]);
    c->input_hint = 1;
    XWMHints *hints = XGetWMHints(dpy, w);
    if (hints) { if (hints->flags & InputHint) c->input_hint = hints->input; XFree(hints); }
    c->decorated = (c->type == T_NORMAL || c->type == T_DIALOG || c->type == T_UTILITY) && !motif_no_decor(w);
    c->desktop = curdesk;
    if (c->type == T_DESKTOP || c->type == T_DOCK || c->type == T_NOTIFY) { c->desktop = -1; c->sticky = 1; c->skip_taskbar = 1; }

    unsigned long n; long *d = get_cardinals(w, A[NET_WM_DESKTOP], XA_CARDINAL, &n);
    if (d && n && d[0] == (long)0xFFFFFFFF) { c->desktop = -1; c->sticky = 1; }
    if (d) XFree(d);
    /* states requested before mapping (Avalonia sets fullscreen / maximised this way) */
    int want_full = 0, want_max = 0;
    long *st = get_cardinals(w, A[NET_WM_STATE], XA_ATOM, &n);
    for (unsigned long i = 0; st && i < n; i++) {
        Atom a = (Atom)st[i];
        if (a == A[NET_WM_STATE_FULLSCREEN]) want_full = 1;
        else if (a == A[NET_WM_STATE_MAXIMIZED_VERT] || a == A[NET_WM_STATE_MAXIMIZED_HORZ]) want_max = 1;
        else if (a == A[NET_WM_STATE_ABOVE]) c->above = 1;
        else if (a == A[NET_WM_STATE_SKIP_TASKBAR]) c->skip_taskbar = 1;
        else if (a == A[NET_WM_STATE_STICKY]) { c->sticky = 1; c->desktop = -1; }
    }
    if (st) XFree(st);

    /* placement */
    if (c->type == T_DESKTOP) place_desktop(c);
    else {
        XSizeHints sh; long sup; int user_pos = 0;
        if (XGetWMNormalHints(dpy, w, &sh, &sup) && (sh.flags & (USPosition | PPosition))) user_pos = 1;
        int top = c->decorated ? TH : 0, side = c->decorated ? BW : 0;
        Client *parent = c->transient_for ? find_client(c->transient_for) : NULL;
        if (parent && !(c->x || c->y)) {
            c->x = parent->x + (parent->w - c->w) / 2;
            c->y = parent->y + (parent->h - c->h) / 2;
        } else if (!user_pos && c->x == 0 && c->y == 0) {
            c->x = wa_x + (wa_w - c->w) / 2;
            c->y = wa_y + (wa_h - c->h - top) / 2 + top;
        } else if (win_gravity(w) != StaticGravity && c->decorated) {
            c->x += side;                     /* the requested position is the frame's top-left */
            c->y += top;
        }
        /* cascade: a window that would land exactly on another one is moved down-right a step */
        if (!parent && c->type == T_NORMAL)
            for (int tries = 0; tries < 12; tries++) {
                int clash = 0;
                for (Client *o = clients; o; o = o->next)
                    if (o->type == T_NORMAL && visible(o) && abs(o->x - c->x) < 4 && abs(o->y - c->y) < 4) { clash = 1; break; }
                if (!clash) break;
                c->x += TH; c->y += TH;
                if (c->x + c->w > wa_x + wa_w || c->y + c->h > wa_y + wa_h) { c->x = wa_x + TH + side; c->y = wa_y + 2 * TH; }
            }
        /* keep the title bar reachable */
        if (c->y - top < wa_y) c->y = wa_y + top;
        if (c->x + c->w < S(60)) c->x = S(60) - c->w;
        if (c->x > SW - S(60)) c->x = SW - S(60);
        if (c->y > wa_y + wa_h - S(40)) c->y = wa_y + wa_h - S(40);
    }

    XSetWindowAttributes fa = {0};
    fa.override_redirect = True;
    fa.background_pixel = col_title_inactive;
    fa.event_mask = SubstructureRedirectMask | SubstructureNotifyMask | ButtonPressMask | ButtonReleaseMask |
                    PointerMotionMask | LeaveWindowMask | ExposureMask;
    fa.cursor = cur_normal;
    c->frame = XCreateWindow(dpy, root, 0, 0, 1, 1, 0, DefaultDepth(dpy, scr), InputOutput, DefaultVisual(dpy, scr),
                             CWOverrideRedirect | CWBackPixel | CWEventMask | CWCursor, &fa);
    XSelectInput(dpy, w, PropertyChangeMask | FocusChangeMask);   /* unmap/destroy arrive once, through the frame */
    XSetWindowBorderWidth(dpy, w, 0);
    XAddToSaveSet(dpy, w);
    if (already_mapped) c->ignore_unmap++;        /* reparenting a mapped window unmaps it once */
    XReparentWindow(dpy, w, c->frame, 0, 0);
    grab_buttons(c, 0);

    c->next = clients; clients = c;
    stack_push_top(c);

    long dv = c->desktop < 0 ? (long)0xFFFFFFFF : c->desktop;
    set_cardinals(w, A[NET_WM_DESKTOP], XA_CARDINAL, &dv, 1);
    long acts[] = { A[NET_WM_ACTION_MOVE], A[NET_WM_ACTION_RESIZE], A[NET_WM_ACTION_MINIMIZE], A[NET_WM_ACTION_MAXIMIZE_HORZ],
                    A[NET_WM_ACTION_MAXIMIZE_VERT], A[NET_WM_ACTION_FULLSCREEN], A[NET_WM_ACTION_CLOSE], A[NET_WM_ACTION_CHANGE_DESKTOP] };
    set_cardinals(w, A[NET_WM_ALLOWED_ACTIONS], XA_ATOM, acts, 8);

    clamp_size(c);
    apply_geometry(c);
    if (want_max) set_maximized(c, 1);
    if (want_full) set_fullscreen(c, 1);      /* (for the desktop: only reported) */
    update_workarea();
    if (c->type == T_DESKTOP) refit_all();

    XMapWindow(dpy, w);
    set_wm_state(c, NormalState);
    if (visible(c)) XMapWindow(dpy, c->frame);
    update_net_state(c);
    restack();
    /* Windows can ask not to be focused when they appear (ICCCM input hint false without WM_TAKE_FOCUS, or
       _NET_WM_USER_TIME = 0 — Avalonia's ShowActivated=false, e.g. Red Star's Alt+Tab panel): they stay passive. */
    int no_focus = (!c->input_hint && !c->focus_proto);
    unsigned long un; long *ut = get_cardinals(w, A[NET_WM_USER_TIME], XA_CARDINAL, &un);
    if (ut && un && ut[0] == 0) no_focus = 1;
    if (ut) XFree(ut);
    if (visible(c) && !no_focus && c->type != T_DOCK && c->type != T_NOTIFY && (c->type != T_DESKTOP || !active)) focus(c);
    DBG("managed 0x%lx \"%s\" type %d at %d,%d %dx%d%s", w, c->title, c->type, c->x, c->y, c->w, c->h, no_focus ? " (not focused: asked not to be)" : "");
}

static void unmanage(Client *c, int destroyed)
{
    if (drag_c == c) { drag_mode = DRAG_NONE; drag_c = NULL; XUngrabPointer(dpy, CurrentTime); snap_preview_hide(); }
    if (hover_c == c) hover_c = NULL;
    if (last_click_c == c) last_click_c = NULL;
    if (!destroyed) {
        XGrabServer(dpy);
        XUngrabButton(dpy, AnyButton, AnyModifier, c->win);
        XReparentWindow(dpy, c->win, root, c->x, c->y);
        XRemoveFromSaveSet(dpy, c->win);
        set_wm_state(c, WithdrawnState);
        XDeleteProperty(dpy, c->win, A[NET_WM_STATE]);
        XUngrabServer(dpy);
    }
    XDestroyWindow(dpy, c->frame);
    for (Client **p = &clients; *p; p = &(*p)->next) if (*p == c) { *p = c->next; break; }
    stack_remove(c);
    int was_active = (active == c);
    if (was_active) active = NULL;
    int had_strut = 1;
    free(c);
    if (had_strut) { update_workarea(); refit_all(); }
    update_client_lists();
    if (was_active) focus_next();
}

/* ───────────────────────── interaction ───────────────────────── */
static int which_button(Client *c, int x, int y)
{
    if (!c->decorated || c->fullscreen || y < 0 || y >= TH) return BTN_NONE;
    int bw, xmin, xmax, xclose;
    button_rects(c, frame_width(c), &bw, &xmin, &xmax, &xclose);
    if (x >= xclose) return BTN_CLOSE;
    if (x >= xmax) return BTN_MAX;
    if (x >= xmin) return BTN_MIN;
    return BTN_NONE;
}

/* edge bits: 1 left, 2 right, 4 top, 8 bottom; mapped to the EWMH direction order */
static int edge_at(Client *c, int x, int y)
{
    if (c->maximized || c->fullscreen || !c->decorated) return 0;
    int fw = frame_width(c), fh = frame_height(c), grip = S(10), e = 0;
    if (x < BW + S(4)) e |= 1; else if (x >= fw - BW - S(4)) e |= 2;
    if (y < S(4)) e |= 4; else if (y >= fh - BW - S(4)) e |= 8;
    /* corners are easier to hit */
    if ((e & 8) && x < grip) e |= 1;
    if ((e & 8) && x >= fw - grip) e |= 2;
    return e;
}

static int edge_cursor_index(int e)
{
    switch (e) {
        case 5: return 0; case 4: return 1; case 6: return 2; case 2: return 3;
        case 10: return 4; case 8: return 5; case 9: return 6; case 1: return 7;
    }
    return -1;
}

static int dir_to_edge(int dir)
{
    static const int map[8] = { 5, 4, 6, 2, 10, 8, 9, 1 };  /* _NET_WM_MOVERESIZE_SIZE_TOPLEFT … LEFT */
    return dir >= 0 && dir < 8 ? map[dir] : 0;
}

/* ───────────────────────── snapping ─────────────────────────
 * A window dragged to an edge of the work area snaps as on Windows: the top maximises, the sides take a half, near a
 * corner a quarter. While it's dragged there, a preview in the shell's accent (_REDSTAR_ACCENT on the root, set by the
 * shell) shows where it will go: a wash of the accent where a compositor can show it see-through, and its edge. The
 * shell hears of a snap through _REDSTAR_SNAPPED on the root (window, zone), to offer the other windows (Snap Assist). */
enum { ZONE_NONE, ZONE_LEFT, ZONE_RIGHT, ZONE_TOP, ZONE_TL, ZONE_TR, ZONE_BL, ZONE_BR };
static int snap_zone;
static Window snap_fill, snap_edge[4];

static void zone_box(int zone, int *x, int *y, int *w, int *h)
{
    int hw = wa_w / 2, hh = wa_h / 2;
    *x = wa_x; *y = wa_y; *w = wa_w; *h = wa_h;
    switch (zone) {
    case ZONE_LEFT: *w = hw; break;
    case ZONE_RIGHT: *x = wa_x + hw; *w = wa_w - hw; break;
    case ZONE_TL: *w = hw; *h = hh; break;
    case ZONE_TR: *x = wa_x + hw; *w = wa_w - hw; *h = hh; break;
    case ZONE_BL: *y = wa_y + hh; *w = hw; *h = wa_h - hh; break;
    case ZONE_BR: *x = wa_x + hw; *y = wa_y + hh; *w = wa_w - hw; *h = wa_h - hh; break;
    default: break;
    }
}

static int zone_at(int px, int py)
{
    int corner = S(64);
    int left = px <= wa_x + 1, right = px >= wa_x + wa_w - 2, top = py <= wa_y + 1;
    int high = py < wa_y + corner, low = py > wa_y + wa_h - corner;
    if (left) return high ? ZONE_TL : low ? ZONE_BL : ZONE_LEFT;
    if (right) return high ? ZONE_TR : low ? ZONE_BR : ZONE_RIGHT;
    if (top) return px < wa_x + corner ? ZONE_TL : px > wa_x + wa_w - corner ? ZONE_TR : ZONE_TOP;
    return ZONE_NONE;
}

static unsigned long mix_white(unsigned long pixel, double t)
{
    XColor c; c.pixel = pixel;
    XQueryColor(dpy, DefaultColormap(dpy, scr), &c);
    c.red += (unsigned short)((65535 - c.red) * t); c.green += (unsigned short)((65535 - c.green) * t); c.blue += (unsigned short)((65535 - c.blue) * t);
    return XAllocColor(dpy, DefaultColormap(dpy, scr), &c) ? c.pixel : pixel;
}

static Window preview_window(unsigned long colour)
{
    XSetWindowAttributes a;
    a.override_redirect = True; a.background_pixel = colour; a.save_under = True;
    return XCreateWindow(dpy, root, 0, 0, 1, 1, 0, CopyFromParent, InputOutput, CopyFromParent,
                         CWOverrideRedirect | CWBackPixel | CWSaveUnder, &a);
}

static void snap_preview_hide(void)
{
    if (snap_fill) { XDestroyWindow(dpy, snap_fill); snap_fill = 0; }
    for (int i = 0; i < 4; i++) if (snap_edge[i]) { XDestroyWindow(dpy, snap_edge[i]); snap_edge[i] = 0; }
    snap_zone = ZONE_NONE;
}

static void snap_preview_show(Client *c, int zone)
{
    if (zone == snap_zone) return;
    snap_preview_hide();
    if (zone == ZONE_NONE) return;
    snap_zone = zone;
    int x, y, w, h, in = S(6), lw = S(2);
    zone_box(zone, &x, &y, &w, &h);
    x += in; y += in; w -= 2 * in; h -= 2 * in;
    if (w <= 2 * lw || h <= 2 * lw) return;
    /* the wash only where a compositor draws it see-through; without one a solid block would hide the screen */
    if (XGetSelectionOwner(dpy, A[NET_WM_CM_S0]) != None) {
        snap_fill = preview_window(col_accent);
        unsigned long opacity = (unsigned long)(0.26 * 0xFFFFFFFFUL);
        XChangeProperty(dpy, snap_fill, A[NET_WM_WINDOW_OPACITY], XA_CARDINAL, 32, PropModeReplace, (unsigned char *)&opacity, 1);
        XMoveResizeWindow(dpy, snap_fill, x, y, w, h);
    }
    unsigned long edge = mix_white(col_accent, 0.25);
    int r[4][4] = { { x, y, w, lw }, { x, y + h - lw, w, lw }, { x, y, lw, h }, { x + w - lw, y, lw, h } };
    for (int i = 0; i < 4; i++) {
        snap_edge[i] = preview_window(edge);
        XMoveResizeWindow(dpy, snap_edge[i], r[i][0], r[i][1], r[i][2], r[i][3]);
    }
    /* under the dragged window, over the rest */
    Window order[6]; int n = 0;
    order[n++] = c->frame;
    for (int i = 0; i < 4; i++) order[n++] = snap_edge[i];
    if (snap_fill) order[n++] = snap_fill;
    for (int i = 1; i < n; i++) XMapWindow(dpy, order[i]);
    XRaiseWindow(dpy, c->frame);
    XRestackWindows(dpy, order, n);
}

static void set_maximized(Client *c, int on);

static void set_snapped(Client *c, int zone)
{
    if (!c || c->fullscreen || c->type == T_DESKTOP || c->type == T_DOCK || zone == ZONE_NONE) return;
    if (zone == ZONE_TOP) { set_maximized(c, 1); return; }
    if (c->maximized) set_maximized(c, 0);
    if (!c->snapped) { c->sx = c->x; c->sy = c->y; c->sw = c->w; c->sh = c->h; }
    c->snapped = zone;
    int x, y, w, h, side = deco_side(c), top = deco_top(c);
    zone_box(zone, &x, &y, &w, &h);
    c->x = x + side; c->y = y + top; c->w = w - 2 * side; c->h = h - top - side;
    apply_geometry(c);
    long v[2] = { (long)c->win, zone };
    set_cardinals(root, A[REDSTAR_SNAPPED], XA_CARDINAL, v, 2);
}

/* the shell's accent: title bars and snap previews */
static void read_accent(void)
{
    unsigned long n;
    long *v = get_cardinals(root, A[REDSTAR_ACCENT], XA_CARDINAL, &n);
    if (!v) return;
    char hex[8];
    snprintf(hex, sizeof hex, "#%06lx", (unsigned long)v[0] & 0xFFFFFF);
    XFree(v);
    col_accent = rgb(hex);
    for (Client *c = clients; c; c = c->next) if (c->decorated) draw_frame(c);
}

static void start_drag(Client *c, int mode, int edge, int px, int py)
{
    if (!c || c->fullscreen || c->type == T_DESKTOP || c->type == T_DOCK) return;
    Cursor cur = cur_move;
    if (mode == DRAG_RESIZE) { int i = edge_cursor_index(edge); if (i < 0) return; cur = cur_edge[i]; }
    if (XGrabPointer(dpy, root, False, ButtonReleaseMask | PointerMotionMask, GrabModeAsync, GrabModeAsync, None, cur, CurrentTime) != GrabSuccess)
        return;
    drag_mode = mode; drag_edge = edge; drag_c = c; drag_px = px; drag_py = py;
    drag_fx = c->x; drag_fy = c->y; drag_fw = c->w; drag_fh = c->h;
}

static void drag_motion(int px, int py)
{
    Client *c = drag_c;
    if (!c) return;
    int dx = px - drag_px, dy = py - drag_py;
    if (drag_mode == DRAG_MOVE) {
        if (c->maximized || c->snapped) {
            if (abs(dx) < S(8) && abs(dy) < S(8)) return;
            /* dragging a maximised or snapped window restores it under the pointer */
            double rel = (double)(px - c->x) / (c->w ? c->w : 1);
            if (c->maximized) set_maximized(c, 0);
            else { c->snapped = 0; c->w = c->sw; c->h = c->sh; apply_geometry(c); }
            drag_fx = px - (int)(rel * c->w); drag_fy = py + TH / 2;
            drag_px = px; drag_py = py; dx = dy = 0;
        }
        c->x = drag_fx + dx; c->y = drag_fy + dy;
        if (c->y - deco_top(c) < wa_y) c->y = wa_y + deco_top(c);
        XMoveWindow(dpy, c->frame, c->x - deco_side(c), c->y - deco_top(c));
        snap_preview_show(c, zone_at(px, py));
    } else if (drag_mode == DRAG_RESIZE) {
        int x = drag_fx, y = drag_fy, w = drag_fw, h = drag_fh;
        if (drag_edge & 1) { w = drag_fw - dx; x = drag_fx + dx; }
        if (drag_edge & 2) w = drag_fw + dx;
        if (drag_edge & 4) { h = drag_fh - dy; y = drag_fy + dy; }
        if (drag_edge & 8) h = drag_fh + dy;
        int minw = c->min_w > S(160) ? c->min_w : S(160), minh = c->min_h > S(60) ? c->min_h : S(60);
        if (w < minw) { if (drag_edge & 1) x -= minw - w; w = minw; }
        if (h < minh) { if (drag_edge & 4) y -= minh - h; h = minh; }
        if (c->max_w > 0 && w > c->max_w) w = c->max_w;
        if (c->max_h > 0 && h > c->max_h) h = c->max_h;
        c->x = x; c->y = y; c->w = w; c->h = h;
        XMoveResizeWindow(dpy, c->frame, c->x - deco_side(c), c->y - deco_top(c), frame_width(c), frame_height(c));
        XResizeWindow(dpy, c->win, c->w, c->h);
        draw_frame(c);
    }
}

static void end_drag(void)
{
    XUngrabPointer(dpy, CurrentTime);
    int zone = drag_mode == DRAG_MOVE ? snap_zone : ZONE_NONE;
    snap_preview_hide();
    if (drag_c && zone != ZONE_NONE) set_snapped(drag_c, zone);
    else if (drag_c) { send_configure(drag_c); draw_frame(drag_c); }
    drag_mode = DRAG_NONE; drag_c = NULL;
}

static void set_hover(Client *c, int btn)
{
    if (hover_c == c && hover_btn == btn) return;
    Client *old = hover_c;
    hover_c = c; hover_btn = btn;
    if (old && old != c) draw_frame(old);
    if (c) draw_frame(c);
}

/* ───────────────────────── event handlers ───────────────────────── */
static void on_map_request(XMapRequestEvent *e)
{
    if (debug) { XWindowAttributes wa; if (XGetWindowAttributes(dpy, e->window, &wa)) DBG("map request 0x%lx at %d,%d %dx%d", e->window, wa.x, wa.y, wa.width, wa.height); }
    Client *c = find_client(e->window);
    if (c) { activate(c); return; }
    XWindowAttributes wa;
    if (!XGetWindowAttributes(dpy, e->window, &wa) || wa.override_redirect) return;
    manage(e->window, &wa, 0);
}

static void on_configure_request(XConfigureRequestEvent *e)
{
    Client *c = find_client(e->window);
    DBG("configure request 0x%lx (%s) mask %lu → %d,%d %dx%d", e->window, c ? c->title : "not managed", e->value_mask, e->x, e->y, e->width, e->height);
    if (!c || c->win != e->window) {
        XWindowChanges wc = { e->x, e->y, e->width, e->height, e->border_width, e->above, e->detail };
        XConfigureWindow(dpy, e->window, e->value_mask, &wc);
        return;
    }
    if (c->type == T_DESKTOP || c->fullscreen || c->maximized) { send_configure(c); return; }
    int grav = win_gravity(c->win);
    if (e->value_mask & CWX) c->x = e->x + (grav == StaticGravity ? 0 : deco_side(c));
    if (e->value_mask & CWY) c->y = e->y + (grav == StaticGravity ? 0 : deco_top(c));
    if (e->value_mask & CWWidth) c->w = e->width;
    if (e->value_mask & CWHeight) c->h = e->height;
    clamp_size(c);
    apply_geometry(c);
    if (e->value_mask & CWStackMode) {
        if (e->detail == Above) raise_client(c);
        else if (e->detail == Below) { stack_remove(c); c->snext = stack; stack = c; restack(); }
    }
}

static void on_unmap(XUnmapEvent *e)
{
    Client *c = find_client(e->window);
    if (!c || c->win != e->window) return;
    if (e->event != c->frame && e->event != root) return;        /* one notice per unmap */
    if (c->ignore_unmap > 0) { c->ignore_unmap--; return; }
    unmanage(c, 0);
}

static void on_destroy(XDestroyWindowEvent *e)
{
    Client *c = find_client(e->window);
    if (c && c->win == e->window) unmanage(c, 1);
}

static void on_property(XPropertyEvent *e)
{
    if (e->window == root && e->atom == A[REDSTAR_ACCENT]) { read_accent(); return; }
    Client *c = find_client(e->window);
    if (!c) return;
    if (e->atom == A[NET_WM_NAME] || e->atom == XA_WM_NAME) { read_title(c); draw_frame(c); }
    else if (e->atom == A[NET_WM_STRUT] || e->atom == A[NET_WM_STRUT_PARTIAL]) { update_workarea(); refit_all(); }
    else if (e->atom == XA_WM_NORMAL_HINTS) read_size_hints(c);
    else if (e->atom == A[WM_PROTOCOLS]) { c->del_proto = has_proto(c->win, A[WM_DELETE_WINDOW]); c->focus_proto = has_proto(c->win, A[WM_TAKE_FOCUS]); }
    else if (e->atom == A[NET_WM_WINDOW_TYPE]) {
        int old = c->type;
        read_type(c);
        if (old != c->type) {
            c->decorated = (c->type == T_NORMAL || c->type == T_DIALOG || c->type == T_UTILITY) && !motif_no_decor(c->win);
            if (c->type == T_DESKTOP || c->type == T_DOCK || c->type == T_NOTIFY) { c->desktop = -1; c->sticky = 1; c->skip_taskbar = 1; }
            if (c->type == T_DESKTOP) { c->fullscreen = 0; c->maximized = 0; place_desktop(c); }
            apply_geometry(c);
            update_net_state(c);
            restack();
            if (c->type == T_DESKTOP && active == c) focus_next();
        }
    } else if (e->atom == A[NET_WM_WINDOW_OPACITY] && e->window == c->win) {
        /* a window's own opacity (the shell's Peek) goes on its frame, which is what the compositor draws */
        unsigned long n;
        long *v = get_cardinals(c->win, A[NET_WM_WINDOW_OPACITY], XA_CARDINAL, &n);
        if (v && n) { unsigned long o = (unsigned long)v[0] & 0xFFFFFFFFUL; XChangeProperty(dpy, c->frame, A[NET_WM_WINDOW_OPACITY], XA_CARDINAL, 32, PropModeReplace, (unsigned char *)&o, 1); }
        else XDeleteProperty(dpy, c->frame, A[NET_WM_WINDOW_OPACITY]);
        if (v) XFree(v);
    } else if (e->atom == A[MOTIF_WM_HINTS]) {
        int dec = (c->type == T_NORMAL || c->type == T_DIALOG || c->type == T_UTILITY) && !motif_no_decor(c->win);
        if (dec != c->decorated) { c->decorated = dec; apply_geometry(c); }
    }
}

static void state_change(Client *c, long action, Atom prop)
{
    if (!prop) return;
    int on;
    #define DECIDE(cur) (on = action == 1 ? 1 : action == 0 ? 0 : !(cur))
    if (prop == A[NET_WM_STATE_FULLSCREEN]) { DECIDE(c->fullscreen); set_fullscreen(c, on); }
    else if (prop == A[NET_WM_STATE_MAXIMIZED_VERT] || prop == A[NET_WM_STATE_MAXIMIZED_HORZ]) { DECIDE(c->maximized); set_maximized(c, on); }
    else if (prop == A[NET_WM_STATE_HIDDEN]) { DECIDE(c->minimized); if (on) minimize(c); else activate(c); }
    else if (prop == A[NET_WM_STATE_ABOVE]) { DECIDE(c->above); c->above = on; update_net_state(c); restack(); }
    else if (prop == A[NET_WM_STATE_SKIP_TASKBAR] || prop == A[NET_WM_STATE_SKIP_PAGER]) { DECIDE(c->skip_taskbar); c->skip_taskbar = on; update_net_state(c); }
    else if (prop == A[NET_WM_STATE_STICKY]) { DECIDE(c->sticky); move_to_desktop(c, on ? (long)0xFFFFFFFF : curdesk); }
    #undef DECIDE
}

static void on_client_message(XClientMessageEvent *e)
{
    Client *c = find_client(e->window);
    long *l = e->data.l;
    if (debug) {
        char *n = XGetAtomName(dpy, e->message_type), *a1 = l[1] > 0 && l[1] < 0xFFFFFFF ? XGetAtomName(dpy, (Atom)l[1]) : NULL;
        DBG("message %s to 0x%lx (%s) [%ld %ld(%s) %ld %ld]", n ? n : "?", e->window, c ? c->title : "not managed", l[0], l[1], a1 ? a1 : "", l[2], l[3]);
        if (n) XFree(n);
        if (a1) XFree(a1);
    }
    if (e->message_type == A[NET_CURRENT_DESKTOP]) return;           /* one workspace: nothing to switch */
    if (e->message_type == A[NET_NUMBER_OF_DESKTOPS]) return;        /* the count stays at one */
    if (e->message_type == A[NET_REQUEST_FRAME_EXTENTS]) {
        long ext[4] = { BW, BW, TH, BW };
        set_cardinals(e->window, A[NET_FRAME_EXTENTS], XA_CARDINAL, ext, 4);
        return;
    }
    if (!c || c->win != e->window) return;
    if (e->message_type == A[NET_ACTIVE_WINDOW]) activate(c);
    else if (e->message_type == A[NET_CLOSE_WINDOW]) close_client(c);
    else if (e->message_type == A[WM_CHANGE_STATE] && l[0] == IconicState) minimize(c);
    else if (e->message_type == A[NET_WM_STATE]) { state_change(c, l[0], (Atom)l[1]); state_change(c, l[0], (Atom)l[2]); }
    else if (e->message_type == A[NET_WM_DESKTOP]) { /* one workspace: every window stays on it */ }
    else if (e->message_type == A[NET_WM_MOVERESIZE]) {
        int dir = (int)l[2];
        if (dir == 11) { if (drag_mode) end_drag(); return; }     /* cancel */
        Window r, ch; int rx, ry, wx, wy; unsigned int mask;
        XQueryPointer(dpy, root, &r, &ch, &rx, &ry, &wx, &wy, &mask);
        if (!(mask & (Button1Mask | Button2Mask | Button3Mask))) return;   /* button already released */
        raise_client(c); focus(c);
        /* Start from where the button went down (l[0], l[1]), not where the pointer is now: apps only ask once the
           pointer has moved past their drag threshold, and starting late left the window trailing the pointer. */
        int ox = (int)l[0], oy = (int)l[1];
        if (ox < -SW || ox > 2 * SW || oy < -SH || oy > 2 * SH) { ox = rx; oy = ry; }
        if (dir == 8) {
            start_drag(c, DRAG_MOVE, 0, ox, oy);
            if (drag_mode == DRAG_MOVE) drag_motion(rx, ry);           /* catch up with the pointer right away */
        } else if (dir >= 0 && dir < 8) {
            if (c->maximized) return;
            start_drag(c, DRAG_RESIZE, dir_to_edge(dir), ox, oy);
            if (drag_mode == DRAG_RESIZE) drag_motion(rx, ry);
        }
    } else if (e->message_type == A[NET_MOVERESIZE_WINDOW]) {
        long flags = l[0];
        int grav = (int)(flags & 0xFF);
        if (c->fullscreen) set_fullscreen(c, 0);
        if (c->maximized) { c->maximized = 0; update_net_state(c); }
        if (flags & (1 << 8)) c->x = (int)l[1] + (grav == StaticGravity ? 0 : deco_side(c));
        if (flags & (1 << 9)) c->y = (int)l[2] + (grav == StaticGravity ? 0 : deco_top(c));
        if (flags & (1 << 10)) c->w = (int)l[3];
        if (flags & (1 << 11)) c->h = (int)l[4];
        /* snapping asks for a frame size that fits the work area: keep the frame inside it */
        if ((flags & (1 << 11)) && c->y - deco_top(c) + frame_height(c) > wa_y + wa_h)
            c->h = wa_y + wa_h - (c->y - deco_top(c)) - deco_top(c) - deco_side(c);
        if ((flags & (1 << 10)) && c->x - deco_side(c) + frame_width(c) > wa_x + wa_w)
            c->w = wa_x + wa_w - (c->x - deco_side(c)) - 2 * deco_side(c);
        clamp_size(c);
        apply_geometry(c);
    }
}

static void on_button_press(XButtonEvent *e)
{
    Client *c = find_client(e->window);
    if (!c) return;
    DBG("press %u at %d,%d on %s%s", e->button, e->x_root, e->y_root, c->title, e->window == c->win ? "" : " (frame)");
    if (e->window == c->win) {
        /* a click inside an app: raise + focus it, then let the app have the click */
        if ((e->state & Mod1Mask) && (e->button == Button1 || e->button == Button3) && c->type != T_DESKTOP) {
            XAllowEvents(dpy, AsyncPointer, e->time);
            raise_client(c); focus(c);
            if (e->button == Button1) start_drag(c, DRAG_MOVE, 0, e->x_root, e->y_root);
            else if (!c->maximized) start_drag(c, DRAG_RESIZE, 10, e->x_root, e->y_root);
            return;
        }
        if (c->type != T_DESKTOP) raise_client(c);
        if (active != c) focus(c);
        XAllowEvents(dpy, ReplayPointer, e->time);
        return;
    }
    /* frame */
    raise_client(c);
    focus(c);
    if (e->button != Button1) return;
    int btn = which_button(c, e->x, e->y);
    if (btn) { drag_mode = DRAG_BUTTON; drag_btn = btn; drag_c = c; set_hover(c, btn); return; }
    int edge = edge_at(c, e->x, e->y);
    if (edge && e->y >= S(4)) { start_drag(c, DRAG_RESIZE, edge, e->x_root, e->y_root); return; }
    if (edge & 4) { start_drag(c, DRAG_RESIZE, edge, e->x_root, e->y_root); return; }
    if (e->y < TH) {
        if (last_click_c == c && e->time - last_click < 400) { last_click_c = NULL; set_maximized(c, !c->maximized); return; }
        last_click_c = c; last_click = e->time;
        start_drag(c, DRAG_MOVE, 0, e->x_root, e->y_root);
    }
}

static void on_button_release(XButtonEvent *e)
{
    if (drag_mode == DRAG_BUTTON) {
        Client *c = drag_c;
        drag_mode = DRAG_NONE; drag_c = NULL;
        if (!c) return;
        int btn = (e->window == c->frame) ? which_button(c, e->x, e->y) : BTN_NONE;
        if (btn == drag_btn) {
            if (btn == BTN_CLOSE) close_client(c);
            else if (btn == BTN_MAX) set_maximized(c, !c->maximized);
            else if (btn == BTN_MIN) minimize(c);
        }
        set_hover(NULL, 0);
        return;
    }
    if (drag_mode == DRAG_MOVE || drag_mode == DRAG_RESIZE) end_drag();
}

static void on_motion(XMotionEvent *e)
{
    XEvent ev;
    while (XCheckTypedEvent(dpy, MotionNotify, &ev)) e = &ev.xmotion;   /* only the latest position matters */
    if (drag_mode == DRAG_MOVE || drag_mode == DRAG_RESIZE) { drag_motion(e->x_root, e->y_root); return; }
    Client *c = find_client(e->window);
    if (!c || e->window != c->frame) return;
    if (drag_mode == DRAG_NONE) {
        set_hover(c, which_button(c, e->x, e->y));
        int edge = edge_at(c, e->x, e->y), i = edge_cursor_index(edge);
        XDefineCursor(dpy, c->frame, i >= 0 && !(hover_btn) ? cur_edge[i] : cur_normal);
    }
}

static void on_key(XKeyEvent *e)
{
    KeySym ks = XLookupKeysym(e, 0);
    if (ks == XK_F4 && (e->state & Mod1Mask) && active && active->type != T_DESKTOP) close_client(active);
}

/* ───────────────────────── setup ───────────────────────── */
static void load_fonts(void)
{
    void *h = dlopen("libXft.so.2", RTLD_NOW | RTLD_GLOBAL);
    if (h) {
        pXftFontOpenName = dlsym(h, "XftFontOpenName");
        pXftDrawCreate = dlsym(h, "XftDrawCreate");
        pXftDrawDestroy = dlsym(h, "XftDrawDestroy");
        pXftColorAllocValue = dlsym(h, "XftColorAllocValue");
        pXftDrawStringUtf8 = dlsym(h, "XftDrawStringUtf8");
        pXftTextExtentsUtf8 = dlsym(h, "XftTextExtentsUtf8");
        if (pXftFontOpenName && pXftDrawCreate && pXftDrawDestroy && pXftColorAllocValue && pXftDrawStringUtf8 && pXftTextExtentsUtf8) {
            char name[256];
            snprintf(name, sizeof name, "Noto Sans,DejaVu Sans,Liberation Sans,Cantarell,sans-serif:size=%.1f:weight=medium", 10.0 * scale);
            xftfont = pXftFontOpenName(dpy, scr, name);
            XRColor w = { 0xffff, 0xffff, 0xffff, 0xffff }, d = { 0xb4b4, 0xb4b4, 0xbaba, 0xffff };
            pXftColorAllocValue(dpy, DefaultVisual(dpy, scr), DefaultColormap(dpy, scr), &w, &xft_white);
            pXftColorAllocValue(dpy, DefaultVisual(dpy, scr), DefaultColormap(dpy, scr), &d, &xft_dim);
        }
    }
    if (!xftfont) {
        corefont = XLoadQueryFont(dpy, "-*-dejavu sans-medium-r-normal--14-*-*-*-*-*-iso10646-1");
        if (!corefont) corefont = XLoadQueryFont(dpy, "-misc-fixed-medium-r-normal--13-*-*-*-*-*-iso10646-1");
        if (!corefont) corefont = XLoadQueryFont(dpy, "fixed");
    }
}

static void read_scale(void)
{
    const char *s = getenv("REDSTAR_UI_SCALE");
    if (!s || !*s) s = getenv("AVALONIA_GLOBAL_SCALE_FACTOR");
    if (s && *s) { double v = atof(s); if (v >= 1 && v <= 4) scale = v; }
    TH = S(32); BW = S(3);
}

static void on_sigterm(int sig) { (void)sig; if (dpy) { XCloseDisplay(dpy); dpy = NULL; } _exit(0); }

/* -DREDSTAR_VERSION="..." from the build (tools/version.sh) */
#ifndef REDSTAR_VERSION
#define REDSTAR_VERSION "unknown"
#endif

int main(int argc, char **argv)
{
    for (int i = 1; i < argc; i++)
        if (!strcmp(argv[i], "--version")) { printf("redstar-wm %s\n", REDSTAR_VERSION); return 0; }
    for (int i = 1; i < argc; i++)
        if (!strcmp(argv[i], "--check")) {          /* "is a window manager running?" — exit 0 = none */
            dpy = XOpenDisplay(NULL);
            if (!dpy) return 3;
            XSetErrorHandler(xerror);
            XSelectInput(dpy, DefaultRootWindow(dpy), SubstructureRedirectMask);
            XSync(dpy, False);
            return other_wm ? 1 : 0;
        }
    dpy = XOpenDisplay(NULL);
    if (!dpy) { fprintf(stderr, "redstar-wm: cannot open display\n"); return 3; }
    signal(SIGTERM, on_sigterm); signal(SIGINT, on_sigterm); signal(SIGCHLD, SIG_IGN);
    scr = DefaultScreen(dpy);
    root = RootWindow(dpy, scr);
    SW = DisplayWidth(dpy, scr); SH = DisplayHeight(dpy, scr);
    read_scale();
    debug = getenv("REDSTAR_WM_DEBUG") && *getenv("REDSTAR_WM_DEBUG") == '1';

    XSetErrorHandler(xerror);
    XSelectInput(dpy, root, SubstructureRedirectMask | SubstructureNotifyMask | PropertyChangeMask | StructureNotifyMask);
    XSync(dpy, False);
    if (other_wm) { fprintf(stderr, "redstar-wm: another window manager is already running\n"); return 1; }

    XInternAtoms(dpy, (char **)atom_names, ATOM_COUNT, False, A);
    col_title_active = rgb("#1c1c21"); col_title_inactive = rgb("#2c2c33"); col_border = rgb("#3a3a42");
    col_accent = rgb("#c8102e"); col_text = rgb("#ffffff"); col_text_dim = rgb("#b4b4ba");
    col_close_hover = rgb("#e81123"); col_btn_hover = rgb("#3d3d46");
    gc = XCreateGC(dpy, root, 0, NULL);
    read_accent();
    load_fonts();

    cur_normal = XCreateFontCursor(dpy, XC_left_ptr);
    cur_move = XCreateFontCursor(dpy, XC_fleur);
    int shapes[8] = { XC_top_left_corner, XC_top_side, XC_top_right_corner, XC_right_side,
                      XC_bottom_right_corner, XC_bottom_side, XC_bottom_left_corner, XC_left_side };
    for (int i = 0; i < 8; i++) cur_edge[i] = XCreateFontCursor(dpy, shapes[i]);
    XDefineCursor(dpy, root, cur_normal);            /* an arrow instead of X's default "X" */
    XSetWindowBackground(dpy, root, BlackPixel(dpy, scr));
    XClearWindow(dpy, root);

    /* EWMH: who we are and what we support */
    check_win = XCreateSimpleWindow(dpy, root, -10, -10, 1, 1, 0, 0, 0);
    XChangeProperty(dpy, check_win, A[NET_SUPPORTING_WM_CHECK], XA_WINDOW, 32, PropModeReplace, (unsigned char *)&check_win, 1);
    XChangeProperty(dpy, check_win, A[NET_WM_NAME], A[UTF8_STRING], 8, PropModeReplace, (unsigned char *)"Crimson Banner", 14);
    XChangeProperty(dpy, root, A[NET_SUPPORTING_WM_CHECK], XA_WINDOW, 32, PropModeReplace, (unsigned char *)&check_win, 1);
    long pid = getpid();
    set_cardinals(check_win, A[NET_WM_PID], XA_CARDINAL, &pid, 1);
    long sup[ATOM_COUNT]; int ns = 0;
    for (int i = NET_SUPPORTED; i < ATOM_COUNT; i++) if (i != UTF8_STRING && i != MOTIF_WM_HINTS && i != REDSTAR_SHADOW) sup[ns++] = A[i];
    set_cardinals(root, A[NET_SUPPORTED], XA_ATOM, sup, ns);
    set_root_desktop_props();
    update_workarea();

    XGrabKey(dpy, XKeysymToKeycode(dpy, XK_F4), Mod1Mask, root, True, GrabModeAsync, GrabModeAsync);
    XGrabKey(dpy, XKeysymToKeycode(dpy, XK_F4), Mod1Mask | Mod2Mask, root, True, GrabModeAsync, GrabModeAsync);

    /* windows that were already there */
    Window r, p, *kids; unsigned int nk;
    if (XQueryTree(dpy, root, &r, &p, &kids, &nk)) {
        for (unsigned int i = 0; i < nk; i++) {
            XWindowAttributes wa;
            if (kids[i] == check_win || !XGetWindowAttributes(dpy, kids[i], &wa) || wa.override_redirect || wa.map_state != IsViewable) continue;
            manage(kids[i], &wa, 1);
        }
        if (kids) XFree(kids);
    }
    focus_next();
    XSync(dpy, False);
    fprintf(stderr, "redstar-wm: running (%dx%d, scale %.2f, titles %s)\n", SW, SH, scale, xftfont ? "Xft" : corefont ? "core font" : "none");

    XEvent ev;
    while (dpy && !XNextEvent(dpy, &ev)) {
        switch (ev.type) {
            case MapRequest: on_map_request(&ev.xmaprequest); break;
            case ConfigureRequest: on_configure_request(&ev.xconfigurerequest); break;
            case UnmapNotify: on_unmap(&ev.xunmap); break;
            case DestroyNotify: on_destroy(&ev.xdestroywindow); break;
            case PropertyNotify: on_property(&ev.xproperty); break;
            case ClientMessage: on_client_message(&ev.xclient); break;
            case ButtonPress: on_button_press(&ev.xbutton); break;
            case ButtonRelease: on_button_release(&ev.xbutton); break;
            case MotionNotify: on_motion(&ev.xmotion); break;
            case LeaveNotify: if (drag_mode == DRAG_NONE) { Client *c = find_client(ev.xcrossing.window); if (c && ev.xcrossing.window == c->frame) set_hover(NULL, 0); } break;
            case Expose: if (ev.xexpose.count == 0) { Client *c = find_client(ev.xexpose.window); if (c && ev.xexpose.window == c->frame) draw_frame(c); } break;
            case KeyPress: on_key(&ev.xkey); break;
            case ConfigureNotify:
                if (ev.xconfigure.window == root && (ev.xconfigure.width != SW || ev.xconfigure.height != SH)) {
                    SW = ev.xconfigure.width; SH = ev.xconfigure.height;
                    set_root_desktop_props(); update_workarea(); refit_all();
                }
                break;
        }
    }
    return 0;
}
