# Murmur — правила проєкту

Локальний рекордер мітингів: звук + екран, транскрипція Parakeet, AI-пошук через Gemma. Windows — MVP, macOS — фаза 5.

## Головні документи
- `docs/PLAN.md` — цілі, стек, архітектура, бюджети, фази з критеріями. Джерело правди.
- `docs/design/` — макети та `tokens.json`.
- `docs/BACKLOG.md` — як ведуться ідеї: GitHub Issues (мітка `idea`) і GitHub Project.
- `.claude/agents/` — команда субагентів; `.claude/commands/phase.md` — цикл фази.

## Стек
.NET 11 (C# 15), Native AOT, Avalonia + CommunityToolkit.Mvvm, sherpa-onnx + Parakeet TDT v3, LLamaSharp + Gemma 4, SQLite + FTS5 + sqlite-vec, Microsoft.Extensions.AI.

## Жорсткі правила
- **Native AOT завжди.** Жодної рефлексії, `dynamic`, `Activator.CreateInstance`, runtime-кодогенерації. JSON — `System.Text.Json` source-gen, MVVM і DI — source-gen. Збірка з trim-попередженнями = червона.
- **Гарячі шляхи без алокацій:** аудіо/відео-callback'и — тільки `Span<T>`, `ArrayPool`, lock-free буфери. Жодних LINQ, `async`, логування з інтерполяцією всередині callback'ів.
- **Межі модулів:** `Murmur.App` (бібліотека UI) залежить лише від `Murmur.Core`; реалізації збирає composition root `Murmur.Desktop` (exe). Платформний код — тільки в `Murmur.Capture.*`. Інтерфейси в `Murmur.Core` змінює лише архітектор.
- **Моделі (ASR, LLM)** працюють в окремому процесі-воркері, вантажаться ліниво, ніколи не тримаються в памʼяті під час запису.
- **Нова залежність** — тільки після перевірки AOT-сумісності, ліцензії й погодження власника.
- **Дизайн** — тільки токени з `docs/design/tokens.json`. Violet/Ember — лише для «сфери» запису та іконок, ніколи для кнопок і тексту.
- **Тести** до кожної публічної поведінки; бенчмарк до кожного гарячого шляху.
- **Комміти** — Conventional Commits (`feat(capture): ...`), одна задача = одна гілка = один PR.
- **Гілки (repo standard v1):** `dev` — default і інтеграція: гілка задачі `<issue>-<slug>` (без issue — `<type>/<slug>`) від `dev`, PR у `dev` із conventional-заголовком і `Closes #<n>`, лише squash, коли `ci` зелений. `main` — релізи: лише release PR `dev -> main` (відкриває власник), лише merge commit; `main` ніколи не мерджиться назад у `dev`. Прямих push у `dev`/`main` немає. Шаблон PR — `.github/pull_request_template.md`.
- **Без трейлерів у коммітах:** жодних `Co-Authored-By:` і `Claude-Session:` рядків у повідомленнях коммітів; в описах PR — без посилань на сесію Claude.

## Команди
```bash
dotnet build Murmur.sln -c Release
dotnet test Murmur.sln -c Release
dotnet publish src/Murmur.Desktop -c Release -f net11.0-windows10.0.22621.0 -r win-x64   # Native AOT, exe = Murmur.exe
dotnet run -c Release --project tests/Murmur.Benchmarks
```

## Перевірка на Windows
- Щоденна перевірка Windows-поведінки (WASAPI, WGC/MF, AOT win-x64, старт) — на ПК власника: `pwsh scripts/verify-local.ps1` (за замовчуванням build + тести + AOT win-x64 + старт; `-Capture`, `-Perf`, `-Asr`, `-AiEval`, `-All`, `-Tone`). Звіт — `artifacts/local-verify/<час>/report.md`, його додають у PR.
- GitHub Actions — мінімально: на кожен PR (будь-яка база) лише Linux-джоб `ci.yml` і gate `ci` (єдиний required check: перевіряє політику `dev`/`main` і падає на неочікувано пропущеному джобі; зміна бази PR перезапускає CI через `ci-retarget.yml`). На PR у `dev` Linux-джоб швидкий, ≈3 хв: збірка з trim/AOT-аналізаторами + швидкі тести (`scripts/ci/test-fast.sh`), без Native AOT publish, AOT-смоуку, NuGet-кешу, без `Murmur.Scripts.Tests` і без тестів із трейтом `Category=Slow` (headless-вікна, скріншоти, довгі, зокрема `OneHourNoLossTests`) чи `DesignParity`; логічні тести тих самих класів лишаються. На release PR `dev -> main` і вручну він повний (усі тести + AOT publish + смоук; gate вимагає повного режиму для бази `main`). Windows-джоби `ci.yml` (тести, AOT, `startup`, `installer`, `capture-e2e`) і `bench` — лише на release PR `dev -> main` або вручну; `asr.yml`, `ai-eval.yml`, `perf-record.yml`, `models.yml` — лише вручну (`workflow_dispatch`). Раннер без GPU (WARP) і з забороненим мікрофоном — не плутати це з багами продукту.
- Теги `phase-N-done` — рядком у `.github/phase-tags.txt` (створює `tags.yml`).

## Середовище
Код захоплення (WASAPI, WGC, Media Foundation) і AOT-збірку win-x64 можна запускати й перевіряти лише на Windows. На Linux — тільки Core, Storage, Intelligence і тести без платформного коду.

## Мова
Документи й звіти — українською, код, коментарі й комміти — англійською.
