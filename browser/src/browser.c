// SPDX-License-Identifier: GPL-3.0-or-later
// Red Star Online: Red Star OS's web browser. GTK 4 and WebKitGTK, its own native window (so typing, scrolling and
// the clipboard go straight to the page), sharing bookmarks, history and settings with the Red Star shell.
//   redstar-online [--private] [--new-window] [URL…]    redstar-online --self-test
#include <gtk/gtk.h>
#include <webkit/webkit.h>
#include <json-glib/json-glib.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "store.h"
#include "filters.h"
#include "util.h"

#define APP_ID "org.redstaros.Online"
#define START_URL "redstar:start"

typedef struct Browser Browser;

typedef struct {
	Browser *b;
	WebKitWebView *view;
	GtkWidget *page;            /* overlay: the view and the link-under-the-pointer label */
	GtkWidget *status;
	GtkWidget *label_box, *icon, *title;
	gboolean recorded;          /* this load's visit is in the history */
	char *uri;                  /* kept here: the view is gone by the time the tab is freed (for Ctrl+Shift+T) */
} Tab;

struct Browser {
	GtkWindow *win;
	GtkNotebook *tabs;
	GtkEntry *address;
	GtkWidget *back, *forward, *reload, *menu_button, *downloads_button, *bookmarks_bar, *bookmarks_box;
	GtkSearchBar *find_bar;
	GtkSearchEntry *find_entry;
	GtkLabel *find_count;
	GMenu *menu;
	gboolean private, editing;
	WebKitNetworkSession *session;
	GPtrArray *closed;          /* URLs of closed tabs, for Ctrl+Shift+T */
};

typedef struct {
	WebKitDownload *dl;
	GtkWidget *row, *name, *bar, *state, *open;
	char *path;
} Download;

static struct {
	GtkApplication *gtk;
	OnlineStore *store;
	OnlineFilters *filters;
	WebKitNetworkSession *session;
	WebKitUserContentManager *ucm;
	WebKitSettings *settings;
	GList *windows;              /* Browser* */
	GtkWidget *downloads_list;  /* shared by every window's downloads popover */
	GPtrArray *downloads;
	gboolean self_test;
	int exit_status;
} app;

static Tab *tab_new(Browser *b, WebKitWebView *related, const char *url, gboolean focus);
static void browser_update(Browser *b);
static void rebuild_bookmarks(void);
static void rebuild_menus(void);
static void selftest_start(void);
static void selftest_event(const char *what);

static Tab *tab_of(GtkWidget *page) { return page ? g_object_get_data(G_OBJECT(page), "tab") : NULL; }
static Tab *current_tab(Browser *b)
{
	int i = gtk_notebook_get_current_page(b->tabs);
	return i < 0 ? NULL : tab_of(gtk_notebook_get_nth_page(b->tabs, i));
}
static Browser *last_window(gboolean private)
{
	for (GList *l = app.windows; l; l = l->next) if (((Browser *)l->data)->private == private) return l->data;
	return NULL;
}

static const char *search_template(void) { return online_store_search_template(app.store); }

static void load(Tab *t, const char *url)
{
	if (!t || !url) return;
	t->recorded = FALSE;
	webkit_web_view_load_uri(t->view, url);
}

// ───────────────────────────── pages of our own: start, errors ─────────────────────────────

static const char *page_css =
	"<style>:root{color-scheme:light dark;font-family:system-ui,sans-serif}"
	"body{max-width:860px;margin:8vh auto;padding:0 24px}h1{font-weight:600;margin:0 0 6px}"
	".muted{opacity:.7}form{display:flex;margin:28px 0}input{flex:1;font-size:18px;padding:12px 16px;border-radius:24px;"
	"border:1px solid #8886;background:Canvas;color:CanvasText}"
	".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:10px;margin-top:14px}"
	".grid a{display:block;padding:12px 14px;border-radius:10px;background:#8881;color:inherit;text-decoration:none;"
	"overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.grid a:hover{background:#c628281f}"
	"h2{font-size:15px;font-weight:600;margin-top:28px}.star{color:#c62828}a.btn{display:inline-block;margin-top:18px;"
	"padding:8px 16px;border-radius:8px;background:#c62828;color:white;text-decoration:none}code{word-break:break-all}</style>";

static gchar *start_page_html(gboolean private)
{
	GString *h = g_string_new("<!doctype html><html><head><meta charset=utf-8><title>New tab</title>");
	g_string_append(h, page_css);
	g_string_append(h, "</head><body><h1><span class=star>★</span> Red Star Online</h1>");
	if (private) g_string_append(h, "<p class=muted>Private window: nothing you do here is remembered when it closes.</p>");
	gchar *tpl = g_strescape(search_template(), NULL);
	g_string_append_printf(h, "<form id=f><input id=q placeholder=\"Search the web or type an address\" autofocus></form>"
		"<script>document.getElementById('f').onsubmit=function(e){e.preventDefault();var v=document.getElementById('q').value.trim();"
		"if(v)location.href=\"%s\".replace('{0}',encodeURIComponent(v));};</script>", tpl);
	g_free(tpl);
	GPtrArray *bm = online_store_bookmarks(app.store);
	if (bm->len) {
		g_string_append(h, "<h2>Bookmarks</h2><div class=grid>");
		for (guint i = bm->len; i-- > 0 && bm->len - i <= 24;) {
			OnlineLink *l = bm->pdata[i];
			gchar *u = online_html_escape(l->url), *t = online_html_escape(l->title);
			g_string_append_printf(h, "<a href=\"%s\" title=\"%s\">%s</a>", u, u, t);
			g_free(u); g_free(t);
		}
		g_string_append(h, "</div>");
	}
	g_ptr_array_unref(bm);
	if (!private) {
		GPtrArray *hist = online_store_history(app.store, 8);
		if (hist->len) {
			g_string_append(h, "<h2>Recently visited</h2><div class=grid>");
			for (guint i = 0; i < hist->len; i++) {
				OnlineLink *l = hist->pdata[i];
				gchar *u = online_html_escape(l->url), *t = online_html_escape(l->title);
				g_string_append_printf(h, "<a href=\"%s\" title=\"%s\">%s</a>", u, u, t);
				g_free(u); g_free(t);
			}
			g_string_append(h, "</div>");
		}
		g_ptr_array_unref(hist);
	}
	g_string_append(h, "</body></html>");
	return g_string_free(h, FALSE);
}

static void scheme_request(WebKitURISchemeRequest *req, gpointer data)
{
	(void)data;
	WebKitWebView *v = webkit_uri_scheme_request_get_web_view(req);
	gboolean private = FALSE;
	for (GList *l = app.windows; l; l = l->next) {
		Browser *b = l->data;
		for (int i = 0; i < gtk_notebook_get_n_pages(b->tabs); i++)
			if (tab_of(gtk_notebook_get_nth_page(b->tabs, i))->view == v) private = b->private;
	}
	gchar *html = start_page_html(private);
	gsize len = strlen(html);
	GInputStream *in = g_memory_input_stream_new_from_data(html, (gssize)len, g_free);
	webkit_uri_scheme_request_finish(req, in, (gint64)len, "text/html");
	g_object_unref(in);
}

static void show_error(Tab *t, const char *url, const char *title, const char *detail)
{
	gchar *u = online_html_escape(url), *ti = online_html_escape(title), *d = online_html_escape(detail);
	gchar *html = g_strdup_printf("<!doctype html><html><head><meta charset=utf-8><title>%s</title>%s</head><body>"
		"<h1>%s</h1><p class=muted>%s</p><p><code>%s</code></p><a class=btn href=\"%s\">Try again</a></body></html>",
		ti, page_css, ti, d, u, u);
	webkit_web_view_load_alternate_html(t->view, html, url, NULL);
	g_free(html); g_free(u); g_free(ti); g_free(d);
}

// ───────────────────────────── the window's controls follow the current tab ─────────────────────────────

static void update_star(Browser *b, Tab *t)
{
	const char *uri = t ? webkit_web_view_get_uri(t->view) : NULL;
	gboolean on = uri && online_store_is_bookmarked(app.store, uri);
	gtk_entry_set_icon_from_icon_name(b->address, GTK_ENTRY_ICON_SECONDARY, on ? "starred-symbolic" : "non-starred-symbolic");
	gtk_entry_set_icon_tooltip_text(b->address, GTK_ENTRY_ICON_SECONDARY, on ? "Remove bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)");
}

static void update_security(Browser *b, Tab *t)
{
	const char *uri = t ? webkit_web_view_get_uri(t->view) : NULL;
	const char *icon = NULL, *tip = NULL;
	if (uri && g_str_has_prefix(uri, "https://")) {
		GTlsCertificate *cert = NULL;
		GTlsCertificateFlags flags = 0;
		if (webkit_web_view_get_tls_info(t->view, &cert, &flags) && flags == 0) { icon = "channel-secure-symbolic"; tip = "Secure connection"; }
		else { icon = "channel-insecure-symbolic"; tip = "This site's certificate has a problem"; }
	} else if (uri && g_str_has_prefix(uri, "http://")) { icon = "channel-insecure-symbolic"; tip = "Not secure: the connection isn't encrypted"; }
	gtk_entry_set_icon_from_icon_name(b->address, GTK_ENTRY_ICON_PRIMARY, icon);
	gtk_entry_set_icon_tooltip_text(b->address, GTK_ENTRY_ICON_PRIMARY, tip);
}

static void set_address(Browser *b, Tab *t)
{
	if (b->editing) return;
	const char *uri = t ? webkit_web_view_get_uri(t->view) : NULL;
	gtk_editable_set_text(GTK_EDITABLE(b->address), uri && strcmp(uri, START_URL) && strcmp(uri, "about:blank") ? uri : "");
}

static void update_title(Browser *b, Tab *t)
{
	const char *title = t ? webkit_web_view_get_title(t->view) : NULL;
	gchar *s = g_strdup_printf("%s%s%sRed Star Online", title && *title ? title : "", title && *title ? " — " : "",
		b->private ? "Private — " : "");
	gtk_window_set_title(b->win, s);
	g_free(s);
}

static void browser_update(Browser *b)
{
	Tab *t = current_tab(b);
	gtk_widget_set_sensitive(b->back, t && webkit_web_view_can_go_back(t->view));
	gtk_widget_set_sensitive(b->forward, t && webkit_web_view_can_go_forward(t->view));
	gboolean loading = t && webkit_web_view_is_loading(t->view);
	gtk_button_set_icon_name(GTK_BUTTON(b->reload), loading ? "process-stop-symbolic" : "view-refresh-symbolic");
	gtk_widget_set_tooltip_text(b->reload, loading ? "Stop" : "Reload (Ctrl+R)");
	gtk_entry_set_progress_fraction(b->address, loading ? webkit_web_view_get_estimated_load_progress(t->view) : 0);
	set_address(b, t);
	update_star(b, t);
	update_security(b, t);
	update_title(b, t);
	GAction *site = g_action_map_lookup_action(G_ACTION_MAP(b->win), "block-site");
	if (site && t) {
		gchar *host = online_host(webkit_web_view_get_uri(t->view));
		g_simple_action_set_enabled(G_SIMPLE_ACTION(site), *host && online_store_settings(app.store)->block_trackers);
		g_simple_action_set_state(G_SIMPLE_ACTION(site), g_variant_new_boolean(!*host || online_store_site_blocking(app.store, host)));
		g_free(host);
	}
	gtk_notebook_set_show_tabs(b->tabs, gtk_notebook_get_n_pages(b->tabs) > 1 && !gtk_window_is_fullscreen(b->win));
}

