# Task p1-storage: SQLite storage: sessions, folders, data paths

## Scope
src/Murmur.Storage/**, tests/Murmur.Storage.Tests/**, Directory.Packages.props (add Microsoft.Data.Sqlite / SQLitePCLRaw only)

## Spec
1. Murmur.Storage.StoragePaths: data root = env MURMUR_DATA_DIR, else Environment.SpecialFolder.LocalApplicationData/Murmur (Windows %LOCALAPPDATA%\Murmur, Linux ~/.local/share/Murmur); Recordings/ under it; database murmur.db.
2. Murmur.Storage.SqliteStorage : IStorage using Microsoft.Data.Sqlite (MIT; 11.0.0-rc.2.26504.105 matches the SDK) with the bundled e_sqlite3. FIRST prove AOT/trim cleanliness and FTS5 availability: a throwaway linux-x64 Native AOT console (outside the repo or deleted afterwards) that opens a DB, creates an FTS5 virtual table, inserts and MATCH-queries; record warnings (must be 0) and the result in notes. If Microsoft.Data.Sqlite is not AOT-clean, fall back to SQLitePCLRaw directly and note why.
   Schema with versioning (PRAGMA user_version + ordered migration SQL strings); WAL; foreign_keys on. Table sessions(id TEXT PRIMARY KEY, title TEXT, started_at INTEGER unix-ms, duration_ms INTEGER, state INTEGER, has_mic INTEGER, has_system INTEGER, screen_segments TEXT json, folder TEXT relative to the data root, error TEXT). ScreenSegments JSON via a System.Text.Json source-generated context (no reflection).
   Session folder: Recordings/yyyy-MM-dd_HH-mm-ss_<first 8 chars of id> (local time of StartedAt). CreateSessionAsync creates the folder + row (State Recording); UpdateSessionAsync; GetSessionAsync; ListSessionsAsync (newest first, From/To range, Limit); DeleteSessionAsync removes the row and the folder recursively; GetFiles(id) -> SessionFiles(absolute folder) (cache id->folder; works for sessions created in earlier runs).
   Transcript and search members (SaveTranscriptAsync, GetTranscriptAsync, SearchKeywordAsync, ReplaceChunksAsync, SearchVectorAsync) throw NotSupportedException("Arrives in phase 2"/"phase 3") for now — later phases implement them.
   Connections: Microsoft.Data.Sqlite pooling, one connection per operation (or a serialized shared connection) — correct under concurrent calls; never blocks the UI thread (async APIs).
3. Tests in tests/Murmur.Storage.Tests (each test uses its own temp data root via a ctor parameter, not the real profile): CRUD, list ordering/filtering/limit, delete removes the folder, data survives reopen, concurrent creates, migration from an empty DB, MURMUR_DATA_DIR override, GetFiles for a session created by a previous instance.

## Acceptance
Build 0 warnings; Storage tests green; AOT probe shows Microsoft.Data.Sqlite + FTS5 working under Native AOT with 0 warnings (evidence in notes).
