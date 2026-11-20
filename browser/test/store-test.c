// SPDX-License-Identifier: GPL-3.0-or-later
// The bookmarks/history file, shared with the Red Star shell (Services/BrowserData.cs): same names, nothing lost.
#include <glib.h>
#include <glib/gstdio.h>
#include <string.h>
#include "../src/store.h"

static gchar *dir, *path;

static void setup(void)
{
	dir = g_dir_make_tmp("online-store-XXXXXX", NULL);
	path = g_build_filename(dir, "browser-data.json", NULL);
}
static void teardown(void)
{
	g_remove(path);
	g_rmdir(dir);
	g_free(path); g_free(dir);
}

static gchar *contents(void)
{
	gchar *t = NULL;
	g_file_get_contents(path, &t, NULL, NULL);
	return t ? t : g_strdup("");
}

/* what the shell writes (System.Text.Json, PascalCase), with a member this browser doesn't know about */
static const char *shell_file =
	"{\"Bookmarks\":[{\"Title\":\"Wiki\",\"Url\":\"https://wiki.test/\",\"Added\":1700000000}],"
	"\"History\":[{\"Url\":\"https://old.test/\",\"Title\":\"Old\",\"Time\":1700000000,\"Visits\":3}],"
	"\"Settings\":{\"SearchEngine\":\"Brave\",\"ShowBookmarksBar\":false,\"BlockTrackers\":true,\"RestoreSession\":true,"
	"\"SaveHistory\":true,\"BlockedSites\":\"casino.test\",\"TrackerAllowSites\":[\"bank.test\"],"
	"\"SiteZoom\":{\"big.test\":1.5},\"SitePermissions\":{\"maps.test|geolocation\":true}},"
	"\"Session\":[\"https://a.test/\",\"https://b.test/\"],\"ShellOnly\":{\"keep\":42}}";

static void test_reads_the_shells_file(void)
{
	setup();
	g_file_set_contents(path, shell_file, -1, NULL);
	OnlineStore *s = online_store_new(path);
	const OnlineSettings *st = online_store_settings(s);
	g_assert_cmpstr(st->search_engine, ==, "Brave");
	g_assert_cmpstr(online_store_search_template(s), ==, "https://search.brave.com/search?q={0}");
	g_assert_false(st->show_bookmarks_bar);
	g_assert_cmpstr(st->blocked_sites, ==, "casino.test");
	g_assert_false(online_store_site_blocking(s, "bank.test"));
	g_assert_true(online_store_site_blocking(s, "news.test"));
	g_assert_cmpfloat(online_store_zoom(s, "big.test"), ==, 1.5);
	g_assert_cmpfloat(online_store_zoom(s, "other.test"), ==, 1.0);
	g_assert_cmpint(online_store_permission(s, "maps.test", "geolocation"), ==, 1);
	g_assert_cmpint(online_store_permission(s, "maps.test", "camera"), ==, -1);
	char **sess = online_store_session(s);
	g_assert_cmpuint(g_strv_length(sess), ==, 2);
	g_assert_cmpstr(sess[1], ==, "https://b.test/");
	g_strfreev(sess);
	GPtrArray *b = online_store_bookmarks(s);
	g_assert_cmpuint(b->len, ==, 1);
	g_assert_cmpstr(((OnlineLink *)b->pdata[0])->title, ==, "Wiki");
	g_ptr_array_unref(b);
	online_store_free(s);
	teardown();
}

