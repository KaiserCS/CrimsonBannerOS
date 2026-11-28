#!/bin/sh
# temporary: how GTK 4.20+ picks a theme's dark look (CI only); runs after the real gallery pictures
G=crimson-gtk/build/crimson-gtk-gallery
T=/usr/share/themes/CrimsonBanner/gtk-4.0
echo "---- GTK's own theme files ----"
gresource list "$(ldconfig -p | awk '/libgtk-4.so.1 /{print $NF; exit}')" | grep '/theme/' | head -40
cp -r /usr/share/themes/CrimsonBanner /tmp/cb-real
# 7, 8: imports that depend on the colour scheme
printf 'window.background { background-color: #00FF00; background-image: none; }\n' > $T/probe-light.css
printf 'window.background { background-color: #FF00FF; background-image: none; }\n' > $T/probe-dark.css
printf '@import url("resource:///org/gtk/libgtk/theme/Default/Default-light.css");\n@import url("probe-light.css") (prefers-color-scheme: light);\n@import url("probe-dark.css") (prefers-color-scheme: dark);\n' > $T/gtk.css
crimson-gtk/test/headless.sh probe7.png $G --as-other-program
crimson-gtk/test/headless.sh probe8.png $G --as-other-program --light
rm -rf /usr/share/themes/CrimsonBanner && mv /tmp/cb-real /usr/share/themes/CrimsonBanner
for f in probe7 probe8; do echo "---- screen.png (Probe-$f) ----"; base64 -w 0 $f.png; echo; echo "---- end screen.png ----"; done
