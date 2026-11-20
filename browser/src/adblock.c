// SPDX-License-Identifier: GPL-3.0-or-later
// Adblock Plus syntax → WebKit content blockers. What converts:
//   ||ads.example.com^  |https://x/  /banner/*/ad.  $third-party  $~third-party  $domain=a.com|~b.com
//   $script,image,stylesheet,font,media,object,xmlhttprequest,subdocument,websocket,ping,other,popup (and ~type)
//   $match-case  @@exceptions  @@||site^$document  ##.generic  a.com,b.com##.specific
// What doesn't (left out, counted as skipped): /regex/ rules, $csp, $redirect, $removeparam… (they change requests
// instead of blocking them), #@# element-hiding exceptions, #?# #$# #%# ##+js scriptlets and procedural selectors.
// WebKit's url-filter regexes have no alternation or {n} counts, so ABP's "^" separator becomes a character class.
#include "adblock.h"
#include <string.h>

#define SEP "[^a-zA-Z0-9_.%-]"

static void json_str(GString *o, const char *s)
{
	g_string_append_c(o, '"');
	for (const unsigned char *p = (const unsigned char *)s; *p; p++) {
		switch (*p) {
		case '"': g_string_append(o, "\\\""); break;
		case '\\': g_string_append(o, "\\\\"); break;
		case '\n': g_string_append(o, "\\n"); break;
		case '\t': g_string_append(o, "\\t"); break;
		case '\r': g_string_append(o, "\\r"); break;
		default:
			if (*p < 0x20) g_string_append_printf(o, "\\u%04x", *p);
			else g_string_append_c(o, (char)*p);
		}
	}
	g_string_append_c(o, '"');
}

char *adblock_pattern_regex(const char *p)
{
	GString *r = g_string_new(NULL);
	gboolean anchored = FALSE;
	if (g_str_has_prefix(p, "||")) {
		/* scheme, then the host or any subdomain of it */
		g_string_append(r, "^[a-z][a-z0-9.+-]*://+([^/?#]*\\.)?");
		p += 2;
		anchored = TRUE;
	} else if (*p == '|') {
		g_string_append_c(r, '^');
		p++;
		anchored = TRUE;
	}
	size_t n = strlen(p);
	gboolean end = n > 0 && p[n - 1] == '|';
	if (end) n--;
	/* leading and trailing wildcards mean nothing in a search (unanchored) */
	while (!anchored && n > 0 && *p == '*') { p++; n--; }
	while (!end && n > 0 && p[n - 1] == '*') n--;
	size_t body = 0;
	for (size_t i = 0; i < n; i++) {
		unsigned char c = (unsigned char)p[i];
		if (c < 0x20 || c > 0x7e) { g_string_free(r, TRUE); return NULL; }     /* WebKit wants ASCII */
		if (c == '*') { g_string_append(r, ".*"); continue; }
		if (c == '^') { g_string_append(r, SEP); continue; }
		if (strchr(".+?()[]{}\\$|", c)) g_string_append_c(r, '\\');
		g_string_append_c(r, (char)c);
		body++;
	}
	if (end) g_string_append_c(r, '$');
	if (body == 0) { g_string_free(r, TRUE); return NULL; }   /* "*" or "||^": would match everything */
	return g_string_free(r, FALSE);
}

/* the WebKit resource types an ABP type name stands for (bit set) */
enum { T_DOCUMENT = 1, T_IMAGE = 2, T_STYLE = 4, T_SCRIPT = 8, T_FONT = 16, T_RAW = 32, T_MEDIA = 64, T_POPUP = 128 };
#define T_ALL (T_DOCUMENT | T_IMAGE | T_STYLE | T_SCRIPT | T_FONT | T_RAW | T_MEDIA)

