# План имплементации: Этап 2 «Сеть» — FishNet, серверный мир, трое в контейнере (app-5nu)

> **For agentic workers:** REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.
> Распределение моделей (предложение агента, утверждает владелец): implementer
> per task = **sonnet** для механики по готовым формулам; **opus** — Т3 (спайк),
> Т26/Т29 (кодек снапшота, избыточность событий), Т30 (шов оружия и
> предсказания), Т31 (часы рендера), Т43–Т44 (фасад и сетевой бэкенд) и
> финал-ревью ветки; ревьюеры фаз = 2 × Explore
> (спека-соответствие + качество/арифметика); **верификация всех вердиктов,
> прогоны R-TEST/R-COMPILE, гейты и вехи — main-агент лично, не на веру.**
> Шаги — чекбоксы `- [ ]`.

**Goal:** server-authoritative матч на трёх сборщиков — мультиплеер в сим-ядре,
стены и коридоры, минимальный PvP, собственный протокол снапшота, предсказание
своего движения на FishNet, серверный фильтр видимости и слышимости,
headless-образ в Docker Hub, лаг-гейт механик и спайк голоса — по спеке
`docs/superpowers/specs/2026-08-04-stage2-network-spec.md` (**v3.1**).

**Architecture:** мир — единственный источник игровых исходов и живёт **только
на сервере**; клиент держит предсказанную копию своего `PlayerState` и рисует
остальное из снапшотов на единых часах рендера. Симуляция остаётся чистым C#
(CR 1): мультиплеер, стены и видимость — новые детерминированные поля и системы
`Ring.Simulation`, а не сетевой код. Networking читает мир только через
существующий `CaptureSnapshot` и три публичных шва (`PlayerPrediction`,
`SimInputSanitizer`, `VisibilitySystem`). Presentation не знает о сети: у
`SimulationRunner` два бэкенда за одним контрактом.

**Tech Stack:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics 1.2.1,
**FishNet 4.7.2** (UPM git-URL; новый пакет, санкционирован ADR-002 T2),
**MetaVoiceChat** (MIT, вендоринг, спайк), Docker + Docker Hub.

**Спека:** v3.1 (С1–С21, Р1–Р109; два круга self-review, аудит перед
имплементацией, фикс-волна фазы Ф1).
**Статус плана:** **v3.1** — v1 → self-review по `review_plan.md` четырьмя
субагентами (A корректность кода, B конвенции, C переиспользование, D TDD и
полнота): 7 Critical + ~25 Important + ~25 Minor, все закрыты; сводка — раздел
«Правки по self-review» в конце. **v3 (2026-08-05) — аудит перед имплементацией**
двумя Explore-субагентами на Opus со сверкой по коду, все вердикты перепроверены
главным агентом командой (урок 49); 4 развилки решены владельцем (F1a–F4a), сводка
— раздел «Правки по аудиту перед имплементацией» в конце.
**v3.1 (2026-08-05) — фикс-волна фазы Ф1** по двум ревью спайка Т3 (прибор
измерений и документы); сводка — раздел «Правки по фикс-волне фазы Ф1» в конце.

## Global Constraints (каждый таск обязан соблюдать)

- **Пути:** `APP_REPO="/home/brolin/Documents/!_MY_Proj/The Ring/app"`
  (bd — ТОЛЬКО отсюда); `WT="$APP_REPO/.worktrees/feature-app-5nu-stage2-network"`
  — cwd всех команд; **worktree создаётся ДО Т1** (`superpowers:using-git-worktrees`,
  ветка `feature/app-5nu-stage2-network` от `main 973e8ec`) и дальше не
  пересоздаётся; `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH=<scratchpad ТЕКУЩЕЙ сессии>` — задать на старте.
- **Стартовые счётчики:** 189 EditMode-тестов, golden `0x760AEB00D11301C4UL`.
  **Инвариант перепинов разнесён по двум константам** (поправка фазы Ф4, спека
  Р124 — прежняя формулировка «перепинов ровно два» противоречила решению
  владельца Р113):
  - **соло-golden — ровно два перепина, Т10 и Т16.** Оба исполнены; любой
    дальнейший его сдвиг — стоп и разбор, а не перепин по дороге;
  - **мультиплеерный golden — ровно три пина: Т10** (первичный), **Т16**
    (геометрия) и **Т17** (матрица поражения, санкция Р113). Все три исполнены,
    санкция израсходована полностью; четвёртый сдвиг — стоп.
- **Запретный список:** не менять `client/CLAUDE.md`, `.github/CODEOWNERS`,
  `.gitattributes`, контент паков вне `_Ring/`, `client/ProjectSettings/**`
  кроме того, что правят бутстрапы. **`client/Assets/Data/*.asset` руками не
  редактировать** — доставка только через `StageOneSceneBootstrap` (Т9/Т16).
- **Simulation меняется** — строго TDD (CR 2), без UnityEngine (CR 1).
- **Два источника чисел** (спека §0, Р56): `.asset` — числа игры; C#-дефолты и
  `TestConfigs` — числа тестов. **Ожидания в тестах — только фикстурными
  выражениями**; где нужна конкретная арифметика — явная фикстура В ТЕСТЕ
  (конвенция `app-n6g` C14). Литерал из `.asset` в тесте = находка ревью.
- **Орфография идентификаторов — американская** (в репо `Sanitize`,
  `Initialize`): `Quantize`, `Serialize`, `Prioritizes`. Британские формы —
  находка ревью.
- **Имена файлов asmdef — по конвенции репо** (`Simulation.asmdef` с
  `"name": "Ring.Simulation"`): новые — `Networking/Networking.asmdef`,
  `Server/Server.asmdef`.
- **ГЕЙТ-ОТКАТ (после КАЖДОГО Unity-прогона):**
  `git status --porcelain -- client/Packages client/Assets/Settings
  .gitattributes client/ProjectSettings "client/Assets/TextMesh Pro"` → дифф
  `client/Packages/**` допустим **только в Т1** (FishNet); дифф
  `client/Assets/Plugins/**` — только в Т54 (MetaVoiceChat); иной дрифт → `git checkout -- <пути>`; TMP-самопис
  откатывать всегда (урок 32).
  **Два отклонения по `client/ProjectSettings/**` приняты и откату не подлежат**
  (внесены фикс-волной Ф1; до неё они были записаны только в телах коммитов):
  - `ProjectSettings.asset` — символы `FISHNET;FISHNET_V4` в
    `scriptingDefineSymbols` **пишет сам пакет** при импорте (Т1). Откат сломал
    бы компиляцию FishNet.
  - `Physics2DSettings.asset` — разовая пересериализация Unity 6.3
    (`serializedVersion 4 → 11`, `m_VelocityThreshold → m_BounceThreshold`,
    `m_AutoSimulation: 1 → m_SimulationMode: 0` — семантически то же), вызвана
    сохранением настроек при первом `Apply` бутстрапа (Т3), а не нашими
    правками; Physics2D в проекте не используется. Принята один раз, иначе
    повторялась бы на каждом R-APPLY и R-IDEM никогда не сошёлся бы.
- **ГЕЙТ-ЛОГ (после каждого batchmode):** `grep -E "error CS|Shader error|
  Failed to import|Error while importing|NullReferenceException|Exception"
  <лог>` → пусто (кроме явно ожидаемых таском строк).
- **ГЕЙТ-META:** каждому новому не-`.meta` файлу **и папке** соответствует
  `<path>.meta`; генерятся ближайшим Unity-прогоном; несопоставленный → стоп.
  (`Scripts/Networking/` и `Scripts/Server/` уже существуют пустыми с `.meta` —
  новые только подпапки.)
- **RED-дисциплина:** тест не компилируется из-за отсутствующих полей/сигнатур
  → сначала заглушки до КОМПИЛЯЦИИ, затем наблюдаемый FAIL ассерта. Ошибка
  компиляции ≠ RED. **Тест всегда пишется до реализации** — шаг «вынести тело»
  не может стоять перед RED, иначе тест не может упасть.
- **Тест-швы состояния:** канон — `var p = w.Player; p.X = …;
  w.SetPlayerForTest(p);`; для индекса — `w.SetPlayerForTest(int, in
  PlayerState)` (Т4). **Существующие хелперы переиспользуются:**
  `TestEvents.TryFirstOf` (не заводить `FirstOf` вторым сканом),
  `TestWorlds.Saturated/SpawnMobsAt/FireAimed3D/RunUntilProjectilesDie`,
  `TestConfigs.Default/Open/Quiet/RegenFixture`. Новые параметры существующих
  хелперов — **только хвостовыми с умолчанием** (иначе ломаются вызовы Э1).
- **Сверка API — по `client/Library/PackageCache/**`**, не по памяти и не по
  сетевым докам (Context7 недоступен). Для FishNet обязательный список — Т2.
  Русские пояснения из сниппетов при переносе в `.cs` **переводятся на
  английский**.
- **Новые SO:** `[CreateAssetMenu]`, `[Range(min, max)]` с осмысленным верхом,
  дефолты, `OnValidate() => RingDataChanged.Raise()` под `#if UNITY_EDITOR` —
  как во всех существующих `Data/*.cs`; маркер-поле — ПОСЛЕДНЕЕ в классе +
  `// sync-marker key — keep LAST`.
- **Словарь ADR-003 §9 + A1:** проза и UI — «Буст», «голова/тело/ноги»,
  «сборщик», «носитель», «цикл/заход»; `Stamina`/`HitZone`/`Player` — только
  код. Комментарии `.cs` — английские (урок 44); свип кириллицы — пункт каждого
  фазового ревью.
- **bd:** сабтаски фаз создаются ДО Т1 (раздел «Декомпозиция bd» в конце);
  клейм сабтаска на старте фазы, `bd note app-5nu` после каждого таска,
  `bd close` сабтаска с evidence; jsonl-дрифт —
  `chore(app-5nu): jsonl-дрифт beads — Фаза ФN` из `$APP_REPO` в main.
- **Коммиты:** `feat|test|fix|refactor|chore|docs(app-5nu): …` (рус.) + трейлер
  фактической модели; перед каждым — секрет-чек
  `git status --short --untracked-files=all | grep -E '\.(env|pem|key)$|secrets/'`
  → пусто. **Токен Docker Hub в git не попадает** (используется в Т51).
- batchmode не гонять при открытом Editor'е владельца
  (`ps aux | grep -i "[U]nity/Hub/Editor.*projectpath"`); перед прогоном —
  проверка/удаление stale `client/Temp/UnityLockfile` (урок 39); запуск —
  `timeout -k 30 900` foreground.

## Runbook

- **R-TEST:** `cd "$WT" && timeout -k 30 900 "$UNITY" -runTests -batchmode
  -projectPath client -testPlatform EditMode -testResults "$SCRATCH/t.xml"
  -logFile "$SCRATCH/t.log"; echo EXIT=$?` → EXIT=0, в xml `failed="0"`
  (БЕЗ `-quit`) + ГЕЙТ-ОТКАТ. `total` растёт по фазам (старт 189).
- **R-FILTER `<Класс>`:** R-TEST + `-testFilter "Ring.Simulation.Tests.<Класс>"`.
- **R-COMPILE:** `cd "$WT" && timeout -k 30 900 "$UNITY" -batchmode -quit
  -projectPath client -logFile "$SCRATCH/c.log"; echo EXIT=$?` → EXIT=0 +
  ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-APPLY-`<X>`:** `cd "$WT" && timeout -k 30 900 "$UNITY" -batchmode -quit
  -projectPath client -executeMethod Ring.Editor.<X>.Apply -logFile
  "$SCRATCH/apply-<X>.log"; echo EXIT=$?` (X ∈ `StageOneSceneBootstrap` |
  `StageTwoSceneBootstrap`) → EXIT=0 + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-IDEM:** повторный R-APPLY → `git status --porcelain -- client/` и
  `git diff -- client/` пусты (мерить ПОСЛЕ коммита артефактов — урок А6).
- **R-GOLDEN (перепин):** R-FILTER `DeterminismTests` → из xml взять
  `But was: <N>` → вписать hex-константу + **обновить десятичный дубль и
  однострочное обоснование перепина** → повторный R-FILTER → PASS. Старый хеш
  `0x760AEB00D11301C4UL`. **Разрешён только в Т10 и Т16.**
- **R-BUILD-`<X>`:** `cd "$WT" && RING_BUILD_ROOT="$SCRATCH/builds"
  timeout -k 30 900 "$UNITY" -batchmode -quit -projectPath client
  -executeMethod Ring.Editor.BuildCommands.Build<X> -logFile "$SCRATCH/b<X>.log";
  echo EXIT=$?` (X ∈ `LinuxServer` | `LinuxClient` | `WindowsClient`).
- **R-DOCKER (Т47):** `cd "$WT" && docker build -f client/docker/Dockerfile
  -t ring-server:dev "$SCRATCH/builds/LinuxServer"` → образ собран.
- **R-IMAGE (с Т51):** `cd "$WT" && client/docker/build.sh [--no-push]` →
  в выводе размер и sha.
- **R-CONTAINER:** `docker run --rm --cpus=1 --memory=1g -e
  RING_MATCH_CONFIG_JSON="$(cat "$SCRATCH/match.json")" -p 7777:7777/udp
  ring-server:dev` → в stdout строка старта матча, порт слушается.
- **R-COMMIT:** секрет-чек → ГЕЙТ-META → `git add <файлы+meta> && git commit
  -m "<msg>" -m "<трейлер>"`.

---

## Фаза Ф1 — вертикальный спайк сети (Т1–Т3)

Цель — снять риск Р-А до массы работ. Мультиплеера, стен и протокола ещё нет.

### Task Т1: FishNet в проект (UPM git-URL с пином)

**Files:** Modify `client/Packages/manifest.json`.

- [x] **Step 1:** проверить тег **не по памяти**:
  `timeout 30 git ls-remote --tags https://github.com/FirstGearGames/FishNet.git
  | grep -E "v?4\.7" | tail -5`.
- [x] **Step 2:** добавить зависимость
  `"<имя из package.json пакета>": "https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet#<тег>"`.
- [x] **Step 3:** R-COMPILE → EXIT=0, ГЕЙТ-ЛОГ пуст;
  `ls client/Library/PackageCache | grep -i fishnet`;
  `grep -i fishnet client/Packages/packages-lock.json` → пин зафиксирован.
- [x] **Step 4:** R-TEST → **189/189**.
- [x] **Step 5:** R-COMMIT `chore(app-5nu): Т1 — FishNet 4.7.2 через UPM-пин`.

### Task Т2: сверка API FishNet по PackageCache (документ-разведка)

**Files:** Create `docs/superpowers/notes/2026-08-04-fishnet-api-notes.md`.

**Interfaces:** письменные ответы на **девять** вопросов с путём и строкой в
`PackageCache`. От них зависят Т29–Т37.

- [x] **Step 1:** выписать:
  1. `IBroadcast`: сигнатуры `ServerManager.Broadcast(NetworkConnection, T,
     bool, Channel)` / `ClientManager.Broadcast(T, Channel)`,
     `RegisterBroadcast<T>`/`UnregisterBroadcast<T>` и типы делегатов;
  2. поддержка `System.ArraySegment<byte>` в writer/reader без аллокаций;
  3. **MTU транспорта Tugboat** и поведение при превышении на `Unreliable`;
  4. `IReplicateData`/`IReconcileData`: обязательные члены, сигнатуры
     `[Replicate]`/`[Reconcile]`, значения `ReplicateState`;
  5. **избыточность инпутов**: где задаётся число прошлых инпутов и дефолт (Р24);
  6. `TimeManager`: `TickRate`, `LocalTick`, `OnTick`/`OnPostTick`, как
     гарантировать «наш обработчик последний»;
  7. graphical smoothing owner-объекта (Р78) и рекомендация по
     `Application.targetFrameRate` для серверной сборки (Р63);
  8. **latency simulator**: где живут поля, можно ли задавать их из кода и как
     гарантировать выключение в релизной сборке (нужно Т33);
  9. требуется ли тестовому asmdef ссылка на `FishNet.Runtime` для типов,
     реализующих `IBroadcast` (риск CS0012 в тестах Т29–Т31).
- [x] **Step 2:** зафиксировать **расхождения с текстом спеки** — каждое либо
  правка спеки отдельным `docs`-коммитом, либо находка владельцу.
- [x] **Step 3:** R-COMMIT `docs(app-5nu): Т2 — сверка API FishNet по PackageCache`.

### Task Т3: вертикальный спайк — один игрок, предсказание движения (opus)

**Files:**
- Create: `client/Assets/Scripts/Networking/Networking.asmdef` (+ `.meta`),
  `.../Networking/Spike/SpikePlayerController.cs` (+ `.meta`),
  `.../Networking/Spike/SpikeBootstrap.cs` (+ `.meta`),
  `client/Assets/Scenes/NetSpike.unity` (+ `.meta`)
- Modify: `client/Assets/Tests/EditMode/Simulation.Tests.asmdef` (+`Ring.Networking`
  **и `FishNet.Runtime` — Т2 п.9 ответил утвердительно: `IBroadcast` компилируется
  именно в сборку `FishNet.Runtime`, без ссылки тесты дадут CS0012**)

**Interfaces:**
- `Networking.asmdef`: `"name": "Ring.Networking"`, `"references":
  ["Ring.Simulation", "Ring.Data", "FishNet.Runtime", "Unity.Mathematics"]`.
- Спайк — **временный код**: `Spike/`, сцена `NetSpike.unity`, префаб
  `SpikePlayer.prefab`, `Editor/SpikeSceneBootstrap.cs` и временный шов в
  `PlayerMovementSystem` **удаляются в Т30** — вместе с появлением
  `PlayerPrediction.Step`, который их и замещает. (**Исправление v3:** здесь
  стояло «в Т26», что противоречило Files Т30; удаление в Т26 оставило бы фазу
  Ф7 без спайка при ещё ненаписанном предсказании.) Внутри `Spike/` разрешено
  срезать углы (нет фильтра, снапшота, мультиплеера) — это разведка; правило
  «не срезать» снято письменно и только здесь. Сцена в `EditorBuildSettings`
  не регистрируется.
- **Фактический состав Т3 шире списка Files выше** (зафиксировано при
  исполнении): сцены в этом проекте строятся только бутстрапом (критическое
  правило), поэтому добавляются `client/Assets/Scripts/Editor/SpikeSceneBootstrap.cs`
  с идемпотентным `Apply()` и правка `Editor/Editor.asmdef` (+`Ring.Networking`,
  `FishNet.Runtime`) — без неё бутстрап не компилируется; бутстрап порождает
  артефакты `client/Assets/Scenes/NetSpike.unity` и
  `client/Assets/Prefabs/SpikePlayer.prefab` (префаб обязан быть отдельным
  ассетом, иначе `PlayerSpawner` его не заспавнит), а FishNet-постпроцессор
  дописывает префаб в уже закоммиченный `DefaultPrefabObjects.asset`.
  Фикс-волной Ф1 добавлен ещё и `.../Networking/Protocol/MathCodegenSupport.cs`
  (+ `.meta`) — **единственный файл Т3, который НЕ временный**: обход поломки
  кодогенерации FishNet на `Unity.Mathematics` (спека Р110), живёт до конца
  проекта, в Т30 из него уходит только спайковый метод.
- **Ввод спайка — скриптованный, а не ручной.** Проект Input-System-only, а
  ссылки `Ring.Networking` запинены спекой §3.1 четвёркой без `Unity.InputSystem`;
  тянуть его в постоянный asmdef ради временного спайка — расширение
  архитектурного решения. Инпут подаётся детерминированной траекторией через
  `SetPendingInput` (тот самый шов, который штатно заводит Т34), поэтому четыре
  наблюдения снимаются **воспроизводимо**. «Пощупать руками» — задача вехи В1,
  а не Т3.

- **Waiver на правку `Ring.Simulation` без TDD** (внесён фикс-волной Ф1).
  Global Constraint «Simulation меняется — строго TDD (CR 2)» исключений не
  имеет, а шов `PlayerMovementSpikeSeam` (`PlayerMovementSystem.cs:371-387`)
  добавлен без теста. Отклонение принято **явно и только здесь**, потому что:
  шов — дословный проброс (`=> PlayerMovementSystem.Update(...)`) ради
  видимости `internal`-типа из `Ring.Networking`; ни новой логики, ни нового
  состояния, ни изменения порядка операций; сдвинуть golden ему нечем (прогон
  подтвердил `0x760AEB00D11301C4`); он удаляется в Т30 вместе с приходом
  `PlayerPrediction.Step`, который приходит **с** тестом паритета
  (`PredictionParityTests`, Т30 Step 2). Любой другой заход в `Simulation/` на
  этом этапе — по общему правилу, RED → GREEN.
- **Как поднять второго участника** (без него три наблюдения из четырёх не
  снимаются — см. ниже): вторая копия проекта во **втором Unity-редакторе**
  (отдельный worktree/каталог — двух редакторов на одном `client/` не бывает,
  мешает `Temp/UnityLockfile`). Сцена спайка закоммичена, поэтому во второй
  копии её достаточно открыть. Пакет Multiplayer Play Mode/ParrelSync **не
  ставится** — это сторонний пакет, а он требует записи в ADR-002 §1.
  Разбор командной строки и режимы `Manual`/`ServerOnly` из первой редакции
  спайка **удалены**: сцена сознательно вне `EditorBuildSettings`, ни один билд
  её не грузит, а в редакторе `Environment.GetCommandLineArgs()` возвращает
  аргументы редактора — код был недостижим по построению. Остались
  `StartMode.Host` и `StartMode.Client` — обе достижимы через инспектор.

