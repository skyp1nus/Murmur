# CI

Workflow: `.github/workflows/ci.yml`. Запускається на push у `master` і `night-run`, на кожен pull request і вручну (`workflow_dispatch`). Нові запуски для тієї ж гілки скасовують попередні (`concurrency: ci-<ref>`). Права токена: лише `contents: read`.

SDK береться з `global.json` (prerelease `11.0.100-rc.2.26504.105`, `rollForward: latestFeature`). Одного `global-json-file` недостатньо: `setup-dotnet` обрізає версію до `major.minor` і ставить те, що зараз віддає канал `11.0`. Тому в усіх кроках `setup-dotnet` додатково вказано `dotnet-version: 11.0.100-rc.2.26504.105`, а наступний крок `Verify SDK matches global.json` валить джоб, якщо `dotnet --version` не дорівнює `sdk.version` з `global.json` (UNVERIFIED-ON-ACTIONS: поведінку `setup-dotnet` не перевірено запуском). При зміні SDK в `global.json` треба оновити `dotnet-version` у `ci.yml`. NuGet-кеш: `actions/cache@v4`, ключ від `Directory.Packages.props` і `**/*.csproj`.

## Джоби

| Джоб | Раннер | Що робить |
|---|---|---|
| `linux` | ubuntu-latest, 30 хв | `dotnet build`, `dotnet test`, Native AOT publish `Murmur.Desktop` (`net11.0`) і `Murmur.Transcription` для linux-x64. Джоб іде в `bash` з `pipefail` (`defaults.run.shell: bash`), тож невдалий publish за `| tee` валить крок. Якщо в логу publish є рядок `: warning ` або `: error ` (зокрема IL2xxx/IL3xxx; через `TreatWarningsAsErrors` попередження стають помилками) - крок червоний. |
| `windows` | windows-latest, 45 хв | `dotnet build`, `dotnet test`, Native AOT publish win-x64 (`Murmur.exe` і `Murmur.Transcription.exe` в одну теку), той самий gate на попередження й помилки, таблиця розмірів у Step Summary, артефакт `murmur-win-x64`. |
| `startup` | windows-latest, після `windows` | Завантажує `murmur-win-x64`, запускає `scripts/measure-startup.ps1` (7 запусків, бюджет 400 мс за медіаною). Падає, якщо медіана більша за бюджет або хоч один запуск не вдався. |
| `capture-e2e` | windows-latest, після `windows`, 30 хв | End-to-end запис на раннері без звукової карти: віртуальна карта Scream, зациклений тон 440 Гц, `Murmur.exe --autorecord 60 --screen`. Деталі - у розділі «Capture e2e» нижче. |
| `bench` | windows-latest, 40 хв | BenchmarkDotNet (`--job short`), усі `*-report-github.md` додаються до Step Summary. |

Тести йдуть через Microsoft.Testing.Platform (`global.json`), тому TRX вмикається прапорцем `--report-xunit-trx` (VSTest-прапорець `--logger trx` у цій конфігурації дає exit code 5).

## Артефакти (зберігаються 14 днів)

