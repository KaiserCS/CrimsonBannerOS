/*
 * redstar-comp — Red Star OS's Wayland compositor (wlroots 0.20).
 *
 * Phase 2 of the Wayland plan: it replaces redstar-wm + picom. Red Star's shell and apps still talk X11, through a
 * rootless Xwayland this compositor runs; native Wayland apps work too (xdg-shell), with Red Star title bars.
 *
 *   redstar-comp [-d] [-- command args…]
 *
 * Runs nested (as a window on another Wayland or X11 desktop) or on the hardware from a text console, whichever
 * wlroots finds. The command (normally Red Star itself) starts with DISPLAY and WAYLAND_DISPLAY pointing here; the
 * compositor quits when it exits.
 *
 * What it does, matching redstar-wm:
 *   • title bars drawn by the compositor (red star, title, minimise / maximise / close; double-click maximises),
 *     3 px borders, resize from the edges, soft shadows on framed windows that aren't maximised or full screen;
 *   • click to focus and raise; the desktop window stays at the bottom and full screen; docks and notifications on top;
 *   • Alt+drag moves, Alt+right-drag resizes, dragging to the top edge maximises, to the left/right edge snaps half,
 *     to a corner a quarter, with a preview of where it will go in the shell's accent;
 *   • Alt+F4 closes; Ctrl+Alt+Backspace quits; Ctrl+Alt+F1…F12 switch consoles when running on the hardware;
 *   • for X11 apps it keeps the EWMH bits the Red Star shell reads: _NET_WORKAREA (minus the taskbar's strut),
 *     _NET_FRAME_EXTENTS, _NET_MOVERESIZE_WINDOW (snapping), _NET_CLOSE_WINDOW, and the compositing-manager selection
 *     (so translucent windows are drawn translucent).
 *
 * Borrowed patterns: wlroots' tinywl (public domain), Cage (MIT) and Sway (MIT). Licence: GPL-3.0-or-later.
 */
#define _POSIX_C_SOURCE 200809L
#include <assert.h>
#include <dirent.h>
#include <drm_fourcc.h>
#include <getopt.h>
#include "version.h"
#include <math.h>
#include <signal.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <sys/wait.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <errno.h>
#include <fcntl.h>
#include <stdarg.h>
#include <strings.h>
#include <sys/stat.h>
#include <linux/input-event-codes.h>
#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#include <cairo.h>
#include <wayland-server-core.h>
#include <xkbcommon/xkbcommon.h>
#include <xcb/xcb.h>
#include <xcb/xcb_icccm.h>

#include <wlr/backend.h>
#include <wlr/backend/session.h>
#include <wlr/interfaces/wlr_buffer.h>
#include <wlr/render/allocator.h>
#include <wlr/render/pixman.h>
#include <wlr/render/wlr_renderer.h>
#include <wlr/types/wlr_buffer.h>
#include <wlr/types/wlr_compositor.h>
#include <wlr/types/wlr_cursor.h>
#include <wlr/types/wlr_data_device.h>
#include <wlr/types/wlr_input_device.h>
#include <wlr/types/wlr_keyboard.h>
#include <wlr/types/wlr_output.h>
#include <wlr/types/wlr_output_layout.h>
#include <wlr/types/wlr_pointer.h>
#include <wlr/types/wlr_primary_selection.h>
#include <wlr/types/wlr_primary_selection_v1.h>
#include <wlr/types/wlr_scene.h>
#include <wlr/types/wlr_seat.h>
#include <wlr/types/wlr_subcompositor.h>
#include <wlr/types/wlr_viewporter.h>
#include <wlr/types/wlr_xcursor_manager.h>
#include <wlr/types/wlr_cursor_shape_v1.h>
#include <wlr/types/wlr_idle_inhibit_v1.h>
#include <wlr/types/wlr_idle_notify_v1.h>
#include <wlr/types/wlr_xdg_decoration_v1.h>
#include <wlr/types/wlr_xdg_output_v1.h>
#include <wlr/types/wlr_xdg_shell.h>
#include <wlr/util/box.h>
#include <wlr/util/edges.h>
#include <wlr/util/log.h>
#include <wlr/xwayland.h>
#include <wlr/types/wlr_output_management_v1.h>
#include <wlr/types/wlr_screencopy_v1.h>
#include <wlr/types/wlr_export_dmabuf_v1.h>
#include <wlr/types/wlr_ext_image_capture_source_v1.h>
#include <wlr/types/wlr_ext_image_copy_capture_v1.h>
#include <wlr/types/wlr_linux_dmabuf_v1.h>
#include <wlr/types/wlr_data_control_v1.h>
#include <wlr/types/wlr_xdg_activation_v1.h>
#include <wlr/types/wlr_virtual_keyboard_v1.h>
#include <wlr/types/wlr_virtual_pointer_v1.h>

#include "nested.h"

/* ───────────────────────── types ───────────────────────── */

enum kind { K_NORMAL, K_DIALOG, K_DESKTOP, K_DOCK, K_NOTIFY, K_SPLASH, K_UTILITY };
enum part { P_NONE, P_CLIENT, P_TITLE, P_MIN, P_MAX, P_CLOSE, P_EDGE };
enum grab { G_NONE, G_MOVE, G_RESIZE, G_BUTTON };

struct server;

struct view {
	struct wl_list link;                /* server->views, front = top of the stack */
	struct server *server;
	bool x11;
	struct wlr_xdg_toplevel *xdg;
	struct wlr_xwayland_surface *xs;
	bool unmanaged;                     /* X11 override-redirect: menus, tooltips — drawn as they are */

	struct wlr_scene_tree *tree;        /* the frame: at (x - side, y - top) */
	struct wlr_scene_tree *content;     /* the client's surfaces, inside the frame */
	struct wlr_scene_buffer *titlebar;
	struct wlr_scene_rect *border[3];   /* left, right, bottom */
	struct wlr_scene_tree *shadow;
	struct wlr_scene_buffer *shadow_part[8];

	int x, y, w, h;                     /* the client area, layout coordinates */
	struct wlr_box saved;               /* before maximise / full screen / snap */
	bool mapped, decorated, maximized, fullscreen, minimized, snapped;
	enum kind kind;
	int hover;                          /* title-bar button under the pointer */
	int title_w, title_active, title_hover;   /* what the title bar was last drawn for */
	char title[256];
	char token[48];                     /* the shell's own identity for its window (see token_take) */
	uint32_t strut[4];                  /* X11 _NET_WM_STRUT(_PARTIAL): left, right, top, bottom */
	uint32_t id;                        /* native windows: the shell knows them as (1 << 32) + id */
	bool announced;                     /* the shell was told about it */
	bool fading;                        /* fading in after it appeared */
	bool bare;                          /* the shell asked for no frame (its switcher, splash…) */
	struct timespec fade_start;

	struct wl_listener map, unmap, commit, destroy;
	struct wl_listener request_move, request_resize, request_maximize, request_minimize, request_fullscreen;
	struct wl_listener set_title, set_app_id;
	/* X11 only */
	struct wl_listener associate, dissociate, request_configure, request_activate, set_decorations,
		set_window_type, set_geometry;
};

struct popup {
	struct wlr_xdg_popup *popup;
	struct wlr_scene_tree *wrapper;     /* the desktop's menus: drawn above every window (see new_popup) */
	struct view *root;
	struct wl_list link;                /* server->popups */
	struct wl_listener commit, destroy, reposition;
};

struct decoration {
	struct wlr_xdg_toplevel_decoration_v1 *deco;
	struct wl_listener destroy, request_mode, commit;
};

struct output {
	struct wl_list link;
	struct server *server;
	struct wlr_output *wlr;
	struct wlr_scene_output *scene_output;
	bool in_layout;
	struct wl_listener frame, request_state, destroy, scene_destroy;
};

struct keyboard {
	struct wl_list link;
	struct server *server;
	struct wlr_keyboard *wlr;
	struct wl_listener modifiers, key, destroy;
	bool own_keymap;                    /* a virtual keyboard's: left as it is */
};

struct server {
	bool software;                 /* drawing with pixman: no GPU renderer could be made */
	struct wl_display *display;
	struct wl_event_loop *loop;
	struct wlr_backend *backend;
	struct wlr_session *session;
	struct wlr_renderer *renderer;
	struct wlr_allocator *allocator;
	struct wlr_compositor *compositor;
	struct wlr_scene *scene;
	struct wlr_scene_output_layout *scene_layout;
	struct wlr_output_layout *layout;
	struct wl_list outputs, views, keyboards, popups;

	struct wlr_scene_rect *background;
	struct wlr_scene_tree *l_desktop, *l_normal, *l_top, *l_full, *l_unmanaged, *l_drag;

	struct wlr_xdg_shell *xdg_shell;
	struct wlr_xdg_decoration_manager_v1 *deco_mgr;
	struct wlr_seat *seat;
	struct wlr_cursor *cursor;
	struct wlr_xcursor_manager *xcursor;
	const char *cursor_name;

	/* idle: the shell asks for "idle-start" after so many seconds without input; programs can hold it off (videos) */
	struct wlr_idle_notifier_v1 *idle_notifier;
	struct wl_listener new_inhibitor;
	int inhibitors, shell_inhibit;          /* idle-inhibit-v1 surfaces; the shell's own "inhibit 1" */
	int idle_timeout;                       /* seconds, 0: never */
	int64_t last_input_ms;
	struct wl_event_source *idle_timer;
	bool idle, dpms_off;

	/* keyboard layout from System Settings → Region & keyboard ("keymap"); NULL: XKB_DEFAULT_* or the system's */
	char *kb_layout, *kb_variant, *kb_options;

	struct view *focused;
	enum grab grab;
	struct view *grab_view;
	double grab_x, grab_y;
	struct wlr_box grab_box;
	uint32_t grab_edges;
	int grab_button_part;
	int snap_zone;                      /* where a dragged window would snap if dropped now (ZONE_*) */
	struct wlr_scene_buffer *snap_preview;
	struct view *last_click_view;
	uint32_t last_click_time;
	struct view *hover_view;            /* the window whose title-bar buttons are lit */

	struct wlr_box workarea;            /* the screen minus struts (taskbar) */

	struct wlr_xwayland *xwayland;
	xcb_connection_t *xcb;
	xcb_window_t xroot, xcm_owner;
	struct wl_event_source *xcb_source;

	pid_t child;
	int exit_code;

	/* the Red Star shell's connection (compositor/README: the IPC lines) */
	int ipc_fd;
	char ipc_path[108];
	struct wl_event_source *ipc_source;
	struct wl_list ipc_clients;
	uint32_t next_id;
	uint64_t last_active;
	char last_stack[2048];
	bool shell_shortcuts;               /* the shell asked for Super, Alt+Tab… (it draws the Start menu and switcher) */
	bool switching, super_down, super_used;
	uint8_t swallowed[128];             /* keycodes whose press we kept from clients: keep the release too */
	struct wl_event_source *fade_timer;
	struct wlr_output_manager_v1 *output_mgr;
	struct wl_listener output_apply, output_test, xdg_activate, new_virtual_keyboard, new_virtual_pointer;

	struct wl_listener new_output, layout_change, new_input, new_toplevel, new_popup, new_decoration,
		request_cursor, request_selection, request_primary, request_drag, start_drag, cursor_motion,
		cursor_motion_abs, cursor_button, cursor_axis, cursor_frame, xwayland_ready, new_xsurface;
	struct wl_listener request_shape;    /* cursor-shape-v1: a program names its cursor, we draw it */
};

/* ───────────────────────── look (matches redstar-wm) ───────────────────────── */

static double ui_scale = 1.0;
static bool want_fullscreen = true;     /* on another desktop: one full-screen window (-w: an ordinary window) */
static char *host_display;              /* the X11 desktop we run on, if any (DISPLAY is ours once Xwayland is up) */
static int TH = 32, BW = 3, SHADOW = 18, BTN_W = 46;
static int S(int v) { return (int)(v * ui_scale + 0.5); }

static const float C_BG[4] = { 0.10f, 0.10f, 0.12f, 1.0f };
#define HEX(r, g, b) (r) / 255.0, (g) / 255.0, (b) / 255.0
static const double C_TITLE_ACTIVE[3] = { HEX(0x1c, 0x1c, 0x21) };
static const double C_TITLE_INACTIVE[3] = { HEX(0x2c, 0x2c, 0x33) };
static double C_ACCENT[3] = { HEX(0xc8, 0x10, 0x2e) };     /* the shell's accent ("accent\t#RRGGBB") */
static const double C_CLOSE_HOVER[3] = { HEX(0xe8, 0x11, 0x23) };
static const double C_BTN_HOVER[3] = { HEX(0x3d, 0x3d, 0x46) };
static const double C_TEXT[3] = { 1, 1, 1 };
static const double C_TEXT_DIM[3] = { HEX(0xb4, 0xb4, 0xba) };
static const float F_BORDER_ACTIVE[4] = { 0x1c / 255.f, 0x1c / 255.f, 0x21 / 255.f, 1 };
static const float F_BORDER_INACTIVE[4] = { 0x3a / 255.f, 0x3a / 255.f, 0x42 / 255.f, 1 };

static int deco_top(struct view *v) { return v->decorated && !v->fullscreen ? TH : 0; }
static int deco_side(struct view *v) { return v->decorated && !v->fullscreen && !v->maximized ? BW : 0; }

static void view_update_decorations(struct view *v);
static struct view *view_from_xdg_toplevel(struct wlr_xdg_toplevel *t);
static void view_apply_geometry(struct view *v);
static void popups_follow(struct view *v);
static void focus_view(struct view *v);
static void update_workarea(struct server *srv);
static void x11_set_frame_extents(struct view *v);
static void x11_read_strut(struct view *v);
static void shell_changed(struct view *v);
static void shell_gone(struct view *v);
static void shell_stack(struct server *s);
static bool positions_get(struct view *v, struct wlr_box *box);
static void positions_put(struct view *v);
static void fade_in(struct view *v);
static bool roles_check(struct view *v);
static void set_custom_mode(struct wlr_output *wo, struct wlr_output_state *st, int w, int h, int mhz);
static void never_zero_hz(struct wlr_output *wo);
static bool child_of_desktop(struct view *v);
static void update_capabilities(struct server *s);
struct ipc_client;
static bool token_take(struct view *v, const char *title);
static void shell_token_line(struct view *v, struct ipc_client *only);
static void role_command(struct server *s, char *arg);
static void outputs_describe(struct server *s, struct ipc_client *c);
static void output_set_from_ipc(struct server *s, struct ipc_client *c, char *args);
static void outputs_save(struct server *s);
static void output_manager_update(struct server *s);

/* ───────────────────────── cairo-backed buffers (title bars, shadows) ───────────────────────── */

struct cairo_buffer {
	struct wlr_buffer base;
	cairo_surface_t *surface;
};

static void cbuf_destroy(struct wlr_buffer *b)
{
	struct cairo_buffer *cb = wl_container_of(b, cb, base);
	cairo_surface_destroy(cb->surface);
	free(cb);
}

static bool cbuf_begin(struct wlr_buffer *b, uint32_t flags, void **data, uint32_t *format, size_t *stride)
{
	(void)flags;
	struct cairo_buffer *cb = wl_container_of(b, cb, base);
	*data = cairo_image_surface_get_data(cb->surface);
	*stride = cairo_image_surface_get_stride(cb->surface);
	*format = DRM_FORMAT_ARGB8888;
	return true;
}

static void cbuf_end(struct wlr_buffer *b) { (void)b; }

static const struct wlr_buffer_impl cbuf_impl = {
	.destroy = cbuf_destroy,
	.begin_data_ptr_access = cbuf_begin,
	.end_data_ptr_access = cbuf_end,
};

static struct cairo_buffer *cbuf_create(int w, int h)
{
	struct cairo_buffer *cb = calloc(1, sizeof *cb);
	if (!cb) return NULL;
	cb->surface = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, w, h);
	wlr_buffer_init(&cb->base, &cbuf_impl, w, h);
	return cb;
}

/* One shadow picture, sliced into 8 parts (corners + stretched edges) for every framed window. */
static struct cairo_buffer *shadow_image;
static int SH_TILE;                     /* the shadow image is SH_TILE × SH_TILE: 2 × SHADOW + 2 */

static void make_shadow_image(void)
{
	SH_TILE = 2 * SHADOW + 2;
	shadow_image = cbuf_create(SH_TILE, SH_TILE);
	cairo_t *cr = cairo_create(shadow_image->surface);
	/* a soft rectangle: stacked rounded rects with falling alpha approximate a blur */
	for (int i = SHADOW; i > 0; i--) {
		double a = 0.30 * pow(1.0 - (double)i / SHADOW, 2.2) / SHADOW * 3.0;
		double inset = SHADOW - i;
		cairo_set_source_rgba(cr, 0, 0, 0, a);
		cairo_rectangle(cr, inset, inset + 3, SH_TILE - 2 * inset, SH_TILE - 2 * inset - 3);
		cairo_fill(cr);
	}
	cairo_destroy(cr);
	cairo_surface_flush(shadow_image->surface);
}

static bool no_input(struct wlr_scene_buffer *b, double *sx, double *sy)
{
	(void)b; (void)sx; (void)sy;
	return false;
}

/* ───────────────────────── title bar ───────────────────────── */

static void star_path(cairo_t *cr, double cx, double cy, double r)
{
	for (int i = 0; i < 10; i++) {
		double a = -M_PI / 2 + i * M_PI / 5, rad = (i % 2 == 0) ? r : r * 0.382;
		if (i == 0) cairo_move_to(cr, cx + rad * cos(a), cy + rad * sin(a));
		else cairo_line_to(cr, cx + rad * cos(a), cy + rad * sin(a));
	}
	cairo_close_path(cr);
}

static void draw_titlebar(struct view *v)
{
	int fw = v->w + 2 * deco_side(v);
	bool active = v->server->focused == v;
	if (fw < 1) fw = 1;
	if (v->title_w == fw && v->title_active == active && v->title_hover == v->hover && v->titlebar &&
		v->titlebar->buffer) {
		return;
	}
	v->title_w = fw;
	v->title_active = active;
	v->title_hover = v->hover;

	struct cairo_buffer *cb = cbuf_create(fw, TH);
	cairo_t *cr = cairo_create(cb->surface);
	const double *bg = active ? C_TITLE_ACTIVE : C_TITLE_INACTIVE;
	cairo_set_source_rgb(cr, bg[0], bg[1], bg[2]);
	cairo_paint(cr);
	if (active) {
		cairo_set_source_rgb(cr, C_ACCENT[0], C_ACCENT[1], C_ACCENT[2]);
		cairo_rectangle(cr, 0, 0, fw, S(2));
		cairo_fill(cr);
	}

	int bw = S(BTN_W), x_close = fw - bw, x_max = x_close - bw, x_min = x_max - bw;
	if (v->hover == P_MIN || v->hover == P_MAX || v->hover == P_CLOSE) {
		int hx = v->hover == P_MIN ? x_min : v->hover == P_MAX ? x_max : x_close;
		const double *c = v->hover == P_CLOSE ? C_CLOSE_HOVER : C_BTN_HOVER;
		cairo_set_source_rgb(cr, c[0], c[1], c[2]);
		cairo_rectangle(cr, hx, active ? S(2) : 0, bw, TH - (active ? S(2) : 0));
		cairo_fill(cr);
	}

	const double *fg = active ? C_TEXT : C_TEXT_DIM;
	cairo_set_source_rgb(cr, fg[0], fg[1], fg[2]);
	cairo_set_line_width(cr, S(1) < 1 ? 1 : S(1));
	double g = S(10), cy = TH / 2.0 + 0.5;
	/* minimise: a line */
	cairo_move_to(cr, x_min + bw / 2.0 - g / 2, cy);
	cairo_line_to(cr, x_min + bw / 2.0 + g / 2, cy);
	cairo_stroke(cr);
	/* maximise: a square, two when maximised */
	double mx = x_max + bw / 2.0 - g / 2 + 0.5;
	if (v->maximized) {
		double o = S(2);
		cairo_rectangle(cr, mx + o, cy - g / 2 - o, g - o, g - o);
		cairo_stroke(cr);
		cairo_set_source_rgb(cr, v->hover == P_MAX ? C_BTN_HOVER[0] : bg[0], v->hover == P_MAX ? C_BTN_HOVER[1] : bg[1],
			v->hover == P_MAX ? C_BTN_HOVER[2] : bg[2]);
		cairo_rectangle(cr, mx, cy - g / 2 + o, g - o, g - o);
		cairo_fill(cr);
		cairo_set_source_rgb(cr, fg[0], fg[1], fg[2]);
		cairo_rectangle(cr, mx, cy - g / 2 + o, g - o, g - o);
		cairo_stroke(cr);
	} else {
		cairo_rectangle(cr, mx, cy - g / 2, g, g);
		cairo_stroke(cr);
	}
	/* close: an X */
	if (v->hover == P_CLOSE) cairo_set_source_rgb(cr, 1, 1, 1);
	double cx = x_close + bw / 2.0;
	cairo_move_to(cr, cx - g / 2, cy - g / 2);
	cairo_line_to(cr, cx + g / 2, cy + g / 2);
	cairo_move_to(cr, cx + g / 2, cy - g / 2);
	cairo_line_to(cr, cx - g / 2, cy + g / 2);
	cairo_stroke(cr);

	/* red star + title */
	double sx = S(16);
	cairo_set_source_rgb(cr, C_ACCENT[0], C_ACCENT[1], C_ACCENT[2]);
	star_path(cr, sx, cy, S(7));
	cairo_fill(cr);
	int tx = (int)sx + S(14), maxw = x_min - tx - S(8);
	if (maxw > 10 && v->title[0]) {
		cairo_select_font_face(cr, "sans-serif", CAIRO_FONT_SLANT_NORMAL, CAIRO_FONT_WEIGHT_NORMAL);
		cairo_set_font_size(cr, S(13));
		char buf[300];
		snprintf(buf, sizeof buf, "%s", v->title);
		cairo_text_extents_t te;
		cairo_text_extents(cr, buf, &te);
		int len = (int)strlen(buf);
		while (te.x_advance > maxw && len > 0) {            /* shorten at a UTF-8 boundary, add "…" */
			len--;
			while (len > 0 && ((unsigned char)buf[len] & 0xC0) == 0x80) len--;
			if (len + 4 > (int)sizeof buf) break;
			memcpy(buf + len, "\xE2\x80\xA6", 4);
			cairo_text_extents(cr, buf, &te);
		}
		cairo_font_extents_t fe;
		cairo_font_extents(cr, &fe);
		cairo_set_source_rgb(cr, fg[0], fg[1], fg[2]);
		cairo_move_to(cr, tx, cy + (fe.ascent - fe.descent) / 2);
		cairo_show_text(cr, buf);
	}
	cairo_destroy(cr);
	cairo_surface_flush(cb->surface);

	if (v->titlebar) {
		wlr_scene_buffer_set_buffer(v->titlebar, &cb->base);
	} else {
		v->titlebar = wlr_scene_buffer_create(v->tree, &cb->base);
	}
	wlr_buffer_drop(&cb->base);
}

