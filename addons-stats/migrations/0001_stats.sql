-- Add-on downloads and likes (addons-stats/src/worker.js).
--
-- counts: one row per add-on, the two numbers the launcher shows. Keyed "<kind>:<id>", the key the
-- add-ons repository's index and the launcher both use.
CREATE TABLE IF NOT EXISTS counts (
  key       TEXT PRIMARY KEY,
  downloads INTEGER NOT NULL DEFAULT 0,
  likes     INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;

-- marks: "this address already did this to this add-on today". The key is a SHA-256 of the day's
-- salt, the address, the action and the add-on, so the address itself is never stored, and once
-- the day's salt is deleted (the daily cron) a mark cannot be tied back to anyone.
CREATE TABLE IF NOT EXISTS marks (
  k       TEXT PRIMARY KEY,
  expires INTEGER NOT NULL      -- unix seconds; the cron deletes rows past it
) WITHOUT ROWID;

-- salts: one random salt per UTC day, made on the day's first write and deleted the day after.
CREATE TABLE IF NOT EXISTS salts (
  day  TEXT PRIMARY KEY,        -- YYYY-MM-DD, UTC
  salt TEXT NOT NULL
) WITHOUT ROWID;
