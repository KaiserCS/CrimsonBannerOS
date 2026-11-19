/*
 * inputtest — keyboard and mouse checks for redstar-comp, with a virtual keyboard and pointer (the same protocols
 * wtype and ydotool-style tools use), so what used to be tried only by hand runs headless:
 *   • the shell's shortcuts: Alt+Tab (open, next, commit on Alt release, Escape cancels), Super alone, Super+arrow,
 *     Ctrl+Alt+T, Print / Shift+Print, and none of it when the shell hasn't asked for them;
 *   • a menu closes when you click outside it and stays open when you click inside it;
 *   • title bar: double-click maximises, the maximise button restores, the close button closes.
 * Run by run-tests.sh after nativetest. The two virtual-input protocols aren't in wayland-protocols, so their few
 * requests are declared here by hand (wlroots' virtual-keyboard-unstable-v1, wlr-virtual-pointer-unstable-v1).
 */
#define _GNU_SOURCE
#include <linux/input-event-codes.h>
#include <poll.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>
#include <wayland-client.h>
#include <xkbcommon/xkbcommon.h>
#include "xdg-shell-client-protocol.h"
#include "xdg-decoration-unstable-v1-client-protocol.h"

/* ── the virtual-input interfaces (request order = opcodes) ── */
extern const struct wl_interface zwp_virtual_keyboard_v1_interface, zwlr_virtual_pointer_v1_interface;
static const struct wl_interface *no_types[8];
static const struct wl_interface *vk_create_types[] = { &wl_seat_interface, &zwp_virtual_keyboard_v1_interface };
static const struct wl_interface *vp_create_types[] = { &wl_seat_interface, &zwlr_virtual_pointer_v1_interface };
static const struct wl_message vk_manager_requests[] = { { "create_virtual_keyboard", "on", vk_create_types } };
static const struct wl_message vk_requests[] = {
	{ "keymap", "uhu", no_types }, { "key", "uuu", no_types }, { "modifiers", "uuuu", no_types }, { "destroy", "", no_types },
};
static const struct wl_message vp_manager_requests[] = { { "create_virtual_pointer", "?on", vp_create_types }, { "destroy", "", no_types } };
static const struct wl_message vp_requests[] = {
	{ "motion", "uff", no_types }, { "motion_absolute", "uuuuu", no_types }, { "button", "uuu", no_types },
	{ "axis", "uuf", no_types }, { "frame", "", no_types }, { "axis_source", "u", no_types }, { "axis_stop", "uu", no_types },
	{ "axis_discrete", "uufi", no_types }, { "destroy", "", no_types },
};
const struct wl_interface zwp_virtual_keyboard_v1_interface = { "zwp_virtual_keyboard_v1", 1, 4, vk_requests, 0, NULL };
static const struct wl_interface vk_manager_interface = { "zwp_virtual_keyboard_manager_v1", 1, 1, vk_manager_requests, 0, NULL };
const struct wl_interface zwlr_virtual_pointer_v1_interface = { "zwlr_virtual_pointer_v1", 1, 9, vp_requests, 0, NULL };
static const struct wl_interface vp_manager_interface = { "zwlr_virtual_pointer_manager_v1", 1, 2, vp_manager_requests, 0, NULL };

static struct wl_display *dpy;
static struct wl_compositor *compositor;
static struct wl_shm *shm;
static struct wl_seat *seat;
static struct xdg_wm_base *wm;
static struct zxdg_decoration_manager_v1 *deco_mgr;
static struct wl_proxy *vk_mgr, *vp_mgr, *vk, *vp;
static int ipc = -1, passed, failed;
static char ipcbuf[65536];
static size_t ipclen;
static uint32_t now_ms_u32;

static void check(bool ok, const char *what)
{
	printf("%s %s\n", ok ? "PASS" : "FAIL", what);
	if (ok) passed++; else failed++;
	fflush(stdout);
}

static double now_ms(void)
{
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	return t.tv_sec * 1000.0 + t.tv_nsec / 1e6;
}
static uint32_t stamp(void) { return now_ms_u32 = (uint32_t)now_ms(); }