/* Which part of a framed window is at frame-relative (fx, fy). */
static int frame_part(struct view *v, double fx, double fy, uint32_t *edges)
{
	int side = deco_side(v), top = deco_top(v);
	int fw = v->w + 2 * side, fh = v->h + top + side;
	*edges = WLR_EDGE_NONE;
	if (!v->decorated || v->fullscreen) return P_CLIENT;
	if (!v->maximized) {
		int grip = side > S(4) ? side : S(4);
		if (fx < grip) *edges |= WLR_EDGE_LEFT;
		if (fx >= fw - grip) *edges |= WLR_EDGE_RIGHT;
		if (fy < S(4)) *edges |= WLR_EDGE_TOP;
		if (fy >= fh - grip) *edges |= WLR_EDGE_BOTTOM;
	}
	if (fy < top) {
		int bw = S(BTN_W), x_close = fw - bw, x_max = x_close - bw, x_min = x_max - bw;
		if (fy >= S(4) || *edges == WLR_EDGE_NONE) {
			if (fx >= x_close) return P_CLOSE;
			if (fx >= x_max) return P_MAX;
			if (fx >= x_min) return P_MIN;
		}
		if (*edges) return P_EDGE;
		return P_TITLE;
	}
	if (*edges) return P_EDGE;
	return P_CLIENT;
}

/* ───────────────────────── views ───────────────────────── */

static struct wlr_surface *view_surface(struct view *v)
{
	if (v->x11) return v->xs ? v->xs->surface : NULL;
	return v->xdg ? v->xdg->base->surface : NULL;
}

static struct wlr_scene_tree *layer_for(struct view *v)
{
	struct server *s = v->server;
	if (v->unmanaged) return s->l_unmanaged;
	if (v->fullscreen && s->focused == v) return s->l_full;
	switch (v->kind) {
	case K_DESKTOP: return s->l_desktop;
	case K_DOCK:
	case K_NOTIFY:
	case K_SPLASH: return s->l_top;
	default: return s->l_normal;
	}
}

static struct wlr_output *output_for_view(struct view *v)
{
	struct server *s = v->server;
	struct wlr_output *o = wlr_output_layout_output_at(s->layout, v->x + v->w / 2.0, v->y + v->h / 2.0);
	if (!o) o = wlr_output_layout_get_center_output(s->layout);
	return o;
}

static struct wlr_box output_box_for(struct view *v)
{
	struct wlr_box box = {0};
	struct wlr_output *o = output_for_view(v);
	if (o) wlr_output_layout_get_box(v->server->layout, o, &box);
	return box;
}

/* The work area on the view's screen: the screen minus the taskbar. */
static struct wlr_box workarea_for(struct view *v)
{
	struct wlr_box out = output_box_for(v), wa = v->server->workarea, r;
	if (wlr_box_intersection(&r, &out, &wa)) return r;
	return out;
}

static void set_cursor(struct server *s, const char *name)
{
	if (s->cursor_name && strcmp(s->cursor_name, name) == 0) return;
	s->cursor_name = name;
	wlr_cursor_set_xcursor(s->cursor, s->xcursor, name);
}

/* Moves the frame and asks the client for its new size. */
static void view_apply_geometry(struct view *v)
{
	if (!v->tree) return;
	int side = deco_side(v), top = deco_top(v);
	wlr_scene_node_set_position(&v->tree->node, v->x - side, v->y - top);
	popups_follow(v);
	if (v->x11) {
		if (v->xs) wlr_xwayland_surface_configure(v->xs, v->x, v->y, v->w > 0 ? v->w : 1, v->h > 0 ? v->h : 1);
		x11_set_frame_extents(v);
	} else if (v->xdg && v->xdg->base->initialized) {
		wlr_xdg_toplevel_set_size(v->xdg, v->w, v->h);
	}
	view_update_decorations(v);
}

static void view_update_decorations(struct view *v)
{
	if (!v->tree) return;
	bool show = v->decorated && !v->fullscreen;
	int side = deco_side(v), top = deco_top(v);
	int fw = v->w + 2 * side, fh = v->h + top + side;

	/* the client inside the frame (xdg: minus its own CSD shadow margin) */
	if (v->content) {
		int ox = 0, oy = 0;
		if (!v->x11 && v->xdg) {
			ox = -v->xdg->base->geometry.x;
			oy = -v->xdg->base->geometry.y;
		}
		wlr_scene_node_set_position(&v->content->node, side + ox, top + oy);
	}

	if (show) {
		draw_titlebar(v);
		wlr_scene_node_set_enabled(&v->titlebar->node, true);
		wlr_scene_node_set_position(&v->titlebar->node, 0, 0);
		const float *bc = v->server->focused == v ? F_BORDER_ACTIVE : F_BORDER_INACTIVE;
		for (int i = 0; i < 3; i++) {
			if (!v->border[i]) v->border[i] = wlr_scene_rect_create(v->tree, 1, 1, bc);
			wlr_scene_rect_set_color(v->border[i], bc);
			wlr_scene_node_set_enabled(&v->border[i]->node, side > 0);
		}
		if (side > 0) {
			wlr_scene_rect_set_size(v->border[0], side, fh - top);
			wlr_scene_node_set_position(&v->border[0]->node, 0, top);
			wlr_scene_rect_set_size(v->border[1], side, fh - top);
			wlr_scene_node_set_position(&v->border[1]->node, fw - side, top);
			wlr_scene_rect_set_size(v->border[2], fw, side);
			wlr_scene_node_set_position(&v->border[2]->node, 0, fh - side);
		}
	} else {
		if (v->titlebar) wlr_scene_node_set_enabled(&v->titlebar->node, false);
		for (int i = 0; i < 3; i++)
			if (v->border[i]) wlr_scene_node_set_enabled(&v->border[i]->node, false);
	}

	/* the shadow: framed windows that aren't maximised or full screen */
	bool shadow = show && !v->maximized && shadow_image;
	if (shadow && !v->shadow) {
		v->shadow = wlr_scene_tree_create(v->tree);
		wlr_scene_node_lower_to_bottom(&v->shadow->node);
		for (int i = 0; i < 8; i++) {
			v->shadow_part[i] = wlr_scene_buffer_create(v->shadow, &shadow_image->base);
			v->shadow_part[i]->point_accepts_input = no_input;   /* clicks go through the shadow */
		}
	}
	if (v->shadow) {
		wlr_scene_node_set_enabled(&v->shadow->node, shadow);
		if (shadow) {
			int R = SHADOW, T = SH_TILE, c = R + 1;          /* corner size; the middle row/column is 0 px of image */
			/* corners: top-left, top-right, bottom-left, bottom-right; edges: top, bottom, left, right */
			/* corners (c × c) sit just outside the frame's corners; edges stretch the 1 px middle row/column */
			struct { double sx, sy, sw, sh; int dx, dy, dw, dh; } p[8] = {
				{ 0, 0, c, c, -R, -R, c, c },
				{ T - c, 0, c, c, fw - 1, -R, c, c },
				{ 0, T - c, c, c, -R, fh - 1, c, c },
				{ T - c, T - c, c, c, fw - 1, fh - 1, c, c },
				{ c - 1, 0, 1, c, 1, -R, fw - 2, c },
				{ c - 1, T - c, 1, c, 1, fh - 1, fw - 2, c },
				{ 0, c - 1, c, 1, -R, 1, c, fh - 2 },
				{ T - c, c - 1, c, 1, fw - 1, 1, c, fh - 2 },
			};
			for (int i = 0; i < 8; i++) {
				struct wlr_fbox src = { p[i].sx, p[i].sy, p[i].sw, p[i].sh };
				int dw = p[i].dw > 0 ? p[i].dw : 1, dh = p[i].dh > 0 ? p[i].dh : 1;
				wlr_scene_buffer_set_source_box(v->shadow_part[i], &src);
				wlr_scene_buffer_set_dest_size(v->shadow_part[i], dw, dh);
				wlr_scene_node_set_position(&v->shadow_part[i]->node, p[i].dx, p[i].dy);
			}
		}
	}
}

static void view_raise(struct view *v)
{
	if (!v->tree || v->kind == K_DESKTOP) return;
	wl_list_remove(&v->link);
	wl_list_insert(&v->server->views, &v->link);
	wlr_scene_node_raise_to_top(&v->tree->node);
	if (v->x11 && v->xs) wlr_xwayland_surface_restack(v->xs, NULL, XCB_STACK_MODE_ABOVE);
}

static void view_set_activated(struct view *v, bool on)
{
	if (v->x11) { if (v->xs) wlr_xwayland_surface_activate(v->xs, on); }
	else if (v->xdg) wlr_xdg_toplevel_set_activated(v->xdg, on);
}

static void keyboard_enter(struct server *s, struct wlr_surface *surface)
{
	struct wlr_keyboard *kb = wlr_seat_get_keyboard(s->seat);
	if (kb) wlr_seat_keyboard_notify_enter(s->seat, surface, kb->keycodes, kb->num_keycodes, &kb->modifiers);
	else wlr_seat_keyboard_notify_enter(s->seat, surface, NULL, 0, NULL);
}

static void focus_view(struct view *v)
{
	struct server *s = v ? v->server : NULL;
	if (!v || !v->mapped || v->minimized) return;
	if (v->unmanaged) {
		/* menus and popups that ask for the keyboard get it without changing the active window */
		struct wlr_surface *surf = view_surface(v);
		if (surf) keyboard_enter(s, surf);
		return;
	}
	struct view *prev = s->focused;
	if (prev != v) {
		s->focused = v;
		if (prev) {
			view_set_activated(prev, false);
			if (prev->fullscreen && prev->tree) wlr_scene_node_reparent(&prev->tree->node, layer_for(prev));
			view_update_decorations(prev);
		}
	}
	view_raise(v);
	if (v->fullscreen && v->tree) wlr_scene_node_reparent(&v->tree->node, layer_for(v));
	view_set_activated(v, true);
	view_update_decorations(v);
	struct wlr_surface *surf = view_surface(v);
	if (surf) keyboard_enter(s, surf);
	shell_stack(s);
}

/* The window to focus when the active one goes away: the top visible normal one, else the desktop. */
static void focus_next(struct server *s, struct view *except)
{
	struct view *v, *desktop = NULL;
	wl_list_for_each(v, &s->views, link) {
		if (v == except || !v->mapped || v->minimized || v->unmanaged) continue;
		if (v->kind == K_DESKTOP) { if (!desktop) desktop = v; continue; }
		if (v->kind == K_DOCK || v->kind == K_NOTIFY) continue;
		focus_view(v);
		return;
	}
	if (desktop) focus_view(desktop);
	else {
		s->focused = NULL;
		wlr_seat_keyboard_notify_clear_focus(s->seat);
		shell_stack(s);
	}
}

static void view_set_maximized(struct view *v, bool on)
{
	if (v->kind == K_DESKTOP || v->fullscreen || v->maximized == on) return;
	if (on) {
		if (!v->snapped) v->saved = (struct wlr_box){ v->x, v->y, v->w, v->h };
		v->maximized = true;
		v->snapped = false;
		struct wlr_box wa = workarea_for(v);
		v->x = wa.x;
		v->y = wa.y + (v->decorated ? TH : 0);
		v->w = wa.width;
		v->h = wa.height - (v->decorated ? TH : 0);
	} else {
		v->maximized = false;
		if (v->saved.width > 0) { v->x = v->saved.x; v->y = v->saved.y; v->w = v->saved.width; v->h = v->saved.height; }
	}
	if (v->x11) { if (v->xs) wlr_xwayland_surface_set_maximized(v->xs, on, on); }
	else if (v->xdg) wlr_xdg_toplevel_set_maximized(v->xdg, on);
	view_apply_geometry(v);
	shell_changed(v);
}

static void view_set_fullscreen(struct view *v, bool on)
{
	if (v->fullscreen == on || v->kind == K_DESKTOP) return;
	if (on) {
		if (!v->maximized && !v->snapped) v->saved = (struct wlr_box){ v->x, v->y, v->w, v->h };
		struct wlr_box ob = output_box_for(v);
		v->fullscreen = true;
		v->x = ob.x; v->y = ob.y; v->w = ob.width; v->h = ob.height;
	} else {
		v->fullscreen = false;
		if (v->maximized) {
			v->maximized = false;
			view_set_maximized(v, true);
		} else if (v->saved.width > 0) {
			v->x = v->saved.x; v->y = v->saved.y; v->w = v->saved.width; v->h = v->saved.height;
		}
	}
	if (v->x11) { if (v->xs) wlr_xwayland_surface_set_fullscreen(v->xs, on); }
	else if (v->xdg) wlr_xdg_toplevel_set_fullscreen(v->xdg, on);
	if (v->tree) wlr_scene_node_reparent(&v->tree->node, layer_for(v));
	view_apply_geometry(v);
	shell_changed(v);
}

static void view_set_minimized(struct view *v, bool on)
{
	if (v->kind == K_DESKTOP || v->minimized == on) return;
	v->minimized = on;
	if (v->tree) wlr_scene_node_set_enabled(&v->tree->node, !on);
	if (v->x11 && v->xs) wlr_xwayland_surface_set_minimized(v->xs, on);
	if (on) {
		if (v->server->focused == v) {
			view_set_activated(v, false);
			v->server->focused = NULL;
			focus_next(v->server, v);
		}
	} else focus_view(v);
	shell_changed(v);
	shell_stack(v->server);
}

/* Where a window can snap, as on Windows: a half (left, right), the whole work area (top), or a quarter (corners). */
enum { ZONE_NONE, ZONE_LEFT, ZONE_RIGHT, ZONE_TOP, ZONE_TL, ZONE_TR, ZONE_BL, ZONE_BR };
static const char *ZONE_NAMES[] = { "none", "left", "right", "maximize", "top-left", "top-right", "bottom-left", "bottom-right" };

/* The part of the work area a zone covers (frames included). */
static struct wlr_box zone_box(struct wlr_box wa, int zone)
{
	int hw = wa.width / 2, hh = wa.height / 2;
	switch (zone) {
	case ZONE_LEFT: return (struct wlr_box){ wa.x, wa.y, hw, wa.height };
	case ZONE_RIGHT: return (struct wlr_box){ wa.x + hw, wa.y, wa.width - hw, wa.height };
	case ZONE_TL: return (struct wlr_box){ wa.x, wa.y, hw, hh };
	case ZONE_TR: return (struct wlr_box){ wa.x + hw, wa.y, wa.width - hw, hh };
	case ZONE_BL: return (struct wlr_box){ wa.x, wa.y + hh, hw, wa.height - hh };
	case ZONE_BR: return (struct wlr_box){ wa.x + hw, wa.y + hh, wa.width - hw, wa.height - hh };
	default: return wa;
	}
}

/* The zone under the pointer while a window is dragged: an edge of the work area, or near one of its corners. */
static int zone_at(struct wlr_box wa, double cx, double cy)
{
	int corner = S(64);
	bool left = cx <= wa.x + 1, right = cx >= wa.x + wa.width - 2, top = cy <= wa.y + 1;
	bool high = cy < wa.y + corner, low = cy > wa.y + wa.height - corner;
	if (left) return high ? ZONE_TL : low ? ZONE_BL : ZONE_LEFT;
	if (right) return high ? ZONE_TR : low ? ZONE_BR : ZONE_RIGHT;
	if (top) return cx < wa.x + corner ? ZONE_TL : cx > wa.x + wa.width - corner ? ZONE_TR : ZONE_TOP;
	return ZONE_NONE;
}

static void ipc_send(struct server *s, struct ipc_client *only, const char *fmt, ...);
static uint64_t view_shell_id(struct view *v);

/* A half or a quarter of the screen (Super+arrows, or a window dropped at an edge or corner). The shell hears of it
 * ("snapped\tID\tZONE"), to offer the other windows for the space left (Snap Assist). */
static void view_snap_zone(struct view *v, int zone, bool tell)
{
	if (v->kind == K_DESKTOP || v->fullscreen || zone == ZONE_NONE) return;
	if (zone == ZONE_TOP) { view_set_maximized(v, true); return; }
	if (v->maximized) view_set_maximized(v, false);
	if (!v->snapped) v->saved = (struct wlr_box){ v->x, v->y, v->w, v->h };
	v->snapped = true;
	struct wlr_box b = zone_box(workarea_for(v), zone);
	int top = deco_top(v), side = deco_side(v);
	v->x = b.x + side;
	v->y = b.y + top;
	v->w = b.width - 2 * side;
	v->h = b.height - top - side;
	view_apply_geometry(v);
	if (tell) ipc_send(v->server, NULL, "snapped\t%llu\t%s", (unsigned long long)view_shell_id(v), ZONE_NAMES[zone]);
}

static void view_snap(struct view *v, int dir) { view_snap_zone(v, dir < 0 ? ZONE_LEFT : ZONE_RIGHT, false); }

/* The preview of a snap while dragging: the zone, inset a little, a soft wash of the accent with a brighter edge,
 * under the dragged window and over the rest. */
static void snap_preview_hide(struct server *s)
{
	if (s->snap_preview) { wlr_scene_node_destroy(&s->snap_preview->node); s->snap_preview = NULL; }
	s->snap_zone = ZONE_NONE;
}

static void snap_preview_show(struct server *s, struct view *v, int zone)
{
	if (zone == s->snap_zone) return;
	snap_preview_hide(s);
	if (zone == ZONE_NONE || !v->tree) return;
	s->snap_zone = zone;
	struct wlr_box b = zone_box(workarea_for(v), zone);
	int inset = S(6), w = b.width - 2 * inset, h = b.height - 2 * inset;
	if (w <= 0 || h <= 0) return;
	struct cairo_buffer *cb = cbuf_create(w, h);
	if (!cb) return;
	cairo_t *cr = cairo_create(cb->surface);
	double r = S(10), lw = S(2), x0 = lw / 2, y0 = lw / 2, x1 = w - lw / 2, y1 = h - lw / 2;
	cairo_new_sub_path(cr);
	cairo_arc(cr, x1 - r, y0 + r, r, -M_PI / 2, 0);
	cairo_arc(cr, x1 - r, y1 - r, r, 0, M_PI / 2);
	cairo_arc(cr, x0 + r, y1 - r, r, M_PI / 2, M_PI);
	cairo_arc(cr, x0 + r, y0 + r, r, M_PI, 3 * M_PI / 2);
	cairo_close_path(cr);
	cairo_set_source_rgba(cr, C_ACCENT[0], C_ACCENT[1], C_ACCENT[2], 0.24);
	cairo_fill_preserve(cr);
	cairo_set_line_width(cr, lw);
	cairo_set_source_rgba(cr, C_ACCENT[0] + (1 - C_ACCENT[0]) * 0.25, C_ACCENT[1] + (1 - C_ACCENT[1]) * 0.25, C_ACCENT[2] + (1 - C_ACCENT[2]) * 0.25, 0.95);
	cairo_stroke(cr);
	cairo_destroy(cr);
	cairo_surface_flush(cb->surface);
	struct wlr_scene_tree *layer = layer_for(v);
	s->snap_preview = wlr_scene_buffer_create(layer, &cb->base);
	wlr_buffer_drop(&cb->base);
	if (!s->snap_preview) return;
	wlr_scene_node_set_position(&s->snap_preview->node, b.x + inset, b.y + inset);
	wlr_scene_node_place_below(&s->snap_preview->node, &v->tree->node);
}

static void view_close(struct view *v)
{
	if (v->kind == K_DESKTOP) return;
	if (v->x11) { if (v->xs) wlr_xwayland_surface_close(v->xs); }
	else if (v->xdg) wlr_xdg_toplevel_send_close(v->xdg);
}

/* A new window: centred in the work area (dialogs over their parent), shifted a step when it would land exactly
 * on another one. */
static void view_place(struct view *v, bool has_position)
{
	struct server *s = v->server;
	if (v->kind == K_DESKTOP) {
		struct wlr_box ob = output_box_for(v);
		if (ob.width <= 0) wlr_output_layout_get_box(s->layout, NULL, &ob);
		v->x = ob.x; v->y = ob.y; v->w = ob.width; v->h = ob.height;
		return;
	}
	struct wlr_box wa = s->workarea;
	if (wa.width <= 0) wlr_output_layout_get_box(s->layout, NULL, &wa);
	int top = deco_top(v);
	if (v->w > wa.width) v->w = wa.width;
	if (v->h + top > wa.height) v->h = wa.height - top;
	if (has_position) {
		if (v->y - top < wa.y) v->y = wa.y + top;       /* keep the title bar on screen */
		return;
	}
	struct wlr_box saved;
	if (v->kind == K_NORMAL && positions_get(v, &saved)) {
		/* where this app's window was last time (when it still fits on the screens there are now) */
		v->x = saved.x; v->y = saved.y; v->w = saved.width; v->h = saved.height;
		return;
	}
	v->x = wa.x + (wa.width - v->w) / 2;
	v->y = wa.y + (wa.height - v->h - top) / 2 + top;
	if (v->kind != K_NORMAL) return;
	for (int tries = 0; tries < 12; tries++) {
		bool clash = false;
		struct view *o;
		wl_list_for_each(o, &s->views, link) {
			if (o != v && o->mapped && !o->unmanaged && o->kind == K_NORMAL && o->x == v->x && o->y == v->y) { clash = true; break; }
		}
		if (!clash) break;
		v->x += S(28); v->y += S(28);
		if (v->x + v->w > wa.x + wa.width || v->y + v->h > wa.y + wa.height) {
			v->x = wa.x + S(28); v->y = wa.y + top + S(28);
		}
	}
}

/* Builds the frame when a window appears. */
static void view_map(struct view *v, struct wlr_surface *surface)
{
	struct server *s = v->server;
	v->mapped = true;
	v->tree = wlr_scene_tree_create(layer_for(v));
	v->tree->node.data = v;
	if (v->unmanaged) {
		struct wlr_scene_surface *ss = wlr_scene_surface_create(v->tree, surface);
		v->content = NULL;
		(void)ss;
		wlr_scene_node_set_position(&v->tree->node, v->xs->x, v->xs->y);
		wl_list_insert(&s->views, &v->link);
		if (wlr_xwayland_surface_override_redirect_wants_focus(v->xs)) focus_view(v);
		return;
	}
	if (v->x11) {
		v->content = wlr_scene_tree_create(v->tree);
		wlr_scene_surface_create(v->content, surface);
	} else {
		v->content = wlr_scene_xdg_surface_create(v->tree, v->xdg->base);
		v->xdg->base->data = v->content;
	}
	wl_list_insert(&s->views, &v->link);
	if (v->kind == K_DESKTOP) wlr_scene_node_lower_to_bottom(&v->tree->node);
	view_apply_geometry(v);
	if (v->kind != K_DESKTOP && v->kind != K_DOCK) fade_in(v);
	/* a window that reserves space (the taskbar's, set on the desktop window, possibly before it appeared) */
	if (v->strut[0] || v->strut[1] || v->strut[2] || v->strut[3]) update_workarea(s);
	if (v->kind == K_DOCK || v->kind == K_NOTIFY || v->kind == K_SPLASH) {
		/* panels and toasts don't take the keyboard from the window being used */
		if (v->kind == K_DOCK) update_workarea(s);
		return;
	}
	shell_changed(v);
	focus_view(v);
}

