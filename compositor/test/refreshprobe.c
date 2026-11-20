// SPDX-License-Identifier: GPL-3.0-or-later
// Run inside a nested redstar-comp (a window on another desktop): watches the screen's mode for a few seconds,
// through the host window going full screen, and fails if clients are ever told 0 Hz — GTK 4 programs divide by
// it and crash (Red Star Online closing seconds after it opened).
#include <poll.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <wayland-client.h>

static int min_mhz = -1, max_mhz = -1, events = 0, last_w = 0, last_h = 0;

static void geometry(void *d, struct wl_output *o, int32_t x, int32_t y, int32_t pw, int32_t ph, int32_t sp, const char *mk,
	const char *md, int32_t tr) { (void)d; (void)o; (void)x; (void)y; (void)pw; (void)ph; (void)sp; (void)mk; (void)md; (void)tr; }
static void mode(void *d, struct wl_output *o, uint32_t flags, int32_t w, int32_t h, int32_t refresh)
{
	(void)d; (void)o;
	if (!(flags & WL_OUTPUT_MODE_CURRENT)) return;
	events++;
	last_w = w; last_h = h;
	if (min_mhz < 0 || refresh < min_mhz) min_mhz = refresh;
	if (refresh > max_mhz) max_mhz = refresh;
}
static void done(void *d, struct wl_output *o) { (void)d; (void)o; }
static void scale(void *d, struct wl_output *o, int32_t f) { (void)d; (void)o; (void)f; }
static const struct wl_output_listener out_listener = { geometry, mode, done, scale, NULL, NULL };

static void global(void *d, struct wl_registry *r, uint32_t name, const char *iface, uint32_t v)
{
	(void)d; (void)v;
	if (!strcmp(iface, wl_output_interface.name))
		wl_output_add_listener(wl_registry_bind(r, name, &wl_output_interface, 2), &out_listener, NULL);
}
static void global_remove(void *d, struct wl_registry *r, uint32_t n) { (void)d; (void)r; (void)n; }
static const struct wl_registry_listener reg = { global, global_remove };

static double now_ms(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec * 1e3 + t.tv_nsec / 1e6; }

int main(void)
{
	struct wl_display *dpy = wl_display_connect(NULL);
	if (!dpy) { printf("FAIL no Wayland display inside the nested redstar-comp\n"); return 1; }
	wl_registry_add_listener(wl_display_get_registry(dpy), &reg, NULL);
	wl_display_roundtrip(dpy);
	wl_display_roundtrip(dpy);
	double end = now_ms() + 3000;
	while (now_ms() < end) {
		wl_display_flush(dpy);
		struct pollfd p = { wl_display_get_fd(dpy), POLLIN, 0 };
		if (poll(&p, 1, 50) > 0) wl_display_dispatch(dpy);
		else wl_display_dispatch_pending(dpy);
	}
	printf("nested screen: %dx%d, %d mode event%s, refresh %d..%d mHz\n", last_w, last_h, events, events == 1 ? "" : "s", min_mhz, max_mhz);
	int ok = events > 0 && min_mhz > 0;
	printf("%s a nested screen never tells programs 0 Hz (GTK 4 divides by it)\n", ok ? "PASS" : "FAIL");
	wl_display_disconnect(dpy);
	return ok ? 0 : 1;
}