- [x] **Step 1:** asmdef + сцена-песочница (с `.meta` в коммите).
- [x] **Step 2:** `SpikePlayerController : NetworkBehaviour` — `[Replicate]`
  двигает копию `PlayerState` через временный публичный шов; `[Reconcile]`
  принимает `PlayerState`; серверная ветка пишет инпут.
- [x] **Step 3 (ручная проверка, за владельцем):** два процесса —
  редактор-**Host** и второй редактор-**Client** (`SpikeBootstrap.StartMode`).
  Симулятор включается **руками в инспекторе** `NetworkManager/TransportManager`
  (постоянный код — Т33): **`Latency = 40`, `Packet Loss = 0.05`** — 40 мс,
  потому что значение применяется **к каждому направлению** (спека Р107), то
  есть 80 мс RTT из CR 7 = `Latency 40`; 5% в поле = 9.75% круговых потерь.
  **Включать в ОБОИХ процессах:** симулятор трогает только **исходящие** пакеты
  своего процесса (`TransportManager.cs:697` — состояние к клиенту, `:772` —
  инпут к серверу), поэтому включённый только на хосте даст задержку вниз и
  ноль вверх, то есть половину заявленного RTT и потери только у снапшотов.
  **С какой роли что читается** (спека Р109 — на объекте владельца FishNet
  всегда зовёт делегат ровно один раз со `Ticked | Created`, очередь и
  голодание живут только в `Replicate_NonAuthoritative`):
  - **(а) резинка и (г) тик `ReconcileData`** — строки `[local owner]` на
    **клиентском** процессе (на хосте реконсиляция пользовательским телом не
    исполняется вовсе, строки покажут нули и подпись роли об этом скажет);
  - **(б) таблица `ReplicateState` и (в) два `[Replicate]` на серверный тик** —
    строки `[server-side, non-owner]` на **хост**-процессе.
  Оверлей печатает обе группы и пишет `n/a`, если подходящего экземпляра нет —
  ноль вместо «нечего мерить» больше не выводится.
  **Оговорка к (б):** при 5% односторонних потерь и `RedundancyCount = 3`
  (`StateInterpolation 2 + 1`) вероятность потерять все три копии одного инпута
  ≈ `0.05³ = 0.0125%` за тик; при 30 Гц это ≈ 0.0038 события в секунду, то есть
  **одно голодание примерно раз в 4.5 минуты** — редкое событие, за пару минут
  наблюдения его может не случиться ни разу. Чтобы увидеть явление, а не зафиксировать
  «потерь нет» как факт о FishNet, на время наблюдения поднять `Packet Loss` до
  **0.35–0.40** либо выставить `PredictionManager.StateInterpolation = 0`
  (тогда `RedundancyCount = 1` и голодание идёт с частотой самих потерь).
  Записать в bd note: (а) резинка; (б) `ReplicateState` при потере инпута;
  (в) поведение при двух `[Replicate]` на серверный тик; (г) какой тик несёт
  `ReconcileData` — с указанием роли и параметров симулятора для каждого числа.
- [x] **Step 4:** ответы (а)–(г) — в заметку Т2 (после Step 3).
- [x] **Step 5:** R-COMPILE + R-TEST → 189/189.
- [x] **Step 6:** R-COMMIT `feat(app-5nu): Т3 — вертикальный спайк предсказания`.

**Гейт фазы Ф1:** R-TEST 189/189; заметка Т2 — девять ответов и четыре
наблюдения; push; jsonl-chore; bd note; `bd close` сабтаска Ф1.
**Стоп-условие:** модель «инпут → общий мир → реконсиляция из мира» несовместима
с конвейером FishNet → стоп и разбор с владельцем, не обход в коде.

---

## Фаза Ф2 — состав состояния мира (Т4–Т10) → перепин golden №1

### Task Т4: массив игроков и `TickAll(inputs)`

**Files:**
- Modify: `.../Simulation/Core/SimulationWorld.cs`, `.../Core/WorldSave.cs`,
  `.../Core/RenderSnapshot.cs`, `.../Core/SimConfig.cs`,
  `client/Assets/Scripts/Data/ArenaConfig.cs`, `.../Data/SimConfigBuilder.cs`,
  **`client/Assets/Scripts/Presentation/SimulationRunner.cs`** (приватный
  `CopySnapshot` — новые поля снапшота), `client/Assets/Tests/EditMode/TestConfigs.cs`
- Create: `client/Assets/Tests/EditMode/MultiPlayerWorldTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
public SimulationWorld(long seed, in SimConfig config, int playerCount = 1);
public void TickAll(System.ReadOnlySpan<SimInput> inputs);  // канонический
public void Tick(in SimInput input);                        // соло; при playerCount > 1
                                                            // -> InvalidOperationException
public PlayerState PlayerAt(int index);
public int PlayerCount { get; }
public PlayerState Player => PlayerAt(0);
internal void SetPlayerForTest(int index, in PlayerState p);
```

- **Имя `TickAll`, а не перегрузка `Tick`** — критично: пара
  `Tick(in SimInput)` + `Tick(ReadOnlySpan<SimInput>)` делает **43 существующих
  вызова `w.Tick(default)` неоднозначными** (CS0121), и компиляция упала бы
  раньше RED-фазы (проверено grep'ом).
- Порядок тика **не меняется**: движение всех игроков по возрастанию индекса →
  оружие всех → мобы → сепарация → снаряды → волны.
- Спавн (Р12): `playerCount == 1` → `(0,0)`; иначе угол `i·2π/playerCount`
  **без поворота от seed**, радиус `Arena.Radius * PlayerSpawnRingFrac`, затем
  `Geometry.Depenetrate`.
- `ArenaConfig`: `MaxPlayers [Range(1,3)] = 3`,
  `PlayerSpawnRingFrac [Range(0.1f,0.95f)] = 0.8f` — **последним полем +
  `// sync-marker key — keep LAST`** (маркер у `ArenaConfig` заводится впервые;
  доставка — Т9).
- `RenderSnapshot`: `Players` + `PlayerCount` + `LocalPlayerIndex`;
  `CaptureSnapshot` и `SimulationRunner.CopySnapshot` переводятся на копирование
  массива — новые поля перечисляются явно.

- [ ] **Step 1 (RED):** `MultiPlayerWorldTests.cs`:

```csharp
[Test]
public void ThreePlayers_MoveIndependently()
{
    var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
    var inputs = new SimInput[3];
    inputs[0] = new SimInput { MoveDir = new float2(1f, 0f) };
    inputs[1] = new SimInput { MoveDir = new float2(-1f, 0f) };
    var b0 = w.PlayerAt(0).Pos; var b1 = w.PlayerAt(1).Pos; var b2 = w.PlayerAt(2).Pos;
    for (int t = 0; t < 10; t++) w.TickAll(inputs);
    Assert.Greater(w.PlayerAt(0).Pos.x, b0.x);
    Assert.Less(w.PlayerAt(1).Pos.x, b1.x);
    Assert.AreEqual(b2.x, w.PlayerAt(2).Pos.x, 1e-4f);
}

[Test] public void SoloOverload_ThrowsWhenMultiplayer() { /* InvalidOperationException */ }
[Test] public void SoloSpawnsAtOrigin_MultiplayerSpawnsOnRing() { /* фикстурно от PlayerSpawnRingFrac */ }
[Test] public void SpawnRing_DoesNotDependOnSeed() { /* seed 1 и 999 — те же позиции */ }
[Test] public void CanonicalTickOrder_MovementBeforeWeapon() { /* выстрел игрока 0 видит
    позицию игрока 1 ЭТОГО тика, а не прошлого */ }
```

- [ ] **Step 2:** заглушки → R-FILTER `MultiPlayerWorldTests` → **FAIL ассертов**.
- [ ] **Step 3 (GREEN):** реализация; санитизация по индексу; `WorldSave`/
  `RenderSnapshot`/`CaptureSnapshot`/`CopySnapshot` расширены. **Хеш не
  трогаем** (Т10).
- [ ] **Step 4:** R-FILTER `MultiPlayerWorldTests`+`WorldLifecycleTests`+
  `DeterminismTests` → PASS (**golden не изменился**); R-TEST → 189 + 5.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т4 — массив игроков и TickAll` (+ `.meta`).

### Task Т5: разделение статистики (`MatchStats[]` + `WorldStats`)

**Files:**
- Modify: `.../Core/SimStates.cs`, `.../Core/SimulationWorld.cs`,
  `.../Core/WorldSave.cs`, `.../Core/RenderSnapshot.cs`, `.../AI/WaveSystem.cs`,
  **`.../Combat/WeaponSystem.cs`** (единственный писатель `ShotsFired` — `:48`
  `ref MatchStats stats = ref w.StatsRef`, `:96` `stats.ShotsFired++`; без него
  RED-тест `PersonalStats_DoNotMix` не позеленеет),
  **`.../Presentation/SimulationRunner.cs`** (`CopySnapshot`),
  `.../Presentation/DevOverlay.cs`,
  **`.../Presentation/DeathOverlayController.cs`** (`:121` `stats.WavesCleared`),
  **`client/Assets/Scripts/Editor/LongRunHarness.cs`** (`:63` заголовок CSV,
  `:77` `stats.MobSpawnsSkipped`/`ProjectileSpawnsSkipped`)
- Modify: `MultiPlayerWorldTests.cs`, `WaveTests.cs`, `TestWorlds.cs`,
  **`WeaponTests.cs`** (`:117` `w2.Stats.ProjectileSpawnsSkipped`)

**Аудит перед имплементацией (2026-08-05):** четыре последних файла в v2 отсутствовали —
перенос трёх счётчиков в `WorldStats` давал три ошибки CS вне списка правок, и шаг
GREEN не скомпилировался бы.

**Interfaces:**

```csharp
public struct MatchStats  // ПЕРСОНАЛЬНЫЕ: Kills, HeadshotKills, ShotsFired, ShotsHit,
{ … }                     // DashesUsed, SlidesUsed, DamageTaken, DeathTick
public struct WorldStats  // МИРОВЫЕ
{ public int WavesCleared, MobSpawnsSkipped, ProjectileSpawnsSkipped; }

public MatchStats StatsAt(int index);
public WorldStats WorldStats => _worldStats;   // свойство с именем типа — легально
public MatchStats Stats => StatsAt(0);
internal void SetStatsForTest(int index, in MatchStats s);
```

- **Хеш обязан остаться прежним.** Сегодня `HashStats` (`SimulationWorld.cs`,
  строки 632/636/637) хеширует `WavesCleared`, `MobSpawnsSkipped`,
  `ProjectileSpawnsSkipped` — после переноса полей он либо не скомпилируется,
  либо (если поля выкинуть) сдвинет golden в Т5, что запрещено инвариантом.
  Решение: `HashStats` **временно** хеширует эти три счётчика из нового дома
  **в прежнем порядке байтов**, с комментарием
  `// temporary: world counters keep their T5 hash position until the T10 reorder`.
- Гварды `IncrementKills`/`IncrementShotsHit`/`IncrementHeadshotKills` получают
  индекс **стрелявшего**; гвард `Alive` — по этому игроку.
- `TestWorlds.ClearFirstWave(world)` — новый хелпер рядом с существующими.

- [ ] **Step 1 (RED):** `PersonalStats_DoNotMix`;
  `WorldStats_CountedOnce_NotPerPlayer` (**зачистить волну втроём →
  `WavesCleared == 1`**, а не тавтологичное `≥ 0`);
  `DeathOfOne_DoesNotFreezeOthersStats`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)** + временный `HashStats`.
- [ ] **Step 4:** R-FILTER `MultiPlayerWorldTests`+`WaveTests`+`DeathTests`+
  **`DeterminismTests`** → PASS, **golden прежний**; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т5 — персональная и мировая статистика`.

### Task Т6: `SimInputSanitizer` — публичный шов санитизации

**Files:** Create `.../Core/SimInputSanitizer.cs` (+ `.meta`);
Modify `.../Core/SimulationWorld.cs`, `DeterminismTests.cs`.

**Interfaces:** `public static SimInput Sanitize(in SimInput raw, in PlayerState
reference, in SimConfig cfg)` — **дословный перенос** приватного `Sanitize`;
формулы не меняются.

- [ ] **Step 1 (BASELINE):** R-FILTER `DeterminismTests` → зелёный.
- [ ] **Step 2 (RED):** завести `SimInputSanitizer.Sanitize` **заглушкой
  `=> default`** и написать `Sanitizer_MatchesWorldBehaviour` (пять враждебных
  входов) → R-FILTER → **FAIL** (RED честный, тест до реализации).
- [ ] **Step 3 (GREEN):** перенести тело; `SimulationWorld.Sanitize(raw, index)`
  → вызов шва; `SanitizeForTest` — тонкая обёртка.
- [ ] **Step 4:** R-FILTER `DeterminismTests` → PASS, **golden не изменился**;
  R-TEST.
- [ ] **Step 5:** R-COMMIT `refactor(app-5nu): Т6 — санитизация публичным швом`.

### Task Т7: `OwnerIndex` снаряда и `SimEvent.PlayerIndex`

**Files:** Modify `.../Core/SimStates.cs`, `.../Core/SimEvents.cs`,
`.../Core/SimulationWorld.cs`, `.../Combat/WeaponSystem.cs`, `.../AI/MobAiSystem.cs`,
`EventTests.cs`, `ProjectileTests.cs`, **`WorldLifecycleTests.cs`**.

**Аудит перед имплементацией (решение владельца F2a):** существующий рефлексивный
`EveryPlayerAndStatsFieldAffectsHash` (`WorldLifecycleTests.cs:36–101`) ассертит
вхождение **каждого** поля `ProjectileState` в хеш, а `OwnerIndex` входит туда только
в Т10 — тест упал бы здесь. Плюс `Bump` (`:103–116`) не знает `byte` и бросил бы
`NotSupportedException`. Решение — **тот же санкционированный паттерн, что у
временного `HashStats` в Т5**: именованный временный skip-list, снимаемый в Т10.

**Interfaces:**

```csharp
public struct ProjectileState { … public byte OwnerIndex; }
public static class ProjectileIds { public const byte NoOwner = byte.MaxValue; }
public struct SimEvent { … public byte PlayerIndex; }   // поле события, не состояния

internal void Emit(…, byte playerIndex = ProjectileIds.NoOwner);  // последним optional
internal int SpawnProjectile(ProjectileOwner owner, byte ownerIndex, …);
```

- `ProjectileFired` уже несёт id снаряда в `EntityId` — поле занято, отсюда
  нужда в отдельном. `OwnerIndex` входит в хеш **в Т10**.
- Тесты используют существующий **`TestEvents.TryFirstOf`**.

- [ ] **Step 1 (RED):** `PlayerEvents_CarryPlayerIndex`,
  `MobProjectile_HasNoOwnerIndex`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 3a:** в `WorldLifecycleTests` — ветка `byte b => (byte)(b + 1)` в `Bump`
  (постоянная, тип обязан поддерживаться) и **временный** skip-list:

```csharp
// TEMPORARY (T7 -> T10): OwnerIndex enters the hash in T10 together with the
// canonical field order and the sanctioned golden re-pin. Until then the
// reflective sweep would assert on a field that is deliberately not hashed yet.
// T10 removes this set and proves the removal (see its Step 3b).
static readonly System.Collections.Generic.HashSet<string> PendingHashFields = new() { "OwnerIndex" };
```

- [ ] **Step 4:** R-FILTER `EventTests`+`ProjectileTests`+**`WorldLifecycleTests`** →
  PASS; R-TEST → **golden не изменился**.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т7 — владелец снаряда и индекс игрока
  в событиях`.

### Task Т8: общий `KillPlayer`, `NearestAlivePlayer`, поле конфига краевых запросов

**Files:** Modify `.../Core/SimulationWorld.cs`, `.../AI/Targeting.cs`,
`.../AI/MobAiSystem.cs`, `.../AI/WaveSystem.cs`,
`client/Assets/Scripts/Data/HeroConfig.cs`, `SimConfig.cs`, `SimConfigBuilder.cs`;
Modify `MultiPlayerWorldTests.cs`, `MobAiTests.cs`.

**Аудит перед имплементацией (решение владельца F1a) — ГЕЙТ КРАЕВЫХ ЗАПРОСОВ
ПЕРЕЕХАЛ В Т10.** Причина: `HashPlayer` хеширует `DashBufferTimer`
(`SimulationWorld.cs:588`) и `SlideBufferTimer` (`:595`), а латч буфера армируется
**сырым** `input.DashRequested` (`PlayerMovementSystem.cs:40–42`, `:51–53`).
Golden-сценарий подаёт краевые запросы с вероятностью 0.05/тик на 1000 тиков
(`DeterminismTests.cs:43–44`) — пара запросов внутри окна гейта в 3 тика
практически неизбежна (P(ни одной) ≈ 0.7 %). Подавляет гейт латч — хеш расходится
на первом же отброшенном запросе, то есть golden уехал бы **в Т8**; не подавляет —
гейт бессилен под спамом, потому что `DashBufferWindow` = 0.15 с = 4.5 тика
**шире** окна гейта. Обоснование hash-нейтральности из v2 («меняется только частота
нехешируемого `StaminaDenied`») **неверно** и снято. Инвариант «перепинов ровно два»
сохраняется: гейт исполняется в Т10, где сдвиг хеша санкционирован.

**Здесь остаётся только hash-нейтральное.**

**Interfaces:**

```csharp
public static bool NearestAlivePlayer(SimulationWorld w, float2 from, out int index);
// false + index = -1 при нуле живых; тай-брейк — меньший индекс

void KillPlayer(int index, HitZone zone, float2 dir);   // приватный: ЕДИНСТВЕННЫЙ дом
                                                        // обнуления таймеров + DeathTick + PlayerDied
public void KillPlayerNoDamage(int index);              // зовёт KillPlayer, без урона/статистики
// ветка смерти в DamagePlayer тоже зовёт KillPlayer — список таймеров существует один раз

// Данные-только, поведения ещё нет (потребитель — гейт в Т10). Хеш-нейтрально:
// SimConfig в StateHash не входит, SimConfigHash появляется лишь в Т23.
// HeroConfig.EdgeRequestMinTicks [Range(0,15)] = 3 — маркер ПЕРЕЕЗЖАЕТ с LinkRefund
// (фактический маркер сегодня — HeroConfig.cs:80, бутстрап StageOneSceneBootstrap.cs:417);
// доставка ключа — Т9, поэтому поле обязано появиться здесь, до Т9.
```

- **`NearestAlivePlayer` — правка по call-site, а не по функции с таким именем**
  (аудит): `Targeting.SwingLead` не существует; прогноз замаха собран в
  `MobAiSystem.cs:96–97` (`cfg.SwingLeadFactor`, `SwingLeadMaxMeters`,
  `w.Config.Hero.MaxSpeed`) из копии `in PlayerState`, взятой в `MobAiSystem.cs:34`.
  В `Targeting.cs` есть `AimWithLead` (`:9`), `PredictPos` (`:46`),
  `HasLineOfFire` (`:59`).
- Ветка «цели нет» уже существует и переиспользуется, а не пишется заново
  (правило 2): `WaveSystem.cs:22` (`if (!w.Player.Alive) return;`),
  `MobAiSystem.cs:36–43` (`!player.Alive` → `Idle` + затухание).

- [ ] **Step 1 (RED):** в `MultiPlayerWorldTests` — `KillPlayerNoDamage`
  (`Alive == false`, `DamageTaken` не изменился, ровно одно `PlayerDied`, зачёт
  никому; снаряды покинувшего наносят урон); в `MobAiTests` —
  `NearestAlivePlayer` при нуле живых → мобы в `Idle`; смена цели при смерти.
- [ ] **Step 2:** заглушки → R-FILTER → FAIL.
- [ ] **Step 3 (GREEN):** реализация; `HeroConfig.EdgeRequestMinTicks` +
  `HeroSimConfig.EdgeRequestMinTicks` + проводка в `SimConfigBuilder` + валидация
  `EdgeRequestMinTicks ≥ 0` — **поле объявлено, гейта ещё нет**.