static void view_unmap(struct view *v)
{
	struct server *s = v->server;
	if (!v->mapped) return;
	positions_put(v);
	shell_gone(v);
	v->mapped = false;
	v->fading = false;
	if (s->grab_view == v) { s->grab = G_NONE; s->grab_view = NULL; snap_preview_hide(s); }
	if (s->last_click_view == v) s->last_click_view = NULL;
	if (s->hover_view == v) s->hover_view = NULL;
	v->hover = P_NONE;
	wl_list_remove(&v->link);
	wl_list_init(&v->link);
	bool was_focused = s->focused == v;
	if (was_focused) s->focused = NULL;
	if (v->tree) wlr_scene_node_destroy(&v->tree->node);
	v->tree = NULL; v->content = NULL; v->titlebar = NULL; v->shadow = NULL;
	memset(v->border, 0, sizeof v->border);
	memset(v->shadow_part, 0, sizeof v->shadow_part);
	v->title_w = -1;
	if (v->strut[0] || v->strut[1] || v->strut[2] || v->strut[3]) update_workarea(s);
	if (was_focused) focus_next(s, v);
	else shell_stack(s);
}

static void view_set_title(struct view *v, const char *t)
{
	snprintf(v->title, sizeof v->title, "%s", t ? t : "");
	v->title_w = -1;                     /* redraw */
	view_update_decorations(v);
	shell_changed(v);
}

/* The view (and part of it) under the pointer. */
static struct view *view_at(struct server *s, double lx, double ly, struct wlr_surface **surface, double *sx, double *sy,
	int *part, uint32_t *edges)
{
	*surface = NULL;
	*part = P_NONE;
	*edges = 0;
	struct wlr_scene_node *node = wlr_scene_node_at(&s->scene->tree.node, lx, ly, sx, sy);
	if (!node) return NULL;
	if (node->type == WLR_SCENE_NODE_BUFFER) {
		struct wlr_scene_buffer *buf = wlr_scene_buffer_from_node(node);
		struct wlr_scene_surface *ss = wlr_scene_surface_try_from_buffer(buf);
		if (ss) *surface = ss->surface;
	}
	struct wlr_scene_tree *tree = node->parent;
	while (tree && !tree->node.data) tree = tree->node.parent;
	if (!tree) return NULL;
	struct view *v = tree->node.data;
	if (v->unmanaged) { *part = P_CLIENT; return v; }
	if (*surface && v->content) {
		/* inside the client (its own surfaces, popups included): still check the frame's resize grips */
		double fx = lx - (v->x - deco_side(v)), fy = ly - (v->y - deco_top(v));
		uint32_t e;
		int p = frame_part(v, fx, fy, &e);
		int fw = v->w + 2 * deco_side(v), fh = v->h + deco_top(v) + deco_side(v);
		if (p == P_EDGE && fy >= deco_top(v) && fx >= 0 && fx < fw && fy < fh &&
			(fx < deco_side(v) || fx >= v->w + deco_side(v) || fy >= v->h + deco_top(v))) {
			*part = P_EDGE; *edges = e; *surface = NULL;
			return v;
		}
		*part = P_CLIENT;
		return v;
	}
	double fx = lx - (v->x - deco_side(v)), fy = ly - (v->y - deco_top(v));
	*part = frame_part(v, fx, fy, edges);
	*surface = NULL;
	return v;
}

/* ───────────────────────── interactive move / resize ───────────────────────── */

static void begin_grab(struct view *v, enum grab mode, uint32_t edges)
{
	struct server *s = v->server;
	if (v->fullscreen || v->kind == K_DESKTOP || v->kind == K_DOCK) return;
	if (mode == G_RESIZE && v->maximized) return;
	s->grab = mode;
	s->grab_view = v;
	s->grab_x = s->cursor->x;
	s->grab_y = s->cursor->y;
	s->grab_box = (struct wlr_box){ v->x, v->y, v->w, v->h };
	s->grab_edges = edges;
	if (mode == G_RESIZE && !v->x11 && v->xdg) wlr_xdg_toplevel_set_resizing(v->xdg, true);
}

static void end_grab(struct server *s)
{
	struct view *v = s->grab_view;
	if (v && s->grab == G_MOVE) {
		/* dropped at an edge or corner: top maximises, the sides take a half, the corners a quarter */
		int zone = zone_at(workarea_for(v), s->cursor->x, s->cursor->y);
		snap_preview_hide(s);
		if (zone != ZONE_NONE) view_snap_zone(v, zone, zone != ZONE_TOP);
	}
	if (v && s->grab == G_RESIZE && !v->x11 && v->xdg) wlr_xdg_toplevel_set_resizing(v->xdg, false);
	snap_preview_hide(s);
	s->grab = G_NONE;
	s->grab_view = NULL;
}

static void grab_motion(struct server *s)
{
	struct view *v = s->grab_view;
	if (!v) return;
	double dx = s->cursor->x - s->grab_x, dy = s->cursor->y - s->grab_y;
	if (s->grab == G_MOVE) {
		if (v->maximized || v->snapped) {
			if (fabs(dx) < S(8) && fabs(dy) < S(8)) return;
			/* dragging a maximised or snapped window restores it under the pointer */
			double rel = (s->cursor->x - v->x) / (v->w ? v->w : 1);
			if (v->maximized) view_set_maximized(v, false);
			else if (v->saved.width > 0) { v->w = v->saved.width; v->h = v->saved.height; v->snapped = false; }
			v->x = (int)(s->cursor->x - rel * v->w);
			v->y = (int)s->cursor->y + TH / 2;
			s->grab_x = s->cursor->x; s->grab_y = s->cursor->y;
			s->grab_box = (struct wlr_box){ v->x, v->y, v->w, v->h };
			view_apply_geometry(v);
			return;
		}
		v->x = s->grab_box.x + (int)dx;
		v->y = s->grab_box.y + (int)dy;
		if (v->y - deco_top(v) < s->workarea.y) v->y = s->workarea.y + deco_top(v);
		wlr_scene_node_set_position(&v->tree->node, v->x - deco_side(v), v->y - deco_top(v));
		popups_follow(v);
		if (v->x11 && v->xs) wlr_xwayland_surface_configure(v->xs, v->x, v->y, v->w, v->h);
		snap_preview_show(s, v, zone_at(workarea_for(v), s->cursor->x, s->cursor->y));
	} else if (s->grab == G_RESIZE) {
		struct wlr_box b = s->grab_box;
		int x = b.x, y = b.y, w = b.width, h = b.height;
		if (s->grab_edges & WLR_EDGE_LEFT) { w = b.width - (int)dx; x = b.x + (int)dx; }
		if (s->grab_edges & WLR_EDGE_RIGHT) w = b.width + (int)dx;
		if (s->grab_edges & WLR_EDGE_TOP) { h = b.height - (int)dy; y = b.y + (int)dy; }
		if (s->grab_edges & WLR_EDGE_BOTTOM) h = b.height + (int)dy;
		int minw = S(160), minh = S(60);
		if (v->x11 && v->xs && v->xs->size_hints) {
			if (v->xs->size_hints->min_width > minw) minw = v->xs->size_hints->min_width;
			if (v->xs->size_hints->min_height > minh) minh = v->xs->size_hints->min_height;
		} else if (!v->x11 && v->xdg) {
			if (v->xdg->current.min_width > minw) minw = v->xdg->current.min_width;
			if (v->xdg->current.min_height > minh) minh = v->xdg->current.min_height;
		}
		if (w < minw) { if (s->grab_edges & WLR_EDGE_LEFT) x -= minw - w; w = minw; }
		if (h < minh) { if (s->grab_edges & WLR_EDGE_TOP) y -= minh - h; h = minh; }
		v->x = x; v->y = y; v->w = w; v->h = h;
		view_apply_geometry(v);
	}
}

/* ───────────────────────── xdg-shell (native Wayland apps) ───────────────────────── */

static void xdg_commit(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, commit);
	struct wlr_xdg_toplevel *t = v->xdg;
	if (t->base->initial_commit) {
		/* first commit: answer with a configure (size 0×0 = the client chooses) */
		wlr_xdg_toplevel_set_wm_capabilities(t, XDG_TOPLEVEL_WM_CAPABILITIES_MAXIMIZE |
			XDG_TOPLEVEL_WM_CAPABILITIES_MINIMIZE | XDG_TOPLEVEL_WM_CAPABILITIES_FULLSCREEN);
		wlr_xdg_toplevel_set_size(t, 0, 0);
		return;
	}
	if (!v->mapped) return;
	struct wlr_box g = t->base->geometry;
	if (!v->maximized && !v->fullscreen && (g.width != v->w || g.height != v->h) && g.width > 0) {
		/* the client picked its own size (or finished a resize) */
		if (v->server->grab == G_RESIZE && v->server->grab_view == v && (v->server->grab_edges & WLR_EDGE_LEFT))
			v->x += v->w - g.width;
		if (v->server->grab == G_RESIZE && v->server->grab_view == v && (v->server->grab_edges & WLR_EDGE_TOP))
			v->y += v->h - g.height;
		v->w = g.width;
		v->h = g.height;
		wlr_scene_node_set_position(&v->tree->node, v->x - deco_side(v), v->y - deco_top(v));
		popups_follow(v);
	}
	view_update_decorations(v);
}

static void xdg_map(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, map);
	struct wlr_box g = v->xdg->base->geometry;
	v->w = g.width > 0 ? g.width : 640;
	v->h = g.height > 0 ? g.height : 480;
	/* a window with a parent is a dialog — except the shell's own app windows, whose parent is the desktop */
	v->kind = v->xdg->parent && !child_of_desktop(v) ? K_DIALOG : K_NORMAL;
	if (!token_take(v, v->xdg->title)) view_set_title(v, v->xdg->title);
	if (roles_check(v)) {
		/* one of the shell's own windows with a role (its desktop, running natively on Wayland); a frameless one
		 * still gets the state it asked for (the lock screen and slide show are full-screen) */
		view_place(v, false);
		view_map(v, v->xdg->base->surface);
		if (v->kind == K_UTILITY && v->xdg->requested.maximized) view_set_maximized(v, true);
		if (v->kind == K_UTILITY && v->xdg->requested.fullscreen) view_set_fullscreen(v, true);
		return;
	}
	if (v->kind == K_DIALOG) {
		/* a dialog: over its parent */
		struct view *pv = view_from_xdg_toplevel(v->xdg->parent);
		if (pv && pv->mapped) {
			v->x = pv->x + (pv->w - v->w) / 2;
			v->y = pv->y + (pv->h - v->h) / 2;
			view_place(v, true);
		} else view_place(v, false);
	} else view_place(v, false);
	view_map(v, v->xdg->base->surface);
	if (v->token[0]) shell_token_line(v, NULL);
	if (v->xdg->requested.maximized) view_set_maximized(v, true);
	if (v->xdg->requested.fullscreen) view_set_fullscreen(v, true);
}

static void xdg_unmap(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, unmap);
	view_unmap(v);
}

static void xdg_destroy(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, destroy);
	view_unmap(v);
	wl_list_remove(&v->commit.link);
	wl_list_remove(&v->map.link);
	wl_list_remove(&v->unmap.link);
	wl_list_remove(&v->destroy.link);
	wl_list_remove(&v->request_move.link);
	wl_list_remove(&v->request_resize.link);
	wl_list_remove(&v->request_maximize.link);
	wl_list_remove(&v->request_minimize.link);
	wl_list_remove(&v->request_fullscreen.link);
	wl_list_remove(&v->set_title.link);
	wl_list_remove(&v->set_app_id.link);
	free(v);
}

static void xdg_request_move(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, request_move);
	begin_grab(v, G_MOVE, 0);
}

static void xdg_request_resize(struct wl_listener *l, void *data)
{
	struct wlr_xdg_toplevel_resize_event *e = data;
	struct view *v = wl_container_of(l, v, request_resize);
	begin_grab(v, G_RESIZE, e->edges);
}

static void xdg_request_maximize(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, request_maximize);
	if (!v->xdg->base->initialized) return;
	if (!v->mapped) { wlr_xdg_surface_schedule_configure(v->xdg->base); return; }
	view_set_maximized(v, v->xdg->requested.maximized);
	wlr_xdg_surface_schedule_configure(v->xdg->base);   /* the protocol wants an answer even when nothing changes */
}

static void xdg_request_minimize(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, request_minimize);
	if (v->mapped) view_set_minimized(v, true);
}

static void xdg_request_fullscreen(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, request_fullscreen);
	if (!v->xdg->base->initialized) return;
	if (!v->mapped) { wlr_xdg_surface_schedule_configure(v->xdg->base); return; }
	view_set_fullscreen(v, v->xdg->requested.fullscreen);
	wlr_xdg_surface_schedule_configure(v->xdg->base);
}

static void xdg_set_title(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, set_title);
	if (token_take(v, v->xdg->title)) return;          /* the shell naming its window, not a title to show */
	view_set_title(v, v->xdg->title);
	roles_check(v);
}

static void xdg_set_app_id(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, set_app_id);
	shell_changed(v);
}

static void new_toplevel(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, new_toplevel);
	struct wlr_xdg_toplevel *t = data;
	struct view *v = calloc(1, sizeof *v);
	if (!v) return;
	v->server = s;
	v->xdg = t;
	v->decorated = false;               /* until it agrees to server-side decorations */
	v->title_w = -1;
	wl_list_init(&v->link);
	t->base->data = NULL;

	v->commit.notify = xdg_commit;
	wl_signal_add(&t->base->surface->events.commit, &v->commit);
	v->map.notify = xdg_map;
	wl_signal_add(&t->base->surface->events.map, &v->map);
	v->unmap.notify = xdg_unmap;
	wl_signal_add(&t->base->surface->events.unmap, &v->unmap);
	v->destroy.notify = xdg_destroy;
	wl_signal_add(&t->events.destroy, &v->destroy);
	v->request_move.notify = xdg_request_move;
	wl_signal_add(&t->events.request_move, &v->request_move);
	v->request_resize.notify = xdg_request_resize;
	wl_signal_add(&t->events.request_resize, &v->request_resize);
	v->request_maximize.notify = xdg_request_maximize;
	wl_signal_add(&t->events.request_maximize, &v->request_maximize);
	v->request_minimize.notify = xdg_request_minimize;
	wl_signal_add(&t->events.request_minimize, &v->request_minimize);
	v->request_fullscreen.notify = xdg_request_fullscreen;
	wl_signal_add(&t->events.request_fullscreen, &v->request_fullscreen);
	v->set_title.notify = xdg_set_title;
	wl_signal_add(&t->events.set_title, &v->set_title);
	v->set_app_id.notify = xdg_set_app_id;
	wl_signal_add(&t->events.set_app_id, &v->set_app_id);
	v->id = ++s->next_id;

	/* remember the view for decoration and dialog lookups */
	t->base->surface->data = v;
}

static struct view *view_from_xdg_toplevel(struct wlr_xdg_toplevel *t)
{
	return t && t->base && t->base->surface ? t->base->surface->data : NULL;
}

/* Red Star opens its app windows as children of its desktop window (so they stay with it): they are ordinary
 * windows, not dialogs — on the taskbar, in Alt+Tab, with their own place. */
static bool child_of_desktop(struct view *v)
{
	struct view *p = NULL;
	if (v->x11) p = v->xs && v->xs->parent ? v->xs->parent->data : NULL;
	else p = v->xdg && v->xdg->parent ? view_from_xdg_toplevel(v->xdg->parent) : NULL;
	return p && p->kind == K_DESKTOP;
}

/* popups (menus): kept inside the screen */
static void popup_unconstrain(struct wlr_xdg_popup *popup)
{
	struct wlr_xdg_surface *parent = wlr_xdg_surface_try_from_wlr_surface(popup->parent);
	while (parent && parent->role == WLR_XDG_SURFACE_ROLE_POPUP)
		parent = wlr_xdg_surface_try_from_wlr_surface(parent->popup->parent);
	if (!parent || parent->role != WLR_XDG_SURFACE_ROLE_TOPLEVEL) return;
	struct view *v = view_from_xdg_toplevel(parent->toplevel);
	if (!v || !v->tree) return;
	struct wlr_box ob = output_box_for(v);
	struct wlr_box box = { ob.x - v->x + v->xdg->base->geometry.x, ob.y - v->y + v->xdg->base->geometry.y, ob.width, ob.height };
	wlr_xdg_popup_unconstrain_from_box(popup, &box);
}

/* where the root window's surface starts, in layout coordinates (a popup's position is relative to it) */
static void popup_place_wrapper(struct popup *p)
{
	struct view *v = p->root;
	if (!p->wrapper || !v || !v->mapped || !v->xdg) return;
	struct wlr_box g = v->xdg->base->geometry;
	wlr_scene_node_set_position(&p->wrapper->node, v->x - g.x, v->y - g.y);
	/* a minimised window, or one on another workspace, takes its menus with it */
	wlr_scene_node_set_enabled(&p->wrapper->node, !v->tree || v->tree->node.enabled);
}

/* a window that moves (dragged, resized from the left or top, placed) takes its open menus along */
static void popups_follow(struct view *v)
{
	struct popup *p;
	wl_list_for_each(p, &v->server->popups, link)
		if (p->root == v) popup_place_wrapper(p);
}

static void popup_commit(struct wl_listener *l, void *data)
{
	(void)data;
	struct popup *p = wl_container_of(l, p, commit);
	if (p->popup->base->initial_commit) popup_unconstrain(p->popup);
	popup_place_wrapper(p);
}

static void popup_reposition(struct wl_listener *l, void *data)
{
	(void)data;
	struct popup *p = wl_container_of(l, p, reposition);
	popup_unconstrain(p->popup);
}

static void popup_destroy(struct wl_listener *l, void *data)
{
	(void)data;
	struct popup *p = wl_container_of(l, p, destroy);
	wl_list_remove(&p->commit.link);
	wl_list_remove(&p->destroy.link);
	wl_list_remove(&p->reposition.link);
	wl_list_remove(&p->link);
	if (p->wrapper) wlr_scene_node_destroy(&p->wrapper->node);
	free(p);
}

static void new_popup(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, new_popup);
	struct wlr_xdg_popup *xp = data;
	struct wlr_xdg_surface *parent = wlr_xdg_surface_try_from_wlr_surface(xp->parent);
	if (!parent || !parent->data) return;            /* parent tree: a toplevel's content tree or a popup's tree */
	struct wlr_scene_tree *parent_tree = parent->data;
	struct popup *p = calloc(1, sizeof *p);
	if (!p) return;
	p->popup = xp;
	wl_list_insert(&s->popups, &p->link);
	/* A window's menus go in the top layer, at the window's position, not inside its frame: there the frame's
	 * borders were drawn over a menu that reaches past the window, and the bottom resize grip took its clicks.
	 * (The desktop is the bottom window, so its menus would open under every other window otherwise, too.)
	 * Nested menus (submenus) go inside their parent menu's tree, so they're in the top layer as well. */
	if (parent->role == WLR_XDG_SURFACE_ROLE_TOPLEVEL) {
		struct view *root = view_from_xdg_toplevel(parent->toplevel);
		if (root) {
			p->root = root;
			p->wrapper = wlr_scene_tree_create(s->l_unmanaged);
			parent_tree = p->wrapper;
			popup_place_wrapper(p);
		}
	}
	xp->base->data = wlr_scene_xdg_surface_create(parent_tree, xp->base);
	p->commit.notify = popup_commit;
	wl_signal_add(&xp->base->surface->events.commit, &p->commit);
	p->destroy.notify = popup_destroy;
	wl_signal_add(&xp->events.destroy, &p->destroy);
	p->reposition.notify = popup_reposition;
	wl_signal_add(&xp->events.reposition, &p->reposition);
}

/* Decorations for native apps: a Red Star frame unless the app asks to draw its own (Chrome's tab strip, GNOME apps
 * using xdg-decoration); apps with no preference get the Red Star frame. Apps that don't use xdg-decoration at all
 * draw their own and get none. REDSTAR_FORCE_FRAMES=1 frames everything. */
static void deco_apply(struct decoration *d)
{
	const char *force = getenv("REDSTAR_FORCE_FRAMES");
	bool own = d->deco->requested_mode == WLR_XDG_TOPLEVEL_DECORATION_V1_MODE_CLIENT_SIDE && !(force && strcmp(force, "1") == 0);
	wlr_xdg_toplevel_decoration_v1_set_mode(d->deco, own ? WLR_XDG_TOPLEVEL_DECORATION_V1_MODE_CLIENT_SIDE
		: WLR_XDG_TOPLEVEL_DECORATION_V1_MODE_SERVER_SIDE);
	struct view *v = view_from_xdg_toplevel(d->deco->toplevel);
	if (v && v->bare) own = true;
	if (v && v->decorated != !own) {
		v->decorated = !own;
		v->title_w = -1;
		if (v->mapped) view_apply_geometry(v);
	}
}

static void deco_commit(struct wl_listener *l, void *data)
{
	(void)data;
	struct decoration *d = wl_container_of(l, d, commit);
	if (d->deco->toplevel->base->initial_commit) deco_apply(d);
}

static void deco_request_mode(struct wl_listener *l, void *data)
{
	(void)data;
	struct decoration *d = wl_container_of(l, d, request_mode);
	if (d->deco->toplevel->base->initialized) deco_apply(d);
}

static void deco_destroy(struct wl_listener *l, void *data)
{
	(void)data;
	struct decoration *d = wl_container_of(l, d, destroy);
	wl_list_remove(&d->destroy.link);
	wl_list_remove(&d->request_mode.link);
	wl_list_remove(&d->commit.link);
	free(d);
}

static void new_decoration(struct wl_listener *l, void *data)
{
	(void)l;
	struct wlr_xdg_toplevel_decoration_v1 *deco = data;
	struct decoration *d = calloc(1, sizeof *d);
	if (!d) return;
	d->deco = deco;
	d->destroy.notify = deco_destroy;
	wl_signal_add(&deco->events.destroy, &d->destroy);
	d->request_mode.notify = deco_request_mode;
	wl_signal_add(&deco->events.request_mode, &d->request_mode);
	d->commit.notify = deco_commit;
	wl_signal_add(&deco->toplevel->base->surface->events.commit, &d->commit);
}

/* ───────────────────────── Xwayland (Red Star's own windows, X11 apps) ───────────────────────── */