| Артефакт | Вміст |
|---|---|
| `test-results-linux`, `test-results-windows` | `.trx` звіти тестів |
| `murmur-win-x64` | Publish-тека win-x64 (`Murmur.exe`, `Murmur.Transcription.exe`, нативні бібліотеки) |
| `perf-startup` | `startup.json` - усі запуски, статистика, інформація про машину |
| `bench` | JSON і markdown звіти BenchmarkDotNet |
| `capture-e2e` | `rec60.json`, `check.md`, тека сесії (`session/`: `mic.wav`, `system.wav`, `screen-NNN.mp4`), TRX тестів `Murmur.Capture.Windows.Tests`, логи програвача тону |
| `perf-record` | (окремий workflow, 30 днів) `recA.json`, `recB.json`, `checkA.md`, `checkB.md`, логи програвача тону |

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
2. `LABSN/sound-ci-helpers@v1` ставить віртуальну звукову карту Scream і запускає службу Windows Audio. Крок `continue-on-error`: якщо він упав, у Step Summary з'являється рядок **no audio device**, а перевірки запису самі покажуть наслідки.
3. `scripts/ensure-media-foundation.ps1`: `Get-WindowsFeature Server-Media-Foundation`, якщо не встановлено - `Install-WindowsFeature`. Статус (`present`, `installed`, `installed-restart-needed`, `install-failed`, `unsupported-sku`) іде в Summary. Скрипт ніколи не валить джоб.
4. `scripts/start-tone.ps1` генерує тон (`scripts/gen-tone.ps1`: 16-bit, 48 кГц, стерео, 440 Гц, 10 с, амплітуда 0.5) і запускає в окремому прихованому процесі `pwsh` `System.Media.SoundPlayer.PlayLooping()` на пристрої за замовчуванням (Scream). Якщо `pwsh`-дитина помирає одразу - запасний варіант `powershell.exe` 5.1. PID пишеться в `artifacts/tone/tone.pid`, крок `Stop tone` (`if: always()`) його зупиняє.
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
  "wsAvgMb": 0.0, "wsMaxMb": 0.0, "privateMaxMb": 0.0,
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
| Бюджети (опційно) | `-IdleCpuBudgetPct` для `idleCpuPct`, `-CpuBudgetPct` для `cpuAvgPct` (відсоток від усієї машини), `-RamBudgetMb` для `wsMaxMb`; `0` вимикає перевірку |

WAV читається вбудованим C#-хелпером (`Add-Type`), тому години запису перевіряються за секунди. Заголовок із нульовим або завеликим розміром `data` (недофіналізований файл після збою) читається до кінця файлу, це лише INFO.

## Perf record (`.github/workflows/perf-record.yml`)

Ручний запуск (`workflow_dispatch`) довгого запису для фази 1 («1 год запису без втрат, CPU у межах бюджету»). Входи:

| Вхід | За замовчуванням | Значення |
|---|---|---|
| `minutes` | 60 | Тривалість запуску A (лише аудіо), 1-85 |
| `screen` | true | Додатково запуск B: аудіо + екран, 10 хв |

Workflow сам збирає Native AOT `Murmur.Desktop` win-x64 (без `Murmur.Transcription`, воркер для запису не потрібен), потім те саме налаштування, що й `capture-e2e` (Scream, Media Foundation, зациклений тон), і два запуски:

| Запуск | Команда | Бюджети (docs/PLAN.md) |
|---|---|---|
| A | `--autorecord <minutes*60>` | CPU у простої < 1%, CPU запису < 3%, RAM < 150 МБ |
| B | `--autorecord 600 --screen` | CPU у простої < 1%, CPU запису < 8%, RAM < 150 МБ |

Обидва запуски перевіряє `check-recording.ps1` (без втрат: тривалість `system.wav` +/- 1 с від `durationS`, жодних нульових серій > 100 мс поки грає тон, `droppedFrames == 0`). Збій запуску A не скасовує B. У Step Summary: таблиці перевірок і зведена таблиця «метрика / бюджет / A / B». `timeout-minutes: 120` (збірка ~15 хв + A + 10 хв B). Артефакт `perf-record` (JSON і markdown, без великих WAV).

Хмарний раннер (4 vCPU, без GPU) повільніший і шумніший за реальний ПК: промах бюджету - сигнал до уваги, а не вирок. Для перевірки на реальному залізі запускайте `Murmur.exe --autorecord` локально.

Непідтверджено запуском (UNVERIFIED-ON-ACTIONS): поведінка `LABSN/sound-ci-helpers@v1` на `windows-latest` (чи стає Scream пристроєм за замовчуванням), доступність `Server-Media-Foundation`, робота WGC на раннері без фізичного дисплея, `SoundPlayer` у `pwsh` 7.

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
```

Скрипти завантаження моделей ідемпотентні (пропускають уже наявні файли), роблять кілька спроб із затримкою та завершуються з помилкою, якщо файл не вдалося отримати.
