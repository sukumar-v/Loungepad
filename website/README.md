# loungepad.app

The website, as a plain static site: no framework, no build step. Built from the design in
`design/` (see `HANDOFF.md`), which stays here as the source of truth for copy and layout.

```
index.html      the page; all the copy is in it, feature tiles included
site.css        every value from the design, as classes
site.js         the interactive parts: the TV, the viewer, the Power Wheel, the controller stage,
                and the Gamepad API / keyboard navigation
glyphs.js       the button drawings (Xbox / PlayStation / Switch / keyboard), copied from
                Loungepad/ui/glyphs.js — keep the two in step
img/            screenshots at five widths in AVIF, WebP and JPEG, the icons, the social card
_headers        security and cache headers (Cloudflare)
.assetsignore   keeps design/ and the repo files out of the upload
wrangler.toml   the Cloudflare Worker (static assets)
```

## Preview

Any static server works. In the Claude desktop app the `website` entry in `.claude/launch.json`
serves this folder on port 8931; by hand:

```
npx http-server website -c-1
```

## Images

`img/` is made by `tools/website-images.js` from the trailer project's 2560x1440 stills
(`trailer/out/screens/*.png`, from `npm run stills` in `trailer/`). After a screenshot changes:

```
cd trailer
npm install --no-save sharp
node ..\tools\website-images.js
```

The widths it writes (480, 720, 1080, 1440, 1920) are the ones `index.html`'s `srcset`s and
`site.js` name; change all three together.

## Deploying to Cloudflare (Workers Builds)

The site is a Worker that serves this folder as static assets. Connected to GitHub, every push to
the production branch deploys it, and other branches get preview URLs.

In the Cloudflare dashboard, Workers & Pages, Create, Import a repository, this repository:

- Worker name: `loungepad` (it has to match `name` in `wrangler.toml`)
- Production branch: `main`
- Root directory: `website`
- Build command: none
- Deploy command: `npx wrangler deploy`

By hand, from this folder, with a Wrangler that is logged in:

```
cd website
npx wrangler deploy
```

**Custom domain.** The Worker, Settings, Domains & Routes, Add, Custom domain, `loungepad.app`.
The domain's DNS has to be on Cloudflare. Add `www.loungepad.app` the same way if wanted.

Nothing else is needed: there are no secrets, no functions and no analytics.

## Notes

- The headers in `_headers` set a Content Security Policy that allows only this site and Google
  Fonts. Adding a script or an image host means adding it there too.
- `.assetsignore` keeps `design/`, `HANDOFF.md`, `README.md` and `wrangler.toml` out of the upload,
  since the assets directory is the whole folder.
- Changed screenshots keep their names, so `img/*` is cached for a week rather than a year.