enum {
	A_NET_WM_WINDOW_TYPE_DESKTOP, A_NET_WM_WINDOW_TYPE_DOCK, A_NET_WM_WINDOW_TYPE_DIALOG,
	A_NET_WM_WINDOW_TYPE_NOTIFICATION, A_NET_WM_WINDOW_TYPE_SPLASH, A_NET_WM_WINDOW_TYPE_UTILITY,
	A_NET_WM_WINDOW_TYPE_TOOLBAR, A_NET_WM_WINDOW_TYPE_MENU, A_NET_WM_WINDOW_TYPE_DROPDOWN_MENU,
	A_NET_WM_WINDOW_TYPE_POPUP_MENU, A_NET_WM_WINDOW_TYPE_TOOLTIP,
	A_NET_WM_STRUT, A_NET_WM_STRUT_PARTIAL, A_NET_WORKAREA, A_NET_FRAME_EXTENTS, A_NET_MOVERESIZE_WINDOW,
	A_NET_CLOSE_WINDOW, A_NET_WM_CM_S0, A_NET_NUMBER_OF_DESKTOPS, A_NET_CURRENT_DESKTOP, A_COUNT
};
static const char *atom_names[A_COUNT] = {
	"_NET_WM_WINDOW_TYPE_DESKTOP", "_NET_WM_WINDOW_TYPE_DOCK", "_NET_WM_WINDOW_TYPE_DIALOG",
	"_NET_WM_WINDOW_TYPE_NOTIFICATION", "_NET_WM_WINDOW_TYPE_SPLASH", "_NET_WM_WINDOW_TYPE_UTILITY",
	"_NET_WM_WINDOW_TYPE_TOOLBAR", "_NET_WM_WINDOW_TYPE_MENU", "_NET_WM_WINDOW_TYPE_DROPDOWN_MENU",
	"_NET_WM_WINDOW_TYPE_POPUP_MENU", "_NET_WM_WINDOW_TYPE_TOOLTIP",
	"_NET_WM_STRUT", "_NET_WM_STRUT_PARTIAL", "_NET_WORKAREA", "_NET_FRAME_EXTENTS", "_NET_MOVERESIZE_WINDOW",
	"_NET_CLOSE_WINDOW", "_NET_WM_CM_S0", "_NET_NUMBER_OF_DESKTOPS", "_NET_CURRENT_DESKTOP",
};
static xcb_atom_t atoms[A_COUNT];

static bool has_type(struct wlr_xwayland_surface *xs, int a)
{
	for (size_t i = 0; i < xs->window_type_len; i++)
		if (xs->window_type[i] == atoms[a]) return true;
	return false;
}

static void x11_classify(struct view *v)
{
	struct wlr_xwayland_surface *xs = v->xs;
	v->kind = K_NORMAL;
	if (has_type(xs, A_NET_WM_WINDOW_TYPE_DESKTOP)) v->kind = K_DESKTOP;
	else if (has_type(xs, A_NET_WM_WINDOW_TYPE_DOCK) || has_type(xs, A_NET_WM_WINDOW_TYPE_TOOLBAR)) v->kind = K_DOCK;
	else if (has_type(xs, A_NET_WM_WINDOW_TYPE_NOTIFICATION)) v->kind = K_NOTIFY;
	else if (has_type(xs, A_NET_WM_WINDOW_TYPE_SPLASH)) v->kind = K_SPLASH;
	else if (has_type(xs, A_NET_WM_WINDOW_TYPE_UTILITY)) v->kind = K_UTILITY;
	else if (has_type(xs, A_NET_WM_WINDOW_TYPE_DIALOG) || (xs->parent && !child_of_desktop(v))) v->kind = K_DIALOG;
	/* title bars for normal windows, dialogs and utilities — unless the app asked for none (Motif hints) */
	v->decorated = (v->kind == K_NORMAL || v->kind == K_DIALOG || v->kind == K_UTILITY) &&
		xs->decorations == WLR_XWAYLAND_SURFACE_DECORATIONS_ALL;
}

static void x11_set_frame_extents(struct view *v)
{
	struct server *s = v->server;
	if (!s->xcb || !v->xs || v->unmanaged) return;
	uint32_t ext[4] = { (uint32_t)deco_side(v), (uint32_t)deco_side(v), (uint32_t)deco_top(v), (uint32_t)deco_side(v) };
	xcb_change_property(s->xcb, XCB_PROP_MODE_REPLACE, v->xs->window_id, atoms[A_NET_FRAME_EXTENTS],
		XCB_ATOM_CARDINAL, 32, 4, ext);
	xcb_flush(s->xcb);
}

static void x11_read_strut(struct view *v)
{
	struct server *s = v->server;
	memset(v->strut, 0, sizeof v->strut);
	if (!s->xcb || !v->xs) return;
	for (int which = 0; which < 2; which++) {
		xcb_get_property_cookie_t ck = xcb_get_property(s->xcb, 0, v->xs->window_id,
			atoms[which ? A_NET_WM_STRUT : A_NET_WM_STRUT_PARTIAL], XCB_ATOM_CARDINAL, 0, 12);
		xcb_get_property_reply_t *r = xcb_get_property_reply(s->xcb, ck, NULL);
		if (!r) continue;
		int n = xcb_get_property_value_length(r) / 4;
		if (r->format == 32 && n >= 4) {
			uint32_t *val = xcb_get_property_value(r);
			for (int i = 0; i < 4; i++) v->strut[i] = val[i];
			free(r);
			return;
		}
		free(r);
	}
}

/* The usable area: the whole layout minus every mapped window's strut (Red Star's taskbar). */
static void update_workarea(struct server *s)
{
	struct wlr_box all;
	wlr_output_layout_get_box(s->layout, NULL, &all);
	int l = 0, r = 0, t = 0, b = 0;
	struct view *v;
	wl_list_for_each(v, &s->views, link) {
		if (!v->mapped) continue;
		if ((int)v->strut[0] > l) l = v->strut[0];
		if ((int)v->strut[1] > r) r = v->strut[1];
		if ((int)v->strut[2] > t) t = v->strut[2];
		if ((int)v->strut[3] > b) b = v->strut[3];
	}
	struct wlr_box wa = { all.x + l, all.y + t, all.width - l - r, all.height - t - b };
	if (wa.width < 100 || wa.height < 100) wa = all;
	bool changed = memcmp(&wa, &s->workarea, sizeof wa) != 0;
	s->workarea = wa;
	if (s->xcb && s->xroot) {
		uint32_t w[4] = { (uint32_t)wa.x, (uint32_t)wa.y, (uint32_t)wa.width, (uint32_t)wa.height };
		xcb_change_property(s->xcb, XCB_PROP_MODE_REPLACE, s->xroot, atoms[A_NET_WORKAREA], XCB_ATOM_CARDINAL, 32, 4, w);
		xcb_flush(s->xcb);
	}
	if (!changed) return;
	/* refit maximised windows and the desktop */
	wl_list_for_each(v, &s->views, link) {
		if (!v->mapped || v->unmanaged) continue;
		if (v->maximized) { v->maximized = false; view_set_maximized(v, true); }
		else if (v->kind == K_DESKTOP) { view_place(v, false); view_apply_geometry(v); }
	}
}

static void x_associate(struct wl_listener *l, void *data);
static void x_dissociate(struct wl_listener *l, void *data);

static void x_map(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, map);
	struct wlr_xwayland_surface *xs = v->xs;
	v->unmanaged = xs->override_redirect;
	if (v->unmanaged) {
		view_map(v, xs->surface);
		return;
	}
	x11_classify(v);
	view_set_title(v, xs->title);
	x11_read_strut(v);
	if (v->server->xcb) {
		/* watch its properties (strut changes) */
		uint32_t mask = XCB_EVENT_MASK_PROPERTY_CHANGE;
		xcb_change_window_attributes(v->server->xcb, xs->window_id, XCB_CW_EVENT_MASK, &mask);
		xcb_flush(v->server->xcb);
	}
	v->x = xs->x; v->y = xs->y; v->w = xs->width; v->h = xs->height;
	bool positioned = false;
	if (xs->size_hints && (xs->size_hints->flags & (XCB_ICCCM_SIZE_HINT_US_POSITION | XCB_ICCCM_SIZE_HINT_P_POSITION)))
		positioned = true;
	if (v->x != 0 || v->y != 0) positioned = true;
	if (xs->parent && !positioned) {
		struct view *pv = xs->parent->data;
		if (pv && pv->mapped) {
			v->x = pv->x + (pv->w - v->w) / 2;
			v->y = pv->y + (pv->h - v->h) / 2;
			positioned = true;
		}
	}
	view_place(v, positioned);
	view_map(v, xs->surface);
	if (xs->maximized_vert && xs->maximized_horz) view_set_maximized(v, true);
	if (xs->fullscreen) view_set_fullscreen(v, true);
	if (v->kind == K_DOCK || v->strut[3] || v->strut[2]) update_workarea(v->server);
}

static void x_unmap(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, unmap);
	view_unmap(v);
}

static void x_commit(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, commit);
	if (!v->mapped || v->unmanaged || !v->xs->surface) return;
	int w = v->xs->surface->current.width, h = v->xs->surface->current.height;
	if (w > 0 && h > 0 && (w != v->w || h != v->h) && !v->maximized && !v->fullscreen && v->server->grab_view != v) {
		v->w = w; v->h = h;
		view_update_decorations(v);
	}
}

static void x_destroy(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, destroy);
	view_unmap(v);
	if (v->xs && v->xs->surface) x_dissociate(&v->dissociate, NULL);
	wl_list_remove(&v->associate.link);
	wl_list_remove(&v->dissociate.link);
	wl_list_remove(&v->destroy.link);
	wl_list_remove(&v->request_configure.link);
	wl_list_remove(&v->request_move.link);
	wl_list_remove(&v->request_resize.link);
	wl_list_remove(&v->request_maximize.link);
	wl_list_remove(&v->request_minimize.link);
	wl_list_remove(&v->request_fullscreen.link);
	wl_list_remove(&v->request_activate.link);
	wl_list_remove(&v->set_title.link);
	wl_list_remove(&v->set_decorations.link);
	wl_list_remove(&v->set_window_type.link);
	wl_list_remove(&v->set_geometry.link);
	if (v->xs) v->xs->data = NULL;
	free(v);
}

static void x_request_configure(struct wl_listener *l, void *data)
{
	struct wlr_xwayland_surface_configure_event *e = data;
	struct view *v = wl_container_of(l, v, request_configure);
	if (!v->mapped || v->unmanaged) {
		wlr_xwayland_surface_configure(v->xs, e->x, e->y, e->width, e->height);
		return;
	}
	if (v->maximized || v->fullscreen || v->kind == K_DESKTOP) {
		view_apply_geometry(v);              /* keep it where it is, but answer */
		return;
	}
	v->snapped = false;
	v->x = e->x; v->y = e->y; v->w = e->width; v->h = e->height;
	if (v->y - deco_top(v) < v->server->workarea.y) v->y = v->server->workarea.y + deco_top(v);
	view_apply_geometry(v);
}

static void x_request_move(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, request_move);
	if (v->mapped) begin_grab(v, G_MOVE, 0);
}

static void x_request_resize(struct wl_listener *l, void *data)
{
	struct wlr_xwayland_resize_event *e = data;
	struct view *v = wl_container_of(l, v, request_resize);
	if (v->mapped) begin_grab(v, G_RESIZE, e->edges);
}

static void x_request_maximize(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, request_maximize);
	if (!v->mapped) return;
	view_set_maximized(v, v->xs->maximized_vert || v->xs->maximized_horz);
}

static void x_request_minimize(struct wl_listener *l, void *data)
{
	struct wlr_xwayland_minimize_event *e = data;
	struct view *v = wl_container_of(l, v, request_minimize);
	if (v->mapped) view_set_minimized(v, e->minimize);
}

static void x_request_fullscreen(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, request_fullscreen);
	if (v->mapped) view_set_fullscreen(v, v->xs->fullscreen);
}

static void x_request_activate(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, request_activate);
	if (!v->mapped) return;
	if (v->minimized) view_set_minimized(v, false);
	else focus_view(v);
}

static void x_set_title(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, set_title);
	view_set_title(v, v->xs->title);
}

static void x_set_decorations(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, set_decorations);
	if (!v->mapped || v->unmanaged) return;
	bool was = v->decorated;
	x11_classify(v);
	if (was != v->decorated) view_apply_geometry(v);
}

static void x_set_window_type(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, set_window_type);
	if (!v->mapped || v->unmanaged) return;
	enum kind before = v->kind;
	x11_classify(v);
	if (before == v->kind) return;
	/* Red Star marks its desktop window after it appears: move it to the bottom and full screen */
	if (v->tree) wlr_scene_node_reparent(&v->tree->node, layer_for(v));
	if (v->kind == K_DESKTOP) {
		view_place(v, false);
		if (v->tree) wlr_scene_node_lower_to_bottom(&v->tree->node);
	}
	view_apply_geometry(v);
}

static void x_set_geometry(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, set_geometry);
	if (v->unmanaged && v->tree) wlr_scene_node_set_position(&v->tree->node, v->xs->x, v->xs->y);
}

static void x_associate(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, associate);
	v->map.notify = x_map;
	wl_signal_add(&v->xs->surface->events.map, &v->map);
	v->unmap.notify = x_unmap;
	wl_signal_add(&v->xs->surface->events.unmap, &v->unmap);
	v->commit.notify = x_commit;
	wl_signal_add(&v->xs->surface->events.commit, &v->commit);
	/* Xwayland may have committed the window's first picture before the window and its surface were matched up: then
	 * the surface is mapped already and its map signal has gone by. Without this the window was never framed or
	 * placed, and a dock's strut never reached the work area (an intermittent failure in the headless tests). */
	if (v->xs->surface->mapped) x_map(&v->map, NULL);
}

static void x_dissociate(struct wl_listener *l, void *data)
{
	(void)data;
	struct view *v = wl_container_of(l, v, dissociate);
	wl_list_remove(&v->map.link);
	wl_list_remove(&v->unmap.link);
	wl_list_remove(&v->commit.link);
	wl_list_init(&v->map.link);
	wl_list_init(&v->unmap.link);
	wl_list_init(&v->commit.link);
}

static void new_xsurface(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, new_xsurface);
	struct wlr_xwayland_surface *xs = data;
	struct view *v = calloc(1, sizeof *v);
	if (!v) return;
	v->server = s;
	v->x11 = true;
	v->xs = xs;
	v->title_w = -1;
	xs->data = v;
	wl_list_init(&v->link);
	wl_list_init(&v->map.link);
	wl_list_init(&v->unmap.link);
	wl_list_init(&v->commit.link);

#define LISTEN(sig, field, fn) do { v->field.notify = fn; wl_signal_add(&xs->events.sig, &v->field); } while (0)
	LISTEN(associate, associate, x_associate);
	LISTEN(dissociate, dissociate, x_dissociate);
	LISTEN(destroy, destroy, x_destroy);
	LISTEN(request_configure, request_configure, x_request_configure);
	LISTEN(request_move, request_move, x_request_move);
	LISTEN(request_resize, request_resize, x_request_resize);
	LISTEN(request_maximize, request_maximize, x_request_maximize);
	LISTEN(request_minimize, request_minimize, x_request_minimize);
	LISTEN(request_fullscreen, request_fullscreen, x_request_fullscreen);
	LISTEN(request_activate, request_activate, x_request_activate);
	LISTEN(set_title, set_title, x_set_title);
	LISTEN(set_decorations, set_decorations, x_set_decorations);
	LISTEN(set_window_type, set_window_type, x_set_window_type);
	LISTEN(set_geometry, set_geometry, x_set_geometry);
#undef LISTEN
}

static struct view *view_by_window(struct server *s, xcb_window_t w)
{
	struct view *v;
	wl_list_for_each(v, &s->views, link)
		if (v->x11 && v->xs && v->xs->window_id == w) return v;
	return NULL;
}

/* Our own X connection: EWMH requests the wlroots window manager leaves to the compositor. */
static int xcb_readable(int fd, uint32_t mask, void *data)
{
	(void)fd;
	struct server *s = data;
	if (mask & (WL_EVENT_HANGUP | WL_EVENT_ERROR)) {
		/* Xwayland went away (it restarts lazily when the next X11 app starts; we reconnect on "ready") */
		wl_event_source_remove(s->xcb_source);
		s->xcb_source = NULL;
		xcb_disconnect(s->xcb);
		s->xcb = NULL;
		s->xcm_owner = 0;
		return 0;
	}
	xcb_generic_event_t *ev;
	while ((ev = xcb_poll_for_event(s->xcb))) {
		uint8_t type = ev->response_type & 0x7f;
		if (type == XCB_CLIENT_MESSAGE) {
			xcb_client_message_event_t *cm = (xcb_client_message_event_t *)ev;
			struct view *v = view_by_window(s, cm->window);
			if (v && v->mapped && !v->unmanaged) {
				if (cm->type == atoms[A_NET_CLOSE_WINDOW]) view_close(v);
				else if (cm->type == atoms[A_NET_MOVERESIZE_WINDOW]) {
					/* snapping from the shell: x / y / width / height, each if its flag is set (client-area sizes) */
					uint32_t fl = cm->data.data32[0];
					int x = v->x, y = v->y, w = v->w, h = v->h;
					if (fl & (1 << 8)) x = (int32_t)cm->data.data32[1] + deco_side(v);
					if (fl & (1 << 9)) y = (int32_t)cm->data.data32[2] + deco_top(v);
					if (fl & (1 << 10)) w = cm->data.data32[3];
					if (fl & (1 << 11)) h = cm->data.data32[4];
					if (v->maximized) { v->maximized = false; wlr_xwayland_surface_set_maximized(v->xs, false, false); }
					if (!v->snapped) v->saved = (struct wlr_box){ v->x, v->y, v->w, v->h };
					v->snapped = true;
					v->x = x; v->y = y; v->w = w; v->h = h;
					view_apply_geometry(v);
				}
			}
		} else if (type == XCB_PROPERTY_NOTIFY) {
			xcb_property_notify_event_t *pn = (xcb_property_notify_event_t *)ev;
			if (pn->atom == atoms[A_NET_WM_STRUT] || pn->atom == atoms[A_NET_WM_STRUT_PARTIAL]) {
				struct view *v = view_by_window(s, pn->window);
				if (v) { x11_read_strut(v); update_workarea(s); }
			}
		}
		free(ev);
	}
	xcb_flush(s->xcb);
	return 0;
}

static void xwayland_ready(struct wl_listener *l, void *data)
{
	(void)data;
	struct server *s = wl_container_of(l, s, xwayland_ready);
	wlr_xwayland_set_seat(s->xwayland, s->seat);
	struct wlr_xcursor *xc = wlr_xcursor_manager_get_xcursor(s->xcursor, "default", 1);
	if (xc) {
		struct wlr_xcursor_image *img = xc->images[0];
		wlr_xwayland_set_cursor(s->xwayland, wlr_xcursor_image_get_buffer(img), img->hotspot_x, img->hotspot_y);
	}

	if (s->xcb_source) { wl_event_source_remove(s->xcb_source); s->xcb_source = NULL; }
	if (s->xcb) { xcb_disconnect(s->xcb); s->xcb = NULL; s->xcm_owner = 0; }
	s->xcb = xcb_connect(s->xwayland->display_name, NULL);
	if (xcb_connection_has_error(s->xcb)) {
		wlr_log(WLR_ERROR, "could not open our own X connection; snapping and the taskbar's work area won't work");
		xcb_disconnect(s->xcb);
		s->xcb = NULL;
		return;
	}
	xcb_intern_atom_cookie_t ck[A_COUNT];
	for (int i = 0; i < A_COUNT; i++) ck[i] = xcb_intern_atom(s->xcb, 0, strlen(atom_names[i]), atom_names[i]);
	for (int i = 0; i < A_COUNT; i++) {
		xcb_intern_atom_reply_t *r = xcb_intern_atom_reply(s->xcb, ck[i], NULL);
		atoms[i] = r ? r->atom : XCB_ATOM_NONE;
		free(r);
	}
	xcb_screen_t *screen = xcb_setup_roots_iterator(xcb_get_setup(s->xcb)).data;
	s->xroot = screen->root;
	uint32_t mask = XCB_EVENT_MASK_SUBSTRUCTURE_NOTIFY | XCB_EVENT_MASK_PROPERTY_CHANGE;
	xcb_change_window_attributes(s->xcb, s->xroot, XCB_CW_EVENT_MASK, &mask);

	/* a compositing manager is running (Avalonia only draws see-through windows when one is) */
	xcb_get_selection_owner_reply_t *own = xcb_get_selection_owner_reply(s->xcb,
		xcb_get_selection_owner(s->xcb, atoms[A_NET_WM_CM_S0]), NULL);
	if (own && own->owner == XCB_WINDOW_NONE) {
		s->xcm_owner = xcb_generate_id(s->xcb);
		xcb_create_window(s->xcb, XCB_COPY_FROM_PARENT, s->xcm_owner, s->xroot, -1, -1, 1, 1, 0,
			XCB_WINDOW_CLASS_INPUT_ONLY, XCB_COPY_FROM_PARENT, 0, NULL);
		xcb_set_selection_owner(s->xcb, s->xcm_owner, atoms[A_NET_WM_CM_S0], XCB_CURRENT_TIME);
	}
	free(own);

	uint32_t one = 1, zero = 0;
	xcb_change_property(s->xcb, XCB_PROP_MODE_REPLACE, s->xroot, atoms[A_NET_NUMBER_OF_DESKTOPS], XCB_ATOM_CARDINAL, 32, 1, &one);
	xcb_change_property(s->xcb, XCB_PROP_MODE_REPLACE, s->xroot, atoms[A_NET_CURRENT_DESKTOP], XCB_ATOM_CARDINAL, 32, 1, &zero);
	xcb_flush(s->xcb);
	s->xcb_source = wl_event_loop_add_fd(s->loop, xcb_get_file_descriptor(s->xcb), WL_EVENT_READABLE, xcb_readable, s);
	update_workarea(s);
	wlr_log(WLR_INFO, "Xwayland ready on %s", s->xwayland->display_name);
}

/* ───────────────────────── the Red Star shell's connection ─────────────────────────
 *
 * A Unix socket ($REDSTAR_COMP_SOCKET, set for everything redstar-comp starts) carrying one line per message,
 * fields separated by tabs. The shell sees X11 windows itself (EWMH); this adds what it can't see:
 *
 *   compositor → shell
 *     window <id> <flags> <pid> <app_id> <title>   a native Wayland window appeared or changed
 *                                                    (flags: "-" or any of m minimised, M maximised, f full screen)
 *     gone <id>                                      it closed
 *     active <id>                                    the active window (a native id, an X11 window id, or 0)
 *     stack <id> <id> …                              taskbar windows, most recently used first (native and X11)
 *     key <name>                                     a global shortcut: start, switcher-open, switcher-open-prev,
 *                                                    switcher-next, switcher-prev, switcher-commit, switcher-cancel,
 *                                                    snap-left, snap-right, maximize, minimize, clipboard, desktop,
 *                                                    files, lock, terminal, search
 *     snapped <id> <zone>                            a window was dropped at an edge or corner and snapped (left,
 *                                                    right, top-left, top-right, bottom-left, bottom-right): the
 *                                                    shell offers the other windows for the space left
 *   shell → compositor
 *     hello                                          answered with every window, then active and stack
 *     shortcuts on|off                               take Super / Alt+Tab / Ctrl+Alt+T for the shell (whatever has
 *                                                    the keyboard, X11 or Wayland)
 *     activate|close|minimize|maximize|restore|left|right|top-left|top-right|bottom-left|bottom-right <id>
 *     accent #RRGGBB                                 the shell's accent: snap previews and the active title bar
 *     peek on|off                                    every window nearly see-through, or solid again (Show desktop)
 *     switcher-end                                   the switcher was closed with the mouse
 *
 * Native ids are (1 << 32) + n, so they never collide with X11 window ids.
 */
#define NATIVE_BASE (1ULL << 32)

struct ipc_client {
	struct wl_list link;
	struct server *server;
	int fd;
	struct wl_event_source *source;
	char buf[4096];
	size_t len;
	bool dead;                          /* a write failed: freed when the loop is idle */
};

