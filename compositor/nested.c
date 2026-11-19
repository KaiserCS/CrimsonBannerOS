/*
 * nested.c — redstar-comp running inside another desktop: one full-screen window on the host, like a virtual machine.
 *
 * On a Wayland host, wlroots' own nested window is a plain resizable window. Instead this file creates the host
 * window itself (xdg-shell), asks for full screen, follows the host's sizes, and hands the surface to wlroots
 * (wlr_wl_output_create_from_surface). It also asks the host to pass every key through (keyboard-shortcuts-inhibit:
 * Super, Alt+Tab and the host's other shortcuts reach Red Star, like the Xwayland -host-grab mode). KDE and GNOME
 * may ask once whether to allow that.
 * On an X11 host the wlroots window is sized to the screen and marked _NET_WM_STATE_FULLSCREEN.
 *
 * Kept apart from redstar-comp.c because it is a Wayland *client* of the host (wayland-client headers).
 * Licence: GPL-3.0-or-later.
 */
#define _POSIX_C_SOURCE 200809L
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wayland-client.h>
#include <xcb/xcb.h>
#include <wlr/backend.h>
#include <wlr/backend/wayland.h>
#if __has_include(<wlr/backend/x11.h>)
#include <wlr/backend/x11.h>
#define HAVE_X11_BACKEND 1
#endif
#include <wlr/types/wlr_output.h>
#include <wlr/util/log.h>
#include "xdg-shell-client-protocol.h"
#include "keyboard-shortcuts-inhibit-unstable-v1-client-protocol.h"
#include "nested.h"

static struct {
	struct wlr_backend *backend;
	struct wl_display *remote;
	struct wl_compositor *compositor;
	struct xdg_wm_base *wm_base;
	struct wl_seat *seat;
	struct zwp_keyboard_shortcuts_inhibit_manager_v1 *inhibit_mgr;
	struct zwp_keyboard_shortcuts_inhibitor_v1 *inhibitor;
	struct wl_surface *surface;
	struct xdg_surface *xsurface;
	struct xdg_toplevel *toplevel;
	struct wlr_output *output;
	struct wl_event_loop *loop;
	struct wl_event_source *pump;       /* reads the host's messages for our window (see pump_host) */
	int w, h, want_w, want_h;
	bool configured;
	void (*on_close)(void *);
	void *data;
} n;

/* ───────── Wayland host ───────── */

static void apply_size(void)
{
	if (!n.output || n.want_w <= 0 || n.want_h <= 0 || (n.want_w == n.w && n.want_h == n.h)) return;
	struct wlr_output_state st;
	wlr_output_state_init(&st);
	wlr_output_state_set_custom_mode(&st, n.want_w, n.want_h, 0);
	if (wlr_output_commit_state(n.output, &st)) {
		n.w = n.want_w; n.h = n.want_h;
		wlr_log(WLR_INFO, "host window is now %dx%d", n.w, n.h);
	} else {
		wlr_log(WLR_ERROR, "could not resize the host window to %dx%d", n.want_w, n.want_h);
	}
	wlr_output_state_finish(&st);
}

/* wlroots reads the host connection but only handles its own objects' messages; the ones for our window (the
 * full-screen size, which KDE and GNOME send once the window is shown, resizes, pings) wait on the default queue
 * until someone dispatches it — that's us, every frame or so. */
static int pump_host(void *data)
{
	(void)data;
	wl_display_dispatch_pending(n.remote);
	wl_display_flush(n.remote);
	wl_event_source_timer_update(n.pump, 16);
	return 0;
}

void nested_initial_size(int *w, int *h)
{
	*w = n.backend && n.want_w > 0 ? n.want_w : 0;
	*h = n.backend && n.want_h > 0 ? n.want_h : 0;
}

static void wm_base_ping(void *data, struct xdg_wm_base *b, uint32_t serial)
{
	(void)data;
	xdg_wm_base_pong(b, serial);
}
static const struct xdg_wm_base_listener wm_base_listener = { .ping = wm_base_ping };

static void xsurface_configure(void *data, struct xdg_surface *s, uint32_t serial)
{
	(void)data;
	xdg_surface_ack_configure(s, serial);
	n.configured = true;
	apply_size();
}
static const struct xdg_surface_listener xsurface_listener = { .configure = xsurface_configure };

static void toplevel_configure(void *data, struct xdg_toplevel *t, int32_t w, int32_t h, struct wl_array *states)
{
	(void)data; (void)t; (void)states;
	if (w > 0 && h > 0) { n.want_w = w; n.want_h = h; }      /* 0 × 0: our choice (keep what we have) */
}

