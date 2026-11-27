# Crimson Banner OS design guideline

Every screen in Crimson Banner OS is graphite with one crimson accent, quiet until it needs your attention, and written in plain words.

This file is the guideline in the repository; the shared copy is a Claude doc kept in step with it. When the two differ, change both in the same change.

## Principles

The look is a piece of equipment: a graphite deck with one crimson signal. Five rules decide between options.

1. **Graphite is the room, the accent is the signal.** Backdrops and panels are near-neutral dark greys. The accent (crimson unless the person picks another) marks only what is on, chosen, or the one action to take.
2. **Quiet until needed.** Hover, focus and selection show; everything else rests. A border, label or colour that carries no information is removed.
3. **One construction everywhere.** The shell and every app share `RedStarOS/Styles/Shell.axaml` and `apps/shared` (Look, Deck). Apps use the classes; they don't restyle controls.
4. **Plain words.** Sentence case, the action on the button, numbers with units, no emoji.
5. **Works for everyone, everywhere.** Dark and light, high contrast, reduced motion, keyboard only, and without the network.

## Colour

Use the tokens by name (`{DynamicResource …}`), never a raw hex in a view: they follow dark and light, the chosen accent and high contrast. The dark theme is the default; values below are the dark theme's (alpha first, as Avalonia writes it).

| Token | Value | Use it for |
| --- | --- | --- |
| `ShellBackdropBrush` | `#1C1C1F` → `#09090A`, top to bottom | every window's background |
| `ShellPanelBrush` | white at 5.5% (`#0EFFFFFF`) | panels and cards lifted off the backdrop |
| `ShellPanelBorderBrush` | white at 9% (`#18FFFFFF`) | the hairline round a panel |
| `ShellCardBrush` | white at 7% (`#12FFFFFF`) | pills, badges, pressed panels |
| `ShellHoverBrush` | white at 10% (`#1AFFFFFF`) | anything under the pointer |
| `ShellBorderBrush` | white at 15% (`#26FFFFFF`) | dividers, menu edges, pill outlines |
| `ShellFieldBrush` | black at 30% (`#4D000000`) | text fields and status wells, sunk into the panel |
| `ShellMutedBrush` | `#969AA0` | captions, secondary text, kickers |
| `ShellAccentBrush` | `#C8102E` (crimson) by default | the selected thing, the primary action, today |
| `ShellAccentSoftBrush` | the accent at 23% | a selected row, a pill that is on, a drop target |
| `ShellAccentTextBrush` | the accent mixed 45% with white on dark (crimson: `#E17C8C`); the accent on light | small text in the accent: links, accent kickers, pills that are on |
| `RedStarBrush` | `#C8102E`, always | the brand mark only, whatever the accent |
| `ShellDangerBrush` | `#E81123` | destructive actions, a window's close button on hover |
| Menus and flyouts | `#1C1C20` at 96%, white 15% edge | every menu, drop-down and flyout |
| Taskbar | `#16161A` at 92%, white 18% top edge | the taskbar, always dark |
| LCD readout | `#F02028` digits on `#0C0C0E` glass | figures that matter (sizes, counts, totals) |

The person can swap the accent for Blue `#0078D4`, Green `#107C10`, Orange `#D83B01`, Purple `#744DA9`, Teal `#00828A` or Gray `#5D5A58`. Design so any of them works: never write "red" or "crimson" in a label, and keep the brand mark on `RedStarBrush`.

Rules:

- One filled accent element per view: the primary button, or today, or the active tile. Others that are on use the soft accent with an accent hairline.
- Text on the accent is white. Text on graphite is the default foreground, or `ShellMutedBrush` when secondary.
- Errors use words and `ShellDangerBrush` for the destructive action, not the accent; with a crimson accent the two must not be confused.
- The compositor and the X11 window manager receive the accent too (snap previews, active title bars): don't hard-code crimson in C either.

## Type

One sans family for the interface, a serif for long reading and a monospace for readouts and code. All three ship with the OS (`noto-fonts`, `ttf-dejavu`), so nothing is fetched.