// ───────────────────────────── one tab ─────────────────────────────

static void tab_title_changed(WebKitWebView *v, GParamSpec *p, Tab *t)
{
	(void)p;
	const char *title = webkit_web_view_get_title(v);
	const char *uri = webkit_web_view_get_uri(v);
	if (uri && g_strcmp0(uri, t->uri)) { g_free(t->uri); t->uri = g_strdup(uri); }
	gtk_label_set_text(GTK_LABEL(t->title), title && *title ? title : uri && strcmp(uri, START_URL) ? uri : "New tab");
	gtk_widget_set_tooltip_text(t->label_box, title && *title ? title : uri);
	if (!t->b->private && title && *title) online_store_update_title(app.store, uri, title);
	if (current_tab(t->b) == t) update_title(t->b, t);
}

static void tab_favicon_changed(WebKitWebView *v, GParamSpec *p, Tab *t)
{
	(void)p;
	GdkTexture *icon = webkit_web_view_get_favicon(v);
	if (icon) gtk_image_set_from_paintable(GTK_IMAGE(t->icon), GDK_PAINTABLE(icon));
	else gtk_image_set_from_icon_name(GTK_IMAGE(t->icon), "web-browser-symbolic");
}

static void tab_state_changed(WebKitWebView *v, GParamSpec *p, Tab *t)
{
	(void)v; (void)p;
	gtk_widget_set_opacity(t->icon, webkit_web_view_is_loading(t->view) ? 0.45 : 1.0);
	if (current_tab(t->b) == t) browser_update(t->b);
}

static void tab_load_changed(WebKitWebView *v, WebKitLoadEvent ev, Tab *t)
{
	const char *uri = webkit_web_view_get_uri(v);
	if (ev == WEBKIT_LOAD_STARTED) t->recorded = FALSE;
	if (ev == WEBKIT_LOAD_COMMITTED) {
		gchar *host = online_host(uri);
		webkit_web_view_set_zoom_level(v, *host ? online_store_zoom(app.store, host) : 1.0);
		g_free(host);
	}
	if (ev == WEBKIT_LOAD_FINISHED) {
		if (!t->b->private && !t->recorded && uri && strcmp(uri, START_URL)) {
			online_store_record_visit(app.store, uri, webkit_web_view_get_title(v), app.self_test);
			t->recorded = TRUE;
		}
		if (app.self_test) selftest_event("loaded");
	}
	if (current_tab(t->b) == t) browser_update(t->b);
}

static gboolean tab_load_failed(WebKitWebView *v, WebKitLoadEvent ev, char *uri, GError *err, Tab *t)
{
	(void)v; (void)ev;
	if (g_error_matches(err, WEBKIT_NETWORK_ERROR, WEBKIT_NETWORK_ERROR_CANCELLED)
	    || g_error_matches(err, WEBKIT_POLICY_ERROR, WEBKIT_POLICY_ERROR_FRAME_LOAD_INTERRUPTED_BY_POLICY_CHANGE)
	    || g_error_matches(err, WEBKIT_MEDIA_ERROR, WEBKIT_MEDIA_ERROR_WILL_HANDLE_LOAD))
		return FALSE;                                   /* stopped, or turned into a download */
	show_error(t, uri, "This page didn't load", err->message);
	return TRUE;
}

static gboolean tab_tls_failed(WebKitWebView *v, char *uri, GTlsCertificate *cert, GTlsCertificateFlags flags, Tab *t)
{
	(void)v; (void)cert; (void)flags;
	show_error(t, uri, "This connection isn't private",
		"The site's certificate couldn't be verified, so someone could be reading or changing what you send and receive. "
		"Red Star Online didn't open it.");
	return TRUE;
}

static void tab_crashed(WebKitWebView *v, WebKitWebProcessTerminationReason why, Tab *t)
{
	show_error(t, webkit_web_view_get_uri(v), "This page stopped working",
		why == WEBKIT_WEB_PROCESS_EXCEEDED_MEMORY_LIMIT ? "It used too much memory." : "Its process ended unexpectedly.");
}

static void tab_hover(WebKitWebView *v, WebKitHitTestResult *hit, guint mods, Tab *t)
{
	(void)v; (void)mods;
	gboolean link = webkit_hit_test_result_context_is_link(hit);
	if (link) gtk_label_set_text(GTK_LABEL(t->status), webkit_hit_test_result_get_link_uri(hit));
	gtk_widget_set_visible(t->status, link);
}

static GtkWidget *tab_create(WebKitWebView *v, WebKitNavigationAction *action, Tab *t)
{
	(void)action;
	/* target=_blank and window.open: a new tab next to this one */
	Tab *n = tab_new(t->b, v, NULL, TRUE);
	return GTK_WIDGET(n->view);
}

static void tab_close_request(WebKitWebView *v, Tab *t)
{
	(void)v;
	int i = gtk_notebook_page_num(t->b->tabs, t->page);
	if (i >= 0) gtk_notebook_remove_page(t->b->tabs, i);
}

static gboolean tab_decide(WebKitWebView *v, WebKitPolicyDecision *d, WebKitPolicyDecisionType type, Tab *t)
{
	(void)v;
	if (type == WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION) {
		WebKitNavigationAction *a = webkit_navigation_policy_decision_get_navigation_action(WEBKIT_NAVIGATION_POLICY_DECISION(d));
		const char *uri = webkit_uri_request_get_uri(webkit_navigation_action_get_request(a));
		if (online_site_blocked(online_store_settings(app.store)->blocked_sites, uri)) {
			webkit_policy_decision_ignore(d);
			show_error(t, uri, "This site is blocked", "It's on the list of blocked sites in Red Star Online's settings.");
			return TRUE;
		}
		/* middle click or Ctrl+click on a link: a new tab in the background */
		guint button = webkit_navigation_action_get_mouse_button(a);
		guint mods = webkit_navigation_action_get_modifiers(a);
		if (webkit_navigation_action_get_navigation_type(a) == WEBKIT_NAVIGATION_TYPE_LINK_CLICKED
		    && (button == 2 || (mods & GDK_CONTROL_MASK))) {
			webkit_policy_decision_ignore(d);
			tab_new(t->b, NULL, uri, FALSE);
			return TRUE;
		}
	} else if (type == WEBKIT_POLICY_DECISION_TYPE_RESPONSE) {
		WebKitResponsePolicyDecision *r = WEBKIT_RESPONSE_POLICY_DECISION(d);
		if (webkit_response_policy_decision_is_main_frame_main_resource(r) && !webkit_response_policy_decision_is_mime_type_supported(r)) {
			webkit_policy_decision_download(d);
			return TRUE;
		}
	}
	return FALSE;
}

// permissions: ask once per site, remember the answer
typedef struct { WebKitPermissionRequest *req; char *host, *kind; } Ask;

static void permission_answered(GObject *src, GAsyncResult *res, gpointer data)
{
	Ask *a = data;
	int choice = gtk_alert_dialog_choose_finish(GTK_ALERT_DIALOG(src), res, NULL);
	gboolean allow = choice == 1;
	if (choice >= 0 && a->host && *a->host) online_store_set_permission(app.store, a->host, a->kind, allow);
	if (allow) webkit_permission_request_allow(a->req); else webkit_permission_request_deny(a->req);
	g_object_unref(a->req);
	g_free(a->host); g_free(a->kind); g_free(a);
}

static gboolean tab_permission(WebKitWebView *v, WebKitPermissionRequest *req, Tab *t)
{
	const char *kind = NULL, *what = NULL;
	if (WEBKIT_IS_GEOLOCATION_PERMISSION_REQUEST(req)) { kind = "geolocation"; what = "know your location"; }
	else if (WEBKIT_IS_NOTIFICATION_PERMISSION_REQUEST(req)) { kind = "notifications"; what = "show notifications"; }
	else if (WEBKIT_IS_USER_MEDIA_PERMISSION_REQUEST(req)) {
		gboolean cam = webkit_user_media_permission_is_for_video_device(WEBKIT_USER_MEDIA_PERMISSION_REQUEST(req));
		gboolean mic = webkit_user_media_permission_is_for_audio_device(WEBKIT_USER_MEDIA_PERMISSION_REQUEST(req));
		kind = cam && mic ? "camera+microphone" : cam ? "camera" : "microphone";
		what = cam && mic ? "use your camera and microphone" : cam ? "use your camera" : "use your microphone";
	} else if (WEBKIT_IS_CLIPBOARD_PERMISSION_REQUEST(req)) { kind = "clipboard"; what = "read your clipboard"; }
	else if (WEBKIT_IS_MEDIA_KEY_SYSTEM_PERMISSION_REQUEST(req)) { kind = "drm"; what = "play protected (DRM) media"; }
	else if (WEBKIT_IS_POINTER_LOCK_PERMISSION_REQUEST(req)) { webkit_permission_request_allow(req); return TRUE; }   /* games: Esc releases it */
	else { webkit_permission_request_deny(req); return TRUE; }
	gchar *host = online_host(webkit_web_view_get_uri(v));
	int known = online_store_permission(app.store, host, kind);
	if (known >= 0 || app.self_test) {
		if (known == 1) webkit_permission_request_allow(req); else webkit_permission_request_deny(req);
		g_free(host);
		return TRUE;
	}
	Ask *a = g_new0(Ask, 1);
	a->req = g_object_ref(req);
	a->host = host;
	a->kind = g_strdup(kind);
	GtkAlertDialog *dlg = gtk_alert_dialog_new("Let %s %s?", *host ? host : "this page", what);
	gtk_alert_dialog_set_detail(dlg, "Red Star Online remembers your answer for this site.");
	const char *buttons[] = { "Block", "Allow", NULL };
	gtk_alert_dialog_set_buttons(dlg, buttons);
	gtk_alert_dialog_set_cancel_button(dlg, 0);
	gtk_alert_dialog_set_default_button(dlg, 0);
	gtk_alert_dialog_choose(dlg, t->b->win, NULL, permission_answered, a);
	g_object_unref(dlg);
	return TRUE;
}

static gboolean tab_fullscreen(WebKitWebView *v, Tab *t)
{
	(void)v;
	/* a video going full screen: no tabs or bookmarks bar (WebKit makes the window full screen itself) */
	gtk_notebook_set_show_tabs(t->b->tabs, FALSE);
	gtk_widget_set_visible(t->b->bookmarks_bar, FALSE);
	return FALSE;
}

static gboolean tab_unfullscreen(WebKitWebView *v, Tab *t)
{
	(void)v;
	rebuild_bookmarks();
	gtk_notebook_set_show_tabs(t->b->tabs, gtk_notebook_get_n_pages(t->b->tabs) > 1);
	return FALSE;
}

static void tab_close_clicked(GtkButton *btn, Tab *t)
{
	(void)btn;
	int i = gtk_notebook_page_num(t->b->tabs, t->page);
	if (i >= 0) gtk_notebook_remove_page(t->b->tabs, i);
}

static void tab_middle_click(GtkGestureClick *g, int n, double x, double y, Tab *t)
{
	(void)g; (void)n; (void)x; (void)y;
	tab_close_clicked(NULL, t);
}

static void tab_free(Tab *t)
{
	if (t->uri && strcmp(t->uri, START_URL) && !t->b->private) g_ptr_array_add(t->b->closed, t->uri);
	else g_free(t->uri);
	g_free(t);
}

