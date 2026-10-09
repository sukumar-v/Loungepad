# Loungepad metadata proxy

Holds the IGDB and SteamGridDB credentials so the launcher does not ship them, which means a user
installs Loungepad and gets artwork with no signup, no API key and no settings to fill in.

This is the same arrangement Playnite uses. Its IGDB plugin ships no keys at all — its
`IgdbClient` takes a base URL rather than credentials, and `plugin.cfg` points it at
`https://api2.playnite.link/api/`. The reason is not preference: a desktop binary cannot keep a
secret, and Twitch's own guidance is that the client secret must never be exposed to users.

## What it costs

Cloudflare's free tier covers 100,000 requests a day and 100,000 D1 rows written a day. The
account is on Workers Paid ($5 a month) since Oct 8 2026, which raises both a long way:

- **Every new game is two lookups** (`/v1/facts` and `/v1/art`), and a lookup of something nobody
  has asked about yet is one row written. Nothing else writes: the rate limit is Cloudflare's own
  binding and costs no storage at all.
- **Answers are cached for 30 days, shared across every user.** Libraries overlap enormously, so
  the hundredth person to own a game costs one row read and no upstream request.
- **Misses are cached too**, for 3 days, so a game nobody's database has does not re-ask forever.
- **ROM collections are the heavy case.** A launcher pointed at a few thousand ROMs asks about every
  one of them on its first pass. That is what ended the KV cache: on Oct 7-8 2026 one such pass
  spent KV's 1,000 free writes a day in six minutes and ran on for five hours keeping nothing. D1
  allows a hundred times that.

The practical ceiling is IGDB's, not Cloudflare's: 4 requests/second across the whole credential,
shared by everyone. The cache is what keeps you under it.

## Where it lives

The service is the Worker `loungepad-metadata-service`, answering `https://api.loungepad.app` (a
Custom Domain on the loungepad.app zone, made by `wrangler deploy` from the `routes` entry). From
1.9.0 the launcher only knows that address, so the Worker behind it can be renamed, rewritten or
moved somewhere else with nothing shipped.

Builds up to 1.8.0 have the old address baked in: `consolify-metadata.s-varmagt.workers.dev`. That
Worker is now `legacy/forwarder.js`, which hands every request to the new one through a service
binding, client address and headers intact. Delete it once nobody runs a build from before 1.9.0:

```powershell
npx wrangler delete --name consolify-metadata
```

`/v1/addons/*` is handed on the same way to `loungepad-addons-stats` (`../addons-stats`), the add-on
downloads and likes; see the Loungepad repository's docs/ADDONS.md.

## Getting the credentials

IGDB is not signed up for at igdb.com -- API access goes through Twitch, who own it. You need a
Twitch account with 2FA enabled, then an application registered at dev.twitch.tv. SteamGridDB is
its own account and takes about a minute.

Once you have them, check they work before deploying anything:

```powershell
$env:IGDB_CLIENT_ID = "..."
$env:IGDB_CLIENT_SECRET = "..."
$env:SGDB_KEY = "..."
.\verify-credentials.ps1
```

It reads from the environment so the values stay out of your shell history, and prints nothing but
pass/fail and the titles that came back.

## Deploying

You need a Cloudflare account (free), an IGDB client id/secret, and a SteamGridDB key.

Every command below passes `--config proxy/wrangler.toml` and so can be run **from the repository
root**, in any order, in a fresh terminal. Wrangler otherwise looks for its config in the current
directory only, and fails with `Required Worker name missing` when it does not find one.

Sign in to Cloudflare (opens a browser):

```bash
npx wrangler login
```

Create the cache. Note the two names: `loungepad-metadata-cache` is the database's name in your
Cloudflare account, shared with every Worker you ever deploy, so it should be specific. `CACHE` is
only how this worker's own code refers to it (`env.CACHE`), and is scoped to this service.

```bash
npx wrangler d1 create loungepad-metadata-cache
```

This prints a `database_id`. Paste it into `proxy/wrangler.toml` under `[[d1_databases]]`, then make
the table (`proxy/migrations/0001_cache.sql`):

```bash
npx wrangler d1 migrations apply loungepad-metadata-cache --remote --config proxy/wrangler.toml
```

`wrangler.toml` also binds the KV namespace the cache used to be (`METADATA`), which is only read
now, until its entries expire (Nov 8 2026). A fresh deployment has no such namespace: delete the
`[[kv_namespaces]]` block, and the `env.METADATA` fallback in `cacheGet` goes with it.

The rate limit (`[[ratelimits]]`) and the daily clean-up of expired rows (`[triggers]`) need nothing
created; the deploy sets both up.

Set the three credentials. Each prompts for its value, which is encrypted at rest and never
written to the repo:

```bash
npx wrangler secret put IGDB_CLIENT_ID --config proxy/wrangler.toml
```

```bash
npx wrangler secret put IGDB_CLIENT_SECRET --config proxy/wrangler.toml
```

