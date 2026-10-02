# loungepad.app — website handoff

The design for the Loungepad website was made on a Claude Design canvas ("Loungepad Website").
This folder is everything needed to build the real site from it.

```
website/
  HANDOFF.md            this file
  design/
    Main.dc.html        the whole page, as designed (source of truth for copy, layout, behaviour)
    Glyph.dc.html       the button-glyph component the page uses (Xbox / PlayStation / Switch / keyboard)
    assets/             the images the design uses: lp-<screen>.jpg is 1920x1080, lp-<screen>-t.jpg is 720x405
```

## What to build

A single static page for **loungepad.app**: `website/index.html` plus its CSS, JS and images. No framework
and no build step are needed. Match the design exactly: copy, layout, colours, motion and interactions.

The `.dc.html` files are the design tool's own component format and cannot be shipped as they are.
How to read them:

- Everything inside `<x-dc>` is the markup. `<helmet>` holds what belongs in `<head>` (fonts, global CSS).
- `{{name}}` is a value returned by `renderVals()` in the `<script type="text/x-dc">` class at the bottom.
  Dotted lookups only (`{{s.title}}`).
- `<sc-for list="{{items}}" as="item">` is a loop; `<sc-if value="{{cond}}">` is a conditional.
- `<dc-import name="Glyph" btn="X" fam="{{fam}}" size="30">` renders `Glyph.dc.html` with those props.
- `onClick="{{fn}}"`, `onMouseEnter`, `onFocus`, `onKeyDown` bind handlers returned by `renderVals()`.
- `this.state` / `setState` and the lifecycle methods behave like a React class component.
- The page's data (features, TV screens, Power Wheel spokes, accent colours) is in `data()`.
- Inline `style="…"` is the visual spec; the `<helmet><style>` block has hover/focus states, keyframes
  and the responsive rules.

Put all the copy in the HTML itself (feature tiles included), so search engines and visitors without
JavaScript see it. Use JavaScript only for the interactive parts.

## Audience and tone

The site is for **non-technical gamers**. Keep the copy exactly as written in the design and don't add
technical words: no .NET, WebView2, x64, file paths, code signing, SmartScreen, XInput, licence names
in body copy, or names of tools like Vortex or RivaTuner.

## Design tokens

- Background `#05070B`; panels `#0B0F16` and `#11151E`; text `#F2F5FA`; muted text
  `rgba(242,245,250,.6–.76)`; hairlines `rgba(255,255,255,.07–.1)`.
- Accent `#F0A253` (the app's "Ember") as `--accent`, with `--accent-10/15/20/30/40/50` rgba variants.
  The page can switch it live to the app's eight accents (Ember, Coral, Rose, Orchid, Indigo, Aqua,
  Mint, Lime — the same list as `ACCENTS` in `Loungepad/ui/app.js`).
- Fonts (Google Fonts): **Manrope** 400–800 for everything; **IBM Plex Mono** only for small uppercase
  labels (letter-spacing .16–.18em). These are the app's own fonts.
- The highlight is the app's: 3px accent ring + 44px accent glow, scale 1.03–1.04,
  180ms `cubic-bezier(.2,.9,.2,1)`. The same ring shows for mouse hover, keyboard focus and controller focus.
- Radii: tiles 10px, panels 24px, buttons fully rounded.

## The page, top to bottom

1. **Header** — logo + "loungepad", nav links (Features, Controllers, Get started), Download button.
2. **Hero** — centred: headline, one sentence, Download for Windows + Star on GitHub, "Free, for Windows 11".
   One soft accent glow behind it, nothing else.
3. **TV on a stand** — a TV with a thin bezel and a small accent power light, showing six real
   screenshots that crossfade every 7 seconds (paused while hovered, or once a visitor picks one).
   Behind the TV, a blurred copy of the current screen makes an ambient glow. Under it: a pedestal, a
   low three-door cabinet lit faintly by the screen, and a floor shadow. Then a caption for the current
   screen and position dots (the active dot fills as a progress bar). Clicking the TV opens the viewer.