static void ipc_client_free(struct ipc_client *c)
{
	wl_event_source_remove(c->source);
	close(c->fd);
	wl_list_remove(&c->link);
	free(c);
}

static void ipc_reap(void *data)
{
	struct server *s = data;
	struct ipc_client *c, *tmp;
	wl_list_for_each_safe(c, tmp, &s->ipc_clients, link) if (c->dead) ipc_client_free(c);
}

static void ipc_kill(struct ipc_client *c)
{
	if (c->dead) return;
	c->dead = true;
	wl_event_loop_add_idle(c->server->loop, ipc_reap, c->server);
}

static void ipc_write(struct ipc_client *c, const char *line, size_t len)
{
	/* the shell reads all the time; a client that stops reading (full buffer) is dropped rather than block us */
	while (len > 0 && !c->dead) {
		ssize_t n = send(c->fd, line, len, MSG_DONTWAIT | MSG_NOSIGNAL);
		if (n <= 0) { if (n < 0 && errno == EINTR) continue; ipc_kill(c); return; }
		line += n; len -= (size_t)n;
	}
}

static void ipc_vsend(struct server *s, struct ipc_client *only, const char *fmt, va_list ap)
{
	char line[4096];
	int n = vsnprintf(line, sizeof line - 1, fmt, ap);
	if (n < 0) return;
	if (n > (int)sizeof line - 2) n = sizeof line - 2;
	line[n++] = '\n';
	if (only) { ipc_write(only, line, (size_t)n); return; }
	struct ipc_client *c, *tmp;
	wl_list_for_each_safe(c, tmp, &s->ipc_clients, link) ipc_write(c, line, (size_t)n);
}

static void ipc_send(struct server *s, struct ipc_client *only, const char *fmt, ...)
{
	if (wl_list_empty(&s->ipc_clients)) return;
	va_list ap;
	va_start(ap, fmt);
	ipc_vsend(s, only, fmt, ap);
	va_end(ap);
}

static void clean(char *dst, size_t size, const char *src)
{
	size_t i = 0;
	for (; src && *src && i + 1 < size; src++) dst[i++] = (*src == '\t' || *src == '\n' || *src == '\r') ? ' ' : *src;
	dst[i] = 0;
}

/* a window the taskbar lists: a native Wayland top-level window (X11 ones the shell reads itself) */
static bool native_listed(struct view *v)
{
	return !v->x11 && v->xdg && v->mapped && v->kind == K_NORMAL && (!v->xdg->parent || child_of_desktop(v));
}

static uint64_t view_shell_id(struct view *v)
{
	if (v->x11) return v->xs ? v->xs->window_id : 0;
	return NATIVE_BASE + v->id;
}

static void shell_window_line(struct view *v, struct ipc_client *only)
{
	char flags[4] = "-", app[128], title[300];
	int f = 0;
	if (v->minimized) flags[f++] = 'm';
	if (v->maximized) flags[f++] = 'M';
	if (v->fullscreen) flags[f++] = 'f';
	if (f) flags[f] = 0;
	pid_t pid = 0;
	if (v->xdg->base->resource) wl_client_get_credentials(wl_resource_get_client(v->xdg->base->resource), &pid, NULL, NULL);
	clean(app, sizeof app, v->xdg->app_id);
	clean(title, sizeof title, v->title);
	ipc_send(v->server, only, "window\t%llu\t%s\t%d\t%s\t%s", (unsigned long long)view_shell_id(v), flags, (int)pid, app, title);
}

static void shell_changed(struct view *v)
{
	if (!native_listed(v)) return;
	v->announced = true;
	shell_window_line(v, NULL);
}

static void shell_gone(struct view *v)
{
	if (!v->announced) return;
	v->announced = false;
	ipc_send(v->server, NULL, "gone\t%llu", (unsigned long long)view_shell_id(v));
}

static bool stack_listed(struct view *v)
{
	if (!v->mapped || v->unmanaged) return false;
	if (v->x11) return v->kind == K_NORMAL && v->xs;
	return native_listed(v);
}

static void shell_stack(struct server *s)
{
	if (wl_list_empty(&s->ipc_clients)) return;
	uint64_t active = s->focused && stack_listed(s->focused) ? view_shell_id(s->focused) : 0;
	if (active != s->last_active) {
		s->last_active = active;
		ipc_send(s, NULL, "active\t%llu", (unsigned long long)active);
	}
	/* views are kept most recently raised first: that's the Alt+Tab order */
	char line[2048] = "stack";
	size_t len = strlen(line);
	struct view *v;
	wl_list_for_each(v, &s->views, link) {
		if (!stack_listed(v)) continue;
		int n = snprintf(line + len, sizeof line - len, "\t%llu", (unsigned long long)view_shell_id(v));
		if (n < 0 || len + (size_t)n >= sizeof line) break;
		len += (size_t)n;
	}
	if (strcmp(line, s->last_stack) == 0) return;
	memcpy(s->last_stack, line, len + 1);
	ipc_send(s, NULL, "%s", line);
}

static struct view *view_by_shell_id(struct server *s, uint64_t id)
{
	struct view *v;
	wl_list_for_each(v, &s->views, link)
		if (v->mapped && !v->unmanaged && view_shell_id(v) == id) return v;
	return NULL;
}

static void cursor_theme_set(struct server *s, const char *theme, int size);
static void idle_rearm(struct server *s);
static void input_activity(struct server *s);
static void keymap_set(struct server *s, const char *layout, const char *variant, const char *options);
static void dpms_set(struct server *s, bool on);

static void peek_buffer(struct wlr_scene_buffer *b, int sx, int sy, void *data)
{
	(void)sx; (void)sy;
	wlr_scene_buffer_set_opacity(b, *(float *)data);
}

static void ipc_command(struct ipc_client *c, char *line)
{
	struct server *s = c->server;
	char *arg = strchr(line, '\t');
	if (!arg) arg = strchr(line, ' ');
	if (arg) *arg++ = 0;
	if (strcmp(line, "hello") == 0) {
		struct view *v;
		wl_list_for_each_reverse(v, &s->views, link) {
			if (native_listed(v)) { v->announced = true; shell_window_line(v, c); }
			if (v->mapped && v->token[0]) shell_token_line(v, c);
		}
		uint64_t active = s->focused && stack_listed(s->focused) ? view_shell_id(s->focused) : 0;
		ipc_send(s, c, "active\t%llu", (unsigned long long)active);
		s->last_active = active;
		s->last_stack[0] = 0;
		shell_stack(s);
		return;
	}
	if (strcmp(line, "shortcuts") == 0) {
		s->shell_shortcuts = arg && strcmp(arg, "on") == 0;
		wlr_log(WLR_INFO, "global shortcuts %s for the shell", s->shell_shortcuts ? "taken" : "released");
		return;
	}
	if (strcmp(line, "switcher-end") == 0) { s->switching = false; return; }
	if (strcmp(line, "keymap") == 0) {               /* "keymap\tLAYOUTS\tVARIANTS\tOPTIONS" */
		char *layout = arg ? arg : (char *)"", *variant = strchr(layout, '\t'), *options = NULL;
		if (variant) { *variant++ = 0; options = strchr(variant, '\t'); if (options) *options++ = 0; }
		keymap_set(s, layout, variant, options);
		return;
	}
	if (strcmp(line, "idle-timeout") == 0) {          /* seconds without input before "idle-start"; 0: never */
		s->idle_timeout = arg ? atoi(arg) : 0;
		if (s->idle_timeout < 0) s->idle_timeout = 0;
		idle_rearm(s);
		return;
	}
	if (strcmp(line, "dpms") == 0) { dpms_set(s, !(arg && strcmp(arg, "off") == 0)); return; }
	if (strcmp(line, "inhibit") == 0) {                /* the shell holds idle off (its own video player, a D-Bus inhibitor) */
		s->shell_inhibit = arg && atoi(arg) > 0;
		wlr_idle_notifier_v1_set_inhibited(s->idle_notifier, s->inhibitors > 0 || s->shell_inhibit);
		return;
	}
	if (strcmp(line, "cursor") == 0) {
		char *theme = arg ? arg : (char *)"", *size = strchr(theme, '\t');
		if (size) *size++ = 0;
		cursor_theme_set(s, theme, size ? atoi(size) : 24);
		return;
	}
	if (strcmp(line, "accent") == 0) {               /* "accent\t#RRGGBB": snap previews and title bars */
		unsigned int r, g, b;
		if (arg && sscanf(arg, "#%02x%02x%02x", &r, &g, &b) == 3) {
			C_ACCENT[0] = r / 255.0; C_ACCENT[1] = g / 255.0; C_ACCENT[2] = b / 255.0;
			struct view *v;
			wl_list_for_each(v, &s->views, link) if (v->mapped) view_apply_geometry(v);   /* redraws the title bars */
		}
		return;
	}
	if (strcmp(line, "peek") == 0) {                 /* "peek\ton|off": every window nearly see-through (Show desktop) */
		float op = arg && strcmp(arg, "on") == 0 ? 0.08f : 1.0f;
		struct view *v;
		wl_list_for_each(v, &s->views, link)
			if (v->tree && v->kind != K_DESKTOP && v->kind != K_DOCK && v->kind != K_NOTIFY)
				wlr_scene_node_for_each_buffer(&v->tree->node, peek_buffer, &op);
		return;
	}
	if (strcmp(line, "outputs") == 0) { outputs_describe(s, c); return; }
	if (strcmp(line, "role") == 0) { if (arg) role_command(s, arg); return; }
	if (strcmp(line, "outputs-save") == 0) { outputs_save(s); return; }
	if (strcmp(line, "output-set") == 0) { output_set_from_ipc(s, c, arg ? arg : (char *)""); return; }
	if (!arg) return;
	struct view *v = view_by_shell_id(s, strtoull(arg, NULL, 10));
	if (!v) return;
	if (strcmp(line, "activate") == 0) { if (v->minimized) view_set_minimized(v, false); else focus_view(v); }
	else if (strcmp(line, "close") == 0) view_close(v);
	else if (strcmp(line, "minimize") == 0) view_set_minimized(v, true);
	else if (strcmp(line, "maximize") == 0) view_set_maximized(v, true);
	else if (strcmp(line, "restore") == 0) { if (v->maximized) view_set_maximized(v, false); }
	else if (strcmp(line, "left") == 0) view_snap(v, -1);
	else if (strcmp(line, "right") == 0) view_snap(v, 1);
	else for (int z = ZONE_TL; z <= ZONE_BR; z++) if (strcmp(line, ZONE_NAMES[z]) == 0) { view_snap_zone(v, z, false); break; }
}

static int ipc_readable(int fd, uint32_t mask, void *data)
{
	struct ipc_client *c = data;
	if (c->dead) return 0;
	if (mask & (WL_EVENT_HANGUP | WL_EVENT_ERROR)) { ipc_kill(c); return 0; }
	ssize_t n = read(fd, c->buf + c->len, sizeof c->buf - 1 - c->len);
	if (n <= 0) { if (n < 0 && (errno == EAGAIN || errno == EINTR)) return 0; ipc_kill(c); return 0; }
	c->len += (size_t)n;
	c->buf[c->len] = 0;
	char *start = c->buf, *nl;
	while ((nl = strchr(start, '\n'))) {
		*nl = 0;
		if (nl > start && nl[-1] == '\r') nl[-1] = 0;
		if (!c->dead) ipc_command(c, start);
		start = nl + 1;
	}
	c->len = strlen(start);
	memmove(c->buf, start, c->len + 1);
	if (c->len >= sizeof c->buf - 1) c->len = 0;        /* an overlong line: forget it */
	return 0;
}

static int ipc_accept(int fd, uint32_t mask, void *data)
{
	(void)mask;
	struct server *s = data;
	int cfd = accept(fd, NULL, NULL);
	if (cfd < 0) return 0;
	fcntl(cfd, F_SETFD, FD_CLOEXEC);
	fcntl(cfd, F_SETFL, O_NONBLOCK);
	struct ipc_client *c = calloc(1, sizeof *c);
	if (!c) { close(cfd); return 0; }
	c->server = s;
	c->fd = cfd;
	c->source = wl_event_loop_add_fd(s->loop, cfd, WL_EVENT_READABLE, ipc_readable, c);
	wl_list_insert(&s->ipc_clients, &c->link);
	return 0;
}

static void ipc_start(struct server *s, const char *socket_name)
{
	snprintf(s->ipc_path, sizeof s->ipc_path, "%s/redstar-comp.%s.sock", getenv("XDG_RUNTIME_DIR"), socket_name);
	int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
	if (fd < 0) return;
	struct sockaddr_un addr = { .sun_family = AF_UNIX };
	snprintf(addr.sun_path, sizeof addr.sun_path, "%s", s->ipc_path);
	unlink(s->ipc_path);
	if (bind(fd, (struct sockaddr *)&addr, sizeof addr) < 0 || listen(fd, 8) < 0) {
		wlr_log(WLR_ERROR, "no shell connection: could not open %s", s->ipc_path);
		close(fd);
		return;
	}
	s->ipc_fd = fd;
	s->ipc_source = wl_event_loop_add_fd(s->loop, fd, WL_EVENT_READABLE, ipc_accept, s);
	setenv("REDSTAR_COMP_SOCKET", s->ipc_path, true);
}

static void ipc_stop(struct server *s)
{
	struct ipc_client *c, *tmp;
	wl_list_for_each_safe(c, tmp, &s->ipc_clients, link) ipc_client_free(c);
	if (s->ipc_source) wl_event_source_remove(s->ipc_source);
	if (s->ipc_fd >= 0) { close(s->ipc_fd); unlink(s->ipc_path); }
}

/* ── tokens: a stable identity for the shell's own windows ──
 * Titles repeat ("Files — Documents" twice), so the shell names each of its windows once: it sets the title to an
 * invisible marker (U+2063) + "rs:" + a token, redstar-comp keeps the token for that window for good — never showing
 * that title — and answers "token <token> <id>"; then the shell puts the real title back. */
#define TOKEN_MARK "\xE2\x81\xA3rs:"

static void shell_token_line(struct view *v, struct ipc_client *only)
{
	if (!v->token[0] || v->x11) return;
	ipc_send(v->server, only, "token\t%s\t%llu", v->token, (unsigned long long)view_shell_id(v));
}

static bool token_take(struct view *v, const char *title)
{
	size_t n = strlen(TOKEN_MARK);
	if (v->x11 || !title || strncmp(title, TOKEN_MARK, n) != 0) return false;
	size_t i = 0;
	for (const char *t = title + n; *t && i + 1 < sizeof v->token; t++)
		if ((*t >= 'a' && *t <= 'z') || (*t >= 'A' && *t <= 'Z') || (*t >= '0' && *t <= '9') || *t == '-') v->token[i++] = *t;
	v->token[i] = 0;
	if (v->mapped) shell_token_line(v, NULL);
	return true;
}

/* ── roles: the shell's own windows when it runs natively on Wayland (no X11 window types there) ──
 *   role <desktop|dock|notification|normal> <pid> <title> <strut left> <right> <top> <bottom>
 * The first native window of that process with that title gets the role (before or after it appears). */
struct shell_role { bool used, bare; enum kind kind; int pid; char title[256]; uint32_t strut[4]; };
#define MAX_ROLES 8
static struct shell_role roles[MAX_ROLES];

static int view_pid(struct view *v)
{
	pid_t pid = 0;
	if (!v->x11 && v->xdg && v->xdg->base->resource)
		wl_client_get_credentials(wl_resource_get_client(v->xdg->base->resource), &pid, NULL, NULL);
	return (int)pid;
}

static void role_apply(struct view *v, struct shell_role *r)
{
	struct server *s = v->server;
	bool changed = v->kind != r->kind;
	/* the desktop, the taskbar and notifications are never full-screen or maximised; the shell's other frameless
	 * windows keep what they asked for (its lock screen is full-screen: the role can arrive after it mapped) */
	bool keeps_state = r->kind == K_NORMAL || r->kind == K_UTILITY;
	bool wants_full = keeps_state && (v->fullscreen || (!v->x11 && v->xdg && v->xdg->requested.fullscreen));
	memcpy(v->strut, r->strut, sizeof v->strut);
	if (r->bare && !v->bare) {
		/* no frame and not on the taskbar: the shell's switcher and other chrome of its own */
		v->bare = true;
		v->decorated = false;
		if (v->mapped && v->tree) { view_apply_geometry(v); view_update_decorations(v); }
	}
	if (changed) {
		shell_gone(v);
		v->kind = r->kind;
		v->decorated = r->kind == K_NORMAL ? v->decorated : false;
		v->title_w = -1;
		if (!keeps_state) v->maximized = v->fullscreen = false;
		if (r->kind == K_DOCK && r->strut[3]) {      /* a bottom panel: along the bottom of the screen */
			struct wlr_box ob = output_box_for(v);
			v->x = ob.x; v->w = ob.width; v->h = (int)r->strut[3]; v->y = ob.y + ob.height - v->h;
		}
		if (v->mapped && v->tree) {
			wlr_scene_node_reparent(&v->tree->node, layer_for(v));
			if (v->kind == K_DESKTOP) { view_place(v, false); wlr_scene_node_lower_to_bottom(&v->tree->node); }
			view_apply_geometry(v);
		}
	}
	if (wants_full && v->mapped && !v->fullscreen) view_set_fullscreen(v, true);
	if (v->mapped) update_workarea(s);
}

/* gives the view its role if the shell asked for one; true when it has one */
static bool roles_check(struct view *v)
{
	if (v->x11 || !v->xdg || !v->xdg->title) return false;
	int pid = view_pid(v);
	for (int i = 0; i < MAX_ROLES; i++) {
		struct shell_role *r = &roles[i];
		if (!r->used || r->pid != pid || strcmp(r->title, v->xdg->title) != 0) continue;
		role_apply(v, r);
		return r->kind != K_NORMAL;
	}
	return false;
}

static void role_command(struct server *s, char *arg)
{
	char *f[8] = {0};
	int n = 0;
	for (char *t = arg; t && n < 8; n++) {
		f[n] = t;
		t = strchr(t, '\t');
		if (t) *t++ = 0;
	}
	if (n < 3) return;
	bool bare = strcmp(f[0], "bare") == 0;
	enum kind k = strcmp(f[0], "desktop") == 0 ? K_DESKTOP : strcmp(f[0], "dock") == 0 ? K_DOCK
		: strcmp(f[0], "notification") == 0 ? K_NOTIFY : bare ? K_UTILITY : K_NORMAL;
	int pid = atoi(f[1]);
	struct shell_role *r = NULL;
	for (int i = 0; i < MAX_ROLES && !r; i++)
		if (roles[i].used && roles[i].pid == pid && strcmp(roles[i].title, f[2]) == 0) r = &roles[i];
	for (int i = 0; i < MAX_ROLES && !r; i++) if (!roles[i].used) r = &roles[i];
	if (!r) return;
	r->used = true;
	r->bare = bare;
	r->kind = k;
	r->pid = pid;
	snprintf(r->title, sizeof r->title, "%s", f[2]);
	for (int i = 0; i < 4; i++) r->strut[i] = f[3 + i] ? (uint32_t)strtoul(f[3 + i], NULL, 10) : 0;
	struct view *v;
	wl_list_for_each(v, &s->views, link) if (v->mapped && !v->x11) roles_check(v);
}

/* ── global shortcuts for the shell: the same keys and rules as its X11 key grabs (Services/X11Desktop.cs) ── */
static bool swallowed(struct server *s, uint32_t kc) { return kc < 1024 && (s->swallowed[kc / 8] & (1u << (kc % 8))); }
static void set_swallowed(struct server *s, uint32_t kc, bool on)
{
	if (kc >= 1024) return;
	if (on) s->swallowed[kc / 8] |= (uint8_t)(1u << (kc % 8));
	else s->swallowed[kc / 8] &= (uint8_t)~(1u << (kc % 8));
}

static void shell_key(struct server *s, const char *name) { ipc_send(s, NULL, "key\t%s", name); }

/* true when the key belongs to the shell (clients don't see it) */
static bool shell_shortcut(struct server *s, xkb_keysym_t sym, bool pressed, uint32_t mods)
{
	if (!s->shell_shortcuts || wl_list_empty(&s->ipc_clients)) return false;
	bool alt = mods & WLR_MODIFIER_ALT, ctrl = mods & WLR_MODIFIER_CTRL, shift = mods & WLR_MODIFIER_SHIFT;
	xkb_keysym_t lower = xkb_keysym_to_lower(sym);

	if (s->switching) {                     /* Alt+Tab switcher open: every key is the switcher's */
		if (!pressed && (sym == XKB_KEY_Alt_L || sym == XKB_KEY_Alt_R || sym == XKB_KEY_Meta_L)) {
			s->switching = false;
			shell_key(s, "switcher-commit");
		} else if (pressed && sym == XKB_KEY_Escape) {
			s->switching = false;
			shell_key(s, "switcher-cancel");
		} else if (pressed && (sym == XKB_KEY_Tab || sym == XKB_KEY_ISO_Left_Tab))
			shell_key(s, shift || sym == XKB_KEY_ISO_Left_Tab ? "switcher-prev" : "switcher-next");
		else if (pressed && (sym == XKB_KEY_Right || sym == XKB_KEY_Down)) shell_key(s, "switcher-next");
		else if (pressed && (sym == XKB_KEY_Left || sym == XKB_KEY_Up)) shell_key(s, "switcher-prev");
		return true;
	}
	if (pressed && alt && !ctrl && (sym == XKB_KEY_Tab || sym == XKB_KEY_ISO_Left_Tab)) {
		s->switching = true;
		shell_key(s, shift || sym == XKB_KEY_ISO_Left_Tab ? "switcher-open-prev" : "switcher-open");
		return true;
	}
	if (sym == XKB_KEY_Super_L || sym == XKB_KEY_Super_R) {
		if (pressed) { s->super_down = true; s->super_used = false; }
		else {
			if (s->super_down && !s->super_used) shell_key(s, "start");
			s->super_down = false;
		}
		return true;
	}
	if (pressed && (s->super_down || (mods & WLR_MODIFIER_LOGO))) {
		s->super_used = true;
		const char *name = NULL;
		switch (lower) {
		case XKB_KEY_Left: name = "snap-left"; break;
		case XKB_KEY_Right: name = "snap-right"; break;
		case XKB_KEY_Up: name = "maximize"; break;
		case XKB_KEY_Down: name = "minimize"; break;
		case XKB_KEY_v: name = "clipboard"; break;
		case XKB_KEY_d: name = "desktop"; break;
		case XKB_KEY_e: name = "files"; break;
		case XKB_KEY_l: name = "lock"; break;
		case XKB_KEY_t: name = "terminal"; break;
		case XKB_KEY_s: name = alt ? "screen-reader" : "search"; break;        /* Super+Alt+S: Orca, as on GNOME */
		case XKB_KEY_space: name = "search"; break;
		default: break;
		}
		if (name) shell_key(s, name);
		return true;                         /* like the X11 keyboard grab: Super+anything stays with the shell */
	}
	if (pressed && ctrl && alt && lower == XKB_KEY_t) { shell_key(s, "terminal"); return true; }
	/* Ctrl+Alt+Delete: the shell's Lock / Log off / Restart / Shut down / Task Manager menu */
	if (ctrl && alt && (sym == XKB_KEY_Delete || sym == XKB_KEY_KP_Delete)) {
		if (pressed) shell_key(s, "power-menu");
		return true;
	}
	/* the keyboard's volume keys: the shell changes the volume and shows the level */
	if (sym == XKB_KEY_XF86AudioRaiseVolume || sym == XKB_KEY_XF86AudioLowerVolume || sym == XKB_KEY_XF86AudioMute) {
		if (pressed) shell_key(s, sym == XKB_KEY_XF86AudioRaiseVolume ? "volume-up"
			: sym == XKB_KEY_XF86AudioLowerVolume ? "volume-down" : "volume-mute");
		return true;
	}
	if (sym == XKB_KEY_Print) {                 /* Print: screenshot (Shift+Print: just the active window) */
		if (!pressed) return true;
		struct view *f = s->focused;
		if (shift && f && f->mapped && f->kind != K_DESKTOP) {
			int side = deco_side(f), top = deco_top(f);
			ipc_send(s, NULL, "key\tscreenshot-window\t%d,%d %dx%d", f->x - side, f->y - top, f->w + 2 * side, f->h + top + side);
		} else shell_key(s, "screenshot");
		return true;
	}
	return false;
}

