/*
 * nativetest — a native Wayland window under redstar-comp, checked through the shell's connection
 * ($REDSTAR_COMP_SOCKET): it's listed with its app id and title, minimise / activate / maximise / restore / close
 * from the taskbar reach it, it leaves the list when it closes, and its position is remembered.
 * Run by run-tests.sh after comptest (the X11 checks).
 */
#define _GNU_SOURCE
#include <errno.h>
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
#include "xdg-shell-client-protocol.h"
#include "xdg-decoration-unstable-v1-client-protocol.h"

static struct wl_display *dpy;
static struct wl_compositor *compositor;
static struct wl_shm *shm;
static struct xdg_wm_base *wm;
static struct zxdg_decoration_manager_v1 *deco_mgr;
static bool configured, closed, maximized_state, server_side;
static int conf_w, conf_h;
static int ipc = -1, passed, failed;
static char globals[8192];               /* every global's interface name, space-separated */
static char ipcbuf[65536];
static size_t ipclen;

static void check(bool ok, const char *what)
{
	printf("%s %s\n", ok ? "PASS" : "FAIL", what);
	if (ok) passed++; else failed++;
	fflush(stdout);
}

static void ping(void *d, struct xdg_wm_base *b, uint32_t serial) { (void)d; xdg_wm_base_pong(b, serial); }
static const struct xdg_wm_base_listener wm_listener = { .ping = ping };

static void surf_configure(void *d, struct xdg_surface *s, uint32_t serial) { (void)d; xdg_surface_ack_configure(s, serial); configured = true; }
static const struct xdg_surface_listener surf_listener = { .configure = surf_configure };

static void top_configure(void *d, struct xdg_toplevel *t, int32_t w, int32_t h, struct wl_array *states)
{
	(void)d; (void)t;
	conf_w = w; conf_h = h;
	maximized_state = false;
	uint32_t *st;
	wl_array_for_each(st, states) if (*st == XDG_TOPLEVEL_STATE_MAXIMIZED) maximized_state = true;
}
static void top_close(void *d, struct xdg_toplevel *t) { (void)d; (void)t; closed = true; }
static const struct xdg_toplevel_listener top_listener = { .configure = top_configure, .close = top_close };

static void deco_configure(void *d, struct zxdg_toplevel_decoration_v1 *z, uint32_t mode)
{
	(void)d; (void)z;
	server_side = mode == ZXDG_TOPLEVEL_DECORATION_V1_MODE_SERVER_SIDE;
}
static const struct zxdg_toplevel_decoration_v1_listener deco_listener = { .configure = deco_configure };

/* the screen's mode, as clients see it: GTK 4 divides by its refresh rate, so it must not be 0 */
static int output_refresh = -1;
static void out_geometry(void *d, struct wl_output *o, int32_t x, int32_t y, int32_t pw, int32_t ph, int32_t sp, const char *mk,
	const char *md, int32_t tr) { (void)d; (void)o; (void)x; (void)y; (void)pw; (void)ph; (void)sp; (void)mk; (void)md; (void)tr; }
static void out_mode(void *d, struct wl_output *o, uint32_t flags, int32_t w, int32_t h, int32_t refresh)
{
	(void)d; (void)o; (void)w; (void)h;
	if (flags & WL_OUTPUT_MODE_CURRENT) output_refresh = refresh;
}
static void out_done(void *d, struct wl_output *o) { (void)d; (void)o; }
static void out_scale(void *d, struct wl_output *o, int32_t f) { (void)d; (void)o; (void)f; }
static const struct wl_output_listener out_listener = { .geometry = out_geometry, .mode = out_mode, .done = out_done, .scale = out_scale };

static void global(void *d, struct wl_registry *r, uint32_t name, const char *iface, uint32_t v)
{
	(void)d; (void)v;
	size_t gl = strlen(globals);
	snprintf(globals + gl, sizeof globals - gl, " %s ", iface);
	if (!strcmp(iface, wl_compositor_interface.name)) compositor = wl_registry_bind(r, name, &wl_compositor_interface, 4);
	else if (!strcmp(iface, wl_shm_interface.name)) shm = wl_registry_bind(r, name, &wl_shm_interface, 1);
	else if (!strcmp(iface, xdg_wm_base_interface.name)) {
		wm = wl_registry_bind(r, name, &xdg_wm_base_interface, 1);
		xdg_wm_base_add_listener(wm, &wm_listener, NULL);
	} else if (!strcmp(iface, zxdg_decoration_manager_v1_interface.name))
		deco_mgr = wl_registry_bind(r, name, &zxdg_decoration_manager_v1_interface, 1);
	else if (!strcmp(iface, wl_output_interface.name) && output_refresh < 0) {
		struct wl_output *o = wl_registry_bind(r, name, &wl_output_interface, 2);
		wl_output_add_listener(o, &out_listener, NULL);
	}
}
static void global_remove(void *d, struct wl_registry *r, uint32_t n) { (void)d; (void)r; (void)n; }
static const struct wl_registry_listener reg_listener = { .global = global, .global_remove = global_remove };

