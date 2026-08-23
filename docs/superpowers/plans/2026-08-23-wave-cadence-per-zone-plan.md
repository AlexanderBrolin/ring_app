# План имплементации: каденция волн по кольцам (app-ggvz)

> **For agentic workers:** REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.
> Распределение моделей (предложение агента, утверждает владелец):
> implementer per task = **sonnet** для Т1/Т2/Т5/Т7 (механика по готовым
> формулам и данным), **opus** — Т3 (переписывание директора волн), Т4
> (потолки и сглаживание), Т6 (снимок и провод); **fable** — ревью фаз.
> Ревьюеры = 2 × Explore (спека-соответствие + качество/арифметика).
> **Все прогоны Unity, вердикты субагентов, гейты и веха — main-агент лично,
> не на веру** (R-14: субагенты Unity не запускают вовсе; R-98: `.meta` не
> пишут). Шаги — чекбоксы `- [ ]`.

**Goal:** вернуть волнам темп — независимая каденция на каждое кольцо арены,
сложность от часов захода, окно тишины, которое зарабатывается зачисткой,
потолок численности на кольцо и сглаженный приток, вместо сегодняшнего
единственного пути «следующая волна только после полного вайпа всей арены».

**Architecture:** всё игровое остаётся в `Ring.Simulation` чистым C# (CR 1).
`WaveState` из одного экземпляра на мир становится массивом из трёх (по
кольцу), долг снова тремя полями внутри каждого; таймер фазы переводится в
целые тики и тикает в обеих фазах; размер и состав волны берутся от шага
сложности, вычисляемого из тика; приписка моба к кольцу — параметр спавнера,
новое поле `MobState.SpawnZone`. Networking игровой логики не получает: блок
волны остаётся мировым и тех же четырёх байт, `ProtocolVersion` не двигается.
Presentation узнаёт о ритме через тот же `RenderSnapshot`: мировой монотонный
номер волны и вспышка строки при его росте.

**Tech Stack:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics, FishNet
4.7.2, Docker. **Новых пакетов задача не вводит** (CR 9).

**Спека:** `docs/superpowers/specs/2026-08-23-wave-cadence-per-zone-spec.md`
**v2** (К1–К10 владельца, Р301–Р330; self-review по `review_spec.md` четырьмя
субагентами — 13 Critical, 28 Important, 17 Minor, §6a). **План против
спеки — верить спеке**, кроме двух записанных отклонений (см. «Отклонения от
спеки» в конце файла).

**Статус плана:** v1 (после написания и саморевью автора).

---

## Global Constraints (каждый таск обязан соблюдать)

- **Пути:** `APP_REPO="/home/brolin/Documents/!_MY_Proj/The Ring/app"`
  (bd — ТОЛЬКО отсюда); `WT="$APP_REPO/.worktrees/feature-app-5nu-stage2-network"`
  — cwd всех команд; ветка `feature/app-35g-stage3-extraction` **уже
  существует** и не пересоздаётся — **worktree не удалять**;
  `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH=<scratchpad ТЕКУЩЕЙ сессии>` — задать на старте;
  `SDD="$WT/.superpowers/sdd/2026-08-17-stage3-extraction-plan"`.
- **Стартовые счётчики:** **1546** EditMode-тестов, все зелёные; соло-golden
  `0xC19F5FDDF6A8F148UL` (`DeterminismTests.cs:1114`), мульти
  `0x259DC0AFF155D907UL` (`:1269`), извлечение `0xC43B54B02689DC8FUL`
  (`:345`); md5 `DeterminismTests.cs` = `02906aadbb2574b409ded7342f29ec75`.
- ⚠ **С Т1 И ДО Т8 ТРИ GOLDEN-ТЕСТА КРАСНЫЕ — ЭТО ОЖИДАЕМО.** Форма состояния
  меняется в Т1. **Иных красных быть не должно ни в одном тике плана**, и
  каждый таск обязан назвать в отчёте точное число красных и их имена.
  Константы трогать запрещено до Т8 (санкция перепина №4 — решение владельца
  К9, расходуется ровно один раз).
- **Запретный список:** не менять `client/CLAUDE.md`, `.github/CODEOWNERS`,
  `.gitattributes`, `client/ProjectSettings/**` (кроме того, что правят
  бутстрапы), `Packages/**` (CR 9), `ProtocolVersion`, `InputCodec.SizeBytes`,
  размер и состав блоков провода, `MobRecord`.
  **`client/Assets/Data/*.asset` руками не редактировать** — доставка только
  бутстрапом (Т5).
- **Simulation меняется** — строго TDD (CR 2), без UnityEngine (CR 1).
- **Два источника чисел** (спека §0): `.asset` — числа игры; C#-дефолты и
  `TestConfigs` — числа тестов. **Ожидания в тестах — только фикстурными
  выражениями**; литерал из `.asset` в тесте = находка ревью.
  ⚠ **Фикстуры зонные** (`TestConfigs.Default()` несёт `ZoneRadius {65,130}`),
  и числа волн у них **свои, скромные** (Т2) — иначе эталон на 18 000 тиков
  превращается в нагрузочный тест.
- **Орфография идентификаторов — американская**; британские формы — находка.
- **ГЕЙТ-ОТКАТ (после КАЖДОГО Unity-прогона):**
  `git status --porcelain -- client/Packages client/Assets/Settings
  .gitattributes client/ProjectSettings "client/Assets/TextMesh Pro"` → пусто;
  иной дрифт → `git checkout -- <пути>`.
- **ГЕЙТ-ЛОГ (после каждого batchmode):** `grep -E "error CS|Shader error|
  Failed to import|NullReferenceException|Exception" <лог>` → пусто (кроме
  явно ожидаемых таском строк).
- **ГЕЙТ-META:** каждому новому не-`.meta` файлу под `client/Assets/**`
  соответствует `<path>.meta` (генерит Unity, субагенты их не пишут — R-98).
- **ГЕЙТ-КОДОГЕН (после любого таска, тронувшего проводную структуру):**
  `strings -a client/Library/ScriptAssemblies/Ring.Networking.dll | grep -E
  "Comparer___|GWrite___Unity|GRead___Unity"` → **ПУСТО**; то же для
  `Ring.Presentation.Net.dll` и `MetaVoiceChat.dll`.
- **RED-дисциплина:** тест не компилируется из-за отсутствующих полей →
  сначала заглушки до КОМПИЛЯЦИИ, затем наблюдаемый FAIL ассерта. Ошибка
  компиляции ≠ RED (урок 332). Заглушка — **КОНСТАНТА**. **RED даёт EXIT=2.**
- **Мутация на каждую ветку:** мутации названы в спеке §4 (M1–M13);
  исполнитель предсказывает жертву **поимённо и числом/механизмом ДО
  прогона**, пишет предсказание в `$SDD/task-<N>-mutations-predicted.md`,
  затем гоняет. Форма — **ослабление**. ⚠ Охват мутации **гварда** берётся из
  тестов всего, что гвард пропускает через себя, а не из тестов его фичи.
- **Тест-швы состояния:** канон — `var m = w.Mobs[i]; m.X = …;
  w.SetMobForTest(i, m);`. Существующие хелперы переиспользуются
  (`TestEvents.TryFirstOf`, `TestWorlds.*`, `TestConfigs.*`). **Новые
  параметры существующих хелперов — только хвостовыми с умолчанием.**
- **Словарь:** идентификаторы английские; комментарии `.cs` — английские,
  американская орфография; **русская проза в `.cs` — находка**, кроме цитаты
  экранной подписи. Свип: `git diff -U0 -- '*.cs' | grep -E "^\+" |
  grep -P "[а-яА-Я]{4,}"`.
- **bd:** сабтаски создаются ДО Т1 (раздел «Декомпозиция bd»); клейм на
  старте таска; `bd note app-ggvz` КОРОТКО после каждого; эвиденс — **файлом
  в `$SDD`**; после каждого `bd close` — явный `bd export -o
  .beads/issues.jsonl` (урок 236); jsonl-дрифт — chore-коммитом из
  `$APP_REPO` в main.
