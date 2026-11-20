// SPDX-License-Identifier: GPL-3.0-or-later
// Bookmarks, history, settings and the last session: ~/.config/redstaros/browser-data.json, the same file (and
// format) the Red Star shell reads for its search and Settings. Every change re-reads the file first and keeps what
// it doesn't touch, so the shell's edits and this browser's don't overwrite each other.
#pragma once
#include <glib.h>

typedef struct _OnlineStore OnlineStore;

typedef struct {
	char *title, *url;
} OnlineLink;

typedef struct {
	char *search_engine;       /* "DuckDuckGo", "Google"… */
	gboolean show_bookmarks_bar, block_trackers, restore_session, save_history;
	char *blocked_sites;       /* one host per line */
	char **allow_sites;        /* where the blocker is off */
} OnlineSettings;

OnlineStore *online_store_new(const char *path);
void online_store_free(OnlineStore *s);
const char *online_store_path(OnlineStore *s);

/* Re-reads the file if it changed on disk. */
void online_store_refresh(OnlineStore *s);

/* Current settings (owned by the store; valid until the next refresh or change). */
const OnlineSettings *online_store_settings(OnlineStore *s);
/* The search URL template ("https://duckduckgo.com/?q={0}") for the chosen engine. */
const char *online_store_search_template(OnlineStore *s);
/* The engines the shell offers, in order; NULL-terminated names. */
const char *const *online_store_engines(void);
void online_store_set_engine(OnlineStore *s, const char *name);
void online_store_set_flag(OnlineStore *s, const char *setting, gboolean on);   /* "BlockTrackers", "SaveHistory"… */
/* The blocker on (TRUE) or off for a site (TrackerAllowSites). */
void online_store_set_site_blocking(OnlineStore *s, const char *host, gboolean block);
gboolean online_store_site_blocking(OnlineStore *s, const char *host);

/* Bookmarks: GPtrArray of OnlineLink*, newest last (as the shell keeps them). */
GPtrArray *online_store_bookmarks(OnlineStore *s);
gboolean online_store_is_bookmarked(OnlineStore *s, const char *url);
/* Adds the bookmark, or removes it when it's there; returns TRUE when it is bookmarked now. */
gboolean online_store_toggle_bookmark(OnlineStore *s, const char *url, const char *title);

/* History, newest first: GPtrArray of OnlineLink*, at most max entries. */
GPtrArray *online_store_history(OnlineStore *s, guint max);
/* A visit (http and https only, as the shell keeps; with any_scheme for tests). Written within a few seconds. */
void online_store_record_visit(OnlineStore *s, const char *url, const char *title, gboolean any_scheme);
/* The page's title arrived after its visit was recorded (only touches visits not yet written). */
void online_store_update_title(OnlineStore *s, const char *url, const char *title);
void online_store_clear_history(OnlineStore *s);
/* Writes pending visits now. */
void online_store_flush(OnlineStore *s);

/* The tabs to bring back next time. */
char **online_store_session(OnlineStore *s);
void online_store_set_session(OnlineStore *s, const char *const *urls);

/* Zoom per site (1.0 when none). */
double online_store_zoom(OnlineStore *s, const char *host);
void online_store_set_zoom(OnlineStore *s, const char *host, double zoom);

/* A remembered answer to a site's permission request: 1 allow, 0 deny, -1 not asked yet. */
int online_store_permission(OnlineStore *s, const char *host, const char *permission);
void online_store_set_permission(OnlineStore *s, const char *host, const char *permission, gboolean allow);

void online_link_free(OnlineLink *l);
