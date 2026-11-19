#ifndef REDSTAR_NESTED_H
#define REDSTAR_NESTED_H
#include <stdbool.h>
struct wl_event_loop;
struct wlr_backend;
struct wlr_output;

/* On a Wayland host (WAYLAND_DISPLAY set, WLR_BACKENDS not): wlroots' Wayland backend without an output yet. */
struct wlr_backend *nested_backend_create(struct wl_event_loop *loop);
/* After the backend started: the host window (full screen unless asked not to); on_close when the host closes it. */
bool nested_create_output(bool fullscreen, void (*on_close)(void *), void *data);
/* The size the host gave our window so far (0 × 0 when not nested on Wayland), for the output's first commit. */
void nested_initial_size(int *w, int *h);
/* On an X11 host: titles the wlroots window, sizes it to the screen and asks the host for full screen. */
void nested_x11_output(struct wl_event_loop *loop, struct wlr_output *o, const char *host_display, bool fullscreen);
/* A window on another desktop (Wayland or X11 host) rather than a real screen. */
bool nested_output(struct wlr_output *o);
#endif