- **Коммиты:** `feat|test|fix|refactor|chore|docs(app-ggvz): …` (рус.) +
  трейлер `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед
  каждым — секрет-чек `git status --short --untracked-files=all | grep -E
  '\.(env|pem|key)$|secrets/'` → пусто, и сверка `git diff --cached --stat`
  со скоупом таска (урок 225).
- batchmode не гонять при открытом Editor'е владельца
  (`ps aux | grep -i "[U]nity/Hub/Editor.*projectpath"`); перед прогоном —
  проверка stale `client/Temp/UnityLockfile` (урок 39); запуск —
  `timeout -k 30 900` foreground; **сборка — ФОНОМ** (199).
  ⚠ **`timeout` НЕ снимает зависший Unity** (364). **НИ `pgrep -f`, НИ
  `pkill -f`** (134) — только `pgrep -x Unity`.

## Runbook

- **R-TEST:** `cd "$WT" && timeout -k 30 900 "$UNITY" -runTests -batchmode
  -projectPath client -testPlatform EditMode -testResults "$SCRATCH/t.xml"
  -logFile "$SCRATCH/t.log"; echo EXIT=$?` → разбор xml **питоном по
  `test-case`**, не грепом; `total` читать **ГЛАЗАМИ** (169); + ГЕЙТ-ОТКАТ.
  Старт 1546.
- **R-FILTER `<Класс>`:** R-TEST + `-testFilter "Ring.Simulation.Tests.<Класс>"`.
- **R-COMPILE:** `cd "$WT" && timeout -k 30 900 "$UNITY" -batchmode -quit
  -projectPath client -logFile "$SCRATCH/c.log"; echo EXIT=$?` → EXIT=0 +
  ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-APPLY:** `cd "$WT" && timeout -k 30 900 "$UNITY" -batchmode -quit
  -projectPath client -executeMethod Ring.Editor.StageOneSceneBootstrap.Apply
  -logFile "$SCRATCH/apply.log"; echo EXIT=$?` → EXIT=0 + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-IDEM:** повторный R-APPLY → `git status --porcelain -- client/` и
  `git diff -- client/` пусты (мерить ПОСЛЕ коммита артефактов).
- **R-GOLDEN (перепин, ТОЛЬКО Т8):** R-FILTER `DeterminismTests` → из xml
  взять три `But was: <N>` → вписать hex + **обновить десятичный дубль и
  письменное обоснование** → повторный R-FILTER → PASS.
- **R-BUILD-`<X>`:** `cd "$WT" && RING_BUILD_ROOT="$SCRATCH/builds"
  timeout -k 30 900 "$UNITY" -batchmode -quit -projectPath client
  -executeMethod Ring.Editor.BuildCommands.Build<X> -logFile "$SCRATCH/b<X>.log"`
  (X ∈ `LinuxServer|LinuxClient|WindowsClient|LinuxServerDev|LinuxClientDev|
  WindowsClientDev`). **ФОНОМ**; вердикт — по строке
  **«Exiting batchmode successfully»**, не грепом error.
- **R-IMAGE:** `cd "$WT" && client/docker/build.sh --no-push`; доставка —
  `docker save <теги> | gzip -1 | ssh -p 2201 brolin@<хост> 'gunzip | docker load'`.
- **R-STAND (стенд без человека, 230):** `./Ring -batchmode -nographics
  -ring-connect <хост>:7777 -ring-player-id pN -ring-join-token tN
  -ring-latency off -logFile <лог>`; троих собирать в ОДНОМ 120-секундном
  окне (240). ⚠ **Стенд ботов не заменяет живой забег** (417).
- **R-COMMIT:** секрет-чек → ГЕЙТ-META → `git diff --cached --stat` против
  скоупа → `git add <файлы+meta> && git commit`.

---

## Фаза 1 — фундамент (Т1–Т2): дом чисел, приписка, данные

Цель фазы — внести всё **аддитивное**, что нужно каденции, не трогая
поведение волн: общий дом для числа колец, приписку моба, новые поля конфига
и их валидацию. После фазы дерево компилируется, красны только три golden.

### Task Т1: `Zones`, приписка моба к кольцу и один дом для радиуса

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs`
  (статический класс `Zones` рядом с `enum Zone`, `:210`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs`
  (`MobState`, `:150-158`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`MobConfigFor` `:1000`, `SpawnMob` `:1924`, `SpawnMobForTest` `:1963`,
  `DevSpawnMob` `:2016`, `HashMob` `:2537`)
- Modify: `client/Assets/Scripts/Simulation/AI/SeparationSystem.cs` (`:40-43`)
- Modify: `client/Assets/Scripts/Simulation/AI/WaveSystem.cs`
  (`:36` — удалить `ZoneCount`; `:320` — вызов `SpawnMob`; `:437` — копия
  конфига в цикле)
- Modify: `client/Assets/Scripts/Simulation/Loot/LootDrops.cs` (`:79` —
  удалить второй `ZoneCount`, док `:58-59`)
- Modify: `client/Assets/Scripts/Simulation/Objectives/MatchFlowSystem.cs`
  (`:123`, `:214` — вызовы `SpawnMob`)
- Test: `client/Assets/Tests/EditMode/WaveCadenceTests.cs` (**создать** +
  `.meta` генерит Unity), `client/Assets/Tests/EditMode/WorldLifecycleTests.cs`
  (квитанция бампов `:141-178`)

**Interfaces:**

```csharp
// SimConfig.cs, сразу под `public enum Zone : byte { Outer = 0, Middle = 1, Core = 2 }`.
// Статический класс, а не const на структуре конфига: рефлексивный свип
// SimConfigHashTests ходит GetFields() с дефолтными биндингами и видит статику
// (та же причина, по которой рядом живут ItemCatalogLookup и LootTransferTimes).
public static class Zones
{
    public const int Count = 3;
}

// SimStates.cs, MobState — новое поле последним в объявлении:
public Zone SpawnZone;

// SimulationWorld.cs — производственный спавн: зона ОБЯЗАТЕЛЬНА, умолчания нет.
internal int SpawnMob(MobType type, float2 pos, Zone zone)

// Тест-шов: хвостовой параметр с умолчанием (конвенция Global Constraints) —
// иначе 127 вызовов в 18 файлах тестов пришлось бы править механически.
internal int SpawnMobForTest(MobType type, float2 pos, Zone zone = Zone.Outer)
    => SpawnMob(type, pos, zone);

// Один дом для «численных характеристик архетипа» БЕЗ копии структуры:
// смена возврата на ref readonly исходно-совместима (все существующие
// call-site'ы, копирующие в локальную переменную, компилируются как были).
internal ref readonly MobSimConfig MobConfigFor(MobType type)
```

- ⚠ **Отклонение от спеки Р327, записанное намеренно** (см. «Отклонения»):
  спека предлагала завести отдельный `float MobRadiusFor(MobType)`. Это
  завело бы **второй** switch по тому же домену, то есть второй дом
  отображения (урок 279). `ref readonly` на существующем методе даёт тот же
  эффект (ноль копий 30-полевой структуры), не заводя второго дома, и снимает
  копию у **всех** потребителей, а не у двух.
- `HashMob` получает `SpawnZone` **сразу после `Type`** — поля, которое оно
  уточняет.
- Док поля обязан сказать три вещи: (1) «`SpawnZone` — **не метка свиты**;
  кто свита, решает `MatchFlowSystem.LiveRetinueCount` по месту (Р215)»;
  (2) «на клиенте остаётся `default` — там `MobState` собирается из
  `MobRecord`, поле читать в Presentation нельзя»; (3) «дев-спавн приписан к
  внешнему кольцу независимо от места».

- [ ] **Step 1 (RED):** создать `WaveCadenceTests.cs` с тремя тестами:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WaveCadenceTests
    {
        [Test]
        public void SpawnZone_IsSetByTheSpawner_NotByPosition()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            // Ставим моба СРЕДНЕГО кольца в точку, геометрически лежащую во ВНЕШНЕМ:
            // приписка обязана следовать за спавнером, а не за координатой.
            float outerR = cfg.Arena.Radius - 1f;
            int id = w.SpawnMobForTest(MobType.Chaser, new float2(outerR, 0f), Zone.Middle);
            Assert.GreaterOrEqual(id, 0, "моб не заспавнился — фикстура упёрлась в кап");
            Assert.AreEqual(Zone.Middle, w.Mobs[w.MobCount - 1].SpawnZone);
        }

        [Test]
        public void SpawnZone_OfDirectorAndRetinue_IsCore()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            w.SpawnMobForTest(MobType.Director, float2.zero, Zone.Core);
            Assert.AreEqual(Zone.Core, w.Mobs[w.MobCount - 1].SpawnZone);
        }

        [Test]
        public void MobRadius_ReadsTheSameNumberAsTheArchetypeConfig_ForEveryType()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            foreach (MobType t in new[] { MobType.Chaser, MobType.Gunner,
                                          MobType.Elite, MobType.Director })
            {
                ref readonly MobSimConfig c = ref w.MobConfigFor(t);
                Assert.Greater(c.Radius, 0f, $"радиус архетипа {t} нулевой");
            }
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `WaveCadenceTests` → **EXIT=2**, три красных с
      ошибкой компиляции? **Нет** — сначала заглушки: добавить `SpawnZone`
      (без записи), третий параметр `SpawnMob` (игнорируется), `ref readonly`.
      Затем прогон обязан дать **наблюдаемый FAIL ассерта**
      (`Expected: Middle, But was: Outer`), а не ошибку компилятора.
- [ ] **Step 3 (GREEN):** `SpawnMob` записывает `SpawnZone = zone`; все
      четыре производственных call-site'а передают зону
      (`WaveSystem.cs:320` — `zone` своего цикла; `MatchFlowSystem.cs:123` и
      `:214` — `Zone.Core`); `HashMob` получает поле; `Zones.Count` заменяет
      обе копии тройки (`WaveSystem.cs:36`, `LootDrops.cs:79`);
      `SeparationSystem.cs:40,43` и `WaveSystem.cs:437` переводятся на
      `ref readonly MobSimConfig cfgI = ref w.MobConfigFor(...)`.
- [ ] **Step 4:** R-FILTER `WaveCadenceTests` → PASS (3/3).
- [ ] **Step 5 (мутация M5, предсказание ДО прогона в
      `$SDD/task-1-mutations-predicted.md`):** в `SpawnMob` записать
      `SpawnZone = Zone.Outer` вместо параметра. Предсказание:
      `SpawnZone_IsSetByTheSpawner_NotByPosition` красный со строкой
      `Expected: Middle, But was: Outer`; `SpawnZone_OfDirectorAndRetinue_IsCore`
      красный со строкой `Expected: Core, But was: Outer`. Откат — **`cp` с
      копии и сверка md5**, НЕ `git checkout` (350).
- [ ] **Step 6:** пересчитать квитанцию бампов `WorldLifecycleTests.cs:141-178`
      **целиком, свежим чтением `typeof(X).GetFields()`** (правило самого
      файла: «re-derived, never incremented»): `MobState` 9 → **10**.
- [ ] **Step 7:** R-TEST полный → **красных ровно три**, и это три golden;
      имена выписать в отчёт.
- [ ] **Step 8:** R-COMMIT `feat(app-ggvz): Т1 — приписка моба к кольцу, один
      дом для числа колец и для чисел архетипа`.

### Task Т2: новые числа волн в конфиге, валидация, числа фикстур

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs`
  (`WaveSimConfig`, `:157`)
- Modify: `client/Assets/Scripts/Data/WaveConfig.cs`
- Modify: `client/Assets/Scripts/Data/SimConfigBuilder.cs` (маппинг `:141-160`,
  ветка дефолтов при отсутствующем SO `:225-245`, валидация `:585-631`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfigHash.cs` (`:133`,
  `:140`, помощники `HashFloatArray`/`HashInt32Array` `:261`)
- Modify: `client/Assets/Tests/EditMode/TestConfigs.cs` (`:84`, `:110`, `:308`)
- Modify: `client/Assets/Tests/EditMode/ZoneConfigTests.cs` (правила),
  `client/Assets/Tests/EditMode/ConfigTests.cs` (`AssertWaveEqual` `:1474`),
  `client/Assets/Tests/EditMode/SimConfigHashTests.cs` (`:50-51`, список
  `expectedArrayFields` `:264-290`)

**Interfaces:**

```csharp
// WaveSimConfig (Core/SimConfig.cs) — ДОБАВЛЯЮТСЯ; ZoneWeights и WavePause
// пока ОСТАЮТСЯ на месте (их снимает Т3, когда исчезнет последний потребитель).
public float[] WavePauseByZone;   // {20, 30, 30} — секунды, по кольцу
public int[] MaxAliveByZone;      // {150, 110, 10} — живых одновременно
public int MaxSpawnsPerTick;      // 2 — сглаживание притока (Р317)
public float DifficultyStepSeconds; // 20 — шаг кривой сложности (Р315)

// WaveConfig (Data/WaveConfig.cs) — те же четыре поля с дефолтами;
// DifficultyStepSeconds объявляется ПОСЛЕДНИМ и получает
// `// sync-marker key — keep LAST`; прежний маркер EliteShareOuterCap (:48)
// свою пометку теряет.
```

- **Пять правил `SimConfigBuilder.Validate`** (форма — существующая идиома
  массива: `if (x == null || x.Length != Zones.Count) errors.Add("… must have
  exactly 3 elements (Outer, Middle, Core) (got {x?.Length ?? 0}).") else
  for(i) …`, прецеденты `ZoneWeights` `:608-631`, `CellsPerMob` `:994-999`):

```csharp
// 1 и 5 — «не меньше одного тика», а НЕ «больше нуля» (Р320): ReqPositive
// пропустил бы 0.02 с, что при тикающем в обеих фазах таймере даёт тридцать
// волн в секунду и переполнение проводного u16 номера за ~36 минут.
if (SimulationWorld.TicksFromSeconds(cfg.Wave.WavePauseByZone[i]) < 1)
    errors.Add($"Wave.WavePauseByZone[{i}] must be at least one tick " +
        $"({SimulationWorld.TickDt:F4} s) -- a shorter pause starts a wave every " +
        $"tick (got {cfg.Wave.WavePauseByZone[i]:F4} s).");

// 2 — нижняя граница ЕДИНИЦА (Р321): при нуле кольцо остаётся активным,
// таймер тикает, событие эмитится, спавн срезан, долг не обнуляется никогда.
ReqInRange(errors, $"Wave.MaxAliveByZone[{i}]", cfg.Wave.MaxAliveByZone[i],
    1, cfg.Arena.MaxMobs);

// 3
ReqPositive(errors, "Wave.MaxSpawnsPerTick", cfg.Wave.MaxSpawnsPerTick);

// 4 — кросс-полевое правило по форме :521-525; существующее
// `Flow.DirectorReserveSlots < Arena.MaxMobs` (:527-531) ОСТАЁТСЯ.
int aliveSum = cfg.Wave.MaxAliveByZone[0] + cfg.Wave.MaxAliveByZone[1]
    + cfg.Wave.MaxAliveByZone[2];
if (aliveSum + cfg.Flow.DirectorReserveSlots > cfg.Arena.MaxMobs)
    errors.Add($"sum(Wave.MaxAliveByZone) + Flow.DirectorReserveSlots " +
        $"({aliveSum} + {cfg.Flow.DirectorReserveSlots}) must not exceed " +
        $"Arena.MaxMobs ({cfg.Arena.MaxMobs}) -- the per-zone ceilings are the " +
        "live governor of population and the arena cap is the physical size of " +
        "the arrays behind it.");
```

- **Числа фикстур** (`TestConfigs`, Р325 — эталон меряет детерминизм, а не
  баланс; при боевых числах прогон на 18 000 тиков стал бы нагрузочным
  тестом): `WavePauseByZone = { 2f, 3f, 3f }`, `MaxAliveByZone = { 24, 16, 8 }`,
  `MaxSpawnsPerTick = 2`, `DifficultyStepSeconds = 2f`. `BaseCount` фикстуры
  **не трогается** (4). Числа задаются во **всех** вариантах `TestConfigs`,
  а не только в `Extraction()`.
- `SimConfigHash`: два новых массива через существующие
  `HashFloatArray`/`HashInt32Array` (`:261`) и два скаляра; шаги `ZoneWeights`
  (`:140`) и `WavePause` (`:133`) пока остаются.

- [ ] **Step 1 (RED):** в `ZoneConfigTests.cs` — пять тестов, по одному на
      правило, каждый со своей строкой отказа:

```csharp
// ФОРМА СКОПИРОВАНА С ZoneConfigTests.cs:213-220, а не изобретена: валидация
// в проекте гоняется через ScriptableObject'ы (MakeDefaults -> BuildShipped),
// а не через готовую структуру SimConfig.
[Test]
public void Validate_WavePauseBelowOneTick_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.WavePauseByZone = new[] { 0.02f, 30f, 30f };   // короче одного тика 1/30 с
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Wave.WavePauseByZone[0]"));
    Assert.That(ex.Message, Does.Contain("at least one tick"));
}

[Test]
public void Validate_ZeroZoneCeiling_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.MaxAliveByZone = new[] { 0, 16, 8 };
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Wave.MaxAliveByZone[0]"));
}

[Test]
public void Validate_CeilingsPlusDirectorReserveAboveMaxMobs_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    // Сумма строго БОЛЬШЕ потолка арены: граничный случай `== MaxMobs` легален.
    wv.MaxAliveByZone = new[] { a.MaxMobs, 1, 1 };
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("must not exceed Arena.MaxMobs"));
}