| Role | Family | Size (pt) | Weight | Where |
| --- | --- | --- | --- | --- |
| Clock | system sans (Noto Sans) | 40, seconds 20 at 45% | Light | the clock panel |
| Display | system sans | 30 | Bold | an app page's title (`TextBlock.display`) |
| Window or panel title | system sans | 15–21 | SemiBold or Bold | the top of a panel, an app's name in its sidebar |
| Body | system sans | 13–14 | Regular | most text |
| Caption | system sans | 12 | Regular, `ShellMutedBrush` | dates, sizes, hints (`TextBlock.caption`) |
| Kicker | system sans | 11, letter spacing 0.8 | Bold, muted or accent | a section's label, written in capitals (`TextBlock.kicker`) |
| Badge | system sans | 10, letter spacing 0.5 | Bold | a status word in capitals (`Border.badge`) |
| LCD digits | DejaVu Sans Mono | 26 (label 10) | Bold | readouts (`Border.lcd`) |
| Reading | Noto Serif | 12–34, the reader's choice | Regular | People's Archive's reading room |
| Code and Terminal | DejaVu Sans Mono | 12–13 | Regular | Terminal, code views |

Rules:

- Sentence case everywhere. Capitals only in kickers, badges and LCD labels, which are styled for them.
- Lines of reading text stay under 80 characters; paragraphs under three sentences in the interface.
- Hierarchy comes from size and weight, not colour: one bold step between a title and its body is enough.
- Numbers that are compared or that change (sizes, counts, time) use the LCD readout or tabular digits, so they don't jump.

## Space, shape and depth

Spacing runs on 2-point steps: 2, 4, 6, 8, 10, 12, 14, 16, 18, 24. Inside a panel use 8–14 between items; between panels 10–18.

| Element | Corner radius | Padding (h, v) |
| --- | --- | --- |
| Buttons, fields, drop-downs | 8 | 14, 7 |
| Sidebar items (`Button.nav`) | 10 | 12, 9 |
| Cards and panels | 12 | 14 (card), 18, 16 (panel) |
| Menus, flyouts and dialogs | 12 | 4–8 |
| The shell's flyouts (Start, Quick Settings, clock) | 14 | 16–18 |
| Pills, badges, status wells | 999 (fully round) | 14, 6 (pill), 8, 1 (badge) |
| App icon tiles | 54 on 240 (22.5% of the side) | the picture sits in 40–216 of 256 |
| Small selections (desktop icons, list rows) | 6 | 2–6 |
| The snap preview | 10, inset 6 from the zone | none |

Depth comes from lighter plastic and a hairline, not shadows. A panel is the backdrop plus 5.5% white with a 1-point white 9% edge; a pressed or sunk area goes darker (`ShellFieldBrush`). The compositor adds a soft shadow under whole windows (Settings → Desktop & Taskbar can turn it off); nothing inside a window casts one. Translucent overlays (Snap Assist, flyouts) fall back to solid graphite when there is no compositor or "Opaque popups" is on.

## Icons

There are two kinds, and no emoji anywhere in the interface.

**App icons** stand for an app wherever it appears: Start menu, taskbar, switcher, notifications, Settings, Software. Each is a drawn picture on the graphite tile.

- The tile: a gradient from `#2A2A2F` to `#0C0C0E`, corners of 54 on a 240 tile in a 256 canvas, and a white 12% rim.
- The picture: a few bold shapes in light greys (`#F2F2F5`, `#DCDCE3`, `#B4B4BC`, `#6E6E78`), with ink `#0C0C0E` for holes and marks.
- One crimson detail (`#C8102E`) per icon: the cursor, the record light, today, the ribbon.
- It must still read at 20 px: no text, no thin lines, no more than four or five shapes.
- Sources are SVG in `branding/src/apps/`; `branding/render.py` renders them to `RedStarOS/Assets/apps/{id}.png` at 128 px. Our Software apps keep theirs in `branding/src/app-*.svg`.
- An app without a picture gets its line icon, or its initial, white on the same plate (`AppIcons.Tile`).
- **Other programs** follow the same rules through the **Crimson Banner icon theme** (`icons/CrimsonBanner`, `branding/icon-theme.py`): a program with a general job gets a drawing of it on the tile; a program with a brand keeps its own mark, set at 64% on the tile (`AdaptiveIcons`). Never redraw another project's logo.

