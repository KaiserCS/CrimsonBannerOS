// SPDX-License-Identifier: GPL-3.0-or-later
#include "util.h"
#include <string.h>

static gboolean has_scheme(const char *s)
{
	static const char *const schemes[] = { "http://", "https://", "file:", "about:", "redstar:", "data:", "view-source:", "blob:", NULL };
	for (int i = 0; schemes[i]; i++) if (g_ascii_strncasecmp(s, schemes[i], strlen(schemes[i])) == 0) return TRUE;
	return FALSE;
}

/* "example.com", "sub.example.co.uk/path?q", "192.168.1.1:8080", "localhost:3000/x" */
static gboolean looks_like_address(const char *s)
{
	if (strchr(s, ' ')) return FALSE;
	gsize hl = strcspn(s, "/?#");
	gchar *host = g_strndup(s, hl);
	char *colon = strrchr(host, ':');
	if (colon) {
		for (char *p = colon + 1; *p; p++) if (!g_ascii_isdigit(*p)) { g_free(host); return FALSE; }
		*colon = 0;
	}
	gboolean ok = FALSE;
	if (!g_ascii_strcasecmp(host, "localhost")) ok = TRUE;
	else {
		gchar **labels = g_strsplit(host, ".", -1);
		guint n = g_strv_length(labels);
		gboolean all_digits = TRUE, valid = n >= 2;
		for (guint i = 0; i < n && valid; i++) {
			const char *l = labels[i];
			if (!*l) valid = FALSE;
			for (const char *p = l; *p && valid; p++) {
				if (!(g_ascii_isalnum(*p) || *p == '-' || (unsigned char)*p >= 0x80)) valid = FALSE;
				if (!g_ascii_isdigit(*p)) all_digits = FALSE;
			}
		}
		if (valid && all_digits) ok = n == 4;                       /* an IPv4 address */
		else if (valid) {
			const char *tld = labels[n - 1];
			gboolean letters = strlen(tld) >= 2;
			for (const char *p = tld; *p; p++) if (g_ascii_isdigit(*p)) letters = FALSE;
			ok = letters;
		}
		g_strfreev(labels);
	}
	g_free(host);
	return ok;
}

char *online_url_from_input(const char *text, const char *search_template)
{
	if (!text) return NULL;
	gchar *t = g_strstrip(g_strdup(text));
	if (!*t) { g_free(t); return NULL; }
	if (has_scheme(t)) return t;
	if (t[0] == '/') { gchar *u = g_filename_to_uri(t, NULL, NULL); if (u) { g_free(t); return u; } }
	if (looks_like_address(t)) {
		gboolean local = g_str_has_prefix(t, "localhost") || g_str_has_prefix(t, "127.") || g_str_has_prefix(t, "192.168.") || g_str_has_prefix(t, "10.");
		gchar *u = g_strconcat(local ? "http://" : "https://", t, NULL);
		g_free(t);
		return u;
	}
	const char *tpl = search_template && strstr(search_template, "{0}") ? search_template : "https://duckduckgo.com/?q={0}";
	gchar *q = g_uri_escape_string(t, NULL, FALSE);
	const char *at = strstr(tpl, "{0}");
	gchar *u = g_strdup_printf("%.*s%s%s", (int)(at - tpl), tpl, q, at + 3);
	g_free(q);
	g_free(t);
	return u;
}

char *online_host(const char *url)
{
	if (!url) return g_strdup("");
	gchar *host = NULL;
	if (g_uri_split(url, G_URI_FLAGS_NONE, NULL, NULL, &host, NULL, NULL, NULL, NULL, NULL) && host) {
		gchar *low = g_ascii_strdown(host, -1);
		g_free(host);
		return low;
	}
	g_free(host);
	return g_strdup("");
}

gboolean online_host_matches(const char *host, const char *site)
{
	if (!host || !site || !*host || !*site) return FALSE;
	if (!g_ascii_strcasecmp(host, site)) return TRUE;
	size_t hl = strlen(host), sl = strlen(site);
	return hl > sl && host[hl - sl - 1] == '.' && !g_ascii_strcasecmp(host + hl - sl, site);
}

gboolean online_site_blocked(const char *blocked_sites, const char *url)
{
	if (!blocked_sites || !*blocked_sites) return FALSE;
	gchar *host = online_host(url);
	gboolean hit = FALSE;
	gchar **lines = g_strsplit(blocked_sites, "\n", -1);
	for (gchar **l = lines; *l && !hit && *host; l++) {
		gchar *p = g_ascii_strdown(g_strstrip(*l), -1);
		const char *s = p;
		if (g_str_has_prefix(s, "https://")) s += 8;
		else if (g_str_has_prefix(s, "http://")) s += 7;
		gchar *site = g_strdup(s);
		gsize n = strlen(site);
		while (n && site[n - 1] == '/') site[--n] = 0;
		while (*site == '/') memmove(site, site + 1, strlen(site));
		if (*site && online_host_matches(host, site)) hit = TRUE;
		g_free(site);
		g_free(p);
	}
	g_strfreev(lines);
	g_free(host);
	return hit;
}

char *online_unique_path(const char *dir, const char *name)
{
	gchar *base = g_path_get_basename(name && *name ? name : "download");
	if (!strcmp(base, ".") || !strcmp(base, "/") || !strcmp(base, "..")) { g_free(base); base = g_strdup("download"); }
	gchar *path = g_build_filename(dir, base, NULL);
	if (!g_file_test(path, G_FILE_TEST_EXISTS)) { g_free(base); return path; }
	/* split "archive.tar.gz" as "archive" + ".tar.gz" */
	const char *dot = strchr(base[0] == '.' ? base + 1 : base, '.');
	gchar *stem = dot ? g_strndup(base, (gsize)(dot - base)) : g_strdup(base);
	const char *ext = dot ? dot : "";
	for (int i = 2; i < 10000; i++) {
		g_free(path);
		gchar *n = g_strdup_printf("%s (%d)%s", stem, i, ext);
		path = g_build_filename(dir, n, NULL);
		g_free(n);
		if (!g_file_test(path, G_FILE_TEST_EXISTS)) break;
	}
	g_free(stem);
	g_free(base);
	return path;
}

char *online_html_escape(const char *s) { return g_markup_escape_text(s ? s : "", -1); }

char *online_menu_label(const char *title, const char *url, guint max)
{
	gchar *text = NULL;
	if (title && *title && g_utf8_validate(title, -1, NULL)) text = g_strstrip(g_strdup(title));
	if (!text || !*text) {
		g_free(text);
		text = online_host(url ? url : "");
		if (!*text) { g_free(text); text = g_strdup(url && *url ? url : "Untitled"); }
	}
	if (max > 1 && g_utf8_strlen(text, -1) > (glong)max) {
		gchar *cut = g_utf8_substring(text, 0, max - 1);
		g_free(text);
		text = g_strconcat(g_strchomp(cut), "…", NULL);
		g_free(cut);
	}
	GString *out = g_string_new(NULL);
	for (const char *p = text; *p; p++) {
		if (*p == '_') g_string_append_c(out, '_');
		g_string_append_c(out, *p);
	}
	g_free(text);
	return g_string_free(out, FALSE);
}