[Test]
public void Validate_WrongZoneArrayLength_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.MaxAliveByZone = new[] { 10, 10 };
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("exactly 3 elements"));
}

[Test]
public void Validate_ZeroSpawnsPerTick_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.MaxSpawnsPerTick = 0;
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Wave.MaxSpawnsPerTick"));
}
```

  ⚠ Кортеж `MakeDefaults()` возвращает **ScriptableObject'ы** (`HeroConfig`,
  `WeaponConfig`, `MobChaserConfig`, `MobGunnerConfig`, `WaveConfig`,
  `ArenaConfig`, `VisibilityConfig`) — поэтому в тесте правится `wv.<поле>`
  ассета, а не поле `SimConfig`. Точный состав кортежа сверить по
  `ConfigTests.MakeDefaults` перед написанием: если Этап 3 дописал в него
  восьмым `MatchFlowConfig` или конфиги элиты/Директора, деструктуризацию
  расширить, а правило 4 (кросс-полевое, читает `Flow.DirectorReserveSlots`)
  проверять именно через него.
- [ ] **Step 2:** заглушки полей → R-FILTER `ZoneConfigTests` → **EXIT=2**,
      пять красных с наблюдаемым «исключение не брошено».
- [ ] **Step 3 (GREEN):** поля в `WaveSimConfig` и `WaveConfig`, маппинг,
      ветка дефолтов при отсутствующем SO (`:225-245` — иначе валидация
      свалится на `null` там, где сегодня всё работает), пять правил, два шага
      хеша.
- [ ] **Step 4:** числа фикстур в `TestConfigs` (все варианты), `AssertWaveEqual`
      расширяется в связке, `SimConfigHashTests.expectedArrayFields` получает
      два новых массива через `AssertInt32ArrayFieldAffectsHash`/
      `AssertFloatArrayFieldAffectsHash`.
- [ ] **Step 5:** R-FILTER `ZoneConfigTests` + `ConfigTests` +
      `SimConfigHashTests` → PASS.
- [ ] **Step 6 (мутация M13, предсказание ДО прогона в
      `$SDD/task-2-mutations-predicted.md`):** ослабить правило 4 с `>` до
      `>=`. Предсказание: краснеет **не** тест отказа (он подаёт сумму строго
      больше потолка и продолжит бросать), а **позитивный** путь — любой тест,
      строящий шиппед-конфиг, у которого `sum + reserve == MaxMobs`.
      ⚠ Если такого теста нет, мутант **выживет**, и это называет
      недостающего свидетеля (361): дописать граничный тест
      `Validate_CeilingsExactlyAtMaxMobs_IsLegal` ДО прогона мутации.
- [ ] **Step 7:** R-TEST полный → красных ровно три (golden).
- [ ] **Step 8:** R-COMMIT `feat(app-ggvz): Т2 — числа каденции в конфиге и
      пять правил валидации`.

**Гейт фазы 1:** R-TEST зелёный кроме трёх golden; ГЕЙТ-ОТКАТ и ГЕЙТ-ЛОГ
чисты; свип кириллицы в `.cs` пуст; `bd note` по каждому таску; push ветки.

---

## Фаза 2 — каденция (Т3–Т4): сердце задачи

### Task Т3: три волны, таймер в тиках, шаг сложности от часов

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs`
  (`WaveState` `:332-358` — новая форма)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`_wave` `:143` → массив; конструктор `:293`; `WaveRef` `:1026`;
  док `MatchRef` `:1033`; `CaptureSnapshot` `:2166`; `SaveState` `:2238`;
  `RestoreState` `:2328`; `HashWave` `:2603`; `SetWaveForTest` `:2363`)