static Tab *tab_new(Browser *b, WebKitWebView *related, const char *url, gboolean focus)
{
	Tab *t = g_new0(Tab, 1);
	t->b = b;
	if (related)
		t->view = WEBKIT_WEB_VIEW(g_object_new(WEBKIT_TYPE_WEB_VIEW, "related-view", related, NULL));
	else
		t->view = WEBKIT_WEB_VIEW(g_object_new(WEBKIT_TYPE_WEB_VIEW, "network-session", b->session,
			"user-content-manager", app.ucm, "settings", app.settings, NULL));
	gtk_widget_set_hexpand(GTK_WIDGET(t->view), TRUE);
	gtk_widget_set_vexpand(GTK_WIDGET(t->view), TRUE);

	t->page = gtk_overlay_new();
	gtk_overlay_set_child(GTK_OVERLAY(t->page), GTK_WIDGET(t->view));
	t->status = gtk_label_new(NULL);
	gtk_label_set_ellipsize(GTK_LABEL(t->status), PANGO_ELLIPSIZE_MIDDLE);
	gtk_label_set_max_width_chars(GTK_LABEL(t->status), 90);
	gtk_widget_set_halign(t->status, GTK_ALIGN_START);
	gtk_widget_set_valign(t->status, GTK_ALIGN_END);
	gtk_widget_add_css_class(t->status, "link-status");
	gtk_widget_set_can_target(t->status, FALSE);
	gtk_widget_set_visible(t->status, FALSE);
	gtk_overlay_add_overlay(GTK_OVERLAY(t->page), t->status);
	g_object_set_data_full(G_OBJECT(t->page), "tab", t, (GDestroyNotify)tab_free);

	t->label_box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6);
	t->icon = gtk_image_new_from_icon_name("web-browser-symbolic");
	gtk_image_set_pixel_size(GTK_IMAGE(t->icon), 16);
	t->title = gtk_label_new("New tab");
	gtk_label_set_ellipsize(GTK_LABEL(t->title), PANGO_ELLIPSIZE_END);
	gtk_label_set_width_chars(GTK_LABEL(t->title), 14);
	gtk_label_set_max_width_chars(GTK_LABEL(t->title), 22);
	gtk_widget_set_hexpand(t->title, TRUE);
	gtk_label_set_xalign(GTK_LABEL(t->title), 0);
	GtkWidget *close = gtk_button_new_from_icon_name("window-close-symbolic");
	gtk_widget_add_css_class(close, "flat");
	gtk_widget_add_css_class(close, "tab-close");
	gtk_widget_set_tooltip_text(close, "Close tab (Ctrl+W)");
	g_signal_connect(close, "clicked", G_CALLBACK(tab_close_clicked), t);
	gtk_box_append(GTK_BOX(t->label_box), t->icon);
	gtk_box_append(GTK_BOX(t->label_box), t->title);
	gtk_box_append(GTK_BOX(t->label_box), close);
	GtkGesture *middle = gtk_gesture_click_new();
	gtk_gesture_single_set_button(GTK_GESTURE_SINGLE(middle), GDK_BUTTON_MIDDLE);
	g_signal_connect(middle, "released", G_CALLBACK(tab_middle_click), t);
	gtk_widget_add_controller(t->label_box, GTK_EVENT_CONTROLLER(middle));

	g_signal_connect(t->view, "notify::title", G_CALLBACK(tab_title_changed), t);
	g_signal_connect(t->view, "notify::uri", G_CALLBACK(tab_title_changed), t);
	g_signal_connect(t->view, "notify::favicon", G_CALLBACK(tab_favicon_changed), t);
	g_signal_connect(t->view, "notify::is-loading", G_CALLBACK(tab_state_changed), t);
	g_signal_connect(t->view, "notify::estimated-load-progress", G_CALLBACK(tab_state_changed), t);
	g_signal_connect(t->view, "notify::uri", G_CALLBACK(tab_state_changed), t);
	g_signal_connect(t->view, "load-changed", G_CALLBACK(tab_load_changed), t);
	g_signal_connect(t->view, "load-failed", G_CALLBACK(tab_load_failed), t);
	g_signal_connect(t->view, "load-failed-with-tls-errors", G_CALLBACK(tab_tls_failed), t);
	g_signal_connect(t->view, "web-process-terminated", G_CALLBACK(tab_crashed), t);
	g_signal_connect(t->view, "mouse-target-changed", G_CALLBACK(tab_hover), t);
	g_signal_connect(t->view, "create", G_CALLBACK(tab_create), t);
	g_signal_connect(t->view, "close", G_CALLBACK(tab_close_request), t);
	g_signal_connect(t->view, "decide-policy", G_CALLBACK(tab_decide), t);
	g_signal_connect(t->view, "permission-request", G_CALLBACK(tab_permission), t);
	g_signal_connect(t->view, "enter-fullscreen", G_CALLBACK(tab_fullscreen), t);
	g_signal_connect(t->view, "leave-fullscreen", G_CALLBACK(tab_unfullscreen), t);

	int cur = gtk_notebook_get_current_page(b->tabs);
	int at = gtk_notebook_insert_page(b->tabs, t->page, t->label_box, related || !focus ? cur + 1 : -1);
	gtk_notebook_set_tab_reorderable(b->tabs, t->page, TRUE);
	if (focus) gtk_notebook_set_current_page(b->tabs, at);
	if (url) load(t, url);
	if (focus) {
		if (!url || !strcmp(url, START_URL)) gtk_widget_grab_focus(GTK_WIDGET(b->address));
		else gtk_widget_grab_focus(GTK_WIDGET(t->view));
	}
	browser_update(b);
	return t;
}

// ───────────────────────────── downloads ─────────────────────────────

static const char *downloads_dir(void)
{
	static gchar *dir;
	if (!dir) {
		const char *d = g_get_user_special_dir(G_USER_DIRECTORY_DOWNLOAD);
		dir = d ? g_strdup(d) : g_build_filename(g_get_home_dir(), "Downloads", NULL);
		g_mkdir_with_parents(dir, 0700);
	}
	return dir;
}

static gboolean download_destination(WebKitDownload *dl, char *suggested, Download *d)
{
	d->path = online_unique_path(downloads_dir(), suggested);
	webkit_download_set_destination(dl, d->path);
	gchar *name = g_path_get_basename(d->path);
	gtk_label_set_text(GTK_LABEL(d->name), name);
	g_free(name);
	return TRUE;
}

static void download_progress(WebKitDownload *dl, GParamSpec *p, Download *d)
{
	(void)p;
	gtk_progress_bar_set_fraction(GTK_PROGRESS_BAR(d->bar), webkit_download_get_estimated_progress(dl));
}

static void download_open(GtkButton *btn, Download *d)
{
	(void)btn;
	if (!d->path) return;
	GFile *f = g_file_new_for_path(d->path);
	GtkFileLauncher *l = gtk_file_launcher_new(f);
	gtk_file_launcher_launch(l, GTK_WINDOW(gtk_widget_get_root(d->row)), NULL, NULL, NULL);
	g_object_unref(l);
	g_object_unref(f);
}

static void download_finished(WebKitDownload *dl, Download *d)
{
	(void)dl;
	if (gtk_widget_get_visible(d->open) || !gtk_widget_get_sensitive(d->bar)) return;   /* failed already */
	gtk_progress_bar_set_fraction(GTK_PROGRESS_BAR(d->bar), 1.0);
	gtk_label_set_text(GTK_LABEL(d->state), "Done");
	gtk_widget_set_visible(d->open, TRUE);
	gchar *name = d->path ? g_path_get_basename(d->path) : g_strdup("A file");
	GNotification *n = g_notification_new("Download finished");
	g_notification_set_body(n, name);
	g_application_send_notification(G_APPLICATION(app.gtk), NULL, n);
	g_object_unref(n);
	g_free(name);
	if (app.self_test) selftest_event("download");
}

static void download_failed(WebKitDownload *dl, GError *err, Download *d)
{
	(void)dl;
	gtk_widget_set_sensitive(d->bar, FALSE);
	gtk_label_set_text(GTK_LABEL(d->state), g_error_matches(err, WEBKIT_DOWNLOAD_ERROR, WEBKIT_DOWNLOAD_ERROR_CANCELLED_BY_USER)
		? "Cancelled" : err->message);
}

static void download_started(WebKitNetworkSession *s, WebKitDownload *dl, gpointer data)
{
	(void)s; (void)data;
	Download *d = g_new0(Download, 1);
	d->dl = g_object_ref(dl);
	d->row = gtk_box_new(GTK_ORIENTATION_VERTICAL, 4);
	gtk_widget_set_margin_top(d->row, 6); gtk_widget_set_margin_bottom(d->row, 6);
	gtk_widget_set_margin_start(d->row, 8); gtk_widget_set_margin_end(d->row, 8);
	GtkWidget *top = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
	d->name = gtk_label_new(webkit_uri_request_get_uri(webkit_download_get_request(dl)));
	gtk_label_set_ellipsize(GTK_LABEL(d->name), PANGO_ELLIPSIZE_MIDDLE);
	gtk_label_set_xalign(GTK_LABEL(d->name), 0);
	gtk_widget_set_hexpand(d->name, TRUE);
	d->open = gtk_button_new_with_label("Open");
	gtk_widget_set_visible(d->open, FALSE);
	g_signal_connect(d->open, "clicked", G_CALLBACK(download_open), d);
	gtk_box_append(GTK_BOX(top), d->name);
	gtk_box_append(GTK_BOX(top), d->open);
	d->bar = gtk_progress_bar_new();
	d->state = gtk_label_new("Downloading…");
	gtk_label_set_xalign(GTK_LABEL(d->state), 0);
	gtk_widget_add_css_class(d->state, "dim-label");
	gtk_box_append(GTK_BOX(d->row), top);
	gtk_box_append(GTK_BOX(d->row), d->bar);
	gtk_box_append(GTK_BOX(d->row), d->state);
	gtk_list_box_prepend(GTK_LIST_BOX(app.downloads_list), d->row);
	g_ptr_array_add(app.downloads, d);
	g_signal_connect(dl, "decide-destination", G_CALLBACK(download_destination), d);
	g_signal_connect(dl, "notify::estimated-progress", G_CALLBACK(download_progress), d);
	g_signal_connect(dl, "finished", G_CALLBACK(download_finished), d);
	g_signal_connect(dl, "failed", G_CALLBACK(download_failed), d);
	for (GList *l = app.windows; l; l = l->next) {
		Browser *b = l->data;
		gtk_widget_set_visible(b->downloads_button, TRUE);
	}
	Browser *b = app.windows ? app.windows->data : NULL;
	if (b && !app.self_test) gtk_menu_button_popup(GTK_MENU_BUTTON(b->downloads_button));
}

static void open_downloads_folder(GtkButton *btn, gpointer data)
{
	(void)data;
	GFile *f = g_file_new_for_path(downloads_dir());
	GtkFileLauncher *l = gtk_file_launcher_new(f);
	gtk_file_launcher_launch(l, GTK_WINDOW(gtk_widget_get_root(GTK_WIDGET(btn))), NULL, NULL, NULL);
	g_object_unref(l);
	g_object_unref(f);
}

