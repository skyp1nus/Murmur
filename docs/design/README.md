# Дизайн

Джерело правди — канвас Claude Design **«Murmur — MVP»**: https://claude.ai/artifact/3rHtocCQPgci5Gpfr1rDCC

Його вихідники лежать у `source/` — HTML кожного артборду (розмітка, inline-стилі, стани) і `canvas.json` (список екранів і їхні розміри). Агенти читають їх напряму: відступи, розміри, радіуси, кольори й тексти беруться звідси.

`reference/<Артборд>.png` — еталонні рендери кожного артборду (Chromium, масштаб 1, ширина з `canvas.json`, повна висота сторінки, анімації вимкнено). Застосунок має збігатися з ними 1:1 (D-041): `DesignParityTests` рендерять ті самі екрани з тими самими даними й кладуть поруч порівняння. Окремо в браузері `.dc.html` не відкриваються — їм потрібен рантайм канвасу; перерендерити після зміни дизайну: `scripts/design/render-mockups.mjs` (рантайм канвасу як `support.js` поруч з артбордами, шрифти Inter і Geist Mono — локально).

| Екран | Світла тема | Темна тема |
| --- | --- | --- |
| 1 · Бібліотека | `Main.dc.html` | `MainDark.dc.html` |
| 2 · Запис | `Recording.dc.html` | `RecordingDark.dc.html` |
| 3 · Перегляд запису | `Session.dc.html` | `SessionDark.dc.html` |
| 4 · AI-чат | `Chat.dc.html` | `ChatDark.dc.html` |
| 5 · Налаштування | `Settings.dc.html` | `SettingsDark.dc.html` |
| Онбординг 1 · Welcome | `Onb1Welcome.dc.html` | `Onb1WelcomeDark.dc.html` |
| Онбординг 2 · Audio | `Onb2Audio.dc.html` | `Onb2AudioDark.dc.html` |
| Онбординг 3 · Models | `Onb3Models.dc.html`, `Onb3ModelsReady.dc.html` | `Onb3ModelsDark.dc.html`, `Onb3ModelsReadyDark.dc.html` |
| Онбординг 4 · Preferences | `Onb4Preferences.dc.html` | `Onb4PreferencesDark.dc.html` |
| Онбординг 5 · First recording | `Onb5FirstRecording.dc.html` | `Onb5FirstRecordingDark.dc.html` |
| Онбординг · компоненти | `OnbComponents.dc.html` | `OnbComponentsDark.dc.html` |

## Правила для агентів
- Макет HTML → Avalonia: відтворюй структуру, відступи й розміри; кольори й шрифти — лише через ресурси з `tokens.json` (`color` для світлої, `colorDark` для темної теми). Жодних hex у XAML-views.
- Тема перемикається в рантаймі (світла / темна / системна) — одні й ті самі views, різні ресурси.
- Мова UI: усі екрани намальовані англійською (канвас оновлено 2026-10-20). Англійська — мова за замовчуванням і текст макета переноситься дослівно; українська — друга мова (D-039). Усі рядки — в ресурсах локалізації (`.resx`).
- Теми: кольори й пензлі (`Color.*`, `Brush.*`) лежать у `ThemeDictionaries` (`Light` ← `color`, `Dark` ← `colorDark`), тож у views і стилях їх шукають лише через `{DynamicResource}` (тест `TokensTests` забороняє `{StaticResource Brush.*}`). Ключі `color` і `colorDark` однакові — додаючи колір, додай його в обидві теми. Семантичні відмінності темної теми (наведення = Stone замість Surface тощо) — `Brush.FillHover`/`Brush.FillPressed` у `Styles/Controls.axaml`.
- Рядки UI: ключ у `src/Murmur.App/Resources/Strings.resx` (українська) і `Strings.en.resx` (англійська) з однаковими `{0}`-підстановками; у XAML — `{x:Static res:Strings.Ключ}`, у view model — `Strings.Ключ` (формат — через `UiText.Format`). Мова береться з `AppSettings.Language` один раз під час старту (D-032). Обидві мови вбудовані в `Murmur.App.dll`: Native AOT не вантажить satellite-збірки.
- `data-name` в онбордингу = назви контролів/компонентів.
- Розмітка, демо-дані й JS у `source/` — лише для прототипу, у код не переносяться.

