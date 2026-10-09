-- What an address's like on an add-on is today, so changing your mind counts (Oct 9 2026: a like,
-- an unlike and a like again from one PC left the third uncounted under the one-like-a-day mark).
-- The key is the same kind of hash as a mark's -- the day's salt, the address and the add-on -- so
-- nothing here names an address either, and the row goes with its day.
CREATE TABLE IF NOT EXISTS like_state (
  k       TEXT PRIMARY KEY,
  liked   INTEGER NOT NULL,     -- 1 liked, 0 not, as of this address's last change today
  changes INTEGER NOT NULL,     -- how many times it changed today; capped (MAX_LIKE_CHANGES)
  expires INTEGER NOT NULL
) WITHOUT ROWID;
