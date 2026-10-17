# Рішення (ADR-lite)

Журнал спірних рішень нічного автономного прогону. Формат: що вирішено · чому · альтернатива. Кожне рішення обране як найпростіше й таке, що легко відкотити.

| # | Фаза | Рішення | Чому | Альтернатива |
| --- | --- | --- | --- | --- |
| D-001 | 0 | .NET SDK 11.0.100-rc.2.26504.105 узято з образу `mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2-resolute-amd64` (шари розпаковано в `~/.dotnet`), а не через `dotnet-install.sh` | `builds.dotnet.microsoft.com` / `dotnetcli.azureedge.net` заблоковані проксі хмарного середовища (403); MCR доступний. На CI — звичайний `actions/setup-dotnet` | `dotnet-install.sh --channel 11.0 --quality preview`, коли хост дозволено |
| D-002 | 0 | Робоча гілка `night-run` від `master` (як просив власник), а не дефолтна гілка сесії `claude/bold-franklin-y5ehba` | Явна інструкція власника | Перенести коміти в іншу гілку `git cherry-pick`/`git push origin night-run:<інша>` |
| D-003 | 0 | Коміти без трейлерів `Co-Authored-By` / `Claude-Session` (правило в `CLAUDE.md` + локальний `commit-msg` хук) | Вимога власника | — |
| D-004 | 0 | Composition root — окремий exe `src/Murmur.Desktop` (AssemblyName `Murmur`); `Murmur.App` став бібліотекою UI, що посилається лише на `Murmur.Core` | Правило «App залежить лише від Core» перевіряється компілятором (і `ArchitectureTests`), а не домовленістю. Команда публікації змінилась на `dotnet publish src/Murmur.Desktop ...` | `Murmur.App` як exe з посиланнями на реалізації в одній папці `Composition/` |
| D-005 | 0 | DI — ручний composition root (Pure DI, без контейнера) | Нуль залежностей і рефлексії, AOT-безпечно, легко замінити | Jab / Pure.DI (source-gen контейнери, MIT) |
| D-006 | 0 | `Murmur.Desktop` мультитаргетний: `net11.0` (Linux/dev, без захоплення) і `net11.0-windows10.0.22621.0` (реальний `Murmur.Capture.Windows`) | Windows-проєкт можна посилати лише з Windows-TFM; Linux-ціль потрібна для розробки UI й AOT-перевірки тут | Лише Windows-TFM (тоді UI не запускається на Linux) |
| D-007 | 0 | `Murmur.Capture.Mac` не створюється до фази 5 | Скоуп фаз 0–3 — лише Windows; порожній проєкт без коду — шум | Порожній проєкт-заглушка зараз |
| D-008 | 0 | Тести — xunit.v3 + VSTest-адаптер; Windows-тести позначені `[Trait("Category","Windows")]` і пропускаються не на Windows | Стандарт `dotnet test`, динамічний skip у v3 | MSTest / TUnit |
| D-009 | 0 | Інтерфейси Core: крім `ICapture`/`IAsr`/`ILlm`/`IStorage` додано `IAudioSource`/`IAudioSink` (гарячий шлях), `IScreenRecorder`, `IAudioPlayer`, `IRecorder` (оркестрація запису), `IAssistant` (RAG); `IStorage` = `ISessionStore` + `ITranscriptStore` + `ISearchIndex` | Інтерфейси покривають фази 1–3 одразу, щоб далі не міняти Core; дрібні інтерфейси легше підміняти фейками | Мінімальні 4 інтерфейси й розширення по фазах |