static struct wl_buffer *make_buffer(int w, int h)
{
	int stride = w * 4, size = stride * h;
	int fd = memfd_create("nativetest", MFD_CLOEXEC);
	if (fd < 0 || ftruncate(fd, size) < 0) return NULL;
	uint32_t *px = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
	for (int i = 0; i < w * h; i++) px[i] = 0xff2e7d32;
	munmap(px, size);
	struct wl_shm_pool *pool = wl_shm_create_pool(shm, fd, size);
	struct wl_buffer *b = wl_shm_pool_create_buffer(pool, 0, w, h, stride, WL_SHM_FORMAT_ARGB8888);
	wl_shm_pool_destroy(pool);
	close(fd);
	return b;
}

static double now_ms(void)
{
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	return t.tv_sec * 1000.0 + t.tv_nsec / 1e6;
}

/* runs both connections for up to ms; returns the first IPC line containing `want` (copied into out), or false */
static bool wait_line(const char *want, char *out, size_t outsize, int ms)
{
	double end = now_ms() + ms;
	while (now_ms() < end) {
		char *nl;
		while ((nl = memchr(ipcbuf, '\n', ipclen))) {
			*nl = 0;
			bool hit = strstr(ipcbuf, want) != NULL;
			if (hit && out && outsize > 0) {
				size_t n = strlen(ipcbuf);
				if (n >= outsize) n = outsize - 1;
				memcpy(out, ipcbuf, n);
				out[n] = 0;
			}
			size_t used = (size_t)(nl - ipcbuf) + 1;
			memmove(ipcbuf, ipcbuf + used, ipclen - used);
			ipclen -= used;
			if (hit) return true;
		}
		wl_display_flush(dpy);
		struct pollfd p[2] = { { wl_display_get_fd(dpy), POLLIN, 0 }, { ipc, POLLIN, 0 } };
		if (poll(p, 2, 50) > 0) {
			if (p[0].revents & POLLIN) { if (wl_display_dispatch(dpy) < 0) return false; }
			if (p[1].revents & POLLIN) {
				ssize_t n = read(ipc, ipcbuf + ipclen, sizeof ipcbuf - 1 - ipclen);
				if (n <= 0) return false;
				ipclen += (size_t)n;
			}
		}
		wl_display_dispatch_pending(dpy);
	}
	return false;
}

/* waits for a Wayland-side condition while keeping the IPC drained */
static bool wait_for(bool *flag, bool value, int ms)
{
	double end = now_ms() + ms;
	while (now_ms() < end && *flag != value) wait_line("\x01never", NULL, 0, 50);
	return *flag == value;
}

static void cmd(const char *verb, unsigned long long id)
{
	char line[128];
	int n = snprintf(line, sizeof line, "%s\t%llu\n", verb, id);
	if (write(ipc, line, (size_t)n) < 0) perror("write");
}