- Modify: `client/Assets/Scripts/Simulation/Core/WorldSave.cs` (`:55`)
- Modify: `client/Assets/Scripts/Simulation/AI/WaveSystem.cs` (переписывание
  `Update` `:41-92`, `StartWave` `:111-183`, `PendingRef` `:251-268`;
  **удаление** `ZonelessWeights` `:34`, `SplitByZones` `:216-240`, переезда
  бюджета ядра `:146-150`, устаревшего дока `AliveCount` `:85-90`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs` (**удалить**
  `ZoneWeights` и `WavePause` из `WaveSimConfig`)
- Modify: `client/Assets/Scripts/Data/WaveConfig.cs` (**удалить** те же два
  поля), `SimConfigBuilder.cs` (маппинг `:142`, `:154`; валидация `:586`,
  `:603-631`), `SimConfigHash.cs` (`:133`, `:140`)
- Test: `WaveCadenceTests.cs`, `WorldLifecycleTests.cs`, `WaveTests.cs`,
  `WaveScalingTests.cs`, `WaveZoneTests.cs`, `EliteAndDirectorTests.cs`,
  `TestConfigs.cs`

**Interfaces:**

```csharp
// SimStates.cs — новая форма. WaveIndex УХОДИТ (Р315: сложность больше не
// функция числа пришедших волн, а функция часов; хранить счётчик, который ни
// на что не влияет, значит завести поле без читателя, правило 411).
// PhaseTimer (float) -> PhaseTicks (int), Р316/R-178: целые тики — единственная
// единица, в которой детерминированное сравнение имеет право производиться.
public struct WaveState
{
    public WavePhase Phase;
    public int PhaseTicks, AliveCount;
    public int PendingChaser, PendingGunner, PendingElite;

    public int PendingTotal => PendingChaser + PendingGunner + PendingElite;
}

// SimulationWorld.cs
WaveState[] _waves;                                   // длиной Zones.Count, один new в конструкторе
internal ref WaveState WaveRef(Zone zone) => ref _waves[(int)zone];
internal void SetWaveForTest(Zone zone, in WaveState w) => _waves[(int)zone] = w;

// WaveSystem.cs — долг снова три поля, зона ушла в индекс экземпляра.
internal static ref int PendingRef(ref WaveState w, MobType type);

// Шаг сложности — ЧИСТАЯ функция тика, нигде не хранится (Р206/Р315).
internal static int DifficultyStep(int tick, in WaveSimConfig cfg);
```

- **`DifficultyStep`** — единственный дом кривой:
  `1 + max(0, tick - TicksFromSeconds(cfg.FirstWaveDelay))
       / max(1, TicksFromSeconds(cfg.DifficultyStepSeconds))`, целочисленно.
  Его результат подставляется **и** в `CountForTest` (как `step - 1`,
  0-базовый вход формулы), **и** в `EliteShareFor`. Сама `CountForTest`
  (`:102-109`) и `EliteShareFor` (`:277-284`) **не трогаются** — меняется
  только то, что им передают.
- **Тик `Update`** (порядок обязателен, он же порядок потребления RNG):
  ранний выход по «нет живых» **не трогается** (`:50`) → один проход по
  мобам считает живых по `SpawnZone` в `Span<int> alive = stackalloc
  int[Zones.Count]` → цикл по кольцам `Outer → Middle → Core`:
  неактивное кольцо (беззонная арена → только `Outer` активен; `Core` при
  `w.Match.Phase != MatchPhase.Farm`) **замораживается идемпотентно**
  (`Phase = Waiting`, `PhaseTicks = 0`, три долга в ноль) и пропускается;
  иначе `PhaseTicks--`; при `<= 0` — `StartWave`; при `Phase == Active` —
  спавн долга (архетипы `Chaser → Gunner → Elite`, R-50) и проверка зачистки
  `PendingTotal == 0 && alive[z] == 0` → `WorldStats.WavesCleared++`,
  `Emit(WaveCleared, …, step)`, `Phase = Waiting`,
  `PhaseTicks = TicksFromSeconds(cfg.WavePauseByZone[z])`; в конце
  `wave.AliveCount = alive[z]`.
- **`StartWave(зона)`**: `count = CountForTest(cfg, step - 1, w.PlayerCount)`,
  доли по `EliteShareFor(зона, step, cfg)` и существующей `GunnerShare`,
  **присваивание** (не накопление) в три поля, `Emit(WaveStarted, …, step)`,
  `Phase = Active`, `PhaseTicks = TicksFromSeconds(cfg.WavePauseByZone[зона])`.
- **`CaptureSnapshot` кладёт в `RenderSnapshot.Wave` АГРЕГАТ** (не первое
  кольцо): `Phase = Active`, если активна хоть одна волна; `PhaseTicks` =
  минимум среди активных; `AliveCount` и три `Pending` — суммы. Массива в
  снимке не появляется, поэтому алиасинга там нет по построению.
- **`WorldSave.Waves`** — массив; аллокация в инициализаторе `SaveState` по
  образцу `Mobs = new MobState[_mobs.Length]` (`:2222`), копирование
  `System.Array.Copy` в обе стороны (идиома `SimulationWorld`), **никогда
  присваиванием ссылки**.
- **`HashWave` ×3** подряд в порядке `Outer → Middle → Core`, на том же месте
  последовательности, где сегодня стоит один вызов (`:2470`).

- [ ] **Step 1 (RED):** дописать в `WaveCadenceTests.cs` шесть тестов —
      первым тот, что бьёт прямо в дефект:

```csharp
[Test]
public void SecondWaveArrives_WithoutASingleKill()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    int pause = SimulationWorld.TicksFromSeconds(cfg.Wave.WavePauseByZone[(int)Zone.Outer]);
    int first = SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay);

    // Тикаем до конца первой волны внешнего кольца: её долг разошёлся,
    // мобы живы, никто не убит.
    for (int i = 0; i < first + pause / 2; i++) w.Tick(default);
    Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Outer).Phase,
        "первая волна внешнего кольца не стартовала");
    int aliveAfterFirst = w.WaveRef(Zone.Outer).AliveCount;
    Assert.Greater(aliveAfterFirst, 0, "первая волна не родила ни одного моба");

    // Ещё одна пауза — и вторая волна обязана прийти САМА, без единого убийства.
    for (int i = 0; i < pause + 2; i++) w.Tick(default);
    Assert.Greater(w.WaveRef(Zone.Outer).AliveCount, aliveAfterFirst,
        "вторая волна не пришла: сегодня очередь двигает только полный вайп арены");
}

