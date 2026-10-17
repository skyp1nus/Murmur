# Design prompt — Onboarding

Paste into your design tool (Figma AI, Claude Design, v0, etc.). Add the existing Murmur screens as reference so the style matches.

```
Create a NEW PAGE named "Onboarding" in the Murmur design file. Do not change existing pages.

Product: Murmur — a local-first desktop meeting recorder (Windows first). It records mic + system audio + optional screen, transcribes on-device, and lets you ask AI questions about recordings. Nothing leaves the computer.

Frame: desktop window 1280×800, light theme. Lay out 5 artboards left to right, one per step, plus one small component sheet.

Style (must match the existing screens and docs/design/tokens.json):
- Warm editorial minimalism, ElevenLabs-like. Canvas #fdfcfc, surfaces #f5f3f1, hairlines 1px #ebe8e4.
- Text: ink #000000, secondary #777169, tertiary #a59f97.
- Headings Inter Light 300, letter-spacing −2%, 36–48px. Body Inter 400/500, 14–16px. Numbers, sizes and timers in Geist Mono 13px.
- Buttons are pills (radius 9999): primary = black fill + white text, secondary = #fdfcfc fill + 1px #e5e5e5 border. Cards radius 20, inputs radius 4.
- The ONLY colour is the recording orb: soft radial gradient of violet #0447ff and ember #ff4704. Never use these colours on buttons, text or borders.
- Flat: no heavy shadows, generous whitespace, one idea per screen.

Shared layout for every step:
- Top: step indicator "1 / 5" in Geist Mono + 5 thin progress segments.
- Centre: one large headline, one line of secondary text, the step's content.
- Bottom right: primary "Continue"; bottom left: text button "Skip" (hidden on step 2 mic permission) and "Back".

Steps:
1. Welcome — large animated recording orb (centre). Headline "Record and transcribe your meetings." Subline "Everything stays on your computer." Primary button "Get started".
2. Audio — headline "Let's check your sound." Two cards side by side: "Microphone" and "System audio", each with a device dropdown and a live level meter (thin bars, ink colour). A permission banner card for microphone access with button "Allow microphone". Continue disabled until mic is allowed.
3. Models — headline "Download the brains." Card "Transcription · Parakeet v3 · 0.67 GB · Required" with a progress bar and "Downloading…" state. Below, two selectable cards for AI: "Gemma 4 E4B · recommended for your PC" and "Gemma 4 26B · best quality, needs GPU", each with size in Geist Mono; plus a text option "Skip AI for now". Note under them: "Downloads continue in the background."
4. Preferences — headline "Make it yours." A vertical settings card: Transcription language (dropdown, "Auto-detect"), Recordings folder (path + "Change" + free space in Geist Mono), Start/stop hotkey (key-capture field showing "Ctrl + Shift + R"), Record screen by default (toggle, off).
5. First recording — headline "Try it: say something." Smaller orb that reacts to voice, a 10-second countdown in Geist Mono, then a transcript card showing the recognised sentence with a timestamp chip "00:03". Small consent note in secondary text: "Always tell people when you're recording a meeting." Primary button "Open Murmur".

Component sheet (separate small artboard): step indicator, progress segments, primary/secondary/text buttons (default, hover, disabled), device dropdown, level meter, model card (default, selected, downloading, done), toggle, hotkey field, timestamp chip.

Also add a second variant of step 3 showing all downloads finished (checkmarks, "Ready").

Use English UI copy. Name every layer clearly (e.g. "Onboarding / 3 Models / Model card – selected") so developers can map them to Avalonia controls.
```

After the mockups are ready, export each artboard as PNG into `docs/design/` as `00-onboarding-1-welcome.png` … `00-onboarding-5-first-recording.png`.
