-- The metadata cache: one row per answer, keyed exactly as the KV entries were.
--
-- WITHOUT ROWID makes the key the table's own order, so a write is one row written. A rowid table
-- with a TEXT primary key keeps a separate index for it, and D1 counts the index entry as a second
-- row written on every insert.
CREATE TABLE IF NOT EXISTS cache (
  key     TEXT PRIMARY KEY,
  value   TEXT NOT NULL,
  expires INTEGER NOT NULL      -- unix seconds; a row past it is never served
) WITHOUT ROWID;