static GtkWidget *downloads_popover(void)
{
	GtkWidget *pop = gtk_popover_new();
	GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 6);
	GtkWidget *scroll = gtk_scrolled_window_new();
	gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
	gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(scroll), TRUE);
	gtk_scrolled_window_set_max_content_height(GTK_SCROLLED_WINDOW(scroll), 360);
	gtk_widget_set_size_request(scroll, 340, -1);
	/* one list for every window: whichever popover opens takes it (downloads_popover_show) */
	g_object_set_data(G_OBJECT(pop), "scroll", scroll);
	gtk_box_append(GTK_BOX(box), scroll);
	GtkWidget *folder = gtk_button_new_with_label("Open Downloads folder");
	gtk_widget_add_css_class(folder, "flat");
	g_signal_connect(folder, "clicked", G_CALLBACK(open_downloads_folder), NULL);
	gtk_box_append(GTK_BOX(box), folder);
	gtk_popover_set_child(GTK_POPOVER(pop), box);
	return pop;
}

static void downloads_popover_show(GtkPopover *pop, gpointer data)
{
	(void)data;
	GtkWidget *scroll = g_object_get_data(G_OBJECT(pop), "scroll");
	GtkWidget *parent = gtk_widget_get_parent(app.downloads_list);
	if (parent && gtk_widget_get_parent(parent) == scroll) return;
	if (parent) {
		/* in another window's popover: move it here */
		g_object_ref(app.downloads_list);
		gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(gtk_widget_get_parent(parent)), NULL);
	}
	gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroll), app.downloads_list);
	if (parent) g_object_unref(app.downloads_list);
}

// ───────────────────────────── bookmarks bar and menus ─────────────────────────────

static void bookmark_clicked(GtkButton *btn, Browser *b)
{
	const char *url = g_object_get_data(G_OBJECT(btn), "url");
	Tab *t = current_tab(b);
	if (t) load(t, url); else tab_new(b, NULL, url, TRUE);
}

static void bookmark_middle(GtkGestureClick *g, int n, double x, double y, Browser *b)
{
	(void)n; (void)x; (void)y;
	const char *url = g_object_get_data(G_OBJECT(gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(g))), "url");
	tab_new(b, NULL, url, FALSE);
}

static void rebuild_bookmarks(void)
{
	GPtrArray *bm = online_store_bookmarks(app.store);
	gboolean show = online_store_settings(app.store)->show_bookmarks_bar && bm->len > 0;
	for (GList *l = app.windows; l; l = l->next) {
		Browser *b = l->data;
		GtkWidget *c;
		while ((c = gtk_widget_get_first_child(b->bookmarks_box))) gtk_box_remove(GTK_BOX(b->bookmarks_box), c);
		for (guint i = 0; i < bm->len; i++) {
			OnlineLink *k = bm->pdata[i];
			GtkWidget *btn = gtk_button_new_with_label(k->title);
			gtk_widget_add_css_class(btn, "flat");
			gtk_label_set_ellipsize(GTK_LABEL(gtk_button_get_child(GTK_BUTTON(btn))), PANGO_ELLIPSIZE_END);
			gtk_label_set_max_width_chars(GTK_LABEL(gtk_button_get_child(GTK_BUTTON(btn))), 22);
			gtk_widget_set_tooltip_text(btn, k->url);
			g_object_set_data_full(G_OBJECT(btn), "url", g_strdup(k->url), g_free);
			g_signal_connect(btn, "clicked", G_CALLBACK(bookmark_clicked), b);
			GtkGesture *mid = gtk_gesture_click_new();
			gtk_gesture_single_set_button(GTK_GESTURE_SINGLE(mid), GDK_BUTTON_MIDDLE);
			g_signal_connect(mid, "released", G_CALLBACK(bookmark_middle), b);
			gtk_widget_add_controller(btn, GTK_EVENT_CONTROLLER(mid));
			gtk_box_append(GTK_BOX(b->bookmarks_box), btn);
		}
		gtk_widget_set_visible(b->bookmarks_bar, show && !gtk_window_is_fullscreen(b->win));
		update_star(b, current_tab(b));
	}
	g_ptr_array_unref(bm);
}

static GMenuItem *open_item(const char *label, const char *url)
{
	GMenuItem *it = g_menu_item_new(label, NULL);
	g_menu_item_set_action_and_target_value(it, "win.open", g_variant_new_string(url));
	return it;
}

static void fill_menu(Browser *b, GMenu *m)
{
	GMenu *s1 = g_menu_new();
	g_menu_append(s1, "New Tab", "win.new-tab");
	g_menu_append(s1, "New Window", "app.new-window");
	g_menu_append(s1, "New Private Window", "app.new-private-window");
	g_menu_append_section(m, NULL, G_MENU_MODEL(s1));

	GMenu *s2 = g_menu_new();
	GMenu *bm = g_menu_new();
	GPtrArray *marks = online_store_bookmarks(app.store);
	for (guint i = marks->len; i-- > 0 && marks->len - i <= 30;) {
		OnlineLink *k = marks->pdata[i];
		GMenuItem *it = open_item(k->title, k->url);
		g_menu_append_item(bm, it);
		g_object_unref(it);
	}
	if (!marks->len) g_menu_append(bm, "No bookmarks yet (Ctrl+D adds one)", "win.nothing");
	g_ptr_array_unref(marks);
	g_menu_append_submenu(s2, "Bookmarks", G_MENU_MODEL(bm));
	if (!b->private) {
		GMenu *hist = g_menu_new(), *recent = g_menu_new(), *clear = g_menu_new();
		GPtrArray *h = online_store_history(app.store, 15);
		for (guint i = 0; i < h->len; i++) {
			OnlineLink *k = h->pdata[i];
			GMenuItem *it = open_item(k->title, k->url);
			g_menu_append_item(recent, it);
			g_object_unref(it);
		}
		if (!h->len) g_menu_append(recent, "Nothing yet", "win.nothing");
		g_ptr_array_unref(h);
		g_menu_append(clear, "Clear History", "app.clear-history");
		g_menu_append_section(hist, NULL, G_MENU_MODEL(recent));
		g_menu_append_section(hist, NULL, G_MENU_MODEL(clear));
		g_menu_append_submenu(s2, "History", G_MENU_MODEL(hist));
		g_object_unref(hist); g_object_unref(recent); g_object_unref(clear);
	}
	g_menu_append_section(m, NULL, G_MENU_MODEL(s2));

	GMenu *s3 = g_menu_new();
	g_menu_append(s3, "Find in Page…", "win.find");
	GMenu *zoom = g_menu_new();
	g_menu_append(zoom, "Zoom In", "win.zoom-in");
	g_menu_append(zoom, "Zoom Out", "win.zoom-out");
	g_menu_append(zoom, "Actual Size", "win.zoom-reset");
	g_menu_append_submenu(s3, "Zoom", G_MENU_MODEL(zoom));
	g_menu_append(s3, "Print…", "win.print");
	g_menu_append_section(m, NULL, G_MENU_MODEL(s3));

	GMenu *s4 = g_menu_new();
	g_menu_append(s4, "Block Ads and Trackers", "app.block-ads");
	g_menu_append(s4, "Block on This Site", "win.block-site");
	GMenu *engines = g_menu_new();
	for (const char *const *e = online_store_engines(); *e; e++) {
		GMenuItem *it = g_menu_item_new(*e, NULL);
		g_menu_item_set_action_and_target_value(it, "app.search-engine", g_variant_new_string(*e));
		g_menu_append_item(engines, it);
		g_object_unref(it);
	}
	g_menu_append_submenu(s4, "Search Engine", G_MENU_MODEL(engines));
	GMenu *prefs = g_menu_new();
	g_menu_append(prefs, "Show Bookmarks Bar", "app.show-bookmarks-bar");
	g_menu_append(prefs, "Remember History", "app.save-history");
	g_menu_append(prefs, "Reopen Tabs at Start", "app.restore-session");
	g_menu_append_submenu(s4, "Settings", G_MENU_MODEL(prefs));
	g_menu_append_section(m, NULL, G_MENU_MODEL(s4));

	GMenu *s5 = g_menu_new();
	g_menu_append(s5, "Developer Tools", "win.inspector");
	g_menu_append_section(m, NULL, G_MENU_MODEL(s5));
	g_object_unref(s1); g_object_unref(s2); g_object_unref(bm); g_object_unref(s3); g_object_unref(zoom);
	g_object_unref(s4); g_object_unref(engines); g_object_unref(prefs); g_object_unref(s5);
}

static void sync_app_actions(void)
{
	const OnlineSettings *st = online_store_settings(app.store);
	GActionMap *m = G_ACTION_MAP(app.gtk);
	g_simple_action_set_state(G_SIMPLE_ACTION(g_action_map_lookup_action(m, "block-ads")), g_variant_new_boolean(st->block_trackers));
	g_simple_action_set_state(G_SIMPLE_ACTION(g_action_map_lookup_action(m, "show-bookmarks-bar")), g_variant_new_boolean(st->show_bookmarks_bar));
	g_simple_action_set_state(G_SIMPLE_ACTION(g_action_map_lookup_action(m, "save-history")), g_variant_new_boolean(st->save_history));
	g_simple_action_set_state(G_SIMPLE_ACTION(g_action_map_lookup_action(m, "restore-session")), g_variant_new_boolean(st->restore_session));
	g_simple_action_set_state(G_SIMPLE_ACTION(g_action_map_lookup_action(m, "search-engine")), g_variant_new_string(st->search_engine));
}

/* A fresh menu for each window, set when nothing is in the middle of using the old one: not during the click that
 * asked for it (after it, from idle), and not while a window's menu is open (later). Emptying a menu GTK is showing
 * leaves it pointing at entries that are gone. */
static guint menus_pending;

static gboolean rebuild_menus_now(gpointer data)
{
	(void)data;
	menus_pending = 0;
	gboolean later = FALSE;
	for (GList *l = app.windows; l; l = l->next) {
		Browser *b = l->data;
		GtkPopover *pop = gtk_menu_button_get_popover(GTK_MENU_BUTTON(b->menu_button));
		if (pop && gtk_widget_get_visible(GTK_WIDGET(pop))) { later = TRUE; continue; }
		GMenu *m = g_menu_new();
		fill_menu(b, m);
		gtk_menu_button_set_menu_model(GTK_MENU_BUTTON(b->menu_button), G_MENU_MODEL(m));
		g_object_unref(b->menu);
		b->menu = m;
	}
	if (later) menus_pending = g_timeout_add(400, rebuild_menus_now, NULL);
	sync_app_actions();
	return G_SOURCE_REMOVE;
}

static void rebuild_menus(void)
{
	sync_app_actions();
	if (!menus_pending) menus_pending = g_idle_add(rebuild_menus_now, NULL);
}

static void apply_filters(void)
{
	const OnlineSettings *st = online_store_settings(app.store);
	online_filters_apply(app.filters, st->block_trackers, (const char *const *)st->allow_sites, NULL, NULL);
}

// ───────────────────────────── actions ─────────────────────────────

#define WIN_ACTION(name) static void name(GSimpleAction *a, GVariant *p, gpointer data)
#define UNUSED (void)a; (void)p