- [ ] **Step 4:** R-FILTER затронутых → PASS; R-TEST (**golden не тронут** —
  поехал, значит объявление поля конфига что-то задело, **стоп**).
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т8 — выход игрока, ближайшая живая цель,
  поле лимита краевых запросов`.

### Task Т9: доставка новых ключей в `.asset` (бутстрап)

**Files:** Modify `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`.

**Interfaces:**
- **Отдельный таск, потому что механизм не автоматический:** вызовы
  `EditorBootstrapUtils.EnsureAssetHasKey` захардкожены пофайлово (строки
  417–421), и для `ArenaConfig` их сегодня нет вовсе. Без этого таска новые поля
  Т4/Т8 не доедут до `.asset`, а фазы Ф3+ работали бы на старых числах при
  зелёных тестах.
- Добавить: `ArenaConfig` → маркер `PlayerSpawnRingFrac`; `HeroConfig` → маркер
  переезжает на `EdgeRequestMinTicks`.
- **`EnsureAssetHasKey` доставляет только ОТСУТСТВУЮЩИЕ ключи и не переписывает
  существующие значения** (подтверждено аудитом: `EditorBootstrapUtils.cs:251–255`
  — тело сводится к `if (!File.ReadAllText(assetPath).Contains(markerField))
  SetDirty(so)`, значения не читаются и не сравниваются) — поэтому здесь же
  заводится каркас `ApplyStageTwoBalance()` (пустой; наполняется в Т16).
- **Аудит перед имплементацией (решение владельца F3a) — образец берётся ТОЛЬКО
  по телу, не по вызову.** `ApplyGunnerZoneDefaults` действительно написан через
  `SetIfDifferent` (`StageOneSceneBootstrap.cs:1548–1560`), но его **вызов
  гейтирован** `(gunnerCreated || !gunnerMarkerPresent)` (`:387`) с явно
  задекларированным контрактом backfill-only («never reapplied unconditionally, so
  an owner hand-tweak of these fields survives a re-run», `:1540–1547`). При
  буквальном копировании вызова Т16 **не перезаписал бы** `Radius 35 → 65`, потому
  что маркер `ArenaConfig` доставлен уже здесь, в Т9 — тесты остались бы зелёными
  (они смотрят на C#-дефолты), а Ф3+ поехала бы на старой арене. Принятая форма:

```csharp
// Body: SetIfDifferent, exactly like ApplyGunnerZoneDefaults.
// Call gate: NOT the backfill marker. Stage-2 balance is delivered ONCE, keyed on
// "walls have not been delivered yet" — after T16 the owner tunes these numbers at
// milestone B1, and no later R-APPLY may stomp that tuning back to the spec values.
// Признак «стены не доставлены» меряется ПО ТЕКСТУ АССЕТА, а не по загруженному
// объекту (поправка фазы Ф2, ревью Т9): отсутствующий в YAML ключ откатывается к
// C#-инициализатору, а Walls[] обязан нести дефолты (иначе падает
// Build_DefaultAssets_MatchesTestConfigsBaseline) — то есть arena.Walls.Length
// в Т16 никогда не ноль, гейт не сработал бы ни разу, и Radius 35 -> 65 не доехал
// бы при зелёных тестах. Тот же приём, что у соседнего gunnerMarkerPresent.
// Снимок берётся ДО блока EnsureAssetHasKey/SaveAssets.
bool stageTwoPending = !System.IO.File
    .ReadAllText($"{DataDir}/ArenaConfig.asset").Contains("Walls:");
// Три флага dirty: MaxMobsPerWave живёт в WaveConfig, а MaxCorpses/MaxCasings/
// MaxDecals — в GameFeelConfig; одного SetDirty(arena) на восемь чисел мало.
arenaChanged |= stageTwoPending
    && ApplyStageTwoBalance(arena, wave, gameFeel, out bool waveDelta, out bool feelDelta);
waveChanged |= waveDelta;
feelChanged |= feelDelta;
```

Фактическая реализация каркаса — `StageOneSceneBootstrap.cs` (заведена Т9,
там же tripwire, роняющий `Apply`, как только `ArenaConfig.Walls` объявлен).
Т16 наполняет тело и снимает tripwire, своего блока доставки не пишет.

  Так одноразовость обеспечена признаком самого этапа, а не маркер-механизмом,
  и правило «тюнинг владельца переживает повторный прогон» не нарушается.

- [ ] **Step 1:** правка бутстрапа; R-APPLY-`StageOneSceneBootstrap`.
- [ ] **Step 2:** `git diff -- client/Assets/Data/` → **только** новые ключи с
  дефолтами; R-IDEM.
- [ ] **Step 3:** R-TEST полный (golden не тронут — числа равны дефолтам).
- [ ] **Step 4:** R-COMMIT `chore(app-5nu): Т9 — доставка ключей этапа 2 в ассеты`.

### Task Т10: рейт-лимит краевых запросов + канонический порядок хеша + **перепин golden №1**

**Files:** Modify `.../Core/SimStates.cs`, `.../Movement/PlayerMovementSystem.cs`,
`.../Core/SimulationWorld.cs`, `.../Core/WorldSave.cs`;
Create `EdgeRateLimitTests.cs` (+ `.meta`);
Modify `DeterminismTests.cs`, `WorldLifecycleTests.cs`, `HotTweakTests.cs`.

**Аудит перед имплементацией (решение владельца F1a/F2a):** таск собирает **всё,
что входит в хеш**, чтобы сдвиг случился ровно один раз. Сюда переехал гейт краевых
запросов из Т8 (обоснование — в шапке Т8) вместе с двумя полями `PlayerState`,
`EdgeRateLimitTests` и правкой существующих тестов; здесь же снимается временный
skip-list рефлексивного теста, заведённый в Т7.

**Interfaces:**

```csharp
public struct PlayerState { … public int DashRequestCooldownTicks, SlideRequestCooldownTicks; }
```

- Порядок хеша: `tick → spreadRng → waveRng → nextEntityId → playerCount →
  players[0..n) → mobCount+mobs → projectileCount+projectiles → wave → worldStats →
  statsCount+stats[0..n)`; `HashPlayer` += оба таймера краевых запросов;
  `HashProjectile` += `OwnerIndex`; `HashStats` теряет временные мировые счётчики
  (Т5), появляется `HashWorldStats`; `EveryPlayerAndStatsFieldAffectsHash`
  расширяется на второго игрока и на `WorldStats`.
- Гейт краевых запросов — **внутри `PlayerMovementSystem.Update`**, таймер **на
  вид** (общий резал бы легальную связку дэш→слайд, Р26). Отброшенный запрос не
  эмитит `StaminaDenied` и не пишет в `MatchStats` (сетевой счётчик — Т23/Т28).
  Гейт **подавляет латч буфера** — именно это и двигает хеш, и именно поэтому таск
  один с перепином.

- [ ] **Step 1 (RED):** `EdgeRateLimitTests.cs` — **фикстура обязана снять
  кулдаун**, иначе тест меряет не гейт, а `DashCooldown` (фактические числа
  `TestConfigs`: `DashCooldown 1.2`, `DashDuration 0.15`, `StaminaMax 100`,
  `DashStaminaCost 40` — пула хватает ровно на два дэша):

```csharp
static SimConfig Fixture()
{
    var cfg = TestConfigs.Open();
    cfg.Hero.EdgeRequestMinTicks = 3;
    cfg.Hero.DashCooldown = 0f;          // иначе 1.2 с блокируют второй дэш
    cfg.Hero.DashDuration = 2f / 30f;    // 2 тика: дэш не перекрывает окно лимита
    cfg.Hero.StaminaMax = 1000f;         // Буст не должен быть ограничителем
    return cfg;
}

[Test]
public void SpammedDash_AcceptedOncePerWindow()
{
    var w = new SimulationWorld(1, Fixture());
    var spam = new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true };
    for (int t = 0; t < 6; t++) w.Tick(spam);
    // тик 0 принят; 1–2 отброшены гейтом; 3 принят (дэш закончился на тике 2); 4–5 отброшены
    Assert.AreEqual(2, w.Stats.DashesUsed);
}

[Test] public void RejectedRequests_AreCounted() { /* счётчик отброшенных — через тест-шов */ }
[Test] public void DashThenSlide_BothAccepted() { /* связку резать нельзя */ }
[Test] public void HonestRhythm_NotThrottled() { /* нажатия реже окна — все приняты */ }
[Test] public void RejectedRequest_DoesNotRearmBuffer() { /* отброшенный запрос НЕ армирует
    DashBufferTimer — иначе гейт бессилен: окно буфера 4.5 тика шире окна гейта 3 */ }
```

- [ ] **Step 2 (RED):** расширить рефлексивный тест на второго игрока и на
  `WorldStats`; добавить `MultiPlayerGoldenHash_ScriptedScenario` (три игрока,
  1000 тиков, независимые вводы от локального `Random` с фиксированным seed),
  константа `0UL`.
- [ ] **Step 3:** заглушки → R-FILTER `EdgeRateLimitTests`+`DeterminismTests` →
  FAIL всех.
- [ ] **Step 3a (GREEN, гейт):** гейт в `PlayerMovementSystem.Update`; `ApplyConfig`
  клампит оба счётчика + строки в карту рефлексивного прохода `HotTweakTests`.
- [ ] **Step 3b (снятие временного skip-list, доказательство):** удалить
  `PendingHashFields` из `WorldLifecycleTests` (Т7) → **временно** вынуть
  `OwnerIndex` и оба таймера из `HashPlayer`/`HashProjectile` → R-FILTER
  `WorldLifecycleTests` обязан **покраснеть** с именем каждого из трёх полей →
  вернуть их в хеш → PASS. Без этого шага снятие skip-list'а не доказано.
- [ ] **Step 4 (GREEN, хеш):** канонический порядок; **R-GOLDEN ×2** — перепин соло
  («Т10 — массив игроков, WorldStats, OwnerIndex, таймеры краевых запросов и гейт
  краевых вошли в хеш») и пин мультиплеерного.
- [ ] **Step 5:** **правка существующих тестов**, спамящих `DashRequested` каждый
  тик: `grep -rn "DashRequested = true" client/Assets/Tests/EditMode/` (аудит: **31
  вхождение в 10 файлах**, истинный спам каждый тик — три места: `DashTests.cs:81–87`,
  `DashRicochetTests.cs:87–94`, `DeterminismTests.cs:149`) → привести ожидания к
  гейту **явно**; каждая правка — строкой в bd note.
- [ ] **Step 6:** R-FILTER `EdgeRateLimitTests`+`DeterminismTests`+
  `WorldLifecycleTests`+`HotTweakTests` → PASS; R-TEST полный; оба хеша — в bd note.
- [ ] **Step 7:** R-COMMIT `feat(app-5nu): Т10 — рейт-лимит краевых запросов и
  состав хеша под мультиплеер (перепин golden №1)`.

**Гейт фазы Ф2:** R-TEST (≈ 189 + 15); **ровно один перепин соло-golden**;
push; jsonl-chore; bd note с обоими хешами; `bd close` сабтаска Ф2.

---

## Фаза Ф3 — геометрия: стены и арена (Т11–Т16) → перепин golden №2

### Task Т11: примитив стадиона в `Geometry`

**Files:** Modify `.../Core/Geometry.cs`, `.../Core/SimConfig.cs`;
Modify `GeometryTests.cs`.

**Interfaces:**

```csharp
public static float2 ClosestPointOnSegment(float2 p, float2 a, float2 b, out float s);
public static bool SegmentStadium(float2 p0, float2 p1, float padR,
    float2 a, float2 b, float halfW, out float t);
public static bool OverlapsStadium(float2 p, float radius, float2 a, float2 b, float halfW);
public static bool PushOutOfStadium(ref float2 pos, float radius,
    float2 a, float2 b, float halfW, out float2 normal);
```

- **`ClosestPointOnSegment` — единственный дом проекции на отрезок:**
  `OverlapsStadium` и `PushOutOfStadium` обязаны идти через него (иначе
  проекционная математика появится в трёх экземплярах); `SegmentStadium` —
  через существующий `SegmentCircle` для торцов + полосу. **`SegmentCircle` не
  трогать** (запинен golden'ом).
- Отрицательный `padR` здесь **не клампится** — кламп делает вызывающая
  `HasLineOfFire` (Т13).
- `ArenaSimConfig` += `WallCount`, `WallA[]`, `WallB[]`, `WallHalfWidth[]`.

- [x] **Step 1 (RED):** `SegmentStadium_HitsFlatSide` (t = (3−0.5)/6),
  `..._HitsRoundedCap`, `..._MissesPastEnd`, `..._StartInside_ReturnsZero`,
  `PushOutOfStadium_NormalPerpendicularToSide`,
  `OverlapsStadium_MatchesSweepAtZeroLength`.
- [x] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [x] **Step 4:** R-FILTER `GeometryTests` → PASS; R-TEST → **golden не
  меняется**.
- [x] **Step 5:** R-COMMIT `feat(app-5nu): Т11 — примитив стены (стадион)`.

### Task Т12: стены в свипе арены и депенетрации

**Files:** Modify `.../Core/Geometry.cs`; Test `GeometryTests.cs`, `MovementTests.cs`.

**Interfaces:** `SweepArena` — круги → **стены** → кольцевая стена;
`Depenetrate` — та же вставка; сигнатуры не меняются.

**Аудит перед имплементацией:** `MoveWithCollisions` живёт **не в `Geometry`**, а в
`.../Movement/PlayerMovementSystem.cs:348`, и именно его поведение проверяет Т15
(collide-and-slide, гашение слайда, рикошет дэша). Правок он не требует — `SweepArena`
зовётся изнутри, — но при красном в Т15 разбор начинается с него, а не только с
Т11–Т14. Hash-нейтральность Т12 подтверждена структурно: `Geometry.cs:172–190`
(`SweepArena`: цикл по `ObstacleCount` → `SegmentRingWall` со строгим `tw < t`) и
`:194–208` (`Depenetrate`), вставленный цикл по стенам при `WallCount == 0`
исполняется ноль раз.

- [x] **Step 1 (RED):** `SweepArena_PrefersNearestAcrossKinds`,
  `Depenetrate_PushesOutOfWall`.
- [x] **Step 2:** FAIL → **Step 3 (GREEN)**.
- [x] **Step 4:** R-FILTER `GeometryTests`+`MovementTests`+`DashTests` → PASS;
  R-TEST → **golden не меняется** (`WallCount == 0`). Поехал — **стоп**.
- [x] **Step 5:** R-COMMIT `feat(app-5nu): Т12 — стены в свипе и депенетрации`.

### Task Т13: LoS со стенами и кламп отрицательного отступа

**Files:** Modify `.../AI/Targeting.cs`; Test `MobAiTests.cs`.

**Interfaces:** `HasLineOfFire` += цикл по стенам + кламп
`circlePad = math.max(padR, -arena.ObstacleRadius[o])`,
`wallPad = math.max(padR, -arena.WallHalfWidth[i])` — иначе отрицательный
отступ консервативной видимости (Т21) превратил бы мелкое препятствие в
фантомное радиуса `|r|`. **Второй LoS-функции не заводится.**

- [x] **Step 1 (RED):** `LineOfFire_BlockedByWall`, `LineOfFire_ClearAlongWall`,
  `LineOfFire_NegativePadClamped` (R = 0.2, `padR = −0.45` → свободно).
- [x] **Step 2:** FAIL → **Step 3 (GREEN)**.
- [x] **Step 4:** R-FILTER `MobAiTests` → PASS; R-TEST → golden не меняется.
- [x] **Step 5:** R-COMMIT `feat(app-5nu): Т13 — линия видимости через стены`.

### Task Т14: стены в ИИ, спавне волн и гварде hot-tweak

**Files:** Modify `.../AI/MobAiSystem.cs`, `.../AI/WaveSystem.cs`,
`.../Core/SimulationWorld.cs`; Test `MobAiTests.cs`, `WaveTests.cs`,
`HotTweakTests.cs`.

**Interfaces:** `SteerAround` += ветка `SegmentStadium` (тот же
`padR = Radius + AvoidMargin`); `IsValidSpawn` += `OverlapsStadium`;
`ArenaTopologyMatches` += стены, `MaxPlayers`, `PlayerSpawnRingFrac`, капы.

**Аудит перед имплементацией:** сегодня `ArenaTopologyMatches`
(`SimulationWorld.cs:201–211`) сравнивает только `Radius`, `ObstacleCount`,
`ObstaclePos[]`, `ObstacleRadius[]`, а единственный тест гварда
(`HotTweakTests.cs:38–42`) тюнит лишь `Radius`. Красного здесь не будет, но после
Т16 (`MaxMobs 64→96`, `MaxProjectiles 256→384`, `MaxEventsPerFrame 256→512` в
`.asset`) hot-tweak с ассетов старого поколения начнёт бросать и уводить
`SimulationRunner` в `Restart` — ожидаемое следствие расширения гварда, а не
регрессия; строкой в bd note.

- [x] **Step 1 (RED):** `Chaser_NavigatesAroundWall`, `Spawn_InsideWall_Rejected`,
  `HotTweak_WallChange_Throws`.
- [x] **Step 2:** FAIL → **Step 3 (GREEN)**.
- [x] **Step 4:** R-FILTER `MobAiTests`+`WaveTests`+`HotTweakTests` → PASS;
  R-TEST → golden не меняется.
- [x] **Step 5:** R-COMMIT `feat(app-5nu): Т14 — стены в обходе, спавне и гварде`.

### Task Т15: механики движения на плоскости (`WallGeometryTests`)

**Files:** Create `client/Assets/Tests/EditMode/WallGeometryTests.cs` (+ `.meta`).

**Interfaces:**
- **Кода не добавляет** — регрессионная сетка для механик, ради которых стены и
  вводились (решение владельца С11). Без неё слайд вдоль боковины, гашение о
  плоскость, рикошет от плоскости и проход через стык проверялись бы только
  руками на вехе, и DoD п.2 не сошёлся бы.
- Конфиг со стенами собирается **в тесте** (`TestConfigs.Open()` + явная
  раскладка), а не из `.asset`; ожидания — фикстурными выражениями.

- [x] **Step 1:** написать пять тестов:
  - `SlideAlongFlatWall_DoesNotAccelerate` — модуль скорости вдоль боковины не
    растёт;
  - `SlideHeadOnIntoWall_IsDamped` — `SlideTimer == 0`, `|Vel| == MaxSpeed`,
    `LinkWindowTimer == 0`;
  - `DashRicochetsOffFlatWall_MirrorsAndRetains` — угол падения равен углу
    отражения, `|Vel|` следующего тика == `DashSpeed * RicochetRetention`;
  - `CorridorSeam_NoSticking` — движение через стык торцевого круга и боковины:
    пройдено ≥ 90% ожидаемого пути (митигация Р-В);
  - `CorridorTraversal_NoTunneling` — дэш поперёк коридора не проходит сквозь.
- [x] **Step 2:** прогон; **любой красный — находка в Т11–Т14**, чинить там, а
  не подгонять тест.
- [x] **Step 3:** R-FILTER `WallGeometryTests` → PASS; R-TEST.
- [x] **Step 4:** R-COMMIT `test(app-5nu): Т15 — механики движения на плоских
  стенах`.

### Task Т16: арена под троих, `TestConfigs`, волны → **перепин golden №2**

**Files:**
- Modify: `client/Assets/Scripts/Data/ArenaConfig.cs` (`Walls[]` — **перед**
  маркером), `.../Data/WaveConfig.cs` (`PerPlayerCountFrac` — **последним полем,
  маркер заводится впервые**), **`.../Data/GameFeelConfig.cs`**
  (C#-дефолты `MaxCasings :27`, `MaxDecals :28`, `MaxCorpses :29` — иначе `.asset`
  уедет в 3072/1536/128 при дефолтах 1024/512/64, а это новое расхождение, которого
  спека §0 не санкционирует и ни один тест не пинит), `.../Data/SimConfigBuilder.cs`,
  `.../Simulation/AI/WaveSystem.cs`,
  **`client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`**
  (`ApplyStageTwoBalance` наполняется здесь)
- Modify: `TestConfigs.cs`, `ConfigTests.cs`, `WaveTests.cs`;
  Create `WaveScalingTests.cs` (+ `.meta`)

**Interfaces:**
- **Санкционированные правки существующих чисел** (спека §3.15) доставляются
  **явным идемпотентным apply-блоком** `ApplyStageTwoBalance()` (маркер-механизм
  их не доставит — он только добавляет отсутствующие ключи): `Radius 35 → 65`,
  `MaxMobs 64 → 96`, `MaxProjectiles 256 → 384`, `MaxEventsPerFrame 256 → 512`,
  `MaxMobsPerWave 24 → 36`, `MaxCorpses 64 → 128`, `MaxCasings 1024 → 3072`,
  `MaxDecals 512 → 1536`.
- **Вызов `ApplyStageTwoBalance` — одноразовый по признаку «стены не доставлены»**,
  а не по маркеру (решение владельца F3a, обоснование и код гейта — в Т9).
  **Каркас метода и call-site уже существуют** (заведены Т9) — Т16 наполняет тело
  и снимает tripwire, а не пишет свой блок доставки заново. Признак меряется по
  ТЕКСТУ ассета (`Contains("Walls:")`), а не по `arena.Walls.Length` — поправка
  Ф2, обоснование в спеке §3.15. **Три флага dirty** (`arena`/`wave`/`gameFeel`):
  четыре из восьми санкционированных чисел живут не в `ArenaConfig`, и одного
  `SetDirty(arena)` для них недостаточно.
- **Новые данные:** шесть стен по таблице спеки §3.15 **и три дополнительных
  круга-препятствия** (спека §3.4 — «к 5 кругам добавляются круги до 8»).
  **Аудит: в v2 раскладки кругов не существовало ни в спеке, ни в плане** — Step 4
  был неисполним, а данные входят в перепин golden №2. Раскладка внесена в спеку
  §3.15 (решение владельца F4a) и берётся оттуда, а не изобретается в таске.
  Итог: 8 кругов + 6 стен = **14** препятствий — внутри вилки С10 (12–15).
- **`TestConfigs.DefaultArena()` меняется синхронно** — иначе перепин golden не
  произойдёт, а `Build_DefaultAssets_MatchesTestConfigsBaseline` упадёт.
- Формула: `count = (int)math.round((BaseCount + CountGrowth * waveIndex) *
  (1f + (playerCount - 1) * PerPlayerCountFrac))`, затем кап. Шов
  `WaveSystem.CountForTest(in WaveSimConfig, int waveIndex, int playerCount)` —
  **`waveIndex` 0-based** (в живом состоянии `WaveState.WaveIndex` 1-based:
  оговорить в докстринге, иначе ожидание `BaseCount` не сойдётся).

- [x] **Step 1 (RED):** `WaveScalingTests.cs` — масштаб от числа игроков
  (фикстурным выражением), кап, `MinSpawnDistanceToPlayer` до ближайшего живого,
  **«при нуле живых директор волн не тикает — фаза и таймер замирают»**.
- [x] **Step 2 (RED):** в `ConfigTests` — раскладка: пара спавнов без прямой
  видимости; коридор ≥ 20 м со свободным проходом ≥ 6 м; кольцо спавна волн не
  заперто.
- [x] **Step 3:** FAIL → **Step 4 (GREEN)**: поля, формула, валидация,
  `TestConfigs`.
- [x] **Step 5:** R-APPLY-`StageOneSceneBootstrap` →
  `git diff -- client/Assets/Data/` показывает **ровно** санкционированный
  список (включая `HeroConfig.asset` с ключом Т8 из Т9); R-IDEM.
- [x] **Step 6:** **R-GOLDEN ×2** («Т16 — новая геометрия арены и капы»);
  R-TEST полный.
- [x] **Step 7:** R-COMMIT `feat(app-5nu): Т16 — арена под троих, стены и
  масштаб волн (перепин golden №2)`.

**Гейт фазы Ф3:** R-TEST (≈ +22); **ровно один перепин**; `WallGeometryTests`
зелёные; визуальная проверка не проводится (стены появятся в грейбоксе в Т46);
push; jsonl-chore; bd note; `bd close` сабтаска Ф3.

---

## Фаза Ф4 — минимальный PvP (Т17–Т18)

### Task Т17: матрица поражения, индекс атакующего, размер скретча

**Files:** Modify `.../Combat/ProjectileSystem.cs`, `.../Core/SimulationWorld.cs`,
`client/Assets/Tests/EditMode/TestWorlds.cs`; Create `PvpDamageTests.cs` (+ `.meta`);
Modify **`DeterminismTests.cs`** (третий пин МУЛЬТИПЛЕЕРНОГО golden — решение
владельца Р113, спека §6e; без этого файла пин некуда положить).

**Фактический состав Files (зафиксировано по исполнении, фазовое ревью Ф4):**
- `ProjectileTests.cs` и `HitZoneTests.cs` **правки не потребовали** — их фикстуры
  соло-мира на матрицу поражения не смотрят, а покрытие матрицы целиком ушло в
  новый `PvpDamageTests.cs` (спека §4 мандатит его отдельно). Строка «Modify
  `ProjectileTests.cs`, `HitZoneTests.cs`» снята как невыполнимая по существу.
- Дополнительно правятся, чего v3.1 не предусмотрела: `.../AI/MobAiSystem.cs`
  (прокидка `targetIndex` в `UpdateChaser` — жертва контактного удара),
  `.../Core/SimEvents.cs` (докстрока `PlayerIndex` под три конвенции — спека
  Р125), `DeathTests.cs` и `EventTests.cs` (вызовы `DamagePlayer` ломаются сменой
  сигнатуры; протухшие комментарии), а также **`Presentation/DeathOverlayController.cs`**
  — **только комментарий**, ставший ложным после Т17 (зона клиентского трека,
  отметить отдельной строкой в описании PR).

**Interfaces:**

```csharp
// Индекс АТАКУЮЩЕГО обязателен: без него нечем заполнить SimEvent.PlayerIndex (Т7)
// и нечему засчитать ShotsHit/Kills.
internal void DamagePlayer(int victimIndex, byte attackerIndex, float dmg,
    float2 pos, HitZone zone, float2 dir);
