# CI

Workflow: `.github/workflows/ci.yml`. Запускається на push у `master` і `night-run`, на кожен pull request і вручну (`workflow_dispatch`). Нові запуски для тієї ж гілки скасовують попередні (`concurrency: ci-<ref>`). Права токена: лише `contents: read`. Окремий workflow `.github/workflows/ai-eval.yml` (AI-eval фази 3) описано в розділі «AI-eval» нижче.

SDK береться з `global.json` (prerelease `11.0.100-rc.2.26504.105`, `rollForward: latestFeature`). Одного `global-json-file` недостатньо: `setup-dotnet` обрізає версію до `major.minor` і ставить те, що зараз віддає канал `11.0`. Тому в усіх кроках `setup-dotnet` додатково вказано `dotnet-version: 11.0.100-rc.2.26504.105`, а наступний крок `Verify SDK matches global.json` валить джоб, якщо `dotnet --version` не дорівнює `sdk.version` з `global.json` (UNVERIFIED-ON-ACTIONS: поведінку `setup-dotnet` не перевірено запуском). При зміні SDK в `global.json` треба оновити `dotnet-version` у `ci.yml`. NuGet-кеш: `actions/cache@v4`, ключ від `Directory.Packages.props` і `**/*.csproj`.

## Джоби

| Джоб | Раннер | Що робить |
|---|---|---|
| `linux` | ubuntu-latest, 30 хв | `dotnet build`, `dotnet test`, Native AOT publish `Murmur.Desktop` (`net11.0`) і `Murmur.Transcription` для linux-x64. Джоб іде в `bash` з `pipefail` (`defaults.run.shell: bash`), тож невдалий publish за `| tee` валить крок. Якщо в логу publish є рядок `: warning ` або `: error ` (зокрема IL2xxx/IL3xxx; через `TreatWarningsAsErrors` попередження стають помилками) - крок червоний. |
| `windows` | windows-latest, 45 хв | `dotnet build`, `dotnet test`, Native AOT publish win-x64 (`Murmur.exe` і `Murmur.Transcription.exe` в одну теку), той самий gate на попередження й помилки, таблиця розмірів у Step Summary, артефакт `murmur-win-x64`. |
| `startup` | windows-latest, після `windows` | Завантажує `murmur-win-x64`, запускає `scripts/measure-startup.ps1` (7 запусків, бюджет 400 мс за медіаною). Падає, якщо медіана більша за бюджет або хоч один запуск не вдався. |
| `capture-e2e` | windows-latest, після `windows`, 30 хв | End-to-end запис на раннері без звукової карти: віртуальна карта VB-Audio Virtual Cable, зациклений тон 440 Гц, `Murmur.exe --autorecord 60 --screen`. Деталі - у розділі «Capture e2e» нижче. |
| `bench` | windows-latest, 40 хв | BenchmarkDotNet (`--job short`), усі `*-report-github.md` додаються до Step Summary. |

Тести йдуть через Microsoft.Testing.Platform (`global.json`), тому TRX вмикається прапорцем `--report-xunit-trx` (VSTest-прапорець `--logger trx` у цій конфігурації дає exit code 5).

## Workflow `asr`

