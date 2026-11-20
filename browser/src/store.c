// SPDX-License-Identifier: GPL-3.0-or-later
#include "store.h"
#include <gio/gio.h>
#include <json-glib/json-glib.h>
#include <string.h>

#define MAX_HISTORY 5000

typedef struct { char *url, *title; gint64 time; } Visit;

struct _OnlineStore {
	char *path;
	JsonNode *root;           /* the whole file, unknown members included */
	char *stamp;              /* modification time and size when last read or written */
	OnlineSettings settings;
	GPtrArray *pending;       /* Visit*: not written yet */
	guint flush_id;
};

/* the same engines as the shell (Services/BrowserData.cs) */
static const struct { const char *name, *tpl; } engines[] = {
	{ "DuckDuckGo", "https://duckduckgo.com/?q={0}" },
	{ "Google", "https://www.google.com/search?q={0}" },
	{ "Bing", "https://www.bing.com/search?q={0}" },
	{ "Brave", "https://search.brave.com/search?q={0}" },
	{ "Startpage", "https://www.startpage.com/do/search?q={0}" },
	{ "Wikipedia", "https://en.wikipedia.org/w/index.php?search={0}" },
};
static const char *const engine_names[] = { "DuckDuckGo", "Google", "Bing", "Brave", "Startpage", "Wikipedia", NULL };

const char *const *online_store_engines(void) { return engine_names; }

void online_link_free(OnlineLink *l)
{
	if (!l) return;
	g_free(l->title);
	g_free(l->url);
	g_free(l);
}

static void visit_free(Visit *v) { g_free(v->url); g_free(v->title); g_free(v); }

static char *file_stamp(const char *path)
{
	GFile *f = g_file_new_for_path(path);
	GFileInfo *info = g_file_query_info(f, G_FILE_ATTRIBUTE_TIME_MODIFIED "," G_FILE_ATTRIBUTE_TIME_MODIFIED_USEC ","
		G_FILE_ATTRIBUTE_STANDARD_SIZE, G_FILE_QUERY_INFO_NONE, NULL, NULL);
	g_object_unref(f);
	if (!info) return g_strdup("");
	char *s = g_strdup_printf("%" G_GUINT64_FORMAT ".%u:%" G_GOFFSET_FORMAT,
		g_file_info_get_attribute_uint64(info, G_FILE_ATTRIBUTE_TIME_MODIFIED),
		g_file_info_get_attribute_uint32(info, G_FILE_ATTRIBUTE_TIME_MODIFIED_USEC), g_file_info_get_size(info));
	g_object_unref(info);
	return s;
}

static JsonObject *root_obj(OnlineStore *s) { return json_node_get_object(s->root); }

static gboolean holds(JsonObject *o, const char *name, JsonNodeType type)
{
	JsonNode *n = o ? json_object_get_member(o, name) : NULL;
	return n && JSON_NODE_TYPE(n) == type;
}

/* a member that must be an array / object: made one if missing or of another type */
static JsonArray *array_member(JsonObject *o, const char *name)
{
	if (!holds(o, name, JSON_NODE_ARRAY)) json_object_set_array_member(o, name, json_array_new());
	return json_object_get_array_member(o, name);
}
static JsonObject *object_member(JsonObject *o, const char *name)
{
	if (!holds(o, name, JSON_NODE_OBJECT)) json_object_set_object_member(o, name, json_object_new());
	return json_object_get_object_member(o, name);
}