static void toplevel_close(void *data, struct xdg_toplevel *t)
{
	(void)data; (void)t;
	if (n.on_close) n.on_close(n.data);
}
static const struct xdg_toplevel_listener toplevel_listener = { .configure = toplevel_configure, .close = toplevel_close };

static void registry_global(void *data, struct wl_registry *r, uint32_t name, const char *iface, uint32_t version)
{
	(void)data;
	if (strcmp(iface, wl_compositor_interface.name) == 0 && !n.compositor)
		n.compositor = wl_registry_bind(r, name, &wl_compositor_interface, version < 4 ? version : 4);
	else if (strcmp(iface, xdg_wm_base_interface.name) == 0 && !n.wm_base) {
		n.wm_base = wl_registry_bind(r, name, &xdg_wm_base_interface, version < 2 ? version : 2);
		xdg_wm_base_add_listener(n.wm_base, &wm_base_listener, NULL);
	} else if (strcmp(iface, wl_seat_interface.name) == 0 && !n.seat)
		n.seat = wl_registry_bind(r, name, &wl_seat_interface, 1);
	else if (strcmp(iface, zwp_keyboard_shortcuts_inhibit_manager_v1_interface.name) == 0 && !n.inhibit_mgr)
		n.inhibit_mgr = wl_registry_bind(r, name, &zwp_keyboard_shortcuts_inhibit_manager_v1_interface, 1);
}

static void registry_remove(void *data, struct wl_registry *r, uint32_t name) { (void)data; (void)r; (void)name; }
static const struct wl_registry_listener registry_listener = { .global = registry_global, .global_remove = registry_remove };

struct wlr_backend *nested_backend_create(struct wl_event_loop *loop)
{
	if (!getenv("WAYLAND_DISPLAY") || getenv("WLR_BACKENDS")) return NULL;     /* not nested on Wayland, or overridden */
	n.loop = loop;
	n.backend = wlr_wl_backend_create(loop, NULL);
	if (!n.backend) return NULL;
	n.remote = wlr_wl_backend_get_remote_display(n.backend);
	return n.backend;
}

bool nested_create_output(bool fullscreen, void (*on_close)(void *), void *data)
{
	if (!n.backend) return false;
	n.on_close = on_close;
	n.data = data;
	struct wl_registry *reg = wl_display_get_registry(n.remote);
	wl_registry_add_listener(reg, &registry_listener, NULL);
	wl_display_roundtrip(n.remote);

	if (n.compositor && n.wm_base) {
		n.surface = wl_compositor_create_surface(n.compositor);
		n.xsurface = xdg_wm_base_get_xdg_surface(n.wm_base, n.surface);
		xdg_surface_add_listener(n.xsurface, &xsurface_listener, NULL);
		n.toplevel = xdg_surface_get_toplevel(n.xsurface);
		xdg_toplevel_add_listener(n.toplevel, &toplevel_listener, NULL);
		xdg_toplevel_set_title(n.toplevel, "Red Star OS");
		xdg_toplevel_set_app_id(n.toplevel, "redstaros");
		if (fullscreen) xdg_toplevel_set_fullscreen(n.toplevel, NULL);
		n.want_w = 1280; n.want_h = 720;
		wl_surface_commit(n.surface);
		for (int i = 0; i < 10 && !n.configured; i++) wl_display_roundtrip(n.remote);
		n.output = wlr_wl_output_create_from_surface(n.backend, n.surface);
	}
	if (!n.output) {
		wlr_log(WLR_ERROR, "could not make our own host window; using a plain one");
		struct wlr_output *o = wlr_wl_output_create(n.backend);
		if (o) wlr_wl_output_set_title(o, "Red Star OS");
		return o != NULL;
	}
	apply_size();
	n.pump = wl_event_loop_add_timer(n.loop, pump_host, NULL);
	wl_event_source_timer_update(n.pump, 16);
	if (n.inhibit_mgr && n.seat) {
		n.inhibitor = zwp_keyboard_shortcuts_inhibit_manager_v1_inhibit_shortcuts(n.inhibit_mgr, n.surface, n.seat);
		wlr_log(WLR_INFO, "asked the host to pass its shortcuts through to Red Star");
	} else {
		wlr_log(WLR_INFO, "the host can't pass its shortcuts through (no keyboard-shortcuts-inhibit)");
	}
	wl_display_flush(n.remote);
	wlr_log(WLR_INFO, "host window %s, %dx%d", fullscreen ? "full screen" : "windowed", n.want_w, n.want_h);
	return true;
}

bool nested_output(struct wlr_output *o)
{
#ifdef HAVE_X11_BACKEND
	if (wlr_output_is_x11(o)) return true;
#endif
	return wlr_output_is_wl(o);
}

/* ───────── X11 host ───────── */

