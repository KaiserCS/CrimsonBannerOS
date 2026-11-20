// SPDX-License-Identifier: GPL-3.0-or-later
// The ad and tracker blocker inside WebKit: the built-in starter list plus EasyList, EasyPrivacy and Peter Lowe's list
// (downloaded every four days), each converted (adblock.c), compiled by WebKit once and cached.
#pragma once
#include <webkit/webkit.h>

typedef struct _OnlineFilters OnlineFilters;
typedef void (*OnlineFiltersReady)(gpointer data);

OnlineFilters *online_filters_new(WebKitUserContentManager *ucm, const char *cache_dir);

/* Only this list (tests): no built-in list, no downloads. */
void online_filters_use_only(OnlineFilters *f, const char *filters);

/* Installs the lists (from WebKit's cache when nothing changed, else converted and compiled), or none when disabled.
 * ready (may be NULL) runs once they're all in place. */
void online_filters_apply(OnlineFilters *f, gboolean enabled, const char *const *allow_sites, OnlineFiltersReady ready, gpointer data);

/* Downloads lists that are missing or older than four days (all of them when forced), then applies again. */
void online_filters_update(OnlineFilters *f, gboolean force);

/* "Blocking ads: 3 lists, 81 230 rules" and the like. */
const char *online_filters_status(OnlineFilters *f);