int main(void)
{
	dpy = wl_display_connect(NULL);
	if (!dpy) { printf("FAIL could not connect to redstar-comp (Wayland)\n"); return 1; }
	struct wl_registry *reg = wl_display_get_registry(dpy);
	wl_registry_add_listener(reg, &reg_listener, NULL);
	wl_display_roundtrip(dpy);
	if (!compositor || !shm || !wm) { printf("FAIL redstar-comp lacks wl_compositor / wl_shm / xdg_wm_base\n"); return 1; }
	check(deco_mgr != NULL, "redstar-comp offers server-side decorations (xdg-decoration)");

	const char *path = getenv("REDSTAR_COMP_SOCKET");
	check(path && *path, "REDSTAR_COMP_SOCKET is set for programs redstar-comp starts");
	if (!path) return 1;
	ipc = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
	struct sockaddr_un addr = { .sun_family = AF_UNIX };
	snprintf(addr.sun_path, sizeof addr.sun_path, "%s", path);
	bool connected = connect(ipc, (struct sockaddr *)&addr, sizeof addr) == 0;
	check(connected, "the shell's connection accepts a client");
	if (!connected) return 1;
	if (write(ipc, "hello\n", 6) < 0) return 1;
	check(wait_line("active\t", NULL, 0, 2000), "hello is answered (active window)");

	/* the window */
	struct wl_surface *surf = wl_compositor_create_surface(compositor);
	struct xdg_surface *xs = xdg_wm_base_get_xdg_surface(wm, surf);
	xdg_surface_add_listener(xs, &surf_listener, NULL);
	struct xdg_toplevel *top = xdg_surface_get_toplevel(xs);
	xdg_toplevel_add_listener(top, &top_listener, NULL);
	xdg_toplevel_set_app_id(top, "comptest-native");
	xdg_toplevel_set_title(top, "Native test");
	struct zxdg_toplevel_decoration_v1 *deco = NULL;
	if (deco_mgr) {
		deco = zxdg_decoration_manager_v1_get_toplevel_decoration(deco_mgr, top);
		zxdg_toplevel_decoration_v1_add_listener(deco, &deco_listener, NULL);
		zxdg_toplevel_decoration_v1_set_mode(deco, ZXDG_TOPLEVEL_DECORATION_V1_MODE_SERVER_SIDE);
	}
	wl_surface_commit(surf);
	for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
	check(configured, "the window gets its first configure");
	check(!deco_mgr || server_side, "it gets a Red Star title bar (server-side mode)");
	struct wl_buffer *buf = make_buffer(400, 300);
	wl_surface_attach(surf, buf, 0, 0);
	wl_surface_commit(surf);

	char line[1024];
	bool listed = wait_line("comptest-native\tNative test", line, sizeof line, 3000);
	check(listed, "the shell is told about it (app id and title)");
	if (!listed) return 1;
	unsigned long long id = strtoull(line + 7, NULL, 10);
	check(id >= (1ULL << 32), "native ids don't collide with X11 window ids");
	check(wait_line("stack\t", line, sizeof line, 2000) || strstr(line, "stack"), "a stacking order is sent");

	xdg_toplevel_set_title(top, "Native test renamed");
	wl_surface_commit(surf);
	check(wait_line("\tNative test renamed", NULL, 0, 2000), "a new title reaches the shell");

	cmd("minimize", id);
	check(wait_line("\tm\t", NULL, 0, 2000), "the taskbar's minimise hides it (flag m)");
	cmd("activate", id);
	snprintf(line, sizeof line, "active\t%llu", id);
	check(wait_line(line, NULL, 0, 2000), "activating it brings it back as the active window");
	cmd("maximize", id);
	check(wait_line("\tM\t", NULL, 0, 2000), "the taskbar's maximise is reported (flag M)");
	check(wait_for(&maximized_state, true, 2000), "the window itself is told it's maximised");
	cmd("restore", id);
	check(wait_for(&maximized_state, false, 2000), "restore un-maximises it");
	wl_surface_attach(surf, buf, 0, 0);
	wl_surface_commit(surf);
	wait_line("\x01never", NULL, 0, 300);

	/* virtual desktops */
	char want[128];
	if (write(ipc, "desktops\t3\n", 11) < 0) return 1;
	check(wait_line("desktops\t3\t0", NULL, 0, 2000), "the shell can ask for three desktops (desktop 1 shown)");
	snprintf(line, sizeof line, "to-desktop\t2\t%llu\n", id);
	if (write(ipc, line, strlen(line)) < 0) return 1;
	snprintf(want, sizeof want, "on-desktop\t%llu\t2", id);
	check(wait_line(want, NULL, 0, 2000), "a window moved to desktop 3 is reported there");
	snprintf(want, sizeof want, "active\t%llu", id);
	check(!wait_line(want, NULL, 0, 300), "and it's no longer the active window");
	cmd("activate", id);
	check(wait_line("desktops\t3\t2", NULL, 0, 2000), "activating it shows desktop 3");
	check(wait_line(want, NULL, 0, 2000), "and makes it the active window there");
	if (write(ipc, "desktop\t0\n", 10) < 0) return 1;
	check(wait_line("desktops\t3\t0", NULL, 0, 2000), "switching back to desktop 1");
	if (write(ipc, "desktops\t1\n", 11) < 0) return 1;
	snprintf(want, sizeof want, "on-desktop\t%llu\t0", id);
	check(wait_line(want, NULL, 0, 2000), "going back to one desktop brings its window along");
	check(wait_line("desktops\t1\t0", NULL, 0, 2000), "one desktop left");

	/* night light: the virtual test screen has no gamma ramp, which must be said rather than ignored */
	if (write(ipc, "nightlight\t1\t0.8\t0.6\n", 21) < 0) return 1;
	bool night = wait_line("nightlight\t", line, sizeof line, 2000);
	check(night, "night light is answered with how many screens took it");
	if (night) printf("     (%s: tinted, unable)\n", line);
	if (write(ipc, "nightlight\t1\t1\t1\n", 17) < 0) return 1;
	check(wait_line("nightlight\t", NULL, 0, 2000), "and turned off again");

	cmd("close", id);
	check(wait_for(&closed, true, 2000), "the taskbar's close asks the window to close");

	if (deco) zxdg_toplevel_decoration_v1_destroy(deco);
	xdg_toplevel_destroy(top);
	xdg_surface_destroy(xs);
	wl_surface_destroy(surf);
	snprintf(line, sizeof line, "gone\t%llu", id);
	check(wait_line(line, NULL, 0, 2000), "it leaves the shell's list when it closes");

	/* an app that draws its own title bar (like Chrome) gets no second one */
	if (deco_mgr) {
		struct wl_surface *s2 = wl_compositor_create_surface(compositor);
		struct xdg_surface *x2 = xdg_wm_base_get_xdg_surface(wm, s2);
		configured = false;
		xdg_surface_add_listener(x2, &surf_listener, NULL);
		struct xdg_toplevel *t2 = xdg_surface_get_toplevel(x2);
		xdg_toplevel_add_listener(t2, &top_listener, NULL);
		xdg_toplevel_set_app_id(t2, "comptest-csd");
		struct zxdg_toplevel_decoration_v1 *d2 = zxdg_decoration_manager_v1_get_toplevel_decoration(deco_mgr, t2);
		server_side = true;
		zxdg_toplevel_decoration_v1_add_listener(d2, &deco_listener, NULL);
		zxdg_toplevel_decoration_v1_set_mode(d2, ZXDG_TOPLEVEL_DECORATION_V1_MODE_CLIENT_SIDE);
		wl_surface_commit(s2);
		for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
		check(configured && !server_side, "an app that draws its own title bar keeps it (client-side mode)");
		zxdg_toplevel_decoration_v1_destroy(d2);
		xdg_toplevel_destroy(t2);
		xdg_surface_destroy(x2);
		wl_surface_destroy(s2);
		wl_display_roundtrip(dpy);
	}

	/* display settings through the shell's connection */
	if (write(ipc, "outputs\n", 8) < 0) return 1;
	bool got = wait_line("output\tHEADLESS-", line, sizeof line, 2000);
	check(got, "outputs lists the screen");
	check(got && strstr(line, "\t1\t") != NULL, "the screen is on");
	check(wait_line("outputs-end", NULL, 0, 2000), "the list ends with outputs-end");
	char name[64] = "HEADLESS-1";
	if (got) sscanf(line + 7, "%63[^\t]", name);
	char req[256];
	int n = snprintf(req, sizeof req, "output-set\t%s\t1\t1600x900@0\t0\t0\t2\t0\n", name);
	if (write(ipc, req, (size_t)n) < 0) return 1;
	check(wait_line("output-ok\t", NULL, 0, 2000), "a new size and scale are accepted");
	if (write(ipc, "outputs\n", 8) < 0) return 1;
	got = wait_line("output\tHEADLESS-", line, sizeof line, 2000);
	check(got && strstr(line, "1600x900") && strstr(line, "\t2.000\t"), "and reported back (1600×900 at 200%)");
	wait_line("outputs-end", NULL, 0, 1000);
	n = snprintf(req, sizeof req, "output-set\t%s\t0\t1600x900@0\t0\t0\t1\t0\n", name);
	if (write(ipc, req, (size_t)n) < 0) return 1;
	check(wait_line("output-failed\t", line, sizeof line, 2000) && strstr(line, "stay on"), "the last screen can't be turned off");
	n = snprintf(req, sizeof req, "output-set\t%s\t1\t1280x720@0\t0\t0\t1\t0\n", name);
	if (write(ipc, req, (size_t)n) < 0) return 1;
	wait_line("output-ok\t", NULL, 0, 2000);
	if (write(ipc, "outputs-save\n", 13) < 0) return 1;
	wait_line("\x01never", NULL, 0, 300);

	/* the shell's desktop, drawn natively on Wayland: it says which window is the desktop and how much the
	 * taskbar takes, then that window fills the screen, isn't listed on the taskbar and moves the work area */
	{
		char role[256];
		int rn = snprintf(role, sizeof role, "role\tdesktop\t%d\tcomptest-desktop\t0\t0\t0\t40\n", (int)getpid());
		if (write(ipc, role, (size_t)rn) < 0) return 1;
		struct wl_surface *s3 = wl_compositor_create_surface(compositor);
		struct xdg_surface *x3 = xdg_wm_base_get_xdg_surface(wm, s3);
		configured = false;
		xdg_surface_add_listener(x3, &surf_listener, NULL);
		struct xdg_toplevel *t3 = xdg_surface_get_toplevel(x3);
		xdg_toplevel_add_listener(t3, &top_listener, NULL);
		xdg_toplevel_set_title(t3, "comptest-desktop");
		xdg_toplevel_set_fullscreen(t3, NULL);          /* like Red Star's desktop window */
		wl_surface_commit(s3);
		for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
		struct wl_buffer *b3 = make_buffer(320, 200);
		wl_surface_attach(s3, b3, 0, 0);
		wl_surface_commit(s3);
		bool listed3 = wait_line("comptest-desktop", NULL, 0, 600);
		check(!listed3, "the shell's desktop window isn't put on the taskbar");
		double end = now_ms() + 2000;
		while (now_ms() < end && !(conf_w == 1280 && conf_h == 720)) wait_line("\x01never", NULL, 0, 50);
		check(conf_w == 1280 && conf_h == 720, "it's sized to fill the screen");

		/* with the desktop's taskbar space reserved (sent before it appeared), a maximised window stops above it */
		if (deco_mgr) {
			struct wl_surface *s4 = wl_compositor_create_surface(compositor);
			struct xdg_surface *x4 = xdg_wm_base_get_xdg_surface(wm, s4);
			configured = false;
			xdg_surface_add_listener(x4, &surf_listener, NULL);
			struct xdg_toplevel *t4 = xdg_surface_get_toplevel(x4);
			xdg_toplevel_add_listener(t4, &top_listener, NULL);
			xdg_toplevel_set_app_id(t4, "comptest-max");
			xdg_toplevel_set_title(t4, "Maximise test");
			struct zxdg_toplevel_decoration_v1 *d4 = zxdg_decoration_manager_v1_get_toplevel_decoration(deco_mgr, t4);
			zxdg_toplevel_decoration_v1_set_mode(d4, ZXDG_TOPLEVEL_DECORATION_V1_MODE_SERVER_SIDE);
			wl_surface_commit(s4);
			for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
			struct wl_buffer *b4 = make_buffer(400, 300);
			wl_surface_attach(s4, b4, 0, 0);
			wl_surface_commit(s4);
			char l4[1024];
			if (wait_line("comptest-max\tMaximise test", l4, sizeof l4, 2000)) {
				unsigned long long id4 = strtoull(l4 + 7, NULL, 10);
				maximized_state = false;
				cmd("maximize", id4);
				wait_for(&maximized_state, true, 2000);
				/* 720 high, minus the 40 px taskbar, minus the 32 px title bar */
				check(maximized_state && conf_w == 1280 && conf_h == 720 - 40 - 32,
					"a maximised window stops above the taskbar the desktop reserved");
				if (!(conf_h == 720 - 40 - 32)) printf("  (it was told %dx%d)\n", conf_w, conf_h);
			} else check(false, "a maximised window stops above the taskbar the desktop reserved");
			zxdg_toplevel_decoration_v1_destroy(d4);
			xdg_toplevel_destroy(t4);
			xdg_surface_destroy(x4);
			wl_surface_destroy(s4);
			wl_buffer_destroy(b4);
			wl_display_roundtrip(dpy);
		}
		/* Red Star opens its app windows as children of the desktop: they are ordinary windows (taskbar, Alt+Tab) */
		{
			struct wl_surface *s5 = wl_compositor_create_surface(compositor);
			struct xdg_surface *x5 = xdg_wm_base_get_xdg_surface(wm, s5);
			configured = false;
			xdg_surface_add_listener(x5, &surf_listener, NULL);
			struct xdg_toplevel *t5 = xdg_surface_get_toplevel(x5);
			xdg_toplevel_add_listener(t5, &top_listener, NULL);
			xdg_toplevel_set_parent(t5, t3);
			xdg_toplevel_set_app_id(t5, "comptest-child");
			xdg_toplevel_set_title(t5, "Child window");
			wl_surface_commit(s5);
			for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
			struct wl_buffer *b5 = make_buffer(300, 200);
			wl_surface_attach(s5, b5, 0, 0);
			wl_surface_commit(s5);
			check(wait_line("comptest-child\tChild window", NULL, 0, 2000),
				"a window whose parent is the shell's desktop is a normal window (taskbar, Alt+Tab)");
			xdg_toplevel_destroy(t5); xdg_surface_destroy(x5); wl_surface_destroy(s5); wl_buffer_destroy(b5);
			wl_display_roundtrip(dpy);
		}
		/* a frameless window of the shell (its Alt+Tab switcher): no frame, not on the taskbar */
		{
			char bare[256];
			int bn = snprintf(bare, sizeof bare, "role\tbare\t%d\tcomptest-bare\t0\t0\t0\t0\n", (int)getpid());
			if (write(ipc, bare, (size_t)bn) < 0) return 1;
			wait_line("\x01never", NULL, 0, 200);
			struct wl_surface *s6 = wl_compositor_create_surface(compositor);
			struct xdg_surface *x6 = xdg_wm_base_get_xdg_surface(wm, s6);
			configured = false;
			xdg_surface_add_listener(x6, &surf_listener, NULL);
			struct xdg_toplevel *t6 = xdg_surface_get_toplevel(x6);
			xdg_toplevel_add_listener(t6, &top_listener, NULL);
			xdg_toplevel_set_app_id(t6, "comptest-bare");
			xdg_toplevel_set_title(t6, "comptest-bare");
			wl_surface_commit(s6);
			for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
			struct wl_buffer *b6 = make_buffer(300, 120);
			wl_surface_attach(s6, b6, 0, 0);
			wl_surface_commit(s6);
			check(!wait_line("comptest-bare", NULL, 0, 600), "a frameless shell window isn't put on the taskbar");
			xdg_toplevel_destroy(t6); xdg_surface_destroy(x6); wl_surface_destroy(s6); wl_buffer_destroy(b6);
			wl_display_roundtrip(dpy);
		}
		/* the shell's lock screen: frameless and full-screen. Its role is known when it appears (A), or arrives
		 * after it appeared (B: Red Star names its windows with a token title first); neither may lose the full screen */
		for (int round = 0; round < 2; round++) {
			const char *title = round == 0 ? "comptest-lock" : "comptest-lock2";
			char lock[256];
			int ln = snprintf(lock, sizeof lock, "role\tbare\t%d\t%s\t0\t0\t0\t0\n", (int)getpid(), title);
			if (write(ipc, lock, (size_t)ln) < 0) return 1;
			wait_line("\x01never", NULL, 0, 200);
			struct wl_surface *s7 = wl_compositor_create_surface(compositor);
			struct xdg_surface *x7 = xdg_wm_base_get_xdg_surface(wm, s7);
			configured = false;
			xdg_surface_add_listener(x7, &surf_listener, NULL);
			struct xdg_toplevel *t7 = xdg_surface_get_toplevel(x7);
			xdg_toplevel_add_listener(t7, &top_listener, NULL);
			xdg_toplevel_set_title(t7, round == 0 ? title : "comptest-lock-token");
			xdg_toplevel_set_fullscreen(t7, NULL);              /* asked before it appears, like Avalonia does */
			wl_surface_commit(s7);
			for (int i = 0; i < 20 && !configured; i++) wl_display_roundtrip(dpy);
			conf_w = conf_h = 0;
			struct wl_buffer *b7 = make_buffer(420, 300);       /* its own size until told otherwise */
			wl_surface_attach(s7, b7, 0, 0);
			wl_surface_commit(s7);
			if (round == 1) {
				wait_line("\x01never", NULL, 0, 200);
				xdg_toplevel_set_title(t7, title);              /* now the role applies */
				wl_surface_commit(s7);
			}
			double end7 = now_ms() + 2000;
			while (now_ms() < end7 && !(conf_w == 1280 && conf_h == 720)) wait_line("\x01never", NULL, 0, 50);
			check(conf_w == 1280 && conf_h == 720, round == 0
				? "a frameless shell window asking for full screen (the lock screen) fills the screen"
				: "it stays full-screen when its role arrives after it appeared");
			if (round == 0) check(!wait_line(title, NULL, 0, 300), "the lock screen isn't put on the taskbar");
			xdg_toplevel_destroy(t7); xdg_surface_destroy(x7); wl_surface_destroy(s7); wl_buffer_destroy(b7);
			wl_display_roundtrip(dpy);
		}

		if (write(ipc, "outputs\n", 8) < 0) return 1;
		wait_line("outputs-end", NULL, 0, 1000);
		xdg_toplevel_destroy(t3);
		xdg_surface_destroy(x3);
		wl_surface_destroy(s3);
		wl_buffer_destroy(b3);
		wl_display_roundtrip(dpy);
	}

	/* System Settings changes the pointer's theme and size while running: an unknown theme falls back to the default
	 * one, and the compositor carries on answering */
	{
		const char *c1 = "cursor\tno-such-cursor-theme\t48\n", *c2 = "cursor\t\t24\n";
		if (write(ipc, c1, strlen(c1)) < 0 || write(ipc, c2, strlen(c2)) < 0) return 1;
		if (write(ipc, "outputs\n", 8) < 0) return 1;
		check(wait_line("outputs-end", NULL, 0, 2000), "a new pointer theme and size are taken while running");
	}

	/* the keyboard layout changes while running; a layout that doesn't exist leaves the old one in place */
	{
		const char *a = "keymap\tus,de\t,nodeadkeys\tgrp:alt_shift_toggle\nkeymap\tno-such-layout\t\t\noutputs\n";
		if (write(ipc, a, strlen(a)) < 0) return 1;
		check(wait_line("outputs-end", NULL, 0, 3000), "keyboard layouts are taken while running");
	}

	/* idle: held off while something inhibits it, then "idle" after the timeout; screens off and on again */
	{
		const char *a = "inhibit\t1\nidle-timeout\t1\n";
		if (write(ipc, a, strlen(a)) < 0) return 1;
		check(!wait_line("idle-start", NULL, 0, 2500), "no idle while something holds it off (a video playing)");
		const char *b = "inhibit\t0\n";
		if (write(ipc, b, strlen(b)) < 0) return 1;
		check(wait_line("idle-start", NULL, 0, 4000), "\"idle-start\" after a second without input");
		const char *c = "dpms\toff\ndpms\ton\nidle-timeout\t0\noutputs\n";
		if (write(ipc, c, strlen(c)) < 0) return 1;
		check(wait_line("outputs-end", NULL, 0, 3000), "screens go off and on again, and the compositor carries on");
	}

	/* two of the shell's windows with the same title, told apart by the tokens it names them with */
	{
		struct wl_surface *sa = wl_compositor_create_surface(compositor), *sb = wl_compositor_create_surface(compositor);
		struct xdg_surface *xa = xdg_wm_base_get_xdg_surface(wm, sa), *xb = xdg_wm_base_get_xdg_surface(wm, sb);
		xdg_surface_add_listener(xa, &surf_listener, NULL);
		xdg_surface_add_listener(xb, &surf_listener, NULL);
		struct xdg_toplevel *ta = xdg_surface_get_toplevel(xa), *tb = xdg_surface_get_toplevel(xb);
		xdg_toplevel_add_listener(ta, &top_listener, NULL);
		xdg_toplevel_add_listener(tb, &top_listener, NULL);
		xdg_toplevel_set_app_id(ta, "comptest-twin");
		xdg_toplevel_set_app_id(tb, "comptest-twin");
		xdg_toplevel_set_title(ta, "Same title");
		xdg_toplevel_set_title(tb, "Same title");
		configured = false;
		wl_surface_commit(sa);
		wl_surface_commit(sb);
		for (int i = 0; i < 20; i++) wl_display_roundtrip(dpy);
		struct wl_buffer *bt = make_buffer(320, 200);
		wl_surface_attach(sa, bt, 0, 0); wl_surface_commit(sa);
		wl_surface_attach(sb, bt, 0, 0); wl_surface_commit(sb);
		wait_line("\x01never", NULL, 0, 300);
		/* the shell names them (the marker title), then puts the real title back */
		xdg_toplevel_set_title(ta, "\xE2\x81\xA3rs:twinA");
		xdg_toplevel_set_title(tb, "\xE2\x81\xA3rs:twinB");
		wl_display_flush(dpy);
		unsigned long long ida = 0, idb = 0;
		bool marker_shown = false;
		double end = now_ms() + 3000;
		char tl[1024];
		while ((!ida || !idb) && now_ms() < end) {
			if (!wait_line("\t", tl, sizeof tl, 200)) continue;
			if (strstr(tl, "rs:twin") && strncmp(tl, "token\t", 6) != 0) marker_shown = true;
			if (!strncmp(tl, "token\ttwinA\t", 12)) ida = strtoull(tl + 12, NULL, 10);
			if (!strncmp(tl, "token\ttwinB\t", 12)) idb = strtoull(tl + 12, NULL, 10);
		}
		check(ida && idb && ida != idb, "two windows with the same title get their own ids from their tokens");
		xdg_toplevel_set_title(ta, "Same title");
		xdg_toplevel_set_title(tb, "Same title");
		wl_display_flush(dpy);
		wait_line("\x01never", NULL, 0, 300);
		check(!marker_shown, "the naming title is never shown or sent on as a title");
		char want[96];
		snprintf(want, sizeof want, "window\t%llu\tm\t", ida);
		cmd("minimize", ida);
		bool a_min = wait_line(want, NULL, 0, 2000);
		snprintf(want, sizeof want, "window\t%llu\tm\t", idb);
		bool b_min = wait_line(want, NULL, 0, 400);
		check(a_min && !b_min, "minimising one of them by its id leaves the other alone");
		/* a later "hello" (the shell restarting its connection) hears the tokens again */
		if (write(ipc, "hello\n", 6) < 0) return 1;
		snprintf(want, sizeof want, "token\ttwinB\t%llu", idb);
		check(wait_line(want, NULL, 0, 2000), "hello repeats the tokens");
		xdg_toplevel_destroy(ta); xdg_toplevel_destroy(tb);
		xdg_surface_destroy(xa); xdg_surface_destroy(xb);
		wl_surface_destroy(sa); wl_surface_destroy(sb);
		wl_buffer_destroy(bt);
		wl_display_roundtrip(dpy);
	}

	/* what screenshot, screen-sharing, display and clipboard tools look for */
	if (output_refresh <= 0) printf("  (screen refresh as sent: %d mHz)\n", output_refresh);
	check(output_refresh > 0, "the screen reports a refresh rate (GTK 4 apps divide by it)");
	check(strstr(globals, " ext_idle_notifier_v1 ") != NULL && strstr(globals, " zwp_idle_inhibit_manager_v1 ") != NULL,
	      "idle notification and inhibiting (screen lockers, video players)");
	check(strstr(globals, " wp_cursor_shape_manager_v1 ") != NULL, "cursor-shape (programs' pointers drawn at Red Star's size)");
	check(strstr(globals, " zwlr_screencopy_manager_v1 ") != NULL, "screencopy (grim, xdg-desktop-portal-wlr)");
	check(strstr(globals, " ext_image_copy_capture_manager_v1 ") != NULL, "ext-image-copy-capture (newer screen sharing)");
	check(strstr(globals, " zwlr_output_manager_v1 ") != NULL, "output management (wlr-randr, kanshi)");
	check(strstr(globals, " zwlr_data_control_manager_v1 ") != NULL, "data control (clipboard managers, wl-clipboard)");
	check(strstr(globals, " xdg_activation_v1 ") != NULL, "xdg-activation (apps brought to the front)");

	/* remembered position (only for framed windows, so only with server-side decorations) */
	const char *cfg = getenv("XDG_CONFIG_HOME");
	if (cfg && deco_mgr) {
		char file[512], content[4096] = "";
		snprintf(file, sizeof file, "%s/redstaros/window-positions", cfg);
		FILE *f = fopen(file, "r");
		if (f) { size_t n = fread(content, 1, sizeof content - 1, f); content[n] = 0; fclose(f); }
		check(strstr(content, "comptest-native ") != NULL, "its position is remembered for next time");
		snprintf(file, sizeof file, "%s/redstaros/outputs", cfg);
		content[0] = 0;
		f = fopen(file, "r");
		if (f) { size_t n2 = fread(content, 1, sizeof content - 1, f); content[n2] = 0; fclose(f); }
		check(strstr(content, "HEADLESS-") != NULL && strstr(content, " 1280 720 "), "the screen layout is saved for next time");
	}

	printf("%d passed, %d failed\n", passed, failed);
	close(ipc);
	wl_display_disconnect(dpy);
	return failed ? 1 : 0;
}
