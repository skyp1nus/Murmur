# Нічний прогін: фази 0–3

Гілка `night-run` від `master`, автономний режим (архітектор + субагенти `coder` / `ui` / `reviewer` / `tester`), 2026-10-17 23:30 → 2026-10-18 ~09:30 UTC.

## Коротко

| Фаза | Зроблено | Критерій переходу | Результат | Тег |
| --- | --- | --- | --- | --- |
| 0 · Фундамент | solution за PLAN, Native AOT, токени в Avalonia, інтерфейси Core, CI, спайк ASR | AOT-старт < 400 мс | ✅ **пройдено**: медіана 289 мс, холодний 362 мс (windows-latest, 7 запусків); з кодом фаз 1–3 — медіана 328 мс, холодний 346 мс | `phase-0-done` |
| 1 · Запис на Windows | lock-free аудіопайплайн, crash-safe WAV, WASAPI mic+loopback (власний interop), WGC → Media Foundation H.264, UI запису зі «сферою», `--autorecord` | 1 год запису без втрат, CPU у межах бюджету | ⚠️ **частково**: «1 год без втрат» — ✅ (прискорений тест на реальному конвеєрі, зелений на Linux і **на Windows CI**); реальний WASAPI-запис 60 с на Windows CI — ✅ (exit 0); годинний реальний запис і CPU — {PERF} | `phase-1-done` |
| 2 · Транскрипція | ASR-воркер (Parakeet TDT v3 + Silero VAD, таймкоди слів), клієнт/черга/обробка, транскрипти + FTS5, WASAPI-плеєр, перегляд запису з синхронним транскриптом | 1 год аудіо → текст < 3 хв на CPU | ❌ **не пройдено на 4-ядерних CI-раннерах**: 197 с (Linux) / 227 с (Windows) проти 180 с. Робота впирається в CPU (утилізація 3,97 з 4 ядер) — на 6–8-ядерному ПК має вкладатися; перевірити вручну (чекліст, крок 6) | `phase-2-done` |
| 3 · AI-пошук | FTS5 BM25 + SIMD-вектори, llama-server + Gemma 4 / EmbeddingGemma, RAG з таймкодами, чат із чипами, пошук у бібліотеці, eval «відповідь → хвилина» | Відповідь AI веде на правильну хвилину | {AIEVAL} | `phase-3-done` |

Що лишилось BLOCKED / неперевірене — див. «Відкриті ризики». Що перевірити руками — «Чекліст для Windows» внизу.

## Середовище й обмеження нічного прогону

- Хмарний Linux x64, 4 vCPU, 15 ГБ RAM, без GPU й аудіопристроїв. `.NET SDK 11.0.100-rc.2.26504.105` — узято з образу MCR, бо `dotnet-install.sh`/`builds.dotnet.microsoft.com` заблоковані проксі (D-001).
- Hugging Face тут недоступний → Gemma/EmbeddingGemma тут не запускались; реальну LLM-перевірку робить workflow `ai-eval` у GitHub Actions.
- Windows — лише через GitHub Actions `windows-latest`: Windows Server 2025 (10.0.26100), AMD EPYC 7763, 4 vCPU, 16 ГБ, **без GPU (WARP)**, без аудіопристроїв; віртуальна звукова карта (VB-Cable через `LABSN/sound-ci-helpers`), доступ до мікрофона заборонений налаштуваннями конфіденційності.
- Пуш тегів через проксі середовища не проходить → теги створює workflow `tags.yml` з `.github/phase-tags.txt` (D-018).
- Фази йшли конвеєром (D-015): незалежні модулі наступних фаз писались паралельно в окремих worktree; мерж у `night-run` і теги — у порядку фаз.

## Фаза 0 — фундамент ✅

Зроблено:
- `Murmur.sln`: Core, App (бібліотека UI), **Desktop** (exe `Murmur`, composition root, D-004), Capture.Windows, Transcription (воркер), Intelligence, Storage + тестові проєкти, бенчмарки, `tools/Murmur.TokenGen`. `Directory.Build.props`: warnings = errors, `IsAotCompatible` для `src/`.
- Інтерфейси Core (архітектор): `ICapture`/`IAudioSource`/`IAudioSink`/`IScreenRecorder`/`IAudioPlayer`, `IRecorder`, `IStorage` (= `ISessionStore` + `ITranscriptStore` + `ISearchIndex`), `IAsr`, `ILlm`, `IAssistant` (D-009); протокол воркера ASR (D-014).
- Токени: `tokens.json` → генератор → `Styles/Tokens.axaml`; тест синхронізації; тест-«охоронець», що Violet/Ember є лише в `OrbControl`.
- CI (`ci.yml`): Linux і Windows build + тести, win-x64 Native AOT з гейтом на попередження, заміри старту, BenchmarkDotNet.
- Спайк: WASAPI loopback → sherpa-onnx → текст. На Linux: правильні тексти en/de/fr/es під JIT і Native AOT, RTF 0,12–0,15, 0 AOT-попереджень. Знайдено баг розкладки в керованих біндингах sherpa-onnx → власний blittable-шар (D-013).

