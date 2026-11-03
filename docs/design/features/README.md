# Макети нових фіч

Вихідники з канвасу Claude Design «Murmur — MVP» (сторінки «Demo (clickable)» і «UI improvements»): https://claude.ai/artifact/3rHtocCQPgci5Gpfr1rDCC

Це **не** борди звірки з макетом (design parity): їх не рендерить `scripts/design/render-mockups.mjs` і не порівнюють `DesignParityTests`. Агенти читають їх як специфікацію UI для issues відповідних епіків: розміри, стани, тексти, назви шарів (`data-name`). Розміри — реальні, для вікна 1100×720 (мінімум 900×600), як і в `../source/`.

| Файл | Що показує | Епік |
| --- | --- | --- |
| `Idea1Autopilot.dc.html` | Виявлення дзвінка (тост), плаваючий орб без таймера, кнопки керування по кліку, мітки | Meeting autopilot |
| `Idea4Transcript.dc.html` | Розділи, перейменування спікерів, слова з низькою впевненістю, follow-along, закладки, гарячі клавіші | Transcript 2.0 |
| `Idea5Activity.dc.html` | Рядок стану 28 px і поповер Activity — лише факти | Activity |
| `Idea6Export.dc.html` | Діалог експорту: формат, що включити, діапазон, прев'ю, Copy / Save | Export |
| `Idea7Import.dc.html` | Імпорт файлів: drop zone, Recent in Downloads, черга, опції, drag & drop на вікно | Import files |
| `Idea2Palette.dc.html` | Command palette (Ctrl+K) — пропозиція, issue ще немає | — |
| `Idea3Tasks.dc.html` | Задачі з усіх мітингів — пропозиція, issue ще немає | — |
| `Demo.dc.html` | Клікабельне демо всього застосунку з ідеями 1, 4, 5, 6, 7 (одна сторінка, світла й темна тема) | усі |

Як і в `../source/`: кольори, шрифти, радіуси й відступи в коді беруться лише з `../tokens.json`; Violet/Ember — лише для сфери запису та іконок, ніколи для кнопок і тексту; розмітка, демо-дані й JS — лише для прототипу і в код не переносяться. Окремо в браузері `.dc.html` не відкриваються — їм потрібен рантайм канвасу.
