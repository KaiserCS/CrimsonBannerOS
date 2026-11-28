// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <glib.h>

/* What the address bar should open for what was typed: a URL as it is, "example.com" as https, anything else as a
 * search with the template ("https://duckduckgo.com/?q={0}"). NULL for nothing. */
char *online_url_from_input(const char *text, const char *search_template);

/* The host of a URL in lower case ("" when it has none). */
char *online_host(const char *url);

/* True when host is site or one of its subdomains. */
gboolean online_host_matches(const char *host, const char *site);

/* True when a line of the "blocked sites" setting (one host per line, http:// and slashes allowed) blocks url. */
gboolean online_site_blocked(const char *blocked_sites, const char *url);

/* A free file name in dir for a download: "name.ext", else "name (2).ext", "name (3).ext"… */
char *online_unique_path(const char *dir, const char *name);

/* A page's title as a menu item: at most max characters (cut with "…"), the URL's host when there is no title, and
 * underscores doubled, since GTK's menus read one as a keyboard shortcut's mark. One long title would otherwise
 * make the whole menu as wide as itself (a menu is as wide as its widest page). */
char *online_menu_label(const char *title, const char *url, guint max);

/* Text made safe to put in HTML. */
char *online_html_escape(const char *s);