Доказ критерію: CI run [39088738986](https://github.com/skyp1nus/Murmur/actions/runs/39088738986) — startup: 362,5 (cold) / 277,7 / 300,7 / 289,2 / 296,8 / 256,3 / 286,2 мс → медіана **289 мс** < 400.

## Фаза 1 — запис на Windows

Зроблено:
- Core: lock-free SPSC-кільце, `CaptureTrack` (пік рівня для сфери), стрімінговий windowed-sinc ресемплер → 16 кГц mono, crash-safe `WavPcm16Writer` + `WavRepair`, `Recorder` (окремий потік запису, сегменти екрана), `RecoveryService`, `SyntheticCapture` для Linux.
- Після CI: write-behind запис WAV — потік запису ніколи не чекає на диск (D-022); запис без мікрофона, якщо Windows забороняє доступ (D-023); утримання сегмента екрана при невдалому вимкненні.
- Storage: SQLite (Microsoft.Data.Sqlite, WAL, міграції через `user_version`), папки сесій.
- Capture.Windows: WASAPI mic + loopback (власний `[GeneratedComInterface]`-interop, event-driven, MMCSS, вирівнювання за wall-clock для тихого loopback); WGC → Media Foundation H.264 у фрагментованих MP4-сегментах; zero-copy на GPU з апаратним енкодером, CPU-readback на машинах без нього (D-027); порожній сегмент не ламає зупинку (D-024).
- App: бібліотека, екран запису (сфера дихає від рівня звуку, pill-перемикачі, таймер Geist Mono, вибір пристроїв), режим `--autorecord <с> [--screen] --perf-out <json>` для CI.
- CI: `capture-e2e` (віртуальна звукова карта + тон 440 Гц, 60 с запису), `perf-record` (1 год лише аудіо + 10 хв аудіо+екран).

Докази:
- Тест «1 година без втрат» (реальний `Recorder` + потоки, 1 год синтетичного аудіо з кодованим сигналом, прискорено ×80): 0 втрачених кадрів, рівно 3600 с × 16000 семплів, без пропусків/повторів — **зелений на Linux і на Windows CI** (до фіксу D-022 на Windows він ловив 1,6 млн втрачених кадрів через 5,7-секундний `FlushFileBuffers`).
- Crash-тест: процес убито на 5,401 с → відновлено 5,348 с аудіо.
- Бенчмарки гарячих шляхів (Linux, 0 B/op): кільце 10 мс — 177 нс; `CaptureTrack.Write` 48 кГц стерео 10 мс — 538 нс; ресемплінг 48k→16k 10 мс — 4,3 мкс; WAV append — 646 нс.
- `capture-e2e` на windows-latest (run [39111039496](https://github.com/skyp1nus/Murmur/actions/runs/39111039496)): `Murmur.exe --autorecord 60 --screen` записав 60 с і завершився з кодом 0 (мікрофон заборонено політикою → лише системний звук, як і має бути після D-023).
- {PERF_DETAILS}

## Фаза 2 — транскрипція

Зроблено:
- `Murmur.Transcription` — окремий AOT-процес: Silero VAD (вікна 512 семплів, фрази ≤ 15 с), Parakeet TDT v3 int8, паралельне декодування (D-025), слова з таймкодами з токенів TDT, JSON-lines сервер, лінивe завантаження, вихід за простоєм, `--bench`. Маніфест з UTF-8 ACP для кириличних шляхів.
- Core: `WorkerAsrClient` (перезапуск воркера, скасування, stderr у помилках), `JobQueue` з пріоритетами й паузою під час запису, `SessionProcessor` (peaks → «Я»/«Інші» → злиття → збереження → індексація), `PeaksBuilder`, `ClockAudioPlayer` для Linux.
- Storage: транскрипти зі словами (компактний бінарний кодек) + FTS5; 1 год транскрипту: збереження ~53–65 мс, читання ~5 мс.
- WASAPI-плеєр (AUTOCONVERTPCM, мікшер без алокацій, відновлення після втрати пристрою).
- UI: перегляд запису — плеєр із waveform, віртуалізований транскрипт, підсвітка поточного слова, клік по слову → перемотка.

Докази: workflow `asr` (run [39107747154](https://github.com/skyp1nus/Murmur/actions/runs/39107747154)) — реальні тести з моделлю пройшли на Windows і Linux; бенч 1 год (847 сегментів, 11 026 слів, Native AOT воркер):

| Машина | Транскрипція 1 год | × realtime | Пік RSS воркера | Бюджет |
| --- | --- | --- | --- | --- |
| ubuntu-latest, EPYC 7763, 4 vCPU | 196,8 с (+2,1 с завантаження) | 18,3× | 1052 МБ | 180 с ❌ |
| windows-latest, EPYC 7763, 4 vCPU | 227,2 с (+2,3 с) | 15,8× | 952 МБ | 180 с ❌ |

## Фаза 3 — AI-пошук

Зроблено:
- Storage: BM25 по FTS5 з безпечною санітизацією запиту; чанки з float32-векторами + SIMD brute-force top-k з кешем (10k × 768 → ~5 мс) (D-017).
- Intelligence: `llama-server` (llama.cpp b11716) як sidecar з Job object на Windows (D-016), `IChatClient`/`IEmbeddingGenerator` поверх OpenAI-сумісного HTTP (STJ source-gen), `LocalLlm` з лінивим стартом, вивантаженням за простоєм і забороною під час запису; `TranscriptChunker` (30–60 с); `Assistant`: гібридний пошук (RRF), RAG-промпт українською, цитати `[mm:ss]` → `Citation` на сесію й момент.
- Core: індексація після транскрипції, `ReindexAllAsync`, `RecordingActivity`; `scripts/fetch-models.(sh|ps1) -Ai` (llama.cpp + GGUF з Hugging Face).
- UI: вкладка «Запитати AI» (стрімінг, чипи таймкодів → плеєр), чат по всій бібліотеці, гібридний пошук у бібліотеці.
- Eval: `tools/Murmur.AiEval` + детермінована фікстура 60-хвилинного мітингу (12 фактів у відомих моментах, 2 питання без відповіді) + workflow `ai-eval` з реальною Gemma 4 E4B на CPU.

Доказ критерію: {AIEVAL_DETAILS}

## Заміри vs бюджети (PLAN.md)

| Метрика | Бюджет | Виміряно | Де | Статус |
| --- | --- | --- | --- | --- |
| Холодний старт (AOT) | < 400 мс | медіана 289 мс (фаза 0) / 328 мс (фаза 3), холодний 346–362 мс | windows-latest | ✅ |
| CPU у простої | < 1% | {IDLE} | {PERF_WHERE} | {IDLE_S} |
| CPU, запис лише аудіо | < 3% | {CPUA} | {PERF_WHERE} | {CPUA_S} |
| CPU, аудіо + екран 1080p30 | < 8% | {CPUB} | {PERF_WHERE} (екран 1024×768, WARP, CPU-readback) | {CPUB_S} |
| RAM під час запису, без моделей | < 150 МБ | {RAM} | {PERF_WHERE} | {RAM_S} |
| 1 год аудіо → текст на CPU | < 3 хв | 197 с / 227 с | ubuntu / windows-latest, 4 vCPU | ❌ на 4 vCPU |

Довідково (Linux, синтетичний захоплювач, програмний рендеринг Xvfb): `--autorecord` — CPU 3,9–4,6% машини (сфера малюється CPU), working set ~133 МБ, idle 0,005%.

## Відкриті ризики й BLOCKED

{RISKS}

## Чекліст для Windows (завтра)

{CHECKLIST}

## Як працювала команда

- ~120 запусків субагентів: на кожну задачу `coder`/`ui` → `reviewer` (до 3 ітерацій) → `tester`; інтеграція squash-мержем, одна задача = один коміт.
- Найкорисніше рев'ю/CI-знахідки: розкладка структур у біндингах sherpa-onnx, синхронний `FlushFileBuffers` у потоці запису, `E_ACCESSDENIED` на мікрофоні, WGC/MF на машині без GPU, гонки в `Recorder` при вимкненні екрана.
- Код: ~26,6 тис. рядків у `src/`, ~27,8 тис. рядків тестів; 1336 тестів (1318 пройшли, 18 — лише для Windows) на Linux.
