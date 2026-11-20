// SPDX-License-Identifier: GPL-3.0-or-later
// Red Star Online's ad and tracker blocker: Adblock Plus / EasyList filters turned into a WebKit content-blocker rule
// list (JSON), which WebKit compiles and applies itself, inside the engine.
#pragma once
#include <glib.h>

typedef struct {
	int network;    /* block rules */
	int cosmetic;   /* element-hiding rules */
	int exceptions; /* @@ rules */
	int skipped;    /* rules WebKit can't express (scriptlets, procedural selectors, regex rules…) */
} AdblockStats;

/* Converts filter-list text into a WebKit content-blocker JSON array. allow_sites (may be NULL) are sites where the
 * blocker is switched off: they come last, as rules that cancel everything before them on those sites. With
 * cosmetic FALSE, element-hiding rules are left out (a fallback when WebKit rejects one of their selectors). */
char *adblock_to_webkit(const char *filters, const char *const *allow_sites, gboolean cosmetic, AdblockStats *stats);

/* An Adblock Plus URL pattern ("||ads.example.com^", "/banner/ad.gif|") as a WebKit url-filter regex, or NULL when
 * it can't be expressed. */
char *adblock_pattern_regex(const char *pattern);