/* ── xdg ── */
static bool configured, maximized, closed, popup_done;
static void ping(void *d, struct xdg_wm_base *b, uint32_t serial) { (void)d; xdg_wm_base_pong(b, serial); }
static const struct xdg_wm_base_listener wm_listener = { .ping = ping };
static void surf_configure(void *d, struct xdg_surface *s, uint32_t serial) { (void)d; xdg_surface_ack_configure(s, serial); configured = true; }
static const struct xdg_surface_listener surf_listener = { .configure = surf_configure };
static void top_configure(void *d, struct xdg_toplevel *t, int32_t w, int32_t h, struct wl_array *states)
{
	(void)d; (void)t; (void)w; (void)h;
	maximized = false;
	uint32_t *st;
	wl_array_for_each(st, states) if (*st == XDG_TOPLEVEL_STATE_MAXIMIZED) maximized = true;
}
static void top_close(void *d, struct xdg_toplevel *t) { (void)d; (void)t; closed = true; }
static const struct xdg_toplevel_listener top_listener = { .configure = top_configure, .close = top_close };
static void popup_configure(void *d, struct xdg_popup *p, int32_t x, int32_t y, int32_t w, int32_t h) { (void)d; (void)p; (void)x; (void)y; (void)w; (void)h; }
static void popup_gone(void *d, struct xdg_popup *p) { (void)d; (void)p; popup_done = true; }
static const struct xdg_popup_listener popup_listener = { .configure = popup_configure, .popup_done = popup_gone };

static void global(void *d, struct wl_registry *r, uint32_t name, const char *iface, uint32_t v)
{
	(void)d; (void)v;
	if (!strcmp(iface, wl_compositor_interface.name)) compositor = wl_registry_bind(r, name, &wl_compositor_interface, 4);
	else if (!strcmp(iface, wl_shm_interface.name)) shm = wl_registry_bind(r, name, &wl_shm_interface, 1);
	else if (!strcmp(iface, wl_seat_interface.name) && !seat) seat = wl_registry_bind(r, name, &wl_seat_interface, 1);
	else if (!strcmp(iface, xdg_wm_base_interface.name)) { wm = wl_registry_bind(r, name, &xdg_wm_base_interface, 1); xdg_wm_base_add_listener(wm, &wm_listener, NULL); }
	else if (!strcmp(iface, zxdg_decoration_manager_v1_interface.name)) deco_mgr = wl_registry_bind(r, name, &zxdg_decoration_manager_v1_interface, 1);
	else if (!strcmp(iface, vk_manager_interface.name)) vk_mgr = wl_registry_bind(r, name, &vk_manager_interface, 1);
	else if (!strcmp(iface, vp_manager_interface.name)) vp_mgr = wl_registry_bind(r, name, &vp_manager_interface, 1);
}
static void global_remove(void *d, struct wl_registry *r, uint32_t n) { (void)d; (void)r; (void)n; }
static const struct wl_registry_listener reg_listener = { .global = global, .global_remove = global_remove };

static struct wl_buffer *make_buffer(int w, int h, uint32_t argb)
{
	int stride = w * 4, size = stride * h;
	int fd = memfd_create("inputtest", MFD_CLOEXEC);
	if (fd < 0 || ftruncate(fd, size) < 0) return NULL;
	uint32_t *px = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
	for (int i = 0; i < w * h; i++) px[i] = argb;
	munmap(px, size);
	struct wl_shm_pool *pool = wl_shm_create_pool(shm, fd, size);
	struct wl_buffer *b = wl_shm_pool_create_buffer(pool, 0, w, h, stride, WL_SHM_FORMAT_ARGB8888);
	wl_shm_pool_destroy(pool);
	close(fd);
	return b;
}

/* runs both connections for about ms, keeping what the shell's connection sends for the checks to look at */
static bool pump(int ms)
{
	double end = now_ms() + ms;
	do {
		wl_display_flush(dpy);
		struct pollfd p[2] = { { wl_display_get_fd(dpy), POLLIN, 0 }, { ipc, POLLIN, 0 } };
		if (poll(p, 2, 10) > 0) {
			if ((p[0].revents & POLLIN) && wl_display_dispatch(dpy) < 0) return false;
			if (ipc >= 0 && (p[1].revents & POLLIN)) {
				if (ipclen > sizeof ipcbuf - 8192) {          /* lines no check asked for: keep only the newest */
					memmove(ipcbuf, ipcbuf + ipclen - 4096, 4096);
					ipclen = 4096;
				}
				ssize_t n = read(ipc, ipcbuf + ipclen, sizeof ipcbuf - 1 - ipclen);
				if (n <= 0) return false;
				ipclen += (size_t)n;
			}
		}
		wl_display_dispatch_pending(dpy);
	} while (now_ms() < end);
	return true;
}