**Line icons** label actions, menu items, settings and file kinds (`RedStarOS/Services/Icons.cs`, about 100).

- A 24 grid; everything drawn inside 2.5–21.5.
- 2-unit strokes, round caps and joins; corners rounded at radius 2 (2.5–3 on large boxes); circles as true arcs.
- They take the colour of the text around them: white on the accent, muted when the label is muted.
- Sizes: 16 in menus and lists, 18 in toolbars and panels, 20–22 in navigation.
- A new icon is added to `Icons.cs` with the same construction and a plain name (`chevron-left`, `bell-off`). Draw it next to its neighbours at 16 and 24 before committing.

## Components

Reach for these before making anything new; they are classes in `Shell.axaml`, and `Deck` builds the common ones in code.

| Component | Class or helper | Looks | Use it for |
| --- | --- | --- | --- |
| Primary button | `Button.primary` | accent fill, white text, radius 8 | the one main action of a dialog or page |
| Flat button | `Button.flat` | no fill until hovered | toolbars, secondary actions, icon buttons |
| Pill | `Button.pill`, `Deck.Pill` (`.accent`, `.on`, `.large`) | fully round, 11.5 bold | actions in an app's pages; `.on` for a choice that is set |
| Sidebar item | `Button.nav` (`.on`) | radius 10, accent wash when on | an app's pages, Settings' sections |
| Panel and card | `Border.panel`, `Border.card`, `Deck.Panel` | 5.5% white, hairline, radius 12 | a group of related content |
| Quick tile | `Button.tile` (`.on`) | accent fill when on | Quick Settings toggles |
| Kicker | `TextBlock.kicker`, `Deck.Kicker` | 11 bold capitals, spaced | a section's label above its content |
| Badge | `Border.badge` (`.accent`) | round, 10 bold capitals | a state word: OPEN, EXAMPLE, ON THIS COMPUTER |
| LCD readout | `Border.lcd`, `Deck.Lcd` | dark glass, red digits | figures that matter: counts, sizes, totals |
| Menus and flyouts | Avalonia's, restyled | graphite at 96%, hairline, radius 12, a 16 px line icon on each item | context menus, drop-downs, the shell's panels |
| Toast | `Border.toast` | graphite card, app icon, title, one line | notifications |
| Field | `TextBox.pill-field` and the default `TextBox` | sunk (`ShellFieldBrush`), radius 8 | text input, search |
| Tabs | Avalonia's `TabItem`, restyled | 14 regular, muted; the chosen one semibold over a 3-point accent bar | a window's two or three views (Clock, Wallpapers) |
| App header | `AppHeader` (`AppId`, `Heading`, `Subtitle`) | the app's picture at 30, its name 17 semibold; the app's own actions on the right | the top of a small app: Calculator, Clock, Calendar, Camera, Notes, Voice Memos, Screenshot, Screen Recorder, Disks |
| Empty state | a centred stack: line icon at 22–28 (55%), a semibold line, a caption | quiet, inside the panel it stands for | an empty list or preview: say what goes here and how to put it there |
| Office chrome | `OfficeToolbar`, `OfficeZoom`, `OfficeChrome.Fit` / `SingleTab`; `Border.officetabs`, `.officebar`, `.officestatus`, `.officeside`; `Button.tool` | tabs row, menu bar, one toolbar that folds into More, a status bar with zoom | Writer, Sheets and Slides, the same in all three |

**Button order.** The action comes first and Cancel last, at the right of the row: `Save`, `Cancel`; `Erase and format`, `Cancel`. The main action is `Button.primary` (or `.danger` when it destroys something), Cancel is plain and answers Esc. Close-only dialogs have one button, Close.

States are the same everywhere:

| State | What changes |
| --- | --- |
| Hover | background +10% white (`ShellHoverBrush`) |
| Pressed | background back to 7% white, or the accent at 75% |
| Selected or on | soft accent fill with an accent hairline; filled accent only for the single main one |
| Keyboard focus | a 2-point accent ring, 2 outside the control, only after Tab |
| Disabled | 45% opacity, no hover |

## Motion and feedback

Things move only because the person did something, and they answer at once. Nothing loops except progress and a slideshow the person started.

