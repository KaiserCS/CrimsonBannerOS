# redstar-comp — Red Star OS's Wayland compositor.
#   meson setup build && meson compile -C build      (or: ./build.sh)
# Needs: wlroots 0.20 (Arch/CachyOS: pacman -S wlroots0.20), wayland, wayland-protocols, libxkbcommon, libxcb,
# xcb-util-wm, cairo, libdrm, pixman, and Xwayland at run time.
project('redstar-comp', 'c',
  version: files('../VERSION'),
  license: 'GPL-3.0-or-later',
  default_options: ['c_std=c11', 'warning_level=2', 'buildtype=release'],
  meson_version: '>= 0.57')

# Crimson Banner OS's version: the release in ../VERSION, and this build's full version from tools/version.sh
version_h = vcs_tag(input: 'version.h.in', output: 'version.h',
  command: ['sh', meson.current_source_dir() / '..' / 'tools' / 'version.sh'],
  fallback: meson.project_version())

add_project_arguments('-DWLR_USE_UNSTABLE', '-Wno-unused-parameter', language: 'c')

wlroots = dependency('wlroots-0.20')
wayland_server = dependency('wayland-server')
wayland_client = dependency('wayland-client')
wayland_protocols = dependency('wayland-protocols')
xkbcommon = dependency('xkbcommon')
xcb = dependency('xcb')
xcb_icccm = dependency('xcb-icccm')
cairo = dependency('cairo')
libdrm = dependency('libdrm').partial_dependency(compile_args: true, includes: true)
pixman = dependency('pixman-1')
math = meson.get_compiler('c').find_library('m')

# Night light sets each screen's gamma ramp. wlroots has had two ways to do that (a colour transform made from a
# lookup table, which replaced setting the ramp directly); use whichever these headers have, and say which.
cc = meson.get_compiler('c')
wlr_args = ['-DWLR_USE_UNSTABLE']
if cc.has_header_symbol('wlr/render/color.h', 'wlr_color_transform_init_lut_3x1d', dependencies: wlroots, args: wlr_args) and \
   cc.has_header_symbol('wlr/types/wlr_output.h', 'wlr_output_state_set_color_transform', dependencies: wlroots, args: wlr_args)
  add_project_arguments('-DHAVE_NIGHT_COLOR_TRANSFORM', language: 'c')
  message('night light: gamma ramps as colour transforms')
elif cc.has_header_symbol('wlr/types/wlr_output.h', 'wlr_output_state_set_gamma_lut', dependencies: wlroots, args: wlr_args)
  add_project_arguments('-DHAVE_NIGHT_GAMMA_LUT', language: 'c')
  message('night light: gamma ramps set directly')
else
  warning('night light: this wlroots has no way to set gamma ramps; it will report every screen as unable')
endif
if cc.has_header_symbol('wlr/types/wlr_output.h', 'wlr_output_get_gamma_size', dependencies: wlroots, args: wlr_args)
  add_project_arguments('-DHAVE_GAMMA_SIZE', language: 'c')
endif

# wlroots' headers include the server-side protocol headers by name; generate them
scanner = find_program(dependency('wayland-scanner', native: true).get_variable('wayland_scanner'))
wp_dir = wayland_protocols.get_variable('pkgdatadir')
protocols = {
  'xdg-shell': wp_dir / 'stable/xdg-shell/xdg-shell.xml',
  'xdg-decoration-unstable-v1': wp_dir / 'unstable/xdg-decoration/xdg-decoration-unstable-v1.xml',
  'linux-dmabuf-v1': wp_dir / 'stable/linux-dmabuf/linux-dmabuf-v1.xml',
  'ext-image-capture-source-v1': wp_dir / 'staging/ext-image-capture-source/ext-image-capture-source-v1.xml',
  'ext-image-copy-capture-v1': wp_dir / 'staging/ext-image-copy-capture/ext-image-copy-capture-v1.xml',
  'xdg-activation-v1': wp_dir / 'staging/xdg-activation/xdg-activation-v1.xml',
  'cursor-shape-v1': wp_dir / 'staging/cursor-shape/cursor-shape-v1.xml',
}
# Only xdg-shell and xdg-decoration need their interface code here: nested.c is a client of the host's xdg-shell.
# For the others the headers are enough — wlroots has the code (and some of it refers to further protocols).
with_code = ['xdg-shell', 'xdg-decoration-unstable-v1']
protocol_src = []
foreach name, xml : protocols
  protocol_src += custom_target(name + '-protocol.h',
    input: xml, output: name + '-protocol.h',
    command: [scanner, 'server-header', '@INPUT@', '@OUTPUT@'])
  if with_code.contains(name)
    protocol_src += custom_target(name + '-protocol.c',
      input: xml, output: name + '-protocol.c',
      command: [scanner, 'private-code', '@INPUT@', '@OUTPUT@'])
  endif
endforeach

# nested.c is a client of the host desktop: client headers (the interface code above is shared)
client_protocols = {
  'xdg-shell': [wp_dir / 'stable/xdg-shell/xdg-shell.xml', false],
  'keyboard-shortcuts-inhibit-unstable-v1':
    [wp_dir / 'unstable/keyboard-shortcuts-inhibit/keyboard-shortcuts-inhibit-unstable-v1.xml', true],
}
foreach name, spec : client_protocols
  protocol_src += custom_target(name + '-client-protocol.h',
    input: spec[0], output: name + '-client-protocol.h',
    command: [scanner, 'client-header', '@INPUT@', '@OUTPUT@'])
  if spec[1]
    protocol_src += custom_target(name + '-client-protocol.c',
      input: spec[0], output: name + '-client-protocol.c',
      command: [scanner, 'private-code', '@INPUT@', '@OUTPUT@'])
  endif
endforeach

executable('redstar-comp',
  ['redstar-comp.c', 'nested.c', version_h] + protocol_src,
  dependencies: [wlroots, wayland_server, wayland_client, xkbcommon, xcb, xcb_icccm, cairo, libdrm, pixman, math],
  install: true)
