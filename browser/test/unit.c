// SPDX-License-Identifier: GPL-3.0-or-later
// Red Star Online without a window: the filter converter, the address bar, file names.
#include <glib.h>
#include <glib/gstdio.h>
#include <string.h>
#include "../src/adblock.h"
#include "../src/util.h"

static void contains(const char *hay, const char *needle)
{
	if (!strstr(hay, needle)) g_error("expected %s\n  in %s", needle, hay);
}
static void lacks(const char *hay, const char *needle)
{
	if (strstr(hay, needle)) g_error("didn't expect %s\n  in %s", needle, hay);
}

static void test_patterns(void)
{
	char *r = adblock_pattern_regex("||ads.example.com^");
	g_assert_cmpstr(r, ==, "^[a-z][a-z0-9.+-]*://+([^/?#]*\\.)?ads\\.example\\.com[^a-zA-Z0-9_.%-]");
	g_free(r);
	r = adblock_pattern_regex("|https://x.test/a|");
	g_assert_cmpstr(r, ==, "^https://x\\.test/a$");
	g_free(r);
	r = adblock_pattern_regex("/banner/*/ad.");
	g_assert_cmpstr(r, ==, "/banner/.*/ad\\.");
	g_free(r);
	r = adblock_pattern_regex("*ad?id=(1)*");
	g_assert_cmpstr(r, ==, "ad\\?id=\\(1\\)");                     /* outer wildcards dropped, specials escaped */
	g_free(r);
	g_assert_null(adblock_pattern_regex("*"));                          /* would match everything */
	g_assert_null(adblock_pattern_regex("||^"));
	g_assert_null(adblock_pattern_regex("/ad\xc3\xa9"));                /* not ASCII */
}

static void test_network_rules(void)
{
	AdblockStats st;
	char *j = adblock_to_webkit(
		"[Adblock Plus 2.0]\n! a comment\n"
		"||tracker.test^$third-party\n"
		"/ads/banner.$image,script,domain=news.test|~sports.news.test\n"
		"||cdn.test/x.js$~script\n"
		"||frames.test^$subdocument,1p\n"
		"@@||tracker.test/ok.js$script\n"
		"@@||friendly.test^$document\n"
		"||rewrite.test^$redirect=noop.js\n"
		"/^https?:\\/\\/[a-z]+\\.ads\\./$script\n"
		"||case.test/Ad$match-case\n",
		NULL, TRUE, &st);
	contains(j, "\"load-type\":[\"third-party\"]");
	contains(j, "\"resource-type\":[\"image\",\"script\"]");
	contains(j, "\"if-domain\":[\"*news.test\"]");            /* includes win over excludes (WebKit takes one) */
	contains(j, "\"resource-type\":[\"document\",\"image\",\"style-sheet\",\"font\",\"raw\",\"media\"]");   /* ~script */
	contains(j, "\"load-type\":[\"first-party\"]");
	contains(j, "\"url-filter-is-case-sensitive\":true");
	contains(j, "\"if-domain\":[\"*friendly.test\"]},\"action\":{\"type\":\"ignore-previous-rules\"}");
	lacks(j, "rewrite.test");                                  /* $redirect: can't honour, left out */
	lacks(j, "[a-z]+");                                        /* /regex/ rules left out */
	g_assert_cmpint(st.network, ==, 5);
	g_assert_cmpint(st.exceptions, ==, 2);
	g_assert_cmpint(st.skipped, ==, 2);
	/* blocks come before the exceptions that cancel them */
	g_assert_true(strstr(j, "tracker\\\\.test[^") < strstr(j, "ignore-previous-rules"));
	g_free(j);
}

static void test_cosmetic_rules(void)
{
	AdblockStats st;
	char *j = adblock_to_webkit(
		"##.ad-box\n"
		"news.test,~m.news.test##div[id^=\"sponsor\"]\n"
		"~shop.test##.promo\n"
		"example.*##.ad\n"
		"news.test##.x:has-text(Sponsored)\n"
		"news.test#@#.ad-box\n"
		"news.test##+js(set-constant, x, 1)\n"
		"news.test#?#.y:-abp-contains(ad)\n",
		NULL, TRUE, &st);
	contains(j, "{\"trigger\":{\"url-filter\":\".*\"},\"action\":{\"type\":\"css-display-none\",\"selector\":\".ad-box\"}}");
	contains(j, "\"if-domain\":[\"*news.test\"]},\"action\":{\"type\":\"css-display-none\",\"selector\":\"div[id^=\\\"sponsor\\\"]\"}");
	contains(j, "\"unless-domain\":[\"*shop.test\"]");
	lacks(j, "has-text");
	lacks(j, "\".ad\"");                                       /* example.* can't be expressed: not made global */
	g_assert_cmpint(st.cosmetic, ==, 3);
	g_assert_cmpint(st.skipped, ==, 5);
	g_free(j);

	/* without cosmetic rules (WebKit rejected a selector): network rules only */
	j = adblock_to_webkit("##.ad-box\n||ads.test^\n", NULL, FALSE, &st);
	lacks(j, "css-display-none");
	contains(j, "ads\\\\.test");
	g_free(j);
}

static void test_allow_sites_and_empty(void)
{
	const char *allow[] = { "Bank.Test", "bad host", NULL };
	char *j = adblock_to_webkit("||ads.test^\n", allow, TRUE, NULL);
	g_assert_true(g_str_has_suffix(j, "{\"trigger\":{\"url-filter\":\".*\",\"if-domain\":[\"*bank.test\"]},\"action\":{\"type\":\"ignore-previous-rules\"}}]"));
	lacks(j, "bad host");
	g_free(j);
	j = adblock_to_webkit("! only comments\n", NULL, TRUE, NULL);
	contains(j, "redstar-never");                              /* WebKit refuses an empty list */
	g_free(j);
}

