#!/bin/sh
# temporary: how GTK picks a theme's dark variant (CI only)
set -x
pkg-config --modversion gtk4
G=crimson-gtk/build/crimson-gtk-gallery
# 1: the variant named in GTK_THEME
GTK_THEME=CrimsonBanner:dark crimson-gtk/test/headless.sh probe1.png $G --as-other-program
# 2: a theme whose gtk.css asks with a media query
mkdir -p /usr/share/themes/ProbeMQ/gtk-4.0
printf '@import url("resource:///org/gtk/libgtk/theme/Default/Default-light.css");\nwindow { background-color: #00FF00; background-image: none; }\n@media (prefers-color-scheme: dark) { window { background-color: #FF00FF; background-image: none; } }\n' > /usr/share/themes/ProbeMQ/gtk-4.0/gtk.css
printf 'window { background-color: #0000FF; background-image: none; }\n' > /usr/share/themes/ProbeMQ/gtk-4.0/gtk-dark.css
sed 's/"CrimsonBanner"/"ProbeMQ"/' crimson-gtk/gallery.vala > /dev/null
GTK_THEME=ProbeMQ crimson-gtk/test/headless.sh probe2.png $G --as-other-program
GTK_THEME=ProbeMQ:dark crimson-gtk/test/headless.sh probe3.png $G --as-other-program
for f in probe1 probe2 probe3; do echo "---- screen.png (Probe-$f) ----"; base64 -w 0 $f.png; echo; echo "---- end screen.png ----"; done
