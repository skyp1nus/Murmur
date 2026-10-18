# Спайк: 10 с WASAPI loopback → sherpa-onnx Parakeet → текст у консолі

Одноразовий доказ двох речей перед фазою 2:

1. 10 секунд системного звуку (WASAPI loopback) можна розпізнати через sherpa-onnx + Parakeet TDT v3 int8.
2. sherpa-onnx працює під Native AOT, і зрозуміло, яким має бути наш шар взаємодії з ним у `Murmur.Transcription`.

Код спайка не є продакшн-кодом і не має залежностей від `src/`. Його не переносимо — переносимо висновки (див. «Вердикт»).

## Що всередині

| Файл | Що робить |
|---|---|
| `Program.cs`, `CliOptions.cs` | CLI, вибір джерела звуку, друк тексту, токенів з таймстемпами, тривалості, часу декодування, RTF |
| `Capture/WasapiInterop.cs` | ручний COM-interop: `[GeneratedComInterface]` + `[LibraryImport]`, без вбудованого COM-маршалінгу |
| `Capture/WasapiLoopbackCapture.cs` | shared-mode loopback з default render endpoint, опитування кожні ~10 мс |
| `Capture/CaptureTimeline.cs` | платформонезалежна частина: пакети → моно float, заповнення пауз тишею за QPC-часом пакета |
| `Audio/WavFile.cs` | читання WAV (PCM 8/16/24/32, float 32/64, `WAVE_FORMAT_EXTENSIBLE`), запис PCM16/float32 |
| `Audio/AudioConverter.cs` | downmix + windowed-sinc (Blackman, 16 перетинів нуля, поліфазна таблиця) ресемплер до 16 кГц |
| `Asr/SherpaNative.cs` | тонкий blittable-шар `[LibraryImport]` над C API sherpa-onnx 1.13.8 |
| `Asr/Recognizers.cs` | `OfflineRecognizer` з конфігом NeMo transducer; опційно — керовані класи `SherpaOnnx.*` з NuGet |
| `SelfTest.cs` | вбудовані перевірки (`--self-test`): WAV, ресемплер, таймлайн захоплення, розкладка C-структур |

Збірка за замовчуванням використовує **наш шар** `[LibraryImport]`. Варіант із керованими класами з пакета
`org.k2fsa.sherpa.onnx` вмикається `-p:SherpaBinding=managed` і лишений лише для відтворення висновків нижче.
NuGet-пакет потрібен в обох випадках: він приносить нативні `sherpa-onnx-c-api` та `onnxruntime`.

## CLI

```
Murmur.Spike.LoopbackAsr [--seconds 10] [--wav <file>] [--model-dir <dir>] [--save <out.wav>] [--self-test]
```

- без `--wav` — тільки Windows: захоплення `--seconds` секунд системного звуку; на інших ОС — зрозуміле повідомлення і код 2;
- `--wav` — WAV будь-якої частоти й кількості каналів (PCM16 або float32, також 8/24/32-bit PCM);
- `--model-dir` — за замовчуванням `$MURMUR_MODELS_DIR/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`, інакше
  `~/.murmur/models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` (на Windows `%USERPROFILE%\.murmur\models\...`);
- `--save` — записати 16 кГц моно PCM16, який іде в розпізнавач (зручно, щоб послухати, що саме захопилося);
- `--self-test` — прогнати вбудовані перевірки й вийти.

## Як запустити на Windows

