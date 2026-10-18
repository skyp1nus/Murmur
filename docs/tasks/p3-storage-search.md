# Task p3-storage-search: BM25 keyword search and vector search in Storage

## Scope
src/Murmur.Storage/**, tests/Murmur.Storage.Tests/**, tests/Murmur.Benchmarks/** (vector search benchmark), Directory.Packages.props (System.Numerics.Tensors only, if used)

## Spec
Implement the ISearchIndex part of SqliteStorage (src/Murmur.Core/Sessions/IStorage.cs). Decision D-017 (architect): vectors are float32 BLOBs in SQLite searched by managed SIMD brute force instead of the sqlite-vec extension (no native extension to ship per platform; sqlite-vec 0.1 is brute force too; swap later behind ISearchIndex if a library outgrows it).
1. SearchKeywordAsync(query, scope, limit): FTS5 MATCH over `segments_fts` with bm25() ranking; sanitize user input into a safe FTS5 query (split on non-letters/digits, quote each token, append * for prefix match on the last token, AND semantics; empty -> no results); optional session scope; returns SearchHit(TextChunk(session, start, end, segment text), score = -bm25 (higher is better), Keyword). Works for Ukrainian/English (unicode61 remove_diacritics 2).
2. Chunks: migration adding `chunks(id INTEGER PRIMARY KEY, session_id TEXT REFERENCES sessions ON DELETE CASCADE, idx INTEGER, start_ms INTEGER, end_ms INTEGER, text TEXT, dim INTEGER, vector BLOB)`. ReplaceChunksAsync(session, chunks, embeddings): validates counts and equal dimensions; one transaction; vectors stored L2-normalized as little-endian float32.
3. SearchVectorAsync(embedding, scope, limit): cosine = dot product of normalized vectors; brute force with System.Numerics.Tensors TensorPrimitives.Dot (MIT, AOT-clean — verify) or Vector256; keep an in-memory cache of the vectors per session (float[] contiguous, invalidated on Replace/Delete) so repeated queries don't hit SQLite; top-k with a small heap (no full sort, no LINQ); scope filter; returns SearchHit(chunk, score = cosine, Vector).
4. Benchmark (tests/Murmur.Benchmarks): 10 000 chunks x 768 dims, top-10 — report time (target < 20 ms on this machine) and allocations.
5. Tests: keyword ranking and sanitization (quotes, operators like NEAR/AND/-, punctuation, Cyrillic, prefix), scope filter, vector top-k correctness vs a naive reference, dimension mismatch errors, cache invalidation on replace/delete, delete cascade.

## Acceptance
Build 0 warnings; Storage tests green; benchmark numbers reported.
