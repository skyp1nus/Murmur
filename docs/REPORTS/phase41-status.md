# Фаза 4.1 — стан (оновлено 2026-10-30)

Гілка інтеграції: `phase4.1` (PR #14, чернетка). Мета й задачі — `docs/PLAN.md` (фаза 4.1), специфікації — `docs/tasks/p41-*.md`, рішення D-039…D-045.

## Зроблено й злито в `phase4.1`

| Задача | Результат |
| --- | --- |
| Канвас англійською, еталони | `docs/design/source` = живий канвас; `docs/design/reference/<Артборд>.png`, `.notext.png`, `.text.json` |
| `p41-english-default` | англійська за замовчуванням, українська друга (D-039) |
| `p41-gpu-acceleration` | CPU / Vulkan / CUDA, збірки з пінами SHA-256, відкат на CPU (D-042) |
| `p41-core-texts` | коди в Core, слова в App (D-043) |
| `p41-parity-harness` | `DesignParityTests`: шар форм, збіг текстів, геометрія тексту; ворота на кожен артборд (D-041) |
| `p41-shell-library` | бічна панель, верхня смуга, бібліотека: форма 0,8 % / 1,2 %, тексти 100 %; токени з канвасу (D-044) |
| `p41-recording` | екран запису: форма 0,77 % / 0,94 %, тексти 100 %; пауза й продовження запису, окреме перейменування (D-045) |
| `p41-onboarding` | онбординг 1:1: форма 0,5–4,6 % на 12 артбордах, тексти 100 %; значення канвасу онбордингу й запису перенесено в `tokens.json` (типографіка, відступи 18/36, `Shadow.PopoverDark`, розділ `glassOrbs` → `Glass.*`); орб читає `Orb.*`, ореол — гаусове розмиття CSS |
| `p41-reduced-motion` | Windows «Animation effects» вимкнено → орби стоять на кадрі канвасу, таймери зупинені; рівні, прогрес і плеєр працюють; підписка на зміну — після першого кадру (бюджет старту) |

Останній зелений локальний прогін на `693d14b`: збірка без попереджень; App 953, Core 492, Storage 204 (+1 пропущений), Desktop 48, Intelligence 152 (+2 пропущені) — усе зелене.

## Не злито

Нічого: усі гілки задач злито в `phase4.1`.

Під час злиття знайшлися дві колізії між файлами стилів (`Size.WordmarkOrb`, `Rectangle.dashedOutline`): спільна область ресурсів брала значення файла, підключеного останнім. Тести-охоронці `TokensTests` тепер не пропускають однаковий ключ чи селектор у двох файлах стилів і `StaticResource` для ключів, що залежать від теми.

## Далі — по черзі

1. Хвиля 3 паралельно (у роботі): `p41-session` (Summary, Export), `p41-chat` (історія чатів), `p41-settings` (розділи, Acceleration).
2. Самовідновлення плеєра після швидкої перемотки на паузі (гонка Media Engine з BACKLOG, втретє в CI).
3. Тестер на всю фазу, зелений CI (мітка `ci:windows` на PR #14), звіт і опис PR.

## Як продовжити у VS Code (Claude Code)

```bash
git fetch origin && git checkout phase4.1
dotnet build Murmur.sln -c Release
dotnet test --project tests/Murmur.App.Tests -c Release
scripts/design/parity.sh --no-build Main Recording      # паритет вибраних артбордів; звіт — artifacts/design-parity/report.md
```

- **Команда агентів:** `.claude/agents/` (coder, ui, reviewer, tester, perf). Цикл: задача → рев'ю → тести → злиття в `phase4.1`.
- **Робота з паритетом:** агент екрана працює по `artifacts/design-parity/<Артборд>.text.md` (помилки тексту за важкістю) і `.side.png` / `.shapes.side.png`.
- **Ворота.** Встановлюються в `tests/Murmur.App.Tests/Design/DesignBoards.cs` на досягнутих значеннях, щоб CI тримав їх.
- **Перерендер еталонів** (`scripts/design/render-mockups.mjs`) потрібен лише тоді, коли змінився сам канвас. Для нього треба рантайм канвасу (`artifact-type/dc-runtime.js` з артефакту дизайну) як `support.js` поруч з артбордами і шрифти Inter / Geist Mono локально.
- **Коміти:** без трейлерів `Co-Authored-By` / `Claude-Session`, Conventional Commits.
