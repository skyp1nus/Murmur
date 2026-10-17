# Murmur — правила проєкту

Локальний рекордер мітингів: звук + екран, транскрипція Parakeet, AI-пошук через Gemma. Windows — MVP, macOS — фаза 5.

## Головні документи
- `docs/PLAN.md` — цілі, стек, архітектура, бюджети, фази з критеріями. Джерело правди.
- `docs/design/` — макети та `tokens.json`.
- `docs/BACKLOG.md` — ідеї поза поточною фазою.
- `.claude/agents/` — команда субагентів; `.claude/commands/phase.md` — цикл фази.

## Стек
.NET 11 (C# 15), Native AOT, Avalonia + CommunityToolkit.Mvvm, sherpa-onnx + Parakeet TDT v3, LLamaSharp + Gemma 4, SQLite + FTS5 + sqlite-vec, Microsoft.Extensions.AI.

## Жорсткі правила
- **Native AOT завжди.** Жодної рефлексії, `dynamic`, `Activator.CreateInstance`, runtime-кодогенерації. JSON — `System.Text.Json` source-gen, MVVM і DI — source-gen. Збірка з trim-попередженнями = червона.
- **Гарячі шляхи без алокацій:** аудіо/відео-callback'и — тільки `Span<T>`, `ArrayPool`, lock-free буфери. Жодних LINQ, `async`, логування з інтерполяцією всередині callback'ів.
- **Межі модулів:** `Murmur.App` залежить лише від `Murmur.Core`. Платформний код — тільки в `Murmur.Capture.*`. Інтерфейси в `Murmur.Core` змінює лише архітектор.
- **Моделі (ASR, LLM)** працюють в окремому процесі-воркері, вантажаться ліниво, ніколи не тримаються в памʼяті під час запису.
- **Нова залежність** — тільки після перевірки AOT-сумісності, ліцензії й погодження власника.
- **Дизайн** — тільки токени з `docs/design/tokens.json`. Violet/Ember — лише для «сфери» запису та іконок, ніколи для кнопок і тексту.
- **Тести** до кожної публічної поведінки; бенчмарк до кожного гарячого шляху.
- **Комміти** — Conventional Commits (`feat(capture): ...`), одна задача = одна гілка = один PR.

## Команди
```bash
dotnet build Murmur.sln -c Release
dotnet test Murmur.sln -c Release
dotnet publish src/Murmur.App -c Release -r win-x64 -p:PublishAot=true
dotnet run -c Release --project tests/Murmur.Benchmarks
```

## Середовище
Код захоплення (WASAPI, WGC, Media Foundation) і AOT-збірку win-x64 можна запускати й перевіряти лише на Windows. На Linux — тільки Core, Storage, Intelligence і тести без платформного коду.

## Мова
Документи й звіти — українською, код, коментарі й комміти — англійською.