WIN_ACTION(act_new_tab) { UNUSED; tab_new(data, NULL, START_URL, TRUE); }
WIN_ACTION(act_close_tab)
{
	UNUSED;
	Browser *b = data;
	int i = gtk_notebook_get_current_page(b->tabs);
	if (i >= 0) gtk_notebook_remove_page(b->tabs, i);
}
WIN_ACTION(act_reopen_tab)
{
	UNUSED;
	Browser *b = data;
	if (!b->closed->len) return;
	gchar *url = g_strdup(g_ptr_array_index(b->closed, b->closed->len - 1));
	g_ptr_array_remove_index(b->closed, b->closed->len - 1);
	tab_new(b, NULL, url, TRUE);
	g_free(url);
}
WIN_ACTION(act_focus_address) { UNUSED; Browser *b = data; gtk_widget_grab_focus(GTK_WIDGET(b->address)); }
WIN_ACTION(act_reload) { UNUSED; Tab *t = current_tab(data); if (t) webkit_web_view_reload(t->view); }
WIN_ACTION(act_reload_hard) { UNUSED; Tab *t = current_tab(data); if (t) webkit_web_view_reload_bypass_cache(t->view); }
WIN_ACTION(act_back) { UNUSED; Tab *t = current_tab(data); if (t) webkit_web_view_go_back(t->view); }
WIN_ACTION(act_forward) { UNUSED; Tab *t = current_tab(data); if (t) webkit_web_view_go_forward(t->view); }
WIN_ACTION(act_open) { (void)a; Tab *t = current_tab(data); const char *u = g_variant_get_string(p, NULL); if (t) load(t, u); else tab_new(data, NULL, u, TRUE); }
WIN_ACTION(act_nothing) { UNUSED; (void)data; }
WIN_ACTION(act_find)
{
	UNUSED;
	Browser *b = data;
	gtk_search_bar_set_search_mode(b->find_bar, TRUE);
	gtk_widget_grab_focus(GTK_WIDGET(b->find_entry));
}
static void find_step(Browser *b, gboolean next)
{
	Tab *t = current_tab(b);
	if (!t) return;
	WebKitFindController *fc = webkit_web_view_get_find_controller(t->view);
	if (next) webkit_find_controller_search_next(fc); else webkit_find_controller_search_previous(fc);
}
WIN_ACTION(act_find_next) { UNUSED; find_step(data, TRUE); }
WIN_ACTION(act_find_prev) { UNUSED; find_step(data, FALSE); }
static void zoom(Browser *b, double factor)
{
	Tab *t = current_tab(b);
	if (!t) return;
	double z = factor == 0 ? 1.0 : CLAMP(webkit_web_view_get_zoom_level(t->view) * factor, 0.3, 5.0);
	webkit_web_view_set_zoom_level(t->view, z);
	gchar *host = online_host(webkit_web_view_get_uri(t->view));
	if (*host && !b->private) online_store_set_zoom(app.store, host, z);
	g_free(host);
}
WIN_ACTION(act_zoom_in) { UNUSED; zoom(data, 1.1); }
WIN_ACTION(act_zoom_out) { UNUSED; zoom(data, 1 / 1.1); }
WIN_ACTION(act_zoom_reset) { UNUSED; zoom(data, 0); }
WIN_ACTION(act_print)
{
	UNUSED;
	Browser *b = data;
	Tab *t = current_tab(b);
	if (!t) return;
	WebKitPrintOperation *op = webkit_print_operation_new(t->view);
	webkit_print_operation_run_dialog(op, b->win);
	g_object_unref(op);
}
WIN_ACTION(act_inspector) { UNUSED; Tab *t = current_tab(data); if (t) webkit_web_inspector_show(webkit_web_view_get_inspector(t->view)); }
WIN_ACTION(act_bookmark)
{
	UNUSED;
	Browser *b = data;
	Tab *t = current_tab(b);
	const char *uri = t ? webkit_web_view_get_uri(t->view) : NULL;
	if (!uri || !strcmp(uri, START_URL)) return;
	online_store_toggle_bookmark(app.store, uri, webkit_web_view_get_title(t->view));
	rebuild_bookmarks();
	rebuild_menus();
}
WIN_ACTION(act_next_tab)
{
	UNUSED;
	Browser *b = data;
	int n = gtk_notebook_get_n_pages(b->tabs);
	if (n > 1) gtk_notebook_set_current_page(b->tabs, (gtk_notebook_get_current_page(b->tabs) + 1) % n);
}
WIN_ACTION(act_prev_tab)
{
	UNUSED;
	Browser *b = data;
	int n = gtk_notebook_get_n_pages(b->tabs);
	if (n > 1) gtk_notebook_set_current_page(b->tabs, (gtk_notebook_get_current_page(b->tabs) + n - 1) % n);
}
WIN_ACTION(act_select_tab)
{
	(void)a;
	Browser *b = data;
	int i = g_variant_get_int32(p), n = gtk_notebook_get_n_pages(b->tabs);
	gtk_notebook_set_current_page(b->tabs, i >= 9 || i > n ? n - 1 : i - 1);     /* Ctrl+9: the last tab */
}
WIN_ACTION(act_fullscreen)
{
	UNUSED;
	Browser *b = data;
	if (gtk_window_is_fullscreen(b->win)) gtk_window_unfullscreen(b->win); else gtk_window_fullscreen(b->win);
}
WIN_ACTION(act_downloads) { UNUSED; Browser *b = data; gtk_widget_set_visible(b->downloads_button, TRUE); gtk_menu_button_popup(GTK_MENU_BUTTON(b->downloads_button)); }
static void act_block_site(GSimpleAction *a, GVariant *p, gpointer data)
{
	Browser *b = data;
	Tab *t = current_tab(b);
	if (!t) return;
	gchar *host = online_host(webkit_web_view_get_uri(t->view));
	if (*host) {
		gboolean on = g_variant_get_boolean(p);
		online_store_set_site_blocking(app.store, host, on);
		g_simple_action_set_state(a, p);
		apply_filters();
		webkit_web_view_reload(t->view);
	}
	g_free(host);
}

// app-wide
static Browser *browser_new(gboolean private);

static void act_new_window(GSimpleAction *a, GVariant *p, gpointer d) { UNUSED; (void)d; Browser *b = browser_new(FALSE); tab_new(b, NULL, START_URL, TRUE); gtk_window_present(b->win); }
static void act_new_private(GSimpleAction *a, GVariant *p, gpointer d) { UNUSED; (void)d; Browser *b = browser_new(TRUE); tab_new(b, NULL, START_URL, TRUE); gtk_window_present(b->win); }
static void act_clear_history(GSimpleAction *a, GVariant *p, gpointer d) { UNUSED; (void)d; online_store_clear_history(app.store); rebuild_menus(); }
static void act_quit(GSimpleAction *a, GVariant *p, gpointer d)
{
	UNUSED; (void)d;
	for (GList *l = g_list_copy(app.windows); l; l = g_list_delete_link(l, l)) gtk_window_close(((Browser *)l->data)->win);
}
static void setting_toggled(GSimpleAction *a, GVariant *p, const char *setting)
{
	online_store_set_flag(app.store, setting, g_variant_get_boolean(p));
	g_simple_action_set_state(a, p);
}
static void act_block_ads(GSimpleAction *a, GVariant *p, gpointer d) { (void)d; setting_toggled(a, p, "BlockTrackers"); apply_filters(); if (g_variant_get_boolean(p)) online_filters_update(app.filters, FALSE); for (GList *l = app.windows; l; l = l->next) browser_update(l->data); }
static void act_show_bar(GSimpleAction *a, GVariant *p, gpointer d) { (void)d; setting_toggled(a, p, "ShowBookmarksBar"); rebuild_bookmarks(); }
static void act_save_history(GSimpleAction *a, GVariant *p, gpointer d) { (void)d; setting_toggled(a, p, "SaveHistory"); }
static void act_restore(GSimpleAction *a, GVariant *p, gpointer d) { (void)d; setting_toggled(a, p, "RestoreSession"); }
static void act_engine(GSimpleAction *a, GVariant *p, gpointer d) { (void)d; online_store_set_engine(app.store, g_variant_get_string(p, NULL)); g_simple_action_set_state(a, p); }

static const GActionEntry win_actions[] = {
	{ "new-tab", act_new_tab, NULL, NULL, NULL, { 0 } }, { "close-tab", act_close_tab, NULL, NULL, NULL, { 0 } },
	{ "reopen-tab", act_reopen_tab, NULL, NULL, NULL, { 0 } }, { "focus-address", act_focus_address, NULL, NULL, NULL, { 0 } },
	{ "reload", act_reload, NULL, NULL, NULL, { 0 } }, { "reload-hard", act_reload_hard, NULL, NULL, NULL, { 0 } },
	{ "back", act_back, NULL, NULL, NULL, { 0 } }, { "forward", act_forward, NULL, NULL, NULL, { 0 } },
	{ "open", act_open, "s", NULL, NULL, { 0 } }, { "nothing", act_nothing, NULL, NULL, NULL, { 0 } },
	{ "find", act_find, NULL, NULL, NULL, { 0 } }, { "find-next", act_find_next, NULL, NULL, NULL, { 0 } },
	{ "find-prev", act_find_prev, NULL, NULL, NULL, { 0 } }, { "zoom-in", act_zoom_in, NULL, NULL, NULL, { 0 } },
	{ "zoom-out", act_zoom_out, NULL, NULL, NULL, { 0 } }, { "zoom-reset", act_zoom_reset, NULL, NULL, NULL, { 0 } },
	{ "print", act_print, NULL, NULL, NULL, { 0 } }, { "inspector", act_inspector, NULL, NULL, NULL, { 0 } },
	{ "bookmark", act_bookmark, NULL, NULL, NULL, { 0 } }, { "next-tab", act_next_tab, NULL, NULL, NULL, { 0 } },
	{ "prev-tab", act_prev_tab, NULL, NULL, NULL, { 0 } }, { "select-tab", act_select_tab, "i", NULL, NULL, { 0 } },
	{ "fullscreen", act_fullscreen, NULL, NULL, NULL, { 0 } }, { "downloads", act_downloads, NULL, NULL, NULL, { 0 } },
	{ "block-site", NULL, NULL, "true", act_block_site, { 0 } },
};

static const GActionEntry app_actions[] = {
	{ "new-window", act_new_window, NULL, NULL, NULL, { 0 } }, { "new-private-window", act_new_private, NULL, NULL, NULL, { 0 } },
	{ "clear-history", act_clear_history, NULL, NULL, NULL, { 0 } }, { "quit", act_quit, NULL, NULL, NULL, { 0 } },
	{ "block-ads", NULL, NULL, "true", act_block_ads, { 0 } }, { "show-bookmarks-bar", NULL, NULL, "true", act_show_bar, { 0 } },
	{ "save-history", NULL, NULL, "true", act_save_history, { 0 } }, { "restore-session", NULL, NULL, "true", act_restore, { 0 } },
	{ "search-engine", NULL, "s", "'DuckDuckGo'", act_engine, { 0 } },
};

