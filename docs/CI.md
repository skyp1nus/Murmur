# CI

Workflow: `.github/workflows/ci.yml`. Запускається на push у `master` і `night-run`, на кожен pull request і вручну (`workflow_dispatch`). Нові запуски для тієї ж гілки скасовують попередні (`concurrency: ci-<ref>`). Права токена: лише `contents: read`.

SDK береться з `global.json` (prerelease `11.0.100-rc.2.26504.105`, `rollForward: latestFeature`). Одного `global-json-file` недостатньо: `setup-dotnet` обрізає версію до `major.minor` і ставить те, що зараз віддає канал `11.0`. Тому в усіх трьох кроках `setup-dotnet` додатково вказано `dotnet-version: 11.0.100-rc.2.26504.105`, а наступний крок `Verify SDK matches global.json` валить джоб, якщо `dotnet --version` не дорівнює `sdk.version` з `global.json` (UNVERIFIED-ON-ACTIONS: поведінку `setup-dotnet` не перевірено запуском). При зміні SDK в `global.json` треба оновити `dotnet-version` у `ci.yml`. NuGet-кеш: `actions/cache@v4`, ключ від `Directory.Packages.props` і `**/*.csproj`.

## Джоби

| Джоб | Раннер | Що робить |
|---|---|---|
| `linux` | ubuntu-latest, 30 хв | `dotnet build`, `dotnet test`, Native AOT publish `Murmur.Desktop` (`net11.0`) і `Murmur.Transcription` для linux-x64. Джоб іде в `bash` з `pipefail` (`defaults.run.shell: bash`), тож невдалий publish за `| tee` валить крок. Якщо в логу publish є рядок `: warning ` або `: error ` (зокрема IL2xxx/IL3xxx; через `TreatWarningsAsErrors` попередження стають помилками) - крок червоний. |
| `windows` | windows-latest, 45 хв | `dotnet build`, `dotnet test`, Native AOT publish win-x64 (`Murmur.exe` і `Murmur.Transcription.exe` в одну теку), той самий gate на попередження й помилки, таблиця розмірів у Step Summary, артефакт `murmur-win-x64`. |
| `startup` | windows-latest, після `windows` | Завантажує `murmur-win-x64`, запускає `scripts/measure-startup.ps1` (7 запусків, бюджет 400 мс за медіаною). Падає, якщо медіана більша за бюджет або хоч один запуск не вдався. |
| `bench` | windows-latest, 40 хв | BenchmarkDotNet (`--job short`), усі `*-report-github.md` додаються до Step Summary. |

Тести йдуть через Microsoft.Testing.Platform (`global.json`), тому TRX вмикається прапорцем `--report-xunit-trx` (VSTest-прапорець `--logger trx` у цій конфігурації дає exit code 5).

## Артефакти (зберігаються 14 днів)

| Артефакт | Вміст |
|---|---|
| `test-results-linux`, `test-results-windows` | `.trx` звіти тестів |
| `murmur-win-x64` | Publish-тека win-x64 (`Murmur.exe`, `Murmur.Transcription.exe`, нативні бібліотеки) |
| `perf-startup` | `startup.json` - усі запуски, статистика, інформація про машину |
| `bench` | JSON і markdown звіти BenchmarkDotNet |

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

## Локальний запуск скриптів

```powershell
# Вимірювання старту (PowerShell 7)
pwsh scripts/measure-startup.ps1 -Exe artifacts/publish/win-x64/Murmur.exe -Runs 7 -BudgetMs 400

# Моделі (Parakeet TDT v3 int8 + Silero VAD); тека за замовчуванням: %LOCALAPPDATA%\Murmur\models
pwsh scripts/fetch-models.ps1
```

```bash
# Linux/macOS; тека за замовчуванням ~/.murmur/models, змінюється через MURMUR_MODELS_DIR
scripts/fetch-models.sh
```

Скрипти завантаження моделей ідемпотентні (пропускають уже наявні файли), роблять кілька спроб із затримкою та завершуються з помилкою, якщо файл не вдалося отримати.
