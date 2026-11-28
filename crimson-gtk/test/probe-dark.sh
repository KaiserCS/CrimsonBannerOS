#!/bin/sh
# temporary: how GTK 4.20+ picks a theme's dark look (CI only); runs after the real gallery pictures
G=crimson-gtk/build/crimson-gtk-gallery
T=/usr/share/themes/CrimsonBanner/gtk-4.0
echo "---- GTK's own theme files ----"
for l in /usr/lib/libgtk-4.so.1 /usr/lib/libgtk-4.so; do [ -e "$l" ] && { gresource list "$l" | grep '/theme/' | sed 's/^/THEMEFILE /'; break; }; done
cp -r /usr/share/themes/CrimsonBanner /tmp/cb-real
for f in Default.css Default/Default.css; do
  printf '@import url("resource:///org/gtk/libgtk/theme/Default/Default.css");\n' > $T/gtk.css
done
crimson-gtk/test/headless.sh probe9.png $G --as-other-program
crimson-gtk/test/headless.sh probe10.png $G --as-other-program --light
rm -rf /usr/share/themes/CrimsonBanner && mv /tmp/cb-real /usr/share/themes/CrimsonBanner
for f in probe9 probe10; do echo "---- screen.png (Probe-$f) ----"; base64 -w 0 $f.png; echo; echo "---- end screen.png ----"; done