```bash
npx wrangler secret put SGDB_KEY --config proxy/wrangler.toml
```

There is no Steam key any more. The two routes that used one (`/v1/owned`, `/v1/achievements`) are
gone: they took a SteamID, which made them the only part of the service that saw anything
identifying, and they let anyone with the URL read any Steam account's library through the
service's key. The launcher reads both from Steam directly, with the user's own sign-in or key. If
a `STEAM_API_KEY` secret is still set from an earlier deploy, delete it:

```bash
npx wrangler secret delete STEAM_API_KEY --config proxy/wrangler.toml
```

Check it builds without uploading anything:

```bash
npx wrangler deploy --dry-run --config proxy/wrangler.toml
```

Then deploy, and confirm it is up:

```bash
npx wrangler deploy --config proxy/wrangler.toml
```

```bash
curl "https://api.loungepad.app/v1/health"
```

(The worker still carries the app's old name, Consolify, because a worker's name is its URL and
every shipped build points at that URL. `proxy/wrangler.toml` says what renaming it involves.)

Finally, point the launcher at it by setting `DefaultEndpoint` in
`Loungepad/Services/MetadataProxyClient.cs` to that URL and rebuilding. Users can override it in
Settings, but the shipped default is what makes it zero-setup.

## When something goes wrong

**`Required Worker name missing`** — wrangler is running somewhere without a config file. It reads
`wrangler.toml` from the current directory, not from the repository root, so this appears whenever
a command is run from anywhere but `proxy/`. Add `--config proxy/wrangler.toml`, as every command
above does.

**`no such table: cache`** in the worker's logs — the migration was not applied to the remote
database. Run the `d1 migrations apply ... --remote` step above.

**Checking what the cache holds** — `npx wrangler d1 execute loungepad-metadata-cache --remote
--command "SELECT count(*) FROM cache"`. The contents are a cache and can be thrown away regardless.

**Something is using a lot of the quota** — each row's `expires` is when it was written plus 30
days (an answer) or 3 days (a miss), so grouping the rows by `expires` shows when the writes
happened. Keys ending `:p<ids>` are ROMs, with the system's IGDB platform ids.

**The worker deploys but `/v1/facts` returns 502** — the credentials are wrong or missing. Check
them on their own first with `proxy/verify-credentials.ps1`, then confirm all three secrets are
set with `npx wrangler secret list --config proxy/wrangler.toml`.

## Endpoints

    GET /v1/facts?title=<title>[&appid=<id>][&platform=<igdb ids>]
                                  → { name, summary, developer, publisher, genres[],
                                      released, criticScore, pegi, cover, artwork, video,
                                      videos[], screenshots[] }
    GET /v1/art?title=<title>[&appid=<id>]
                                  → { name, portrait, tile, hero, logo }
    GET /v1/health                → { ok: true }

Every request except `/v1/health` must carry the header `X-Loungepad-Client: 1`, or it is answered
`403` before anything is looked up. The launcher sends it on every call. It is not a secret -- it
is in the shipped binary -- but it turns away the traffic that only knows the URL.

`404` means "no confident answer", which is a normal outcome rather than a failure. The launcher
treats a 404, a 429 and a dead connection identically: it keeps whatever art it already had.

Every response carries the matched `name`, and **the launcher re-checks it against its own strict
title rule before accepting anything**. The proxy being loose, wrong or compromised cannot put the
wrong game's art on a tile.

Icon and image URLs the launcher receives from here are checked on its side too (`https` only,
plain URL characters) before they are written into its page.

## Things to know before you run this

- **You become the accountable party.** Under the Twitch Developer Service Agreement the traffic
  through your IGDB credential is yours, whoever generated it. Same for your SteamGridDB key.
- **IGDB's free tier is non-commercial.** That now applies to a service you operate, not just to
  your own copy of the app. If Loungepad ever takes money, this needs revisiting first.
- **When this is down, artwork is down for everyone.** That is the trade you accept for zero
  setup — Playnite has the same failure mode, which is what their recurring "IGDB is broken"
  issues actually are. The launcher degrades quietly rather than erroring, and Steam games are
  unaffected because they never touch this.
- **Put a contact address in the worker's User-Agent** if you publish widely, so an upstream that
  is unhappy with your traffic can reach you before it revokes the key.
- **Nothing here is about a person.** A request carries a game's title, its Steam app id and, for a
  ROM, the IGDB platform ids of its system. The cache holds answers keyed on those, and the IGDB
  token. The rate limit is keyed on a hash of the client's address, never the address itself, and
  is counted by Cloudflare, not stored here. Invocation logs are off in `wrangler.toml`, so Workers
  Logs keeps only the worker's own console lines, which name titles and app ids on failure and
  nothing else.
- **A refused cache write does not fail the request.** The answer in hand is returned and the entry
  is simply not kept (`cache: write failed` in the logs). A rate-limit binding that errors lets the
  request through. Neither is ever a 502.