/* the first kept line containing `want` (and the lines before it) is used up; true if there was one */
static bool take_line(const char *want)
{
	size_t start = 0;
	while (start < ipclen) {
		char *nl = memchr(ipcbuf + start, '\n', ipclen - start);
		if (!nl) break;
		*nl = 0;
		bool hit = strstr(ipcbuf + start, want) != NULL;
		*nl = '\n';
		start = (size_t)(nl - ipcbuf) + 1;
		if (hit) {
			memmove(ipcbuf, ipcbuf + start, ipclen - start);
			ipclen -= start;
			return true;
		}
	}
	return false;
}

/* waits up to ms for a line from the shell's connection containing `want` */
static bool wait_line(const char *want, int ms)
{
	double end = now_ms() + ms;
	for (;;) {
		if (take_line(want)) return true;
		if (now_ms() >= end) return false;
		if (!pump(20)) return false;
	}
}
static void settle(int ms) { pump(ms); }
static bool wait_for(bool *flag, bool value, int ms)
{
	double end = now_ms() + ms;
	while (now_ms() < end && *flag != value) settle(20);
	return *flag == value;
}
static void ipc_send(const char *line) { if (write(ipc, line, strlen(line)) < 0) perror("write"); }

/* ── typing and pointing ── */
/* A virtual keyboard reports which modifiers are held itself (as wtype does): the compositor doesn't work them
 * out from the keys. Their bits come from the key map. */
static uint32_t mod_shift, mod_ctrl, mod_alt, mod_logo, mods_down;

static void key(uint32_t code, bool down)
{
	wl_proxy_marshal_flags(vk, 1, NULL, 1, 0, stamp(), code, down ? WL_KEYBOARD_KEY_STATE_PRESSED : WL_KEYBOARD_KEY_STATE_RELEASED);
	uint32_t bit = code == KEY_LEFTSHIFT || code == KEY_RIGHTSHIFT ? mod_shift
		: code == KEY_LEFTCTRL || code == KEY_RIGHTCTRL ? mod_ctrl
		: code == KEY_LEFTALT || code == KEY_RIGHTALT ? mod_alt
		: code == KEY_LEFTMETA || code == KEY_RIGHTMETA ? mod_logo : 0;
	if (bit) {
		mods_down = down ? (mods_down | bit) : (mods_down & ~bit);
		wl_proxy_marshal_flags(vk, 2, NULL, 1, 0, mods_down, 0u, 0u, 0u);
	}
	wl_display_flush(dpy);
	settle(30);
}
static void tap(uint32_t code) { key(code, true); key(code, false); }

static void move_to(int x, int y)
{
	wl_proxy_marshal_flags(vp, 1, NULL, 1, 0, stamp(), (uint32_t)x, (uint32_t)y, 1280u, 720u);
	wl_proxy_marshal_flags(vp, 4, NULL, 1, 0);
	wl_display_flush(dpy);
	settle(30);
}
static void button(bool down)
{
	wl_proxy_marshal_flags(vp, 2, NULL, 1, 0, stamp(), (uint32_t)BTN_LEFT, down ? WL_POINTER_BUTTON_STATE_PRESSED : WL_POINTER_BUTTON_STATE_RELEASED);
	wl_proxy_marshal_flags(vp, 4, NULL, 1, 0);
	wl_display_flush(dpy);
	settle(30);
}
static void click_at(int x, int y) { move_to(x, y); button(true); button(false); }