Потрібні .NET 11 SDK і модель у `%USERPROFILE%\.murmur\models\sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`
(`encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, `tokens.txt`; архів моделі —
`sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2` з релізу `asr-models` репозиторію k2-fsa/sherpa-onnx).

```powershell
dotnet publish spikes/Murmur.Spike.LoopbackAsr -c Release -r win-x64
cd spikes\Murmur.Spike.LoopbackAsr\bin\Release\net11.0\win-x64\publish
.\Murmur.Spike.LoopbackAsr.exe --self-test
# Увімкніть відео з мовленням (YouTube, подкаст) на пристрої виводу за замовчуванням, потім:
.\Murmur.Spike.LoopbackAsr.exe --seconds 10 --save loopback16k.wav
```

Очікується: рядок `Loopback: 48000 Hz, 2 ch, Float32 (WAVE_FORMAT_EXTENSIBLE) ...`, через 10 с — статистика захоплення
(скільки секунд тиші дописано), далі `Text:` з розпізнаним текстом, токени з таймстемпами й рядок
`Audio: 10.000 s | decode: ... | RTF: ...`. Якщо під час запису нічого не грає — loopback не віддає пакетів,
спайк дописує тишу, і текст буде порожнім; це очікувана поведінка.

> **UNVERIFIED-ON-WINDOWS.** Шлях захоплення на Windows тут лише компілюється (Linux, `EnableWindowsTargeting`).
> Жоден рядок WASAPI-коду не виконувався. Перший запуск на Windows — головна перевірка цього спайка.

## Як запустити на Linux (тільки `--wav`)

```bash
M=/root/models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8
dotnet run -c Release --project spikes/Murmur.Spike.LoopbackAsr -- --wav $M/test_wavs/en.wav --model-dir $M
dotnet publish spikes/Murmur.Spike.LoopbackAsr -c Release -r linux-x64
spikes/Murmur.Spike.LoopbackAsr/bin/Release/net11.0/linux-x64/publish/Murmur.Spike.LoopbackAsr --wav $M/test_wavs/en.wav --model-dir $M
```

## Результати, виміряні тут

Машина: хмарна Linux x64, 4 vCPU, 15 GB RAM, Ubuntu 24.04, без GPU; .NET SDK 11.0.100-rc.2; 4 потоки ONNX Runtime, CPU.
Машину паралельно ділили інші агенти (збірки), тож час «шумний» на ±30 %. RTF = час декодування / тривалість аудіо
(без завантаження моделі).

Тексти (однакові для JIT, Native AOT і обох біндингів):

| Файл | Текст |
|---|---|
| en.wav (24 кГц, 3.845 с) | Ask not what your country can do for you, ask what you can do for your country. |
| de.wav (22.05 кГц, 2.752 с) | Alles hat ein Ende, nur die Wurst hat zwei. |
| fr.wav (22.05 кГц, 4.969 с) | Ne vous demandez pas ce que votre pays peut faire pour vous. Demandez-vous plutôt ce que vous pouvez faire pour lui. |
| es.wav (22.05 кГц, 5.329 с) | No preguntes qué puede hacer tu país por ti, pregunta qué puedes hacer tú por tu país. |

Час (наш шар `[LibraryImport]`):

| Режим | Файл | Декодування | RTF | Завантаження моделі | Пік working set |
|---|---|---|---|---|---|
| JIT | en | 527–825 мс | 0.14–0.22 | 3.9–4.2 с | 836–839 MiB |
| JIT | de | 322–523 мс | 0.12–0.19 | 3.9–5.0 с | 829–836 MiB |
| JIT | fr | 516–642 мс | 0.10–0.13 | 4.3 с | 844–848 MiB |
| JIT | es | 539–606 мс | 0.10–0.11 | 3.9–4.2 с | 849–850 MiB |
| JIT | en×3, 13.0 с | 1486 мс | 0.114 | 3.7 с | 898 MiB |
| JIT | en+fr+es, 15.6 с | 1583 мс | 0.101 | 3.9 с | 911 MiB |
| Native AOT | en | 486 мс | 0.127 | 3.8 с | 818 MiB |
| Native AOT | de | 336 мс | 0.122 | 3.7 с | 806 MiB |
| Native AOT | fr | 606 мс | 0.122 | 4.0 с | 829 MiB |
| Native AOT | es | 649 мс | 0.122 | 4.2 с | 835 MiB |

Керовані класи `SherpaOnnx.*` під Native AOT: en 453 мс (RTF 0.118), de 408 мс (0.148), fr 527 мс (0.106) — та сама швидкість.

Розмір Native AOT публікації linux-x64: `Murmur.Spike.LoopbackAsr` 1.7 MB + `libsherpa-onnx-c-api.so` 5.1 MB +
`libonnxruntime.so` 27 MB (+ `.dbg` 2.8 MB).

## Native AOT: вердикт щодо sherpa-onnx

**Попередження trim/AOT: 0** у всіх варіантах `dotnet publish -c Release -r linux-x64` (з `TrimmerSingleWarn=false`,
тобто кожне місце виклику окремо):

- наш шар `[LibraryImport]` — 0 IL2xxx/IL3xxx, працює;
- керовані класи з пакета (`-p:SherpaBinding=managed`) — 0 IL2xxx/IL3xxx, працює;
- окремий застосунок, що коренить **усю** збірку `sherpa-onnx.dll` (`<TrimmerRootAssembly Include="sherpa-onnx" />`) —
  0 попереджень. Контрольний `Marshal.SizeOf(Type.GetType(...))` у тому ж застосунку дав очікувані IL2057 + IL3050,
  тобто попередження справді показуються.

Чому керований біндинг «чистий», хоча він і не AOT-орієнтований: усі виклики — класичні `[DllImport]` з
вбудованим маршалінгом (структури з `[MarshalAs(UnmanagedType.LPStr)] string`, `ref struct`-параметри, `float[]`),
а `Marshal.PtrToStructure(IntPtr, typeof(Impl))` викликається з константним `typeof`. ILC генерує стаби маршалінгу
на етапі компіляції й розпізнає `PtrToStructure` з відомим типом, тому `RequiresDynamicCode` не спрацьовує.

Що з ним не так (знайдено декомпіляцією `lib/net10.0/sherpa-onnx.dll` і порівнянням з `c-api.h` з релізу v1.13.8):

1. **Баг розкладки результату.** `OfflineRecognizerResult.Impl` має поля `Text, Timestamps, Count, Tokens, Durations`,
   а C-структура — `text, timestamps, count, tokens, tokens_arr, json, lang, emotion, event, durations, ...`.
   Керований `Durations` читає `tokens_arr` (масив `char*`) як `float*` — тривалості токенів сміттєві
   (видно в спайку: `+-8.4e32`, `+2.01`, `+0.00`). Текст і таймстемпи коректні.
2. **`LPStr` на Windows = ANSI-кодова сторінка, не UTF-8.** Шлях до моделі з кирилицею в імені користувача
   (`C:\Users\Владислав\...`) пройде через ACP. Наш шар передає UTF-8. Як сам sherpa-onnx на Windows відкриває
   файл за `const char*` (ACP чи UTF-8) — **UNVERIFIED-ON-WINDOWS**; перевірити у фазі 2 на шляху з не-ASCII, за потреби —
   `activeCodePage=UTF-8` у маніфесті воркера.
3. Зайві копії й алокації: `AcceptWaveform(int, float[])` вимагає масив (наш шар приймає `ReadOnlySpan<float>` без копії),
   результат завжди копіюється в `string[]`/`float[]`; несумісно з `[assembly: DisableRuntimeMarshalling]`.
4. Делегат `OfflineSpeakerDiarizationProgressCallback` без `[UnmanagedFunctionPointer]` — на x64 байдуже, але це ще одне
   місце вбудованого маршалінгу (не перевірялося, у спайку не використовується).

**Рішення для фази 2 (пропозиція):** у `Murmur.Transcription` — власний тонкий blittable-шар `[LibraryImport]` над C API
(як `Asr/SherpaNative.cs`: `nint` замість `const char*`, UTF-8 рядки, структури за вказівником, `ReadOnlySpan<float>`),
плюс тест розкладки структур, звірений з `c-api.h` тієї версії, яку тягнемо (див. `SelfTest.SherpaStructLayout`;
очікувані розміри отримані з `clang` проти `c-api.h`). Від NuGet беремо тільки нативні бібліотеки — краще посилатися
напряму на `org.k2fsa.sherpa.onnx.runtime.win-x64` (і `osx-arm64` у фазі 5), а не на мета-пакет, який тягне 9 RID
(~260 MB у `bin` при звичайній збірці). Альтернатива — лишитися на керованих класах: працює під AOT, але з пунктами 1–3.
Ризик власного шару — розсинхрон розкладки при оновленні sherpa-onnx; закривається тестом розкладки й фіксованою версією.

## Ще одна знахідка: зміна мови посеред фрагмента

Один виклик `Decode` на склеєному фрагменті з різними мовами губить другу мову: en+fr+es (15.6 с) → розпізнано
en і fr, es зникла; es+en (10.2 с) → тільки es. Та сама мова тричі поспіль (13 с) розпізнається повністю.
Висновок для фази 2: різати потік на фрази VAD (Silero, уже в плані) і декодувати кожну окремо — це критично для
мітингів, де змішуються українська й англійська.

## WASAPI: що звірено з заголовками Windows SDK

Звірено з `Include/10.0.26100.0` з NuGet-пакета `Microsoft.Windows.SDK.CPP` (C-оголошення `...Vtbl`):

- `IMMDeviceEnumerator` (`mmdeviceapi.h`, IID `A95664D2-9614-4F35-A746-DE8DB63617E6`): після IUnknown —
  `EnumAudioEndpoints, GetDefaultAudioEndpoint, GetDevice, RegisterEndpointNotificationCallback,
  UnregisterEndpointNotificationCallback`;
- `IMMDevice` (IID `D666063F-1587-4E43-81F1-B948E807363F`): `Activate, OpenPropertyStore, GetId, GetState`;
  `Activate(REFIID, DWORD, PROPVARIANT*, void**)`;
- CLSID `MMDeviceEnumerator` = `BCDE0395-E52F-467C-8E3D-C4579291692E`; `eRender = 0`, `eConsole = 0`;
- `IAudioClient` (`Audioclient.h`, IID `1CB9AD4C-DBFA-4c32-B178-C2F568A703B2`): `Initialize, GetBufferSize,
  GetStreamLatency, GetCurrentPadding, IsFormatSupported, GetMixFormat, GetDevicePeriod, Start, Stop, Reset,
  SetEventHandle, GetService`; `Initialize(AUDCLNT_SHAREMODE, DWORD, REFERENCE_TIME, REFERENCE_TIME,
  const WAVEFORMATEX*, LPCGUID)` — enum як `int`, `REFERENCE_TIME` як `long`;
- `IAudioCaptureClient` (IID `C8ADBD64-E71E-48a0-A4DE-185C395CD317`): `GetBuffer, ReleaseBuffer, GetNextPacketSize`;
  `GetBuffer(BYTE**, UINT32*, DWORD*, UINT64*, UINT64*)`;
- константи: `AUDCLNT_SHAREMODE_SHARED = 0` і `AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000` (`AudioSessionTypes.h`),
  `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1`, `SILENT = 0x2`, `TIMESTAMP_ERROR = 0x4` (`Audioclient.h`),
  `CLSCTX_ALL = 0x17` (`combaseapi.h` + `WTypesbase.h`), `COINIT_MULTITHREADED = 0` (`combaseapi.h`/`objbase.h`);
- `WAVEFORMATEX` / `WAVEFORMATEXTENSIBLE` (`mmreg.h`, `#pragma pack(1)`): 18 і 40 байт, `SubFormat` на зсуві 24
  (`Pack = 1`, перевіряється в `--self-test`); `WAVE_FORMAT_PCM = 1`, `IEEE_FLOAT = 3`, `EXTENSIBLE = 0xFFFE`;
  `KSDATAFORMAT_SUBTYPE_PCM = 00000001-0000-0010-8000-00aa00389b71`, `IEEE_FLOAT = 00000003-...` (`ksmedia.h`).

Поведінка захоплення: `CoInitializeEx(MTA)` (S_FALSE і RPC_E_CHANGED_MODE допустимі) → `CoCreateInstance` →
`GetDefaultAudioEndpoint(eRender, eConsole)` → `Activate(IAudioClient)` → `GetMixFormat` →
`Initialize(SHARED, LOOPBACK, 1 с)` → `CoTaskMemFree(mix format)` → `GetService(IAudioCaptureClient)` → `Start` →
цикл `GetNextPacketSize / GetBuffer / ReleaseBuffer` кожні ~10 мс → `Stop`. Кожен HRESULT перевіряється,
RCW створюються через `StrategyBasedComWrappers` з `UniqueInstance` і звільняються `ComObject.FinalRelease()`.
Пакети з `AUDCLNT_BUFFERFLAGS_SILENT` пишуться нулями; паузи, коли нічого не грає, заповнюються тишею за
`pu64QPCPosition` пакета відносно старту (порогом 30 мс), хвіст доповнюється до рівно N секунд.
Цикл захоплення не алокує (попередньо виділений буфер, без LINQ і форматування рядків).

## Обмеження спайка

- Ресемплінг після захоплення, офлайн, на всьому буфері; у фазі 1 потрібен потоковий ресемплер.
- Підтримані формати mix format: float32 і PCM16 (інше — зрозуміла помилка).
- Немає юніт-тестів у `tests/` (спайк поза межами solution-тестів) — замість них `--self-test`, який проходить і під JIT,
  і під Native AOT. Бенчмарку немає: код одноразовий, гарячий шлях фази 1 буде переписаний у `Murmur.Capture.Windows`.
