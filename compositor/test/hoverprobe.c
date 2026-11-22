// SPDX-License-Identifier: GPL-3.0-or-later
/*
 * hoverprobe — moves a virtual pointer slowly along lines of the screen, the way a hand moves a mouse across the
 * taskbar or a menu, so a hover highlight that blinks can be caught in the shell's REDSTAR_TRACE_POINTER log.
 *   hoverprobe W H  x0,y0:x1,y1[:click]  …     each segment is walked in 2 px steps, 8 ms apart; ":click" clicks
 *                                              at its end (to open the Start menu, say)
 * The virtual-pointer protocol (wlroots' wlr-virtual-pointer-unstable-v1) is declared by hand, as in inputtest.
 */
#define _GNU_SOURCE
#include <linux/input-event-codes.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <wayland-client.h>

extern const struct wl_interface zwlr_virtual_pointer_v1_interface;
static const struct wl_interface *no_types[8];
static const struct wl_interface *vp_create_types[] = { &wl_seat_interface, &zwlr_virtual_pointer_v1_interface };
static const struct wl_message vp_manager_requests[] = { { "create_virtual_pointer", "?on", vp_create_types }, { "destroy", "", no_types } };
static const struct wl_message vp_requests[] = {
	{ "motion", "uff", no_types }, { "motion_absolute", "uuuuu", no_types }, { "button", "uuu", no_types },
	{ "axis", "uuf", no_types }, { "frame", "", no_types }, { "axis_source", "u", no_types }, { "axis_stop", "uu", no_types },
	{ "axis_discrete", "uufi", no_types }, { "destroy", "", no_types },
};
const struct wl_interface zwlr_virtual_pointer_v1_interface = { "zwlr_virtual_pointer_v1", 1, 9, vp_requests, 0, NULL };
static const struct wl_interface vp_manager_interface = { "zwlr_virtual_pointer_manager_v1", 1, 2, vp_manager_requests, 0, NULL };

static struct wl_display *dpy;
static struct wl_seat *seat;
static struct wl_proxy *vp_mgr, *vp;
static uint32_t W = 1280, H = 720;

static void global(void *d, struct wl_registry *r, uint32_t name, const char *iface, uint32_t v)
{
	(void)d; (void)v;
	if (!strcmp(iface, wl_seat_interface.name) && !seat) seat = wl_registry_bind(r, name, &wl_seat_interface, 1);
	else if (!strcmp(iface, vp_manager_interface.name)) vp_mgr = wl_registry_bind(r, name, &vp_manager_interface, 1);
}
static void global_remove(void *d, struct wl_registry *r, uint32_t n) { (void)d; (void)r; (void)n; }
static const struct wl_registry_listener reg_listener = { .global = global, .global_remove = global_remove };

static uint32_t stamp(void)
{
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	return (uint32_t)(t.tv_sec * 1000 + t.tv_nsec / 1000000);
}
static void pause_ms(int ms) { struct timespec t = { ms / 1000, (ms % 1000) * 1000000L }; nanosleep(&t, NULL); }

static void move_to(double x, double y)
{
	wl_proxy_marshal_flags(vp, 1, NULL, 1, 0, stamp(), (uint32_t)lround(x), (uint32_t)lround(y), W, H);
	wl_proxy_marshal_flags(vp, 4, NULL, 1, 0);
	wl_display_flush(dpy);
}
static void button(bool down)
{
	wl_proxy_marshal_flags(vp, 2, NULL, 1, 0, stamp(), (uint32_t)BTN_LEFT, down ? 1u : 0u);
	wl_proxy_marshal_flags(vp, 4, NULL, 1, 0);
	wl_display_flush(dpy);
}

int main(int argc, char **argv)
{
	if (argc < 4) { fprintf(stderr, "usage: hoverprobe W H x0,y0:x1,y1[:click] …\n"); return 2; }
	W = (uint32_t)atoi(argv[1]); H = (uint32_t)atoi(argv[2]);
	dpy = wl_display_connect(NULL);
	if (!dpy) { fprintf(stderr, "hoverprobe: no Wayland display\n"); return 1; }
	wl_registry_add_listener(wl_display_get_registry(dpy), &reg_listener, NULL);
	wl_display_roundtrip(dpy);
	if (!seat || !vp_mgr) { fprintf(stderr, "hoverprobe: no virtual pointers here\n"); return 1; }
	vp = wl_proxy_marshal_flags(vp_mgr, 0, &zwlr_virtual_pointer_v1_interface, 1, 0, seat, NULL);
	wl_display_roundtrip(dpy);
	for (int i = 3; i < argc; i++) {
		double x0, y0, x1, y1;
		char tail[16] = "";
		if (sscanf(argv[i], "%lf,%lf:%lf,%lf:%15s", &x0, &y0, &x1, &y1, tail) < 4) { fprintf(stderr, "hoverprobe: bad segment %s\n", argv[i]); return 2; }
		double len = hypot(x1 - x0, y1 - y0);
		int steps = len > 0 ? (int)ceil(len / 2) : 1;
		printf("segment %s\n", argv[i]);
		fflush(stdout);
		for (int s = 0; s <= steps; s++) {
			move_to(x0 + (x1 - x0) * s / steps, y0 + (y1 - y0) * s / steps);
			pause_ms(8);
		}
		pause_ms(300);
		if (!strcmp(tail, "click")) { button(true); pause_ms(40); button(false); pause_ms(1500); }
	}
	wl_display_roundtrip(dpy);
	return 0;
}
