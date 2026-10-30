# Фаза 4.1 — стан (оновлено 2026-10-30, код фази готовий)

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
| `p41-session` | перегляд запису: форма 1,1 % / 1,14 %, тексти 100 %; Summary локальною Gemma (map → reduce, таймкоди), Export (Markdown, текст, SRT, WebVTT), хвиля звуку, фільтри You / Others, пошук у транскрипті (D-046) |
| `p41-chat` | Ask AI: форма 3,7 % / 1,35 %, тексти 100 %; збережені чати в бічній панелі (міграція 5), область пошуку, джерела, копіювати / перегенерувати; вкладка Ask AI у записі й «Ask AI about this recording» |
| `p41-settings` | налаштування: форма 0,8 % / 0,65 %, тексти 100 %; розділи з підсвіткою, картка Acceleration із завантаженням збірок (D-047), розділ Preferences для налаштувань поза макетом |

Останній зелений локальний прогін на `58a694d`: збірка без попереджень; App 1106, Core 517, Storage 229 (+1 пропущений), Intelligence 183 (+2 пропущені), Desktop 52 — усе зелене.

## Не злито

Нічого: усі гілки задач злито в `phase4.1`.

Під час злиття знайшлися дві колізії між файлами стилів (`Size.WordmarkOrb`, `Rectangle.dashedOutline`): спільна область ресурсів брала значення файла, підключеного останнім. Тести-охоронці `TokensTests` тепер не пропускають однаковий ключ чи селектор у двох файлах стилів і `StaticResource` для ключів, що залежать від теми.

## Далі

Код фази готовий, CI зелений, тестер пройшов (`docs/REPORTS/phase41-tester.md`). Звіт і чекліст для власника — `docs/REPORTS/phase41.md`. Лишилась перевірка власника на ПК.

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
