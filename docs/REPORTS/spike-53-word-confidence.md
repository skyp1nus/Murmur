# Спайк #53: впевненість слів (word confidence) з Parakeet TDT

Гілка `53-word-confidence-spike`, епік #48 (Transcript 2.0). Без UI.

> **Статус: чернетка.** Розділи 1, 2 і 4 — дослідження API та коду. Числа з розділу 3 (розподіл оцінок,
> поріг, precision/recall, вартість beam search і hotwords) з'являться після ручного запуску
> `asr.yml` з `word_confidence=true`. Поки що там стоять позначки «очікує на вимірювання».

## Коротко

- sherpa-onnx 1.13.8 віддає для офлайн-транcдюсера одну оцінку на токен: **`ys_log_probs`**, натуральний
  логарифм імовірності вибраного токена. Окремий прапорець у конфігурації не потрібен: для Parakeet TDT
  (greedy search) масив заповнений завжди. Ми не читали це поле, хоча воно вже було в нашій структурі.
- Оцінку слова складаємо з оцінок його токенів тим самим групуванням, що й `WordBuilder`. Токени лише з
  пунктуацією не враховуються. Агрегат за замовчуванням — мінімум (найменш певний підтокен).
- **n-best немає.** C API повертає лише найкращу гіпотезу, і для greedy, і для beam search. Альтернативи
  доведеться брати не з ASR (Gemma, словник користувача) або з повторного декодування beam search / hotwords.
- **Hotwords для TDT є**, але тільки з `modified_beam_search`, `modeling_unit = "bpe"` і `bpe_vocab`.
  Вартість: очікує на вимірювання.
- Поріг і precision/recall: очікує на вимірювання.

## 1. Які оцінки є і як рахується оцінка слова

**Версія.** `org.k2fsa.sherpa.onnx.runtime.*` 1.13.8 (`Directory.Packages.props`). Нативний пакет містить
лише `libsherpa-onnx-c-api` і `onnxruntime`, без заголовків. Тому код читався з тегу `v1.13.8` репозиторію
k2-fsa/sherpa-onnx (коміт `11afbd0`): `c-api/c-api.{h,cc}`, `csrc/offline-stream.{h,cc}`,
`csrc/offline-recognizer-transducer-{,nemo-}impl.h`, `csrc/offline-transducer-greedy-search-nemo-decoder.cc`,
`csrc/offline-transducer-modified-beam-search-nemo-decoder.cc`.

**Що є в результаті** (`SherpaOnnxOfflineRecognizerResult` і JSON з `SherpaOnnxGetOfflineStreamResult`):

| Поле | Що це | Для Parakeet TDT |
|---|---|---|
| `tokens_arr` | SentencePiece-токени; `▁` уже замінено на пробіл (`SymbolTable::operator[]`) | є (ми вже використовуємо) |
| `timestamps` | початок токена, с (кадр × 80 мс) | є (використовуємо) |
| `durations` | тривалість токена за TDT, с | є (використовуємо, D-013) |
| **`ys_log_probs`** | ln P(токен) у кадрі, де його видано | **є, без прапорця** |
| `words` | ID слів FST-декодера | лише CTC + FST, у нас порожнє |
| `lang`, `emotion`, `event`, `segment_*` | інші моделі (SenseVoice, Whisper) | порожні |

- `lm_probs` і `context_scores` у **офлайн**-результаті sherpa-onnx немає. Вони бувають лише в
  streaming-результаті (`OnlineRecognizerResult`). Ентропії, top-k на токен, оцінки тривалості й оцінки
  на рівні слова немає теж.
- C API копіює `ys_log_probs` у `float*`, тільки коли значень рівно стільки, скільки токенів, інакше `NULL`
  (`c-api.cc`, `SherpaOnnxGetOfflineStreamResult`). У JSON те саме лежить у полі `"ys_log_probs"` (6 знаків
  після коми). Поле `YsLogProbs` у нашій blittable-структурі вже було (зсув 80, його фіксує
  `NativeLayoutTests`). Тепер `OfflineRecognizer.ReadResult` копіює його в `RecognitionResult.LogProbs`.

**Як sherpa-onnx його рахує (greedy, TDT, `DecodeOneTDT`).** У кожному кадрі joiner видає логіти токенів
(8193 = словник + blank) і логіти тривалостей. Береться `argmax` токена, до копії логітів токенів
застосовується LogSoftmax, і `log_prob = logsoftmax[y]`. Звідси:

- це апостеріорна ймовірність **top-1 токена** в кадрі видачі, нормована по всьому словнику **разом із
  blank**. Розподіл тривалостей до неї не входить;
- `blank_penalty` (у нас 0) віднімається до softmax;
- кадри, де виграв blank, оцінок не дають. Тому пропущене слово (deletion) оцінкою не позначити.

**Modified beam search** (`ys_probs` найкращої гіпотези) кладе туди той самий log-softmax токена **плюс
буст hotword**, якщо токен продовжує шлях hotword. Отже, з hotwords оцінки зміщені вгору саме для слів
зі словника. Сума тривалостей входить у рахунок гіпотези, але не в `ys_log_probs`.