4. **What it does** — three groups (Like a console / Still a PC / What a console can't do), 15 tiles.
   Each tile opens the viewer.
5. **Controllers** — a large face-button diamond drawn for the selected controller; chips for Xbox,
   PlayStation, Switch, Keyboard; a line that changes when a real controller is connected; the accent
   swatches; and a list of what each button does (different rows for keyboard, an extra touchpad row
   for PlayStation).
6. **Get started** — five steps in a settings-style panel. Step 4 (setup) is marked "Recommended";
   don't invite people to skip it.
7. **Closing** — "Games are the reason you sit down." with both buttons; footer with links and the
   trademark note.
8. **Hint bar** — sticky along the bottom like the app's button hints: Download, Star on GitHub, Menu.
   "Move" and "Select" appear only after a controller or the keyboard has been used.

Overlays: the **Power Wheel** (the site's navigation: seven spokes in a circle, the centre describes the
highlighted one) and the **viewer** (a screen shown whole, with title, description, previous/next).

## Controller and keyboard support — keep all of it

- Poll the Gamepad API every frame. Work out the brand from the gamepad id: vendor `054c` is
  PlayStation, `057e` is Switch, anything else is drawn as Xbox. Every button hint on the page redraws
  for it (`Glyph.dc.html`, whose drawings are copied from `Loungepad/ui/glyphs.js`; keep the two in step).
- Standard mapping: D-pad / left stick moves between `[data-nav]` elements by nearest neighbour in that
  direction (380ms before repeating, then every 120ms); A activates; B closes the wheel or viewer;
  X opens Download; Y opens GitHub; Menu (button 9) or Guide (16) toggles the wheel. With the TV
  highlighted, left/right change the screen; in the viewer, left/right step through screens.
- Keyboard: arrows, Esc, X, Y, M do the same; the hints switch to key caps.
- Browsers may refuse to open a new tab from a controller press. Try `window.open`, and if it returns
  nothing, navigate in the same tab, so the button always does something.
- `data-input` on the page root (`mouse` / `key` / `pad`) decides which focus styles show.
- Respect `prefers-reduced-motion`: no automatic screen changes, no animation.

## Responsive

At 760px and below: hide the header nav, make the hero buttons full width, thin the TV bezel and shrink
the wheel spokes. At 480px and below: shorten "Star on GitHub" to "Star". Check it at 390px wide.

## Links

- Download: `https://github.com/sukumar-v/Loungepad/releases/latest`
- GitHub: `https://github.com/sukumar-v/Loungepad` · Issues: `…/issues` · License: `…/blob/main/LICENSE`

Better for non-technical visitors: if release zips were named without the version
(e.g. `Loungepad-win-x64.zip`, set in `tools/package.ps1`), Download could point straight at
`https://github.com/sukumar-v/Loungepad/releases/latest/download/Loungepad-win-x64.zip` and start the
download instead of showing GitHub's release page. `UpdateService.cs` only matches the `-win-x64.zip`
suffix, so auto-update keeps working. Only switch the link once a release with that name exists.

## Needed for production (not part of the design)

- `<title>`, meta description, canonical `https://loungepad.app/`, favicon (`assets/lp-icon.png` or
  `Loungepad/Loungepad.ico`), Open Graph / Twitter card image (`trailer/out/stills/Poster.png` works).
- Images: serve WebP/AVIF with `srcset`, lazy-load everything below the TV, preload the first TV screen.
  Full-size originals are `trailer/out/screens/*.png` (2560×1440, made by `npm run stills` in `trailer/`).
- Accessibility: real buttons and links throughout (already so in the design), visible focus, alt text,
  `aria-live` caption under the TV. Aim for Lighthouse accessibility 95+.
- Hosting: it's a static site; Cloudflare Pages fits, since the repo already uses Wrangler for the
  metadata proxy. Point `loungepad.app` at it.
- No analytics or cookies unless that's decided separately.

## Open items

- The trailer isn't on the page yet; add it once it's online.
- No version number is shown on the page, on purpose, so nothing needs updating per release.