```

- Снаряд `Owner == Player` собирает мобов **и живых игроков с индексом ≠
  `OwnerIndex`**; снаряд моба — всех живых (атакующий = `NoOwner`). Само-урон
  невозможен по построению.
- **`ShotsHit` стрелка инкрементится и при попадании по игроку** (сегодня — только
  в `DamageMob`, `SimulationWorld.cs:353`); `Kills`/`HeadshotKills` —
  `StatsAt(attackerIndex)`.
- **Аудит: инкремент ОБЯЗАН быть отгорожен `attackerIndex != NoOwner`.** `ShotsHit`
  входит в `HashStats` (`SimulationWorld.cs:633`), а в соло-golden ганнеры регулярно
  попадают по игроку — без гварда мобий выстрел начал бы засчитываться, и golden
  уехал бы **в Т17** вопреки инварианту «перепинов ровно два».
- **Аудит: место вставки игроков в скретч кандидатов.** Канонический порядок паковки
  сегодня — «0 = barrier → мобы по индексу → player → floor»
  (`ProjectileSystem.cs:38–43`, `:44–90`). Игроки допаковываются **в слот после
  мобов**, на место нынешнего одиночного `player`, а не перед `HitBarrier`.
  **ПОПРАВКА ФАКТА (фаза Ф4, спека Р123):** прежнее обоснование — «иначе порядок
  кандидатов, а с ним и исход соло-сценария, изменится» — **неверно и снято**.
  Мутация, прогнанная дважды, показала, что перестановка игроков перед
  `HitBarrier` не роняет ничего, включая оба golden. Реальный эффект порядка —
  **разрешение точных `t`-ничьих**, которых 1000-тиковый соло-прогон не
  наблюдает. Поэтому порядок обязан пиниться характеризационными тестами на
  точное равенство `t`, а не считаться защищённым golden'ом: их четыре —
  `BarrierOutranksPlayerOnAnExactTie`, `MobOutranksPlayerOnAnExactTie`,
  `PlayerOutranksFloorOnAnExactTie`, `LowerPlayerIndexWinsAnExactTie`.
- `AcceptCandidate` переводится с `w.Player` на `w.PlayerAt(index)` — обе строки
  (позиция цели и `SlideTimer` для срезанного профиля).
- **Скретч:** `_projCandidates = new (float,int,int)[MaxMobs + MaxPlayers + 2]`
  (сегодня `MaxMobs + 3`), комментарий-исключение из `SaveState`/`StateHash` — как
  у `_sepForces`.
- `TestWorlds.FireAimed3D` получает `byte ownerIndex = 0` **хвостовым параметром
  с умолчанием** (иначе ломаются вызовы Э1); `SpawnMobsToCap` — не новый цикл, а
  выделенный из существующего `TestWorlds.Saturated`, который начинает звать его.

- [x] **Step 1 (RED):** `PvpDamageTests.cs` — `PlayerShot_DamagesOtherPlayer`
  (Hp жертвы падает, `StatsAt(0).ShotsHit == 1`, `StatsAt(1).ShotsHit == 0`);
  `PlayerShot_DoesNotDamageOwner`; `HeadZoneOnPlayer_AppliesMultiplier`;
  `SlidingTarget_IsMissedByHorizontalShot`; `IframesAbsorbPvpDamage`;
  `KillCreditGoesToShooter`; **`SaturatedWorld_CandidateScratchDoesNotOverflow`**
  (`MaxMobs` мобов + 3 игрока, выстрел сквозь толпу — без исключения).
- [x] **Step 2:** заглушки → R-FILTER → FAIL. **Step 3 (GREEN)**.
- [x] **Step 4:** R-FILTER `PvpDamageTests`+`ProjectileTests`+`HitZoneTests`+
  `DeathTests` → PASS; R-TEST. **Соло-golden не меняется** (в соло-сценарии
  второго игрока нет — ветка мёртвая; поехал → СТОП, инвариант «перепинов соло
  ровно два» остаётся в силе). **Мультиплеерный golden ОБЯЗАН уехать** — это
  санкционированный третий пин (решение владельца Р113, вариант «а», спека
  §6e): матрица поражения меняется (жертва по индексу, мобий снаряд против всех
  живых, снаряд игрока против чужих), и хеш, запиненный в Т10 на неполной
  матрице, обязан за ней последовать.
- [x] **Step 4a (третий пин, только мультиплеерный):** R-GOLDEN по
  `MultiPlayerGoldenHash_ScriptedScenario` — взять фактическое значение из
  `But was:`, вписать hex, обновить десятичный дубль и **письменное обоснование
  в комментарии** («Т17 — матрица поражения: жертва по индексу, мобий снаряд
  против всех живых, снаряд игрока против чужих»). Соло-константу не трогать.
- [x] **Step 5:** R-COMMIT `feat(app-5nu): Т17 — снаряды игроков поражают игроков`.

### Task Т18: ноль аллокаций в мире на троих

**Files:** Modify `AllocationTests.cs`, `TestWorlds.cs`,
**`PvpDamageTests.cs`** (добавлено по исполнении: фикс-раунд свёл дубль
`PlaceAt`/`RelocatePlayerForTest` к одному хелперу в `TestWorlds`, 21 call site
переключён).

- [x] **Step 1:** `SaturatedTrio_TicksWithoutAllocations` — 1000 тиков,
  `MaxMobs` мобов, огонь всех троих, идиом `Is.Not.AllocatingGCMemory()`.
  **Исключение из RED-правила:** hardening-тест может пройти сразу — это
  нормально, он ловит будущие регрессии; пометить в bd note.
- [x] **Step 2:** при FAIL — найти источник (боксинг структур, `Span`-обёртки).
- [x] **Step 3:** R-TEST полный. **Step 4:** R-COMMIT
  `test(app-5nu): Т18 — ноль аллокаций в мире на троих`.

**Гейт фазы Ф4:** R-TEST — **фактически +16 (311 → 327)**, не «≈ +8»: оценка
плана оказалась занижена, потому что часть требований он перечислил прозой, но
забыл в списках Step 1. Разбивка сверх плановых: половина матрицы «мобий снаряд
против всех живых» (§3.6), вторая строка `AcceptCandidate` (план требовал «обе
строки»), тест контактного удара чейзера (перенос carryover п.1 требовал «плюс
тест на это»), индекс атакующего в `ProjectileHit`/`MobDied` (перенос п.2),
**четыре** характеризационных теста канонического порядка паковки (закрытие
дыры, которую план считал закрытой — спека Р123), ассерт предпосылки теста
переполнения скретча. **Соло-golden не тронут, мультиплеерный перепинен ровно
один раз (Т17, Р113) с письменным обоснованием**; push; jsonl-chore; bd note;
`bd close` сабтаска Ф4.

---

## Фаза Ф5 — видимость, LoS, слышимость (Т19–Т22)

### Task Т19: `VisibilitySystem` — ядро фильтра

**Files:** Create `.../Simulation/Visibility/VisibilitySystem.cs`,
`.../Visibility/VisibilitySet.cs`, `client/Assets/Scripts/Data/VisibilityConfig.cs`
(+ `.meta`); Modify `.../Core/SimConfig.cs`, `.../Data/SimConfigBuilder.cs`,
`TestConfigs.cs`; Create `VisibilityTests.cs` (+ `.meta`).

**Interfaces:**

```csharp
public static class VisibilitySystem
{
    public static void Compute(SimulationWorld w, int observerIndex,
        in VisibilitySimConfig cfg, VisibilitySet previous, VisibilitySet result);
}

/// Id-keyed set with per-id linger counters (Р19/Р20): _mobs uses swap-remove,
/// so slot indices are unstable and would transfer state to a different mob.
public sealed class VisibilitySet
{
    public VisibilitySet(int capacity);
    public bool Contains(int entityId);
    public int LingerOf(int entityId);            // 0 = видим сейчас, >0 = доживает линжер
    public void Add(int entityId, int lingerTicks = 0);
    public void Clear();
    public int Count { get; }
}
```

- Видимость: `dist ≤ SightRadius` (или `+ ExitHysteresis`, если
  `previous.Contains(id)`) **И** `Targeting.HasLineOfFire(observerPos, targetPos,
  -targetRadius, arena)` (консервативный отступ, кламп — Т13).
- Линжер: потерявшая LoS сущность остаётся с `LingerOf > 0` ещё `LingerTicks`
  тиков.
- Свой игрок — всегда; мёртвые тела — по обычным правилам.
- `VisibilityConfig`: `SightRadius [Range(5,150)] = 45`,
  `HearRadius [Range(5,200)] = 60`, `ExitHysteresis [Range(0,20)] = 3`,
  `LingerTicks [Range(0,30)] = 5`, `HearPositionGridMeters [Range(0,10)] = 3`
  (последнее — маркер), `[CreateAssetMenu]`, `OnValidate → RingDataChanged.Raise()`.
- Валидация в `SimConfigBuilder`: `SightRadius > 0`, `HearRadius ≥ SightRadius`,
  `ExitHysteresis ≥ 0`, `HearPositionGridMeters ≥ 0`. **Проверка `LingerTicks`
  против буфера — не здесь, а в Т41** (Р72: `NetConfig` в `SimConfig` не входит).
- `VisibilitySet` — **без `HashSet`** (аллокации и недетерминированный обход):
  плоский массив id + параллельный массив счётчиков, ёмкость
  `MaxMobs + MaxPlayers`.

- [ ] **Step 1 (RED):** `VisibilityTests.cs` — `BeyondSightRadius_NotVisible`;
  `BehindObstacle_NotVisible`; `BehindWall_NotVisible`;
  **`EdgePeek_IsVisible_ConservativeLos`** (центр скрыт кромкой, корпус
  выглядывает: строгий LoS дал бы false); `Hysteresis_KeepsVisibleUntilExitRadius`;
  `LingerTicks_KeepVisibleAfterLosBreak`;
  **`SwapRemove_DoesNotTransferState`** (моб в середине списка умирает — «новичок»
  не унаследовал видимость и линжер); `OwnPlayer_AlwaysVisibleToSelf`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `VisibilityTests`+`ConfigTests` → PASS; R-TEST →
  golden не меняется (видимость в состояние мира не входит).
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т19 — серверный фильтр видимости`.

### Task Т20: слышимость и огрубление позиции невидимого источника

**Files:** Modify `.../Visibility/VisibilitySystem.cs`; Test `VisibilityTests.cs`.

**Interfaces:**

```csharp
public static bool IsAudible(float2 observerPos, float2 sourcePos, in VisibilitySimConfig cfg);
/// Snaps the position of an event whose source is NOT visible onto a coarse grid
/// (Р21): exact coordinates of every shot through walls are an ESP-grade leak.
public static float2 QuantizeAudiblePos(float2 pos, in VisibilitySimConfig cfg);
```

- `QuantizeAudiblePos` = `round(pos / grid) * grid` при `grid > 0`, иначе
  тождество (детерминировано, без RNG).

- [ ] **Step 1 (RED):** `AudiblePos_SnappedForInvisibleSource`,
  `AudiblePos_ExactForVisible`, `Audible_BeyondHearRadius_False`,
  `HearRadius_IgnoresLos_AndIsWider`.
- [ ] **Step 2:** FAIL → **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `VisibilityTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т20 — слышимость и огрубление позиции`.

### Task Т21: правила доставки событий

**Files:** Create `.../Visibility/EventRelevance.cs` (+ `.meta`),
`client/Assets/Tests/EditMode/EventDeliveryTests.cs` (+ `.meta`).

**Interfaces:**

```csharp
public enum DeliveryChannel : byte { None = 0, Owner = 1, Visible = 2, Audible = 3, All = 4 }

public static class EventRelevance
{
    public static DeliveryChannel ChannelFor(SimEventKind kind);
    public static bool ShouldDeliver(in SimEvent ev, int observerIndex,
        SimulationWorld w, VisibilitySet observerSet, in VisibilitySimConfig cfg,
        out float2 deliveredPos);
}
```

- Таблица Р28: `StaminaDenied` → `Owner`; `PlayerDashed`/`PlayerSlideStarted`/
  `DashRicocheted` → `Visible`, иначе `Audible` с огрублением; **`MobDied` — по
  видимости моба** (Р81: моб видим до `SightRadius + ExitHysteresis`, а спавн
  снаряда доставлен до `SightRadius` — в полосе 3 м событие иначе терялось бы);
  `MobSpawned`/`PlayerDamaged`/`PlayerDied` → `Visible` (`PlayerDied` своего —
  всегда владельцу); `WaveStarted`/`WaveCleared` → `All` **без позиции**.
- Релевантность снаряда — **не здесь**, а в Т28 (нужна траектория).

- [ ] **Step 1 (RED):** `EventDeliveryTests.cs` — по тесту на строку таблицы:
  `StaminaDenied_OnlyToOwner`, `WaveEvents_ToAllWithoutPosition`,
  `OwnDeath_AlwaysDelivered`, **`MobDied_DeliveredInHysteresisBand`**,
  `DashEvent_AudibleWithCoarsePos`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `EventDeliveryTests`+`VisibilityTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т21 — правила доставки событий`.

### Task Т22: проводка `VisibilityConfig` во все четыре дома

