# Task p4-models: download and install models from the app

Branch `feat/model-manager`. PLAN.md, phase 4: "моделі завантажуються при першому запуску". This task builds the
engine only; the onboarding and settings screens that use it are later tasks.

## Scope
- New project `src/Murmur.Models` (class library, references Murmur.Core only; `IsAotCompatible`; add to Murmur.sln)
  and `tests/Murmur.Models.Tests`.
- Implements `Murmur.Core.Models.IModelManager` (src/Murmur.Core/Models/IModelManager.cs — architect-owned, read
  its doc comments, do not change it).
- `tools/Murmur.ModelFetch` console (references Murmur.Models) + a CI workflow `.github/workflows/models.yml` (see 6).
- Do NOT wire it into Murmur.Desktop/Murmur.App yet and do not change scripts/fetch-models.*.

## Spec
1. **Layout.** Install into the same layout the app already reads (`ModelLocator` in Core/Transcription and
   `AiModelPaths` in Murmur.Desktop, written today by scripts/fetch-models.ps1 — read them):
   - `<models>/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/{encoder.int8.onnx,decoder.int8.onnx,joiner.int8.onnx,tokens.txt}`
   - `<models>/silero_vad.onnx`
   - `<models>/llama.cpp/llama-server(.exe)` with its shared libraries next to it (whole archive content of the server's folder)
   - `<models>/gguf/<file>.gguf` and `<models>/gguf/models.json` `{ "chat": "<abs path>", "embed": "<abs path>", "repo": {...}, "file": {...} }`
     — keep the format AiModelPaths reads; installing a chat or embedding model updates that key (the "active" one),
     removing the active one clears it.
2. **Catalog** (pinned, data in code; per OS where it matters). Sizes are approximate display values; the real total
   comes from Content-Length. SHA-256: optional field, unknown for now (leave null; the CI run of step 6 prints the
   hashes so the architect can pin them later).
   | Id | Kind | Source | Required |
   |---|---|---|---|
   | `parakeet-tdt-0.6b-v3-int8` | Asr | Hugging Face `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`, the 4 files via `https://huggingface.co/<repo>/resolve/main/<file>` (~0.67 GB). No archive: .NET has no bzip2 and a new dependency needs the owner's approval. | yes |
   | `silero-vad` | Vad | `https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx` | yes |
   | `llama-cpp-b11716` | LlmRuntime | `https://github.com/ggml-org/llama.cpp/releases/download/b11716/llama-b11716-bin-win-cpu-x64.zip` on Windows, `…-bin-ubuntu-x64.tar.gz` on Linux (System.IO.Compression / System.Formats.Tar + GZipStream; copy the folder that contains llama-server, chmod +x on Linux) | no |
   | `embeddinggemma-300m-q8` | Embedding | HF `ggml-org/embeddinggemma-300M-GGUF`, file `embeddinggemma-300M-Q8_0.gguf` | no |
   | `gemma-4-e4b-q4` | Chat | HF `ggml-org/gemma-4-E4B-it-GGUF`, file `gemma-4-E4B-it-Q4_K_M.gguf` (default) | no |
   | `gemma-4-26b-a4b-q4` | Chat | HF `ggml-org/gemma-4-26B-A4B-it-GGUF`, file `gemma-4-26B-A4B-it-Q4_K_M.gguf` — UNVERIFIED name, CI checks it | no |
   These are the repos/files the CI ai-eval runs actually used (fetch-models tried them first); the 26B one is a guess.
   `RecommendedChatModelId`: 26B when the machine has ≥ 32 GB of physical memory (`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`), else E4B.
   AI items are useful only together: document it on the catalog; the UI later installs runtime + embedding + chat as one choice.
3. **Download.** HttpClient (one shared, no proxy override — honour the system proxy), streaming to `<dest>.part`,
   resume with `Range: bytes=<partLength>-` (handle 206, and 200 = server ignored the range → restart), retry
   transient failures (IOException, 5xx, timeouts) with backoff up to 3 times, then Failed with the error.
   Verify size (Content-Length or Content-Range total) and SHA-256 when known. Multi-file items download into a
   staging folder `<models>/.staging/<id>/` and are moved into place only when every file is complete and verified,
   so a half-installed model never looks installed (ModelLocator only checks file existence). Single files: atomic
   rename `.part` → final. Archives: extract into staging, then move. Disk space: check `AvailableFreeSpaceBytes`
   against the remaining size before starting; fail fast with a clear error.
4. **Status and events.** `GetStatus` reflects the disk (installed = all files present and non-empty, plus for
   chat/embedding the file existing; a leftover `.part` counts as partial progress), `StatusChanged` raised on state
   changes and progress (throttled ≤ 10/s), off the caller's thread. A second `InstallAsync` of the same id while one
   runs returns the same task; cancellation keeps `.part` files; `RemoveAsync` deletes the item's files (and clears
   models.json keys) and refuses while that item is installing.
5. **Tests** (Linux, no network): a local fake HTTP server (HttpListener on 127.0.0.1, see
   tests/Murmur.Intelligence.Tests/Fakes/FakeOpenAiServer.cs for a pattern that works on both OSes) serving files
   with/without Range support, slow bodies, mid-stream disconnects, wrong sizes, wrong hashes, 500s; cover resume,
   restart on 200, retries, verification failures, staging atomicity (a crash simulated by cancelling mid-way leaves
   nothing "installed"), zip and tar.gz extraction, models.json updates (and that AiModelPaths-style reading still
   works: copy its parsing expectations into a test, do not reference Murmur.Desktop), concurrency (same id joins,
   different ids in parallel), RemoveAsync, events throttling, free-space failure (inject the free-space probe).
   The catalog URLs/hosts are injectable so tests point them at the fake server.
6. **CI.** `tools/Murmur.ModelFetch`: `--dir <path> [--ids a,b] [--head-only a,b]` installs the given items with the
   real catalog and prints, per item, size, SHA-256 and elapsed time; `--head-only` only checks that every URL of the
   item answers (HEAD or a 1-byte Range GET) and prints Content-Length. Workflow `models.yml` (ubuntu-latest; on
   pull_request touching src/Murmur.Models/**, tools/Murmur.ModelFetch/** or the workflow, and workflow_dispatch):
   install `silero-vad,parakeet-tdt-0.6b-v3-int8,llama-cpp-b11716,embeddinggemma-300m-q8`, head-only the two chat
   models, then run the ASR worker test that needs the real Parakeet model against the downloaded folder if that is a
   one-liner (look at .github/workflows/asr.yml; otherwise skip this part). Summary table to $GITHUB_STEP_SUMMARY.
7. **Docs.** docs/CI.md: a section for the models workflow. THIRD-PARTY-NOTICES.md: nothing new (no new packages);
   if you think one is needed, stop and say so instead.

## Acceptance
Linux: 0 warnings (incl. AOT analyzers), tests green (numbers in the final notes). The real downloads are verified by
the architect's CI run of models.yml. Windows-specific bits (zip of the Windows llama.cpp build) UNVERIFIED-ON-WINDOWS
except through tests with a synthetic zip.