/* ───────────────────────── remembered window positions ─────────────────────────
 * Other programs' windows open where they were last closed: ~/.config/redstaros/window-positions, one line per
 * app ("app-id x y width height"). Red Star's own windows remember theirs in its settings. */
#define MAX_POSITIONS 200
static struct { char key[96]; struct wlr_box box; } positions[MAX_POSITIONS];
static int n_positions;
static char positions_file[512];

static const char *view_key(struct view *v)
{
	const char *k = v->x11 ? (v->xs ? v->xs->class : NULL) : (v->xdg ? v->xdg->app_id : NULL);
	if (!k || !*k || strchr(k, ' ') || strchr(k, '\n')) return NULL;
	if (strcasecmp(k, "RedStarOS") == 0 || strcasecmp(k, "redstaros") == 0) return NULL;
	return k;
}

static void positions_load(void)
{
	const char *cfg = getenv("XDG_CONFIG_HOME"), *home = getenv("HOME");
	if (cfg && *cfg) snprintf(positions_file, sizeof positions_file, "%s/redstaros/window-positions", cfg);
	else if (home) snprintf(positions_file, sizeof positions_file, "%s/.config/redstaros/window-positions", home);
	else return;
	FILE *f = fopen(positions_file, "r");
	if (!f) return;
	char key[96];
	int x, y, w, h;
	while (n_positions < MAX_POSITIONS && fscanf(f, "%95s %d %d %d %d", key, &x, &y, &w, &h) == 5) {
		snprintf(positions[n_positions].key, sizeof positions[n_positions].key, "%s", key);
		positions[n_positions].box = (struct wlr_box){ x, y, w, h };
		n_positions++;
	}
	fclose(f);
}

static bool positions_get(struct view *v, struct wlr_box *box)
{
	const char *k = view_key(v);
	if (!k) return false;
	for (int i = 0; i < n_positions; i++) {
		if (strcmp(positions[i].key, k) != 0) continue;
		struct wlr_box b = positions[i].box;
		/* only when the title bar lands on a screen that's still there */
		if (b.width < 100 || b.height < 60 || !wlr_output_layout_contains_point(v->server->layout, NULL,
				b.x + b.width / 2, b.y - deco_top(v) / 2)) return false;
		*box = b;
		return true;
	}
	return false;
}

static void positions_put(struct view *v)
{
	if (v->kind != K_NORMAL || !v->decorated || v->maximized || v->fullscreen || v->snapped || !positions_file[0]) return;
	const char *k = view_key(v);
	if (!k || v->w < 100 || v->h < 60) return;
	int i;
	for (i = 0; i < n_positions; i++) if (strcmp(positions[i].key, k) == 0) break;
	if (i == n_positions) {
		if (n_positions == MAX_POSITIONS) {      /* full: forget the oldest */
			memmove(&positions[0], &positions[1], sizeof positions[0] * (MAX_POSITIONS - 1));
			i = MAX_POSITIONS - 1;
		} else n_positions++;
		snprintf(positions[i].key, sizeof positions[i].key, "%s", k);
	}
	positions[i].box = (struct wlr_box){ v->x, v->y, v->w, v->h };
	char tmp[520];
	snprintf(tmp, sizeof tmp, "%s", positions_file);
	char *slash = strrchr(tmp, '/');
	if (slash) { *slash = 0; mkdir(tmp, 0755); }
	snprintf(tmp, sizeof tmp, "%s.new", positions_file);
	FILE *f = fopen(tmp, "w");
	if (!f) return;
	for (int j = 0; j < n_positions; j++)
		fprintf(f, "%s %d %d %d %d\n", positions[j].key, positions[j].box.x, positions[j].box.y,
			positions[j].box.width, positions[j].box.height);
	if (fclose(f) == 0) rename(tmp, positions_file);
}

/* ───────────────────────── fades ─────────────────────────
 * A new window fades in over 150 ms (Settings → Accessibility → Reduce motion turns it off). */
static bool reduce_motion;
#define FADE_MS 150

static void set_opacity(struct wlr_scene_buffer *b, int sx, int sy, void *data)
{
	(void)sx; (void)sy;
	wlr_scene_buffer_set_opacity(b, *(float *)data);
}

static int fade_tick(void *data)
{
	struct server *s = data;
	struct timespec now;
	clock_gettime(CLOCK_MONOTONIC, &now);
	bool more = false;
	struct view *v;
	wl_list_for_each(v, &s->views, link) {
		if (!v->fading || !v->tree) continue;
		double ms = (now.tv_sec - v->fade_start.tv_sec) * 1000.0 + (now.tv_nsec - v->fade_start.tv_nsec) / 1e6;
		float a = ms >= FADE_MS ? 1.0f : (float)(1.0 - pow(1.0 - ms / FADE_MS, 3));   /* ease out */
		wlr_scene_node_for_each_buffer(&v->tree->node, set_opacity, &a);
		if (ms >= FADE_MS) v->fading = false; else more = true;
	}
	if (more) wl_event_source_timer_update(s->fade_timer, 16);
	return 0;
}

static void fade_in(struct view *v)
{
	struct server *s = v->server;
	if (reduce_motion || !v->tree || !s->fade_timer) return;
	v->fading = true;
	clock_gettime(CLOCK_MONOTONIC, &v->fade_start);
	float zero = 0.0f;
	wlr_scene_node_for_each_buffer(&v->tree->node, set_opacity, &zero);
	wl_event_source_timer_update(s->fade_timer, 16);
}

/* ───────────────────────── input ───────────────────────── */

static void keyboard_modifiers(struct wl_listener *l, void *data)
{
	(void)data;
	struct keyboard *k = wl_container_of(l, k, modifiers);
	wlr_seat_set_keyboard(k->server->seat, k->wlr);
	wlr_seat_keyboard_notify_modifiers(k->server->seat, &k->wlr->modifiers);
}

static bool handle_keybinding(struct server *s, xkb_keysym_t sym, uint32_t mods)
{
	/* console switching on the hardware: Ctrl+Alt+F1…F12 */
	if (sym >= XKB_KEY_XF86Switch_VT_1 && sym <= XKB_KEY_XF86Switch_VT_12) {
		if (s->session) wlr_session_change_vt(s->session, sym - XKB_KEY_XF86Switch_VT_1 + 1);
		return true;
	}
	if ((mods & WLR_MODIFIER_CTRL) && (mods & WLR_MODIFIER_ALT) && sym == XKB_KEY_BackSpace) {
		wl_display_terminate(s->display);
		return true;
	}
	if ((mods & WLR_MODIFIER_ALT) && !(mods & (WLR_MODIFIER_CTRL | WLR_MODIFIER_LOGO)) && sym == XKB_KEY_F4) {
		if (s->focused && s->focused->kind != K_DESKTOP) view_close(s->focused);
		return true;
	}
	return false;
}

static void keyboard_key(struct wl_listener *l, void *data)
{
	struct keyboard *k = wl_container_of(l, k, key);
	struct server *s = k->server;
	struct wlr_keyboard_key_event *e = data;
	uint32_t keycode = e->keycode + 8;
	input_activity(s);
	const xkb_keysym_t *syms;
	int n = xkb_state_key_get_syms(k->wlr->xkb_state, keycode, &syms);
	bool handled = false, pressed = e->state == WL_KEYBOARD_KEY_STATE_PRESSED;
	uint32_t mods = wlr_keyboard_get_modifiers(k->wlr);
	if (pressed)
		for (int i = 0; i < n && !handled; i++) handled = handle_keybinding(s, syms[i], mods);
	if (!handled) {
		/* the shell's shortcuts go by the unshifted key (Super+Shift+Left is still "Left") */
		const xkb_keysym_t *base;
		xkb_layout_index_t layout = xkb_state_key_get_layout(k->wlr->xkb_state, keycode);
		int nb = xkb_keymap_key_get_syms_by_level(k->wlr->keymap, keycode, layout == XKB_LAYOUT_INVALID ? 0 : layout, 0, &base);
		xkb_keysym_t sym = nb > 0 ? base[0] : (n > 0 ? syms[0] : XKB_KEY_NoSymbol);
		handled = shell_shortcut(s, sym, pressed, mods);
	}
	/* a key whose press a client never saw doesn't get its release either (and the other way round) */
	if (pressed) set_swallowed(s, keycode, handled);
	else { handled = swallowed(s, keycode); set_swallowed(s, keycode, false); }
	if (!handled) {
		wlr_seat_set_keyboard(s->seat, k->wlr);
		wlr_seat_keyboard_notify_key(s->seat, e->time_msec, e->keycode, e->state);
	}
}

static void keyboard_destroy(struct wl_listener *l, void *data)
{
	(void)data;
	struct keyboard *k = wl_container_of(l, k, destroy);
	wl_list_remove(&k->modifiers.link);
	wl_list_remove(&k->key.link);
	wl_list_remove(&k->destroy.link);
	wl_list_remove(&k->link);
	struct server *srv = k->server;
	free(k);
	update_capabilities(srv);
}

/* own_keymap: a virtual keyboard brings its own key map (the protocol requires it) */
/* the keyboard layouts in use: "us,de" with variants ",nodeadkeys" and options such as "grp:alt_shift_toggle" */
static struct xkb_keymap *server_keymap(struct server *s)
{
	struct xkb_context *ctx = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
	struct xkb_rule_names names = { .layout = s->kb_layout, .variant = s->kb_variant, .options = s->kb_options };
	struct xkb_keymap *km = xkb_keymap_new_from_names(ctx, &names, XKB_KEYMAP_COMPILE_NO_FLAGS);
	xkb_context_unref(ctx);
	return km;
}

static void keymap_set(struct server *s, const char *layout, const char *variant, const char *options)
{
	char *ol = s->kb_layout, *ov = s->kb_variant, *oo = s->kb_options;
	s->kb_layout = layout && *layout ? strdup(layout) : NULL;
	s->kb_variant = variant && *variant ? strdup(variant) : NULL;
	s->kb_options = options && *options ? strdup(options) : NULL;
	struct xkb_keymap *km = server_keymap(s);
	if (!km) {
		wlr_log(WLR_ERROR, "keyboard layout %s (%s) couldn't be made; keeping the old one", layout ? layout : "", variant ? variant : "");
		free(s->kb_layout); free(s->kb_variant); free(s->kb_options);
		s->kb_layout = ol; s->kb_variant = ov; s->kb_options = oo;
		return;
	}
	free(ol); free(ov); free(oo);
	struct keyboard *k;
	wl_list_for_each(k, &s->keyboards, link) if (!k->own_keymap) wlr_keyboard_set_keymap(k->wlr, km);
	xkb_keymap_unref(km);
	wlr_log(WLR_INFO, "keyboard layout %s", layout && *layout ? layout : "(default)");
}

static void add_keyboard(struct server *s, struct wlr_input_device *dev, bool own_keymap)
{
	struct wlr_keyboard *wk = wlr_keyboard_from_input_device(dev);
	struct keyboard *k = calloc(1, sizeof *k);
	if (!k) return;
	k->server = s;
	k->wlr = wk;
	k->own_keymap = own_keymap;
	if (!own_keymap) {
		/* the layout chosen in Settings, else XKB_DEFAULT_LAYOUT etc. (or the system default) */
		struct xkb_keymap *km = server_keymap(s);
		if (!km) {
			char *ol = s->kb_layout, *ov = s->kb_variant, *oo = s->kb_options;
			s->kb_layout = s->kb_variant = s->kb_options = NULL;
			km = server_keymap(s);
			s->kb_layout = ol; s->kb_variant = ov; s->kb_options = oo;
		}
		if (km) { wlr_keyboard_set_keymap(wk, km); xkb_keymap_unref(km); }
		wlr_keyboard_set_repeat_info(wk, 25, 600);
	}
	k->modifiers.notify = keyboard_modifiers;
	wl_signal_add(&wk->events.modifiers, &k->modifiers);
	k->key.notify = keyboard_key;
	wl_signal_add(&wk->events.key, &k->key);
	k->destroy.notify = keyboard_destroy;
	wl_signal_add(&dev->events.destroy, &k->destroy);
	wlr_seat_set_keyboard(s->seat, wk);
	wl_list_insert(&s->keyboards, &k->link);
	update_capabilities(s);
}

static void new_keyboard(struct server *s, struct wlr_input_device *dev) { add_keyboard(s, dev, false); }

static void update_capabilities(struct server *s)
{
	uint32_t caps = WL_SEAT_CAPABILITY_POINTER;
	if (!wl_list_empty(&s->keyboards)) caps |= WL_SEAT_CAPABILITY_KEYBOARD;
	wlr_seat_set_capabilities(s->seat, caps);
}

/* Virtual input (wtype, ydotool-style tools, and the headless tests, which press Alt+Tab and click outside menus):
 * a keyboard or pointer a client creates, handled exactly like a real one. */
static void new_virtual_keyboard(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, new_virtual_keyboard);
	struct wlr_virtual_keyboard_v1 *vk = data;
	add_keyboard(s, &vk->keyboard.base, true);
}

static void new_virtual_pointer(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, new_virtual_pointer);
	struct wlr_virtual_pointer_v1_new_pointer_event *e = data;
	struct wlr_input_device *dev = &e->new_pointer->pointer.base;
	wlr_cursor_attach_input_device(s->cursor, dev);
	if (e->suggested_output) wlr_cursor_map_input_to_output(s->cursor, dev, e->suggested_output);
}

static void new_input(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, new_input);
	struct wlr_input_device *dev = data;
	switch (dev->type) {
	case WLR_INPUT_DEVICE_KEYBOARD: new_keyboard(s, dev); break;
	case WLR_INPUT_DEVICE_POINTER: wlr_cursor_attach_input_device(s->cursor, dev); break;
	default: break;
	}
	update_capabilities(s);
}

static const char *edge_cursor(uint32_t e)
{
	if (e == (WLR_EDGE_TOP | WLR_EDGE_LEFT)) return "nw-resize";
	if (e == (WLR_EDGE_TOP | WLR_EDGE_RIGHT)) return "ne-resize";
	if (e == (WLR_EDGE_BOTTOM | WLR_EDGE_LEFT)) return "sw-resize";
	if (e == (WLR_EDGE_BOTTOM | WLR_EDGE_RIGHT)) return "se-resize";
	if (e & WLR_EDGE_TOP) return "n-resize";
	if (e & WLR_EDGE_BOTTOM) return "s-resize";
	if (e & WLR_EDGE_LEFT) return "w-resize";
	if (e & WLR_EDGE_RIGHT) return "e-resize";
	return "default";
}

static void set_hover(struct server *s, struct view *v, int part)
{
	int btn = (part == P_MIN || part == P_MAX || part == P_CLOSE) ? part : P_NONE;
	struct view *hv = s->hover_view;
	if (hv && hv != v && hv->mapped) { hv->hover = P_NONE; view_update_decorations(hv); }
	s->hover_view = v;
	if (v && v->hover != btn) { v->hover = btn; view_update_decorations(v); }
}

/* REDSTAR_COMP_TRACE_POINTER=1: every change of the surface the pointer is over, on stderr (for chasing hover bugs) */
static bool trace_pointer;

static void trace_focus(struct server *s, struct wlr_surface *before, struct view *v, int part)
{
	struct wlr_surface *now = s->seat->pointer_state.focused_surface;
	if (!trace_pointer || now == before) return;
	const char *what = !v ? "-" : v->unmanaged ? "x11-unmanaged" : v->xdg && v->xdg->app_id ? v->xdg->app_id : v->title;
	fprintf(stderr, "pointer\t%.1f,%.1f\t%p -> %p\tview %s\tpart %d\n", s->cursor->x, s->cursor->y, (void *)before, (void *)now,
		what ? what : "?", part);
}

static void process_motion(struct server *s, uint32_t time)
{
	if (s->grab == G_MOVE || s->grab == G_RESIZE) {
		grab_motion(s);
		return;
	}
	double sx, sy;
	struct wlr_surface *surface = NULL;
	int part;
	uint32_t edges;
	struct view *v = view_at(s, s->cursor->x, s->cursor->y, &surface, &sx, &sy, &part, &edges);
	if (s->grab != G_BUTTON) set_hover(s, v, part);
	struct wlr_surface *before = s->seat->pointer_state.focused_surface;
	if (!surface) {
		set_cursor(s, part == P_EDGE ? edge_cursor(edges) : "default");
		wlr_seat_pointer_clear_focus(s->seat);
		trace_focus(s, before, v, part);
		return;
	}
	s->cursor_name = NULL;                   /* the client sets its own cursor */
	wlr_seat_pointer_notify_enter(s->seat, surface, sx, sy);
	wlr_seat_pointer_notify_motion(s->seat, time, sx, sy);
	trace_focus(s, before, v, part);
}

static void cursor_motion(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, cursor_motion);
	struct wlr_pointer_motion_event *e = data;
	wlr_cursor_move(s->cursor, &e->pointer->base, e->delta_x, e->delta_y);
	input_activity(s);
	process_motion(s, e->time_msec);
}

static void cursor_motion_abs(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, cursor_motion_abs);
	struct wlr_pointer_motion_absolute_event *e = data;
	wlr_cursor_warp_absolute(s->cursor, &e->pointer->base, e->x, e->y);
	input_activity(s);
	process_motion(s, e->time_msec);
}

/* A click outside a menu closes it (popup_done), like on X11. wlroots only does this for clicks in another app;
 * clicks elsewhere in the same app are left to the app, and Avalonia (Red Star running natively) leaves its menus
 * open, so every right-click added another. A submenu and the menus it opened from stay open. */
static void dismiss_popups(struct server *s, struct wlr_surface *clicked)
{
	if (wl_list_empty(&s->popups)) return;
	struct wlr_surface *chain[32];
	int n = 0;
	for (struct wlr_surface *cur = clicked ? wlr_surface_get_root_surface(clicked) : NULL; cur && n < 32;) {
		chain[n++] = cur;
		struct wlr_xdg_popup *pp = wlr_xdg_popup_try_from_wlr_surface(cur);
		cur = pp && pp->parent ? wlr_surface_get_root_surface(pp->parent) : NULL;
	}
	/* the outermost menus not on the clicked chain (closing one closes the menus opened from it) */
	struct wlr_xdg_popup *close_these[32];
	int m = 0;
	struct popup *p;
	wl_list_for_each(p, &s->popups, link) {
		struct wlr_surface *ps = p->popup->base->surface;
		bool keep = false;
		for (int i = 0; i < n && !keep; i++) keep = chain[i] == ps;
		if (keep) continue;
		struct wlr_xdg_popup *parent = p->popup->parent ? wlr_xdg_popup_try_from_wlr_surface(p->popup->parent) : NULL;
		if (parent) {
			bool parent_kept = false;
			for (int i = 0; i < n && !parent_kept; i++) parent_kept = chain[i] == parent->base->surface;
			if (!parent_kept) continue;          /* goes with its parent */
		}
		if (m < 32) close_these[m++] = p->popup;
	}
	for (int i = 0; i < m; i++) {
		bool alive = false;                  /* closing an earlier one may have taken this one with it */
		wl_list_for_each(p, &s->popups, link) if (p->popup == close_these[i]) { alive = true; break; }
		if (alive) wlr_xdg_popup_destroy(close_these[i]);
	}
}

static void cursor_button(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, cursor_button);
	struct wlr_pointer_button_event *e = data;
	input_activity(s);
	uint32_t state = e->state;
	bool pressed = state == WL_POINTER_BUTTON_STATE_PRESSED;

	if (!pressed) {
		if (s->grab == G_BUTTON) {
			struct view *v = s->grab_view;
			double sx, sy; struct wlr_surface *surf; int part; uint32_t edges;
			struct view *under = view_at(s, s->cursor->x, s->cursor->y, &surf, &sx, &sy, &part, &edges);
			if (v && under == v && part == s->grab_button_part) {
				if (part == P_CLOSE) view_close(v);
				else if (part == P_MAX) view_set_maximized(v, !v->maximized);
				else if (part == P_MIN) view_set_minimized(v, true);
			}
			s->grab = G_NONE;
			s->grab_view = NULL;
			return;
		}
		if (s->grab == G_MOVE || s->grab == G_RESIZE) {
			end_grab(s);
			return;
		}
		wlr_seat_pointer_notify_button(s->seat, e->time_msec, e->button, state);
		return;
	}

	double sx, sy;
	struct wlr_surface *surface = NULL;
	int part;
	uint32_t edges;
	struct view *v = view_at(s, s->cursor->x, s->cursor->y, &surface, &sx, &sy, &part, &edges);
	dismiss_popups(s, surface);
	if (v && !v->unmanaged) focus_view(v);
	else if (v && v->unmanaged && wlr_xwayland_surface_override_redirect_wants_focus(v->xs)) focus_view(v);

	struct wlr_keyboard *kb = wlr_seat_get_keyboard(s->seat);
	uint32_t mods = kb ? wlr_keyboard_get_modifiers(kb) : 0;
	if (v && !v->unmanaged && (mods & WLR_MODIFIER_ALT) && v->kind != K_DESKTOP &&
		(e->button == BTN_LEFT || e->button == BTN_RIGHT)) {
		/* Alt+drag moves, Alt+right-drag resizes (from the nearest corner) */
		if (e->button == BTN_LEFT) begin_grab(v, G_MOVE, 0);
		else {
			uint32_t ed = 0;
			ed |= s->cursor->x < v->x + v->w / 2.0 ? WLR_EDGE_LEFT : WLR_EDGE_RIGHT;
			ed |= s->cursor->y < v->y + v->h / 2.0 ? WLR_EDGE_TOP : WLR_EDGE_BOTTOM;
			begin_grab(v, G_RESIZE, ed);
		}
		return;
	}
	if (!v || part == P_CLIENT || surface) {
		wlr_seat_pointer_notify_button(s->seat, e->time_msec, e->button, state);
		return;
	}
	if (e->button != BTN_LEFT) return;
	switch (part) {
	case P_MIN:
	case P_MAX:
	case P_CLOSE:
		s->grab = G_BUTTON;
		s->grab_view = v;
		s->grab_button_part = part;
		break;
	case P_EDGE:
		begin_grab(v, G_RESIZE, edges);
		break;
	case P_TITLE:
		if (s->last_click_view == v && e->time_msec - s->last_click_time < 400) {
			s->last_click_view = NULL;
			view_set_maximized(v, !v->maximized);
			break;
		}
		s->last_click_view = v;
		s->last_click_time = e->time_msec;
		begin_grab(v, G_MOVE, 0);
		break;
	default:
		break;
	}
}