**Files:** Modify `.../Data/SimConfigBuilder.cs` (сигнатура `Build`),
`.../Presentation/SimulationRunner.cs` (поле `[SerializeField] VisibilityConfig`
**и ОБА call-site `SimConfigBuilder.Build` — `:231` hot-tweak `ApplyConfig` и
`:366` `Restart`**), `client/Assets/Scripts/Editor/LongRunHarness.cs`
(`BuildBattleConfig`, `:122`),
`client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
(`GetOrCreate<VisibilityConfig>` + `EnsureAssetHasKey` + wiring в раннер),
**`client/Assets/Tests/EditMode/ConfigTests.cs`**.

**Interfaces:**
- **Аудит перед имплементацией — CRITICAL:** спека Р52 называет **четыре** места,
  перечисляющих SO поимённо; фактически домов **пять**, а точек правки сигнатуры —
  **шесть**. Пятый дом — `ConfigTests.cs:13–21` (`MakeDefaults()` создаёт те же
  шесть SO через `ScriptableObject.CreateInstance<>`) с **восемью** вызовами
  `SimConfigBuilder.Build` (`:29, :36, :48, :57, :67, :109, :175, :187`); шестая
  точка — второй call-site в раннере (`:231` против `:366`). Без правки
  `ConfigTests.cs` седьмой параметр `Build` **ломает компиляцию тестового
  ассембли**, и R-COMPILE Step 1 краснеет до Step 3, каскадом блокируя всё до Ф8.
  Без правки обоих call-site раннера hot-tweak и рестарт разъедутся по составу
  конфига.
- Ассет `VisibilityConfig.asset` создаётся бутстрапом с дефолтами.
- **ПОПРАВКА ФАЗЫ Ф5 (Р135, спека §6h): домов СЕМЬ, а не пять.** Аудит
  2026-08-05 устарел: к пяти добавились **`Networking/Spike/SpikeBootstrap.cs`**
  (шесть SO-полей + вызов `Build`) и **`Editor/SpikeSceneBootstrap.cs`**
  (`SetRef` на те же поля) — спайк удаляется только в Т30, значит между Т22 и
  Т30 обязан компилироваться; пропуск ломает сборку `Ring.Networking`.
  Вызовов `Build` в `ConfigTests` — **двадцать два**, а не восемь (Ф2 и Ф3
  дописали ещё четырнадцать). Спайку заведено седьмое поле «как всем»:
  передача `null` упала бы на валидации этого же таска.
- Спека §3.15 и Р52 правятся строкой «пять домов / шесть точек» (см. §6c),
  затем ещё раз — строкой «семь домов» (см. §6h).

- [ ] **Step 1:** правки; R-COMPILE.
- [ ] **Step 2:** R-APPLY-`StageOneSceneBootstrap` → `VisibilityConfig.asset`
  создан, ссылка в сцене проставлена; R-IDEM.
- [ ] **Step 3:** R-TEST полный (`ConfigTests` видит новую секцию).
- [ ] **Step 4:** R-COMMIT `chore(app-5nu): Т22 — проводка конфига видимости`.

**Гейт фазы Ф5:** R-TEST — **фактически +51 (327 → 378)**, не «≈ +22»: перевес
целиком пришёл из фикс-раундов, то есть из закрытия дыр фальсифицируемости, а
не из сверхскоупа (каждый тест лежит внутри §3.5/§3.7). Разбивка сверх
плановых: восемь тестов фикс-раунда Т19 (состав набора, позитивные свидетели,
чужой игрок и непересекаемость id-пространств, гвард алиасинга, парный
LoS-разрыв, семантика гистерезиса в линжере, аллокации `Compute`); три
фикс-раунда Т20 (наблюдатель вне нуля, недефолтные `grid`/`HearRadius`,
`ToEven` на точной половине); два фикс-раунда Т21 (синтетический id жертвы,
набор прошлого тика для смертей); десять негативов валидации Т22; плюс
фазовая фикс-волна. **Golden не тронут ни один** — `DeterminismTests.cs` не
встречается ни в одном коммите фазы; свип кириллицы; push; jsonl-chore;
bd note; `bd close` сабтаска Ф5. Решения фазы — спека §6h (Р129–Р140).

---

## Фаза Ф6 — протокол снапшота (Т23–Т29)

Сигнатуры FishNet — **из заметки Т2**, не по памяти.

### Task Т23: `NetConfig`, `NetStats`, `SimConfigHash`

**Files:** Create `client/Assets/Scripts/Data/NetConfig.cs`,
`.../Networking/NetStats.cs`, `.../Simulation/Core/SimConfigHash.cs` (+ `.meta`),
`client/Assets/Tests/EditMode/SimConfigHashTests.cs` (+ `.meta`);
Modify `StageOneSceneBootstrap.cs` (создание ассета `NetConfig`).

**Interfaces:**
- **Идёт первым в фазе** — Т24–Т29 используют его числа как дефолты.
- `NetConfig` (в `SimConfig` **НЕ входит**, Р52): `TickRate 30`,
  **`InterpBufferTicks 3`** (целое — `ceil(0.1f/(1f/30f))` во float даёт 4),
  `InterpMaxStaleTicks 3`, `RenderClockSnapTicks 10`, `EventRedundancyTicks 4`,
  `SnapshotEventBudget 16`, `SnapshotMaxBytes 1000`, `GhostConfirmTicks 12`,
  `ReconcileSnapMeters 1.0`, `InputStarveTicks 10`, `LatencySimRttMs 80`,
  `LatencySimLossPercent 5`, `JoinTimeoutSeconds 120`, `MatchEndLingerSeconds 10`,
  `MatchMaxDurationSeconds 1800` — все с `[Range]`, `[CreateAssetMenu]`,
  `OnValidate`.
- `NetStats` — сетевые счётчики вне `MatchStats` и вне хеша:
  `EdgeRequestsRejected`, `StaleSnapshots`, `DuplicateSnapshots`,
  `DroppedEntities`, `DroppedEvents`, `InputStarved`, `InputOverwritten`,
  `UnconfirmedGhosts`, `BytesDown`, `BytesUp`.
- `SimConfigHash` — **в `Simulation/Core/`** (хеширует `SimConfig` через
  существующий `StateHash64`; из Networking доступен как public).

- [ ] **Step 1 (RED):** `SimConfigHashTests` — изменение **каждого** числа
  `SimConfig` меняет хеш (рефлексией); `NetConfig` в него не входит.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**; R-APPLY (ассет).
- [ ] **Step 4:** R-FILTER `SimConfigHashTests`+`ConfigTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т23 — сетевой конфиг, счётчики и хеш
  баланса`.

### Task Т24: квантизация примитивов

**Files:** Create `.../Networking/Protocol/Quantize.cs`,
`client/Assets/Tests/EditMode/QuantizeTests.cs` (+ `.meta`).

**Interfaces:**

```csharp
public static class Quantize
{
    public static ushort Pos(float v, float radius);       // [-r, +r] -> [0, 65535]
    public static float  PosBack(ushort q, float radius);
    public static ushort Aim(float v, float radius);       // [-3r, +3r] (Р30)
    public static float  AimBack(ushort q, float radius);
    public static byte   Dir(float2 v);                    // угол, 1.4 град
    public static float2 DirBack(byte q);
    public static byte   Unit(float v, float max);         // симметрично UnitBack
    public static float  UnitBack(byte q, float max);
}
```

- **Идемпотентность обязательна** (Р34): `Q(D(q)) == q` — иначе предсказание и
  сервер разойдутся на шаге квантизации.

- [ ] **Step 1 (RED):** round-trip позиции ≤ 4 мм при `radius = 65`, прицела
  ≤ 8 мм при `3r`, угла ≤ 1.5°, `Unit` ≤ 0.5%; **идемпотентность** на 1000
  значений; границы (`-r`, `+r`, `0`) и клампы за диапазоном.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `QuantizeTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т24 — квантизация протокола`.

### Task Т25: кодек инпута (`ReplicateData`)

**Files:** Create `.../Networking/Protocol/InputCodec.cs`,
`client/Assets/Tests/EditMode/InputCodecTests.cs` (+ `.meta`).

**Interfaces:** формат — `MoveDir` угол `byte` + модуль `byte`; `AimPoint`
`Aim`-квантизация (u16×2); **`AimHeight` `byte` в `[0, Hero.MaxAimHeight]`**
(Р84); флаги `byte`. **Итого 8 Б** полезной нагрузки + тик (в спеке §3.8 стоит
«9 Б» — это округление вверх с запасом; тест на размер писать от фактической
раскладки).

- [ ] **Step 1 (RED):** round-trip всех полей; **сохранение частичного
  отклонения стика** (модуль 0.5 не превращается в 1.0); идемпотентность;
  `AimPoint` на границе клампа `Sanitize` (`2·Radius` от игрока) не схлопывается;
  `AimHeight` round-trip ≤ 2 см.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `InputCodecTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т25 — кодек инпута`.

### Task Т26: каркас снапшота — версия, эпоха, теги, bounds-checked чтение (opus)

**Files:** Create `.../Networking/Protocol/SnapshotBroadcast.cs`,
`ProtocolVersion.cs`, `SnapshotWriter.cs`, `SnapshotReader.cs` (+ `.meta`),
`client/Assets/Tests/EditMode/SnapshotCodecTests.cs` (+ `.meta`).

**Interfaces:**
- Заголовок: версия (u8), эпоха (u16), тик (u32), флаги (u8); далее
  **тегированные блоки** (`blockKind u8` + `count`); неизвестный тег —
  пропуск по длине со счётчиком.
- `SnapshotBroadcast : IBroadcast` — `uint Tick`, `ushort MatchEpoch`,
  `System.ArraySegment<byte> Payload` (тип — по заметке Т2 п.2).
- **`SnapshotReader` bounds-checked** (Р82): обрезанный payload → `false` +
  счётчик, не исключение и не мусор.
- Писатель — **без аллокаций**: один преаллоцированный `byte[SnapshotMaxBytes]`
  на соединение (пул не нужен — потолок фиксирован).

- [ ] **Step 1 (RED):** пустой снапшот (нулевые счётчики) round-trip; неизвестный
  тег пропускается со счётчиком; **обрезанный на N байт payload не бросает**;
  несовпадение версии → отказ; ноль аллокаций.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `SnapshotCodecTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т26 — каркас снапшота с версией и
  тегами`.

### Task Т27: блоки снапшота — игроки, живость, мобы, волна, события

**Files:** Modify `.../Networking/Protocol/SnapshotWriter.cs`, `SnapshotReader.cs`;
Test `SnapshotCodecTests.cs`.

**Interfaces:** блоки по таблице спеки §3.8, включая **бит «жив/мёртв» всех
игроков** (нужен мёртвому для списка кандидатов наблюдения, Р70) и блок событий
(`вид u8`, `seq`, тик-дельта u8, позиция u16×2, нагрузка 0–4).
**`seq` — `ushort`, а не `byte`** (Р-A M7: при `MaxEventsPerFrame 512` ключ
дедупликации `(epoch, tick, seq)` перестал бы быть уникальным).

- [ ] **Step 1 (RED):** round-trip полного снапшота по каждому блоку; порядок
  блоков детерминирован; **переполнение `u16`-идентификатора** сущности при
  `_nextEntityId > 65535` — сопоставление остаётся корректным (живых ≤ 480).
- [ ] **Step 2:** FAIL → **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `SnapshotCodecTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т27 — блоки снапшота`.

### Task Т28: сборка снапшота на сервере — фильтр, релевантность, бюджет

**Files:** Create `.../Networking/Server/SnapshotAssembler.cs` (+ `.meta`);
Modify `SnapshotCodecTests.cs`, `EventDeliveryTests.cs`.

**Interfaces:**
- На соединение: `VisibilitySystem.Compute` → блоки → события через
  `EventRelevance` → **релевантность снаряда по траектории** (Р32):
  `ProjectileSpawnedNet` уходит, если `Geometry.ClosestPointOnSegment` от
  наблюдателя до отрезка `spawnPos → spawnPos + vel * lifetime` ближе
  `SightRadius`; `ProjectileEndedNet` — **всем, кто получил спавн** (per-connection
  набор id).
- **Кап событий** `SnapshotEventBudget` с приоритетом (смерти и попадания выше
  косметики); остаток переносится в следующий снапшот, при исчерпании —
  отброс со счётчиком (Р61).
- **Усечение сущностей** при превышении `SnapshotMaxBytes`: дальние раньше,
  детерминированно, со счётчиком.
- **Пространство имён** — `Ring.Networking.Server` (папка `Networking/Server/`);
  не путать с asmdef `Ring.Server` (headless-бутстрап) — оговорить комментарием
  в шапке файла.

- [ ] **Step 1 (RED):** в `EventDeliveryTests` —
  **`ProjectileRelevance_ByTrajectory`** (снаряд через зону
  `SightRadius … дальность` доставлен; летящий мимо — нет: регрессия на дыру
  Р32) и **`ProjectileEnded_GoesToSpawnSubscribers`**; в `SnapshotCodecTests` —
  **`WorstCase_ByCaps_TriggersTruncation`** (`MaxPlayers−1` игроков + `MaxMobs`
  мобов + полный бюджет событий: проверяется именно срабатывание усечения и
  детерминированный порядок отброса) и `EventBudget_PrioritizesDeaths`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER затронутых → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т28 — сборка снапшота с фильтром и
  бюджетом`.

### Task Т29: избыточность и дедупликация событий (opus)

**Files:** Modify `.../Networking/Server/SnapshotAssembler.cs`;
Create `.../Networking/Client/EventDedup.cs` (+ `.meta`);
Modify `SnapshotCodecTests.cs`.

**Interfaces:** сервер повторяет события последних `EventRedundancyTicks` тиков;
клиент дедуплицирует по `(MatchEpoch, Tick, seq)`. **Анти-stale относится к
состоянию, не к событиям:** снапшот с `Tick ≤ _lastAppliedTick` **той же эпохи**
не применяет блоки состояния, но его невиденные события обрабатываются.

- [ ] **Step 1 (RED):** `LostSnapshot_EventsRecoveredByRedundancy` (выбросить
  каждый второй — все события дошли по разу);
  `ReorderedSnapshot_StateDropped_EventsKept`; `Dedup_DoesNotReplayEvents`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `SnapshotCodecTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т29 — избыточность и дедупликация
  событий`.

**Гейт фазы Ф6:** R-TEST (≈ +35); golden не тронут; **сверка с заметкой Т2** —
все использованные сигнатуры FishNet в ней присутствуют; push; jsonl-chore;
bd note; `bd close` сабтаска Ф6.

---

## Фаза Ф7 — предсказание, часы, серверный цикл (Т30–Т37)

### Task Т30: `PlayerPrediction` и шов оружия с приёмником выстрелов (opus)

**Files:** Create `.../Simulation/Core/PlayerPrediction.cs` (+ `.meta`);
Modify `.../Combat/WeaponSystem.cs`, `.../Core/SimulationWorld.cs`;
Create `PredictionParityTests.cs` (+ `.meta`).

**Удаление спайка — полный список** (дополнено фикс-волной Ф1: прежние две
позиции оставляли `Ring.Editor` несобираемым, а в git — висячий guid):

- Delete `client/Assets/Scripts/Networking/Spike/**` (+ `.meta` на файлы **и на
  папку**);
- Delete `client/Assets/Scenes/NetSpike.unity` (+ `.meta`);
- Delete **`client/Assets/Scripts/Editor/SpikeSceneBootstrap.cs`** (+ `.meta`) —
  он делает `using Ring.Networking.Spike;`, и без его удаления вся сборка
  `Ring.Editor` падает с CS0246, унося `BuildCommands` и
  `StageOneSceneBootstrap`;
- Delete **`client/Assets/Prefabs/SpikePlayer.prefab`** (+ `.meta`) — артефакт
  бутстрапа, отдельный ассет (иначе `PlayerSpawner` его не спавнил);
- Modify `.../Simulation/Movement/PlayerMovementSystem.cs` — снять шов
  `PlayerMovementSpikeSeam` (его замещает `PlayerPrediction.Step` этого же
  таска, приходящий **с** тестом паритета — waiver Т3 закрывается здесь);
- Modify **`.../Networking/Protocol/MathCodegenSupport.cs`** — снять из
  `WireComparers` спайковый метод `CompareSpikeReplicateData` (он ссылается на
  удаляемый тип). **Файл НЕ удалять**: это обход ограничения пакета, а не часть
  спайка (спека Р110) — сериализатор `float2` в нём нужен постоянно, а компарер
  боевой `ReplicateData` приходит в Т34. Между Т30 и Т34 класс `WireComparers`
  временно пуст — это нормально;
- Modify `client/Assets/Scripts/Editor/Editor.asmdef` — откатить ссылки
  `Ring.Networking` и `FishNet.Runtime`, добавленные в Т3, **если** к моменту
  Т30 их не требует другой editor-код (проверить грепом, не по памяти);
- Modify **`client/Assets/DefaultPrefabObjects.asset`** — реестр спавнимых
  префабов FishNet: после удаления префаба прогнать Unity (R-COMPILE достаточно,
  постпроцессор `Runtime/Editor/PrefabCollectionGenerator/Generator.cs` отработает
  сам) и **закоммитить обновлённый ассет**; иначе в версионируемом реестре
  останется висячий guid удалённого префаба.

**Interfaces:**

```csharp
// Simulation/Combat/WeaponSystem.cs — ОДНО ядро с опциональным стоком выстрелов.
// Сигнатура void AdvanceWeaponNoSpawn(...) недостаточна: overshoot берётся из
// FireCooldown ДО инкремента, конус — из RecoilOffset ДО накопления выстрела,
// цикл допускает >1 выстрела за тик; сервер иначе прокрутил бы цикл заново,
// то есть завёл вторую реализацию кулдауна (находка ревью C-1/I5).
static void Advance(ref PlayerState p, in SimInput input, in SimConfig cfg,
    SimulationWorld worldOrNull, byte ownerIndex);
public static void Update(SimulationWorld w, ref PlayerState p, in SimInput input,
    byte ownerIndex);                                   // = Advance(..., w, ownerIndex)
public static void AdvanceNoSpawn(ref PlayerState p, in SimInput input,
    in SimConfig cfg);                                  // = Advance(..., null, NoOwner)
/// Single home of the "can fire this frame" predicate — consumed by Advance,
/// by SimulationRunner.WouldFireThisFrame (T43) and by ghost projectiles (T35).
public static bool CanFire(in PlayerState p, in SimInput input, in WeaponSimConfig w);

// Simulation/Core/PlayerPrediction.cs
public static class PlayerPrediction
{
    /// Exactly what the world does to a player in one tick, minus what the client
    /// must never own: projectile spawn, RNG draw and stats.
    /// NB: the edge-request gate lives INSIDE PlayerMovementSystem.Update (T8) —
    /// there is no separate gate step here (double decrement otherwise).
    public static void Step(ref PlayerState p, in SimInput rawInput, in SimConfig cfg);
}
```

- Состав `Step`: `SimInputSanitizer.Sanitize` → `p.AimPoint = input.AimPoint` →
  `PlayerMovementSystem.Update` (гейт внутри) → `WeaponSystem.AdvanceNoSpawn`.

- [ ] **Step 1 (BASELINE):** R-FILTER `DeterminismTests` → зелёный (фиксация
  перед самым рискованным hash-нейтральным рефакторингом этапа).
- [ ] **Step 2 (RED):** `PredictionParityTests.cs` — `AssertPlayerStateBitEqual`
  **рефлексией по всем полям** (забытое поле обязано ронять тест); сценарии,
  прогоняемые **через кодек** (Р34): бег; дэш в стену с рикошетом; слайд с
  гашением; связка дэш↔слайд; отказ по Бусту; спам `DashRequested` каждый тик;
  **стрельба очередью в движении, включая тик точного истечения кулдауна**;
  враждебный ввод (NaN, `MoveDir` длиной 5, `AimPoint` за 500 м).
- [ ] **Step 3:** заглушки → R-FILTER → FAIL.
- [ ] **Step 4 (GREEN):** `Advance` с приёмником, `Step`, удаление спайка **по
  полному списку Files выше** (включая `SpikeSceneBootstrap.cs`,
  `SpikePlayer.prefab`, шов в `PlayerMovementSystem.cs` и откат ссылок
  `Editor.asmdef`).
- [ ] **Step 5:** R-FILTER `PredictionParityTests`+`WeaponTests` → PASS;
  R-TEST → **golden не меняется** (рефакторинг бит-в-бит; поехал — шов изменил
  порядок операций, **стоп**).
- [ ] **Step 6:** R-COMMIT `feat(app-5nu): Т30 — шов предсказания движения и
  оружия`.

### Task Т31: часы рендера (opus)

**Files:** Create `.../Networking/Client/RenderClock.cs`,
`.../Networking/Client/NetTimings.cs` (+ `.meta`);
Create `client/Assets/Tests/EditMode/RenderClockTests.cs` (+ `.meta`).

**Interfaces:**

```csharp
/// Plain timings snapshot built from NetConfig once per match (the SO itself
/// never crosses into Ring.Networking logic).
public struct NetTimings
{
    public int InterpBufferTicks, InterpMaxStaleTicks, RenderClockSnapTicks;
    public float SlewFraction;   // 0.05..0.10
}

/// Continuous interpolation clock (Р57): renderTime advances with local delta
/// time and slews towards (newestBufferedTick - InterpBufferTicks).
/// A snapshot-anchored clock freezes on every lost packet — the buffer would
/// smooth nothing, which is the whole reason it exists.
public sealed class RenderClock
{
    public double RenderTime { get; }
    public int RenderTick { get; }
    public float Phase { get; }
    public bool Started { get; }        // рендер стартует после >= 2 снапшотов
    public void OnSnapshot(uint tick, ushort epoch);
    public void Advance(float unscaledDeltaTime, in NetTimings cfg);
    public void ResetForEpoch(ushort epoch);
}
```

- [ ] **Step 1 (RED):** **`Advance_IsUniformUnderPacketLoss`** — ключевой тест:
  подать снапшоты с 5% выброшенных, продвигать часы фиксированным dt; шаг
  `RenderTick` равномерен (нет пар «замер / двойной скачок»);
  `Monotonic_WithinEpoch`; `Snaps_OnEpochChange`; `DoesNotStart_UntilTwoSnapshots`;
  `Slew_ConvergesToTarget`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `RenderClockTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т31 — непрерывные часы рендера`.

### Task Т32: очередь интерполяции, приём, общая копия снапшота

**Files:** Create `.../Networking/Client/SnapshotQueue.cs` (+ `.meta`);
Modify `.../Simulation/Core/RenderSnapshot.cs` (`CopyFrom`),
**`.../Presentation/SimulationRunner.cs`** (удалить приватный `CopySnapshot`,
`FreezeRender`/`UnfreezeRender` зовут `CopyFrom`);
Create `InterpolationBufferTests.cs` (+ `.meta`).

**Interfaces:**
- Кольцо глубиной `InterpBufferTicks + 2`; приём: устаревший/дублирующий по
  `(epoch, tick)` — счётчик и отказ применить **состояние** (события — через
  `EventDedup`); **переполнение** — отброс старейшего со счётчиком (Р83).
- `RenderSnapshot.CopyFrom(RenderSnapshot other)` — **единственная** глубокая
  копия (приватный `CopySnapshot` раннера удаляется, иначе останутся две).

- [ ] **Step 1 (RED):** глубина; устаревший отбрасывает состояние, но отдаёт
  невиденные события; переставленная и повторённая пара; **после смены эпохи
  снапшот с меньшим тиком принимается**; переполнение считает отброшенное;
  **`CopyFrom` копирует `Players`/`PlayerCount`/`LocalPlayerIndex`/`WorldStats`**
  (иначе замороженная пара хитстопа теряет новые поля).
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `InterpolationBufferTests` → PASS; R-TEST полный
  (соло-путь не деградировал).
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т32 — очередь снапшотов и общая копия`.

### Task Т33: latency simulator в дев-обвязке (CR 7)

**Files:** Create `.../Networking/DevLatencySetup.cs` (+ `.meta`);
Modify `.../Networking/Server/MatchServer.cs` (или бутстрап — по факту Т36/Т41).

**Interfaces:**
- **Без этого таска симулятор нигде не включается**, а на него опираются Т3,
  Т34, Т48, вехи В1–В3 и DoD п.1/6 (находка ревью C3).
- Чтение `NetConfig.LatencySimRttMs`/`LatencySimLossPercent` и применение к
  транспорту — способ по заметке Т2 п.8 — **целиком под**
  `#if UNITY_EDITOR || DEVELOPMENT_BUILD`; в релизной сборке кода нет по
  построению.
- **`SetLatency` принимает ОДНОСТОРОННЮЮ задержку — делить на два обязательно**
  (спека Р107, фикс-волна Ф1): `LatencySimulator` навешивает `_latency` на
  каждое направление отдельно (`LatencySimulator.cs:245-248`, `:286`; вызовы
  `TransportManager.cs:697` и `:772`), удвоения в коде нет, тултип про
  host-удвоение (`:84`) ложен. Значит
  `SetLatency(cfg.LatencySimRttMs / 2)` — при `LatencySimRttMs = 80` в транспорт
  уходит **40**. Потери односторонние тоже: `SetPacketLoss(0.05)` даёт 9.75%
  круговых. **Тест/проверка обязана поймать именно это:** после включения
  симулятора `TimeManager.RoundTripTime` должен встать около
  `LatencySimRttMs`, а не около его удвоения.
- **Применяется в ОБОИХ процессах — и на сервере, и на клиенте.** Симулятор
  обрабатывает только **исходящие** пакеты своего процесса (`AddOutgoing` из
  `TransportManager.cs:697` «к клиенту» и `:772` «к серверу»); включённый только
  на сервере он задержит снапшоты и не задержит инпут — получится половина
  заявленного RTT и потери только в одну сторону. Значит вызов живёт в общем
  дев-хуке, а не только в `MatchServer`.
- В `NetStats` кладутся оба числа — заданный RTT и фактическая односторонняя
  величина, отданная транспорту, — чтобы оверлей Т48 не пришлось читать в уме.
- Флаг «симулятор активен» + его параметры выставляются в `NetStats` для
  дев-оверлея (Т48) — чтобы на вехах было видно, под каким лагом снят замер.

- [ ] **Step 1:** реализация; R-COMPILE.
- [ ] **Step 2 (проверка):** дев-сборка клиента — в оверлее видно «80 мс RTT
  (40 мс на направление) / 5% на направление ≈ 9.75% круговых», а
  `TimeManager.RoundTripTime` ≈ 80, не ≈ 160;
  релизная сборка — `grep -c "LatencySim" <лог сборки>` не показывает включения
  (или проверка через отсутствие символа в билде).