static const char *str(JsonObject *o, const char *name)
{
	JsonNode *n = o ? json_object_get_member(o, name) : NULL;
	if (!n || !JSON_NODE_HOLDS_VALUE(n) || json_node_get_value_type(n) != G_TYPE_STRING) return "";
	return json_node_get_string(n);
}
static gboolean boolean(JsonObject *o, const char *name, gboolean dflt)
{
	JsonNode *n = o ? json_object_get_member(o, name) : NULL;
	if (!n || !JSON_NODE_HOLDS_VALUE(n) || json_node_get_value_type(n) != G_TYPE_BOOLEAN) return dflt;
	return json_node_get_boolean(n);
}
static gint64 integer(JsonObject *o, const char *name, gint64 dflt)
{
	JsonNode *n = o ? json_object_get_member(o, name) : NULL;
	if (!n || !JSON_NODE_HOLDS_VALUE(n)) return dflt;
	GType t = json_node_get_value_type(n);
	if (t == G_TYPE_INT64) return json_node_get_int(n);
	if (t == G_TYPE_DOUBLE) return (gint64)json_node_get_double(n);
	return dflt;
}
static double number(JsonObject *o, const char *name, double dflt)
{
	JsonNode *n = o ? json_object_get_member(o, name) : NULL;
	if (!n || !JSON_NODE_HOLDS_VALUE(n)) return dflt;
	GType t = json_node_get_value_type(n);
	if (t == G_TYPE_DOUBLE) return json_node_get_double(n);
	if (t == G_TYPE_INT64) return (double)json_node_get_int(n);
	return dflt;
}

static void settings_clear(OnlineSettings *st)
{
	g_free(st->search_engine);
	g_free(st->blocked_sites);
	g_strfreev(st->allow_sites);
	memset(st, 0, sizeof *st);
}

static void read_settings(OnlineStore *s)
{
	settings_clear(&s->settings);
	JsonObject *set = holds(root_obj(s), "Settings", JSON_NODE_OBJECT) ? json_object_get_object_member(root_obj(s), "Settings") : NULL;
	const char *engine = str(set, "SearchEngine");
	s->settings.search_engine = g_strdup(*engine ? engine : "DuckDuckGo");
	s->settings.show_bookmarks_bar = boolean(set, "ShowBookmarksBar", TRUE);
	s->settings.block_trackers = boolean(set, "BlockTrackers", TRUE);
	s->settings.restore_session = boolean(set, "RestoreSession", TRUE);
	s->settings.save_history = boolean(set, "SaveHistory", TRUE);
	s->settings.blocked_sites = g_strdup(str(set, "BlockedSites"));
	GPtrArray *allow = g_ptr_array_new();
	if (holds(set, "TrackerAllowSites", JSON_NODE_ARRAY)) {
		JsonArray *a = json_object_get_array_member(set, "TrackerAllowSites");
		for (guint i = 0; i < json_array_get_length(a); i++) {
			JsonNode *n = json_array_get_element(a, i);
			if (JSON_NODE_HOLDS_VALUE(n) && json_node_get_value_type(n) == G_TYPE_STRING) g_ptr_array_add(allow, g_strdup(json_node_get_string(n)));
		}
	}
	g_ptr_array_add(allow, NULL);
	s->settings.allow_sites = (char **)g_ptr_array_free(allow, FALSE);
}

static void load(OnlineStore *s)
{
	if (s->root) json_node_unref(s->root);
	s->root = NULL;
	JsonParser *p = json_parser_new();
	if (g_file_test(s->path, G_FILE_TEST_EXISTS) && json_parser_load_from_file(p, s->path, NULL)) {
		JsonNode *r = json_parser_get_root(p);
		if (r && JSON_NODE_HOLDS_OBJECT(r)) s->root = json_node_copy(r);
	}
	g_object_unref(p);
	if (!s->root) {
		s->root = json_node_new(JSON_NODE_OBJECT);
		json_node_take_object(s->root, json_object_new());
	}
	g_free(s->stamp);
	s->stamp = file_stamp(s->path);
	read_settings(s);
}

static void save(OnlineStore *s)
{
	gchar *dir = g_path_get_dirname(s->path);
	g_mkdir_with_parents(dir, 0700);
	g_free(dir);
	JsonGenerator *g = json_generator_new();
	json_generator_set_root(g, s->root);
	gsize len = 0;
	gchar *data = json_generator_to_data(g, &len);
	GError *err = NULL;
	if (!g_file_set_contents(s->path, data, (gssize)len, &err)) {          /* written to a temporary file, then renamed */
		g_warning("online: can't save %s: %s", s->path, err->message);
		g_error_free(err);
	}
	g_free(data);
	g_object_unref(g);
	g_free(s->stamp);
	s->stamp = file_stamp(s->path);
	read_settings(s);
}