[Test]
public void Rings_TickIndependently()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    // Пауза внешнего кольца короче средней (фикстура {2, 3, 3}) — значит
    // на общем отрезке внешнее успевает больше волн, чем среднее.
    int ticks = SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay + 2.5f);
    for (int i = 0; i < ticks; i++) w.Tick(default);
    Assert.Greater(w.WaveRef(Zone.Outer).AliveCount, 0);
    Assert.AreNotEqual(w.WaveRef(Zone.Outer).PhaseTicks, w.WaveRef(Zone.Middle).PhaseTicks,
        "кольца тикают одним таймером");
}

[Test]
public void ClearingARing_RestartsItsOwnTimerAtFullWindow()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay) + 2; i++)
        w.Tick(default);

    // Снимаем всех мобов внешнего кольца и закрываем его долг руками:
    // кольцо обязано засчитать зачистку и перезарядить СВОЙ таймер целиком.
    WaveState outer = w.WaveRef(Zone.Outer);
    outer.PendingChaser = outer.PendingGunner = outer.PendingElite = 0;
    w.SetWaveForTest(Zone.Outer, outer);
    w.ClearMobsForTest();
    int before = w.WorldStatsRef.WavesCleared;
    w.Tick(default);

    Assert.AreEqual(before + 1, w.WorldStatsRef.WavesCleared);
    Assert.AreEqual(WavePhase.Waiting, w.WaveRef(Zone.Outer).Phase);
    Assert.AreEqual(
        SimulationWorld.TicksFromSeconds(cfg.Wave.WavePauseByZone[(int)Zone.Outer]),
        w.WaveRef(Zone.Outer).PhaseTicks,
        "окно тишины должно быть ПОЛНЫМ, а не остатком");
}

[Test]
public void ClearingARing_DoesNotWeakenItsNextWave()
{
    // Р315: размер и состав берутся от ЧАСОВ, а не от числа пришедших волн,
    // поэтому кольцо, которое чистили, на том же тике получает ту же волну,
    // что и пассивное.
    SimConfig cfg = TestConfigs.Default();
    int tick = SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay + 6f);
    Assert.AreEqual(
        WaveSystem.DifficultyStep(tick, in cfg.Wave),
        WaveSystem.DifficultyStep(tick, in cfg.Wave),
        "шаг сложности обязан быть чистой функцией тика");
    Assert.Greater(WaveSystem.DifficultyStep(tick, in cfg.Wave),
        WaveSystem.DifficultyStep(SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay),
            in cfg.Wave),
        "кривая сложности не растёт со временем");
}

[Test]
public void ZonelessArena_RunsOnlyTheOuterRing()
{
    SimConfig cfg = TestConfigs.OpenField();   // беззонная фикстура
    var w = new SimulationWorld(7, cfg);
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay) + 4; i++)
        w.Tick(default);
    Assert.AreEqual(WavePhase.Waiting, w.WaveRef(Zone.Middle).Phase);
    Assert.AreEqual(0, w.WaveRef(Zone.Middle).PendingTotal);
    Assert.AreEqual(0, w.WaveRef(Zone.Core).PendingTotal);
    Assert.Greater(w.WaveRef(Zone.Outer).AliveCount, 0);
}

[Test]
public void CoreFreezes_WhenTheDirectorIsAwake()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay) + 4; i++)
        w.Tick(default);
    w.SetMatchPhaseForTest(MatchPhase.DirectorActive);
    w.Tick(default);
    Assert.AreEqual(WavePhase.Waiting, w.WaveRef(Zone.Core).Phase);
    Assert.AreEqual(0, w.WaveRef(Zone.Core).PhaseTicks);
    Assert.AreEqual(0, w.WaveRef(Zone.Core).PendingTotal);
}
```

  ⚠ Имена швов `ClearMobsForTest`/`SetMatchPhaseForTest` **проверить в
  `SimulationWorld`**: если такого шва нет — использовать существующий
  (`SetMatchForTest`, `TestWorlds.*`) и **не заводить новый ради теста**;
  если ни одного нет, шов добавляется рядом с `SetWaveForTest` по той же
  форме и упоминается в отчёте таска.
- [ ] **Step 2:** заглушки (`WaveRef(Zone)`, `DifficultyStep` → константа 1,
      новая форма `WaveState`) до **компиляции**, затем R-FILTER
      `WaveCadenceTests` → **EXIT=2**, шесть наблюдаемых FAIL ассертов.
- [ ] **Step 3 (GREEN, часть 1 — состояние):** массив из трёх, `WaveRef(Zone)`,
      `SetWaveForTest(Zone, …)`, `WorldSave.Waves` с `Array.Copy` и
      аллокацией, `HashWave` ×3, агрегат в `CaptureSnapshot`, перецеленные
      доки `MatchRef` и `AliveCount`.
- [ ] **Step 4 (GREEN, часть 2 — каденция):** переписать `Update`/`StartWave`/
      `PendingRef`; **удалить** `SplitByZones`, `ZonelessWeights`, переезд
      бюджета ядра, `ZoneWeights` и `WavePause` во всех шести местах
      (`SimConfig.cs`, `WaveConfig.cs`, `SimConfigBuilder.cs:142,154,586,603-631`,
      `SimConfigHash.cs:133,140`).
- [ ] **Step 5:** правка ломающихся тестов **поимённо**: `WaveZoneTests` —
      удалить `SplitByZones_*` и `CoreBudgetMovesToMiddle_TotalUnchanged`,
      переписать `PendingRef_*`, `Debt_IsNeverLostOnRounding`,
      `CoreLosesItsWaveBudget_AfterActivation`,
      `CoreDoesNotRegainBudget_AfterTheDirectorDies`; `WaveTests` и
      `WaveScalingTests` — на новую форму (включая
      `Count_ThreePlayersScalesAndRoundsToTen` и четыре ручные фикстуры
      `new WaveSimConfig`); `NoAlivePlayers_WaveDirectorFreezes_…` (`:222`)
      **расширяется на три кольца, а не дублируется**; `EliteAndDirectorTests:721`.
- [ ] **Step 6:** `WorldLifecycleTests` — свип **по каждой зоне отдельно**
      (цикл `for (int z = 0; z < Zones.Count; z++)` с
      `SetWaveForTest((Zone)z, …)`), иначе `HashWave`, усечённый до
      `waves[0]`, прошёл бы тест; квитанция бампов пересчитывается целиком:
      `WaveState` 13 → **6 × 3 = 18**, `MobState` 10 (из Т1).
- [ ] **Step 7 (мутации M1/M2/M3/M6/M9/M11/M12, предсказания ДО прогона в
      `$SDD/task-3-mutations-predicted.md`):** M1 — снять `PhaseTicks--` в
      `Active`; M2 — снять `alive[z] == 0` из зачистки; M3 — `PhaseTicks = 0`
      на зачистке; **M6 — снять гвард неактивного кольца: жертв ДВЕ**
      (`CoreFreezes_WhenTheDirectorIsAwake` И
      `ZonelessArena_RunsOnlyTheOuterRing`, потому что гвард пропускает через
      себя оба случая); M9 — `step` от счётчика волн вместо часов;
      M11 — накапливать долг вместо присваивания; M12 — хешировать только
      `waves[Outer]`. Откат каждой — **`cp` и md5**, НЕ `git checkout` (350).
- [ ] **Step 8:** R-TEST полный → красных **ровно три** (golden); `total`
      глазами; **записать время прогона** (Р325/§4 спеки: рост более чем
      вдвое против ~40–60 с — находка, а не норма).
- [ ] **Step 9:** ГЕЙТ-КОДОГЕН (тронут `WaveSimConfig`, входящий в
      `simConfigHash`) → пусто.
- [ ] **Step 10:** R-COMMIT `feat(app-ggvz): Т3 — независимая каденция волн по
      кольцам, таймер в тиках, сложность от часов захода`.

### Task Т4: потолок численности кольца и сглаживание притока

**Files:**
- Modify: `client/Assets/Scripts/Simulation/AI/WaveSystem.cs`
  (`SpawnPendingOfType` `:292-324`)
- Test: `client/Assets/Tests/EditMode/WaveCadenceTests.cs`

**Interfaces:** новых публичных имён не появляется. Меняется тело цикла
спавна: два гварда перед поиском места и счётчик спавнов за тик.

```csharp
// Гвард потолка стоит ПЕРЕД поиском места — ровно там же и по той же причине,
// что и существующий резерв Директора (:312-318): кольцу, которому нельзя
// спавнить, не нужно тратить поиск кандидата. НЕ трогает MobSpawnsSkipped:
// тот счётчик означает «мир упёрся в свой ФИЗИЧЕСКИЙ потолок» (контракт
// SpawnMob), а потолок кольца — политика директора волн.
if (alive[(int)zone] >= cfg.MaxAliveByZone[(int)zone]) return;   // долг остаётся
if (spawnedThisTick >= cfg.MaxSpawnsPerTick) return;             // долг остаётся
```

- `alive[]` — тот же `Span<int>`, что считает `Update`; **успешный спавн
  инкрементит его на месте**, иначе потолок переехали бы внутри одного тика.
- `spawnedThisTick` — счётчик **на кольцо**, обнуляется в начале обработки
  кольца.

- [ ] **Step 1 (RED):** четыре теста в `WaveCadenceTests.cs`:

```csharp
[Test]
public void RingAtItsCeiling_DoesNotSpawn_AndKeepsItsDebt()
{
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxAliveByZone = new[] { 4, 16, 8 };
    var w = new SimulationWorld(7, cfg);
    int skippedBefore = w.WorldStatsRef.MobSpawnsSkipped;
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay) + 30; i++)
        w.Tick(default);

    Assert.LessOrEqual(w.WaveRef(Zone.Outer).AliveCount, 4, "потолок кольца перееден");
    Assert.Greater(w.WaveRef(Zone.Outer).PendingTotal, 0, "долг обязан сохраниться");
    Assert.AreEqual(skippedBefore, w.WorldStatsRef.MobSpawnsSkipped,
        "потолок кольца — не отказ арены, MobSpawnsSkipped расти не должен");
}

