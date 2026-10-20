# Дизайн

Джерело правди — канвас Claude Design **«Murmur — MVP»**: https://claude.ai/artifact/3rHtocCQPgci5Gpfr1rDCC

Його вихідники лежать у `source/` — HTML кожного артборду (розмітка, inline-стилі, стани) і `canvas.json` (список екранів і їхні розміри). Агенти читають їх напряму: відступи, розміри, радіуси, кольори й тексти беруться звідси, а не з картинок. Окремо в браузері ці файли не відкриваються — для перегляду використовуй посилання вище.

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
- Мова UI: основні екрани намальовані українською, онбординг — англійською. Усі рядки — в ресурсах локалізації (`.resx`), стартова мова — українська, англійська — друга.
- Теми: кольори й пензлі (`Color.*`, `Brush.*`) лежать у `ThemeDictionaries` (`Light` ← `color`, `Dark` ← `colorDark`), тож у views і стилях їх шукають лише через `{DynamicResource}` (тест `TokensTests` забороняє `{StaticResource Brush.*}`). Ключі `color` і `colorDark` однакові — додаючи колір, додай його в обидві теми. Семантичні відмінності темної теми (наведення = Stone замість Surface тощо) — `Brush.FillHover`/`Brush.FillPressed` у `Styles/Controls.axaml`.
- Рядки UI: ключ у `src/Murmur.App/Resources/Strings.resx` (українська) і `Strings.en.resx` (англійська) з однаковими `{0}`-підстановками; у XAML — `{x:Static res:Strings.Ключ}`, у view model — `Strings.Ключ` (формат — через `UiText.Format`). Мова береться з `AppSettings.Language` один раз під час старту (D-032). Обидві мови вбудовані в `Murmur.App.dll`: Native AOT не вантажить satellite-збірки.
- `data-name` в онбордингу = назви контролів/компонентів.
- Розмітка, демо-дані й JS у `source/` — лише для прототипу, у код не переносяться.

`tokens.json` — джерело правди для кольорів, шрифтів, радіусів і відступів. Якщо макет розходиться з токенами — перемагають токени.

Промт, яким генерувався онбординг: `prompts/onboarding.md`.