**Вартість.** Обчислення вже є в декодері: LogSoftmax рахується в кожному кадрі, незалежно від того, чи
читаємо ми результат. Читання коштує один `float[]` на сегмент (4 байти на токен). Агрегація проходить
токени сегмента один раз (O(n)) після декодування, поза аудіо-callback'ами. Додаткової памʼяті моделі
немає. Час декодування не змінюється; виміряні числа — в розділі 3.

**Від токенів до слів.** sherpa-onnx віддає токени NeMo з пробілом на початку слова (а не `▁`). Правило
`WordBuilder` таке: токен, що починається з `▁` або пробілу, починає слово, решта (підслова, пунктуація)
продовжують його. Тепер це правило винесено в `WordBuilder.Spans`, і `WordConfidence.Compute` ним
користується, тож оцінки відповідають `WorkerWord` сегмента одна до одної. Оцінка слова така:

- токени без літер і цифр (`,`, `.`, `?`) пропускаються. Parakeet v3 видає пунктуацію окремими токенами,
  часто з низькою ймовірністю, а про правильність самого слова це нічого не каже. Слово лише з пунктуації
  оцінюється за всіма своїми токенами;
- агрегати: `Min` = exp(min ln p), `Mean` = середнє p, `GeometricMean` = exp(середнє ln p). Вибір між
  ними — за AUC у розділі 3.

## 2. Альтернативи (n-best) і hotwords

