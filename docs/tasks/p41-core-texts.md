# Task p41-core-texts: no Ukrainian text from Core — codes in Core, words in the App

Branch `fix/core-texts` (based on `phase4` with `p41-english-default` merged). With English as the default UI language
(D-039) every Ukrainian sentence produced below the App shows up in an English UI. BACKLOG already lists it ("Локалізація
рядків із Core"). Parallel tasks: the design-parity harness (tests/Murmur.App.Tests/Design) and GPU acceleration
(Models/Intelligence/Settings acceleration) — avoid their files.

## Where the texts are (literal Ukrainian strings outside the App resources)
- `Murmur.Core/Recording/Recorder.cs`: the default session title `Запис {yyyy-MM-dd HH:mm}`, microphone/system-audio
  warnings (denied, unavailable, …).
- `Murmur.Core/Recording/ScreenFaultTexts.cs`: screen fault texts (D-038 already gives the UI a `CaptureFaultKind`).
- `Murmur.Core/Processing/SessionProcessor.cs`: "Parakeet is not installed", "Indexing failed: …".
- `Murmur.Intelligence/Rag/Assistant.cs`: `UntitledSession = "Без назви"` (reaches citations / sources in the UI).
- Out of scope, keep as is and say so: the assistant's system prompt and the transcript chunk labels «Я» / «Інші»
  (`TranscriptChunker`) — they are model input tuned by ai-eval (11/12), not UI text; the prompt already tells the model
  to answer in the question's language.

## Core contract (approved by the architect)
- User-facing problems from Core become **codes + parameters**: e.g. `RecorderWarning` gets a `RecorderWarningCode`
  (MicrophoneDenied, MicrophoneUnavailable, SystemAudioUnavailable, ScreenFramesDropped, ScreenCpuFallback, ScreenOther,
  ScreenStopped, …) plus optional detail (device name, OS message for logs); session warnings and `SessionRecord.Error`
  are stored as the code (+ detail) — choose the smallest model that works (e.g. a stable code string like
  `rec.mic.denied` with an optional detail, parsed by the App), document it in the XML docs. Processing failures the
  same way (`proc.asr.missing`, `proc.index.failed`). Logs keep English technical text.
- Default title: Core no longer invents a localized title. The App always passes `RecordingOptions.Title` — formatted
  like the canvas, "Untitled — Oct 18, 01:12" (English) / its Ukrainian translation — and Core's fallback for a null
  title is a neutral English "Untitled — <date>" (logs/tests only).
- Citations/sources with an untitled session carry an empty title; the App shows the localized "Untitled".
- Already stored sessions (the owner's PC): old Ukrainian `Error`/warning texts that are not codes are shown as they are
  (no migration of free text); new sessions store codes.

## Spec
1. Implement the contract; the App maps every code to `Strings` keys (English + Ukrainian; English wording short and in
   the canvas tone, e.g. "Microphone unavailable: Windows blocks access (Settings → Privacy & security → Microphone).
   Recording system audio only." — reuse existing App keys where they exist).
2. Every place that showed these texts (recording screen warnings, library/session error lines, toasts, onboarding
   trial) shows the localized text; switching the UI language changes them after restart (D-032).
3. Tests: code mapping (every code has both languages — a test that enumerates the codes), recorder emits codes, the
   processor stores codes, legacy free text passes through, the App's default title format in both languages,
   untitled citations. Update existing tests that asserted Ukrainian sentences from Core.
4. Report (Ukrainian): the code list, what the UI shows for each, files touched, tests run with results.
