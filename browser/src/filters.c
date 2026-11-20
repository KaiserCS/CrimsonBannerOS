// SPDX-License-Identifier: GPL-3.0-or-later
#include "filters.h"
#include "adblock.h"
#include <libsoup/soup.h>
#include <glib/gstdio.h>
#include <string.h>

#define CONVERTER_VERSION "1"
#define STALE_SECONDS (4 * 24 * 3600)

static const struct { const char *id, *name, *url; } lists[] = {
	{ "easylist", "EasyList (ads)", "https://easylist.to/easylist/easylist.txt" },
	{ "easyprivacy", "EasyPrivacy (trackers)", "https://easylist.to/easylist/easyprivacy.txt" },
	{ "pgl", "Peter Lowe's ad servers", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext" },
};

struct _OnlineFilters {
	WebKitUserContentManager *ucm;
	WebKitUserContentFilterStore *store;
	char *list_dir;
	char *only;                       /* tests: just this list */
	gboolean enabled;
	char **allow;
	guint generation;                 /* an apply superseded by a newer one drops its results */
	int pending, installed, rules;
	GPtrArray *ids;                   /* identifiers in use (older compiled lists are removed) */
	OnlineFiltersReady ready;
	gpointer ready_data;
	SoupSession *soup;
	int downloading;
	char *status;
};

typedef struct {
	OnlineFilters *f;
	guint generation;
	char *id, *text;
	gboolean cosmetic;
	int rules;
} Job;

static void job_free(Job *j) { g_free(j->id); g_free(j->text); g_free(j); }

OnlineFilters *online_filters_new(WebKitUserContentManager *ucm, const char *cache_dir)
{
	OnlineFilters *f = g_new0(OnlineFilters, 1);
	f->ucm = g_object_ref(ucm);
	gchar *store_dir = g_build_filename(cache_dir, "content-filters", NULL);
	f->list_dir = g_build_filename(cache_dir, "filter-lists", NULL);
	g_mkdir_with_parents(store_dir, 0700);
	g_mkdir_with_parents(f->list_dir, 0700);
	f->store = webkit_user_content_filter_store_new(store_dir);
	g_free(store_dir);
	f->ids = g_ptr_array_new_with_free_func(g_free);
	f->status = g_strdup("Starting the blocker…");
	return f;
}

void online_filters_use_only(OnlineFilters *f, const char *filters)
{
	g_free(f->only);
	f->only = g_strdup(filters);
}

const char *online_filters_status(OnlineFilters *f) { return f->status; }

static void set_status(OnlineFilters *f)
{
	g_free(f->status);
	if (!f->enabled) f->status = g_strdup("Ad and tracker blocking is off");
	else if (f->pending) f->status = g_strdup("Preparing the blocker…");
	else f->status = g_strdup_printf("Blocking ads and trackers: %d list%s, %d rules", f->installed, f->installed == 1 ? "" : "s", f->rules);
}

static void remove_old_ids(GObject *src, GAsyncResult *res, gpointer data)
{
	OnlineFilters *f = data;
	gchar **ids = webkit_user_content_filter_store_fetch_identifiers_finish(WEBKIT_USER_CONTENT_FILTER_STORE(src), res);
	for (gchar **i = ids; i && *i; i++) {
		gboolean used = FALSE;
		for (guint k = 0; k < f->ids->len; k++) if (!strcmp(f->ids->pdata[k], *i)) used = TRUE;
		if (!used) webkit_user_content_filter_store_remove(f->store, *i, NULL, NULL, NULL);
	}
	g_strfreev(ids);
}

static void job_done(Job *j, WebKitUserContentFilter *filter)
{
	OnlineFilters *f = j->f;
	if (j->generation == f->generation) {
		if (filter) {
			webkit_user_content_manager_add_filter(f->ucm, filter);
			f->installed++;
			f->rules += j->rules;
		}
		if (--f->pending == 0) {
			set_status(f);
			webkit_user_content_filter_store_fetch_identifiers(f->store, NULL, remove_old_ids, f);
			if (f->ready) { OnlineFiltersReady r = f->ready; f->ready = NULL; r(f->ready_data); }
		}
	}
	if (filter) webkit_user_content_filter_unref(filter);
	job_free(j);
}

static void compile(Job *j);

static void saved(GObject *src, GAsyncResult *res, gpointer data)
{
	Job *j = data;
	GError *err = NULL;
	WebKitUserContentFilter *filter = webkit_user_content_filter_store_save_finish(WEBKIT_USER_CONTENT_FILTER_STORE(src), res, &err);
	if (!filter && j->cosmetic) {
		/* WebKit refused the list, most likely one of its element-hiding selectors: again without them */
		g_warning("online: %s: %s; using its network rules only", j->id, err ? err->message : "refused");
		g_clear_error(&err);
		j->cosmetic = FALSE;
		gchar *id = g_strconcat(j->id, "-net", NULL);
		g_free(j->id);
		j->id = id;
		g_ptr_array_add(j->f->ids, g_strdup(id));
		compile(j);
		return;
	}
	if (!filter) g_warning("online: %s: %s", j->id, err ? err->message : "refused");
	g_clear_error(&err);
	job_done(j, filter);
}

static void compile(Job *j)
{
	AdblockStats st;
	char *json = adblock_to_webkit(j->text, (const char *const *)j->f->allow, j->cosmetic, &st);
	j->rules = st.network + st.cosmetic + st.exceptions;
	GBytes *bytes = g_bytes_new_take(json, strlen(json));
	webkit_user_content_filter_store_save(j->f->store, j->id, bytes, NULL, saved, j);
	g_bytes_unref(bytes);
}

static void loaded(GObject *src, GAsyncResult *res, gpointer data)
{
	Job *j = data;
	WebKitUserContentFilter *filter = webkit_user_content_filter_store_load_finish(WEBKIT_USER_CONTENT_FILTER_STORE(src), res, NULL);
	if (filter) {
		/* compiled before: just the count, for the status line */
		AdblockStats st;
		g_free(adblock_to_webkit(j->text, NULL, TRUE, &st));
		j->rules = st.network + st.cosmetic + st.exceptions;
		job_done(j, filter);
	} else compile(j);
}

static void add_job(OnlineFilters *f, const char *name, const char *text)
{
	/* the identifier says what went in, so WebKit's compiled copy is reused until the list or the allow list changes */
	GChecksum *sum = g_checksum_new(G_CHECKSUM_SHA256);
	g_checksum_update(sum, (const guchar *)CONVERTER_VERSION, -1);
	g_checksum_update(sum, (const guchar *)text, -1);
	for (char **a = f->allow; a && *a; a++) { g_checksum_update(sum, (const guchar *)"\n", 1); g_checksum_update(sum, (const guchar *)*a, -1); }
	Job *j = g_new0(Job, 1);
	j->f = f;
	j->generation = f->generation;
	j->id = g_strdup_printf("%s-%.16s", name, g_checksum_get_string(sum));
	j->text = g_strdup(text);
	j->cosmetic = TRUE;
	g_checksum_free(sum);
	g_ptr_array_add(f->ids, g_strdup(j->id));
	f->pending++;
	webkit_user_content_filter_store_load(f->store, j->id, NULL, loaded, j);
}

void online_filters_apply(OnlineFilters *f, gboolean enabled, const char *const *allow_sites, OnlineFiltersReady ready, gpointer data)
{
	f->generation++;
	f->enabled = enabled;
	gchar **allow = g_strdupv((gchar **)allow_sites);     /* allow_sites may be f->allow itself */
	g_strfreev(f->allow);
	f->allow = allow;
	f->installed = f->rules = 0;
	if (ready) { f->ready = ready; f->ready_data = data; }   /* a newer apply still tells whoever waited */
	g_ptr_array_set_size(f->ids, 0);
	webkit_user_content_manager_remove_all_filters(f->ucm);
	f->pending = 1;                        /* held until every job is queued */
	if (enabled) {
		if (f->only) add_job(f, "test", f->only);
		else {
			GBytes *b = g_resources_lookup_data("/org/redstaros/Online/filters-builtin.txt", 0, NULL);
			if (b) {
				gchar *t = g_strndup(g_bytes_get_data(b, NULL), g_bytes_get_size(b));
				add_job(f, "builtin", t);
				g_free(t);
				g_bytes_unref(b);
			}
			for (size_t i = 0; i < G_N_ELEMENTS(lists); i++) {
				gchar *p = g_build_filename(f->list_dir, lists[i].id, NULL), *t = NULL;
				if (g_file_get_contents(p, &t, NULL, NULL)) add_job(f, lists[i].id, t);
				g_free(t);
				g_free(p);
			}
		}
	}
	set_status(f);
	Job hold = { .f = f, .generation = f->generation };
	Job *h = g_memdup2(&hold, sizeof hold);
	job_done(h, NULL);                     /* lets go of the hold (and calls ready if nothing was queued) */
}

// ── downloading the lists ──

typedef struct { OnlineFilters *f; int index; SoupMessage *msg; } Fetch;

static void fetched(GObject *src, GAsyncResult *res, gpointer data)
{
	Fetch *x = data;
	OnlineFilters *f = x->f;
	GError *err = NULL;
	GBytes *b = soup_session_send_and_read_finish(SOUP_SESSION(src), res, &err);
	guint status = soup_message_get_status(x->msg);
	gsize len = b ? g_bytes_get_size(b) : 0;
	const char *text = b ? g_bytes_get_data(b, NULL) : NULL;
	/* a filter list, not an error page: big enough and starting like one */
	if (b && status == 200 && len > 512 && (text[0] == '[' || text[0] == '!' || text[0] == '|')) {
		gchar *p = g_build_filename(f->list_dir, lists[x->index].id, NULL);
		g_file_set_contents(p, text, (gssize)len, NULL);
		g_free(p);
	} else g_message("online: couldn't update %s (%s)", lists[x->index].name, err ? err->message : soup_status_get_phrase(status));
	g_clear_error(&err);
	if (b) g_bytes_unref(b);
	g_object_unref(x->msg);
	g_free(x);
	if (--f->downloading == 0) online_filters_apply(f, f->enabled, (const char *const *)f->allow, NULL, NULL);
}

void online_filters_update(OnlineFilters *f, gboolean force)
{
	if (f->only || !f->enabled || f->downloading) return;
	if (!f->soup) {
		f->soup = soup_session_new();
		soup_session_set_user_agent(f->soup, "RedStarOnline (filter lists)");
	}
	gint64 now = g_get_real_time() / G_USEC_PER_SEC;
	for (size_t i = 0; i < G_N_ELEMENTS(lists); i++) {
		gchar *p = g_build_filename(f->list_dir, lists[i].id, NULL);
		GStatBuf st;
		gboolean stale = force || g_stat(p, &st) != 0 || now - (gint64)st.st_mtime > STALE_SECONDS;
		g_free(p);
		if (!stale) continue;
		Fetch *x = g_new0(Fetch, 1);
		x->f = f;
		x->index = (int)i;
		x->msg = soup_message_new("GET", lists[i].url);
		if (!x->msg) { g_free(x); continue; }
		f->downloading++;
		soup_session_send_and_read_async(f->soup, x->msg, G_PRIORITY_LOW, NULL, fetched, x);
	}
}
