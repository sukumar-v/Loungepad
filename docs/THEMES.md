# Themes

[README](../README.md) · [User guide](GUIDE.md) · [Themes](THEMES.md) · [Development](DEVELOPMENT.md)


A theme is a folder under `%APPDATA%\Loungepad\themes` — **Settings → Appearance → Themes
folder** opens it — with a `theme.css` in it and, optionally, a `theme.json` and a `theme.html`.
Nothing is compiled or copied: the launcher loads the files straight off disk, watches the folder
and reloads on save, so editing a theme is editing a file. Loungepad, the theme the launcher opens
on, ships this way too; Shelf is the built-in look with no theme applied.

- **`theme.css`** is loaded after the app's own stylesheet, so it overrides by ordinary cascade
  order and needs no `!important`. Every colour in the UI resolves to a token on `:root` (`--bg`,
  `--bg-deep`, `--surface`, `--surface-hi`, `--card`, `--ink`, `--edge`, `--danger`, `--accent`),
  so a recolour is a handful of lines; anything else on the page is fair game.
- **`theme.html`** holds `<template data-template="…">` blocks that replace the built-in markup
  for a game tile, a carousel tile or a whole screen's layout (`screen-library`, with `data-slot`s
  the app moves its own regions into). A theme cannot run script: `<script>` and `on*` attributes
  are stripped. The header of `ui/theme.js` documents the binding.
- **`theme.json`** names the theme, and can set tokens and declare options:

  ```json
  {
    "name": "Loungepad", "author": "Loungepad", "version": "4.1",
    "description": "Full-bleed art, recents in a row, the grid on the way down",
    "tokens": { "--bg": "#05070B" },
    "settings": [ ]
  }
  ```

  For a bundled theme the installed copy is refreshed whenever the shipped files change (the old
  copy goes to `theme-backups`); `version` is what Settings shows.

### A theme's own options

`settings` in `theme.json` puts rows under **Settings → Appearance**, directly under the Theme
row, that belong to that theme alone. Their values are stored per theme and go nowhere but the
page, as CSS: each option becomes a `data-theme-<id>` attribute on `<html>` and, if it names a
`token`, a custom property on the root — so a theme keys its rules off either, with no script.
Loungepad declares five; two of them:

```json
"settings": [
  { "id": "columns", "name": "Tiles across", "type": "select",
    "options": ["5", "6", "7"], "default": "6", "token": "--tv-cols" },
  { "id": "labels", "name": "Always show titles", "type": "toggle", "default": false }
]
```

```css
.tv { --tile-w: calc((1760px - (var(--tv-cols, 6) - 1) * 28px) / var(--tv-cols, 6)); }
html[data-theme-labels="true"] .tv-name { opacity: 1; }
```

| field | |
|---|---|
| `id` | letters, digits and hyphens; the attribute is `data-theme-<id>` |
| `name`, `hint` | the row's label and the line under it |
| `type` | `toggle`, `select` or `slider` |
| `default` | `true`/`false`, one of the options, or a number |
| `options`, `labels` | a select's values, and what to show for each (optional) |
| `min`, `max`, `step`, `unit` | a slider's range, and the suffix its value carries into CSS (`px`) |
| `token` | the custom property the value is written to (`--…`) |
| `values` | what each value becomes in CSS when it is not the value itself: `{ "Slow": "700ms" }` |

Without `values`, a toggle writes `1` or `0`, a slider writes the number with its unit, and a
select writes the option. An entry that does not make sense is skipped rather than shown broken.

The accent colour, the button hints, the animation settings and the trailer settings are kept in
the same per-theme store, under the ids `accent`, `hide-hints`, `animations`, `animation-speed`,
`trailers` and `trailer-sound` — which is why a theme cannot declare an option with one of those
ids. **Restore <theme>'s defaults**, the last row, puts that one theme's look, animation and
options back and leaves every other theme as it is.

Two things a theme can say to the app in CSS alone, with no option declared:

- `--continue-max: 6` on the Continue row (`.continue-row`) is how many recents it lists. The
  built-in row is a carousel and takes twelve; Loungepad ties it to its column count so the row is
  always exactly full and never scrolls.
- `--trailers` on `#backdrop` is whether a film may play behind the library at all. The app's
  default is `none`, which is what Shelf has; Loungepad sets `auto`. Set it per state to turn the
  film off in some states only. The film itself is `#backdrop video` (Steam's) or
  `#backdrop .trailer-yt` (a YouTube one), faded up with `.playing`; Loungepad lets it be the
  screen at rest and darkens it behind the grid.
- `--trailer-surface: backdrop` on `#screen-detail` says the game's page is drawn over the
  backdrop rather than over its own art, so the film stays in `#backdrop` and never restarts when
  the page opens. Loungepad makes the page transparent, hides `.detail-art` and sets this; the
  library's own tiles fade out under the page (`body[data-view="detail"] #screen-library.under`).

### Animation

Everything that moves — screens changing, menus and dialogs opening and closing, the Power Wheel,
the highlight — is timed off tokens on `:root`, each a base time multiplied by `--motion`:

```css
--motion: 1;                                 /* Settings → Appearance: the speed slider divides it, "off" writes 0 */
--t-focus: calc(180ms * var(--motion));      /* the highlight moving */
--t-fade: calc(240ms * var(--motion));       /* colour and opacity: the backdrop, a toast */
--t-menu: calc(220ms * var(--motion));       /* a menu, dialog or the wheel opening */
--t-menu-out: calc(150ms * var(--motion));   /* ...and closing */
--t-screen: calc(300ms * var(--motion));     /* one screen giving way to another */
--t-screen-out: calc(220ms * var(--motion));
--t-slide: calc(260ms * var(--motion));      /* a list or carousel following the highlight */
--ease, --ease-in                            /* the curves, in and out */
```

A theme retunes one kind of motion by redefining its token — keep the `var(--motion)` in it, so
the user's speed and their "off" still apply — or replaces an animation outright: a screen or
overlay carries `.active` while it is up and `.closing` for the length of its exit, and the
keyframes are ordinary rules in `app.css` (`screenIn`, `cardIn`, `cardOut`, `spokeIn`, …).
Multiply the durations in a theme's own transitions by `var(--motion, 1)` and they follow the
setting too; Loungepad does, and exposes its row's slide as one of its options.