- [ ] **Step 3:** R-TEST. **Step 4:** R-COMMIT
  `feat(app-5nu): Т33 — симулятор задержки в дев-билдах`.

### Task Т34: сетевой контроллер игрока (opus)

**Files:** Create `.../Networking/PlayerNetworkController.cs`,
`.../Networking/Protocol/ReplicateData.cs`, `ReconcileData.cs` (+ `.meta`);
Modify `.../Networking/Protocol/MathCodegenSupport.cs`,
`PredictionParityTests.cs` (или новый `ReconcileCodecTests.cs` + `.meta`).

**Interfaces:**
- `ReplicateData : IReplicateData` — квантованный инпут (Т25) + тик;
  `ReconcileData : IReconcileData` — полный `PlayerState` + тик, берётся **из
  мира**.
- **`[CustomComparer]` для `ReplicateData` — обязателен, если в ней остаётся хоть
  одно поле `Unity.Mathematics`** (спека Р110): без него кодоген FishNet 4.7.2
  выдаёт неверифицируемый IL и `InvalidProgramException` рушит конвейер тика на
  первом же тике. Метод дописывается в `WireComparers` внутри
  `Protocol/MathCodegenSupport.cs` (файл заведён фикс-волной Ф1 и **пережил Т30**)
  на место снятого там спайкового; сравниваются публичные поля, приватный тик — нет.
  Сериализатор `float2` в том же файле уже есть и покрывает `PlayerState` внутри
  `ReconcileData`; своего компарера reconcile не требует — FishNet его не
  генерирует. Если `ReplicateData` окажется полностью квантованной (шорты/байты),
  компарер не нужен — проверить по факту, не по памяти.
- **Ввод приходит извне, а не тянется из Presentation** (иначе цикл asmdef:
  `InputSampler` живёт в `Ring.Presentation`, а Presentation уже ссылается на
  `Ring.Networking`): контроллер публикует
  `public void SetPendingInput(in SimInput input)`, а сэмплит и передаёт
  `NetworkSimBackend` (Т44).
- Остановка предсказания на смерти — **по событию И по состоянию** (Р41/Р59):
  `PlayerDied` своего индекса **или** `Alive == false` в `ReconcileData`/снапшоте.
- Сглаживание поправки (Р78) — штатным механизмом FishNet (имя — из заметки Т2
  п.7); снап выше `ReconcileSnapMeters`.

- [ ] **Step 1 (RED):** **`ReconcileData_RoundTripsEveryPlayerStateField`** —
  рефлексией по всем полям `PlayerState` (забытое при будущем расширении поле
  молча сломало бы реконсиляцию; тест EditMode-задача, сетевой рантайм не нужен).
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)** — контроллер и структуры.
- [ ] **Step 4:** R-COMPILE → EXIT=0; R-TEST полный.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т34 — сетевой контроллер игрока`.

### Task Т35: призраки своих снарядов

**Files:** Create `.../Networking/Client/GhostProjectiles.cs` (+ `.meta`),
`client/Assets/Tests/EditMode/GhostProjectileTests.cs` (+ `.meta`).

**Interfaces:**
- Призрак рождается от предсказанного тика выстрела (детекция — общий
  `WeaponSystem.CanFire` из Т30) и живёт **на предсказанной базе**, не на
  `RenderTick`.
- **Идентификатор призрака наружу СТАБИЛЕН** (отрицательный): `ViewRegistry`
  диффит снаряды по id, и смена id на серверный вызвала бы возврат в пул и
  аренду заново — трассер потерял бы след (находка ревью C-2). Соответствие
  «серверный id → id призрака» живёт **внутри** `GhostProjectiles` и через него
  транслируются `ProjectileEndedNet`/`ProjectileBlocked`/`ProjectileExpired`.
- Не подтверждён за `GhostConfirmTicks` (≈400 мс — сетевое подтверждение
  приходит через ~140 мс, поэтому существующая константа 0.05 с не годится) →
  гаснет, счётчик `NetStats.UnconfirmedGhosts`.

- [ ] **Step 1 (RED):** `Ghost_KeepsStableIdAfterConfirmation` (наружу id не
  менялся); `Ghost_TrajectoryUnchangedOnConfirm`;
  `Ghost_ExpiresWithoutConfirmation`; `Ghost_IdSpaceDoesNotCollide`;
  `Ghost_EndEventTranslatedToGhostId`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `GhostProjectileTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т35 — предсказанные трассеры своих
  выстрелов`.

### Task Т36: серверный цикл матча и голодание инпута

**Files:** Create `.../Networking/Server/MatchServer.cs`,
`.../Networking/Server/InputStarvation.cs` (+ `.meta`),
`client/Assets/Tests/EditMode/InputStarvationTests.cs` (+ `.meta`).

**Interfaces:**
- `OnTick` — доставка `[Replicate]` (инпуты в слоты); `OnPostTick` —
  `world.TickAll(inputs)` → `SnapshotAssembler` на каждое соединение → рассылка
  → `SendReconcile` → **`world.ClearEvents()`** (Р22: рендер-кадров на headless
  нет, буфер иначе переполнится за секунду).
- **Голодание — чистая функция, а не обвязка** (иначе не тестируется):

```csharp
/// History of received inputs -> effective input for this tick + counters (Р25).
public static class InputStarvation
{
    public static SimInput Effective(in SimInput last, int ticksSinceLast,
        int starveTicks, out bool starved);
    // <= starveTicks: повтор последнего с обнулёнными краевыми флагами;
    // > starveTicks:  MoveDir = 0, FireHeld = false, AimHeld сохраняется
}
```

- Подписка на `TimeManager`: **механизма приоритетов НЕТ** (Т2 п.6 — события
  объявлены обычными `event Action`, вызываются в порядке подписки). Поэтому шаг
  мира вешается на **`OnPostTick`**, который в порядке тика
  (`OnPreTick → TryIterateData(true) → OnTick → OnPostTick → SendStateUpdate`)
  идёт после `OnTick` — «подписаться последним» не закладывать. Счётчики
  `InputStarved`/`InputOverwritten` — в `NetStats`.
- Структурная строка лога матча (спека §3.11) включает **среднее и максимум
  времени тика** — замер заводится здесь.

- [ ] **Step 1 (RED):** `InputStarvationTests` — повтор с обнулёнными краевыми
  внутри окна; обнуление движения за окном; `AimHeld` сохраняется; счётчик
  растёт.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)** — функция + `MatchServer`.
- [ ] **Step 4:** R-COMPILE → EXIT=0; R-FILTER `InputStarvationTests` → PASS;
  R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т36 — серверный цикл матча и
  голодание инпута`.

### Task Т37: политика устаревших сущностей и глобального голодания

**Files:** Create `.../Networking/Client/StalePolicy.cs` (+ `.meta`);
Modify `RenderClockTests.cs`/`InterpolationBufferTests.cs`.

**Interfaces:**
- **Р39/Р77 — в v1 плана не были назначены никому** (находка ревью C4):
  - сущность, пропавшая из **приходящих** снапшотов (вышла из interest),
    замирает на `InterpMaxStaleTicks`, затем гаснет плавным фейдом;
  - при **глобальном** голодании (нет снапшотов ≥ `InterpMaxStaleTicks` подряд —
    при 5% потерь ожидаемо несколько раз за матч) замирает **весь мир**, фейд не
    применяется, поднимается флаг «связь» для оверлея.
- Чистая логика: вход — карта «id → тик последнего обновления», текущий
  `RenderTick`; выход — состояние сущности (`Live`/`Stale`/`Fading`/`Gone`) и
  глобальный флаг.

- [ ] **Step 1 (RED):** `Entity_FreezesThenFades`;
  **`GlobalStarvation_FreezesWorld_NoFade`**; `Recovery_ClearsFlag`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т37 — политика устаревших сущностей`.

**Гейт фазы Ф7:** R-TEST (≈ +35); golden не тронут; **ручные проверки сети —
НЕ здесь**: они требуют `MatchConfig`/`ServerBootstrap`/`Server.unity` из Ф8 и
перенесены в её гейт (находка ревью I4); push; jsonl-chore; bd note;
`bd close` сабтаска Ф7.

---

## Фаза Ф8 — жизненный цикл матча (Т38–Т42)

### Task Т38: `MatchConfig` и разбор окружения

**Files:** Create `client/Assets/Scripts/Server/Server.asmdef`,
`.../Server/MatchConfig.cs`, `.../Server/MatchConfigLoader.cs` (+ `.meta`),
`client/Assets/Tests/EditMode/MatchConfigTests.cs` (+ `.meta`);
**Modify `client/Assets/Tests/EditMode/Simulation.Tests.asmdef`** (+`Ring.Server`).

**Interfaces:**
- `Server.asmdef`: `"name": "Ring.Server"`, `"references": ["Ring.Simulation",
  "Ring.Networking", "Ring.Data", "FishNet.Runtime"]`.
- `MatchConfig` — plain-структура: `matchId`, `seed` (long), `maxPlayers`,
  `port`, `players[]` (`{playerId, joinToken}`), `startMode`
  (`waitForAll`/`countdown`), `countdownSeconds`. Загрузка: `RING_MATCH_CONFIG`
  (путь) → `RING_MATCH_CONFIG_JSON` (тело) → дев-дефолты; разбор —
  `UnityEngine.JsonUtility`.
- Код 2: битый JSON, отсутствующее обязательное поле, `maxPlayers` вне
  `[1, Arena.MaxPlayers]`. **`seed == 0` валиден** (Р42) — мир сворачивает его
  существующим `Fold`; ошибка — только отсутствие поля.
- **Состав матча** (Р73): `playerCount` = **число подключившихся на старте**;
  пустых слотов в мире нет; подключение к идущему матчу отклоняется.
  Логика «множество подключившихся → playerCount / старт / отказ» — чистая
  функция `MatchRoster`, тестируемая в EditMode.

- [ ] **Step 1 (RED):** `MatchConfigTests.cs` — разбор корректного JSON;
  дефолты при пустом окружении; отказ на битом JSON; отказ при отсутствующем
  `seed`; **`seed == 0` принимается**; `maxPlayers` 0 и 99 отклоняются;
  `joinToken` сравнивается только при непустом `players[]`;
  **`Roster_PlayerCountEqualsConnected`** и **`Roster_JoinAfterStart_Rejected`**.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `MatchConfigTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т38 — конфигурация и состав матча`.

### Task Т39: приветствие и сверки

**Files:** Create `.../Networking/Protocol/HandshakeNet.cs` (+ `.meta`);
Modify `.../Networking/Server/MatchServer.cs`.

**Interfaces:** приветствие (`Reliable`): `ProtocolVersion`, `SimConfigHash`,
`MatchEpoch`, `seed`, назначенный `playerIndex`. Расхождение версии или хеша →
отключение с причиной. **В коде и логе формулировка: диагностика рассинхрона,
не античит** (модифицированный клиент вернёт любой хеш).

- [ ] **Step 1:** реализация. **Step 2:** R-COMPILE → EXIT=0.
- [ ] **Step 3:** R-TEST. **Step 4:** R-COMMIT
  `feat(app-5nu): Т39 — приветствие, сверка версии и баланса`.

### Task Т40: конец матча, границы жизни, рестарт и эпоха

**Files:** Modify `.../Networking/Server/MatchServer.cs`; Create
`.../Networking/Protocol/MatchEndedNet.cs`, `MatchRestartedNet.cs` (+ `.meta`);
Modify `.../Networking/Client/SnapshotQueue.cs`, `RenderClock.cs`,
`EventDedup.cs`, `GhostProjectiles.cs`; Modify `InterpolationBufferTests.cs`.

**Interfaces:**
- Конец: живых нет → `MatchEndedNet` → `MatchEndLingerSeconds` → код 0.
  Границы (Р43): `MatchMaxDurationSeconds` → код 4; «все отключились → грация
  30 с → код 0».
- Рестарт (Р44/Р60): `MatchEpoch++`, `MatchRestartedNet(seed, epoch)`;
  **полный сброс клиентского сетевого состояния**: `_lastAppliedTick`,
  `RenderClock`, очередь снапшотов, `EventDedup`, призраки, предсказанная копия,
  per-connection набор видимости на сервере. Снапшот с меньшим тиком, но **новой
  эпохой**, принимается.

- [ ] **Step 1 (RED):** `AfterEpochChange_LowerTickAccepted`;
  `EpochChange_ResetsDedup`; `EpochChange_ClearsGhosts`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т40 — конец, границы и рестарт матча`.

### Task Т41: серверный бутстрап, сцена, сборки, сетевые валидации

**Files:** Create `client/Assets/Scripts/Server/ServerBootstrap.cs`,
`client/Assets/Scripts/Editor/StageTwoSceneBootstrap.cs` (+ `.meta`),
`client/Assets/Scenes/Server.unity` (+ `.meta`),
`.../Networking/NetInvariants.cs` (+ `.meta`),
`client/Assets/Tests/EditMode/NetInvariantsTests.cs` (+ `.meta`);
Modify `client/Assets/Scripts/Editor/BuildCommands.cs`,
**`client/Assets/Scripts/Editor/Editor.asmdef`** (+`Ring.Server`, `FishNet.Runtime`).

**Interfaces:**
- `ServerBootstrap`: `MatchConfigLoader` → `NetworkManager` (Tugboat, порт) →
  `SimulationWorld(seed, config, playerCount)` → `MatchServer`.
  **Кап кадров** (Р63/Р102) — **через `ServerManager.FrameRate`, НЕ присваиванием
  `Application.targetFrameRate`**: `NetworkManager.UpdateFramerate()` присваивает
  его сам при каждой смене состояния и затёр бы наше значение (Т2 п.7). Под
  `UNITY_SERVER && !UNITY_EDITOR` FishNet дополнительно клампит до
  `TickRate + 15` (при 30 Гц — 45), и `BuildLinuxServer` собирается подтаргетом
  `StandaloneBuildSubtarget.Server`, то есть кламп в headless-сборке действует.
  Бутстрап обязан **залогировать фактический `Application.targetFrameRate` после
  старта** — замер В2 снимается с известной частоты, а не с предполагаемой.
  Без капа player loop крутится на тысячах кадров и замер под `--cpus=1` показал
  бы потолок независимо от стоимости симуляции.
- **`BuildCommands.Build(..., string[] scenes)`** (Р45): per-target список;
  `BuildLinuxServer` → только `Server.unity`; клиентские → `Main.unity`; гвард
  «пустой список → throw» сохраняется.
- `StageTwoSceneBootstrap.Apply` — идемпотентно строит `Server.unity` (без
  камеры, HUD, вьюх, партиклов) и регистрирует обе сцены в `EditorBuildSettings`
  в детерминированном порядке.
- **Аудит перед имплементацией:** прецедента идемпотентной **записи**
  `EditorBuildSettings` в репо нет — единственные обращения к нему сегодня на
  чтение (`BuildCommands.cs:29`, гвард `:34`), поэтому R-IDEM для этого куска
  строится с нуля и проверяется отдельно (два прогона Apply подряд → `git diff --
  client/ProjectSettings/EditorBuildSettings.asset` пуст). Плюс в
  `client/Assets/Scenes/` лежит **`AssetPreview.unity`, сегодня НЕ
  зарегистрированная** (в `EditorBuildSettings` одна запись — `Main.unity`):
  «регистрирует обе сцены» означает `Main.unity` + `Server.unity`,
  `AssetPreview.unity` осознанно остаётся вне списка (иначе она попала бы в
  клиентские сборки). Записать строкой в bd note.
- **`NetInvariants` — чистые предикаты** (Р72; единственное место, где видны оба
  конфига): `LingerTicks ≥ InterpBufferTicks + 2`,
  `SnapshotMaxBytes ≤ MTU − накладные` (MTU — из заметки Т2 п.3),
  `GhostConfirmTicks > InterpBufferTicks`, `InterpBufferTicks > 0`,
  `SnapshotEventBudget > 0`. Нарушение → лог и код 2.

- [ ] **Step 1 (RED):** `NetInvariantsTests` — позитив и **негатив на каждый
  инвариант** (нарушение возвращает false с внятным сообщением).
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**: бутстрап, сцена, сборки.
- [ ] **Step 4:** R-APPLY-`StageTwoSceneBootstrap`; R-IDEM.
- [ ] **Step 5:** R-BUILD-`LinuxServer` → `grep -i "server.unity"
  "$SCRATCH/bLinuxServer.log"` подтверждает стартовую сцену;
  R-BUILD-`LinuxClient` и R-BUILD-`WindowsClient` → `Main.unity`.
- [ ] **Step 6:** запустить серверный билд локально с дев-конфигом → в stdout
  строка старта матча, порт слушается.
- [ ] **Step 7:** R-TEST полный. **Step 8:** R-COMMIT
  `feat(app-5nu): Т41 — серверная сцена, бутстрап, сборки и сетевые инварианты`.

### Task Т42: наблюдение — протокол запроса

**Files:** Create `.../Networking/Protocol/SpectateRequestNet.cs` (+ `.meta`);
Modify `.../Networking/Server/MatchServer.cs`, `SnapshotAssembler.cs`.

**Interfaces:** `SpectateRequestNet(byte targetIndex)` — `Reliable`, от мёртвого.
Сервер валидирует (запросивший мёртв, цель — живой игрок), применяет кулдаун
`SpectatorSwitchCooldown` **на своей стороне** (Р70) и считает фильтр видимости
наблюдателя **от позиции цели**. Список живых — из бита «жив/мёртв» блока
заголовка (Т27).

- [ ] **Step 1:** реализация. **Step 2:** R-COMPILE → EXIT=0. **Step 3:** R-TEST.
- [ ] **Step 4:** R-COMMIT `feat(app-5nu): Т42 — протокол наблюдения`.

**Гейт фазы Ф8:** R-TEST; три сборки зелёные; серверный билд поднимается
локально; **ручные проверки сети, перенесённые из Ф7** (host + клиент под
симулятором 80/5): движение предсказано, реконсиляция без рывков, смерть не
откатывает тело, снапшоты идут — **числа в bd note** (медиана поправки, потери);
push; jsonl-chore; `bd close` сабтаска Ф8.

---

## Фаза Ф9 — Presentation в сетевом режиме (Т43–Т49) → **веха В1**

### Task Т43: фасад раннера и локальный бэкенд (opus)

**Files:** Modify `.../Presentation/SimulationRunner.cs`; Create
`.../Presentation/ISimBackend.cs`, `LocalSimBackend.cs` (+ `.meta`);
Modify `Presentation.asmdef` (+`Ring.Networking`); Modify **восемь** читателей
`World.Config` — `AimProvider` (`:194`), `AudioDirector` (`:179`), `HudController`
(`:64`), `CrosshairView` (`:206`), `PlayerVisual` (`:154`), `MuzzleFlashView`
(`:96`, `:140`), `PersistentPropsDirector` (`:331`, `:430`), `ViewRegistry`
(`:171`) — плюс сам фасад (`SimulationRunner.cs:115`); и **семь** носителей гварда
`World == null` (`AimProvider`, `AimRayView`, `CrosshairView`, `DevOverlay`,
`HudController`, `PlayerVisual`, `ViewRegistry`), `SimEventRouter`.

**Аудит перед имплементацией:** «десять читателей» в v2 плана завышено — `AimRayView`
(`:82`) и `MobView` (`:141`) упоминают `World.Config` **только в комментариях**, у
`MobView` вообще нет поля `_runner`. Спека §3.12 («8 классов») права. Чек-лист Step 2
закрывается по восьми реальным диффам плюс фасад; два «недостающих» — правка
комментария, если она вообще нужна. Счёт гвардов (семь) и подписчиков
`WorldRestarted` (десять, Р89) аудит подтвердил.

**Interfaces:**
- Фасад: `Config`, `CurrentTick`, `EventCount`/`GetEvent`/`ClearEvents`,
  **`Ready`** (Р66 — заменяет `World == null`; на сетевом клиенте мира нет
  никогда), `StateHash`/`DroppedEvents` (локальный бэкенд; на сетевом —
  прочерк), `DevSpawnMob` (соло и host-mode), **`RequestApplyConfig`** (Р75 —
  канал hot-tweak `RingDataChanged`), плюс всё существующее.
- `World` остаётся **только** у `LocalSimBackend`.
- `RenderMuzzleHeight` и `WouldFireThisFrame` читают **один** источник `Config`;
  `WouldFireThisFrame` переводится на общий предикат `WeaponSystem.CanFire`
  (Т30) — третье рукописное зеркало гейта огня удаляется.
- **`SimEventRouter`** (Р65): меняется **точка чтения** (мир → фасад) и
  добавляется **один** вызов фан-аута; состав `SimEventKind`, порядок и
  инвариант П-1 (единственный подписчик `TicksFlushed`) — прежние.

- [ ] **Step 1:** `ISimBackend` + `LocalSimBackend`; поведение соло **не
  меняется**; R-COMPILE; R-TEST → 0 failed.
- [ ] **Step 2:** перевод семи гвардов на `Ready`, десяти читателей — на
  `Config`, `SimEventRouter` — на фасад; R-COMPILE.
- [ ] **Step 3 (PlayMode-смоук):** соло-сцена запускается, стреляет, мобы живут,
  хитстоп работает, дев-оверлей показывает хеш и счётчики — локальный путь не
  деградировал.
- [ ] **Step 4:** R-TEST полный. **Step 5:** R-COMMIT
  `refactor(app-5nu): Т43 — фасад раннера и локальный бэкенд`.