static void test_address_bar(void)
{
	const char *ddg = "https://duckduckgo.com/?q={0}";
	struct { const char *in, *out; } cases[] = {
		{ "https://example.com/a", "https://example.com/a" },
		{ "  example.com  ", "https://example.com" },
		{ "sub.example.co.uk/path?q=1", "https://sub.example.co.uk/path?q=1" },
		{ "localhost:3000/x", "http://localhost:3000/x" },
		{ "192.168.1.1", "http://192.168.1.1" },
		{ "8.8.8.8", "https://8.8.8.8" },
		{ "redstar:start", "redstar:start" },
		{ "about:blank", "about:blank" },
		{ "how do magnets work", "https://duckduckgo.com/?q=how%20do%20magnets%20work" },
		{ "c# & .net", "https://duckduckgo.com/?q=c%23%20%26%20.net" },
		{ "readme.md2", "https://duckduckgo.com/?q=readme.md2" },     /* a digit in the "TLD": a search */
		{ "1.5", "https://duckduckgo.com/?q=1.5" },
	};
	for (size_t i = 0; i < G_N_ELEMENTS(cases); i++) {
		char *u = online_url_from_input(cases[i].in, ddg);
		g_assert_cmpstr(u, ==, cases[i].out);
		g_free(u);
	}
	char *u = online_url_from_input("rust", "https://www.google.com/search?q={0}&hl=en");
	g_assert_cmpstr(u, ==, "https://www.google.com/search?q=rust&hl=en");
	g_free(u);
	u = online_url_from_input("/tmp/page.html", ddg);
	g_assert_cmpstr(u, ==, "file:///tmp/page.html");
	g_free(u);
	g_assert_null(online_url_from_input("   ", ddg));
}

static void test_hosts_and_blocking(void)
{
	char *h = online_host("https://User@WWW.Example.COM:8443/p?q");
	g_assert_cmpstr(h, ==, "www.example.com");
	g_free(h);
	g_assert_true(online_host_matches("a.b.example.com", "example.com"));
	g_assert_false(online_host_matches("badexample.com", "example.com"));
	g_assert_true(online_site_blocked("  https://Casino.test/ \nother.test", "http://www.casino.test/x"));
	g_assert_false(online_site_blocked("casino.test", "https://notcasino.test/"));
	g_assert_false(online_site_blocked("", "https://casino.test/"));
}

static void test_unique_paths(void)
{
	gchar *dir = g_dir_make_tmp("online-test-XXXXXX", NULL);
	gchar *p1 = online_unique_path(dir, "report.tar.gz");
	g_assert_true(g_str_has_suffix(p1, "/report.tar.gz"));
	g_file_set_contents(p1, "x", 1, NULL);
	gchar *p2 = online_unique_path(dir, "report.tar.gz");
	g_assert_true(g_str_has_suffix(p2, "/report (2).tar.gz"));
	gchar *p3 = online_unique_path(dir, "../../etc/passwd");      /* only the name counts */
	g_assert_true(g_str_has_prefix(p3, dir));
	g_assert_true(g_str_has_suffix(p3, "/passwd"));
	gchar *p4 = online_unique_path(dir, "");
	g_assert_true(g_str_has_suffix(p4, "/download"));
	g_remove(p1);
	g_rmdir(dir);
	g_free(p1); g_free(p2); g_free(p3); g_free(p4); g_free(dir);
	gchar *e = online_html_escape("<b>\"Tom\" & 'Jerry'</b>");
	g_assert_cmpstr(e, ==, "&lt;b&gt;&quot;Tom&quot; &amp; &apos;Jerry&apos;&lt;/b&gt;");
	g_free(e);
}

static void test_menu_labels(void)
{
	char *s = online_menu_label("Short", "https://a.example/", 48);
	g_assert_cmpstr(s, ==, "Short");
	g_free(s);
	/* a long title is cut, with an ellipsis, to the length asked for */
	s = online_menu_label("The very long title of a video about the history of the workers' movement in America", "https://www.youtube.com/watch?v=x", 30);
	g_assert_cmpint(g_utf8_strlen(s, -1), <=, 30);
	g_assert_true(g_str_has_suffix(s, "…"));
	g_free(s);
	/* characters, not bytes */
	s = online_menu_label("Ünïcödé ünïcödé ünïcödé", "https://a.example/", 10);
	g_assert_cmpint(g_utf8_strlen(s, -1), ==, 10);
	g_assert_true(g_utf8_validate(s, -1, NULL));
	g_free(s);
	/* no title: the host */
	s = online_menu_label("", "https://News.Example.org/page", 48);
	g_assert_cmpstr(s, ==, "news.example.org");
	g_free(s);
	s = online_menu_label(NULL, NULL, 48);
	g_assert_cmpstr(s, ==, "Untitled");
	g_free(s);
	/* an underscore stays an underscore, not a shortcut's mark */
	s = online_menu_label("my_file_name", "https://a.example/", 48);
	g_assert_cmpstr(s, ==, "my__file__name");
	g_free(s);
}

int main(int argc, char **argv)
{
	g_test_init(&argc, &argv, NULL);
	g_test_add_func("/adblock/patterns", test_patterns);
	g_test_add_func("/adblock/network", test_network_rules);
	g_test_add_func("/adblock/cosmetic", test_cosmetic_rules);
	g_test_add_func("/adblock/allow-sites", test_allow_sites_and_empty);
	g_test_add_func("/address-bar", test_address_bar);
	g_test_add_func("/hosts", test_hosts_and_blocking);
	g_test_add_func("/download-names", test_unique_paths);
	g_test_add_func("/menu-labels", test_menu_labels);
	return g_test_run();
}