static void set_accels(void)
{
	static const struct { const char *action; const char *keys[4]; } accels[] = {
		{ "win.new-tab", { "<Control>t" } }, { "win.close-tab", { "<Control>w", "<Control>F4" } },
		{ "win.reopen-tab", { "<Control><Shift>t" } }, { "win.focus-address", { "<Control>l", "<Alt>d", "F6" } },
		{ "win.reload", { "<Control>r", "F5" } }, { "win.reload-hard", { "<Control><Shift>r", "<Shift>F5" } },
		{ "win.back", { "<Alt>Left", "Back" } }, { "win.forward", { "<Alt>Right", "Forward" } },
		{ "win.find", { "<Control>f" } }, { "win.find-next", { "<Control>g" } }, { "win.find-prev", { "<Control><Shift>g" } },
		{ "win.zoom-in", { "<Control>plus", "<Control>equal", "<Control>KP_Add" } },
		{ "win.zoom-out", { "<Control>minus", "<Control>KP_Subtract" } }, { "win.zoom-reset", { "<Control>0", "<Control>KP_0" } },
		{ "win.print", { "<Control>p" } }, { "win.inspector", { "F12", "<Control><Shift>i" } }, { "win.bookmark", { "<Control>d" } },
		{ "win.next-tab", { "<Control>Tab", "<Control>Page_Down" } }, { "win.prev-tab", { "<Control><Shift>Tab", "<Control>Page_Up" } },
		{ "win.fullscreen", { "F11" } }, { "win.downloads", { "<Control>j" } },
		{ "app.new-window", { "<Control>n" } }, { "app.new-private-window", { "<Control><Shift>n", "<Control><Shift>p" } },
		{ "app.quit", { "<Control>q" } },
	};
	for (size_t i = 0; i < G_N_ELEMENTS(accels); i++) gtk_application_set_accels_for_action(app.gtk, accels[i].action, accels[i].keys);
	for (int n = 1; n <= 9; n++) {
		gchar *action = g_strdup_printf("win.select-tab(%d)", n), *key = g_strdup_printf("<Control>%d", n);
		const char *keys[] = { key, NULL };
		gtk_application_set_accels_for_action(app.gtk, action, keys);
		g_free(action); g_free(key);
	}
}

// ───────────────────────────── the window ─────────────────────────────

static void address_activate(GtkEntry *e, Browser *b)
{
	gchar *url = online_url_from_input(gtk_editable_get_text(GTK_EDITABLE(e)), search_template());
	if (!url) return;
	b->editing = FALSE;
	Tab *t = current_tab(b);
	if (!t) t = tab_new(b, NULL, NULL, TRUE);
	load(t, url);
	gtk_widget_grab_focus(GTK_WIDGET(t->view));
	g_free(url);
}

static gboolean select_all_later(gpointer e) { gtk_editable_select_region(GTK_EDITABLE(e), 0, -1); return G_SOURCE_REMOVE; }
static void address_focus_in(GtkEventControllerFocus *c, Browser *b) { (void)c; b->editing = TRUE; g_idle_add(select_all_later, b->address); }
static void address_focus_out(GtkEventControllerFocus *c, Browser *b) { (void)c; b->editing = FALSE; set_address(b, current_tab(b)); }

static gboolean address_key(GtkEventControllerKey *c, guint key, guint code, GdkModifierType state, Browser *b)
{
	(void)c; (void)code; (void)state;
	if (key != GDK_KEY_Escape) return FALSE;
	b->editing = FALSE;
	set_address(b, current_tab(b));
	Tab *t = current_tab(b);
	if (t) gtk_widget_grab_focus(GTK_WIDGET(t->view));
	return TRUE;
}

static void address_icon(GtkEntry *e, GtkEntryIconPosition pos, Browser *b)
{
	(void)e;
	if (pos == GTK_ENTRY_ICON_SECONDARY) g_action_group_activate_action(G_ACTION_GROUP(b->win), "bookmark", NULL);
}

static void reload_clicked(GtkButton *btn, Browser *b)
{
	(void)btn;
	Tab *t = current_tab(b);
	if (!t) return;
	if (webkit_web_view_is_loading(t->view)) webkit_web_view_stop_loading(t->view);
	else webkit_web_view_reload(t->view);
}

static void switch_page(GtkNotebook *nb, GtkWidget *page, guint n, Browser *b)
{
	(void)nb; (void)n; (void)page;
	g_idle_add_once((GSourceOnceFunc)browser_update, b);
	gtk_search_bar_set_search_mode(b->find_bar, FALSE);
}

static void page_removed(GtkNotebook *nb, GtkWidget *page, guint n, Browser *b)
{
	(void)page; (void)n;
	if (gtk_notebook_get_n_pages(nb) == 0) gtk_window_close(b->win);
	else browser_update(b);
}

static void find_changed(GtkSearchEntry *e, Browser *b)
{
	Tab *t = current_tab(b);
	if (!t) return;
	const char *text = gtk_editable_get_text(GTK_EDITABLE(e));
	WebKitFindController *fc = webkit_web_view_get_find_controller(t->view);
	if (!*text) { webkit_find_controller_search_finish(fc); gtk_label_set_text(b->find_count, ""); return; }
	guint opts = WEBKIT_FIND_OPTIONS_CASE_INSENSITIVE | WEBKIT_FIND_OPTIONS_WRAP_AROUND;
	webkit_find_controller_count_matches(fc, text, opts, 1000);
	webkit_find_controller_search(fc, text, opts, 1000);
}

static void find_activate(GtkSearchEntry *e, Browser *b) { (void)e; find_step(b, TRUE); }

static void find_counted(WebKitFindController *fc, guint n, Browser *b)
{
	(void)fc;
	gchar *s = n ? g_strdup_printf("%u match%s", n, n == 1 ? "" : "es") : g_strdup("Not found");
	gtk_label_set_text(b->find_count, s);
	g_free(s);
	if (app.self_test) selftest_event(n ? "found" : "not-found");
}

static void find_stop(GtkSearchEntry *e, Browser *b)
{
	(void)e;
	gtk_search_bar_set_search_mode(b->find_bar, FALSE);
	Tab *t = current_tab(b);
	if (t) { webkit_find_controller_search_finish(webkit_web_view_get_find_controller(t->view)); gtk_widget_grab_focus(GTK_WIDGET(t->view)); }
}

static void find_mode(GtkSearchBar *bar, GParamSpec *p, Browser *b)
{
	(void)p;
	if (gtk_search_bar_get_search_mode(bar)) return;
	Tab *t = current_tab(b);
	if (t) webkit_find_controller_search_finish(webkit_web_view_get_find_controller(t->view));
}

/* the find controller belongs to each view: follow the current tab */
static void find_follow(GtkNotebook *nb, GtkWidget *page, guint n, Browser *b)
{
	(void)nb; (void)n;
	Tab *t = tab_of(page);
	if (!t) return;
	WebKitFindController *fc = webkit_web_view_get_find_controller(t->view);
	if (!g_object_get_data(G_OBJECT(fc), "hooked")) {
		g_signal_connect(fc, "counted-matches", G_CALLBACK(find_counted), b);
		g_object_set_data(G_OBJECT(fc), "hooked", GINT_TO_POINTER(1));
	}
}

static void window_active(GtkWindow *w, GParamSpec *p, Browser *b)
{
	(void)p;
	if (!gtk_window_is_active(w)) return;
	/* most recently used first; and pick up changes the shell made (Settings, imports) */
	app.windows = g_list_remove(app.windows, b);
	app.windows = g_list_prepend(app.windows, b);
	/* only when the shell changed something (Settings, imports): rebuilding on every focus change disturbed menus */
	if (online_store_refresh(app.store)) {
		rebuild_bookmarks();
		rebuild_menus();
	}
	browser_update(b);
}

static gboolean window_close(GtkWindow *w, Browser *b)
{
	(void)w;
	if (!b->private) {
		gboolean last = TRUE;
		for (GList *l = app.windows; l; l = l->next) if (l->data != b && !((Browser *)l->data)->private) last = FALSE;
		if (last) {
			/* the last ordinary window: its tabs come back next time */
			GPtrArray *urls = g_ptr_array_new_with_free_func(g_free);
			if (online_store_settings(app.store)->restore_session)
				for (int i = 0; i < gtk_notebook_get_n_pages(b->tabs); i++) {
					const char *u = webkit_web_view_get_uri(tab_of(gtk_notebook_get_nth_page(b->tabs, i))->view);
					if (u && strcmp(u, START_URL)) g_ptr_array_add(urls, g_strdup(u));
				}
			g_ptr_array_add(urls, NULL);
			if (!app.self_test) online_store_set_session(app.store, (const char *const *)urls->pdata);
			g_ptr_array_unref(urls);
			online_store_flush(app.store);
		}
	}
	app.windows = g_list_remove(app.windows, b);
	g_signal_handlers_disconnect_by_data(b->tabs, b);    /* tabs going away now aren't "closed tabs" to reopen */
	g_ptr_array_unref(b->closed);
	b->closed = g_ptr_array_new_with_free_func(g_free);
	if (b->private) g_object_unref(b->session);
	return FALSE;
}

static void window_destroyed(GtkWidget *w, Browser *b) { (void)w; g_ptr_array_unref(b->closed); g_object_unref(b->menu); g_free(b); }

