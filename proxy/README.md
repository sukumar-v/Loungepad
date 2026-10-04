# Loungepad metadata proxy

Holds the IGDB and SteamGridDB credentials so the launcher does not ship them, which means a user
installs Loungepad and gets artwork with no signup, no API key and no settings to fill in.

This is the same arrangement Playnite uses. Its IGDB plugin ships no keys at all — its
`IgdbClient` takes a base URL rather than credentials, and `plugin.cfg` points it at
`https://api2.playnite.link/api/`. The reason is not preference: a desktop binary cannot keep a
secret, and Twitch's own guidance is that the client secret must never be exposed to users.

## What it costs

Cloudflare's free tier covers 100,000 requests a day and 1,000 KV writes a day, and this is
written to sit well inside that:

- **The launcher asks the proxy about very few games.** Steam titles are resolved by app id
  straight from Steam, with no key and no proxy involved. Non-Steam games are first looked up
  against Steam's own keyless search. Only what survives both — Epic and Game Pass exclusives,
  GOG-only classics, ROMs, itch.io games — reaches this service.
- **Answers are cached for 30 days, shared across every user.** Libraries overlap enormously, so
  the hundredth person to own a game costs one KV read and no upstream request.
- **Misses are cached too**, for 3 days, so a game nobody's database has does not re-ask forever.

The practical ceiling is IGDB's, not Cloudflare's: 4 requests/second across the whole credential,
shared by everyone. The cache is what keeps you under it.

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

Create the cache. Note the two names: `loungepad-metadata-cache` is the namespace's title in your
Cloudflare account, shared with every Worker you ever deploy, so it should be specific.
`METADATA` is only how this worker's own code refers to it (`env.METADATA`), and is scoped to
this service.

```bash
npx wrangler kv namespace create loungepad-metadata-cache --binding METADATA --config proxy/wrangler.toml
```

This prints an `id` — paste it into `proxy/wrangler.toml`, replacing
`PUT_YOUR_KV_NAMESPACE_ID_HERE`. Do this **before** deploying; the placeholder is not a real
namespace and a deploy carrying it will fail.

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
curl "https://consolify-metadata.<your-subdomain>.workers.dev/v1/health"
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

**`KV namespace ... is not valid`** — the `kv namespace create` step has not been done, or its id
was not pasted into `proxy/wrangler.toml`.

**You named the namespace something you regret** — `npx wrangler kv namespace list` shows what you
have, and `npx wrangler kv namespace rename <old-name> <new-name>` fixes it without touching the
id, so `wrangler.toml` needs no change. The contents are a cache and can be thrown away regardless.

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
  ROM, the IGDB platform ids of its system. KV holds answers keyed on those, the IGDB token, and
  rate counters keyed on a hash of the client's address and the minute -- never the address
  itself. Invocation logs are off in `wrangler.toml`, so Workers Logs keeps only the worker's own
  console lines, which name titles and app ids on failure and nothing else.
- **The free tier's KV write quota is the service's weak point.** Every cache miss is a write (two
  with the rate counter), and the free plan allows a thousand a day. A refused write no longer
  fails the request -- the answer in hand is returned and the counter or cache entry is simply not
  kept -- but a busy day still means the cache stops growing until midnight UTC. Workers Paid, or
  Cloudflare's Rate Limiting binding in place of the KV counter, is the next step if the launcher
  finds an audience.