#ifdef HAVE_X11_BACKEND
struct x11_fs {
	struct wl_event_source *timer;
	char *display;
	char title[64];
	int tries;
};

static xcb_atom_t atom(xcb_connection_t *c, const char *name)
{
	xcb_intern_atom_reply_t *r = xcb_intern_atom_reply(c, xcb_intern_atom(c, 0, strlen(name), name), NULL);
	xcb_atom_t a = r ? r->atom : XCB_ATOM_NONE;
	free(r);
	return a;
}

/* finds the wlroots window by its (unique) title among the host's managed windows and asks for full screen */
static int x11_try_fullscreen(void *data)
{
	struct x11_fs *f = data;
	bool done = false;
	xcb_connection_t *c = xcb_connect(f->display, NULL);
	if (!xcb_connection_has_error(c)) {
		xcb_window_t root = xcb_setup_roots_iterator(xcb_get_setup(c)).data->root;
		xcb_atom_t list = atom(c, "_NET_CLIENT_LIST"), name = atom(c, "_NET_WM_NAME"), utf8 = atom(c, "UTF8_STRING");
		xcb_get_property_reply_t *r = xcb_get_property_reply(c, xcb_get_property(c, 0, root, list, XCB_ATOM_WINDOW, 0, 1024), NULL);
		if (r) {
			xcb_window_t *wins = xcb_get_property_value(r);
			int cnt = xcb_get_property_value_length(r) / 4;
			for (int i = 0; i < cnt && !done; i++) {
				for (int k = 0; k < 2 && !done; k++) {
					xcb_get_property_reply_t *t = xcb_get_property_reply(c,
						xcb_get_property(c, 0, wins[i], k ? XCB_ATOM_WM_NAME : name, k ? XCB_ATOM_STRING : utf8, 0, 64), NULL);
					if (t) {
						int len = xcb_get_property_value_length(t);
						if (len == (int)strlen(f->title) && memcmp(xcb_get_property_value(t), f->title, len) == 0) {
							xcb_client_message_event_t ev = {0};
							ev.response_type = XCB_CLIENT_MESSAGE;
							ev.format = 32;
							ev.window = wins[i];
							ev.type = atom(c, "_NET_WM_STATE");
							ev.data.data32[0] = 1;                       /* add */
							ev.data.data32[1] = atom(c, "_NET_WM_STATE_FULLSCREEN");
							ev.data.data32[3] = 1;
							xcb_send_event(c, 0, root, XCB_EVENT_MASK_SUBSTRUCTURE_REDIRECT | XCB_EVENT_MASK_SUBSTRUCTURE_NOTIFY,
								(const char *)&ev);
							xcb_flush(c);
							done = true;
						}
						free(t);
					}
				}
			}
			free(r);
		}
	}
	xcb_disconnect(c);
	if (done || ++f->tries > 40) {
		if (!done) wlr_log(WLR_INFO, "the host window manager didn't list our window; it stays screen-sized");
		wl_event_source_remove(f->timer);
		free(f->display);
		free(f);
		return 0;
	}
	wl_event_source_timer_update(f->timer, 100);
	return 0;
}

void nested_x11_output(struct wl_event_loop *loop, struct wlr_output *o, const char *host_display, bool fullscreen)
{
	if (!wlr_output_is_x11(o)) return;
	static int count;
	struct x11_fs *f = calloc(1, sizeof *f);
	if (!f) return;
	if (count++) snprintf(f->title, sizeof f->title, "Red Star OS (%d)", count);
	else snprintf(f->title, sizeof f->title, "Red Star OS");
	wlr_x11_output_set_title(o, f->title);
	if (!fullscreen || !host_display) { free(f); return; }
	/* the host screen's size first, then full screen once the host's window manager has the window */
	xcb_connection_t *c = xcb_connect(host_display, NULL);
	if (!xcb_connection_has_error(c)) {
		xcb_screen_t *scr = xcb_setup_roots_iterator(xcb_get_setup(c)).data;
		struct wlr_output_state st;
		wlr_output_state_init(&st);
		wlr_output_state_set_custom_mode(&st, scr->width_in_pixels, scr->height_in_pixels, 0);
		wlr_output_commit_state(o, &st);
		wlr_output_state_finish(&st);
	}
	xcb_disconnect(c);
	f->display = strdup(host_display);
	f->timer = wl_event_loop_add_timer(loop, x11_try_fullscreen, f);
	wl_event_source_timer_update(f->timer, 150);
}
#else
void nested_x11_output(struct wl_event_loop *loop, struct wlr_output *o, const char *host_display, bool fullscreen)
{
	(void)loop; (void)o; (void)host_display; (void)fullscreen;
}
#endif
