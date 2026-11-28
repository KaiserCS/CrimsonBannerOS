/* SPDX-License-Identifier: GPL-3.0-or-later
 * The theme folder's GTK 3 style sheets, read by GTK 3 itself as a GTK 3 program would: any line it can't read, or
 * GTK 3's own Adwaita not found to start from, fails. Needs a display (it skips without one, unless
 * CRIMSON_GTK_REQUIRE_DISPLAY is set: CI runs it inside redstar-comp).
 *   gtk3-check THEME-FOLDER */
#include <gtk/gtk.h>
#include <stdlib.h>

static int problems;

static void on_error(GtkCssProvider *p, GtkCssSection *s, const GError *e, gpointer file) {
	(void)p;
	fprintf(stderr, "%s:%u: %s\n", (const char *)file, s ? gtk_css_section_get_start_line(s) + 1 : 0, e->message);
	problems++;
}

int main(int argc, char **argv) {
	if (argc < 2) { fprintf(stderr, "usage: gtk3-check THEME-FOLDER\n"); return 2; }
	if (!gtk_init_check(&argc, &argv)) {
		if (getenv("CRIMSON_GTK_REQUIRE_DISPLAY")) { fprintf(stderr, "no display\n"); return 1; }
		printf("no display: skipped\n");
		return 77;
	}
	const char *names[] = { "gtk.css", "gtk-dark.css" };
	for (int i = 0; i < 2; i++) {
		char *path = g_build_filename(argv[1], "gtk-3.0", names[i], NULL);
		GtkCssProvider *p = gtk_css_provider_new();
		g_signal_connect(p, "parsing-error", G_CALLBACK(on_error), path);
		GError *err = NULL;
		if (!gtk_css_provider_load_from_path(p, path, &err)) {
			fprintf(stderr, "%s: %s\n", path, err ? err->message : "not read");
			problems++;
		}
		g_clear_error(&err);
		g_object_unref(p);
		printf("read %s\n", path);
	}
	printf("%d problems\n", problems);
	return problems == 0 ? 0 : 1;
}