OnlineStore *online_store_new(const char *path)
{
	OnlineStore *s = g_new0(OnlineStore, 1);
	s->path = g_strdup(path);
	s->pending = g_ptr_array_new_with_free_func((GDestroyNotify)visit_free);
	load(s);
	return s;
}

void online_store_free(OnlineStore *s)
{
	if (!s) return;
	online_store_flush(s);
	if (s->flush_id) g_source_remove(s->flush_id);
	g_ptr_array_unref(s->pending);
	settings_clear(&s->settings);
	json_node_unref(s->root);
	g_free(s->stamp);
	g_free(s->path);
	g_free(s);
}

const char *online_store_path(OnlineStore *s) { return s->path; }

gboolean online_store_refresh(OnlineStore *s)
{
	char *now = file_stamp(s->path);
	gboolean changed = g_strcmp0(now, s->stamp) != 0;
	if (changed) load(s);
	g_free(now);
	return changed;
}

const OnlineSettings *online_store_settings(OnlineStore *s) { return &s->settings; }

const char *online_store_search_template(OnlineStore *s)
{
	for (size_t i = 0; i < G_N_ELEMENTS(engines); i++)
		if (!g_strcmp0(engines[i].name, s->settings.search_engine)) return engines[i].tpl;
	return engines[0].tpl;
}

static JsonObject *settings_for_change(OnlineStore *s)
{
	online_store_refresh(s);
	return object_member(root_obj(s), "Settings");
}

void online_store_set_engine(OnlineStore *s, const char *name)
{
	json_object_set_string_member(settings_for_change(s), "SearchEngine", name);
	save(s);
}

void online_store_set_flag(OnlineStore *s, const char *setting, gboolean on)
{
	json_object_set_boolean_member(settings_for_change(s), setting, on);
	save(s);
}

gboolean online_store_site_blocking(OnlineStore *s, const char *host)
{
	for (char **a = s->settings.allow_sites; a && *a; a++) if (!g_ascii_strcasecmp(*a, host)) return FALSE;
	return TRUE;
}

void online_store_set_site_blocking(OnlineStore *s, const char *host, gboolean block)
{
	JsonObject *set = settings_for_change(s);
	JsonArray *old = array_member(set, "TrackerAllowSites");
	JsonArray *now = json_array_new();
	for (guint i = 0; i < json_array_get_length(old); i++) {
		JsonNode *n = json_array_get_element(old, i);
		if (JSON_NODE_HOLDS_VALUE(n) && json_node_get_value_type(n) == G_TYPE_STRING && !g_ascii_strcasecmp(json_node_get_string(n), host)) continue;
		json_array_add_element(now, json_node_copy(n));
	}
	if (!block) json_array_add_string_element(now, host);
	json_object_set_array_member(set, "TrackerAllowSites", now);
	save(s);
}

// ── bookmarks ──

GPtrArray *online_store_bookmarks(OnlineStore *s)
{
	GPtrArray *out = g_ptr_array_new_with_free_func((GDestroyNotify)online_link_free);
	if (!holds(root_obj(s), "Bookmarks", JSON_NODE_ARRAY)) return out;
	JsonArray *a = json_object_get_array_member(root_obj(s), "Bookmarks");
	for (guint i = 0; i < json_array_get_length(a); i++) {
		JsonNode *n = json_array_get_element(a, i);
		if (!JSON_NODE_HOLDS_OBJECT(n)) continue;
		JsonObject *o = json_node_get_object(n);
		if (!*str(o, "Url")) continue;
		OnlineLink *l = g_new0(OnlineLink, 1);
		l->url = g_strdup(str(o, "Url"));
		l->title = g_strdup(*str(o, "Title") ? str(o, "Title") : str(o, "Url"));
		g_ptr_array_add(out, l);
	}
	return out;
}