static Browser *browser_new(gboolean private)
{
	Browser *b = g_new0(Browser, 1);
	b->private = private;
	b->closed = g_ptr_array_new_with_free_func(g_free);
	if (private) {
		b->session = webkit_network_session_new_ephemeral();
		g_signal_connect(b->session, "download-started", G_CALLBACK(download_started), NULL);
	} else b->session = app.session;
	b->win = GTK_WINDOW(gtk_application_window_new(app.gtk));
	gtk_window_set_default_size(b->win, 1200, 820);
	gtk_window_set_icon_name(b->win, "web-browser");
	if (private) gtk_widget_add_css_class(GTK_WIDGET(b->win), "private");
	g_action_map_add_action_entries(G_ACTION_MAP(b->win), win_actions, G_N_ELEMENTS(win_actions), b);

	GtkWidget *header = gtk_header_bar_new();
	b->back = gtk_button_new_from_icon_name("go-previous-symbolic");
	b->forward = gtk_button_new_from_icon_name("go-next-symbolic");
	b->reload = gtk_button_new_from_icon_name("view-refresh-symbolic");
	gtk_actionable_set_action_name(GTK_ACTIONABLE(b->back), "win.back");
	gtk_actionable_set_action_name(GTK_ACTIONABLE(b->forward), "win.forward");
	gtk_widget_set_tooltip_text(b->back, "Back (Alt+Left)");
	gtk_widget_set_tooltip_text(b->forward, "Forward (Alt+Right)");
	g_signal_connect(b->reload, "clicked", G_CALLBACK(reload_clicked), b);
	gtk_header_bar_pack_start(GTK_HEADER_BAR(header), b->back);
	gtk_header_bar_pack_start(GTK_HEADER_BAR(header), b->forward);
	gtk_header_bar_pack_start(GTK_HEADER_BAR(header), b->reload);

	b->address = GTK_ENTRY(gtk_entry_new());
	gtk_entry_set_placeholder_text(b->address, private ? "Search privately or type an address" : "Search the web or type an address");
	gtk_entry_set_input_purpose(b->address, GTK_INPUT_PURPOSE_URL);
	gtk_editable_set_max_width_chars(GTK_EDITABLE(b->address), 110);
	gtk_widget_set_hexpand(GTK_WIDGET(b->address), TRUE);
	gtk_widget_set_size_request(GTK_WIDGET(b->address), 360, -1);
	g_signal_connect(b->address, "activate", G_CALLBACK(address_activate), b);
	g_signal_connect(b->address, "icon-press", G_CALLBACK(address_icon), b);
	GtkEventController *focus = gtk_event_controller_focus_new();
	g_signal_connect(focus, "enter", G_CALLBACK(address_focus_in), b);
	g_signal_connect(focus, "leave", G_CALLBACK(address_focus_out), b);
	gtk_widget_add_controller(GTK_WIDGET(b->address), focus);
	GtkEventController *keys = gtk_event_controller_key_new();
	gtk_event_controller_set_propagation_phase(keys, GTK_PHASE_CAPTURE);
	g_signal_connect(keys, "key-pressed", G_CALLBACK(address_key), b);
	gtk_widget_add_controller(GTK_WIDGET(b->address), keys);
	gtk_header_bar_set_title_widget(GTK_HEADER_BAR(header), GTK_WIDGET(b->address));

	b->menu = g_menu_new();
	b->menu_button = gtk_menu_button_new();
	gtk_menu_button_set_icon_name(GTK_MENU_BUTTON(b->menu_button), "open-menu-symbolic");
	gtk_menu_button_set_menu_model(GTK_MENU_BUTTON(b->menu_button), G_MENU_MODEL(b->menu));
	gtk_widget_set_tooltip_text(b->menu_button, "Menu");
	gtk_header_bar_pack_end(GTK_HEADER_BAR(header), b->menu_button);
	b->downloads_button = gtk_menu_button_new();
	gtk_menu_button_set_icon_name(GTK_MENU_BUTTON(b->downloads_button), "folder-download-symbolic");
	gtk_widget_set_tooltip_text(b->downloads_button, "Downloads (Ctrl+J)");
	GtkWidget *pop = downloads_popover();
	g_signal_connect(pop, "show", G_CALLBACK(downloads_popover_show), NULL);
	gtk_menu_button_set_popover(GTK_MENU_BUTTON(b->downloads_button), pop);
	gtk_widget_set_visible(b->downloads_button, app.downloads->len > 0);
	gtk_header_bar_pack_end(GTK_HEADER_BAR(header), b->downloads_button);
	GtkWidget *plus = gtk_button_new_from_icon_name("tab-new-symbolic");
	gtk_actionable_set_action_name(GTK_ACTIONABLE(plus), "win.new-tab");
	gtk_widget_set_tooltip_text(plus, "New tab (Ctrl+T)");
	gtk_header_bar_pack_end(GTK_HEADER_BAR(header), plus);
	gtk_window_set_titlebar(b->win, header);

	GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
	b->bookmarks_bar = gtk_scrolled_window_new();
	gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(b->bookmarks_bar), GTK_POLICY_EXTERNAL, GTK_POLICY_NEVER);
	gtk_widget_add_css_class(b->bookmarks_bar, "bookmarks-bar");
	b->bookmarks_box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 2);
	gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(b->bookmarks_bar), b->bookmarks_box);
	gtk_box_append(GTK_BOX(box), b->bookmarks_bar);

	b->find_bar = GTK_SEARCH_BAR(gtk_search_bar_new());
	GtkWidget *find_box = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6);
	b->find_entry = GTK_SEARCH_ENTRY(gtk_search_entry_new());
	gtk_widget_set_size_request(GTK_WIDGET(b->find_entry), 280, -1);
	GtkWidget *prev = gtk_button_new_from_icon_name("go-up-symbolic"), *next = gtk_button_new_from_icon_name("go-down-symbolic");
	gtk_actionable_set_action_name(GTK_ACTIONABLE(prev), "win.find-prev");
	gtk_actionable_set_action_name(GTK_ACTIONABLE(next), "win.find-next");
	b->find_count = GTK_LABEL(gtk_label_new(""));
	gtk_widget_add_css_class(GTK_WIDGET(b->find_count), "dim-label");
	gtk_box_append(GTK_BOX(find_box), GTK_WIDGET(b->find_entry));
	gtk_box_append(GTK_BOX(find_box), prev);
	gtk_box_append(GTK_BOX(find_box), next);
	gtk_box_append(GTK_BOX(find_box), GTK_WIDGET(b->find_count));
	gtk_search_bar_set_child(b->find_bar, find_box);
	gtk_search_bar_connect_entry(b->find_bar, GTK_EDITABLE(b->find_entry));
	gtk_search_bar_set_show_close_button(b->find_bar, TRUE);
	g_signal_connect(b->find_entry, "search-changed", G_CALLBACK(find_changed), b);
	g_signal_connect(b->find_entry, "activate", G_CALLBACK(find_activate), b);
	g_signal_connect(b->find_entry, "stop-search", G_CALLBACK(find_stop), b);
	g_signal_connect(b->find_bar, "notify::search-mode-enabled", G_CALLBACK(find_mode), b);
	gtk_box_append(GTK_BOX(box), GTK_WIDGET(b->find_bar));

	b->tabs = GTK_NOTEBOOK(gtk_notebook_new());
	gtk_notebook_set_scrollable(b->tabs, TRUE);
	gtk_notebook_set_show_border(b->tabs, FALSE);
	gtk_widget_set_vexpand(GTK_WIDGET(b->tabs), TRUE);
	g_signal_connect(b->tabs, "switch-page", G_CALLBACK(switch_page), b);
	g_signal_connect(b->tabs, "switch-page", G_CALLBACK(find_follow), b);
	g_signal_connect(b->tabs, "page-removed", G_CALLBACK(page_removed), b);
	gtk_box_append(GTK_BOX(box), GTK_WIDGET(b->tabs));
	gtk_window_set_child(b->win, box);

	g_signal_connect(b->win, "notify::is-active", G_CALLBACK(window_active), b);
	g_signal_connect(b->win, "close-request", G_CALLBACK(window_close), b);
	g_signal_connect(b->win, "destroy", G_CALLBACK(window_destroyed), b);
	app.windows = g_list_prepend(app.windows, b);
	fill_menu(b, b->menu);
	rebuild_bookmarks();
	sync_app_actions();
	return b;
}

// ───────────────────────────── start-up ─────────────────────────────

static const char *app_css =
	".link-status { background: alpha(@window_bg_color, 0.96); color: @window_fg_color; padding: 3px 10px; "
	"  border-top-right-radius: 6px; border: 1px solid alpha(@borders, 0.6); border-left: none; border-bottom: none; font-size: smaller; }"
	".tab-close { min-width: 18px; min-height: 18px; padding: 0; margin: 0; }"
	"notebook > header > tabs > tab:checked { box-shadow: inset 0 -3px #c62828; }"
	".bookmarks-bar { border-bottom: 1px solid alpha(@borders, 0.5); }"
	".bookmarks-bar button { padding: 2px 8px; min-height: 26px; }"
	"window.private headerbar { background: #3a1f4d; color: white; }"
	"window.private headerbar entry { background: alpha(white, 0.12); color: white; }";

/* Red Star's own light/dark choice (Settings → Appearance) */
static void follow_red_star_theme(void)
{
	gchar *path = g_build_filename(g_get_user_config_dir(), "redstaros", "settings.json", NULL);
	JsonParser *p = json_parser_new();
	gboolean dark = FALSE, large = FALSE;
	if (json_parser_load_from_file(p, path, NULL)) {
		JsonNode *r = json_parser_get_root(p);
		if (r && JSON_NODE_HOLDS_OBJECT(r)) {
			JsonObject *o = json_node_get_object(r);
			dark = !g_ascii_strcasecmp(json_object_get_string_member_with_default(o, "Theme", ""), "Dark");
			large = json_object_get_boolean_member_with_default(o, "LargeCursor", FALSE);
		}
	}
	g_object_unref(p);
	g_free(path);
	GtkSettings *gs = gtk_settings_get_default();
	if (g_object_class_find_property(G_OBJECT_GET_CLASS(gs), "gtk-interface-color-scheme"))
		g_object_set(gs, "gtk-interface-color-scheme", dark ? 2 : 3, NULL);        /* GTK 4.20+: DARK, LIGHT */
	else
		g_object_set(gs, "gtk-application-prefer-dark-theme", dark, NULL);
	/* the pointer at Red Star's size (Accessibility → Large mouse pointer), not whatever another desktop's settings
	 * say; redstar-comp draws it itself where GTK uses cursor-shape */
	const char *env = g_getenv("XCURSOR_SIZE");
	int size = env && atoi(env) > 0 ? atoi(env) : large ? 48 : 24;
	g_object_set(gtk_settings_get_default(), "gtk-cursor-theme-size", size, NULL);
}