static void test_changes_keep_everything_else(void)
{
	setup();
	g_file_set_contents(path, shell_file, -1, NULL);
	OnlineStore *s = online_store_new(path);
	g_assert_true(online_store_toggle_bookmark(s, "https://new.test/", "New"));
	g_assert_true(online_store_is_bookmarked(s, "https://new.test/"));
	gchar *t = contents();
	g_assert_nonnull(strstr(t, "\"ShellOnly\""));                        /* unknown members survive */
	g_assert_nonnull(strstr(t, "\"Title\":\"New\""));
	g_assert_nonnull(strstr(t, "\"Added\":"));
	g_free(t);
	g_assert_false(online_store_toggle_bookmark(s, "https://new.test/", "New"));   /* again: removed */
	g_assert_false(online_store_is_bookmarked(s, "https://new.test/"));

	online_store_set_engine(s, "Wikipedia");
	online_store_set_flag(s, "ShowBookmarksBar", TRUE);
	online_store_set_site_blocking(s, "bank.test", TRUE);
	online_store_set_site_blocking(s, "shop.test", FALSE);
	online_store_set_zoom(s, "big.test", 1.0);                              /* back to normal: forgotten */
	online_store_set_zoom(s, "small.test", 0.8);
	online_store_set_permission(s, "cam.test", "camera", FALSE);
	const char *tabs[] = { "https://x.test/", NULL };
	online_store_set_session(s, tabs);
	online_store_free(s);

	s = online_store_new(path);                                             /* read back fresh */
	g_assert_cmpstr(online_store_settings(s)->search_engine, ==, "Wikipedia");
	g_assert_true(online_store_settings(s)->show_bookmarks_bar);
	g_assert_true(online_store_site_blocking(s, "bank.test"));
	g_assert_false(online_store_site_blocking(s, "shop.test"));
	g_assert_cmpfloat(online_store_zoom(s, "big.test"), ==, 1.0);
	g_assert_cmpfloat(online_store_zoom(s, "small.test"), ==, 0.8);
	g_assert_cmpint(online_store_permission(s, "cam.test", "camera"), ==, 0);
	char **sess = online_store_session(s);
	g_assert_cmpuint(g_strv_length(sess), ==, 1);
	g_strfreev(sess);
	t = contents();
	g_assert_nonnull(strstr(t, "\"keep\":42"));
	g_free(t);
	online_store_free(s);
	teardown();
}

static void test_history(void)
{
	setup();
	g_file_set_contents(path, shell_file, -1, NULL);
	OnlineStore *s = online_store_new(path);
	online_store_record_visit(s, "https://new.test/", "", FALSE);
	online_store_update_title(s, "https://new.test/", "New page");
	online_store_record_visit(s, "https://old.test/", "Old, renamed", FALSE);
	online_store_record_visit(s, "file:///tmp/x.html", "Local", FALSE);    /* not kept, like the shell */
	online_store_flush(s);
	GPtrArray *h = online_store_history(s, 10);
	g_assert_cmpuint(h->len, ==, 2);
	g_assert_cmpstr(((OnlineLink *)h->pdata[0])->url, ==, "https://old.test/");   /* newest first */
	g_assert_cmpstr(((OnlineLink *)h->pdata[0])->title, ==, "Old, renamed");
	g_assert_cmpstr(((OnlineLink *)h->pdata[1])->title, ==, "New page");
	g_ptr_array_unref(h);
	gchar *t = contents();
	g_assert_nonnull(strstr(t, "\"Visits\":4"));                            /* 3 before, one more */
	g_free(t);

	/* the shell changed the file meanwhile: the next change starts from its version */
	g_file_set_contents(path, "{\"Bookmarks\":[],\"History\":[],\"Settings\":{\"SaveHistory\":true},\"FromShell\":1}", -1, NULL);
	online_store_record_visit(s, "https://later.test/", "Later", FALSE);
	online_store_flush(s);
	t = contents();
	g_assert_nonnull(strstr(t, "\"FromShell\":1"));
	g_assert_nonnull(strstr(t, "later.test"));
	g_assert_null(strstr(t, "old.test"));
	g_free(t);

	online_store_set_flag(s, "SaveHistory", FALSE);
	online_store_record_visit(s, "https://private.test/", "x", FALSE);     /* history off */
	online_store_flush(s);
	t = contents();
	g_assert_null(strstr(t, "private.test"));
	g_free(t);
	online_store_clear_history(s);
	h = online_store_history(s, 10);
	g_assert_cmpuint(h->len, ==, 0);
	g_ptr_array_unref(h);
	online_store_free(s);
	teardown();
}

static void test_missing_or_broken_file(void)
{
	setup();
	OnlineStore *s = online_store_new(path);                                /* no file yet: defaults */
	g_assert_cmpstr(online_store_settings(s)->search_engine, ==, "DuckDuckGo");
	g_assert_true(online_store_settings(s)->block_trackers);
	g_assert_true(online_store_toggle_bookmark(s, "https://a.test/", NULL));
	online_store_free(s);
	g_file_set_contents(path, "{ not json", -1, NULL);
	s = online_store_new(path);
	g_assert_true(online_store_settings(s)->save_history);
	online_store_free(s);
	teardown();
}

int main(int argc, char **argv)
{
	g_test_init(&argc, &argv, NULL);
	g_test_add_func("/store/reads-the-shells-file", test_reads_the_shells_file);
	g_test_add_func("/store/changes-keep-everything-else", test_changes_keep_everything_else);
	g_test_add_func("/store/history", test_history);
	g_test_add_func("/store/missing-or-broken-file", test_missing_or_broken_file);
	return g_test_run();
}
