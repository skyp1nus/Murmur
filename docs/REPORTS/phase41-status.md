# Фаза 4.1 — стан на паузі (2026-10-21)

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

Останній зелений локальний прогін на `53e5573`: збірка без попереджень; App 915, Core 492, Storage 205, Desktop 48, Intelligence 154 — усе зелене. CI на цьому коміті ще не перевірений.

## Не злито

- **`design/onboarding`** (запушена, `740e68a`): онбординг 1:1, рев'ю пройдено й виправлено (форма 0,5–4,6 % на 12 артбордах).
  - Злиття з `phase4.1` дає конфлікти в `GlassOrbControl.cs` (обидві гілки мали копію; беремо версію онбордингу) і `DEVIATIONS.md` (об'єднати рядки).
  - Після злиття падають тест-охоронець `TokensTests.StylesAndViews_DoNotDefineKeysThatLookLikeTokens` і кілька воріт паритету (Onb1/2/4/5, Recording).
  - Що зробити: перевести ключі `Onb.*` з `TODO(D-044)` на згенеровані токени (додати відсутні в `tokens.json`, зокрема тінь списку для темної теми); узгодити `GlassOrbControl` з екраном запису; прогнати паритет усіх артбордів.

## Далі — по черзі

1. Інтеграція онбордингу (вище).
2. `p41-reduced-motion` — системне «зменшення анімації» для орбів.
3. Хвиля 3 паралельно: `p41-session` (Summary, Export), `p41-chat` (історія чатів), `p41-settings` (розділи, Acceleration).
4. Тестер на всю фазу, зелений CI (мітка `ci:windows` на PR #14), звіт і опис PR.

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