static void cursor_axis(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, cursor_axis);
	struct wlr_pointer_axis_event *e = data;
	input_activity(s);
	wlr_seat_pointer_notify_axis(s->seat, e->time_msec, e->orientation, e->delta, e->delta_discrete, e->source,
		e->relative_direction);
}

static void cursor_frame(struct wl_listener *l, void *data)
{
	(void)data;
	struct server *s = wl_container_of(l, s, cursor_frame);
	wlr_seat_pointer_notify_frame(s->seat);
}

static void request_cursor(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, request_cursor);
	struct wlr_seat_pointer_request_set_cursor_event *e = data;
	if (s->seat->pointer_state.focused_client == e->seat_client)
		wlr_cursor_set_surface(s->cursor, e->surface, e->hotspot_x, e->hotspot_y);
}

/* cursor-shape-v1: programs (GTK 4, Qt 6, Chromium) name the cursor they want and redstar-comp draws it from its own
 * theme, at its own size (XCURSOR_SIZE; 48 with Settings → Accessibility → Large mouse pointer): every program's
 * pointer matches instead of each drawing its own at whatever size its toolkit picked */
static void request_shape(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, request_shape);
	struct wlr_cursor_shape_manager_v1_request_set_shape_event *e = data;
	if (e->device_type != WLR_CURSOR_SHAPE_MANAGER_V1_DEVICE_TYPE_POINTER) return;
	if (s->seat->pointer_state.focused_client != e->seat_client) return;
	s->cursor_name = NULL;                   /* a client's choice: the next frame-drawn cursor is set again */
	wlr_cursor_set_xcursor(s->cursor, s->xcursor, wlr_cursor_shape_v1_name(e->shape));
}

/* "cursor\tTHEME\tSIZE" (System Settings → Appearance): the pointer's theme and size from now on, for redstar-comp's
 * own pointer, the programs that let it draw theirs (cursor-shape-v1), X11 programs' default pointer and programs
 * started from here. An empty theme is the default one. */
static void cursor_theme_set(struct server *s, const char *theme, int size)
{
	if (size < 12 || size > 256) size = 24;
	struct wlr_xcursor_manager *m = wlr_xcursor_manager_create(theme && *theme ? theme : NULL, size);
	if (!m || !wlr_xcursor_manager_load(m, 1)) {
		wlr_log(WLR_ERROR, "cursor theme %s (%d) couldn't be loaded", theme && *theme ? theme : "(default)", size);
		if (m) wlr_xcursor_manager_destroy(m);
		return;
	}
	struct wlr_xcursor_manager *old = s->xcursor;
	s->xcursor = m;
	const char *name = s->cursor_name ? s->cursor_name : "default";
	s->cursor_name = NULL;
	set_cursor(s, name);                     /* the cursor stops pointing into the old manager before it goes */
	wlr_xcursor_manager_destroy(old);
	if (s->xwayland) {
		struct wlr_xcursor *xc = wlr_xcursor_manager_get_xcursor(s->xcursor, "default", 1);
		if (xc) {
			struct wlr_xcursor_image *img = xc->images[0];
			wlr_xwayland_set_cursor(s->xwayland, wlr_xcursor_image_get_buffer(img), img->hotspot_x, img->hotspot_y);
		}
	}
	if (theme && *theme) setenv("XCURSOR_THEME", theme, 1); else unsetenv("XCURSOR_THEME");
	char sz[16];
	snprintf(sz, sizeof sz, "%d", size);
	setenv("XCURSOR_SIZE", sz, 1);
	wlr_log(WLR_INFO, "cursor theme %s, size %d", theme && *theme ? theme : "(default)", size);
}

// ── idle ──

static int64_t mono_ms(void)
{
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	return (int64_t)t.tv_sec * 1000 + t.tv_nsec / 1000000;
}

/* Screens off (DPMS) and on again, without touching the layout: windows stay where they are. */
static void dpms_set(struct server *s, bool on)
{
	if (s->dpms_off == !on) return;
	struct output *o;
	wl_list_for_each(o, &s->outputs, link) {
		if (!o->in_layout) continue;
		struct wlr_output_state st;
		wlr_output_state_init(&st);
		wlr_output_state_set_enabled(&st, on);
		if (!wlr_output_commit_state(o->wlr, &st)) wlr_log(WLR_ERROR, "screen %s couldn't be turned %s", o->wlr->name, on ? "on" : "off");
		wlr_output_state_finish(&st);
		if (on) wlr_output_schedule_frame(o->wlr);
	}
	s->dpms_off = !on;
	wlr_log(WLR_INFO, "screens %s", on ? "on" : "off");
}

static int idle_fire(void *data)
{
	struct server *s = data;
	if (s->idle_timeout <= 0 || s->idle) return 0;
	int64_t left = s->last_input_ms + (int64_t)s->idle_timeout * 1000 - mono_ms();
	if (s->inhibitors > 0 || s->shell_inhibit) left = (int64_t)s->idle_timeout * 1000;      /* a video is playing: ask again later */
	else if (left <= 0) {
		s->idle = true;
		ipc_send(s, NULL, "idle-start");
		return 0;
	}
	wl_event_source_timer_update(s->idle_timer, (int)(left > 0 ? left : 1000));
	return 0;
}

static void idle_rearm(struct server *s)
{
	if (!s->idle_timer) return;
	wl_event_source_timer_update(s->idle_timer, s->idle_timeout > 0 ? s->idle_timeout * 1000 : 0);
}

/* any input: the screens come back, the shell hears "idle-end" once, and the countdown starts again */
static void input_activity(struct server *s)
{
	s->last_input_ms = mono_ms();
	if (s->idle_notifier) wlr_idle_notifier_v1_notify_activity(s->idle_notifier, s->seat);
	if (s->dpms_off) dpms_set(s, true);
	if (s->idle) {
		s->idle = false;
		ipc_send(s, NULL, "idle-end");
		idle_rearm(s);
	}
}

struct inhibitor { struct server *server; struct wl_listener destroy; };

static void inhibitor_destroy(struct wl_listener *l, void *data)
{
	(void)data;
	struct inhibitor *in = wl_container_of(l, in, destroy);
	struct server *s = in->server;
	s->inhibitors--;
	wlr_idle_notifier_v1_set_inhibited(s->idle_notifier, s->inhibitors > 0 || s->shell_inhibit);
	wl_list_remove(&in->destroy.link);
	free(in);
}

static void new_inhibitor(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, new_inhibitor);
	struct wlr_idle_inhibitor_v1 *inh = data;
	struct inhibitor *in = calloc(1, sizeof *in);
	if (!in) return;
	in->server = s;
	in->destroy.notify = inhibitor_destroy;
	wl_signal_add(&inh->events.destroy, &in->destroy);
	s->inhibitors++;
	wlr_idle_notifier_v1_set_inhibited(s->idle_notifier, true);
}

static void request_selection(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, request_selection);
	struct wlr_seat_request_set_selection_event *e = data;
	wlr_seat_set_selection(s->seat, e->source, e->serial);
}

static void request_primary(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, request_primary);
	struct wlr_seat_request_set_primary_selection_event *e = data;
	wlr_seat_set_primary_selection(s->seat, e->source, e->serial);
}

static void request_drag(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, request_drag);
	struct wlr_seat_request_start_drag_event *e = data;
	if (wlr_seat_validate_pointer_grab_serial(s->seat, e->origin, e->serial))
		wlr_seat_start_pointer_drag(s->seat, e->drag, e->serial);
	else
		wlr_data_source_destroy(e->drag->source);
}

static void start_drag(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, start_drag);
	struct wlr_drag *drag = data;
	if (drag->icon) wlr_scene_drag_icon_create(s->l_drag, drag->icon);
}

/* ───────────────────────── display settings ─────────────────────────
 * The same changes three ways: Red Star's Settings → Displays (the shell's connection: outputs / output-set /
 * outputs-save), tools such as wlr-randr and kanshi (wlr-output-management), and at start-up from what was saved
 * (~/.config/redstaros/outputs: "name enabled width height mHz x y scale transform", one screen per line).
 * Running nested (a window on another desktop) the host decides the size, so none of this applies there. */

struct output_conf { char name[64]; bool enabled; int w, h, mhz, x, y, transform; double scale; };
#define MAX_SAVED_OUTPUTS 16
static struct output_conf saved_outputs[MAX_SAVED_OUTPUTS];
static int n_saved_outputs;
static char outputs_file[512];

static void outputs_load(void)
{
	const char *cfg = getenv("XDG_CONFIG_HOME"), *home = getenv("HOME");
	if (cfg && *cfg) snprintf(outputs_file, sizeof outputs_file, "%s/redstaros/outputs", cfg);
	else if (home) snprintf(outputs_file, sizeof outputs_file, "%s/.config/redstaros/outputs", home);
	else return;
	FILE *f = fopen(outputs_file, "r");
	if (!f) return;
	struct output_conf c;
	int en;
	while (n_saved_outputs < MAX_SAVED_OUTPUTS &&
			fscanf(f, "%63s %d %d %d %d %d %d %lf %d", c.name, &en, &c.w, &c.h, &c.mhz, &c.x, &c.y, &c.scale, &c.transform) == 9) {
		c.enabled = en != 0;
		if (c.scale < 0.5 || c.scale > 4) c.scale = 1;
		if (c.transform < 0 || c.transform > 7) c.transform = 0;
		saved_outputs[n_saved_outputs++] = c;
	}
	fclose(f);
}

static struct output_conf *saved_output(const char *name)
{
	for (int i = 0; i < n_saved_outputs; i++)
		if (strcmp(saved_outputs[i].name, name) == 0) return &saved_outputs[i];
	return NULL;
}

static void output_scene_destroyed(struct wl_listener *l, void *data)
{
	(void)data;
	struct output *o = wl_container_of(l, o, scene_destroy);
	wl_list_remove(&o->scene_destroy.link);
	wl_list_init(&o->scene_destroy.link);
	o->scene_output = NULL;
}

/* puts the screen into the layout at (x, y) — or anywhere free when auto — drawing it from the scene */
static void output_place(struct output *o, bool automatic, int x, int y)
{
	struct server *s = o->server;
	if (!o->scene_output) {
		o->scene_output = wlr_scene_output_create(s->scene, o->wlr);
		o->scene_destroy.notify = output_scene_destroyed;
		wl_signal_add(&o->scene_output->events.destroy, &o->scene_destroy);
	}
	struct wlr_output_layout_output *lo = automatic ? wlr_output_layout_add_auto(s->layout, o->wlr)
		: wlr_output_layout_add(s->layout, o->wlr, x, y);
	if (lo && !o->in_layout) wlr_scene_output_layout_add_output(s->scene_layout, lo, o->scene_output);
	o->in_layout = lo != NULL;
}

static void output_unplace(struct output *o)
{
	if (o->in_layout) wlr_output_layout_remove(o->server->layout, o->wlr);
	o->in_layout = false;
	if (o->scene_output) wlr_scene_output_destroy(o->scene_output);   /* → output_scene_destroyed */
}

static int enabled_outputs(struct server *s, struct output *except)
{
	int n = 0;
	struct output *o;
	wl_list_for_each(o, &s->outputs, link) if (o != except && o->wlr->enabled) n++;
	return n;
}

static struct wlr_output_mode *find_mode(struct wlr_output *wo, int w, int h, int mhz)
{
	struct wlr_output_mode *m, *best = NULL;
	wl_list_for_each(m, &wo->modes, link) {
		if (m->width != w || m->height != h) continue;
		if (mhz <= 0) { if (!best || m->preferred || m->refresh > best->refresh) best = m; continue; }
		if (!best || abs(m->refresh - mhz) < abs(best->refresh - mhz)) best = m;
	}
	return best;
}

/* one screen's new settings: mode (or a custom size), scale, rotation, position, on/off */
static bool output_configure(struct output *o, bool enabled, struct wlr_output_mode *mode, int w, int h, int mhz,
	double scale, int transform, bool automatic, int x, int y, bool test_only, char *why, size_t whysize)
{
	if (!enabled && enabled_outputs(o->server, o) == 0) {
		snprintf(why, whysize, "at least one screen has to stay on");
		return false;
	}
	struct wlr_output_state st;
	wlr_output_state_init(&st);
	wlr_output_state_set_enabled(&st, enabled);
	if (enabled) {
		if (!mode && w > 0 && h > 0) mode = find_mode(o->wlr, w, h, mhz);
		if (mode && mode->refresh > 0) wlr_output_state_set_mode(&st, mode);
		else if (mode) set_custom_mode(o->wlr, &st, mode->width, mode->height, 0);
		else if (w > 0 && h > 0) set_custom_mode(o->wlr, &st, w, h, mhz);
		if (scale >= 0.5 && scale <= 4) wlr_output_state_set_scale(&st, (float)scale);
		if (transform >= 0 && transform <= 7) wlr_output_state_set_transform(&st, (enum wl_output_transform)transform);
	}
	bool ok = test_only ? wlr_output_test_state(o->wlr, &st) : wlr_output_commit_state(o->wlr, &st);
	wlr_output_state_finish(&st);
	if (!ok) { snprintf(why, whysize, "the screen didn't accept that mode"); return false; }
	if (test_only) return true;
	never_zero_hz(o->wlr);
	if (enabled) output_place(o, automatic, x, y);
	else output_unplace(o);
	return true;
}

static void output_manager_update(struct server *s)
{
	if (!s->output_mgr) return;
	struct wlr_output_configuration_v1 *config = wlr_output_configuration_v1_create();
	struct output *o;
	wl_list_for_each(o, &s->outputs, link) {
		struct wlr_output_configuration_head_v1 *head = wlr_output_configuration_head_v1_create(config, o->wlr);
		if (!head) continue;
		struct wlr_box box;
		wlr_output_layout_get_box(s->layout, o->wlr, &box);
		head->state.enabled = o->wlr->enabled && o->in_layout;
		head->state.x = box.x;
		head->state.y = box.y;
	}
	wlr_output_manager_v1_set_configuration(s->output_mgr, config);
}

static void output_manager_apply_or_test(struct server *s, struct wlr_output_configuration_v1 *config, bool test_only)
{
	bool ok = true;
	char why[128] = "";
	struct wlr_output_configuration_head_v1 *head;
	wl_list_for_each(head, &config->heads, link) {
		struct output *o = head->state.output->data;
		if (!o) { ok = false; continue; }
		ok &= output_configure(o, head->state.enabled, head->state.mode, head->state.custom_mode.width,
			head->state.custom_mode.height, head->state.custom_mode.refresh, head->state.scale, head->state.transform,
			false, head->state.x, head->state.y, test_only, why, sizeof why);
	}
	if (ok) wlr_output_configuration_v1_send_succeeded(config);
	else { wlr_log(WLR_ERROR, "display change refused: %s", why); wlr_output_configuration_v1_send_failed(config); }
	wlr_output_configuration_v1_destroy(config);
	if (!test_only) output_manager_update(s);
}

static void output_manager_apply(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, output_apply);
	output_manager_apply_or_test(s, data, false);
}

static void output_manager_test(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, output_test);
	output_manager_apply_or_test(s, data, true);
}

static void outputs_describe(struct server *s, struct ipc_client *c)
{
	struct output *o;
	wl_list_for_each_reverse(o, &s->outputs, link) {
		struct wlr_output *wo = o->wlr;
		char modes[2048] = "", desc[160];
		size_t len = 0;
		struct wlr_output_mode *m;
		wl_list_for_each(m, &wo->modes, link) {
			int n = snprintf(modes + len, sizeof modes - len, "%s%dx%d@%d%s", len ? "," : "", m->width, m->height,
				m->refresh, m->preferred ? "*" : "");
			if (n < 0 || len + (size_t)n >= sizeof modes) break;
			len += (size_t)n;
		}
		snprintf(desc, sizeof desc, "%s %s", wo->make ? wo->make : "", wo->model ? wo->model : "");
		clean(desc, sizeof desc, desc);
		struct wlr_box box = {0};
		if (o->in_layout) wlr_output_layout_get_box(s->layout, wo, &box);
		ipc_send(s, c, "output\t%s\t%s\t%d\t%dx%d@%d\t%.3f\t%d\t%d\t%d\t%d\t%s", wo->name, desc,
			wo->enabled && o->in_layout ? 1 : 0, wo->width, wo->height, wo->refresh, wo->scale, box.x, box.y,
			(int)wo->transform, nested_output(wo) ? 1 : 0, modes);
	}
	ipc_send(s, c, "outputs-end");
}

/* output-set <name> <0|1> <W>x<H>@<mHz> <x> <y> <scale> <transform> */
static void output_set_from_ipc(struct server *s, struct ipc_client *c, char *args)
{
	char name[64] = "", mode[64] = "", why[128] = "unknown screen";
	int en = 1, w = 0, h = 0, mhz = 0, x = 0, y = 0, transform = 0;
	double scale = 1;
	for (char *p = args; *p; p++) if (*p == '\t') *p = ' ';
	if (sscanf(args, "%63s %d %63s %d %d %lf %d", name, &en, mode, &x, &y, &scale, &transform) != 7 ||
			sscanf(mode, "%dx%d@%d", &w, &h, &mhz) < 2) {
		ipc_send(s, c, "output-failed\t%s\tcould not read the request", name);
		return;
	}
	struct output *o;
	wl_list_for_each(o, &s->outputs, link) {
		if (strcmp(o->wlr->name, name) != 0) continue;
		if (nested_output(o->wlr)) { snprintf(why, sizeof why, "running in a window: the other desktop sets its size"); break; }
		if (output_configure(o, en != 0, NULL, w, h, mhz, scale, transform, false, x, y, false, why, sizeof why)) {
			output_manager_update(s);
			ipc_send(s, c, "output-ok\t%s", name);
			return;
		}
		break;
	}
	ipc_send(s, c, "output-failed\t%s\t%s", name, why);
}

static void outputs_save(struct server *s)
{
	if (!outputs_file[0]) return;
	struct output *o;
	wl_list_for_each(o, &s->outputs, link) {
		if (nested_output(o->wlr)) continue;
		struct output_conf *c = saved_output(o->wlr->name);
		if (!c) {
			if (n_saved_outputs == MAX_SAVED_OUTPUTS) continue;
			c = &saved_outputs[n_saved_outputs++];
			snprintf(c->name, sizeof c->name, "%s", o->wlr->name);
		}
		struct wlr_box box = {0};
		if (o->in_layout) wlr_output_layout_get_box(s->layout, o->wlr, &box);
		c->enabled = o->wlr->enabled && o->in_layout;
		if (c->enabled) {
			c->w = o->wlr->width; c->h = o->wlr->height; c->mhz = o->wlr->refresh;
			c->x = box.x; c->y = box.y; c->scale = o->wlr->scale; c->transform = (int)o->wlr->transform;
		}
	}
	char tmp[520];
	snprintf(tmp, sizeof tmp, "%s", outputs_file);
	char *slash = strrchr(tmp, '/');
	if (slash) { *slash = 0; mkdir(tmp, 0755); }
	snprintf(tmp, sizeof tmp, "%s.new", outputs_file);
	FILE *f = fopen(tmp, "w");
	if (!f) return;
	for (int i = 0; i < n_saved_outputs; i++) {
		struct output_conf *c = &saved_outputs[i];
		fprintf(f, "%s %d %d %d %d %d %d %.3f %d\n", c->name, c->enabled ? 1 : 0, c->w, c->h, c->mhz, c->x, c->y, c->scale, c->transform);
	}
	if (fclose(f) == 0) rename(tmp, outputs_file);
}

/* ───────────────────────── outputs ───────────────────────── */

static void output_frame(struct wl_listener *l, void *data)
{
	(void)data;
	struct output *o = wl_container_of(l, o, frame);
	if (!o->wlr->enabled || !o->scene_output) return;
	/* keep drag icons under the pointer */
	wlr_scene_node_set_position(&o->server->l_drag->node, (int)o->server->cursor->x, (int)o->server->cursor->y);
	wlr_scene_output_commit(o->scene_output, NULL);
	struct timespec now;
	clock_gettime(CLOCK_MONOTONIC, &now);
	wlr_scene_output_send_frame_done(o->scene_output, &now);
}

static void output_request_state(struct wl_listener *l, void *data)
{
	struct output *o = wl_container_of(l, o, request_state);
	struct wlr_output_event_request_state *e = data;
	wlr_output_commit_state(o->wlr, e->state);         /* the nested window was resized */
}

static void output_destroy(struct wl_listener *l, void *data)
{
	(void)data;
	struct output *o = wl_container_of(l, o, destroy);
	wl_list_remove(&o->frame.link);
	wl_list_remove(&o->request_state.link);
	wl_list_remove(&o->destroy.link);
	wl_list_remove(&o->scene_destroy.link);
	wl_list_remove(&o->link);
	struct server *s = o->server;
	free(o);
	output_manager_update(s);
	if (wl_list_empty(&s->outputs)) wl_display_terminate(s->display);   /* the nested window was closed */
}

/* A custom mode at 60 Hz rather than 0, which GTK 4 divides by; but the nested backends (a window on another
 * desktop) refuse custom refresh rates, so there it stays 0. */
static void set_custom_mode(struct wlr_output *wo, struct wlr_output_state *st, int w, int h, int mhz)
{
	wlr_output_state_set_custom_mode(st, w, h, mhz > 0 ? mhz : 60000);
	if (mhz <= 0 && !wlr_output_test_state(wo, st)) wlr_output_state_set_custom_mode(st, w, h, 0);
}

/* What clients are told the refresh rate is. A window on another desktop has none (its backend refuses custom
 * rates, so wlroots says 0 Hz), and GTK 4 programs divide by it and crash: tell them 60 Hz. wl_output reads this
 * field when it sends the mode; nothing else in the nested backends uses it (they pace frames by the host). */
static void never_zero_hz(struct wlr_output *wo)
{
	if (wo->enabled && wo->refresh <= 0) wo->refresh = 60000;
}

