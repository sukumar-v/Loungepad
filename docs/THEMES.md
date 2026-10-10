# Themes

[README](../README.md) · [User guide](GUIDE.md) · [Themes](THEMES.md) · [Add-ons](ADDONS.md) · [Development](DEVELOPMENT.md)


A theme is a folder under `%APPDATA%\Loungepad\themes` — **Settings → Appearance → Themes
folder** opens it — with a `theme.css` in it and, optionally, a `theme.json` and a `theme.html`.
Community themes install from **Settings → Add-ons**, from the
[add-ons repository](https://github.com/sukumar-v/loungepad-addons) or from a zip or folder; a
theme to share goes there with a pull request (see [Add-ons](ADDONS.md)).
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
  are stripped. The header of `ui/theme.js` documents the binding. The library's slots are
  `topbar`, `playing`, `continue`, `grid`, `legend` and `focus-detail`, which only appears when a
  theme asks for it and always shows the title and a line about the game under the highlight. For
  more than that, see "A layout of your own" below.
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
  copy goes to `theme-backups`); `version` is what Settings shows. A theme in the add-ons
  repository gives `version` as `major.minor.patch` (it is how the launcher sees an update) and
  may add `icon` (an svg, png, jpg or webp in the folder, for its tile), `homepage` and
  `minLauncher`; `id` is the folder's name and need not be written.

## A layout of your own

A theme can lay out the library and a game's page entirely in its own markup. Loungepad gives a
template everything it knows about a game, and a fixed list of buttons that work wherever the
theme puts them. Everything in this section needs Loungepad 1.9.0, so a theme that uses it says
`"minLauncher": "1.9.0"`.

### Templates

`{{field}}` prints a field in text or in an attribute, and `data-if` / `data-unless` keep an
element only when a field is set or not set (empty strings, `0`, `false`, `null` and empty lists
count as not set). Three more:

| | |
|---|---|
| `data-each="list"` | One copy of the element for every entry of the list. Inside it, a name is looked up on the entry first and then outwards, so `{{title}}` still finds the game's title from inside its media. |
| `data-limit="8"` | With `data-each`: at most this many. |
| `data-bg="{{thumb}}"` | The picture as the element's background. Use this rather than `style="background-image:url({{…}})"`: the URL is quoted so nothing in it can break out. |

Inside a `data-each`, `{{@index}}` (from 0), `{{@number}}` (from 1), `{{@count}}`, `{{@first}}`
and `{{@last}}` say where the entry is, and `{{@value}}` is the entry itself when it is a plain
string or number. Lists nest: an extension's facts inside the list of extensions, for example.

```html
<div class="trophies" data-if="ach.has" data-act="achievements">
  <b>{{ach.percent}}%</b> {{ach.unlocked}} of {{ach.total}}
  <i data-each="ach.recent" data-limit="4" data-bg="{{icon}}" title="{{name}}"></i>
</div>
<div class="media-row">
  <div data-each="media" data-act="media" data-index="{{index}}" data-bg="{{thumb}}">
    <span data-if="isVideo">▶</span>
  </div>
</div>
```

### Buttons: `data-act`

An element with `data-act` set to one of these names is a real button. The highlight can land on
it and A or a click presses it. When it stands for a game, Y opens that game's menu, the same as
on a tile. No other name does anything, and a theme has no other way to make something happen.

| `data-act` | what it does |
|---|---|
| `play` | Play the game, or Continue it, or Install it when it is not installed (`{{playLabel}}` says which); Resume when it is running |
| `options` | The game's menu, as Y on a tile |
| `details` | The game's page |
| `favorite` | Favorite it, or take it off |
| `achievements` | Its achievements sheet |
| `stats` | Its Stats sheet (sessions, playtime) |
| `collect` | Add it to a collection |
| `manage` | Its Manage sheet (on its page; anywhere else it opens the page) |
| `media` | Open the gallery's item `data-index` in the viewer |
| `library` | Open the game list on the tab `data-tab`: `installed`, `collection` (not installed), `all` or `favorites` |
| `home` | Back from the game list to the library's home |
| `search`, `filter`, `settings` | What View, X and Menu do on the library |
| `back` | What B does |

Highlighting a `media` button changes the picture behind the screen to that screenshot, or plays
that film after a short beat, as the built-in gallery does. A button keeps its place under the
highlight when its region is drawn again. To control that yourself, give the button a
`data-focus-key`.

### Live regions: `data-render`

A screen template can hold regions that Loungepad fills from one of the theme's item templates
and keeps up to date:

```html
<div class="hub" data-render="game-hub" data-model="game"></div>
<div class="list-head" data-render="list-head" data-model="library"></div>
```

- `data-model="game"` draws the template with the game model. On the library that is the game
  under the highlight, and it stays that game while the highlight is inside the region. On a
  game's page it is that game.
- When the highlight is on something that is not a game, such as the game list's tile, the
  region is empty and carries `data-empty`. Add `data-hold` to keep showing the last game instead.
- `data-model="library"` draws the template with the library model.

A region is drawn again when its game changes, after a state push, and when a gallery or an
achievement list arrives. Galleries for games that aren't installed, and the lists behind
`ach.recent`, `ach.rarest` and `ach.next`, are fetched the first time a live region asks for them.