### Task Т44: сетевой бэкенд (opus)

**Files:** Create `.../Presentation/NetworkSimBackend.cs` (+ `.meta`),
`.../Networking/Protocol/PlayerFlags.cs` (+ `.meta`),
`client/Assets/Tests/EditMode/PlayerFlagsTests.cs` (+ `.meta`);
Modify **`.../Simulation/Combat/ProjectileSystem.cs`** и затронутые тесты —
закрытие `app-dsh` (решение владельца Р128, вариант «а»).

**`app-dsh` — обязательная часть Т44 (спека Р128).** Ветка `case HitPlayer`
снимает снаряд **без единого `Emit`**, тогда как `HitBarrier`/`HitFloor` эмитят
`ProjectileBlocked`, а `HitMob` — `ProjectileHit`. Это расходится со спекой §3.12
(«снятие снаряда идёт через существующие `ProjectileBlocked`/`ProjectileExpired`»)
и с таблицей Р28. Следствий два: стрелок не отличает своё PvP-попадание от
чужого (единственное событие — `PlayerDamaged` с индексом **жертвы**), и
клиентский призрак-трассер (Т35) не получает события конца, поэтому гаснет по
`GhostConfirmTicks` вместо мгновенного снятия; при поглощении i-frames снаряд
игрока исчезает **вообще без событий** (путь пришпилен зелёным
`PvpDamageTests.IframesAbsorbPvpDamage`). Правка **хеш-нейтральна** (события вне
`StateHash` с Э1), пина не требует. Т44 обязан определить контракт события для
жертвы-игрока: сегодня `ProjectileHit` несёт `EntityId` = id моба и `MobType`.
Потребители, которых заденет расширение: тесты через `TestEvents.TryFirstOf(
ProjectileHit)` и маршрутизация `ProjectileHit` в хит-VFX по `MobType`.

**Interfaces:**
- Бэкенд собирает `RenderSnapshot` полностью: `Players`/`Mobs`/`Wave`/
  **`WorldStats`** — из снапшота; `Projectiles` — из локальных призраков (Т35),
  поэтому `ViewRegistry.SyncProjectiles` и `ProjectileView` **не меняются**;
  события — из `EventDedup` по `RenderTick`; политика устаревших — `StalePolicy`
  (Т37).
- Он же **сэмплит ввод** (`InputSampler.SampleFrame`) и передаёт его
  контроллеру через `SetPendingInput` (Т34), затем `ClearLatches` — так
  избегается цикл asmdef «Networking → Presentation».
- **Таблица «байт флагов → синтетический `PlayerState`» живёт в
  `Ring.Networking.Protocol`** (Р68 + находка ревью: в Presentation её нельзя
  протестировать — тестовый asmdef не видит `Ring.Presentation`):

```csharp
public static class PlayerFlags
{
    public const byte Alive = 1 << 0, Dashing = 1 << 1, Sliding = 1 << 2,
                      AimHeld = 1 << 3, LinkWindow = 1 << 4;
    /// Flags + quantized heading -> a PlayerState good enough for the doll:
    /// dash/slide timers become "one tick" so the visual reads the pose.
    public static PlayerState ToSyntheticState(byte flags, float2 pos, float2 heading,
        float hp01, in SimConfig cfg);
}
```

- [ ] **Step 1 (RED):** `PlayerFlagsTests` — бит дэша даёт `DashTimer > 0` и
  `DashDir == heading`; бит слайда — `SlideTimer > 0`; бит прицела —
  `AimSettleTimer == AimSettleSeconds`; отсутствие бита `Alive` — `Alive == false`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**: `PlayerFlags` +
  `NetworkSimBackend`.
- [ ] **Step 4:** R-COMPILE; R-TEST полный.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т44 — сетевой бэкенд раннера`.

### Task Т45: вьюхи игрока по контракту `Bind`/`Sync` + поля game feel

**Files:** Modify `.../Presentation/PlayerView.cs`, `PlayerVisual.cs`,
`ViewRegistry.cs`, `SimEventRouter.cs`, `client/Assets/Scripts/Data/GameFeelConfig.cs`,
`client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`;
Create `.../Presentation/PlayerVisualParams.cs` (+ `.meta`).

**Interfaces:**
- `PlayerVisual.Bind(in PlayerState, float scale)` /
  `Sync(in PlayerState, in PlayerVisualParams)` — по образцу `MobVisual`;
  `PlayerView` — по образцу `MobView` (позицию подаёт регистр). Из обоих уезжают
  ссылки на сцену и SO (`_runner`, `_aimProvider`, `_gameFeel`, прямая подписка
  на `WorldRestarted`) — требование пулённых вьюх; editor-workflow
  `CaptureGunTransformToConfig` переезжает на отдельный editor-компонент.
- `ViewRegistry` — пул кукол по индексу; `SimEventRouter` →
  `ViewRegistry.HandlePlayerEvent(index, in SimEvent)` по `SimEvent.PlayerIndex`,
  **без нового подписчика на `TicksFlushed`**.
- **Новые поля `GameFeelConfig`** (спека §3.15; в v1 плана их никто не добавлял):
  `RemotePlayerEmission` (цвет), `SpectatorSwitchCooldown [Range(0.05f,2f)] = 0.35f`
  — **маркер переезжает** на последнее; доставка — `EnsureAssetHasKey` в
  бутстрапе + R-APPLY. **Аудит: фактический маркер сегодня —
  `HeadHoverPulseAmp`** (`GameFeelConfig.cs:313`, бутстрап
  `StageOneSceneBootstrap.cs:421`), а не `CasingEjectSpeedMax` (устаревшая запись в
  handoff'е; в коде это обычное поле `:161`). Историческую цепочку маркеров в
  комментарии Apply дополнить новым звеном — иначе расхождение с уроком 40.
- **Аудит: перестановка слота `_playerVisual` — поведенческая правка роутера.**
  `SimEventRouter` держит прямую ссылку на куклу (`:34` `[SerializeField]
  PlayerVisual _playerVisual;`, `:55` `_playerVisual.HandleEvent(in e);`) внутри
  фиксированного фан-аута из восьми слотов, чей **порядок объявлен load-bearing** в
  доке класса (`:6–26`, `GameFeelDirector` обязан быть первым). Перевод куклы на
  `ViewRegistry.HandlePlayerEvent(index, …)` обязан сохранить относительный порядок
  остальных семи слотов; изменение порядка — находка ревью, а не деталь.
- **Аудит: `CaptureGunTransformToConfig` переносится не один.**
  `PlayerVisual.cs:367–381` — приватный `[ContextMenu]` под `#if UNITY_EDITOR`,
  который пишет `_gameFeel.GunLocalPosition/GunLocalEuler` из `_gun` **и
  синхронизирует внутреннее состояние вьюхи** `_appliedGunPosition`,
  `_appliedGunEuler`, `_gunApplied`, читаемое в горячем пути (`:136–147`). Перенос
  требует либо публичного шва к `_gun`/`_gunApplied`, либо переезда всего
  gun-apply-блока, а числа позы ствола тогда приходят через `PlayerVisualParams`.
  Объём фиксируется до начала таска, «просто вынести метод» неисполнимо.
- **`ExtrapolateLocalPlayer` удаляется** из `GameFeelConfig` и всех чтений
  (спека §3.12; закрывает `app-j6m`).

- [ ] **Step 1:** рефактор + поля; R-COMPILE.
- [ ] **Step 2:** R-APPLY-`StageOneSceneBootstrap` → новые ключи в
  `GameFeelConfig.asset`, `ExtrapolateLocalPlayer` исчез; R-IDEM.
- [ ] **Step 3 (PlayMode-смоук):** соло — кукла ведёт себя как раньше
  (дэш-наклон, спайн-yaw, слайд-поза, Death01).
- [ ] **Step 4:** R-TEST полный. **Step 5:** R-COMMIT
  `refactor(app-5nu): Т45 — вьюхи игрока по контракту Bind/Sync`;
  `bd close app-j6m` с evidence.

### Task Т46: стены в грейбоксе

**Files:** Modify `.../Presentation/GreyboxBuilder.cs`.

**Interfaces:** **`BuildWallSegments()`** (имя разведено с существующим
`BuildWall()` для кольцевой стены): бокс на каждую запись `Walls[]`, длина
`|B−A|`, ширина `2 * HalfWidth`, **высота выше линии огня** (стена блокирует
пули на любой высоте — бокс обязан читаться как непрострельный), тем же идиомом
`CreatePrimitive` + `localScale` + материал `_wall`. Пересборка — на существующем
`WorldRestarted`.

- [ ] **Step 1:** реализация; R-APPLY-`StageOneSceneBootstrap`; R-IDEM.
- [ ] **Step 2 (PlayMode-смоук, обязателен):** дойти до коридора, упереться в
  стену — **видимая геометрия совпадает с коллизией**.
- [ ] **Step 3:** R-TEST полный. **Step 4:** R-COMMIT
  `feat(app-5nu): Т46 — стены и коридоры в грейбоксе`.

### Task Т47: наблюдение и `ObservedIndex`

**Files:** Modify `CameraRig.cs`, `HudController.cs`, `CrosshairView.cs`,
`AimRayView.cs`, `DeathOverlayController.cs`, `NetworkSimBackend.cs`,
**`.../Presentation/SimulationRunner.cs`**.

**Аудит перед имплементацией:** в Presentation сегодня **нет никакого понятия
индекса игрока** — `grep` по `LocalPlayerIndex|ObservedIndex|PlayerIndex` во всём
`client/Assets/Scripts/` даёт **ноль** совпадений, а `RenderPlayerWorldPos`
(`SimulationRunner.cs:99`), `RenderMuzzleHeight` (`:114`) и `WouldFireThisFrame`
(`:142`) захардкожены на единственный `RenderCurr.Player`. Поэтому: (1) таск
опирается на `Players[]`/`LocalPlayerIndex` из Т4 и блок игроков снапшота из Т27 и
раньше них не стартует; (2) `SimulationRunner.cs` — обязательный файл правки,
в v2 плана он в Files отсутствовал.

**Interfaces:** **`ObservedIndex` отдельно от `LocalPlayerIndex`** (Р88): камера,
HUD, прицел и луч читают его; в наблюдении прицел и луч **скрыты**, HUD —
наблюдательский. **Чужой прицельный луч не рисуется** (Р48). Переключение —
`SpectateRequestNet` (Т42) с локальным кулдауном поверх серверного.

- [ ] **Step 1:** реализация; R-COMPILE. **Step 2:** R-TEST полный.
- [ ] **Step 3:** R-COMMIT `feat(app-5nu): Т47 — наблюдение и индекс наблюдаемого`.

### Task Т48: сетевая секция дев-оверлея

**Files:** Modify `.../Presentation/DevOverlay.cs`.

**Interfaces:** RTT; тики клиента и сервера с расхождением; байт/с в обе
стороны; число и **медиана** поправок реконсиляции (метры); устаревшие,
повторные и отброшенные снапшоты; отброшенные сущности и события;
`InputStarved`/`InputOverwritten`; неподтверждённые призраки; состояние часов
(слю/снап); **параметры активного симулятора задержки** (Т33) — чтобы на вехах
было видно, под каким лагом снят замер. На локальном бэкенде секция скрыта;
`StateHash` на сетевом — прочерк; кнопки `DevSpawnMob` скрыты (CR 3).
**Это приборная панель лаг-гейта В3** — без неё гейт не проводится.

**Аудит перед имплементацией:** область оверлея фиксирована —
`GUILayout.BeginArea(new Rect(10f, 10f, 300f, 560f), …)` (`DevOverlay.cs:88`), и
объявленные ~13 сетевых строк в неё не влезут: высота (и, вероятно, ширина под
подписи вроде `InputOverwritten`) правится этим же таском. Кроме того, весь оверлей
сегодня стоит за гвардом `World == null` (`:86`) — на сетевом бэкенде он не
отрисуется вовсе, пока Т43 не переведёт гвард на `Ready`; порядок Т43 → Т48
обязателен.

- [ ] **Step 1:** реализация; R-COMPILE.
- [ ] **Step 2 (PlayMode-смоук):** цифры меняются под симулятором.
- [ ] **Step 3:** R-TEST; R-COMMIT `feat(app-5nu): Т48 — сетевые приборы в
  дев-оверлее`.

### Task Т49: **веха В1** — двое локально

- [ ] **Step 1:** R-BUILD-`LinuxServer` + R-BUILD-`LinuxClient`; поднять
  headless локально на двоих.
- [ ] **Step 2:** плейтест владельца (симулятор 80/5): резинка, слайд вдоль
  стены, рикошет от плоскости, проход по коридору, обход стены мобами, свой
  трассер из ствола, чужой трассер сквозь тело, отсутствие рывков у чужой куклы,
  чистота клиента после рестарта.
- [ ] **Step 3:** тюнинг-лист владельца: радиус арены, раскладка стен,
  `InterpBufferTicks`, масштаб волн — правки только через `.asset`/`NetConfig`.
- [ ] **Step 4:** фикс-волны короткими итерациями; после каждой — R-TEST и
  scoped re-review.
- [ ] **Step 5:** `bd note app-5nu "В1 принята: <числа из оверлея>"`.

**Гейт фазы Ф9:** R-TEST полный; **веха В1 принята владельцем**; push;
jsonl-chore; `bd close` сабтаска Ф9.

---

## Фаза Ф10 — Docker и хост локальной сети (Т50–Т53) → **веха В2**

### Task Т50: Dockerfile и entrypoint

**Files:** Create `client/docker/Dockerfile`, `client/docker/entrypoint.sh`,
`client/docker/.dockerignore`.

**Interfaces:** `FROM debian:12-slim`; `apt-get install -y
--no-install-recommends ca-certificates libc6 libstdc++6`;
непривилегированный `ring`; `COPY` артефакта; `ENTRYPOINT ["/app/entrypoint.sh"]`.
**`entrypoint.sh` запускает игру через `exec`** (Р50) — иначе PID 1 остаётся
шеллом, SIGTERM до игры не доходит, лог не дописан и код выхода потерян.
SIGTERM = «матч прерван» → код 143.

- [ ] **Step 1:** написать; `shellcheck client/docker/entrypoint.sh` (если есть).
- [ ] **Step 2:** R-BUILD-`LinuxServer` → **R-DOCKER** (сырой `docker build`;
  `build.sh` появится в Т51).
- [ ] **Step 3:** R-CONTAINER → строка старта; `docker stop` → **код 143 и
  дописанный лог** (проверка `exec`).
- [ ] **Step 4:** R-COMMIT `feat(app-5nu): Т50 — образ headless-сервера`.

### Task Т51: `build.sh` и публикация в Docker Hub

**Files:** Create `client/docker/build.sh`.

**Interfaces:** идемпотентно: Unity-сборка (`RING_BUILD_ROOT` вне git, только
`Server.unity`) → `docker build -t <user>/ring-server:<git-sha> -t
<user>/ring-server:dev` → `docker push` обоих тегов; печатает размер и sha.
Флаг `--no-push` для локального прогона. **Токен Docker Hub — только в
`docker login` на машине**, в git не попадает.

- [ ] **Step 1:** написать; прогон с `--no-push`.
- [ ] **Step 2:** `docker login` → полный прогон с `push`; тег виден в приватном
  репозитории.
- [ ] **Step 3:** R-COMMIT `feat(app-5nu): Т51 — сборка и публикация образа`.

### Task Т52: `docs/deploy.md` и LAN-хост (`app-u0l`)

**Files:** Create `docs/deploy.md`.

**Interfaces:** протокол «сборка и push на рабочей машине → `ssh` на LAN-хост →
`docker login` → `docker pull` → запуск с пробросом UDP-порта». **Эталонная
команда замера:** `docker run --cpus=1 --memory=1g -p <порт>:<порт>/udp …` +
проверка «CPU расходуется тиком, а не циклом ожидания» (кап кадров — Т41).
Секреты — только на машинах; в документе плейсхолдеры.

- [ ] **Step 1:** написать и **выполнить по шагам на живом хосте** (это и есть
  закрытие `app-u0l`).
- [ ] **Step 2:** зафиксировать в bd: адрес хоста, порт, версия образа.
- [ ] **Step 3:** R-COMMIT `docs(app-5nu): Т52 — протокол ручного деплоя`;
  `bd close app-u0l` с evidence.

### Task Т53: **веха В2** — трое через контейнер + замеры

- [ ] **Step 1:** образ на LAN-хосте; трое заходят.
- [ ] **Step 2:** плейтест: работает ли фог, слышно ли выстрелы за стеной и
  **не выдаёт ли звук точные координаты**, попадает ли PvP, держит ли контейнер
  30 Гц.
- [ ] **Step 3:** **замеры в эвиденс** (DoD п.8): трафик на клиента (порог
  **40 КБ/с** — оверлей + `docker stats`) и CPU под `--cpus=1`. Превышение →
  `bd create` задачи (дельта-снапшоты / рост контейнера), а не «посмотрим потом».
- [ ] **Step 4:** фикс-волны; после каждой — R-TEST.
- [ ] **Step 5:** `bd note app-5nu "В2 принята: трафик <N> КБ/с, CPU <M>%"`.

**Гейт фазы Ф10:** **веха В2 принята**; замеры в bd; push; jsonl-chore;
`bd close` сабтаска Ф10.

---

## Фаза Ф11 — спайк голоса (Т54–Т55)

### Task Т54: MetaVoiceChat в проект

**Files:** Create `client/Assets/Plugins/MetaVoiceChat/**` (+ все `.meta`);
Modify `CREDITS.md`.

**Interfaces:** **каталог `Assets/Plugins/`, не `ThirdParty/`** (Р54):
существующий `ThirdPartyImportBootstrap.CheckJunk` падает на любых
`.cs`/`.dll`/`.unity`/`.mat` в `ThirdParty/**` вне `_Ring/`. Лицензия MIT —
строка в `CREDITS.md`; бинарники — LFS по существующей маске `client/**/*.dll`.

- [ ] **Step 1:** распаковать релиз в `Assets/Plugins/MetaVoiceChat/`;
  R-COMPILE → EXIT=0, ГЕЙТ-ЛОГ пуст.
- [ ] **Step 2:** `git check-attr filter -- client/Assets/Plugins/MetaVoiceChat/**/*.dll`
  → `lfs`; ГЕЙТ-META.
- [ ] **Step 3:** R-TEST полный → счётчик не изменился.
- [ ] **Step 4:** R-COMMIT `chore(app-5nu): Т54 — MetaVoiceChat (MIT) в Plugins`.

### Task Т55: спайк голоса через контейнер (таймбокс — один рабочий день)

**Files:** Create `.../Networking/Voice/VoiceAdapter.cs` (+ `.meta`).

**Interfaces:** критерий go/no-go (С15): двое слышат друг друга через
headless-контейнер, затухание по дистанции работает, речь разборчива под 80/5.
Радиус — **тот же `HearRadius`**. Мут мёртвых, UI, фразы — Э4.

- [ ] **Step 1:** адаптер + подключение к `NetworkManager`; R-COMPILE.
- [ ] **Step 2:** прогон: два клиента + контейнер; замер CPU релея под `--cpus=1`.
- [ ] **Step 3:** **вердикт** go/no-go с числами (задержка, разборчивость, CPU) —
  в bd note; подготовить текст амендмента T11 (Т57).
- [ ] **Step 4:** при no-go — **не чинить в этой сессии**: зафиксировать причину;
  планы Б (Dissonance) и В (свой релей) — решение владельца.
- [ ] **Step 5:** R-COMMIT `feat(app-5nu): Т55 — спайк голоса (вердикт: <go|no-go>)`.

**Гейт фазы Ф11:** вердикт записан; R-TEST полный; push; jsonl-chore;
`bd close` сабтаска Ф11.

---

## Фаза Ф12 — финализация (Т56–Т59)

### Task Т56: **веха В3** — лаг-гейт механик боёвки-глубины

- [ ] **Step 1:** прогон на LAN-хосте под 80/5; приборы — дев-оверлей (Т48).
- [ ] **Step 2:** восемь пунктов чек-листа спеки §3.14: PvP-попадания (ориентир
  упреждения **≈ 1.4 м** = `(RTT + InterpBufferSeconds) × MaxSpeed` на числах
  `.asset`); хедшоты по ганнеру против соло-эталона; окна связки дэш↔слайд;
  слайд под выстрел ганнера; смерть в дэше/слайде без отката; **выстрел в кадре
  смерти**; резинка (медиана > 0.25 м — разбирать); трассеры.
- [ ] **Step 3:** каждый пункт — числом или явным вердиктом владельца в bd note.
- [ ] **Step 4:** при провале — **не чинить самовольно**: рычаг
  `InterpBufferTicks`, затем амендмент под CR 5 решением владельца.

### Task Т57: амендменты ADR

**Files:** Modify `docs/adr/ADR-002-Разработка.md` (A11–A17),
`docs/adr/ADR-001-Концепт.md` (A2–A3).

**Interfaces:** тексты — по спеке §9, дословно, с датой и указанием замещаемого
пункта; исходный текст ADR **не редактируется**. **A12 (голос) пишется по
фактическому вердикту Т55.** A15 включает строку про interest management из T2
(Р86).

- [ ] **Step 1:** внести девять записей; вычитать глазами.
- [ ] **Step 2:** R-COMMIT `docs(app-5nu): Т57 — амендменты ADR этапа 2`.

### Task Т58: финальный прогон и финал-ревью ветки

