# Loungepad add-ons

Community themes and extensions for [Loungepad](https://github.com/sukumar-v/Loungepad), the
controller-first launcher for a TV. Everything here is installed from inside the launcher, under
**Settings → Add-ons**, and updated from there when a new version is merged.

> This folder is the seed of the `loungepad-addons` repository. It lives inside the Loungepad
> repository only until that one exists; nothing in the launcher's build reads it.

```
themes/<id>/          a theme: theme.css, with theme.json and theme.html as it needs
extensions/<id>/      an extension: manifest.json and its module
index.json            what the launcher reads -- built from the folders, never edited by hand
tools/build-index.mjs rebuilds index.json
```

## Contributing

1. Put your theme under `themes/<id>` or your extension under `extensions/<id>`, where `<id>` is
   letters, digits and hyphens, starting with a letter. The id is the folder name, forever.
2. Give it a `version` of the form `major.minor.patch` in its manifest, and bump it in every pull
   request that changes it: the version is how the launcher knows there is an update.
3. Run `node tools/build-index.mjs` and commit `index.json` with your change (the checks fail
   otherwise; a merge to `main` rebuilds it anyway).
4. Open a pull request against `main`. Say what it does and, for an extension, what it reaches.

How to write one, and what an extension may and may not do, is in Loungepad's
[docs/ADDONS.md](https://github.com/sukumar-v/Loungepad/blob/main/docs/ADDONS.md); themes are in
[docs/THEMES.md](https://github.com/sukumar-v/Loungepad/blob/main/docs/THEMES.md).

### What a review looks for

- **It does what it says.** An extension's `permissions.hosts` names every host it talks to, and
  nothing else; what it stores per game is what its `gameFacts` describe.
- **It is readable.** Plain JavaScript, no minified or generated code, no `eval`, no code fetched
  at run time. A reviewer has to be able to read the whole thing in one sitting.
- **It matches carefully.** A metadata source takes a result only when it is certainly the game.
  A wrong answer looks exactly like a right one, and "Portal" must never match "Portal 2".
- **It is polite to the site it uses.** No more requests than the job needs; the launcher paces
  them, but a search per game is the budget.
- **A theme is CSS and markup.** `<script>` and `on*` attributes are stripped by the launcher
  anyway; a theme that needs them is not a theme.

### Testing before you submit

An extension folder should carry a `test.mjs` that runs the module under Node against the live
site with a stand-in `loungepad` (HowLongToBeat's is the pattern). In the launcher, **Install from
a file → a folder** installs your working copy, **Reload from the folder** picks up an edit, and
**Open developer tools** on the add-on's page is its console.

## What is here

| | | |
|---|---|---|
| **HowLongToBeat** | extension | how long each game takes to beat, from howlongtobeat.com, on its page |