### The library's two pages

The library has a home and a game list. `data-act="library"` opens the list on a tab and B goes
back home. On the list, LB and RB step through the tab buttons the theme has drawn, and the
grid shows only that tab's games. The screen says where it is, for the theme's CSS:

```css
#screen-library[data-library-page="home"]  .my-game-list { display: none; }
#screen-library[data-library-page="games"] .my-home      { display: none; }
#screen-library[data-library-tab="installed"] …
```

A hidden element can't be highlighted, so hiding one page is enough to keep the D-pad on the
other. A theme that never uses `data-act="library"` never leaves home.

The `continue-end` item template, when a theme has one, is drawn after the recents in their row,
with the library model. That is where a console-style "Game library" tile goes. Its root element
should be the button:

```html
<template data-template="continue-end">
  <div class="library-tile" data-act="library" data-tab="installed">Game library</div>
</template>
```

### The game's page

`screen-detail` is a screen template like `screen-library`. Its slots are `header` (the
breadcrumb and the store), `main` (the built-in column: title, facts, gallery, numbers and
buttons) and `legend`. A theme that draws the page itself leaves `main` out and puts a live
region in its place:

```html
<template data-template="screen-detail">
  <div class="page" data-render="game-page" data-model="game"></div>
  <div data-slot="legend"></div>
</template>
```

The highlight starts on the theme's `data-act="play"`. The page's art, its film and its two
shades (`.detail-art`, `.detail-video`, `.detail-shade-x`, `.detail-shade-y`) stay whatever the
layout. Hide them in CSS to draw the page over the library's backdrop instead, with
`--trailer-surface: backdrop` on `#screen-detail` so the film carries on from the library.

### The game model

Every field a game template can use. Tiles (`game-tile`, `continue-tile`) get the same model, but
only the game in a live region fetches what isn't on the page yet.

| field | |
|---|---|
| `id`, `title`, `platform`, `platformIcon` | the store (`Steam`, `Epic`, …), and the name of its icon |
| `installed`, `favorite`, `hidden`, `running`, `emulated`, `played` | true or false |
| `cover`, `banner`, `hero`, `logo`, `backdrop` | picture URLs: 2:3 box art, 1.75:1 tile art, ~3:1 hero, the transparent wordmark, the best full-screen art. Empty when there is none |
| `initials` | two letters, for a game with no art |
| `description`, `developer`, `publisher`, `genres`, `year`, `released`, `releaseDate` | the store's facts; `released` is formatted for reading |
| `genreList` | list of `{name}` |
| `facts` | list of `{text}`: year, developer, publisher, up to three genres. The line under a title |
| `playLabel` | `Play`, `Continue`, `Install` or `Not installed` |
| `playtime`, `playtimeMinutes`, `sessions`, `avgSession`, `lastPlayed`, `size`, `sizeBytes`, `meta` | how it has been played, and the tiles' short line |
| `stats` | list of `{id, label, value}`: what a game's page shows, each only when it has something to say |
| `score`, `scoreBand` | the Metacritic score, and `good`, `mixed` or `poor` for its colour |
| `age` | `{board, label, image, descriptors}`: the ESRB or PEGI rating in the board Settings asks for. `image` is the board's mark, ready for an `<img src>`; `descriptors` is a list of `{text}` |
| `ach` | achievements: `has`, `canHave`, `unlocked`, `total`, `locked`, `percent` (0–100), `done`, `score`, `totalScore`, `lastUnlock`, `loaded`, and three lists of up to eight: `recent` (latest unlocks), `rarest` (rarest unlocks), `next` (locked ones most players have) |
| `ach.recent[…]` and the others | `{name, description, icon, unlocked, when, ago, percent, rarity, rarityId}`. `rarity` is `Ultra rare`, `Rare`, `Uncommon` or `Common` |
| `achievements`, `achUnlocked`, `achTotal`, `achPercent` | the tile fields: `achievements` is true only while the theme's "Achievement progress" option is on |
| `media`, `videos`, `pictures` | the gallery, trailer first: lists of `{index, kind, isVideo, isPicture, url, thumb, name}`. `index` is what `data-act="media"` takes |
| `hasMedia`, `mediaCount`, `hasTrailer` | |
| `stores` | list of `{id, name, installed, current, first}`: every store the game is in |
| `extLines` | list of `{id, name, facts}`, one per extension with something on this game; each fact is `{key, label, text}` |
| `extFacts` | every extension's facts in one list, each with `source` (the extension's name) and `sourceId` |
| `ext.<id>.<key>`, `ext.<id>.<key>Text` | one extension's value by name, raw and formatted. `{{ext.howlongtobeat.mainText}}` is "27 h" |

### The library model

For `data-model="library"` and the `continue-end` tile.

| field | |
|---|---|
| `page`, `tab`, `tabLabel` | `home` or `games`; the open tab and its name |
| `tabs` | list of `{id, label, count, active}`: Installed, Your collection, All games, Favorites |
| `all`, `installed`, `collection`, `favorites` | how many games each tab holds |
| `shown`, `summary` | how many games the grid is showing, and the line the built-in heading prints |
| `search`, `searching` | the standing search, if any |

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