static int find_url(JsonArray *a, const char *url)
{
	for (guint i = 0; i < json_array_get_length(a); i++) {
		JsonNode *n = json_array_get_element(a, i);
		if (JSON_NODE_HOLDS_OBJECT(n) && !g_strcmp0(str(json_node_get_object(n), "Url"), url)) return (int)i;
	}
	return -1;
}

gboolean online_store_is_bookmarked(OnlineStore *s, const char *url)
{
	return url && holds(root_obj(s), "Bookmarks", JSON_NODE_ARRAY)
		&& find_url(json_object_get_array_member(root_obj(s), "Bookmarks"), url) >= 0;
}

gboolean online_store_toggle_bookmark(OnlineStore *s, const char *url, const char *title)
{
	if (!url || !*url) return FALSE;
	online_store_refresh(s);
	JsonArray *a = array_member(root_obj(s), "Bookmarks");
	int at = find_url(a, url);
	if (at >= 0) json_array_remove_element(a, (guint)at);
	else {
		JsonObject *b = json_object_new();
		json_object_set_string_member(b, "Title", title && *title ? title : url);
		json_object_set_string_member(b, "Url", url);
		json_object_set_int_member(b, "Added", g_get_real_time() / G_USEC_PER_SEC);
		json_array_add_object_element(a, b);
	}
	save(s);
	return at < 0;
}

// ── history ──

GPtrArray *online_store_history(OnlineStore *s, guint max)
{
	GPtrArray *out = g_ptr_array_new_with_free_func((GDestroyNotify)online_link_free);
	if (!holds(root_obj(s), "History", JSON_NODE_ARRAY)) return out;
	JsonArray *a = json_object_get_array_member(root_obj(s), "History");
	for (guint i = 0; i < json_array_get_length(a) && out->len < max; i++) {
		JsonNode *n = json_array_get_element(a, i);
		if (!JSON_NODE_HOLDS_OBJECT(n)) continue;
		JsonObject *o = json_node_get_object(n);
		if (!*str(o, "Url")) continue;
		OnlineLink *l = g_new0(OnlineLink, 1);
		l->url = g_strdup(str(o, "Url"));
		l->title = g_strdup(*str(o, "Title") ? str(o, "Title") : str(o, "Url"));
		g_ptr_array_add(out, l);
	}
	return out;
}

static gboolean flush_cb(gpointer data)
{
	OnlineStore *s = data;
	s->flush_id = 0;
	online_store_flush(s);
	return G_SOURCE_REMOVE;
}

void online_store_record_visit(OnlineStore *s, const char *url, const char *title, gboolean any_scheme)
{
	if (!url || !*url || !s->settings.save_history) return;
	if (!any_scheme && !g_str_has_prefix(url, "http://") && !g_str_has_prefix(url, "https://")) return;
	Visit *v = g_new0(Visit, 1);
	v->url = g_strdup(url);
	v->title = g_strdup(title ? title : "");
	v->time = g_get_real_time() / G_USEC_PER_SEC;
	g_ptr_array_add(s->pending, v);
	if (!s->flush_id) s->flush_id = g_timeout_add_seconds(2, flush_cb, s);
}

void online_store_update_title(OnlineStore *s, const char *url, const char *title)
{
	if (!url || !title || !*title) return;
	for (guint i = 0; i < s->pending->len; i++) {
		Visit *v = g_ptr_array_index(s->pending, i);
		if (!g_strcmp0(v->url, url)) { g_free(v->title); v->title = g_strdup(title); }
	}
}