static void startup(GApplication *gapp, gpointer data)
{
	(void)gapp; (void)data;
	follow_red_star_theme();
	GtkCssProvider *css = gtk_css_provider_new();
	gtk_css_provider_load_from_string(css, app_css);
	gtk_style_context_add_provider_for_display(gdk_display_get_default(), GTK_STYLE_PROVIDER(css), GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
	g_object_unref(css);

	gchar *path = g_build_filename(g_get_user_config_dir(), "redstaros", "browser-data.json", NULL);
	app.store = online_store_new(path);
	g_free(path);

	gchar *data_dir = g_build_filename(g_get_user_data_dir(), "redstaros", "online", NULL);
	gchar *cache_dir = g_build_filename(g_get_user_cache_dir(), "redstaros", "online", NULL);
	app.session = webkit_network_session_new(data_dir, cache_dir);
	webkit_network_session_set_itp_enabled(app.session, TRUE);                  /* WebKit's tracking prevention */
	webkit_cookie_manager_set_accept_policy(webkit_network_session_get_cookie_manager(app.session), WEBKIT_COOKIE_POLICY_ACCEPT_NO_THIRD_PARTY);
	webkit_website_data_manager_set_favicons_enabled(webkit_network_session_get_website_data_manager(app.session), TRUE);
	g_signal_connect(app.session, "download-started", G_CALLBACK(download_started), NULL);

	app.ucm = webkit_user_content_manager_new();
	app.settings = webkit_settings_new();
	webkit_settings_set_enable_developer_extras(app.settings, TRUE);
	webkit_settings_set_enable_back_forward_navigation_gestures(app.settings, TRUE);
	webkit_settings_set_enable_smooth_scrolling(app.settings, TRUE);
	if (app.self_test) webkit_settings_set_javascript_can_open_windows_automatically(app.settings, TRUE);
	webkit_web_context_register_uri_scheme(webkit_web_context_get_default(), "redstar", scheme_request, NULL, NULL);

	app.downloads = g_ptr_array_new();
	app.downloads_list = gtk_list_box_new();
	gtk_list_box_set_selection_mode(GTK_LIST_BOX(app.downloads_list), GTK_SELECTION_NONE);
	g_object_ref_sink(app.downloads_list);
	GtkWidget *empty = gtk_label_new("No downloads yet");
	gtk_widget_add_css_class(empty, "dim-label");
	gtk_list_box_set_placeholder(GTK_LIST_BOX(app.downloads_list), empty);

	app.filters = online_filters_new(app.ucm, cache_dir);
	g_free(data_dir);
	g_free(cache_dir);

	g_action_map_add_action_entries(G_ACTION_MAP(app.gtk), app_actions, G_N_ELEMENTS(app_actions), NULL);
	set_accels();
	if (!app.self_test) {
		apply_filters();
		online_filters_update(app.filters, FALSE);
	}
}

static int command_line(GApplication *gapp, GApplicationCommandLine *cl, gpointer data)
{
	(void)gapp; (void)data;
	int argc;
	gchar **argv = g_application_command_line_get_arguments(cl, &argc);
	gboolean private = FALSE, new_window = FALSE;
	GPtrArray *urls = g_ptr_array_new_with_free_func(g_free);
	for (int i = 1; i < argc; i++) {
		if (!strcmp(argv[i], "--private")) private = TRUE;
		else if (!strcmp(argv[i], "--new-window")) new_window = TRUE;
		else if (!strcmp(argv[i], "--self-test")) continue;
		else if (argv[i][0] == '-') continue;
		else {
			/* a file next to the caller, or what the address bar would make of it */
			GFile *f = g_application_command_line_create_file_for_arg(cl, argv[i]);
			gchar *u = g_file_query_exists(f, NULL) ? g_file_get_uri(f) : online_url_from_input(argv[i], search_template());
			g_object_unref(f);
			if (u) g_ptr_array_add(urls, u);
		}
	}
	g_strfreev(argv);
	if (app.self_test) { g_ptr_array_unref(urls); selftest_start(); return 0; }

	Browser *b = new_window ? NULL : last_window(private);
	if (!b) {
		b = browser_new(private);
		if (!urls->len && !private && online_store_settings(app.store)->restore_session) {
			char **sess = online_store_session(app.store);
			for (char **s = sess; *s; s++) tab_new(b, NULL, *s, s == sess);
			g_strfreev(sess);
		}
		if (!urls->len && gtk_notebook_get_n_pages(b->tabs) == 0) tab_new(b, NULL, START_URL, TRUE);
	}
	for (guint i = 0; i < urls->len; i++) tab_new(b, NULL, urls->pdata[i], TRUE);
	g_ptr_array_unref(urls);
	gtk_window_present(b->win);
	return 0;
}

// ───────────────────────────── self-test (headless, under redstar-comp) ─────────────────────────────
// Runs the real browser through its paces on local pages: the blocker (network and element hiding), the address
// bar, back, bookmarks, history, find, target=_blank tabs, closing and reopening tabs, downloads.

static struct { int step, failed, retries; char *dir; Browser *b; guint timeout; char *pending_check; } st;

static void check(gboolean ok, const char *what)
{
	printf("%s %s\n", ok ? "PASS" : "FAIL", what);
	fflush(stdout);
	if (!ok) st.failed++;
}

static void selftest_finish(void)
{
	printf("%d failed\n", st.failed);
	fflush(stdout);
	app.exit_status = st.failed ? 1 : 0;
	g_application_release(G_APPLICATION(app.gtk));
	act_quit(NULL, NULL, NULL);
}

static gboolean selftest_timeout(gpointer d)
{
	(void)d;
	printf("FAIL timed out at step %d\n", st.step);
	st.failed++;
	st.timeout = 0;
	selftest_finish();
	return G_SOURCE_REMOVE;
}

static char *test_uri(const char *name)
{
	gchar *p = g_build_filename(st.dir, name, NULL);
	gchar *u = g_filename_to_uri(p, NULL, NULL);
	g_free(p);
	return u;
}

static void selftest_next(void);

static void js_done(GObject *src, GAsyncResult *res, gpointer data)
{
	(void)data;
	GError *err = NULL;
	JSCValue *v = webkit_web_view_evaluate_javascript_finish(WEBKIT_WEB_VIEW(src), res, &err);
	gchar *s = v ? jsc_value_to_string(v) : g_strdup(err ? err->message : "?");
	g_clear_error(&err);
	if (v) g_object_unref(v);
	gchar **f = g_strsplit(s, "|", -1);
	if (st.step == 1) {
		check(g_strv_length(f) == 4, "the test page's script ran");
		if (g_strv_length(f) == 4) {
			check(!strcmp(f[0], "Page A"), "the page loads with its title");
			check(!strcmp(f[1], "none"), "an element-hiding rule hides the ad box");
			check(!strcmp(f[2], "0"), "a network rule blocks the ad image");
			check(strcmp(f[3], "0") != 0, "other images still load");
		}
	}
	g_strfreev(f);
	g_free(s);
	selftest_next();
}

static void selftest_next(void)
{
	Browser *b = st.b;
	Tab *t = current_tab(b);
	st.step++;
	switch (st.step) {
	case 1:                                     /* page A is loaded: look at it */
		webkit_web_view_evaluate_javascript(t->view,
			"document.title+'|'+getComputedStyle(document.querySelector('.ad-box')).display+'|'"
			"+document.getElementById('blocked').naturalWidth+'|'+document.getElementById('ok').naturalWidth",
			-1, NULL, NULL, NULL, js_done, NULL);
		break;
	case 2: {                                   /* the address bar opens page B */
		gchar *u = test_uri("b.html");
		gtk_editable_set_text(GTK_EDITABLE(b->address), u);
		g_signal_emit_by_name(b->address, "activate");
		g_free(u);
		st.pending_check = "loaded";
		break;
	}
	case 3: {
		const char *title = webkit_web_view_get_title(t->view);
		check(!g_strcmp0(title, "Page B"), "typing an address opens it");
		check(strstr(gtk_window_get_title(b->win), "Page B") != NULL, "the window title follows the page");
		g_action_group_activate_action(G_ACTION_GROUP(b->win), "back", NULL);
		st.pending_check = "loaded";
		break;
	}
	case 4: {
		check(!g_strcmp0(webkit_web_view_get_title(t->view), "Page A"), "Back returns to the previous page");
		g_action_group_activate_action(G_ACTION_GROUP(b->win), "bookmark", NULL);
		const char *uri = webkit_web_view_get_uri(t->view);
		check(online_store_is_bookmarked(app.store, uri), "Ctrl+D bookmarks the page");
		gchar *text = NULL;
		g_file_get_contents(online_store_path(app.store), &text, NULL, NULL);
		check(text && strstr(text, "a.html") && strstr(text, "\"Bookmarks\""), "the bookmark is saved where the shell reads it");
		g_free(text);
		check(gtk_widget_get_first_child(b->bookmarks_box) != NULL, "it appears on the bookmarks bar");
		online_store_flush(app.store);
		GPtrArray *h = online_store_history(app.store, 5);
		gboolean seen = FALSE;
		for (guint i = 0; i < h->len; i++) if (strstr(((OnlineLink *)h->pdata[i])->url, "b.html")) seen = TRUE;
		check(seen, "visited pages are in the history");
		g_ptr_array_unref(h);
		g_action_group_activate_action(G_ACTION_GROUP(b->win), "find", NULL);
		gtk_editable_set_text(GTK_EDITABLE(b->find_entry), "needle");
		st.pending_check = "found";
		break;
	}
	case 5:
		check(TRUE, "Find in page finds text");
		gtk_search_bar_set_search_mode(b->find_bar, FALSE);
		webkit_web_view_evaluate_javascript(t->view, "document.getElementById('popup').click()", -1, NULL, NULL, NULL, NULL, NULL);
		st.pending_check = "loaded";
		break;
	case 6:
		/* a new tab can report an empty first document before the page, and its title a moment after: wait for both */
		if ((!t || g_strcmp0(webkit_web_view_get_title(t->view), "Page B")) && st.retries++ < 50) {
			/* its title can arrive just after the load ends: look again shortly */
			st.step--;
			g_timeout_add_once(100, (GSourceOnceFunc)selftest_next, NULL);
			break;
		}
		check(gtk_notebook_get_n_pages(b->tabs) == 2, "a target=_blank link opens a new tab");
		check(t && !g_strcmp0(webkit_web_view_get_title(t->view), "Page B"), "the new tab loads the link and comes to the front");
		g_action_group_activate_action(G_ACTION_GROUP(b->win), "close-tab", NULL);
		check(gtk_notebook_get_n_pages(b->tabs) == 1, "Ctrl+W closes the tab");
		g_action_group_activate_action(G_ACTION_GROUP(b->win), "reopen-tab", NULL);
		check(gtk_notebook_get_n_pages(b->tabs) == 2, "Ctrl+Shift+T brings it back");
		st.pending_check = "loaded";
		break;
	case 7: {
		gchar *u = test_uri("file.bin");
		webkit_web_view_download_uri(t->view, u);
		g_free(u);
		st.pending_check = "download";
		break;
	}
	case 8: {
		gchar *p = g_build_filename(g_get_home_dir(), "Downloads", "file.bin", NULL);
		const char *d = g_get_user_special_dir(G_USER_DIRECTORY_DOWNLOAD);
		gchar *alt = d ? g_build_filename(d, "file.bin", NULL) : g_strdup(p);
		check(g_file_test(p, G_FILE_TEST_EXISTS) || g_file_test(alt, G_FILE_TEST_EXISTS), "a download is saved to Downloads");
		g_free(p); g_free(alt);
		if (st.timeout) g_source_remove(st.timeout);
		st.timeout = 0;
		selftest_finish();
		break;
	}
	}
}

static void selftest_event(const char *what)
{
	if (!st.pending_check || strcmp(st.pending_check, what) != 0) return;
	st.pending_check = NULL;
	g_idle_add_once((GSourceOnceFunc)selftest_next, NULL);
}

static void filters_ready(gpointer data)
{
	(void)data;
	check(TRUE, "the blocker's rules compile in WebKit");
	gchar *a = test_uri("a.html");
	st.b = browser_new(FALSE);
	st.pending_check = "loaded";
	st.step = 0;
	tab_new(st.b, NULL, a, TRUE);
	gtk_window_present(st.b->win);
	g_free(a);
}

static void selftest_start(void)
{
	static const guchar png[] = {        /* a 1×1 PNG */
		0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01,
		0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4, 0x89, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x44, 0x41,
		0x54, 0x78, 0x9c, 0x63, 0xf8, 0xcf, 0xc0, 0xf0, 0x1f, 0x00, 0x05, 0x00, 0x01, 0xff, 0x89, 0x99, 0x3d, 0x1d, 0x00, 0x00,
		0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
	};
	g_application_hold(G_APPLICATION(app.gtk));
	st.dir = g_dir_make_tmp("online-selftest-XXXXXX", NULL);
	gchar *p;
	p = g_build_filename(st.dir, "a.html", NULL);
	g_file_set_contents(p, "<!doctype html><title>Page A</title><div class=ad-box>advert</div>"
		"<img id=blocked src=adimg.png><img id=ok src=ok.png><p>a needle in a haystack</p>"
		"<a id=popup href=b.html target=_blank>B in a new tab</a>", -1, NULL);
	g_free(p);
	p = g_build_filename(st.dir, "b.html", NULL);
	g_file_set_contents(p, "<!doctype html><title>Page B</title><p>second page</p>", -1, NULL);
	g_free(p);
	const char *imgs[] = { "adimg.png", "ok.png" };
	for (int i = 0; i < 2; i++) {
		p = g_build_filename(st.dir, imgs[i], NULL);
		g_file_set_contents(p, (const char *)png, sizeof png, NULL);
		g_free(p);
	}
	p = g_build_filename(st.dir, "file.bin", NULL);
	g_file_set_contents(p, "\x01\x02\x03 not a page", 15, NULL);
	g_free(p);
	st.timeout = g_timeout_add_seconds(90, selftest_timeout, NULL);
	online_filters_use_only(app.filters, "! self-test\n##.ad-box\n/adimg.png\n");
	online_filters_apply(app.filters, TRUE, NULL, filters_ready, NULL);
}

int main(int argc, char **argv)
{
	signal(SIGPIPE, SIG_IGN);       /* our messages go to the shell's log through a pipe; a restarted shell mustn't end us */
	for (int i = 1; i < argc; i++) if (!strcmp(argv[i], "--self-test")) app.self_test = TRUE;
	app.gtk = gtk_application_new(APP_ID, G_APPLICATION_HANDLES_COMMAND_LINE | (app.self_test ? G_APPLICATION_NON_UNIQUE : 0));
	g_set_application_name("Red Star Online");
	g_signal_connect(app.gtk, "startup", G_CALLBACK(startup), NULL);
	g_signal_connect(app.gtk, "command-line", G_CALLBACK(command_line), NULL);
	int status = g_application_run(G_APPLICATION(app.gtk), argc, argv);
	if (app.store) online_store_free(app.store);
	g_object_unref(app.gtk);
	return app.self_test ? app.exit_status : status;
}
