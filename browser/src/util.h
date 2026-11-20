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

/* Text made safe to put in HTML. */
char *online_html_escape(const char *s);
