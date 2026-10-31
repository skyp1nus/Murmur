# Фаза 4.1: звіт тестувальника (гілка test/phase41, база phase4.1 @2261dc6)

## Результати прогонів (3 повні прогони, Linux, Release, --no-build)

| Проєкт | Усього | Пройшло | Пропущено | Впало (3 прогони) |
| --- | ---: | ---: | ---: | ---: |
| Murmur.App.Tests | 1110 (з моїми тестами 1142) | 1110 | 0 | 0 / 0 / 0 |
| Murmur.Core.Tests | 525 | 525 | 0 | 0 / 0 / 0 |
| Murmur.Storage.Tests | 230 | 229 | 1 | 0 / 0 / 0 |
| Murmur.Intelligence.Tests | 185 | 183 | 2 | 0 / 0 / 0 |
| Murmur.Desktop.Tests | 52 | 52 | 0 | 0 / 0 / 0 |
| Murmur.Capture.Windows.Tests (частина Linux) | 841 | 749 | 92 (Windows) | 0 / 0 / 0 |
| Murmur.Transcription.Tests | 104 | 104 | 0 | 0 / 0 / 0 |
| Murmur.Models.Tests | 186 | 186 | 0 | 0 / 0 / 0 |

Флейків не виявлено (24 запуски проєктів, жодного збою). Після додавання тестів App.Tests: 1142 / 1142; мої 32 тести пройшли 3 рази поспіль окремо й раз у повному прогоні.

Примітка про середовище: диск був заповнений (623 МБ вільно; інші worktree по 3 ГБ). bin/obj/artifacts цього worktree винесені симлінками в /dev/shm (через це перший прогін мав 1 хибну помилку про LICENSE, і Core Architecture тести падали про Directory.Build.props, доки я не додав симлінки; усе враховано, у підсумкових прогонах чисто).

## Паритет (scripts/design/parity.sh --no-build)

74 тести DesignParity пройшли, 22 дошки, усі з гейтами (shape / text / geometry; головні екрани ще й sidebar). Невикористані (ungated) дошки: лише 2 компонентні аркуші OnbComponents і OnbComponentsDark (свідомо виключені, DesignBoards.Excluded; з 24 у canvas.json). Доданий тест `EveryBoardOfTheParityTable_HasAllThreeGates...` не дає з'явитися негейтованій дошці.

| Дошка | Shape % (гейт) | Текст | Geom err (гейт) |
| --- | --- | --- | --- |
| Main / Dark | 0.82 (1.0) / 1.21 (1.5) | 100% | 2 (2) |
| Recording / Dark | 0.78 (1.0) / 0.95 (1.2) | 100% | 1 (1) |
| Session / Dark | 1.10 (1.5) / 1.14 (1.5) | 100% | 1 (1) |
| Chat / Dark | 3.72 (3.8) / 1.35 (1.5) | 100% | 1 (1) |
| Settings / Dark | 0.82 (1.0) / 0.65 (1.0) | 100% | 1 (1) |
| Onb1Welcome / Dark | 0.50 (0.8) / 0.59 (0.8) | 100% | 0 |
| Onb2Audio / Dark | 1.91 (3.0) / 2.50 (3.0) | 100% | 0 |
| Onb3Models / Dark | 4.54 (5.0) / 4.61 (5.0) | 100% | 0 |
| Onb3ModelsReady / Dark | 3.78 (4.5) / 3.91 (4.5) | 100% | 0 |
| Onb4Preferences / Dark | 2.46 (2.8) / 1.24 (2.8) | 100% | 0 |
| Onb5FirstRecording / Dark | 2.88 (3.5) / 3.10 (3.5) | 100% | 0 |

Chat (світла) 3.72 проти гейту 3.8 і Onb3Models 4.54/5.0 мають найменший запас; решта комфортно.

### Візуальний огляд side-by-side (Main, Recording, Session, Chat, Settings, Onb1, Onb5), що метрики пропускають
- Onb5 (First recording): рамка поля «Your words will appear here.» в застосунку пунктирна, у макеті суцільна (стан порожнього поля). P3.
- Chat: у першої (не останньої) відповіді застосунок показує лише іконку Copy, макет показує Copy і Regenerate (Regenerate лише для останньої відповіді за задумом). Також заголовки карток джерел у застосунку довші («Client demo — Q4 road…» проти «Client demo» у макеті; різниця демо-даних). P3.
- Recording: смужки рівнів «Tracks» у застосунку мають «майбутні» штрихи-заглушки замість світлих стовпчиків (задокументовано, ігнорований прямокутник). Куля, таймер, кнопки збігаються.
- Session: кадр відео синій (заглушка фейкового програвача, ігнорується). Settings: Vulkan замість DirectML і секція Preferences (задокументовані відхилення D-042). Main, Onb1: візуально ідентичні.
- Куля (orb) на всіх оглянутих дошках візуально збігається з макетом.

