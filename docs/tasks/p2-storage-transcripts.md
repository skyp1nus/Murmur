# Task p2-storage-transcripts: Transcripts in SQLite with an FTS5 index

## Scope
src/Murmur.Storage/**, tests/Murmur.Storage.Tests/**

## Spec
Implement the ITranscriptStore part of `SqliteStorage` (phase 1 left it throwing NotSupportedException).
1. Migration (next PRAGMA user_version step, keep existing migrations untouched): table `segments(id INTEGER PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE, idx INTEGER, speaker INTEGER, start_ms INTEGER, end_ms INTEGER, text TEXT, words BLOB)` with an index on (session_id, start_ms); table `transcripts(session_id TEXT PRIMARY KEY REFERENCES sessions ON DELETE CASCADE, engine TEXT, language TEXT, created_at INTEGER)`; FTS5 external-content table `segments_fts(text, content='segments', content_rowid='id', tokenize='unicode61 remove_diacritics 2')` kept in sync explicitly (insert/delete rows in the same transaction; no triggers needed, but triggers are fine if simpler). Words are stored compactly in `words` as a little-endian binary blob (count, then per word: start_ms int32, end_ms int32, utf8 length uint16, utf8 bytes) — no reflection serializers; write a small codec with tests.
2. SaveTranscriptAsync(transcript): one transaction: delete old segments (+ their FTS rows) of the session, insert new segments + FTS rows, upsert `transcripts`. GetTranscriptAsync(id): segments ordered by start_ms (then idx), words decoded, null when no transcript. DeleteSessionAsync also removes segments/FTS rows (cascade + FTS cleanup).
3. Keyword search stays NotSupported (phase 3), but make sure the FTS table is queryable: add an internal helper used by tests that runs a MATCH query, so phase 3 can build on it.
4. Performance: saving a 1-hour transcript (~900 segments, ~10k words) < 300 ms on this machine; loading < 100 ms — add a test that asserts generous bounds (x3) and report the numbers.
5. Tests: roundtrip (speaker, times, words incl. Cyrillic and emoji), replace semantics, delete cascade incl. FTS, FTS MATCH finds Ukrainian words (e.g. "бюджет") and is updated on replace, empty transcript, concurrent save/get.

## Acceptance
Build 0 warnings; Storage tests green; numbers for save/load of a 1-hour transcript reported.