Workflow: `.github/workflows/asr.yml` (окремо від `ci.yml`, щоб не уповільнювати pull request'и). Запускається на push у `master` і `night-run` та вручну (`workflow_dispatch`); `concurrency: asr-<ref>`, права `contents: read`. Той самий пін SDK і крок `Verify SDK matches global.json`, що в `ci.yml` (при зміні `global.json` оновити `dotnet-version` в обох файлах).

Один джоб `asr`, матриця `ubuntu-latest` (linux-x64) і `windows-latest` (win-x64), 45 хв, `fail-fast: false`, shell `bash`. 1-годинний бенчмарк запускається лише тут, по одному разу на ОС.

Кроки:

1. Кеш моделей (розділено на `actions/cache/restore@v4` перед скачуванням і `actions/cache/save@v4` одразу після нього з умовою `cache-hit != 'true'`, щоб кеш зберігався навіть коли тести чи бюджетний гейт падають; комбінований `actions/cache` зберігає лише при успішному джобі; тека `<workspace>/models`, ключ `models-<OS>-parakeet-tdt-0.6b-v3-int8-silero_vad-v1`; при зміні набору моделей у `fetch-models.*` підняти суфікс `v1`) і `scripts/fetch-models.sh` (Linux) або `scripts/fetch-models.ps1` (Windows) з `MURMUR_MODELS_DIR=<workspace>/models`. Скрипти ідемпотентні, тож при влучанні в кеш нічого не качається.
2. `dotnet test --project tests/Murmur.Transcription.Tests` і `tests/Murmur.Core.Tests` з `MURMUR_MODELS_DIR`. Тести на реальних моделях без моделей скіпаються; тут моделі є, тож вони виконуються.
3. Native AOT publish `Murmur.Transcription` для RID ОС із тим самим gate на `: warning ` / `: error `, що в `ci.yml`.
4. Бенч: `Murmur.Transcription --bench --model-dir <parakeet> --vad <silero_vad.onnx> --wav en.wav,de.wav,es.wav,fr.wav --seconds 3600 --out artifacts/perf/asr-bench.json` (чотири `test_wavs` з теки моделі зациклюються воркером до 3600 с аудіо).
5. `scripts/asr-bench-summary.sh <json> 180 <os>` (потрібен `jq`, є на обох образах раннерів) друкує таблицю (audio s, transcribe s, load, RTF, x-realtime, segments/words, threads/batch, CPU, OS, бюджет, PASS/FAIL) у Step Summary і `asr-bench.md`, і **валить джоб**, якщо `transcribeMs` > 180 с (бюджет PLAN.md: 1 год аудіо -> текст < 3 хв на CPU) або якщо аудіо коротше за 3600 с.

Артефакти (14 днів): `asr-bench-linux-x64`, `asr-bench-win-x64` (`asr-bench.json` + `asr-bench.md`), `asr-test-results-<rid>` (`.trx`).

Контракт воркера: `Murmur.Transcription --bench ... --out <json>` пише camelCase JSON (`audioSeconds`, `loadMs`, `transcribeMs`, `rtf`, `realtimeFactorX`, `segments`, `words`, `threads`, `batch`, `machine{os, cpu, cores}` та ін.). Цифри з хмарного раннера (2-4 vCPU) шумні: це верхня межа для порівняння між комітами.

UNVERIFIED-ON-ACTIONS: workflow не запускався на GitHub Actions (YAML розібрано `python yaml`, shell-скрипти пройшли `bash -n`, `asr-bench-summary.sh` перевірено локально на синтетичному JSON). Крок `Fetch models (Windows)` і поведінка Git Bash на `windows-latest` не перевірені.

## Артефакти (зберігаються 14 днів)

| Артефакт | Вміст |
|---|---|
| `test-results-linux`, `test-results-windows` | `.trx` звіти тестів |
| `murmur-win-x64` | Publish-тека win-x64 (`Murmur.exe`, `Murmur.Transcription.exe`, нативні бібліотеки) |
| `perf-startup` | `startup.json` - усі запуски, статистика, інформація про машину |
| `bench` | JSON і markdown звіти BenchmarkDotNet |
| `capture-e2e` | `rec60.json`, `check.md`, тека сесії (`session/`: `mic.wav`, `system.wav`, `screen-NNN.mp4`), TRX тестів `Murmur.Capture.Windows.Tests`, логи програвача тону |
| `perf-record` | (окремий workflow, 30 днів) `recA.json`, `recB.json`, `checkA.md`, `checkB.md`, логи програвача тону |
| `ai-eval-linux`, `ai-eval-windows` | `ai-eval.json`, `ai-eval.md`, лог `fetch-models.log` (workflow `ai-eval`) |

## Як завантажити `murmur-win-x64`

Через браузер: Actions -> потрібний запуск -> секція Artifacts внизу сторінки -> `murmur-win-x64` (zip).

Через CLI:

```powershell
gh run list --workflow ci --branch night-run --limit 5
gh run download <run-id> --name murmur-win-x64 --dir murmur-win-x64
.\murmur-win-x64\Murmur.exe
```

Артефакт не підписаний: Windows SmartScreen може попередити при першому запуску.

## Як читати таблицю старту

У Step Summary джоба `startup` (і в `startup.md`/`startup.json` артефакту `perf-startup`):

- `startupMs` - час усередині процесу: від старту процесу до першого відрисованого кадру (це значення пише сам застосунок).
- `wall ms` - зовнішній час: від запуску до виходу процесу (включає завершення, тому завжди більший).
- Run 1 - холодний запуск (перший після розпакування), показаний окремо. Статистика (median, p95, min, max) рахується по всіх успішних запусках.
- Бюджет: медіана `startupMs` <= 400 мс (PLAN.md, фаза 0). Результат PASS/FAIL - в останньому рядку першої таблиці.
- Хмарний раннер GitHub повільніший і шумніший за реальний ПК: цифри з CI - це верхня межа для порівняння між комітами, а не еталон.

Контракт із застосунком: `Murmur.exe --startup-probe <path>` після першого відрисованого кадру записує `{"startupMs": number, "utc": string}` у файл і завершується з кодом 0. Поки застосунок цього не реалізував, джоб `startup` червоний (запуск не пише файл).

## Capture e2e (`capture-e2e` у `ci.yml`)

Мета: прогнати справжній WASAPI loopback + WGC/Media Foundation на хмарному раннері, де немає ні мікрофона, ні динаміків.

Кроки джоба:

1. Завантажує `murmur-win-x64` з джоба `windows`.
2. `LABSN/sound-ci-helpers@v1` ставить віртуальну звукову карту VB-Audio Virtual Cable (VB-CABLE) і запускає службу Windows Audio. Крок `continue-on-error`: якщо він упав, у Step Summary з'являється рядок **no audio device**, а перевірки запису самі покажуть наслідки.
3. `scripts/ensure-media-foundation.ps1`: `Get-WindowsFeature Server-Media-Foundation`, якщо не встановлено - `Install-WindowsFeature`. Статус (`present`, `installed`, `installed-restart-needed`, `install-failed`, `unsupported-sku`) іде в Summary. Скрипт ніколи не валить джоб.
4. `scripts/start-tone.ps1` генерує тон (`scripts/gen-tone.ps1`: 16-bit, 48 кГц, стерео, 440 Гц, 10 с, амплітуда 0.5) і запускає в окремому прихованому процесі `pwsh` `System.Media.SoundPlayer.PlayLooping()` на пристрої за замовчуванням (VB-CABLE). Якщо `pwsh`-дитина помирає одразу - запасний варіант `powershell.exe` 5.1. PID пишеться в `artifacts/tone/tone.pid`, крок `Stop tone` (`if: always()`) його зупиняє.
   Плеєр стартує через `Start-Process ... -WindowStyle Hidden -PassThru` **без** `-RedirectStandard*`: перенаправлення вмикає `bInheritHandles=TRUE`, і прихований плеєр успадкував би pipe-и stdout/stderr кроку; після виходу кроку runner чекає ~5 с на закриття pipe-ів і вбиває все дерево процесів кроку (тон замовкав би через кілька секунд). Тому `play-tone.ps1` сам пише лог (`try/catch` + `Out-File`) у `artifacts/tone/play-tone-<host>.log`, а `start-tone.ps1` одразу кешує `$p.Handle`, щоб `HasExited`/`ExitCode` лишалися валідними. Перед записом (`Autorecord 60 s`; у perf-record - перед `Run A` і `Run B`) крок `Tone alive` запускає `scripts/check-tone.ps1`: читає `tone.pid`, `Get-Process -Id`, пише `tone: alive` / `tone: DEAD` у Step Summary і `::warning` для мертвого плеєра, щоб тишу в записі не плутали з причиною. Верифікація на Windows-runner-і: UNVERIFIED-ON-WINDOWS.
5. `dotnet test --project tests/Murmur.Capture.Windows.Tests -c Release`: тести з трейтом `Windows` виконуються по-справжньому, бо render-пристрій існує.
6. `scripts/run-autorecord.ps1` запускає `Murmur.exe --autorecord 60 --screen --perf-out artifacts/perf/rec60.json`, чекає на вихід (ліміт: тривалість + 120 с, потім kill) і перевіряє код виходу та наявність JSON.
7. `scripts/check-recording.ps1 -ExpectTone -ExpectScreen` перевіряє запис (див. нижче), таблиця йде в Step Summary; код 1 при порушеннях.
8. Артефакт `capture-e2e`.

Усі кроки після налаштування мають `if: ${{ !cancelled() }}`, тож збій тестів не ховає результат запису і навпаки.

### Контракт із застосунком

`Murmur.exe --autorecord <seconds> [--screen] --perf-out <json>` записує вказаний час, пише JSON і завершується з кодом 0:

```json
{ "durationS": 60.0, "idleCpuPct": 0.0, "cpuAvgPct": 0.0, "cpuP95Pct": 0.0, "cpuAvgPctOneCore": 0.0,
  "wsAvgMb": 0.0, "wsMaxMb": 0.0, "privateMaxMb": 0.0, "wsSlopeMbPerMin": 0.0,
  "memory": [ { "seconds": 0.0, "wsMb": 0.0, "privateMb": 0.0, "gcHeapMb": 0.0, "gcCommittedMb": 0.0, "gcAllocatedMb": 0.0, "gen0Collections": 0, "gen2Collections": 0 } ],
  "recorderStats": { "microphoneFrames": 0, "systemFrames": 0, "droppedFrames": 0, "screenFramesDropped": 0 },
  "session": { "id": "", "folder": "", "micSeconds": 0.0, "systemSeconds": 0.0, "screenSegments": 0 } }
```

`mic.wav` і `system.wav` у `session.folder` - 16 кГц mono PCM16, сегменти екрана - `screen-NNN.mp4`. Реалізація - окрема задача; поки її немає, `capture-e2e` червоний на кроці автозапису.

### Що перевіряє `check-recording.ps1`

| Перевірка | Умова |
|---|---|
| `recorderStats.droppedFrames` | `== 0` |
| `recorderStats.systemFrames` | `> 0` |
| `system.wav` | 16 кГц, mono, 16 біт; тривалість у межах +/- 1 с від `durationS` |
| `mic.wav` | якщо є дані: той самий формат і допуск за тривалістю (на CI мікрофона немає, порожній файл - лише INFO) |
| Тиша (`-ExpectTone`) | жодної серії нульових семплів (`abs <= 1`) довше 100 мс у `system.wav`; перші 5 с і останні 2 с ігноруються (старт тону й запису не синхронні) |
| Тон (`-ExpectTone`) | не менше 95% односекундних вікон містять 440 Гц (відношення енергії Goertzel >= 0.5), пік сигналу > 0 |
| Екран (`-ExpectScreen`) | є хоча б один непорожній `screen-NNN.mp4`, `session.screenSegments` дорівнює кількості файлів |
| Відео екрана (`-ExpectScreen`) | кожен сегмент розбирається як MP4 з відеосемплами; у звіті кадри, тривалість і fps сегмента; fps не нижче `-MinScreenFps` (0.5; `0` вимикає). Частота кадрів змінна: на статичному екрані рекордер раз на секунду повторює останній кадр (~1 fps), рухомий вміст іде до 60 fps на GPU-шляху і до 30 fps у CPU readback (WARP на раннері; програмний енкодер сам доповнює статичні проміжки до 30 fps). Сума тривалостей сегментів у межах +/- `-ScreenToleranceS` (2 с) від `durationS`: файл із 12 кадрів (0.4 с) для 10-хвилинного запису тепер дає FAIL |
| Бюджети (опційно) | `-IdleCpuBudgetPct` для `idleCpuPct`, `-CpuBudgetPct` для `cpuAvgPct` (відсоток від усієї машини), `-RamBudgetMb` для `wsMaxMb`; `0` вимикає перевірку |
| Ріст памʼяті (опційно) | `-MaxWsSlopeMbPerMin`: нахил working set (МБ/хв, метод найменших квадратів по `memory[].seconds` / `memory[].wsMb`) лише по вибірках від `-WsSlopeSkipS` (60 с) - перша хвилина є прогрівом (JIT, Media Foundation, енкодер, пули семплів) і в підгонку не входить; `wsSlopeMbPerMin` самого застосунку рахується з початку запису, тому лише показується (INFO). Менше 3 вибірок після прогріву - FAIL (нема по чому рахувати); `0` вимикає перевірку |

Тривалість відео - це сума тривалостей семплів відеотреку з `moof/traf/trun` (з типовими значеннями з `tfhd`/`trex`), поділена на timescale з `mdhd`; для нефрагментованого MP4 - таблиця `stts`; без семплів береться `mehd`/`mvhd`, але тоді кількість кадрів невідома і сегмент отримує FAIL. Файл, обірваний збоєм, читається до останнього повного боксу (у звіті `file truncated`). `screen-NNN.mp4` читає `scripts/MurmurMp4Scan.cs` (його ж `Add-Type -Path` компілює скрипт); JSON сесії не містить зсувів сегментів, тому порівнюється сума, а `--autorecord` не перемикає екран, отже сегмент покриває весь запис мінус затримка старту. Юніт-тести парсера і скрипту - `tests/Murmur.Scripts.Tests` (тести скрипту потребують `pwsh`, інакше скіпаються).

WAV читається вбудованим C#-хелпером (`Add-Type`), тому години запису перевіряються за секунди. Заголовок із нульовим або завеликим розміром `data` (недофіналізований файл після збою) читається до кінця файлу, це лише INFO.

## Perf record (`.github/workflows/perf-record.yml`)

Ручний запуск (`workflow_dispatch`) довгого запису для фази 1 («1 год запису без втрат, CPU у межах бюджету»). Входи:

| Вхід | За замовчуванням | Значення |
|---|---|---|
| `minutes` | 60 | Тривалість запуску A (лише аудіо), 1-85 |
| `screen` | true | Додатково запуск B: аудіо + екран, 10 хв |

Workflow сам збирає Native AOT `Murmur.Desktop` win-x64 (без `Murmur.Transcription`, воркер для запису не потрібен), потім те саме налаштування, що й `capture-e2e` (VB-CABLE, Media Foundation, зациклений тон), і два запуски:

| Запуск | Команда | Бюджети (docs/PLAN.md) |
|---|---|---|
| A | `--autorecord <minutes*60>` | CPU у простої < 1%, CPU запису < 3%, RAM < 150 МБ, ріст памʼяті після першої хвилини < 1 МБ/хв |
| B | `--autorecord 600 --screen` | CPU у простої < 1%, CPU запису < 8%, ріст памʼяті після першої хвилини < 1 МБ/хв (`-MaxWsSlopeMbPerMin 1`); абсолютного ліміту RAM для запису з екраном немає (рішення власника 2026-10-19: якість важливіша, памʼяті - скільки треба), `wsMaxMb` лише в звіті |

Обидва запуски перевіряє `check-recording.ps1` (без втрат: тривалість `system.wav` +/- 1 с від `durationS`, жодних нульових серій > 100 мс поки грає тон, `droppedFrames == 0`). Збій запуску A не скасовує B. У Step Summary: таблиці перевірок і зведена таблиця «метрика / бюджет / A / B». `timeout-minutes: 120` (збірка ~15 хв + A + 10 хв B). Артефакт `perf-record` (JSON і markdown, без великих WAV).

Хмарний раннер (4 vCPU, без GPU) повільніший і шумніший за реальний ПК: промах бюджету - сигнал до уваги, а не вирок. Для перевірки на реальному залізі запускайте `Murmur.exe --autorecord` локально.

Підтверджено запусками на `windows-latest` (2026-10-18): `LABSN/sound-ci-helpers@v1` ставить VB-Audio Virtual Cable, і він стає пристроєм за замовчуванням (loopback записує тон 440 Гц); `Server-Media-Foundation` відсутній в образі й ставиться кроком за ~1 хв; WGC віддає кадри без фізичного дисплея (WARP, тому CPU-readback, D-027: 1080p30, VBR 6 Мбіт/с; на ПК з апаратним H.264-енкодером - 1080p60, VBR 12 Мбіт/с, D-030); `SoundPlayer` у `pwsh` 7 грає тон увесь запуск. Мікрофон (`CABLE Output`) заборонений налаштуваннями конфіденційності раннера → `E_ACCESSDENIED`, запис іде без мікрофона (D-023).

## Models (`.github/workflows/models.yml`)

Реальні завантаження каталогу моделей (`src/Murmur.Models`, фаза 4 «моделі завантажуються при першому запуску») з Hugging Face і GitHub тим самим рушієм, що й застосунок. Юніт-тести `Murmur.Models.Tests` ходять лише до локального фейкового HTTP-сервера (вони в `ci.yml`), тож це єдине місце, де перевіряються справжні URL. Запускається на pull request, що чіпає `src/Murmur.Models/**`, `tools/Murmur.ModelFetch/**` або сам workflow, і вручну (`workflow_dispatch`); `concurrency: models-<ref>`, права `contents: read`, `ubuntu-latest`, 45 хв. Той самий пін SDK і крок `Verify SDK matches global.json`, що в `ci.yml`.

Кроки:

1. `dotnet build tools/Murmur.ModelFetch -c Release`.
2. Встановлення `silero-vad,parakeet-tdt-0.6b-v3-int8,llama-cpp-b11716,embeddinggemma-300m-q8` у `$MURMUR_MODELS_DIR` (`<workspace>/models`, без кешу: мета - реальне завантаження). `Murmur.ModelFetch --dir <тека> --ids ... --summary $GITHUB_STEP_SUMMARY` друкує для кожного файла розмір, SHA-256 і час; таблиця йде в Step Summary. Саме ці SHA-256 архітектор пізніше вписує в каталог (`ModelFileSpec.Sha256`), зараз вони `null`.
3. `--head-only gemma-4-e4b-q4,gemma-4-26b-a4b-q4`: лише перевірка, що URL відповідають (запит `Range: bytes=0-0`), і `Content-Length` (5 і 17 ГБ не качаємо). Крок виконується навіть після збою кроку 2. Ім'я файла 26B у каталозі - припущення, цей крок його перевіряє; червоний крок означає, що репозиторій або файл названо інакше.
4. Перевірка розкладки, яку читає застосунок (`ModelLocator`, `AiModelPaths`): чотири файли Parakeet, `silero_vad.onnx`, `llama.cpp/llama-server` (виконуваний, `--version` запускається), `gguf/embeddinggemma-300M-Q8_0.gguf`, `gguf/models.json`; тека `.staging` після встановлення порожня й видалена.
5. Повторний запуск встановлення нічого не качає (4 рядки `already installed, nothing downloaded`).

Тест ASR-воркера на справжній моделі до workflow не додано: `RealModelTests` беруть тестові WAV із `<parakeet>/test_wavs/`, а каталог ставить лише чотири файли моделі (без `test_wavs`), тож це не однорядкова команда.

Локально: `dotnet run -c Release --project tools/Murmur.ModelFetch -- --dir ./models --ids silero-vad,llama-cpp-b11716`. Код виходу 0 - усе гаразд, 1 - предмет не встановився або URL недоступний, 2 - помилка аргументів. UNVERIFIED-ON-ACTIONS: workflow не запускався на GitHub Actions (YAML розібрано `python yaml`); завантаження `silero-vad` і `llama-cpp-b11716` (tar.gz) із GitHub перевірено вручну на Linux-середовищі розробки, Hugging Face там недоступний. Windows-збірка llama.cpp (zip) перевірена лише на синтетичному архіві (UNVERIFIED-ON-WINDOWS).

## AI-eval: «відповідь AI веде на правильну хвилину» (`.github/workflows/ai-eval.yml`)

Окремий workflow (не `ci.yml`), критерій фази 3. Запускається на push у `master` і `night-run` та вручну (`workflow_dispatch`); `concurrency: ai-eval-<ref>`.

| Джоб | Раннер | Що робить |
|---|---|---|
| `build` | ubuntu-latest, 20 хв | `dotnet build Murmur.sln`, тести `tools/Murmur.AiEval/tests`, потім eval у режимі `--retrieval-only` без жодної моделі (BM25 за ключовими словами питань). Таблиця - у Step Summary. |
| `ai-eval` | ubuntu-latest (4 vCPU, 16 GB), 60 хв, після `build` | `scripts/fetch-models.sh --ai` (llama.cpp `llama-server` CPU + Gemma 4 E4B Q4_K_M + EmbeddingGemma 300M Q8_0), експорт `MURMUR_LLM_MODEL` / `MURMUR_EMBED_MODEL` / `MURMUR_LLAMA_SERVER` з виводу скрипта, повний eval з `--threads $(nproc)`. Таблиця - у Step Summary, JSON і markdown - артефакт `ai-eval-linux`. Джоб червоний, якщо частка пройдених питань < 0.8 (exit code 1) або моделей немає (exit code 2). |
| `ai-eval-windows` | windows-latest, 60 хв | Лише вручну: `workflow_dispatch` з `windows: true`. Те саме через Git Bash і win-cpu-x64 збірку llama.cpp (UNVERIFIED-ON-WINDOWS: не запускався). |

Кеш моделей: `actions/cache/restore@v4` (id `models-cache`) перед `Fetch models` і `actions/cache/save@v4` одразу після нього (`if: always() && steps.fetch-models.outcome == 'success' && steps.models-cache.outputs.cache-hit != 'true'`). Звичайний `actions/cache@v4` зберігає кеш лише в post-кроці успішного джоба, тож кожен червоний eval (поріг, падіння, таймаут) викидав би ~6 ГБ завантажень. Зберігаємо тільки після успішного fetch, щоб не закешувати неповний набір. Шляхи `~/murmur-models/llama.cpp` і `~/murmur-models/gguf`, ключ `ai-models-<os>-<hash fetch-models.sh>` (зміна тегу llama.cpp, репозиторіїв або квантизації в скрипті = новий кеш). `models.json` зберігає абсолютні шляхи, тому тека моделей завжди `~/murmur-models`. Parakeet і Silero скрипт тягне щоразу (GitHub Releases, не кешуються). Для gated-репозиторіїв Hugging Face можна додати секрет `HF_TOKEN` (необов'язковий).

### Фікстура і питання

- `tests/fixtures/meeting-60min.json` - синтетичний 60-хвилинний мітинг українською з англійськими термінами: 651 сегмент по 3-15 с, спікери `Me`/`Others`, таймінги слів рівномірно в межах сегмента. 12 фактів закладено в точно відомі моменти (03:15 ... 57:40); їхні ключові основи слів не трапляються більше ніде (перевіряє тест).
- `tests/fixtures/meeting-60min.questions.json` - 12 перефразованих питань (10 з відповіддю, допуск ±60 с; 2 без відповіді в записі; одне англійською). Поле `keywords` - те, що користувач ввів би в пошук; воно потрібне лише для keyword-режиму. Поле `factStems` - основи факту, одна з яких має бути у відповіді, якщо збіглася лише запасна цитата (див. нижче).
- Обидва файли генерує детермінований `FixtureGenerator` (власний SplitMix64, без LLM). Тест `CommittedFiles_MatchTheGenerator` падає, якщо файли розійшлися з генератором. Перегенерувати:

```bash
dotnet run -c Release --project tools/Murmur.AiEval/src -- --generate-fixture tests/fixtures
```

### Як рахується результат

- **Повний режим** (є chat-модель, embedding-модель і `llama-server`): тимчасовий `SqliteStorage`, сесія, `SaveTranscriptAsync`, `Assistant.IndexSessionAsync`, далі `AskAsync` для кожного питання (scope = ця сесія). Питання з відповіддю пройдене, якщо (а) модель сама написала таймкод у межах ±60 с від факту, або (б) у межах допуску лише запасна цитата топ-уривка (її `Assistant` додає, коли модель не написала таймкод; саме цей чип користувач і натисне) і відповідь справді передає факт: вона непорожня, не каже «не знайдено» і містить хоч одну з основ `factStems` (основи факту з `FixtureContent`, плюс цифри на кшталт `500`, `1,2`, `15%`; жодна не трапляється в тексті питання, перевіряє тест). Без умови (б) gate не залежав би від chat-моделі: запасна цитата потрапляє на факт щоразу, коли його знайшов пошук, і порожні відповіді давали б 10/12. Звіт показує `explicitPassed` (скільки разів правильний таймкод написала сама модель) і `conveysFact` для кожного питання. Питання без відповіді пройдене, якщо відповідь каже «в записі цього немає» (прості ключові слова UA/EN) і модель не написала жодного власного таймкоду.
- **`--retrieval-only`**: chat-модель не потрібна. З embedding-моделлю - гібридний `SearchAsync` із текстом питання, факт має бути в топ-3 (питання без відповіді пропускаються: векторний пошук завжди щось повертає). Без моделей - BM25 з `keywords` питання; для питань без відповіді не має бути жодного збігу.
- Exit code: 0 - частка ≥ порогу (`--threshold`, типово 0.8), 1 - нижче, 2 - помилка запуску чи відсутні моделі.
- JSON (`--out`): `mode`, `passed`, `total`, `rate`, `explicitPassed`, `models` (імена й розміри файлів), `timings` (`saveMs`, `indexMs`, `chatLoadMs`, `avgAnswerMs`, `maxAnswerMs`, `totalMs`), `machine`, `perQuestion[]` (очікуваний час, цитати, pass, latency, time-to-first-token, відповідь).

### Локальний запуск eval

```bash
# Без моделей: перевірка «труб» (keyword-only), < 2 с
dotnet run -c Release --project tools/Murmur.AiEval/src -- \
  --fixture tests/fixtures/meeting-60min.json --questions tests/fixtures/meeting-60min.questions.json \
  --out artifacts/ai-eval/retrieval.json --retrieval-only

# З моделями (після scripts/fetch-models.sh --ai і export рядків, які він друкує)
dotnet run -c Release --project tools/Murmur.AiEval/src -- \
  --fixture tests/fixtures/meeting-60min.json --questions tests/fixtures/meeting-60min.questions.json \
  --out artifacts/ai-eval/ai-eval.json --markdown artifacts/ai-eval/ai-eval.md --threads "$(nproc)"
```

`--data-dir <dir>` залишає базу після запуску (для розбору); без нього тимчасова тека видаляється.

## Локальний запуск скриптів

```powershell
# Вимірювання старту (PowerShell 7)
pwsh scripts/measure-startup.ps1 -Exe artifacts/publish/win-x64/Murmur.exe -Runs 7 -BudgetMs 400

# Тон для ручної перевірки (440 Гц, 48 кГц, стерео, 10 с) і його зациклене відтворення (Windows)
pwsh scripts/gen-tone.ps1 -Out artifacts/tone/tone-440hz.wav
pwsh scripts/start-tone.ps1 -DurationS 900

# Запис + перевірка (Windows, потрібен render-пристрій)
pwsh scripts/run-autorecord.ps1 -Exe artifacts/publish/win-x64/Murmur.exe -Seconds 60 -Screen -PerfOut artifacts/perf/rec60.json
pwsh scripts/check-recording.ps1 -PerfJson artifacts/perf/rec60.json -ExpectTone -ExpectScreen

# Моделі (Parakeet TDT v3 int8 + Silero VAD); тека за замовчуванням: %LOCALAPPDATA%\Murmur\models
pwsh scripts/fetch-models.ps1
```

```bash
# Linux/macOS; тека за замовчуванням ~/.murmur/models, змінюється через MURMUR_MODELS_DIR
scripts/fetch-models.sh

# Таблиця й перевірка бюджету за JSON бенчу воркера (потрібен jq); код виходу 1, якщо > 180 с
scripts/asr-bench-summary.sh artifacts/perf/asr-bench.json 180 local
```

Скрипти завантаження моделей ідемпотентні (пропускають уже наявні файли), роблять кілька спроб із затримкою та завершуються з помилкою, якщо файл не вдалося отримати.