static int type_bits(const char *t)
{
	if (!strcmp(t, "script")) return T_SCRIPT;
	if (!strcmp(t, "image")) return T_IMAGE;
	if (!strcmp(t, "stylesheet") || !strcmp(t, "css")) return T_STYLE;
	if (!strcmp(t, "font")) return T_FONT;
	if (!strcmp(t, "media")) return T_MEDIA;
	if (!strcmp(t, "subdocument") || !strcmp(t, "frame")) return T_DOCUMENT;
	if (!strcmp(t, "popup")) return T_POPUP;
	if (!strcmp(t, "object") || !strcmp(t, "object-subrequest") || !strcmp(t, "xmlhttprequest") || !strcmp(t, "xhr")
	    || !strcmp(t, "websocket") || !strcmp(t, "ping") || !strcmp(t, "other") || !strcmp(t, "beacon"))
		return T_RAW;
	return 0;
}

static void append_types(GString *o, int bits)
{
	static const struct { int bit; const char *name; } names[] = {
		{ T_DOCUMENT, "document" }, { T_IMAGE, "image" }, { T_STYLE, "style-sheet" }, { T_SCRIPT, "script" },
		{ T_FONT, "font" }, { T_RAW, "raw" }, { T_MEDIA, "media" }, { T_POPUP, "popup" },
	};
	g_string_append(o, ",\"resource-type\":[");
	gboolean first = TRUE;
	for (size_t i = 0; i < G_N_ELEMENTS(names); i++)
		if (bits & names[i].bit) {
			if (!first) g_string_append_c(o, ',');
			g_string_append_printf(o, "\"%s\"", names[i].name);
			first = FALSE;
		}
	g_string_append_c(o, ']');
}

/* a lower-case ASCII host for if-domain/unless-domain ("*host" = with subdomains), or NULL */
static gboolean usable_domain(const char *d)
{
	if (!*d || g_str_has_suffix(d, ".*")) return FALSE;      /* "example.*": any top-level domain, not expressible */
	for (const char *p = d; *p; p++)
		if (!(g_ascii_isalnum(*p) || *p == '.' || *p == '-')) return FALSE;
	return TRUE;
}

/* domains "a.com|~b.com" (sep '|') or "a.com,~b.com" (sep ','): appends if-domain or unless-domain.
 * FALSE when nothing usable is left of a list that had includes (the rule must not go global). */
static gboolean append_domains(GString *o, const char *list, char sep)
{
	char seps[2] = { sep, 0 };
	gchar **parts = g_strsplit(list, seps, -1);
	GString *inc = g_string_new(NULL), *exc = g_string_new(NULL);
	int had_inc = 0;
	for (gchar **d = parts; *d; d++) {
		gchar *dom = g_ascii_strdown(g_strstrip(*d), -1);
		gboolean neg = dom[0] == '~';
		const char *name = neg ? dom + 1 : dom;
		if (!neg && *name) had_inc++;
		if (usable_domain(name)) {
			GString *t = neg ? exc : inc;
			if (t->len) g_string_append_c(t, ',');
			gchar *star = g_strconcat("*", name, NULL);
			json_str(t, star);
			g_free(star);
		}
		g_free(dom);
	}
	g_strfreev(parts);
	gboolean ok = TRUE;
	if (had_inc && inc->len == 0) ok = FALSE;
	else if (inc->len) g_string_append_printf(o, ",\"if-domain\":[%s]", inc->str);     /* WebKit: not both */
	else if (exc->len) g_string_append_printf(o, ",\"unless-domain\":[%s]", exc->str);
	g_string_free(inc, TRUE);
	g_string_free(exc, TRUE);
	return ok;
}

static const char *const procedural[] = {
	":-abp-", ":has-text(", ":matches-css", ":matches-attr", ":matches-prop", ":matches-path", ":matches-media",
	":xpath(", ":upward(", ":remove(", ":style(", ":min-text-length(", ":watch-attr(", ":others(", ":if(", ":if-not(",
	":nth-ancestor(", ":contains(", ":properties(", ":shadow", ":remove-attr(", ":remove-class(", NULL,
};

