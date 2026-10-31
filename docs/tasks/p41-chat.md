# Task p41-chat: Ask AI exactly as the canvas, with saved chats

Branch `design/chat` (based on `phase4` after wave 2). Boards: `Chat`, `ChatDark`, plus the **Chats** list in the sidebar
(the hook `p41-shell-library` left on the sidebar context list). Truth: `docs/design/source/Chat*.dc.html`,
`docs/design/reference/Chat*.png`; measure with `DesignParityTests`.

## Core contract (approved by the architect — exactly this)
- `Murmur.Core.Intelligence`: `ChatThreadId` (readonly record struct over a string), `ChatThread(ChatThreadId Id, string
  Title, SessionId? Scope, DateTimeOffset UpdatedAt)`, `ChatMessage(ChatThreadId Thread, int Index, TurnRole Role, string
  Text, IReadOnlyList<Citation> Citations, DateTimeOffset At)`.
- `Murmur.Core.Sessions.IChatStore` (`ListThreadsAsync(limit)`, `CreateThreadAsync(title, scope)`, `GetMessagesAsync`,
  `AppendMessageAsync`, `RenameThreadAsync`, `DeleteThreadAsync`), part of `IStorage`; SQLite implementation in
  `Murmur.Storage` with a migration. A thread scoped to a deleted session is deleted with it; citations to a deleted
  session stay in the text but stop being links.

## Spec (UI, `Chat.dc.html`)
1. Header: scope pills **Whole library** / **<session title>** (the latter only when the chat was opened from a session or
   a thread is scoped; the dot marks the active scope) and the model chip "Gemma 4 E4B · local" (the active chat model,
   with the acceleration from D-042 when not CPU, e.g. "· Vulkan").
2. Conversation column (max 820 px, centred): user bubbles right-aligned; answers with the orb avatar + "Murmur", text
   16/1.65 with inline time chips (Geist Mono, play glyph; a cross-session citation shows "Retro · 31:05"); **Sources**
   cards (title, time, quoted snippet) in an auto-fit grid; Copy answer and Regenerate icon buttons; the composer at the
   bottom (rounded 24, shadow in light / border in dark, send button) and the disclaimer line.
3. Threads: the first question names a new thread; every exchange is saved; the sidebar **Chats** list shows recent
   threads (the open one highlighted), click reopens it with its history; rename / delete from a context menu; a "New
   chat" affordance consistent with the canvas (e.g. the Ask AI nav item starts a new chat when the current one is not
   empty — choose and describe). The Library's "Ask your library" hand-off (from `p41-shell-library`) opens a new thread.
4. Streaming, cancellation, missing-model states, AI-busy-while-recording states keep working — restyle them.
5. Parity: content area of `Chat` / `ChatDark` within the parity gates with the two drawn exchanges; set the three parity gates (below).
6. Tests: storage (threads, order, cascade on session delete), view models (thread lifecycle, regenerate replaces the last
   answer, copy, scope pills), sidebar list.
7. Report (Ukrainian): mismatch before/after, what still differs, test results.

## Parity gates (D-041)
`DesignParityTests` measure three things per board (report: `artifacts/design-parity/report.md`, per board `<Board>.side.png`,
`<Board>.shapes.side.png`, `<Board>.text.md`, `<Board>.text.png`):
- **Shape layer** (app rendered with every glyph transparent vs `reference/<Board>.notext.png`, symmetric, tolerance 5/255):
  `MaxShapeMismatchPercent` ≤ **4 %** of drawn pixels (onboarding steps 3 and 4: ≤ 10 % until measured lower).
- **Text matched** (reference texts from `reference/<Board>.text.json` found in the app by exact text): `MinTextMatchedPercent`
  ≥ **95 %**; every text still missing is either a documented deviation (`docs/design/DEVIATIONS.md`) or demo data the
  app cannot produce — list them in the report.
- **Text geometry** (matched texts: |Δx| and |Δ centre y| ≤ 2 px, width within 3 %, same line count, size, weight, family):
  `MaxTextGeometryErrors` ≤ **2**.
Set the three gates for your boards in `DesignBoards.cs` at the values you reach (not looser than above) so CI keeps them.
Pixel-level text differences (Chromium vs Skia rasterisation) are not a gate. Work from `<Board>.text.md` (sorted list of
text errors) and the side-by-sides; check visually at the end that nothing differs that the metrics miss (e.g. icon
shapes, orb look, hover/selected states drawn on the board).