## Крос-функціональні сценарії (нові тести: tests/Murmur.App.Tests/Phase41CrossFeatureTests.cs, UkrainianBoardsTests.cs)
- Англійська за замовчуванням: вже покрито (AppSettings_DefaultLanguage_IsEnglish, Strings_ComeFromTheNeutralEnglishResources_ByDefault).
- Українська: 22 дошки (усі екрани, чат, вкладки сесії, Settings із Acceleration/Preferences, усі кроки онбордингу, обидві теми) рендеряться з uk на рівні процесу; жодного англійського UI-рядка (точних і форматних) не лишилося. Тест у колекції без паралелізму, бо міняє мову процесу. Мова змінюється лише після перезапуску (D-032), «на льоту» не перемикається за задумом. Hard-coded Text="..." у .axaml: лише бренд «Murmur» (5 місць).
- Чат: вкладка і сторінка на одному треді (див. P2). Видалення запису під час стрімінгу відповіді у чаті зі scope: відповідь лишається, нотатка «no longer saved», тред скинуто, без винятків (тест).
- Запис: пауза/відновлення з reduced motion: кулю не анімовано у всіх станах, стан і таймер слідують (тест). Rename під час stop уже покрито (ARenameStillBeingWritten_IsFinishedBeforeTheStop...).
- Settings: зміна мови й теми під час завантаження runtime Acceleration не зачіпає завантаження, прогрес триває, вибір зберігається (тест). Онбординг знову з Preferences: покрито (OnboardingViewModelTests); додано випадок під час запису: екран не показується, прапорець скинуто, запис триває (див. P3).
- Швидкість: плеєр без IAudioPlayerControls (з відео з керуванням і без): чіп вимкнений, ставка відео не змінюється; аудіо з керуванням і відео без: аудіо циклує, ставки відео немає; обидва: однакова ставка в обох (3 тести).
- Вже покриті існуючими тестами (перевірено за назвами): Summary під час запису (WhileRecording_TheTab...), видалення під час обробки (Delete_IsNotOffered...), експорт без транскрипту (Export_NeedsATranscript).

## Знайдені проблеми
- **P2: Ask AI сторінка і вкладка сесії тримають окремі копії одного треду.** Відтворено тестом `ThePageAndTheTab_HoldingOneThread_RefuseTheLaterSave_AndTheExchangeIsNotStored`. Кроки: відкрити збережений тред запису T на сторінці Ask AI, перейти до цього запису (сторінка живе далі), у вкладці Ask AI поставити питання (T: 4 повідомлення), повернутися на сторінку і поставити питання. Сторінка рахує `_storedCount`=2, додає індекс 2 з роллю User, store відмовляє («Only the last answer can be replaced»), на відповіді «This exchange could not be saved». Користувач бачить попередження, але обмін зникає після перезавантаження треду (втрата одного обміну, застарілий вигляд треду на сторінці теж лишається). Місце: src/Murmur.App/ViewModels/ChatViewModel.cs SaveExchangeAsync (~рядки 425-470) і ShellViewModel.ShowLibraryChat (не перечитує тред). Пропозиція: на ShowLibraryChat і в OnChatOpened перезавантажувати відкритий тред зі store (LoadThreadAsync), або в SaveExchangeAsync при відмові перечитати кількість повідомлень і повторити. Тест зараз фіксує поточну поведінку (характеризаційний); після виправлення його треба інвертувати.
- **P3: `CycleSpeedCommand.Execute` на вимкненому чіпі** (src/Murmur.App/ViewModels/SessionViewModel.Player.cs:~57) змінює Speed/SpeedText ще до перевірки можливостей плеєра (CommunityToolkit.Execute не перевіряє CanExecute). З UI недосяжно (кнопка вимкнена), лише програмно. Виправлення: ранній вихід `if (!CanChangeSpeed) return;`. Не виправляв.
- **P3: Повторний запуск онбордингу під час запису**: прапорець OnboardingCompleted скидається, але екран не з'являється (ShowOnboarding ігнорується), `_wasCompleted` оновлюється, тож до наступного старту онбординг не покажеться, хоч користувач натиснув «Restart». Поведінка безпечна для запису, але без пояснення в UI.
- **P3: Тести перезаписують відстежувані файли** docs/screenshots/phase1-recording-*.png і phase3-chat.png при кожному прогоні (робоче дерево стає брудним). Пропозиція: писати скріншоти в artifacts/ або під env-прапор. Я їх не коммітив (git checkout).
- P3 (візуальні): див. розділ про Onb5 (пунктир) і Chat (іконки).

## Grep-перевірки
- `TODO(` у src/: 0.
- Літеральні кольори в .axaml поза Tokens.axaml: hex-кольорів немає; лише `Transparent` (прозорий фон у стилях, не колір палітри).
- Violet/Ember (Brush.Accent*, Orb.*): лише Tokens.axaml і файли кулі/іконок (OrbDisc, OrbControl, GlassOrbControl; RecordingView.axaml.cs і FirstRecordingStepView.axaml.cs лише присвоюють `Orb.LevelSource`/`VoiceOrb`, це імена елементів). Тести TokensTests (AccentColours_AreOnlyUsedByOrbAndIconFiles та ін.) виконуються й проходять у повному прогоні.
- Вибіркова перевірка найновіших VM (AccelerationSettingsViewModel, ChatViewModel, SessionViewModel.Summary/Player, RecordingViewModel pause): є профільні тести (AccelerationSettingsTests 15+, ChatThreadTests/ChatRobustnessTests, SessionFeaturesTests, RecordingPauseViewModelTests).

## Прогалини, які не вдалося закрити
- Реальні Windows-перевірки (WASAPI, WGC, MF, Vulkan/CUDA runtime, seek-recovery на справжньому плеєрі, швидкість WSOLA на реальному аудіо) на Linux недоступні: 92 тести Capture.Windows пропущено за Trait Windows.
- Розкладка 3-х прогонів не охоплює Linux-невидимі гонки на повільнішому CI; навантаження CPU у моїх прогонах було невисоке.
- Перемикання мови «на льоту» не тестується, бо за D-032 його немає (потрібен перезапуск).

## Критерій фази 4.1 («дизайн 1:1 з canvas, Windows»)
На Linux: так (22/22 дошок у гейтах, 1142/1142 тестів App, інші проєкти зелені, флейків нема). Windows-частина (запуск, AOT, реальні драйвери) підтверджується лише CI. Відкриті: P2 вище.
