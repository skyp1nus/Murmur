# Task p3-chat-ui: "Запитати AI" chat with timestamp chips, library search, AI wiring

## Scope
src/Murmur.App/**, src/Murmur.Desktop/**, tests/Murmur.App.Tests/**, docs/screenshots/**

## Spec
PLAN.md 'Екрани MVP' #4 (AI chat with timestamp chips) and the library search.
1. Session view tab 'Запитати AI': chat list (user bubbles right on Surface, assistant answers left), input pill + primary 'Запитати' button; streaming text appears as it arrives (IAssistant.AskAsync deltas); citations rendered as chips under the answer (Geist Mono label like 12:34, outline pill, achromatic); clicking a chip seeks the player to that time and plays; history kept per session while the view is open; cancel button while generating; states: AI not installed (explain scripts/fetch-models.ps1 -Ai), AI paused while recording, errors.
2. Library: the pill search box runs hybrid search (IAssistant.SearchAsync; falls back to keyword-only when AI/embeddings are not installed — the Assistant handles it or catch and call ISearchIndex.SearchKeywordAsync) with debounce 300 ms; results list: session title, time chip, snippet; click opens the session at that time. A 'Запитати AI по всіх записах' action opens a library-wide chat (Scope = null) with the same chip behaviour (chip opens the right session at the right time).
3. Composition root (Murmur.Desktop): LocalLlm.FromEnvironment() (+ models dir defaults from ModelLocator / fetch-models layout <models>/llama.cpp, <models>/gguf/models.json), Assistant(llm, storage), SessionProcessor with indexer = assistant.IndexSessionAsync and AI-installed check; recording guard: RecordingActivity.RecordingStarted -> LocalLlm.NotifyRecordingStarted(); dispose LocalLlm on exit. App references Core only.
4. Screenshots (Avalonia.Headless): docs/screenshots/phase3-chat.png (an answer with 2 chips), phase3-library-search.png.
5. Tests: view-model tests with fake IAssistant (streaming accumulation, chips -> seek/navigation with the right session and time, cancel, not-installed state, library search debounce and keyword fallback).

## Acceptance
Build 0 warnings; App tests green; ArchitectureTests green; linux AOT publish of Murmur.Desktop 0 warnings; screenshots committed.