static gboolean cosmetic_rule(GString *out, const char *line, AdblockStats *st)
{
	const char *hash = strstr(line, "##");
	if (!hash) return FALSE;
	const char *sel = hash + 2;
	if (*sel == '+' || !*sel) return FALSE;                    /* ##+js(...) scriptlets */
	for (int i = 0; procedural[i]; i++) if (strstr(sel, procedural[i])) return FALSE;
	for (const unsigned char *p = (const unsigned char *)sel; *p; p++) if (*p > 0x7e) return FALSE;
	GString *r = g_string_new("{\"trigger\":{\"url-filter\":\".*\"");
	if (hash > line) {
		gchar *doms = g_strndup(line, (gsize)(hash - line));
		gboolean ok = append_domains(r, doms, ',');
		g_free(doms);
		if (!ok) { g_string_free(r, TRUE); return FALSE; }
	}
	g_string_append(r, "},\"action\":{\"type\":\"css-display-none\",\"selector\":");
	json_str(r, sel);
	g_string_append(r, "}}");
	if (out->len) g_string_append_c(out, ',');
	g_string_append(out, r->str);
	g_string_free(r, TRUE);
	st->cosmetic++;
	return TRUE;
}

static gboolean option_char(char c) { return g_ascii_isalnum(c) || strchr("~,=|.*_-", c); }

static gboolean network_rule(GString *blocks, GString *exceptions, const char *line, AdblockStats *st)
{
	gboolean exception = g_str_has_prefix(line, "@@");
	const char *rule = exception ? line + 2 : line;
	if (rule[0] == '/' && strlen(rule) > 2) {
		/* "/regex/" (possibly with options) — not in WebKit's dialect */
		const char *dollar = strrchr(rule, '$');
		size_t len = dollar ? (size_t)(dollar - rule) : strlen(rule);
		if (len > 1 && rule[len - 1] == '/') return FALSE;
	}
	gchar *pattern = NULL, *opts = NULL;
	const char *dollar = strrchr(rule, '$');
	if (dollar && dollar[1]) {
		gboolean all = TRUE;
		for (const char *c = dollar + 1; *c; c++) if (!option_char(*c)) { all = FALSE; break; }
		if (all) { pattern = g_strndup(rule, (gsize)(dollar - rule)); opts = g_strdup(dollar + 1); }
	}
	if (!pattern) pattern = g_strdup(rule);

	int types = 0, not_types = 0;
	int party = 0;                       /* 1 third-party, -1 first-party */
	gboolean match_case = FALSE, document = FALSE, ok = TRUE;
	gchar *domains = NULL;
	if (opts) {
		gchar **list = g_strsplit(opts, ",", -1);
		for (gchar **o = list; *o && ok; o++) {
			const char *op = *o;
			gboolean neg = op[0] == '~';
			const char *name = neg ? op + 1 : op;
			int bits;
			if (!strcmp(name, "third-party") || !strcmp(name, "3p")) party = neg ? -1 : 1;
			else if (!strcmp(name, "first-party") || !strcmp(name, "1p")) party = neg ? 1 : -1;
			else if (!neg && (g_str_has_prefix(name, "domain=") || g_str_has_prefix(name, "from="))) {
				g_free(domains);
				domains = g_strdup(strchr(name, '=') + 1);
			} else if (!strcmp(name, "match-case")) match_case = TRUE;
			else if (!strcmp(name, "important") || !strcmp(name, "all")) { if (!strcmp(name, "all")) types |= T_ALL | T_POPUP; }
			else if (!strcmp(name, "document") || !strcmp(name, "doc")) { if (neg) not_types |= T_DOCUMENT; else document = TRUE; }
			else if ((bits = type_bits(name)) != 0) { if (neg) not_types |= bits; else types |= bits; }
			else ok = FALSE;                 /* an option we can't honour: leave the rule out rather than over-block */
		}
		g_strfreev(list);
	}
	if (exception && document) {
		/* @@||site^$document: the blocker is off on that site */
		gchar *host = NULL;
		if (g_str_has_prefix(pattern, "||")) {
			host = g_strdup(pattern + 2);
			gsize l = strlen(host);
			while (l && (host[l - 1] == '^' || host[l - 1] == '/' || host[l - 1] == '|')) host[--l] = 0;
			gchar *low = g_ascii_strdown(host, -1);
			g_free(host);
			host = low;
		}
		if (ok && host && usable_domain(host)) {
			if (exceptions->len) g_string_append_c(exceptions, ',');
			g_string_append(exceptions, "{\"trigger\":{\"url-filter\":\".*\",\"if-domain\":[");
			gchar *star = g_strconcat("*", host, NULL);
			json_str(exceptions, star);
			g_free(star);
			g_string_append(exceptions, "]},\"action\":{\"type\":\"ignore-previous-rules\"}}");
			st->exceptions++;
		} else ok = FALSE;
		g_free(host); g_free(pattern); g_free(opts); g_free(domains);
		return ok;
	}
	if (document) types |= T_DOCUMENT;
	gchar *regex = ok ? adblock_pattern_regex(pattern) : NULL;
	if (!regex) { g_free(pattern); g_free(opts); g_free(domains); return FALSE; }

	GString *r = g_string_new("{\"trigger\":{\"url-filter\":");
	json_str(r, regex);
	if (match_case) g_string_append(r, ",\"url-filter-is-case-sensitive\":true");
	if (types || not_types) {
		int bits = types ? types : T_ALL;
		bits &= ~not_types;
		if (!bits) ok = FALSE;
		else append_types(r, bits);
	}
	if (party == 1) g_string_append(r, ",\"load-type\":[\"third-party\"]");
	else if (party == -1) g_string_append(r, ",\"load-type\":[\"first-party\"]");
	if (ok && domains) ok = append_domains(r, domains, '|');
	g_string_append_printf(r, "},\"action\":{\"type\":\"%s\"}}", exception ? "ignore-previous-rules" : "block");
	if (ok) {
		GString *t = exception ? exceptions : blocks;
		if (t->len) g_string_append_c(t, ',');
		g_string_append(t, r->str);
		if (exception) st->exceptions++; else st->network++;
	}
	g_string_free(r, TRUE);
	g_free(regex); g_free(pattern); g_free(opts); g_free(domains);
	return ok;
}

