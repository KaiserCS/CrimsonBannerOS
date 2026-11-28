#!/bin/sh
# temporary: how GTK 4.20+ picks a theme's dark look (CI only); runs after the real gallery pictures
G=crimson-gtk/build/crimson-gtk-gallery
T=/usr/share/themes/CrimsonBanner/gtk-4.0
cp -r /usr/share/themes/CrimsonBanner /tmp/cb-real
# 4: a media query, with the colour scheme asked for dark in code
printf '@import url("resource:///org/gtk/libgtk/theme/Default/Default-light.css");\nwindow.background { background-color: #00FF00; background-image: none; }\n@media (prefers-color-scheme: dark) { window.background { background-color: #FF00FF; background-image: none; } }\n' > $T/gtk.css
printf 'window.background { background-color: #0000FF; background-image: none; }\n' > $T/gtk-dark.css
crimson-gtk/test/headless.sh probe4.png $G --as-other-program
# 5: CSS variables redefined in the media query
printf '@import url("resource:///org/gtk/libgtk/theme/Default/Default-light.css");\n:root { --probe: #00FF00; }\n@media (prefers-color-scheme: dark) { :root { --probe: #FF00FF; } }\nwindow.background { background-color: alpha(var(--probe), 1.0); background-image: none; }\n' > $T/gtk.css
crimson-gtk/test/headless.sh probe5.png $G --as-other-program
# 6: the light look asked for: the media query must not match
crimson-gtk/test/headless.sh probe6.png $G --as-other-program --light
rm -rf /usr/share/themes/CrimsonBanner && mv /tmp/cb-real /usr/share/themes/CrimsonBanner
for f in probe4 probe5 probe6; do echo "---- screen.png (Probe-$f) ----"; base64 -w 0 $f.png; echo; echo "---- end screen.png ----"; done