`tokens.json` — джерело правди для кольорів, шрифтів, радіусів і відступів. Якщо макет розходиться з токенами — перемагають токени.

Промт, яким генерувався онбординг: `prompts/onboarding.md`.

## Звірка з макетом (design parity)
`tests/Murmur.App.Tests/Design/` рендерить кожен із 22 екранних артбордів застосунком (Avalonia.Headless + Skia, англійська, масштаб 1, розмір і тема артборду) з демо-даними макета (`DesignDemoData`) і порівнює з `reference/<Артборд>.png`. `OnbComponents*` — аркуш компонентів, не рендериться.

```bash
scripts/design/parity.sh                       # усі борди (збірка + тести з тегом Category=DesignParity), друкує report.md
scripts/design/parity.sh --no-build Main Chat  # лише ці борди, без збірки
pwsh scripts/design/parity.ps1 -NoBuild -Board Main, Chat
```

Результат — `artifacts/design-parity/` (не в git; CI Linux-job вантажить його як артефакт `design-parity`): `<Борд>.app.png`, `<Борд>.diff.png` (еталон сірий; червоне — лише в застосунку, синє — лише в еталоні, фіолетове — обидва; ігноровані прямокутники жовті), `<Борд>.side.png` (еталон | застосунок | diff) і `report.md` (відсоток розбіжності від намальованих пікселів — сайдбар / контент, потім усього / сайдбар / контент по всіх пікселях; ігнорована площа).

- Порівняння симетричне: піксель — розбіжність, якщо піксель застосунку не має збігу (≤ 5/255 на канал) у 3×3-околі еталона **або** піксель еталона не має збігу в 3×3-околі застосунку (1 px зсув і згладжування не рахуються; хибна заливка, зниклий hairline чи відсутній вміст — рахуються). Головна метрика — «Mismatch of drawn %»: частка розбіжностей серед пікселів, що не є фоном ні в еталоні, ні в застосунку (об'єднання); є також варіанти для сайдбара (x < 241 на п'яти основних екранах) і контенту, загальні відсотки по всіх пікселях і колонка «Drawn % at tol 24» (допуск 24 прощає текст). Текст Skia світліший за Chromium, тож навіть при ідеальній розкладці рядки тексту не збігаються при 5/255: виміряна «підлога» в `artifacts/design-parity/calibration.md` (генерує `ParityCalibrationTests`).
- Застосунок рендериться з сіро-градаційним згладжуванням тексту (як Chromium), без LCD-підпікселів.
- Ігноровані прямокутники — лише для того, що застосунок відтворити не може (плейсхолдер відеокадру, стовпчики хвиль, що малюються формулою, `[VERSION]`/`[LICENSE]`/`[X.X] GB`/`[N] GB free`); оголошені в таблиці `DesignBoards.cs` з причиною.
- Тести поки лише звітують: борд падає, тільки якщо його не вдалося відрендерити. Екранна задача, що доводить екран до паритету, виставляє `MaxMismatchPercent` цього борда в `DesignBoards.cs` — після цього перевищення стає червоним тестом.
- Дані, яких застосунок ще не вміє показати (Recent у сайдбарі, Pause, Summary, список чатів тощо), перелічені в `DesignDemoData.NotRepresented` і в кінці `report.md`; екранна задача додає функцію й дані в `DesignDemoData`.
- `MURMUR_PARITY_BOARDS=Main,Chat` обмежує прогін бордами, `MURMUR_PARITY_DIR` змінює папку виводу.