**n-best — ні.** `OfflineTransducerDecoderResult` містить одну гіпотезу. `modified_beam_search` тримає
`max_active_paths` гіпотез усередині, але віддає лише найкращу. Top-k на токен назовні теж не виходить.
Варіанти для «запропонувати виправлення» (#58):

1. «Друга думка»: повторно декодувати сегмент з beam search або hotwords і показати слова, які змінилися.
   Вартість — розділ 3 (`changed vs greedy`).
2. Пропозиції не з ASR: Gemma за контекстом сегмента та/або власний словник користувача (нечіткий пошук).
3. Справжній n-best потребує патча sherpa-onnx (форк або нова залежність). Не рекомендую.

**Beam search для TDT** у 1.13.8 є: `decoding_method = "modified_beam_search"`, `max_active_paths`. Для
кожної гіпотези в кожному кадрі окремо запускаються decoder і joiner, без батчу між гіпотезами, а
LSTM-стани клонуються. Тому вартість росте приблизно пропорційно до `max_active_paths`. Числа — розділ 3.

**Hotwords (контекстне зміщення) для TDT** є, але з умовами:

- тільки з `modified_beam_search`; greedy їх ігнорує;
- потрібні `modeling_unit = "bpe"` і `bpe_vocab` (файл SentencePiece `піс оцінка`). З `modeling_unit` за
  замовчуванням (`cjkchar`) латиниця кодується по літерах і не збігається з токенами Parakeet. Якщо
  `bpe` задано без словника, sherpa-onnx розіменовує нульовий BPE-енкодер. `OfflineRecognizer.ValidateDecoding`
  відкидає обидва випадки ще до нативного коду;
- `bpe.vocab` створює експорт sherpa-onnx (`scripts/nemo/generate_bpe_vocab.py`, з `.nemo`). Чи є він в
  архіві `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`, покаже крок «Show models» у CI. Якщо немає, його
  можна вивести з `tokens.txt`: оцінки SentencePiece BPE — це мінус порядок злиття, тобто порядок ID, отже
  `оцінка = −id` (так роблять і workflow, і реальний тест);
- словник задається на recognizer (`hotwords_file`, `hotwords_score`) або **на потік** через
  `SherpaOnnxCreateOfflineStreamWithHotwords(recognizer, "слово1/слово2")`. Другий варіант дає словник на
  кожен запис без перезавантаження моделі; наш шар поки його не привʼязує;
- буст `hotwords_score` додається до log-ймовірності токенів, що продовжують шлях hotword, ще до top-k.
  Тому він потрапляє і в `ys_log_probs` (див. розділ 1).

## 3. Вимірювання

**Як міряємо.** `Murmur.Transcription --confidence-eval clips.tsv` (Native AOT, linux-x64) у
`asr.yml` (job `word-confidence`). Кліпи: Google FLEURS test (CC BY 4.0, читане мовлення, 16 кГц), перші
N = 150 висловлювань `en_us` і `uk_ua` (`scripts/word-confidence-clips.sh`). Якщо FLEURS недоступний,
скрипт синтезує речення через espeak-ng (мови `en-tts`, `uk-tts`). Кожен кліп декодується як одне
висловлювання, без VAD. Модель Parakeet TDT v3 int8, greedy, раннер `ubuntu-latest`.

Слово **неправильне**, якщо після нормалізації (нижній регістр, без пунктуації, апострофи `’ʼ` → `'`) воно
є заміною або вставкою щодо еталона (вирівнювання Левенштейна за словами). Пропуски (deletions)
рахуються окремо: оцінкою слова їх не позначити. Розбіжність «цифри ↔ слова» (`5` проти `five`)
рахується помилкою, тож precision занижено.

Повторне декодування beam search (`max_active_paths = 4`) і beam + hotwords проходить на перших 20
кліпах кожної мови. Hotwords — «оракульний» словник: еталонні слова, які greedy помилково розпізнав на
цих кліпах. Це верхня межа користі від словника користувача, а заодно видно, що буст робить з іншими
словами.

### 3.1 Розподіл оцінок і поріг

_Очікує на вимірювання._

### 3.2 Вартість (час, памʼять)

_Очікує на вимірювання._

### 3.3 Beam search і hotwords

_Очікує на вимірювання._

## 4. Рекомендована форма даних для #58 (пропозиція рішення D-051, вирішує архітектор)

1. **Протокол воркера.** `WorkerWord(string Text, double Start, double End, float? Confidence = null)`,
   JSON `"confidence": 0.93` (`WhenWritingNull`). Сумісно в обидва боки: старий застосунок поле ігнорує,
   а новий зі старим воркером отримує `null`. Рахує воркер (`WordConfidence`, агрегат за розділом 3).
2. **Core.** `TranscriptWord` отримує `float? Confidence { get; init; }` (0..1, `null` = невідомо).
   Init-властивість, а не новий позиційний параметр, щоб наявні виклики `new TranscriptWord(text, start, end)`
   лишилися як є. `WorkerAsrClient` переносить значення з `WorkerWord`.
3. **Альтернативи не зберігати в слові.** ASR їх не дає (розділ 2). Пропозиції виправлень (#58) рахувати
   на вимогу (Gemma з контекстом сегмента та словником користувача, або «друга думка» beam / hotwords) і не
   класти в `TranscriptWord`. Якщо пізніше знадобиться, то окремим необовʼязковим списком у моделі
   представлення, не в сховищі.
4. **Зберігання (`WordsCodec`, коли до нього дійде; зараз поза обсягом #53).** Поточний блоб:
   `count:int32`, далі на слово `start_ms:int32, end_ms:int32, len:uint16, utf8`, без поля версії. Пропоную
   **v2 з маркером**: перше `int32 = −2` (у v1 це кількість ≥ 0, тож форми не сплутати), далі `count:int32`,
   а на слово додається `confidence:byte` (0..254 → 0..1 з кроком 1/254, 255 = невідомо). Старі блоби
   читаються як v1 з `Confidence = null`, без міграції. Плата — +1 байт на слово (≈ +6 % до блобу при
   ~16 байтах на слово). Альтернатива — окрема nullable-колонка `word_confidence BLOB` (байт на слово) у
   міграції, без зміни кодека. Вона простіша для читачів, яким оцінки не потрібні, але це ще одна міграція
   і два блоби, які треба тримати узгодженими.
5. **Поріг** — константа застосунку (розділ 3), не налаштування користувача.

## Що зроблено в коді (лише воркер; користувацька поведінка не змінилась)

- `Native/OfflineRecognizer.cs`: `RecognitionResult.LogProbs` (із `ys_log_probs`); `RecognizerOptions`
  (greedy за замовчуванням, beam, hotwords, `bpe_vocab`) і `ValidateDecoding`. Продакшн-шлях створює
  recognizer з тими самими параметрами, що й раніше.
- `Pipeline/WordConfidence.cs`: оцінка слова з токенів (`Min` / `Mean` / `GeometricMean`).
  `WordBuilder.Spans` — спільне групування токенів.
- `PipelineOptions.OnWordConfidence` — гачок, за замовчуванням вимкнений (`null`). Протокол `--serve` не
  змінився. `--transcribe … --word-confidence` друкує слова з оцінками.
- `Bench/ConfidenceEval.cs`, `WordAlignment.cs`, `ConfidenceMetrics.cs`: команда `--confidence-eval`
  (нормалізація, вирівнювання, AUC, поріг, гістограма, beam і hotwords, JSON і Markdown).
- `scripts/word-confidence-clips.sh`, `.github/workflows/asr.yml` (вхід `word_confidence`, job
  `word-confidence`). Тести: `WordConfidenceTests`, `WordAlignmentTests`, `ConfidenceMetricsTests`,
  `ConfidenceEvalTests`, `RecognizerOptionsTests`, нові кейси в `CliOptionsTests` і `RealModelTests`
  (`ys_log_probs` на реальній моделі; beam + hotwords).

Відхилення від тексту issue: звіт названо `spike-53-word-confidence.md` (як у брифі архітектора), а не
`spike-word-confidence.md`. Витягування оцінок зроблено в коді воркера за вимкненим гачком, а не як
одноразовий скрипт; так попросив архітектор. Номер рішення D-051 — пропозиція: `docs/DECISIONS.md`
поза зоною цієї задачі.