char *adblock_to_webkit(const char *filters, const char *const *allow_sites, gboolean cosmetic, AdblockStats *stats)
{
	AdblockStats st = { 0 };
	GString *blocks = g_string_new(NULL), *hides = g_string_new(NULL), *exceptions = g_string_new(NULL);
	gchar **lines = g_strsplit(filters ? filters : "", "\n", -1);
	for (gchar **l = lines; *l; l++) {
		char *line = g_strstrip(*l);
		if (!*line || line[0] == '!' || line[0] == '[') continue;          /* comments, "[Adblock Plus 2.0]" */
		gboolean done;
		if (strstr(line, "#@#") || strstr(line, "#?#") || strstr(line, "#$#") || strstr(line, "#%#") || strstr(line, "#@$#")
		    || strstr(line, "#@?#"))
			done = FALSE;
		else if (strstr(line, "##")) done = cosmetic && cosmetic_rule(hides, line, &st);
		else done = network_rule(blocks, exceptions, line, &st);
		if (!done) st.skipped++;
	}
	g_strfreev(lines);

	/* order matters: an "ignore-previous-rules" only cancels rules that come before it */
	GString *out = g_string_new("[");
	GString *parts[] = { blocks, hides, exceptions };
	gboolean first = TRUE;
	for (size_t i = 0; i < G_N_ELEMENTS(parts); i++) {
		if (!parts[i]->len) continue;
		if (!first) g_string_append_c(out, ',');
		g_string_append(out, parts[i]->str);
		first = FALSE;
	}
	for (const char *const *s = allow_sites; s && *s; s++) {
		gchar *low = g_ascii_strdown(*s, -1);
		if (usable_domain(low)) {
			if (!first) g_string_append_c(out, ',');
			g_string_append(out, "{\"trigger\":{\"url-filter\":\".*\",\"if-domain\":[");
			gchar *star = g_strconcat("*", low, NULL);
			json_str(out, star);
			g_free(star);
			g_string_append(out, "]},\"action\":{\"type\":\"ignore-previous-rules\"}}");
			first = FALSE;
		}
		g_free(low);
	}
	/* WebKit refuses an empty list: a rule that never fires keeps it valid */
	if (first) g_string_append(out, "{\"trigger\":{\"url-filter\":\"^redstar-never:\"},\"action\":{\"type\":\"block\"}}");
	g_string_append_c(out, ']');
	g_string_free(blocks, TRUE); g_string_free(hides, TRUE); g_string_free(exceptions, TRUE);
	if (stats) *stats = st;
	return g_string_free(out, FALSE);
}