void online_store_flush(OnlineStore *s)
{
	if (s->pending->len == 0) return;
	online_store_refresh(s);
	for (guint p = 0; p < s->pending->len; p++) {
		Visit *v = g_ptr_array_index(s->pending, p);
		JsonArray *old = array_member(root_obj(s), "History");
		int at = find_url(old, v->url);
		/* the visited page goes first (newest first), counted once more if it was there */
		JsonObject *e = json_object_new();
		JsonObject *was = at >= 0 ? json_array_get_object_element(old, (guint)at) : NULL;
		json_object_set_string_member(e, "Url", v->url);
		json_object_set_string_member(e, "Title", *v->title ? v->title : (was ? str(was, "Title") : ""));
		json_object_set_int_member(e, "Time", v->time);
		json_object_set_int_member(e, "Visits", (was ? integer(was, "Visits", 1) : 0) + 1);
		JsonArray *now = json_array_new();
		json_array_add_object_element(now, e);
		for (guint i = 0; i < json_array_get_length(old) && json_array_get_length(now) < MAX_HISTORY; i++)
			if ((int)i != at) json_array_add_element(now, json_node_copy(json_array_get_element(old, i)));
		json_object_set_array_member(root_obj(s), "History", now);
	}
	g_ptr_array_set_size(s->pending, 0);
	save(s);
}

void online_store_clear_history(OnlineStore *s)
{
	g_ptr_array_set_size(s->pending, 0);
	online_store_refresh(s);
	json_object_set_array_member(root_obj(s), "History", json_array_new());
	save(s);
}

// ── session ──

char **online_store_session(OnlineStore *s)
{
	GPtrArray *out = g_ptr_array_new();
	if (holds(root_obj(s), "Session", JSON_NODE_ARRAY)) {
		JsonArray *a = json_object_get_array_member(root_obj(s), "Session");
		for (guint i = 0; i < json_array_get_length(a); i++) {
			JsonNode *n = json_array_get_element(a, i);
			if (JSON_NODE_HOLDS_VALUE(n) && json_node_get_value_type(n) == G_TYPE_STRING && *json_node_get_string(n))
				g_ptr_array_add(out, g_strdup(json_node_get_string(n)));
		}
	}
	g_ptr_array_add(out, NULL);
	return (char **)g_ptr_array_free(out, FALSE);
}

void online_store_set_session(OnlineStore *s, const char *const *urls)
{
	online_store_refresh(s);
	JsonArray *a = json_array_new();
	for (const char *const *u = urls; u && *u; u++) json_array_add_string_element(a, *u);
	json_object_set_array_member(root_obj(s), "Session", a);
	save(s);
}

// ── per site ──

double online_store_zoom(OnlineStore *s, const char *host)
{
	JsonObject *set = holds(root_obj(s), "Settings", JSON_NODE_OBJECT) ? json_object_get_object_member(root_obj(s), "Settings") : NULL;
	JsonObject *z = set && holds(set, "SiteZoom", JSON_NODE_OBJECT) ? json_object_get_object_member(set, "SiteZoom") : NULL;
	double v = number(z, host, 1.0);
	return v > 0.2 && v < 6 ? v : 1.0;
}

void online_store_set_zoom(OnlineStore *s, const char *host, double zoom)
{
	if (!host || !*host) return;
	JsonObject *z = object_member(settings_for_change(s), "SiteZoom");
	if (zoom > 0.99 && zoom < 1.01) { if (json_object_has_member(z, host)) json_object_remove_member(z, host); }
	else json_object_set_double_member(z, host, zoom);
	save(s);
}

int online_store_permission(OnlineStore *s, const char *host, const char *permission)
{
	JsonObject *set = holds(root_obj(s), "Settings", JSON_NODE_OBJECT) ? json_object_get_object_member(root_obj(s), "Settings") : NULL;
	JsonObject *p = set && holds(set, "SitePermissions", JSON_NODE_OBJECT) ? json_object_get_object_member(set, "SitePermissions") : NULL;
	gchar *key = g_strconcat(host, "|", permission, NULL);
	JsonNode *n = p ? json_object_get_member(p, key) : NULL;
	g_free(key);
	if (!n || !JSON_NODE_HOLDS_VALUE(n) || json_node_get_value_type(n) != G_TYPE_BOOLEAN) return -1;
	return json_node_get_boolean(n) ? 1 : 0;
}

void online_store_set_permission(OnlineStore *s, const char *host, const char *permission, gboolean allow)
{
	JsonObject *p = object_member(settings_for_change(s), "SitePermissions");
	gchar *key = g_strconcat(host, "|", permission, NULL);
	json_object_set_boolean_member(p, key, allow);
	g_free(key);
	save(s);
}
