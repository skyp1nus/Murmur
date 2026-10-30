# p41-chat: Ask AI за макетом, зі збереженими чатами

Гілка `design/chat` (від `phase4.1` da0402e). Борди `Chat`, `ChatDark`, список «Chats» у сайдбарі.

## Що зроблено
- **Core** (затверджений контракт, без відхилень): `ChatThreadId`, `ChatThread`, `ChatMessage` у `Murmur.Core.Intelligence`; `IChatStore` у `Murmur.Core.Sessions`, входить в `IStorage`. Рівно шість методів. Семантика `AppendMessageAsync(ChatMessage)`: індекс = кількості повідомлень — додавання; нижчий — повідомлення замінюється, пізніші відкидаються (так Regenerate замінює останню відповідь, не додаючи сьомого методу); вищий — виняток.
- **Storage**: міграція **№ 4** (`chat_threads`, `chat_messages`). `chat_threads.scope_session` → `sessions(id) ON DELETE CASCADE`, повідомлення каскадом з гілкою; цитати — JSON усередині повідомлення без зовнішнього ключа, тож після видалення запису вони лишаються текстом. `SqliteStorage.Chats.cs`, source-gen JSON (`CitationDto[]`).
- **Назва `ChatMessage` збігається з `Microsoft.Extensions.AI.ChatMessage`**: у файлах, що імпортують обидва простори імен (LocalLlm, Assistant, тести Intelligence, AiEval), додано `using ChatMessage = Microsoft.Extensions.AI.ChatMessage;`.
- **ViewModels**: `ChatViewModel` (необов'язкові `store`, `sessions`, `time`: збереження обміну, `LoadThreadAsync`, `Regenerate`, `CopyAnswer`, назва гілки з першого запитання ≤ 60 символів); `ChatMessageViewModel` (абзаци з чипами в рядку, картки Sources, дії); `AnswerText` (розбір `[12:34]`, `[1:02:03]`, `[12:00–12:45]` тим самим синтаксисом, що `CitationResolver`); `LibraryChatViewModel` став сторінкою Ask AI (`IFillsFrame`): розмова, гілки, список для сайдбара, scope-пілюлі; `ChatScopeViewModel`, `ChatModelChipViewModel` (модель + прискорення з `IAccelerationStatusSource`, D-042: «Gemma 4 E4B · local · Vulkan»); `SidebarEntry` отримав Rename/Delete і вбудований редактор.
- **Views/Styles**: `ConversationView` (віртуалізований стовпець 820 px, бульбашки, відповідь з орбом `OrbDisc`, чипи через `AnswerTextBlock` з `InlineUIContainer`, Sources в `AutoFitPanel`, іконки, composer), `ChatScopeView` і `ChatModelChipView` у верхній панелі (`TopBar.Leading/Trailing`), `Styles/Chat.axaml` (усі класи `chat*`, ресурси `Chat.*`), контекстне меню рядка чату й `RenameBox` у сайдбарі. Нові типографічні кроки в `tokens.json` (кінець `typeScale`): `bubble` 15/1.55, `answer` 16/1.65, `chip` 12/1.65; `Tokens.axaml` згенеровано.
- **Поведінка гілок**: перше запитання називає гілку (створюється, коли перший обмін завершився; скасований або невдалий обмін не зберігається). Рядок «Ask AI» у сайдбарі відкриває **порожній** чат: поточний, якщо він порожній, інакше новий (старий лишається у списку). Повернення з цитованого запису (`ShowLibraryChat`) нового чату не починає. Питання з «Ask your library» відкриває нову гілку. Scope належить гілці: вибір іншої пілюлі на чаті з повідомленнями починає новий чат у тому scope. Запис, видалений разом зі scope-гілкою, прибирає її зі списку при оновленні. Помилка сховища чат не ламає.
- Стрімінг, скасування (Stop у composer), «AI не встановлено» й «пауза на час запису» збережені й перестилізовані.

## Паритет (до → після)
| Борд | Shape drawn % (весь / контент) | Text matched | Помилки геометрії | Missing |
| --- | --- | --- | --- | --- |
| Chat | 66.82 / 98.86 → **3.72 / 6.53** | 42.5 % (17/40) → **100 % (40/40)** | 7 → **1** | 23 → 0 |
| ChatDark | 76.78 / 99.92 → **1.35 / 1.70** | 42.5 % → **100 %** | 7 → **1** | 23 → 0 |

Ворота (`DesignBoards.cs`): `Chat` 3.8 / 100 / 1, `ChatDark` 1.5 / 100 / 1. `Main` 0.82 / 100 / 2 і `Recording` 0.78 / 100 / 1 не змінились.

Що ще відрізняється:
- **Плейсхолдер «Ask about any recording…»** — єдина помилка геометрії. Еталон міряє його від центру всієї textarea (984.5), а в макеті він, як і в застосунку, стоїть на першому рядку (973).
- Чипи й крапки після них лежать на 1–2 px від еталона (різна ширина тексту Chromium/Skia: попередній текст ~1.5 px ширший; чип зсунуто на −1.5 px, його висоту 22 px Avalonia не вміщує в рядок, тому чип розміщено від'ємними відступами, `Chat.ChipMargin`). Усі в межах допуску.
- Іконки під відповідями (див. відхилення): під першою лише Copy, під останньою Copy + Regenerate; composer на 1 px вужчий ліворуч (округлення 462.5), друга бульбашка на 1 px.
- Назви карток Sources, відсутність Sources під другою відповіддю — у `docs/design/DEVIATIONS.md` (чотири нові рядки).

Зміни паритетного харнесу (спільні тестові файли):
- `AppTextLayout`: для TextBlock з вбудованими елементами (чипи) вимірювання рядків знімається, поки вікно живе (`LineSnapshot`); після закриття вікна `HitTestTextRange` порожній, а для тексту поруч із вбудованим елементом порожній і живий.
- Допуск ширини: більше з 3 % і 0.5 px (3 % від крапки = 0.14 px — менше за розбіжність растеризаторів); загальний для всіх бордів.
- `DesignScene`/`DesignRig`/`DesignDemoData`: чат відкрито з Client demo, старіший чат «Action items this week», `FakeChatStore`; відповіді містять маркери `[12:52]`; прибрано рядки `NotRepresented` про Chat і Chats.

## Тести
- Storage: `ChatStoreTests` (11: гілки, порядок, заміна за індексом, перейменування, видалення, каскад зі scope-записом, цитати лишаються), міграція з v3.
- App: `ChatThreadTests` (30: життєвий цикл гілки, повторне відкриття з історією, видалені цитати не посилання, Regenerate, Copy, scope-пілюлі, список і виділення в сайдбарі, Rename/Delete, нова гілка з «Ask AI» і з Library, верхня панель, чип моделі з прискоренням), `AnswerTextTests` (8), Core `ChatThreadIdTests` (2).
- Усього: App 991, Core 494, Storage 216 (+1 пропущено), Intelligence 152 (+2), Desktop 48 — зелені; збірка рішення 0 warnings, 0 errors.

## Для злиття
- Міграція № 4 — якщо Session-гілка (ISummaryStore) теж бере 4, одну з них треба перенумерувати.
- Спільні файли, змінені адитивно: `IStorage` (+`IChatStore`), `AppServices` (+`Chats` у кінці), `CompositionRoot` (+`Chats: storage`), `ShellViewModel` (нові необов'язкові параметри в кінці), `IconControl` (+`IsFilled`), `tokens.json` (кінець `typeScale`), `Strings*.resx`, `SidebarEntry`. `ChatView` (вкладка сесії) не чіпано; `ChatViewModel` лишився сумісним (нові параметри конструктора необов'язкові).
- Рядок `LibraryChat_Subtitle` видалено (новий екран його не показує).

## Не зроблено
- Підтвердження перед видаленням чату (меню Delete видаляє одразу).
- Кнопки «відкрити чат про цей запис» на екрані сесії (екран належить іншій гілці): API готове — `ShellViewModel.ShowNewChat(SessionRecord)`.
- Скриншот екрана Chat у `docs/screenshots` не оновлювався.