static void new_output(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, new_output);
	struct wlr_output *wo = data;
	if (wo->non_desktop) return;
	if (!wlr_output_init_render(wo, s->allocator, s->renderer)) {
		wlr_log(WLR_ERROR, "could not set up drawing for output %s", wo->name);
		return;
	}
	struct output_conf *saved = nested_output(wo) ? NULL : saved_output(wo->name);
	struct wlr_output_state st;
	wlr_output_state_init(&st);
	wlr_output_state_set_enabled(&st, true);
	struct wlr_output_mode *mode = wlr_output_preferred_mode(wo);
	if (saved && saved->enabled && find_mode(wo, saved->w, saved->h, saved->mhz)) mode = find_mode(wo, saved->w, saved->h, saved->mhz);
	int nw, nh;
	nested_initial_size(&nw, &nh);
	/* outputs without modes (a window on another desktop, the headless test screen) say 60 Hz rather than 0:
	 * GTK 4 divides by the refresh rate */
	if (mode && mode->refresh > 0) wlr_output_state_set_mode(&st, mode);
	else if (mode) set_custom_mode(wo, &st, mode->width, mode->height, 0);
	else if (nw > 0 && nh > 0) set_custom_mode(wo, &st, nw, nh, 0);   /* our host window's size */
	else if (wo->width > 0 && wo->height > 0) set_custom_mode(wo, &st, wo->width, wo->height, 0);
	if (saved && saved->enabled) {
		wlr_output_state_set_scale(&st, (float)saved->scale);
		wlr_output_state_set_transform(&st, (enum wl_output_transform)saved->transform);
	}
	bool committed = wlr_output_commit_state(wo, &st);
	if (!committed) wlr_log(WLR_ERROR, "output %s: its first setting was refused%s", wo->name, saved ? ", trying its preferred mode" : "");
	if (!committed && saved) {
		/* what was saved doesn't work any more (another monitor on that port?): its preferred mode, unscaled */
		wlr_output_state_finish(&st);
		wlr_output_state_init(&st);
		wlr_output_state_set_enabled(&st, true);
		if (wlr_output_preferred_mode(wo)) wlr_output_state_set_mode(&st, wlr_output_preferred_mode(wo));
		wlr_output_commit_state(wo, &st);
		saved = NULL;
	}
	wlr_output_state_finish(&st);
	never_zero_hz(wo);

	struct output *o = calloc(1, sizeof *o);
	if (!o) return;
	o->server = s;
	o->wlr = wo;
	wo->data = o;
	o->frame.notify = output_frame;
	wl_signal_add(&wo->events.frame, &o->frame);
	o->request_state.notify = output_request_state;
	wl_signal_add(&wo->events.request_state, &o->request_state);
	o->destroy.notify = output_destroy;
	wl_signal_add(&wo->events.destroy, &o->destroy);
	wl_list_insert(&s->outputs, &o->link);

	wl_list_init(&o->scene_destroy.link);
	output_place(o, !saved || !saved->enabled, saved ? saved->x : 0, saved ? saved->y : 0);
	if (saved && !saved->enabled && enabled_outputs(s, o) > 0) {
		char why[64];
		output_configure(o, false, NULL, 0, 0, 0, 1, 0, true, 0, 0, false, why, sizeof why);   /* it was turned off */
	}
	output_manager_update(s);
	nested_x11_output(s->loop, wo, host_display, want_fullscreen);     /* no-op unless nested on X11 */
}

static void host_closed(void *data)
{
	struct server *s = data;
	wl_display_terminate(s->display);
}

static void layout_change(struct wl_listener *l, void *data)
{
	(void)data;
	struct server *s = wl_container_of(l, s, layout_change);
	struct wlr_box all;
	wlr_output_layout_get_box(s->layout, NULL, &all);
	if (s->background) {
		wlr_scene_node_set_position(&s->background->node, all.x, all.y);
		wlr_scene_rect_set_size(s->background, all.width, all.height);
	}
	output_manager_update(s);
	memset(&s->workarea, 0, sizeof s->workarea);      /* force a refit */
	update_workarea(s);
	struct view *v;
	wl_list_for_each(v, &s->views, link) {
		if (!v->mapped || v->unmanaged) continue;
		if (v->fullscreen) {
			struct wlr_box ob = output_box_for(v);
			v->x = ob.x; v->y = ob.y; v->w = ob.width; v->h = ob.height;
			view_apply_geometry(v);
		}
	}
}

/* An app asks to be brought to the front (xdg-activation: a click on its taskbar button, a link opened in it…).
 * Red Star is the user's own desktop, so any request is honoured. */
static void xdg_activate(struct wl_listener *l, void *data)
{
	struct server *s = wl_container_of(l, s, xdg_activate);
	(void)s;
	struct wlr_xdg_activation_v1_request_activate_event *e = data;
	struct wlr_xdg_toplevel *t = wlr_xdg_toplevel_try_from_wlr_surface(e->surface);
	struct view *v = t ? view_from_xdg_toplevel(t) : NULL;
	if (!v || !v->mapped) return;
	if (v->minimized) view_set_minimized(v, false);
	else focus_view(v);
}

/* ───────────────────────── the program it runs ───────────────────────── */

static int child_exited(int sig, void *data)
{
	(void)sig;
	struct server *s = data;
	int status;
	pid_t pid;
	while ((pid = waitpid(-1, &status, WNOHANG)) > 0) {
		if (pid == s->child) {
			s->exit_code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + (WIFSIGNALED(status) ? WTERMSIG(status) : 0);
			s->child = 0;
			wl_display_terminate(s->display);
		}
	}
	return 0;
}

static int terminate(int sig, void *data)
{
	(void)sig;
	struct server *s = data;
	wl_display_terminate(s->display);
	return 0;
}

static void spawn(struct server *s, char **argv)
{
	pid_t pid = fork();
	if (pid == 0) {
		sigset_t set;
		sigemptyset(&set);
		sigprocmask(SIG_SETMASK, &set, NULL);
		setsid();
		setenv("REDSTAR_COMPOSITOR_RUNNING", "redstar-comp", 1);
		execvp(argv[0], argv);
		fprintf(stderr, "redstar-comp: could not start %s\n", argv[0]);
		_exit(127);
	}
	s->child = pid;
}

static void usage(const char *me)
{
	fprintf(stderr,
		"Usage: %s [-d] [-w] [-v] [-- command args…]\n"
		"  Crimson Banner OS's Wayland compositor. Runs the command with DISPLAY (Xwayland) and WAYLAND_DISPLAY set,\n"
		"  and quits when it exits.\n"
		"  -d   debug messages\n"
		"  -w   on another desktop, open as an ordinary window instead of full screen (or REDSTAR_COMP_WINDOWED=1)\n"
		"  -v   print the version (also --version)\n"
		"  Keys: Alt+F4 close, Alt+drag move, Alt+right-drag resize, Ctrl+Alt+Backspace quit.\n", me);
}

/* True when there are GPU render nodes but every one is a virtual machine's display adapter (as in the shell's
 * Graphics.SoftwareOnlyDrivers). VMware's vmwgfx offers a render node even without 3D; wlroots' GPU renderer, and
 * the GPU buffers it hands Xwayland, then fail and take the session down, so we draw in software from the start.
 * REDSTAR_COMP_SYS_DRM points this at another folder (the tests). */
static bool only_software_drm_drivers(const char *sys_drm)
{
	static const char *const vm[] = { "vmwgfx", "qxl", "bochs", "bochs-drm", "cirrus", "cirrus-qemu", "simpledrm",
		"hyperv_drm", "vboxvideo", "udl", "ast", "mgag200", NULL };
	const char *over = getenv("REDSTAR_COMP_SYS_DRM");
	if (over && *over) sys_drm = over;
	DIR *dir = opendir(sys_drm);
	if (!dir) return false;
	int nodes = 0, vm_nodes = 0;
	struct dirent *e;
	while ((e = readdir(dir))) {
		if (strncmp(e->d_name, "renderD", 7) != 0) continue;
		nodes++;
		char link[512], target[512];
		snprintf(link, sizeof link, "%s/%s/device/driver", sys_drm, e->d_name);
		ssize_t n = readlink(link, target, sizeof target - 1);
		if (n <= 0) continue;
		target[n] = '\0';
		const char *name = strrchr(target, '/');
		name = name ? name + 1 : target;
		for (int i = 0; vm[i]; i++)
			if (!strcmp(name, vm[i])) { vm_nodes++; break; }
	}
	closedir(dir);
	return nodes > 0 && vm_nodes == nodes;
}

int main(int argc, char **argv)
{
	int c;
	enum wlr_log_importance level = WLR_INFO;
	if (argc > 1 && !strcmp(argv[1], "--version")) { printf("redstar-comp %s\n", REDSTAR_VERSION); return 0; }
	while ((c = getopt(argc, argv, "dwhv")) != -1) {
		if (c == 'd') level = WLR_DEBUG;
		else if (c == 'v') { printf("redstar-comp %s\n", REDSTAR_VERSION); return 0; }
		else if (c == 'w') want_fullscreen = false;
		else { usage(argv[0]); return c == 'h' ? 0 : 1; }
	}
	wlr_log_init(level, NULL);
	if (!getenv("XDG_RUNTIME_DIR")) {
		wlr_log(WLR_ERROR, "XDG_RUNTIME_DIR is not set");
		return 1;
	}
	const char *sc = getenv("REDSTAR_UI_SCALE");
	if (sc && *sc) { double v = atof(sc); if (v >= 1 && v <= 4) ui_scale = v; }
	TH = S(32); BW = S(3); SHADOW = S(18);

	struct server srv = {0};
	struct server *s = &srv;
	wl_list_init(&s->outputs);
	wl_list_init(&s->views);
	wl_list_init(&s->keyboards);
	wl_list_init(&s->popups);
	wl_list_init(&s->ipc_clients);
	s->ipc_fd = -1;

	s->display = wl_display_create();
	s->loop = wl_display_get_event_loop(s->display);
	const char *win = getenv("REDSTAR_COMP_WINDOWED");
	if (win && strcmp(win, "1") == 0) want_fullscreen = false;
	if (getenv("DISPLAY") && !getenv("WAYLAND_DISPLAY")) host_display = strdup(getenv("DISPLAY"));
	/* on a Wayland desktop: our own host window (full screen, host shortcuts passed through); else wlroots picks */
	bool nested_wl = (s->backend = nested_backend_create(s->loop)) != NULL;
	if (!nested_wl) s->backend = wlr_backend_autocreate(s->loop, &s->session);
	if (!s->backend) { wlr_log(WLR_ERROR, "no backend (no display to run on?)"); return 1; }
	const char *force = getenv("REDSTAR_COMP_GPU");
	bool vm_only = !nested_wl && !host_display && !(force && strcmp(force, "1") == 0) && only_software_drm_drivers("/sys/class/drm");
	if (vm_only || (force && strcmp(force, "0") == 0))
		wlr_log(WLR_INFO, "%s: not trying the GPU", vm_only ? "a virtual machine's display adapter (vmwgfx, qxl…)" : "REDSTAR_COMP_GPU=0");
	else
		s->renderer = wlr_renderer_autocreate(s->backend);
	if (!s->renderer) {
		/* no GPU drawing here (a VM without 3D graphics, a driver Mesa can't use): wlroots only falls back to
		 * software by itself when there's no GPU at all, so we do: pixman draws into dumb buffers on the screen */
		wlr_log(WLR_INFO, "no GPU renderer: drawing in software (pixman)");
		s->renderer = wlr_pixman_renderer_create();
		s->software = true;
		/* Xwayland then draws in software too: its GPU buffers (glamor) are ones pixman can't read */
		setenv("XWAYLAND_NO_GLAMOR", "1", 1);
		/* and programs that draw with OpenGL use Mesa's software driver, not the adapter's that can't draw */
		if (vm_only) setenv("LIBGL_ALWAYS_SOFTWARE", "1", 1);
	}
	if (!s->renderer) { wlr_log(WLR_ERROR, "no renderer"); return 1; }
	wlr_renderer_init_wl_display(s->renderer, s->display);
	s->allocator = wlr_allocator_autocreate(s->backend, s->renderer);
	if (!s->allocator) { wlr_log(WLR_ERROR, "no allocator"); return 1; }

	s->compositor = wlr_compositor_create(s->display, 6, s->renderer);
	wlr_subcompositor_create(s->display);
	wlr_data_device_manager_create(s->display);
	wlr_primary_selection_v1_device_manager_create(s->display);
	wlr_viewporter_create(s->display);

	s->layout = wlr_output_layout_create(s->display);
	s->layout_change.notify = layout_change;
	wl_signal_add(&s->layout->events.change, &s->layout_change);
	s->new_output.notify = new_output;
	wl_signal_add(&s->backend->events.new_output, &s->new_output);
	wlr_xdg_output_manager_v1_create(s->display, s->layout);
	/* display settings for tools (wlr-randr, kanshi), screenshots and screen sharing (grim, xdg-desktop-portal-wlr,
	 * OBS), clipboard tools (wl-clipboard), and GPU buffers for apps when the renderer can take them */
	s->output_mgr = wlr_output_manager_v1_create(s->display);
	s->output_apply.notify = output_manager_apply;
	wl_signal_add(&s->output_mgr->events.apply, &s->output_apply);
	s->output_test.notify = output_manager_test;
	wl_signal_add(&s->output_mgr->events.test, &s->output_test);
	wlr_screencopy_manager_v1_create(s->display);
	wlr_export_dmabuf_manager_v1_create(s->display);
	wlr_ext_image_copy_capture_manager_v1_create(s->display, 1);
	wlr_ext_output_image_capture_source_manager_v1_create(s->display, 1);
	wlr_data_control_manager_v1_create(s->display);
	struct wlr_virtual_keyboard_manager_v1 *vkm = wlr_virtual_keyboard_manager_v1_create(s->display);
	s->new_virtual_keyboard.notify = new_virtual_keyboard;
	wl_signal_add(&vkm->events.new_virtual_keyboard, &s->new_virtual_keyboard);
	struct wlr_virtual_pointer_manager_v1 *vpm = wlr_virtual_pointer_manager_v1_create(s->display);
	s->new_virtual_pointer.notify = new_virtual_pointer;
	wl_signal_add(&vpm->events.new_virtual_pointer, &s->new_virtual_pointer);
	struct wlr_xdg_activation_v1 *activation = wlr_xdg_activation_v1_create(s->display);
	s->xdg_activate.notify = xdg_activate;
	wl_signal_add(&activation->events.request_activate, &s->xdg_activate);
	/* GPU buffers from programs: only when drawing on the GPU (in software, a tiled GPU buffer can't be read, and
	 * offering them made Xwayland fail with "importing the supplied dmabufs failed", taking the shell with it) */
	if (!s->software && wlr_renderer_get_texture_formats(s->renderer, WLR_BUFFER_CAP_DMABUF))
		wlr_linux_dmabuf_v1_create_with_renderer(s->display, 5, s->renderer);
	outputs_load();

	s->scene = wlr_scene_create();
	s->scene_layout = wlr_scene_attach_output_layout(s->scene, s->layout);
	s->background = wlr_scene_rect_create(&s->scene->tree, 0, 0, C_BG);
	s->l_desktop = wlr_scene_tree_create(&s->scene->tree);
	s->l_normal = wlr_scene_tree_create(&s->scene->tree);
	s->l_top = wlr_scene_tree_create(&s->scene->tree);
	s->l_full = wlr_scene_tree_create(&s->scene->tree);
	s->l_unmanaged = wlr_scene_tree_create(&s->scene->tree);
	s->l_drag = wlr_scene_tree_create(&s->scene->tree);
	const char *shadows = getenv("REDSTAR_SHADOWS");
	if (!shadows || strcmp(shadows, "0") != 0) make_shadow_image();   /* Settings → Desktop & Taskbar → shadows */

	s->xdg_shell = wlr_xdg_shell_create(s->display, 5);
	s->new_toplevel.notify = new_toplevel;
	wl_signal_add(&s->xdg_shell->events.new_toplevel, &s->new_toplevel);
	s->new_popup.notify = new_popup;
	wl_signal_add(&s->xdg_shell->events.new_popup, &s->new_popup);
	s->deco_mgr = wlr_xdg_decoration_manager_v1_create(s->display);
	s->new_decoration.notify = new_decoration;
	wl_signal_add(&s->deco_mgr->events.new_toplevel_decoration, &s->new_decoration);

	s->cursor = wlr_cursor_create();
	wlr_cursor_attach_output_layout(s->cursor, s->layout);
	const char *csize = getenv("XCURSOR_SIZE");
	s->xcursor = wlr_xcursor_manager_create(getenv("XCURSOR_THEME"), csize && atoi(csize) > 0 ? atoi(csize) : 24);
	wlr_xcursor_manager_load(s->xcursor, 1);
	s->cursor_motion.notify = cursor_motion;
	wl_signal_add(&s->cursor->events.motion, &s->cursor_motion);
	s->cursor_motion_abs.notify = cursor_motion_abs;
	wl_signal_add(&s->cursor->events.motion_absolute, &s->cursor_motion_abs);
	s->cursor_button.notify = cursor_button;
	wl_signal_add(&s->cursor->events.button, &s->cursor_button);
	s->cursor_axis.notify = cursor_axis;
	wl_signal_add(&s->cursor->events.axis, &s->cursor_axis);
	s->cursor_frame.notify = cursor_frame;
	wl_signal_add(&s->cursor->events.frame, &s->cursor_frame);

	s->new_input.notify = new_input;
	wl_signal_add(&s->backend->events.new_input, &s->new_input);
	s->seat = wlr_seat_create(s->display, "seat0");
	s->request_cursor.notify = request_cursor;
	wl_signal_add(&s->seat->events.request_set_cursor, &s->request_cursor);
	s->idle_notifier = wlr_idle_notifier_v1_create(s->display);           /* ext-idle-notify-v1, for other programs */
	struct wlr_idle_inhibit_manager_v1 *inhibit = wlr_idle_inhibit_v1_create(s->display);
	s->new_inhibitor.notify = new_inhibitor;
	wl_signal_add(&inhibit->events.new_inhibitor, &s->new_inhibitor);
	s->idle_timer = wl_event_loop_add_timer(s->loop, idle_fire, s);
	s->last_input_ms = mono_ms();
	struct wlr_cursor_shape_manager_v1 *shapes = wlr_cursor_shape_manager_v1_create(s->display, 1);
	s->request_shape.notify = request_shape;
	wl_signal_add(&shapes->events.request_set_shape, &s->request_shape);
	s->request_selection.notify = request_selection;
	wl_signal_add(&s->seat->events.request_set_selection, &s->request_selection);
	s->request_primary.notify = request_primary;
	wl_signal_add(&s->seat->events.request_set_primary_selection, &s->request_primary);
	s->request_drag.notify = request_drag;
	wl_signal_add(&s->seat->events.request_start_drag, &s->request_drag);
	s->start_drag.notify = start_drag;
	wl_signal_add(&s->seat->events.start_drag, &s->start_drag);

	/* Xwayland, started when the first X11 client connects */
	s->xwayland = wlr_xwayland_create(s->display, s->compositor, true);
	if (s->xwayland) {
		s->xwayland_ready.notify = xwayland_ready;
		wl_signal_add(&s->xwayland->events.ready, &s->xwayland_ready);
		s->new_xsurface.notify = new_xsurface;
		wl_signal_add(&s->xwayland->events.new_surface, &s->new_xsurface);
		setenv("DISPLAY", s->xwayland->display_name, true);
	} else {
		wlr_log(WLR_ERROR, "Xwayland could not start: X11 apps (Crimson Banner's own windows) won't work");
	}

	const char *socket = wl_display_add_socket_auto(s->display);
	if (!socket) { wlr_log(WLR_ERROR, "could not open a Wayland socket"); return 1; }
	if (!wlr_backend_start(s->backend)) { wlr_log(WLR_ERROR, "could not start the backend"); return 1; }
	if (nested_wl && !nested_create_output(want_fullscreen, host_closed, s)) {
		wlr_log(WLR_ERROR, "could not open a window on the host desktop");
		return 1;
	}
	setenv("WAYLAND_DISPLAY", socket, true);
	ipc_start(s, socket);
	positions_load();
	const char *rm = getenv("REDSTAR_REDUCE_MOTION");
	reduce_motion = rm && strcmp(rm, "1") == 0;
	const char *tp = getenv("REDSTAR_COMP_TRACE_POINTER");
	trace_pointer = tp && strcmp(tp, "1") == 0;
	s->fade_timer = wl_event_loop_add_timer(s->loop, fade_tick, s);
	setenv("XDG_SESSION_TYPE", "wayland", true);
	setenv("REDSTAR_COMPOSITOR_RUNNING", "redstar-comp", true);
	wlr_log(WLR_INFO, "redstar-comp on WAYLAND_DISPLAY=%s, DISPLAY=%s", socket, s->xwayland ? s->xwayland->display_name : "(none)");

	struct wl_event_source *sigchld = wl_event_loop_add_signal(s->loop, SIGCHLD, child_exited, s);
	struct wl_event_source *sigterm = wl_event_loop_add_signal(s->loop, SIGTERM, terminate, s);
	struct wl_event_source *sigint = wl_event_loop_add_signal(s->loop, SIGINT, terminate, s);
	if (optind < argc) spawn(s, argv + optind);

	wlr_cursor_set_xcursor(s->cursor, s->xcursor, "default");
	struct wlr_box all;
	wlr_output_layout_get_box(s->layout, NULL, &all);
	wlr_cursor_warp(s->cursor, NULL, all.x + all.width / 2.0, all.y + all.height / 2.0);

	wl_display_run(s->display);

	if (s->child > 0) { kill(-s->child, SIGTERM); kill(s->child, SIGTERM); }
	/* wlroots 0.20 checks that nobody still listens when an object is destroyed: let go of everything first */
	ipc_stop(s);
	if (s->fade_timer) wl_event_source_remove(s->fade_timer);
	wl_display_destroy_clients(s->display);
	if (s->xwayland) {
		wl_list_remove(&s->xwayland_ready.link);
		wl_list_remove(&s->new_xsurface.link);
	}
	wl_list_remove(&s->new_toplevel.link);
	wl_list_remove(&s->new_popup.link);
	wl_list_remove(&s->new_decoration.link);
	wl_list_remove(&s->cursor_motion.link);
	wl_list_remove(&s->cursor_motion_abs.link);
	wl_list_remove(&s->cursor_button.link);
	wl_list_remove(&s->cursor_axis.link);
	wl_list_remove(&s->cursor_frame.link);
	wl_list_remove(&s->request_cursor.link);
	wl_list_remove(&s->request_shape.link);
	wl_list_remove(&s->new_inhibitor.link);
	if (s->idle_timer) wl_event_source_remove(s->idle_timer);
	wl_list_remove(&s->request_selection.link);
	wl_list_remove(&s->request_primary.link);
	wl_list_remove(&s->request_drag.link);
	wl_list_remove(&s->start_drag.link);
	wl_list_remove(&s->new_input.link);
	wl_list_remove(&s->new_output.link);
	wl_list_remove(&s->layout_change.link);
	wl_list_remove(&s->output_apply.link);
	wl_list_remove(&s->output_test.link);
	wl_list_remove(&s->xdg_activate.link);
	wl_list_remove(&s->new_virtual_keyboard.link);
	wl_list_remove(&s->new_virtual_pointer.link);
	s->output_mgr = NULL;
	wl_event_source_remove(sigchld);
	wl_event_source_remove(sigterm);
	wl_event_source_remove(sigint);
	if (s->xcb_source) wl_event_source_remove(s->xcb_source);
	if (s->xcb) xcb_disconnect(s->xcb);
	if (s->xwayland) wlr_xwayland_destroy(s->xwayland);
	wlr_scene_node_destroy(&s->scene->tree.node);
	wlr_xcursor_manager_destroy(s->xcursor);
	wlr_cursor_destroy(s->cursor);
	wlr_allocator_destroy(s->allocator);
	wlr_renderer_destroy(s->renderer);
	wlr_backend_destroy(s->backend);
	wl_display_destroy(s->display);
	if (shadow_image) wlr_buffer_drop(&shadow_image->base);
	free(host_display);
	return s->exit_code;
}