- [ ] **Step 1:** R-TEST полный → EXIT=0, `failed="0"`, `total` совпадает с
  ожидаемым; оба golden — **ровно с двумя** обоснованиями перепина.
- [ ] **Step 2:** R-BUILD ×3; R-IMAGE; R-IDEM обоих бутстрапов.
- [ ] **Step 3:** **финал-ревью всей ветки** (opus, урок 43): cross-task drift —
  «контракты между фазами» и «одинаковые ветки написаны одинаково»; свип
  кириллицы в `.cs`; проверка, что `Spike/` и `NetSpike.unity` удалены,
  `ExtrapolateLocalPlayer` исчез, счётчики `NetStats` нигде не попали в
  `MatchStats`.
- [ ] **Step 4:** фикс-волна по находкам + scoped re-review; повторный R-TEST.
- [ ] **Step 5:** секрет-чек; `git status --short --untracked-files=all` чист.

### Task Т59: PR, merge, закрытие

- [ ] **Step 1:** `gh pr create` — что вошло, оба хеша, замеры трафика и CPU,
  вердикт голоса, ссылки на спеку и план.
- [ ] **Step 2:** merge (branch protection → `--admin`, владелец); уборка ветки
  и worktree по `superpowers:finishing-a-development-branch`.
- [ ] **Step 3:** `bd close app-5nu` с evidence по девяти пунктам DoD §7 спеки;
  jsonl-дрифт — chore-коммитом в main.
- [ ] **Step 4:** handoff — **по команде владельца**, по `HANDOFF_PROTOCOL.md`.

---

## Порядок и вехи (сводно)

| Фаза | Таски | Гейт | Golden |
|---|---|---|---|
| Ф1 спайк сети | Т1–Т3 | заметка API (9 ответов) + вердикт по Р-А | 189, не тронут |
| Ф2 состав состояния | Т4–Т10 | ровно один перепин | **перепин №1 (Т10)** |
| Ф3 геометрия | Т11–Т16 | `WallGeometryTests` зелёные | **перепин №2 (Т16)** |
| Ф4 PvP | Т17–Т18 | ноль аллокаций на троих | соло не тронут; **мультиплеерный — третий пин (Т17, Р113)** |
| Ф5 видимость | Т19–Т22 | таблица доставки покрыта тестами | не тронут |
| Ф6 протокол | Т23–Т29 | сверка с заметкой Т2 | не тронут |
| Ф7 предсказание | Т30–Т37 | паритет зелёный | не тронут |
| Ф8 матч | Т38–Т42 | сборки + **ручные проверки сети с числами** | не тронут |
| Ф9 Presentation | Т43–Т49 | **веха В1** | не тронут |
| Ф10 Docker и LAN | Т50–Т53 | **веха В2** + замеры | не тронут |
| Ф11 голос | Т54–Т55 | вердикт go/no-go | не тронут |
| Ф12 финализация | Т56–Т59 | **веха В3**, PR, DoD | не тронут |

**Инвариант:** **соло**-golden двигается **только** в Т10 и Т16. Любой другой
его сдвиг — стоп и разбор. **Мультиплеерный** golden имеет три пина: Т10
(первичный), Т16 (геометрия, общий перепин №2) и Т17 (матрица поражения,
санкционировано решением владельца Р113, спека §6e).

## Соответствие спеке (самопроверка покрытия)

| Раздел спеки | Таски |
|---|---|
| §3.1 слои и asmdef | Т3, Т19, Т38, Т41, Т43 |
| §3.2 мультиплеер в ядре | Т4–Т10 |
| §3.3 стены и пять потребителей | Т11–Т15, Т46 (грейбокс) |
| §3.4 арена, волны, пропсы | Т16, Т45 (поля game feel) |
| §3.5 видимость и слышимость | Т19, Т20, Т22 |
| §3.6 PvP-минимум | Т17 |
| §3.7 тик, инпут, каналы, доставка | Т21, Т29, Т36 |
| §3.8 протокол, квантизация, бюджет | Т23–Т29 |
| §3.9 предсказание, часы, призраки, stale | Т30–Т32, Т34, Т35, Т37 |
| §3.10 жизненный цикл матча | Т38, Т39, Т40, Т42 |
| §3.11 серверный процесс | Т41, Т36 (строка лога) |
| §3.12 Presentation | Т43–Т48 |
| §3.13 Docker и деплой | Т50, Т51, Т52 |
| §3.14 симулятор, приборы, гейт | **Т33** (включение), Т48 (приборы), Т56 (гейт) |
| §3.15 данные и валидации | Т4, Т8, Т9, Т16, Т19, Т22, Т23, Т41, Т45 |
| §3.16 инструментарий | Т1, Т2, Т54 |
| §4 тесты и golden | Т10, Т16, **Т17** (третий пин мультиплеерного, Р113) + тест-классы в каждом таске |
| §5 вехи | Т49 (В1), Т53 (В2), Т56 (В3) |
| §7 DoD | Т58, Т59 |
| §9 амендменты | Т57 |

**Не покрыто планом сознательно:** дельта-снапшоты (заводятся задачей только при
превышении порога на В2), отмотка хит-объёмов (Э4), полноценный голос (Э4),
переподключение (Э5).

## Декомпозиция bd (создать ДО Т1)

```bash
cd "$APP_REPO"
# 12 фазовых сабтасков, parent-child к эпику + blocks-цепочка по порядку
bd create "Ф1: спайк сети — FishNet, API-заметка, вертикальный срез" -t task -p 1
bd create "Ф2: состав состояния мира — игроки, статистика, хеш (перепин №1)" -t task -p 1
bd create "Ф3: геометрия — стены, арена, волны (перепин №2)" -t task -p 1
bd create "Ф4: минимальный PvP" -t task -p 1
bd create "Ф5: видимость, слышимость, доставка событий" -t task -p 1
bd create "Ф6: протокол снапшота" -t task -p 1
bd create "Ф7: предсказание, часы рендера, серверный цикл" -t task -p 1
bd create "Ф8: жизненный цикл матча, сцена, сборки" -t task -p 1
bd create "Ф9: Presentation в сетевом режиме → веха В1" -t task -p 1
bd create "Ф10: Docker и LAN-хост → веха В2" -t task -p 1
bd create "Ф11: спайк голоса" -t task -p 1
bd create "Ф12: финализация — лаг-гейт В3, амендменты, PR" -t task -p 1
# для каждого: bd dep add <ФN> app-5nu --type parent-child
# цепочка: bd dep add <ФN+1> <ФN>   (blocks — следующая фаза не стартует раньше)
# app-u0l остаётся child эпика и закрывается в Т52 (Ф10)
```

## Правки по self-review (v1 → v2)

**Critical (7):**
1. **A-C1.** `Tick(ReadOnlySpan)` как перегрузка делал **43** существующих
   вызова `w.Tick(default)` неоднозначными (CS0121) → канонический метод
   переименован в **`TickAll`**.
2. **A-C2.** Фикстура `EdgeRateLimitTests` не снимала `DashCooldown 1.2 с` —
   тест дал бы 1 дэш вместо 2 → фикстура обнуляет кулдаун и укорачивает дэш.
3. **B-C1.** Доставка чисел в `.asset` не была назначена никому, а
   `EnsureAssetHasKey` не переписывает существующие значения → **новый Т9**
   (ключи) и `ApplyStageTwoBalance` в **Т16** (санкционированные правки чисел);
   `StageOneSceneBootstrap` добавлен в Files Т9/Т16/Т22/Т23/Т45.
4. **D-C1.** Т5 переносил три счётчика, которые хешируются в `HashStats`, —
   не скомпилировался бы или сдвинул golden на пять тасков раньше → **временный
   `HashStats`** сохраняет прежний порядок байтов до Т10.
5. **D-C2.** Класса тестов стен не существовало → **новый Т15**
   (`WallGeometryTests`: слайд вдоль боковины, гашение о плоскость, рикошет,
   стык коридора, отсутствие туннелирования).
6. **D-C3.** Включение симулятора задержки не было назначено никому → **новый
   Т33**; §3.14 в таблице покрытия теперь закрыт.
7. **D-C4.** Политика устаревших сущностей и глобального голодания (Р39/Р77) не
   реализовывалась → **новый Т37**.

**Important (сведено):** индекс атакующего в `DamagePlayer` и `ShotsHit` при
PvP (A-I1); инверсия сэмплинга ввода — цикл asmdef Networking→Presentation
(A-I2); ссылки тестового asmdef на `Ring.Server` и состав `Server.asmdef`
(A-I3/B-I7); таблица флагов переехала в `Ring.Networking.Protocol` и стала
тестируемой (A-I4/B-I3/D-I7); шов оружия получил приёмник выстрелов вместо
`void` (A-I5/C-C1); `NetTimings` объявлен (A-I6/C-C9); синтаксис
`WorldStats` исправлен (A-I7/B-I1); три дополнительных круга арены (A-I8);
`CopySnapshot` в Files Т4/Т5 и его удаление в Т32 (A-I9/C-C3); дом
`SimConfigHash` (B-I2/A-M6); `R-DOCKER` в Т50 вместо `R-IMAGE` (B-I4); маркер
`WaveConfig` (B-I5); **раздел декомпозиции bd** (B-I6); жизненный цикл
`NetSpike.unity` (B-I8); стабильный id призрака наружу (C-C2); общий
`KillPlayer` (C-5); хвостовой `ownerIndex` у `FireAimed3D` и переиспользование
`Saturated` (C-6); гейт краевых запросов только внутри `Update` (C-7/A-M8);
общий предикат `WeaponSystem.CanFire` (C-8); единственный дом
`ClosestPointOnSegment` (C-11); порядок RED в Т6 (D-I1); round-trip
`ReconcileData` (D-I2); `InputStarvation` как чистая функция (D-I3); ручные
проверки сети перенесены в гейт Ф8 (D-I4); тесты состава матча (D-I5);
`NetInvariants` с негативами (D-I6); удаление `ExtrapolateLocalPlayer` и
`bd close app-j6m` (D-I8); новые поля `GameFeelConfig` (D-I9/A-M9); проводка
`VisibilityConfig` в четыре дома — **новый Т22** (D-I10); недостающие тесты
(D-I11); осмысленный ассерт `WorldStats` (D-I12); **Т37 v1 разбит на Т43/Т44**,
**Т22 v1 разбит на Т26/Т27** (D-I13/I14); подключение клиента назначено
`NetworkSimBackend` (D-I15).

**Minor:** `TryFirstOf` вместо нового `FirstOf`; десять читателей `Config`, а не
восемь (**отменено аудитом v3 — их восемь**); API линжера в `VisibilitySet`;
симметрия `Unit`/`UnitBack`; `NetConfig`
первым в Ф6; `seq` расширен до `ushort`; 8 Б инпута вместо 9; `waveIndex`
0-based в шве; имена asmdef без префикса `Ring.`; американская орфография
(`Quantize`); `Create` вместо `Modify` у новых файлов; `[CreateAssetMenu]` и
`OnValidate` у новых SO; кросс-ссылка на токен; пространство имён
`Ring.Networking.Server` против asmdef `Ring.Server`; аргумент
hash-нейтральности Т8 записан явно (**отменён аудитом v3 — аргумент неверен**);
baseline-шаг в Т30; строка лога с временем
тика; применение избыточности инпутов (Р24) — в Т36.

## Правки по аудиту перед имплементацией (v2 → v3, 2026-08-05)

Два Explore-субагента на Opus сверили спеку и план с кодом worktree; **каждый
вердикт перепроверен главным агентом командой** (урок 49). Четыре находки оказались
развилками (golden-инвариант и тюнинг вехи В1) и решены владельцем.

**Развилки, решённые владельцем:**

1. **F1a — гейт краевых запросов переехал из Т8 в Т10.** `HashPlayer` хеширует
   `DashBufferTimer`/`SlideBufferTimer` (`SimulationWorld.cs:588`, `:595`), латч
   армируется сырым `input.DashRequested` (`PlayerMovementSystem.cs:40–42`,
   `:51–53`), golden подаёт краевые запросы с p = 0.05/тик на 1000 тиков
   (`DeterminismTests.cs:43–44`) → пара внутри окна гейта почти неизбежна
   (P(ни одной) ≈ 0.7 %). Гейт в Т8 сдвинул бы golden в Т8; «hash-нейтральность
   Т8» из v2 **неверна и снята**. Инвариант «перепинов ровно два» сохранён.
2. **F2a — временный skip-list рефлексивного теста** (`WorldLifecycleTests.cs:36–101`
   ассертит вхождение каждого поля в хеш; `Bump` `:103–116` не знает `byte`).
   Заводится в Т7 на `OwnerIndex`, снимается в Т10 **с доказательством** (Т10
   Step 3b: поля временно вынимаются из хеша → тест обязан покраснеть). Паттерн —
   тот же, что у санкционированного временного `HashStats` в Т5.
3. **F3a — `ApplyStageTwoBalance` одноразовый по признаку «стены не доставлены»**, а
   не по backfill-маркеру: образец `ApplyGunnerZoneDefaults` гейтирован
   `(created || !markerPresent)` (`:387`) с контрактом «hand-tweak владельца
   переживает re-run», и буквальная копия **не доставила бы** `Radius 35 → 65`.
4. **F4a — раскладка трёх дополнительных кругов** внесена в спеку §3.15 (её не
   существовало нигде, а Т16 обязан её применить и запинить golden №2).

**Critical, исправленные правкой документов:**

5. **Т22 — `ConfigTests.cs` пятый дом перечисления SO** (**устарело — домов семь,
   вызовов двадцать два; поправка Ф5, спека Р135/§6h**) (`:13–21`, восемь вызовов
   `Build`) и **второй call-site `Build` в раннере** (`:231` против `:366`): без них
   седьмой параметр ломает компиляцию тестового ассембли и каскадом блокирует всё
   до Ф8. Спека Р52 «четыре места» → пять домов / шесть точек.
6. **Т5 — четыре потребителя мировых счётчиков вне Files** (`WeaponSystem.cs:96` —
   единственный писатель `ShotsFired`; `DeathOverlayController.cs:121`;
   `LongRunHarness.cs:63/:77`; `WeaponTests.cs:117`): шаг GREEN не скомпилировался бы.
7. **Т17 — `ShotsHit` обязан быть отгорожен `attackerIndex != NoOwner`**: он входит
   в `HashStats` (`:633`), а в соло ганнеры попадают по игроку — golden уехал бы в Т17.

**Important:** Т43 — читателей `World.Config` восемь, а не десять (`AimRayView`,
`MobView` держат его только в комментариях); Т47 — в Presentation ноль вхождений
индекса игрока, `SimulationRunner.cs` добавлен в Files, зависимость от Т4/Т27
зафиксирована; Т16 — `GameFeelConfig.cs` добавлен в Files (иначе несанкционированное
расхождение `.asset` против C#-дефолтов по трём FIFO-лимитам); Т45 — фактический
маркер `HeadHoverPulseAmp`, перестановка слота `_playerVisual` меняет load-bearing
порядок фан-аута `SimEventRouter` (`:6–26`), объём переноса
`CaptureGunTransformToConfig` (переплетён с `_appliedGun*`, читаемыми в горячем
пути); Т41 — прецедента идемпотентной записи `EditorBuildSettings` нет, судьба
`AssetPreview.unity` зафиксирована; Т48 — `Rect(…, 300, 560)` не вмещает сетевую
секцию, порядок Т43 → Т48 обязателен; Т14 — расширение `ArenaTopologyMatches`
капами начнёт бросать на ассетах старого поколения после Т16; Т12 —
`MoveWithCollisions` живёт в `PlayerMovementSystem.cs:348`, а не в `Geometry`;
Т8 — `Targeting.SwingLead` не существует, правка идёт по call-site
`MobAiSystem.cs:96–97`.

**Подтверждено сошедшимся (правок не потребовало):** 43 вызова `Tick(default)`
(аргумент за имя `TickAll`); `HashStats` строки 632/636/637; `_projCandidates =
MaxMobs + 3` (`:81`); пять `EnsureAssetHasKey` на `:417–421` без `ArenaConfig`/
`WaveConfig`; `EnsureAssetHasKey` не переписывает значения (`EditorBootstrapUtils.cs:251–255`);
семь гвардов `World == null`; десять подписчиков `WorldRestarted`; порядок тика §3.2
буквально; фактические расхождения `.asset` против C#-дефолтов
(**поправлено Ф3, спека Р117: их пятнадцать, а не три — три числа были
ПРИМЕРОМ**), все целевые числа Т16 внутри
существующих `[Range]`; hash-нейтральность Т12 при `WallCount == 0`; «при нуле живых
директор волн не тикает» уже реализовано (`WaveSystem.cs:22`); все тест-хелперы
существуют с заявленными именами и сигнатурами; asmdef-конвенция; `CheckJunk`
действительно обосновывает `Plugins/`; `Scripts/Networking`/`Scripts/Server` пусты
с `.meta`; FishNet отсутствует в манифесте, прецедент git-URL есть; маска LFS
`client/**/*.dll` на месте; 189 `[Test]` подтверждено счётом.

**Поправка к телу коммита Т1 (`d3d8575`), внесена фикс-волной Ф1.** В теле
коммита сказано, что вместе с FishNet «транзитивно пришёл
`com.unity.nuget.newtonsoft-json 3.2.1`». Это фактическая ошибка, и коммит
переписать нельзя — он запушен, поправка живёт здесь. По
`client/Packages/packages-lock.json`:
`com.unity.nuget.newtonsoft-json` версии **3.2.2** стоял **до** Т1
(`"depth": 1`, `"source": "registry"`, есть в `d3d8575^`), и его версия после
Т1 **не изменилась**. Строка `"com.unity.nuget.newtonsoft-json": "3.2.1"` в
диффе — это запись **внутри блока `dependencies` самого FishNet** (какую версию
он просит), а не новая установленная зависимость.
**Следствие для Т57:** в амендмент ADR-002 §1 заводится **только**
`com.firstgeargames.fishnet 4.7.2`; несуществующей новой зависимости
`newtonsoft-json` в ADR быть не должно.

## Правки по фикс-волне фазы Ф1 (v3 → v3.1, 2026-08-05)

Два ревьюера разобрали спайк Т3 как **измерительный инструмент** и признали его
негодным: три наблюдения из четырёх снимались не с того объекта, четвёртое было
систематически смещено. Все вердикты перепроверены главным агентом по исходникам
FishNet 4.7.2. Правки кода — внутри `Spike/` (плюс `SpikeSceneBootstrap`),
правки документов — здесь и в спеке (§3.7, §3.14, §6d: Р107–Р109) и в заметке Т2
(§7, §8).

**Что изменилось в приборе (Т3):**
1. Наблюдения (б) и (в) читаются с **серверной копии объекта удалённого
   клиента**, (а) и (г) — с **клиентского владельца**; оверлей печатает две
   группы строк с явной подписью роли и `n/a` там, где экземпляра нет
   (спека Р109).
2. Резинка (а) больше не считает повтор тика за поправку (штамп тика
   обновляется только для созданных реплик) и не берёт в выборку реконсиляции,
   которые FishNet подставил из локальной истории клиента; оба случая —
   отдельные счётчики в оверлее.
3. Таблица `ReplicateState` строится через `Contains*`, а не через `Is*`
   (последние — точное равенство, реплей `Replayed|Ticked|Created` не совпал бы
   ни с одной корзиной).
4. Политика голодания инпута — одна функция на серверную и клиентскую стороны
   (раньше клиент повторял последний инпут бесконечно, без порога).
5. Скриптованный ввод переведён на тиковую базу и подаётся из `OnPreTick`
   (раньше зависел от FPS), траектория — без вырождений и разворотов, период
   дэша поднят над устойчивой каденцией Буста (2.8 с) и пинится бутстрапом.
6. Удалён недостижимый код: разбор командной строки и режимы
   `Manual`/`ServerOnly`.
7. **Обход поломки кодогенерации FishNet на `Unity.Mathematics`** (спека Р110) —
   новый **постоянный** файл `.../Networking/Protocol/MathCodegenSupport.cs`:
   пользовательские `WriteFloat2`/`ReadFloat2` (штатные writer/reader `float2`
   пакет генерирует рекурсивными — они зовут сами себя на свизлах `xy`/`yx`)
   и `[CustomComparer]` на проводную структуру (сгенерированный компарер
   ветвится `brfalse` по `bool2` из `float2.op_Equality` — неверифицируемый IL,
   `InvalidProgramException` на первом тике, капсула стоит и клиент не
   подключается). Патчить пакет нельзя — `PackageCache` восстанавливается по
   UPM-пину. Файл **не удаляется в Т30 вместе со спайком**: в Т30 из него
   уходит только спайковый метод, в Т34 приходит компарер боевой
   `ReplicateData` (обе правки внесены в списки Files соответствующих тасков).

**Что изменилось в документах:** Р107 (односторонняя семантика симулятора → Т33
делит RTT на два, шаг ручной проверки Т3 ставит `Latency 40`), Р108
(`StateInterpolation` публичен на чтение → `RedundancyCount` можно показывать в
Т48/В2), Р109 (роли наблюдений), Р110 (поломка кодогена на `Unity.Mathematics`,
постоянный обход → Files Т30 и Т34); полный список Files удаления спайка в Т30;
два принятых отклонения по `client/ProjectSettings/**` в ГЕЙТ-ОТКАТ; waiver на
шов в `Ring.Simulation` в Т3; оговорка про редкость голодания при 5% потерь;
поправка к телу коммита Т1 (выше); §7 заметки Т2 приведён в соответствие с Р102.
