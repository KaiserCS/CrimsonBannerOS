#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""
The real program session_test.py puts under redstar-wm: a GTK 3 window with its own title bar (a header bar, like most
GNOME apps and browsers draw), a button, a tooltip, a drop-down list, a text field, a hover area, full screen on F11 and
a second window. It reports what happened, and where its parts are on screen, in the JSON file named on its command line.

    python3 wm/test/gtkapp.py STATE.json        (needs PyGObject and GTK 3: python3-gi gir1.2-gtk-3.0 / python-gobject gtk3)
"""
import json, os, sys
import gi
gi.require_version('Gtk', '3.0')
from gi.repository import Gtk, Gdk, GLib

STATE = sys.argv[1]
log = {'click': 0, 'down': 0, 'up': 0, 'hoverEnter': 0, 'hoverLeave': 0, 'combo': '', 'entry': '', 'popup': False,
       'fullscreen': False, 'widgets': {}}

win = Gtk.Window(title='Red Star test page')
win.set_default_size(1200, 700)
win.move(200, 80)
bar = Gtk.HeaderBar(title='Red Star test page', show_close_button=True)
win.set_titlebar(bar)

fixed = Gtk.Fixed()
win.add(fixed)

def put(widget, name, x, y, w, h):
    widget.set_size_request(w, h)
    fixed.put(widget, x, y)
    widgets[name] = widget
widgets = {}

bt = Gtk.Button(label='Click me')
bt.connect('clicked', lambda *_: log.__setitem__('click', log['click'] + 1))
bt.connect('button-press-event', lambda *_: log.__setitem__('down', log['down'] + 1) or False)
bt.connect('button-release-event', lambda *_: log.__setitem__('up', log['up'] + 1) or False)
put(bt, 'bt', 40, 40, 220, 90)

tip = Gtk.Button(label='Hover for tooltip')
tip.set_tooltip_text('A tooltip that should stay steady')
put(tip, 'tip', 300, 40, 220, 90)

def open_popup(*_):
    p = Gtk.Window(title='Second window')
    p.set_default_size(500, 360)
    close = Gtk.Button(label='Close')
    close.connect('clicked', lambda *_: p.destroy())
    p.add(close)
    widgets['popup-close'] = close
    p.connect('destroy', lambda *_: (log.__setitem__('popup', False), widgets.pop('popup-close', None)))
    log['popup'] = True
    p.show_all()
pop = Gtk.Button(label='Open window')
pop.connect('clicked', open_popup)
put(pop, 'pop', 820, 40, 220, 90)

combo = Gtk.ComboBoxText()
for o in ('one', 'two', 'three', 'four'): combo.append_text(o)
combo.set_active(0)
combo.connect('changed', lambda c: log.__setitem__('combo', c.get_active_text() or ''))
put(combo, 'sel', 40, 180, 220, 40)

entry = Gtk.Entry(placeholder_text='type here')
entry.connect('changed', lambda e: log.__setitem__('entry', e.get_text()))
put(entry, 'inp', 300, 180, 300, 36)

hov = Gtk.EventBox()
hov.add(Gtk.Label(label='hover area'))
hov.add_events(Gdk.EventMask.ENTER_NOTIFY_MASK | Gdk.EventMask.LEAVE_NOTIFY_MASK)
def crossing(key):
    def f(_w, ev):
        if ev.detail != Gdk.NotifyType.INFERIOR: log[key] += 1     # moving onto the label inside isn't leaving
        return False
    return f
hov.connect('enter-notify-event', crossing('hoverEnter'))
hov.connect('leave-notify-event', crossing('hoverLeave'))
put(hov, 'hov', 40, 260, 300, 120)

def key(_w, ev):
    if ev.keyval == Gdk.KEY_F11:
        win.unfullscreen() if log['fullscreen'] else win.fullscreen()
        return True
    return False
win.connect('key-press-event', key)
win.connect('window-state-event', lambda _w, ev: log.__setitem__('fullscreen', bool(ev.new_window_state & Gdk.WindowState.FULLSCREEN)) or False)
win.connect('destroy', Gtk.main_quit)

last = ''
def report():
    """where each part is on screen (its centre is what the test clicks), and the counts, when anything changed"""
    global last
    where = {}
    for name, w in list(widgets.items()):
        top = w.get_toplevel()
        gw = top.get_window() if top else None
        if gw is None or not w.get_mapped(): continue
        o = gw.get_origin()                               # (x, y), or (result, x, y) in older PyGObject
        tr = w.translate_coordinates(top, 0, 0)           # (x, y) or None
        if not tr: continue
        a = w.get_allocation()
        where[name] = [o[-2] + tr[-2], o[-1] + tr[-1], a.width, a.height]
    log['widgets'] = where
    text = json.dumps(log)
    if text != last:
        last = text
        tmp = STATE + '.tmp'
        with open(tmp, 'w') as f: f.write(text)
        os.replace(tmp, STATE)
    return True
GLib.timeout_add(50, report)

win.show_all()
Gtk.main()