[Test]
public void CeilingIsPerRing_NotPerArena()
{
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxAliveByZone = new[] { 1, 16, 8 };
    var w = new SimulationWorld(7, cfg);
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay) + 30; i++)
        w.Tick(default);
    Assert.LessOrEqual(w.WaveRef(Zone.Outer).AliveCount, 1);
    Assert.Greater(w.WaveRef(Zone.Middle).AliveCount, 1,
        "среднее кольцо остановилось из-за чужого потолка");
}

[Test]
public void WaveDoesNotOvershootTheCeiling_WithinASingleTick()
{
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxAliveByZone = new[] { 3, 16, 8 };
    cfg.Wave.MaxSpawnsPerTick = 64;          // сглаживание намеренно снято
    var w = new SimulationWorld(7, cfg);
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay) + 4; i++)
        w.Tick(default);
    Assert.LessOrEqual(w.WaveRef(Zone.Outer).AliveCount, 3,
        "инкремент alive внутри тика потерян — волна перелетела потолок");
}

[Test]
public void WaveArrivesGradually_NotInASingleTick()
{
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxSpawnsPerTick = 1;
    var w = new SimulationWorld(7, cfg);
    int first = SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay);
    for (int i = 0; i < first + 1; i++) w.Tick(default);
    // Ровно один моб на кольцо за первый тик волны, остальное — долг.
    Assert.AreEqual(1, w.WaveRef(Zone.Outer).AliveCount);
    Assert.Greater(w.WaveRef(Zone.Outer).PendingTotal, 0);
}
```

- [ ] **Step 2:** R-FILTER `WaveCadenceTests` → **EXIT=2**, четыре красных.
- [ ] **Step 3 (GREEN):** два гварда + инкремент + счётчик на кольцо.
- [ ] **Step 4:** R-FILTER `WaveCadenceTests` → PASS.
- [ ] **Step 5 (мутации M4/M8/M10, предсказания ДО прогона):** M4 — `>=` → `>`
      в гварде потолка (жертва: `RingAtItsCeiling_…`, население станет 5 при
      потолке 4); M8 — снять инкремент `alive[]` (жертва:
      `WaveDoesNotOvershootTheCeiling_WithinASingleTick`, население уйдёт к
      размеру волны); M10 — снять гвард `MaxSpawnsPerTick` (жертва:
      `WaveArrivesGradually_NotInASingleTick`, `AliveCount` станет равен
      размеру волны в первом же тике).
- [ ] **Step 6:** тест инварианта ядра (спека §4 п. 12):

```csharp
[Test]
public void RingWhoseCeilingIsBelowItsWave_NeitherHangsNorClears()
{
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxAliveByZone = new[] { 24, 16, 1 };   // ядро: волна элит против потолка 1
    var w = new SimulationWorld(7, cfg);
    int cleared = w.WorldStatsRef.WavesCleared;
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay) + 120; i++)
        w.Tick(default);
    Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Core).Phase);
    Assert.Greater(w.WaveRef(Zone.Core).PendingTotal, 0, "долг обязан сохраняться");
    Assert.LessOrEqual(w.WaveRef(Zone.Core).AliveCount, 1);
    Assert.AreEqual(cleared, w.WorldStatsRef.WavesCleared,
        "кольцо с потолком ниже волны не может быть вычищено — это инвариант ядра");
}
```

- [ ] **Step 7:** R-TEST полный → красных ровно три; время прогона записать.
- [ ] **Step 8:** R-COMMIT `feat(app-ggvz): Т4 — потолок численности кольца и
      сглаживание притока`.

**Гейт фазы 2:** R-TEST кроме трёх golden зелёный; все семь мутаций фазы
убиты и предсказания сверены; два фазовых ревьюера (Explore: спека-соответствие
и качество/арифметика); `bd note`; push.

---

## Фаза 3 — данные, провод, экран (Т5–Т7)

### Task Т5: мёртвые ссылки и доставка чисел в `.asset`

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/Geometry.cs` (`:322-347` —
  тексты двух отказов `ZoneSpawnRingRadius`)
- Modify: `client/Assets/Scripts/Simulation/Loot/ContainerStore.cs` (`:144-148`)
- Modify: `client/Assets/Scripts/Data/LootConfig.cs` (`:19`),
  `client/Assets/Scripts/Data/SimConfigBuilder.cs` (`:199`)
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
  (новый `ApplyWaveCadence` + маркер-ключ `:798`)
- Modify (через бутстрап, руками — НЕТ):
  `client/Assets/Data/WaveConfig.asset`

**Interfaces:**