| Moment | Timing |
| --- | --- |
| Hover, press, selection | immediate, no transition |
| Taskbar preview | appears after the pointer rests 450 ms; closes 280 ms after it leaves both the button and the preview |
| Peek (resting on Show desktop) | after 500 ms; ends the moment the pointer leaves |
| Tooltips | the default; 900 ms on desktop icons so they don't pop up while dragging |
| Snap preview | shows as soon as the pointer reaches an edge or corner, goes on release |
| A press that confirms a choice (a calendar day) | scales to 94% while held |

"Reduce motion" in Settings turns off every animation; build anything that moves so it can be switched off without losing information. When work takes time, say what is happening in words ("Asking the Mapsforge server what it has…") and show progress with figures, not a spinner alone.

## Writing

Words are part of the design: short, plain, and from the person's side of the screen.

- **Names.** The system is Crimson Banner OS; apps go by their plain names (Files, Atlas, Calendar, Crimson Online). Internal names (`redstar-*`, `RedStarOS`) never appear on screen.
- **Sentence case** for titles, buttons, menu items and settings: "Open in Terminal", not "Open In Terminal".
- **Buttons say what happens:** Download, Carry on, Remove, Show desktop. The result uses the same word: "Download" leads to "Albania is on this computer."
- **Errors say what went wrong and what to do,** in the interface's voice, with the server's code in brackets when there is one: "The Mapsforge server is busy (503). Try again in a while." No apologies, no "Oops", no exclamation marks.
- **Empty states invite the next step:** "Nothing planned." with an "Add an event" link; "You're all caught up."
- **Specifics over adjectives:** "3.3 GB", "made 2 November 2026", "5 places", not "large" or "recent".
- **Dates and times** follow the person's region; written out, a date reads "Thursday 26 November 2026".
- **Credit what we use** where it is used: maps say "Map data © OpenStreetMap contributors (ODbL)".
- **Settings describe the effect,** as a sentence the person would say: "Peek at the desktop when I point at Show desktop".

## Accessibility and the checklist

The aim is WCAG 2.2 AA: 4.5:1 for text, 3:1 for large text, icons and edges. Measured on the dark theme:

| Pair | Contrast | Verdict |
| --- | --- | --- |
| White text on the backdrop (`#1C1C1F`) | 17.0:1 | passes |
| White text on the crimson accent | 5.88:1 | passes |
| Muted text (`#969AA0`) on the backdrop | 6.01:1 | passes |
| Muted text on a panel (`#28282B`) | 5.20:1 | passes (the token was lightened from `#8A8E94`, which gave 4.46:1) |
| LCD digits (`#F02028`) on their glass | 4.6:1 | passes |
| Crimson text (`#C8102E`) on the backdrop | 2.89:1 | fails for text: the accent itself is a fill, a ring or an icon; small text uses the accent-text token |
| Accent text (`ShellAccentTextBrush`: crimson mixed 45% with white, `#E17C8C`) on a panel | 5.22:1 | passes; every accent choice comes out at 5.2:1 or better |

Both gaps found while writing this are closed: muted text is now `#969AA0`, and small accent-coloured text (`Button.callink`, `TextBlock.kicker.accent`, pills that are on) uses `ShellAccentTextBrush`, the accent mixed 45% with white on dark and the accent itself on light.

Also:

- Colour never carries meaning alone: Gems' stones differ in shape, today is bold as well as filled, errors are written out.
- Every action has a keyboard path; Tab shows the focus ring; games read their keys before any button can take them.
- Targets are at least 28 points (taskbar buttons 40, calendar days 36).
- High contrast (Settings) swaps the focus ring to yellow `#FFD700` on dark; Reduce motion turns off animation.

Before a screen ships:

- [ ] Only `Shell.axaml` classes and tokens; no new colours or raw hex in views
- [ ] At most one filled accent element in view
- [ ] Icons from `Icons.cs` or the app pictures; no emoji
- [ ] Looks right dark and light, with another accent, and in high contrast
- [ ] Works with the keyboard alone, focus visible
- [ ] Text passes the contrast table above; small accent-coloured text uses `ShellAccentTextBrush`
- [ ] Words: sentence case, the action on the button, errors say what to do
- [ ] A screenshot in `tests/RedStarOS.Tests/ScreenshotTests.cs`, looked at in the CI log