int main(void)
{
	dpy = wl_display_connect(NULL);
	if (!dpy) { printf("FAIL could not connect to redstar-comp\n"); return 1; }
	struct wl_registry *reg = wl_display_get_registry(dpy);
	wl_registry_add_listener(reg, &reg_listener, NULL);
	wl_display_roundtrip(dpy);
	check(vk_mgr != NULL, "redstar-comp offers virtual keyboards (wtype, tests)");
	check(vp_mgr != NULL, "redstar-comp offers virtual pointers");
	if (!vk_mgr || !vp_mgr || !seat || !wm || !shm) { printf("%d passed, %d failed\n", passed, failed); return 1; }

	/* a virtual keyboard needs a key map first: the usual one, from xkbcommon */
	vk = wl_proxy_marshal_flags(vk_mgr, 0, &zwp_virtual_keyboard_v1_interface, 1, 0, seat, NULL);
	struct xkb_context *ctx = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
	struct xkb_rule_names names = { .rules = "evdev", .model = "pc105", .layout = "us" };
	struct xkb_keymap *km = xkb_keymap_new_from_names(ctx, &names, XKB_KEYMAP_COMPILE_NO_FLAGS);
	char *map = km ? xkb_keymap_get_as_string(km, XKB_KEYMAP_FORMAT_TEXT_V1) : NULL;
	if (km) {
		mod_shift = 1u << xkb_keymap_mod_get_index(km, XKB_MOD_NAME_SHIFT);
		mod_ctrl = 1u << xkb_keymap_mod_get_index(km, XKB_MOD_NAME_CTRL);
		mod_alt = 1u << xkb_keymap_mod_get_index(km, XKB_MOD_NAME_ALT);
		mod_logo = 1u << xkb_keymap_mod_get_index(km, XKB_MOD_NAME_LOGO);
	}
	if (!map) { printf("FAIL no key map from xkbcommon\n"); return 1; }
	size_t mapsize = strlen(map) + 1;
	int mfd = memfd_create("keymap", MFD_CLOEXEC);
	if (mfd < 0 || write(mfd, map, mapsize) != (ssize_t)mapsize) { printf("FAIL keymap file\n"); return 1; }
	wl_proxy_marshal_flags(vk, 0, NULL, 1, 0, (uint32_t)XKB_KEYMAP_FORMAT_TEXT_V1, mfd, (uint32_t)mapsize);
	vp = wl_proxy_marshal_flags(vp_mgr, 0, &zwlr_virtual_pointer_v1_interface, 1, 0, seat, NULL);
	wl_display_roundtrip(dpy);

	/* the shell's connection */
	const char *path = getenv("REDSTAR_COMP_SOCKET");
	ipc = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
	struct sockaddr_un addr = { .sun_family = AF_UNIX };
	snprintf(addr.sun_path, sizeof addr.sun_path, "%s", path ? path : "");
	if (!path || connect(ipc, (struct sockaddr *)&addr, sizeof addr) != 0) { printf("FAIL no shell connection\n"); return 1; }
	ipc_send("hello\n");
	settle(200);

	/* ── shortcuts: nothing is taken until the shell asks ── */
	tap(KEY_LEFTMETA);
	check(!wait_line("key\tstart", 300), "without the shell asking, Super is left to the apps");
	ipc_send("shortcuts\ton\n");
	settle(100);

	tap(KEY_LEFTMETA);
	check(wait_line("key\tstart", 1000), "Super alone opens the Start menu");
	key(KEY_LEFTMETA, true); tap(KEY_LEFT); key(KEY_LEFTMETA, false);
	check(wait_line("key\tsnap-left", 1000), "Super+Left snaps left");
	check(!wait_line("key\tstart", 300), "…and doesn't also open the Start menu");
	key(KEY_LEFTCTRL, true); key(KEY_LEFTALT, true); tap(KEY_T); key(KEY_LEFTALT, false); key(KEY_LEFTCTRL, false);
	check(wait_line("key\tterminal", 1000), "Ctrl+Alt+T opens a terminal");

	key(KEY_LEFTALT, true);
	tap(KEY_TAB);
	check(wait_line("key\tswitcher-open", 1000), "Alt+Tab opens the switcher");
	tap(KEY_TAB);
	check(wait_line("key\tswitcher-next", 1000), "Tab again moves to the next window");
	key(KEY_LEFTSHIFT, true); tap(KEY_TAB); key(KEY_LEFTSHIFT, false);
	check(wait_line("key\tswitcher-prev", 1000), "Shift+Tab moves back");
	key(KEY_LEFTALT, false);
	check(wait_line("key\tswitcher-commit", 1000), "letting go of Alt switches");
	key(KEY_LEFTALT, true); tap(KEY_TAB); wait_line("key\tswitcher-open", 1000);
	tap(KEY_ESC);
	check(wait_line("key\tswitcher-cancel", 1000), "Escape closes the switcher without switching");
	key(KEY_LEFTALT, false);
	tap(KEY_SYSRQ);
	check(wait_line("key\tscreenshot", 1000), "Print takes a screenshot");

	/* ── a framed window to click on (centred: 400×300 on 1280×720 → client area at 440,226) ── */
	struct wl_surface *ws = wl_compositor_create_surface(compositor);
	struct xdg_surface *wx = xdg_wm_base_get_xdg_surface(wm, ws);
	xdg_surface_add_listener(wx, &surf_listener, NULL);
	struct xdg_toplevel *wt = xdg_surface_get_toplevel(wx);
	xdg_toplevel_add_listener(wt, &top_listener, NULL);
	xdg_toplevel_set_app_id(wt, "comptest-input");
	xdg_toplevel_set_title(wt, "Input test");
	if (deco_mgr) {
		struct zxdg_toplevel_decoration_v1 *d = zxdg_decoration_manager_v1_get_toplevel_decoration(deco_mgr, wt);
		zxdg_toplevel_decoration_v1_set_mode(d, ZXDG_TOPLEVEL_DECORATION_V1_MODE_SERVER_SIDE);
	}
	configured = false;
	wl_surface_commit(ws);
	for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
	struct wl_buffer *wb = make_buffer(400, 300, 0xff1e5aa8);
	wl_surface_attach(ws, wb, 0, 0);
	wl_surface_commit(ws);
	check(wait_line("comptest-input\tInput test", 2000), "the test window appears");
	key(KEY_LEFTSHIFT, true); tap(KEY_SYSRQ); key(KEY_LEFTSHIFT, false);
	check(wait_line("key\tscreenshot-window\t437,194 406x335", 1000), "Shift+Print sends the active window's frame (x,y w×h)");

	/* ── a menu: open, click inside (stays), click outside (closes) ── */
	struct wl_surface *ps = wl_compositor_create_surface(compositor);
	struct xdg_surface *px = xdg_wm_base_get_xdg_surface(wm, ps);
	xdg_surface_add_listener(px, &surf_listener, NULL);
	struct xdg_positioner *pos = xdg_wm_base_create_positioner(wm);
	xdg_positioner_set_size(pos, 150, 100);
	xdg_positioner_set_anchor_rect(pos, 20, 20, 1, 1);
	xdg_positioner_set_anchor(pos, XDG_POSITIONER_ANCHOR_TOP_LEFT);
	xdg_positioner_set_gravity(pos, XDG_POSITIONER_GRAVITY_BOTTOM_RIGHT);
	struct xdg_popup *pp = xdg_surface_get_popup(px, wx, pos);
	xdg_popup_add_listener(pp, &popup_listener, NULL);
	configured = false;
	wl_surface_commit(ps);
	for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
	struct wl_buffer *pb = make_buffer(150, 100, 0xff333333);
	wl_surface_attach(ps, pb, 0, 0);
	wl_surface_commit(ps);
	settle(200);
	click_at(440 + 20 + 75, 226 + 20 + 50);                 /* inside the menu */
	check(!wait_for(&popup_done, true, 400), "a click inside a menu leaves it open");
	click_at(440 + 300, 226 + 250);                         /* on the window, outside the menu */
	check(wait_for(&popup_done, true, 1000), "a click outside a menu closes it (popup_done)");
	xdg_popup_destroy(pp); xdg_surface_destroy(px); wl_surface_destroy(ps); xdg_positioner_destroy(pos);
	settle(200);

	/* ── title bar ── */
	if (deco_mgr) {
		move_to(600, 210); button(true); button(false); settle(60); button(true); button(false);
		check(wait_for(&maximized, true, 1000), "double-clicking the title bar maximises");
		click_at(1280 - 46 - 23, 16);                            /* maximise button of the maximised frame */
		check(wait_for(&maximized, false, 1000), "the maximise button restores it");
		settle(200);
		click_at(437 + 406 - 23, 194 + 16);                      /* close button of the restored frame */
		check(wait_for(&closed, true, 1000), "the close button asks the window to close");
	}

	printf("%d passed, %d failed\n", passed, failed);
	close(ipc);
	wl_display_disconnect(dpy);
	return failed ? 1 : 0;
}