```csharp
// StageOneSceneBootstrap — ДВА РАЗНЫХ МЕХАНИЗМА, и их нельзя смешивать:
//
// 1) Появление и исчезновение полей — маркер-ключ:
//    EnsureAssetHasKey(waveConfig, $"{DataDir}/WaveConfig.asset", "DifficultyStepSeconds")
//    (прежний ключ EliteShareOuterCap уходит; переезжают ТРИ вещи —
//     комментарий `// sync-marker key — keep LAST` на поле, аргумент здесь,
//     и хвостовая пометка `// … (was EliteShareOuterCap, app-ggvz)`).
//
// 2) Правка СУЩЕСТВУЮЩИХ значений — отдельный одноразовый гейт, ключённый на
//    СТАРОМ значении (правило 413), по образцу ApplyPlaytestOneArena (:2369):
static void ApplyWaveCadence(WaveConfig wave)   // только SetIfDifferent
{
    // BaseCount 4 -> 16 (owner decision К5), EliteShareOuterGrowth 0.02 -> 0.007
    // (Р311: the periphery's elite ceiling must land on minute 12, ADR-001 §3.1).
}
```

- ⚠ **Ключ гейта — `"ZoneWeights:"`** (Р319), а НЕ `"BaseCount: 4"`: гейт
  читается подстрокой (`File.ReadAllText(...).Contains(...)`), и
  `"BaseCount: 4"` совпал бы с будущим `BaseCount: 40` — первый же тюнинг
  владельца в эту сторону снова открыл бы гейт и **затёр его число обратно на
  16**. `ZoneWeights` исчезает из ассета этой задачей и вернуться не может.
- ⚠ **Порядок внутри одного `Apply` обязателен:** `ApplyWaveCadence`
  (читает ключ) выполняется **до** `EnsureAssetHasKey` (переписывает и
  сохраняет ассет). **Step 1 проверяет ключ на месте** — если Unity уже
  переcериализовал ассет и `ZoneWeights:` из него исчез, гейт не откроется и
  числа не доедут молча; тогда ключ заменяется на **построчный**
  `"\n  BaseCount: 4\n"` (перевод строки якорит совпадение, `BaseCount: 40`
  не матчится), и замена записывается в отчёт таска.

- [ ] **Step 1 (проверка ключа ПЕРЕД работой):**
      `grep -c "^  ZoneWeights:" client/Assets/Data/WaveConfig.asset` → **1**.
      Ноль — переходить на построчный ключ `BaseCount` (см. выше).
- [ ] **Step 2:** переписать шесть мёртвых ссылок: два текста отказа
      `Geometry.ZoneSpawnRingRadius` (`:338-347`) — обоснование переводится с
      «`ZonelessWeights` + `SplitByZones`» на новый инвариант «неактивное
      кольцо заморожено, долг до `Middle`/`Core` на беззонной арене не
      доходит»; `ContainerStore.cs:144-148`, `LootConfig.cs:19`,
      `SimConfigBuilder.cs:199` — перецелить на живые прецеденты.
- [ ] **Step 3:** написать `ApplyWaveCadence` + переезд маркер-ключа.
- [ ] **Step 4:** R-APPLY → EXIT=0; `git diff -- client/Assets/Data/` содержит
      **ровно**: `BaseCount: 16`, `EliteShareOuterGrowth: 0.007`, исчезнувшие
      `ZoneWeights`/`WavePause`, появившиеся `WavePauseByZone [20,30,30]`,
      `MaxAliveByZone [150,110,10]`, `MaxSpawnsPerTick: 2`,
      `DifficultyStepSeconds: 20`. Ничего сверх.
- [ ] **Step 5:** коммит артефактов, затем **R-IDEM** → `git status
      --porcelain -- client/` и `git diff -- client/` пусты.
- [ ] **Step 6:** R-TEST полный → красных ровно три.
- [ ] **Step 7:** R-COMMIT `feat(app-ggvz): Т5 — числа каденции в .asset,
      мёртвые ссылки на удалённый бюджет зон переписаны`.

### Task Т6: мировой номер волны в снимке и на проводе

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/RenderSnapshot.cs`
  (новое поле `WaveNumber`; `CopyFrom` `:426`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`CaptureSnapshot` — заполнение `WaveNumber`)
- Modify: `client/Assets/Scripts/Networking/Server/SnapshotAssembler.cs`
  (`:1713-1715` — запись блока)
- Modify: `client/Assets/Scripts/Editor/LongRunHarness.cs` (`:82`)
- Test: `client/Assets/Tests/EditMode/InterpolationBufferTests.cs`
  (филлер `:925-940`), `SnapshotAssemblerTests.cs`, `WorldLifecycleTests.cs`

**Interfaces:**

```csharp
// RenderSnapshot — НЕ хешируется, поэтому поле бесплатно.
/// The raid's difficulty step, a pure function of the tick (Р315). World-wide
/// and MONOTONIC: unlike a per-ring counter it never falls, so the HUD line
/// cannot flicker when a collector walks across a zone boundary.
public int WaveNumber;
```

- Блок волны на проводе — **те же 4 байта**: `phase` = фаза агрегата,
  `waveIndex` u16 = `WaveNumber`, `aliveCount` u8 = сумма живых.
  `ProtocolVersion` **не двигается**.
- ⚠ Байт живых **насыщается** (270 > 255; писатель уже режет через
  `math.min(..., byte.MaxValue)`), и читателя у него сегодня нет вовсе — это
  записывается в док и заводится side-quest'ом, а правило валидации «потолок
  ≤ 255» **не вводится** (оно защищало бы несуществующего читателя и мешало
  владельцу поднять потолок кольца выше 255 на плейтесте).

- [ ] **Step 1 (RED):** тест на монотонность и на агрегат:

```csharp
[Test]
public void WaveNumber_IsMonotonic_AndIndependentOfWhereTheCollectorStands()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    var frame = new RenderSnapshot(in cfg);
    int prev = 0;
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(10f); i++)
    {
        w.Tick(default);
        w.CaptureSnapshot(frame);
        Assert.GreaterOrEqual(frame.WaveNumber, prev, "номер волны упал");
        prev = frame.WaveNumber;
    }
    Assert.Greater(prev, 1, "номер волны не вырос за десять секунд захода");
}

[Test]
public void SnapshotWave_IsTheWorldAggregate_NotTheFirstRing()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    var frame = new RenderSnapshot(in cfg);
    for (int i = 0; i < SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay) + 10; i++)
        w.Tick(default);
    w.CaptureSnapshot(frame);
    int sum = w.WaveRef(Zone.Outer).AliveCount + w.WaveRef(Zone.Middle).AliveCount
        + w.WaveRef(Zone.Core).AliveCount;
    Assert.AreEqual(sum, frame.Wave.AliveCount);
}
```

- [ ] **Step 2:** R-FILTER → **EXIT=2**, два наблюдаемых FAIL.
- [ ] **Step 3 (GREEN):** поле, заполнение в `CaptureSnapshot`, агрегат,
      `CopyFrom` (цикл по полям — идиома этого файла), запись блока в
      сборщике, `LongRunHarness.cs:82` → `snapshot.WaveNumber`.
- [ ] **Step 4:** филлер рефлексивного сторожа `InterpolationBufferTests`
      (`:925-940`) — под новую форму `WaveState` (девять `Pending` → три) и
      новое поле `WaveNumber`; сторож `CopyFrom_CopiesEveryPublicField_ByReflection`
      обязан остаться зелёным **без исключений в словаре**.
- [ ] **Step 5:** ГЕЙТ-КОДОГЕН → пусто (тронут `Ring.Networking`).
- [ ] **Step 6:** R-TEST полный → красных ровно три.
- [ ] **Step 7:** R-COMMIT `feat(app-ggvz): Т6 — мировой монотонный номер
      волны в снимке и на проводе`.

### Task Т7: номер волны и вспышка анонса в HUD

**Files:**
- Modify: `client/Assets/Scripts/Presentation/HudController.cs`
  (`:202` строка волны, `:115` соседство таймеров, `LateUpdate` `:245`,
  `HandleWorldRestarted` `:364-384`)
- Modify: `client/Assets/Scripts/Data/GameFeelConfig.cs` (новое поле +
  переезд маркера с `ContainerVisualScale` `:546`)
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
  (`SetRef` строки, `EnsureAssetHasKey` для `GameFeelConfig` `:788`)
- Test: `client/Assets/Tests/EditMode/HudPhaseLineTests.cs`

**Interfaces:**

```csharp
// Чистый статический шов — правило вспышки живёт ЗДЕСЬ, а не в MonoBehaviour,
// потому что новая ветка продакшена обязана получить свидетеля в том же таске
// (369/383), а прецедент чистого шва в HUD уже есть (HudPhaseLineTests
// тестирует PhaseWord/Clock).
public static float WaveAnnounceTimerAfter(int previousWaveNumber, int waveNumber,
    float timerNow, float announceSeconds, float deltaSeconds);
```

- Семантика: номер вырос → вернуть `announceSeconds` (**перезарядка**, не
  очередь); иначе — `max(0, timerNow - deltaSeconds)`. Одна вспышка на шаг
  сложности, то есть раз в `DifficultyStepSeconds`, независимо от числа колец.
- Строка `_waveText` показывает `«ВОЛНА » + curr.WaveNumber`; вспышка — смена
  цвета на `announceSeconds`. Виджет читаемый, поэтому **`raycastTarget =
  false`** (правило 34: лежащий поверх читаемый виджет молча съедает клик —
  прецедент `app-pih0`/`app-d0no`), и бутстрап чинит **уже существующий**
  объект, а не только вновь созданный.
- Гашение на `WorldRestarted` — в существующий список (`:364-384`).

- [ ] **Step 1 (RED):** в `HudPhaseLineTests.cs`:

```csharp
[Test]
public void WaveAnnounce_RearmsOnGrowth_AndDecaysOtherwise()
{
    // Рост номера перезаряжает таймер целиком.
    Assert.AreEqual(1.5f, HudController.WaveAnnounceTimerAfter(3, 4, 0.2f, 1.5f, 0.016f), 1e-4f);
    // Тот же номер — таймер гаснет, а не копится.
    Assert.AreEqual(1.484f, HudController.WaveAnnounceTimerAfter(4, 4, 1.5f, 1.5f, 0.016f), 1e-4f);
    // Ниже нуля не уходит.
    Assert.AreEqual(0f, HudController.WaveAnnounceTimerAfter(4, 4, 0.01f, 1.5f, 0.016f), 1e-4f);
}
```

- [ ] **Step 2:** R-FILTER `HudPhaseLineTests` → **EXIT=2**, три ассерта.
- [ ] **Step 3 (GREEN):** чистый шов + его использование в `LateUpdate`;
      поле `WaveAnnounceSeconds` (дефолт 1.5) в `GameFeelConfig` последним +
      переезд маркера (три вещи); строка HUD читает `curr.WaveNumber`.
- [ ] **Step 4:** R-APPLY → EXIT=0; сверить **набор `m_Name`** в `Main.unity`
      до и после (правило 14) и порядок отрисовки читать **из сцены**, а не
      из порядка вызовов бутстрапа (урок 423).
- [ ] **Step 5:** коммит артефактов → **R-IDEM** → пусто.
- [ ] **Step 6:** R-TEST полный → красных ровно три.
- [ ] **Step 7:** R-COMMIT `feat(app-ggvz): Т7 — номер волны и вспышка анонса
      в HUD`.

**Гейт фазы 3:** R-TEST кроме трёх golden зелёный; R-IDEM сошёлся дважды;
ГЕЙТ-КОДОГЕН пуст; свип кириллицы и британизмов пуст; два фазовых ревьюера;
push; jsonl-chore.

---

## Фаза 4 — перепин и гейт (Т8) → веха В4

### Task Т8: перепин №4, замеры, образ, отчёт

**Files:**
- Modify: `client/Assets/Tests/EditMode/DeterminismTests.cs` (`:345`, `:986`,
  `:1114`, `:1269`)
- Create: `$SDD/task-ggvz-report.md`

- [ ] **Step 1:** R-TEST полный **до** перепина; выписать три `But was: <N>`
      из xml разбором питоном.
- [ ] **Step 2 (R-GOLDEN, санкция №4 — решение владельца К9):** вписать три
      hex-константы + десятичные дубли + письменное обоснование, называющее
      **шесть** причин сдвига: форма `WaveState` и три экземпляра в хеше;
      таймер в целых тиках; шаг сложности от часов; сглаживание притока;
      `MobState.SpawnZone`; исчезновение весов и паузы из конфига.
      **В том же коммите** поправить обоснование перепина №2 (`:986`), которое
      сегодня называет `ZoneWeights {0.45, 0.45, 0.10}` причиной прошлого
      сдвига, — иначе репозиторий будет держать два противоречивых утверждения
      о самой охраняемой константе.
- [ ] **Step 3:** R-TEST полный → **1546 + новые, красных НОЛЬ**; `total`
      глазами; **время прогона записать в отчёт**.
- [ ] **Step 4:** снять **новый** md5 `DeterminismTests.cs`
      (`md5sum client/Assets/Tests/EditMode/DeterminismTests.cs`) и записать в
      отчёт — прежний `02906aadbb2574b409ded7342f29ec75` мёртв.
- [ ] **Step 5:** R-COMMIT **отдельным коммитом** (R-23)
      `test(app-ggvz): перепин golden №4 — каденция волн по кольцам`.
- [ ] **Step 6:** **шесть** целей R-BUILD ФОНОМ; вердикт каждой — по строке
      «Exiting batchmode successfully».
- [ ] **Step 7:** R-IMAGE + доставка на хост + сверка метки ревизии
      (`docker image inspect --format '{{index .Config.Labels
      "org.opencontainers.image.revision"}}'`).
- [ ] **Step 8 (замер, §7 п. 4 спеки):** стенд втроём, `--cpus=1 --memory=1g`.
      Снять и записать: CPU, `tickAvg`/`tickMax`, `framesMissing`,
      **`DroppedEntities`**, долю видимых мобов, попавших в кадр,
      `MobSpawnsSkipped`, `PickupSpawnsSkipped`/`ContainerSpawnsSkipped`.
      ⚠ Трафик против порога 40 КБ/с мерить **тоже**, но он ничего не
      докажет: при `SnapshotMaxBytes 1000` и `MobRecordBytes 9` в кадр
      физически влезает ~60–80 записей мобов, поэтому порог структурно
      недостижим — деградирует не трафик, а полнота кадра.
- [ ] **Step 9:** сверка сборки, отдаваемой владельцу, **содержимым**:
      имена метаданных — `strings -a`, **литералы — `strings -a -el`** (418).
- [ ] **Step 10:** отчёт в `$SDD/task-ggvz-report.md`; `bd note app-ggvz`
      коротко; push; jsonl-chore.
- [ ] **Step 11 (СТОП):** **веха В4 — живой забег владельца** по восьми
      пунктам §5 спеки. Стенд ботов её не заменяет (417).

---

## Декомпозиция bd (создать ДО Т1, после апрува плана)

```bash
cd "$APP_REPO"
# восемь сабтасков, parent-child к app-ggvz + blocks-цепочка по порядку
bd create "Т1: приписка моба к кольцу, Zones, один дом чисел архетипа" -t task -p 0
bd create "Т2: числа каденции в конфиге и пять правил валидации"        -t task -p 0
bd create "Т3: три волны, таймер в тиках, сложность от часов"           -t task -p 0
bd create "Т4: потолок численности кольца и сглаживание притока"        -t task -p 0
bd create "Т5: числа в .asset и мёртвые ссылки на бюджет зон"           -t task -p 0
bd create "Т6: мировой номер волны в снимке и на проводе"               -t task -p 0
bd create "Т7: номер волны и вспышка анонса в HUD"                      -t task -p 0
bd create "Т8: перепин golden №4, замеры, образ, веха В4"               -t task -p 0
# для каждого: bd dep add <ТN> app-ggvz --type parent-child
# цепочка:     bd dep add <ТN+1> <ТN>
```

Side-quest'ы задачи (заводятся по ходу, `discovered-from app-ggvz`):
мёртвый байт `aliveCount` на проводе; звук анонса хозяина; индекс `id → слот`
в сборщике снапшота вместо линейного скана; пространственная сетка
расталкивания; обрезка бумажных потолков после замера.

---

## Отклонения от спеки (записаны намеренно, правило 22)

1. **Р327 исполняется через `ref readonly` на существующем `MobConfigFor`, а
   не новым `float MobRadiusFor(MobType)`** (Т1). Причина: отдельный
   аксессор завёл бы **второй** switch по тому же домену, то есть второй дом
   отображения «архетип → числа» (урок 279). `ref readonly` даёт тот же
   эффект — ноль копий 30-полевой структуры — не заводя второго дома, и
   снимает копию у всех потребителей, а не у двух. Исходная совместимость
   полная: call-site'ы, копирующие в локальную переменную, компилируются как
   были.
2. **Спека §10 называла семь тасков, план даёт восемь** (Т3 разделён на
   «состояние + каденция» и «потолки + сглаживание»). Причина: граница
   проходит там, где ревьюер может принять одно и отвергнуть другое, а
   объединённый таск не помещался бы в один цикл тестов.

## Self-review плана (выполнен автором)

- **Покрытие спеки.** §3.2 → Т3; §3.3 → Т3; §3.4 → Т2 (числа) + Т4 (гварды);
  §3.5 → Т1; §3.6 → Т3 (заморозка ядра) + Т3 (удаление переезда); §3.7 → Т3
  (удаление) + Т5 (мёртвые ссылки); §3.8 → Т2 (поля и правила) + Т5
  (доставка); §3.9 → Т6; §3.10 → Т7; §4 (перепин, мутации, бюджет времени) →
  Т1–Т4 и Т8; §5 (веха) → Т8 Step 11; §7 (DoD) → гейты фаз и Т8.
  **Гэпов не найдено.**
- **Плейсхолдеры.** «TBD»/«TODO»/«аналогично таску N»/«добавить обработку
  ошибок» — нет; каждый шаг с кодом несёт код.
- **Согласованность типов.** `Zones.Count`, `Zone`, `WaveState` (шесть полей),
  `WaveRef(Zone)`, `SetWaveForTest(Zone, in WaveState)`, `PendingRef(ref
  WaveState, MobType)`, `DifficultyStep(int, in WaveSimConfig)`,
  `SpawnMob(MobType, float2, Zone)`, `SpawnMobForTest(MobType, float2, Zone =
  Zone.Outer)`, `MobConfigFor(MobType) → ref readonly MobSimConfig`,
  `RenderSnapshot.WaveNumber`, `HudController.WaveAnnounceTimerAfter(int, int,
  float, float, float)` — имена и сигнатуры совпадают во всех тасках,
  где встречаются.
- **Два места, где исполнитель обязан СВЕРИТЬ, а не поверить плану**, и оба
  названы в шагах: имена тест-швов `ClearMobsForTest`/`SetMatchPhaseForTest`
  (Т3 Step 1) и присутствие ключа гейта `ZoneWeights:` в ассете (Т5 Step 1).
