# План имплементации: каденция волн по кольцам (app-ggvz)

> **For agentic workers:** REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.
> Модели: implementer per task = **sonnet** для Т1/Т2/Т6/Т7 (механика по готовым
> формулам и данным), **opus** — Т3 (форма состояния и хеш), Т4 (директор волн),
> Т5 (потолки и сглаживание); **fable** — ревью фаз. Ревьюеры = 2 × Explore
> (спека-соответствие + качество/арифметика). **Все прогоны Unity, вердикты
> субагентов, гейты и веха — main-агент лично** (R-14: субагенты Unity не
> запускают вовсе; R-98: `.meta` не пишут). Шаги — чекбоксы `- [ ]`.

**Goal:** вернуть волнам темп — независимая каденция на каждое кольцо арены,
сложность от часов захода, окно тишины, которое зарабатывается зачисткой,
потолок численности на кольцо и сглаженный приток, вместо сегодняшнего
единственного пути «следующая волна только после полного вайпа всей арены».

**Architecture:** всё игровое остаётся в `Ring.Simulation` чистым C# (CR 1).
`WaveState` из одного экземпляра на мир становится массивом из трёх (по
кольцу), долг снова тремя полями внутри каждого; таймер фазы переводится в
целые тики и тикает в обеих фазах; `WaveIndex` перестаёт быть счётчиком и
получает шаг сложности из часов захода; приписка моба к кольцу — параметр
спавнера. **Провод не двигается ни на байт, и ни один потребитель волны вне
`Simulation` не меняет сигнатур**: блок волны остаётся мировым и четырёх байт,
`RenderSnapshot.Wave` остаётся одним `WaveState` (теперь агрегатом трёх колец),
`ProtocolVersion` остаётся 3.

**Tech Stack:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics, FishNet
4.7.2, Docker. **Новых пакетов задача не вводит** (CR 9).

**Спека:** `docs/superpowers/specs/2026-08-23-wave-cadence-per-zone-spec.md`
**v4** (К1–К10 владельца, Р301–Р335; self-review спеки — 13 Critical;
self-review плана — 25 Critical, §6c). **План против спеки — верить спеке.**

**Статус плана:** **v2 — после self-review по `review_plan.md`** (четыре
Explore-субагента: A корректность кода, B конвенции, C переиспользование,
D TDD/полнота; **25 Critical**, ~40 прочих; каждая критическая проверена
главным агентом лично по коду). Что изменилось против v1 — раздел
«Что исправил self-review» в конце файла.

---

## Global Constraints (каждый таск обязан соблюдать)

- **Пути:** `APP_REPO="/home/brolin/Documents/!_MY_Proj/The Ring/app"`
  (bd — ТОЛЬКО отсюда); `WT="$APP_REPO/.worktrees/feature-app-5nu-stage2-network"`
  — cwd всех команд; ветка `feature/app-35g-stage3-extraction` **уже
  существует** и не пересоздаётся — **worktree не удалять**;
  `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH=<scratchpad ТЕКУЩЕЙ сессии>`;
  `SDD="$WT/.superpowers/sdd/2026-08-17-stage3-extraction-plan"`.
- **Стартовые счётчики:** **1546** EditMode-тестов, все зелёные; соло-golden
  `0xC19F5FDDF6A8F148UL` (`DeterminismTests.cs:1114`), мульти
  `0x259DC0AFF155D907UL` (`:1269`), извлечение `0xC43B54B02689DC8FUL`
  (`:345`); md5 `DeterminismTests.cs` = `02906aadbb2574b409ded7342f29ec75`.
- ⚠ **ЧТО КРАСНОЕ НА КАЖДОМ ТАСКЕ — ТАБЛИЦА, А НЕ ОБЕЩАНИЕ.** v1 обещала
  «везде ровно три golden» и ошибалась радикально (находка D-C1). Ожидание
  проверяется по этой таблице; **любое расхождение — стоп и разбор**, а не
  «наверное, так и надо»:

  | Таск | Ожидаемые красные |
  |---|---|
  | Т1 | три golden (форма `MobState` вошла в хеш) |
  | Т2 | три golden (числа фикстур вошли в `simConfigHash`) |
  | Т3 | три golden. ⚠ Компиляция обязана быть ЧИСТОЙ: все читатели `WaveState` правятся В ЭТОМ ЖЕ таске |
  | Т4 | три golden + до правки шага 6 — `SimConfigHashTests.EveryConfigNumberAffectsHash_Wave` (держит имя `"ZoneWeights"` СТРОКОЙ, компиляцию не ломает) |
  | Т5 | три golden |
  | Т6 | три golden |
  | Т7 | три golden |
  | Т8 | **ноль** после перепина |

- **Запретный список:** не менять `client/CLAUDE.md`, `.github/CODEOWNERS`,
  `.gitattributes`, `client/ProjectSettings/**` (кроме правок бутстрапов),
  `Packages/**` (CR 9), `ProtocolVersion`, `InputCodec.SizeBytes`, размер и
  состав блоков провода, `MobRecord`.
  **`client/Assets/Data/*.asset` руками не редактировать** — только бутстрапом.
- **Simulation меняется** — строго TDD (CR 2), без UnityEngine (CR 1).
- **Два источника чисел** (спека §0): `.asset` — числа игры; C#-дефолты и
  `TestConfigs` — числа тестов. Ожидания в тестах — только фикстурными
  выражениями. ⚠ **Фикстуры зонные** (`TestConfigs.Default()` несёт
  `ZoneRadius {65,130}`, `:308`), числа волн у них **свои и скромные** (Т2),
  и задаются **в одном месте** — `Default()`, от которой производны все семь
  вариантов (находки B/A: дублировать в каждом — нарушение правила 2).
- **Орфография идентификаторов — американская.**
- ⚠ **Свип кириллицы в `.cs` — с явным исключением для сообщений ассертов**
  (находка B-Important). Свип `git diff -U0 -- '*.cs' | grep -E "^\+" |
  grep -P "[а-яА-Я]{4,}"` ловит **прозу и комментарии**; русские сообщения
  `Assert.*` в `Tests/EditMode` — законный прецедент репозитория (63 живых
  литерала) и находкой не считаются. Комментарии и доки — только английские.
- **ГЕЙТ-ОТКАТ (после КАЖДОГО Unity-прогона):**
  `git status --porcelain -- client/Packages client/Assets/Settings
  .gitattributes client/ProjectSettings "client/Assets/TextMesh Pro"` → пусто.
- **ГЕЙТ-ЛОГ:** `grep -E "error CS|Shader error|Failed to import|
  NullReferenceException|Exception" <лог>` → пусто. ⚠ **`error CS` в этом
  плане недопустим ни на одном таске** — компиляция обязана быть чистой после
  каждого.
- **ГЕЙТ-META:** каждому новому не-`.meta` файлу под `client/Assets/**`
  соответствует `<path>.meta` (генерит Unity).
- **ГЕЙТ-КОДОГЕН (после таска, тронувшего проводную структуру):**
  `strings -a client/Library/ScriptAssemblies/Ring.Networking.dll | grep -E
  "Comparer___|GWrite___Unity|GRead___Unity"` → ПУСТО; то же для
  `Ring.Presentation.Net.dll`, `MetaVoiceChat.dll`.
- **RED-дисциплина:** тест не компилируется из-за отсутствующих полей →
  сначала заглушки до КОМПИЛЯЦИИ, затем наблюдаемый FAIL ассерта.
  **Ошибка компиляции ≠ RED** (урок 332). Заглушка — КОНСТАНТА.
  **RED даёт EXIT=2.** ⚠ **Тест, который зелен на сегодняшнем коде, свидетелем
  не является** (находка D-C: именно так v1 «покрыла» рефактор).
- **Мутация на каждую ветку:** мутации спеки §4 (M1–M13) распределены по
  таскам таблицей в конце файла. Исполнитель предсказывает жертву **поимённо
  и числом/механизмом ДО прогона**, пишет в `$SDD/task-<N>-mutations-predicted.md`.
  Форма — **ослабление**. ⚠ Охват мутации **гварда** берётся из тестов всего,
  что гвард пропускает через себя.
- **Тест-швы:** канон — `var m = w.Mobs[i]; m.X = …; w.SetMobForTest(i, m);`.
  Существующие переиспользуются (`TestWorlds.IdleTicks/ClearFirstWave`,
  `TestEvents.TryFirstOf`, `w.MatchRef`). **Новые параметры существующих
  хелперов — только хвостовыми с умолчанием.**
- **bd:** сабтаски создаются ДО Т1; клейм на старте таска; `bd note app-ggvz`
  КОРОТКО после каждого; эвиденс — файлом в `$SDD`; после каждого `bd close`
  — явный `bd export -o .beads/issues.jsonl` (урок 236); jsonl-дрифт —
  chore-коммитом из `$APP_REPO` в main.
- **Коммиты:** `feat|test|fix|refactor|chore|docs(app-ggvz): …` (рус.) +
  трейлер `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед
  каждым — секрет-чек и сверка `git diff --cached --stat` со скоупом.
- batchmode не гонять при открытом Editor'е владельца; перед прогоном —
  проверка stale `client/Temp/UnityLockfile`; запуск — `timeout -k 30 900`
  foreground; **сборка — ФОНОМ** (199). ⚠ `timeout` НЕ снимает зависший Unity
  (364). **НИ `pgrep -f`, НИ `pkill -f`** (134) — только `pgrep -x Unity`.

## Runbook

- **R-TEST:** `cd "$WT" && timeout -k 30 900 "$UNITY" -runTests -batchmode
  -projectPath client -testPlatform EditMode -testResults "$SCRATCH/t.xml"
  -logFile "$SCRATCH/t.log"; echo EXIT=$?` → разбор xml **питоном по
  `test-case`**; `total` — **ГЛАЗАМИ** (169); + ГЕЙТ-ОТКАТ. Старт 1546.
- **R-FILTER `<Класс>`:** R-TEST + `-testFilter "Ring.Simulation.Tests.<Класс>"`.
- **R-COMPILE:** `cd "$WT" && timeout -k 30 900 "$UNITY" -batchmode -quit
  -projectPath client -logFile "$SCRATCH/c.log"; echo EXIT=$?` → EXIT=0 +
  ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-APPLY:** `... -executeMethod Ring.Editor.StageOneSceneBootstrap.Apply
  -logFile "$SCRATCH/apply.log"` → EXIT=0 + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-IDEM:** повторный R-APPLY → `git status --porcelain -- client/` и
  `git diff -- client/` пусты (мерить ПОСЛЕ коммита артефактов).
- **R-GOLDEN (перепин, ТОЛЬКО Т8):** R-FILTER `DeterminismTests` → три
  `But was: <N>` из xml → hex + десятичный дубль + обоснование → повтор → PASS.
- **R-BUILD-`<X>`:** `RING_BUILD_ROOT="$SCRATCH/builds" ... -executeMethod
  Ring.Editor.BuildCommands.Build<X>` (X ∈ `LinuxServer|LinuxClient|
  WindowsClient|LinuxServerDev|LinuxClientDev|WindowsClientDev`). **ФОНОМ**;
  вердикт — по строке **«Exiting batchmode successfully»**.
- **R-IMAGE:** `client/docker/build.sh --no-push`; доставка `docker save …
  | gzip -1 | ssh -p 2201 brolin@<хост> 'gunzip | docker load'`.
- **R-STAND (230):** `./Ring -batchmode -nographics -ring-connect <хост>:7777
  -ring-player-id pN -ring-join-token tN -ring-latency off -logFile <лог>`;
  троих в ОДНОМ 120-секундном окне (240). ⚠ Стенд не заменяет живой забег (417).
- **R-COMMIT:** секрет-чек → ГЕЙТ-META → `git diff --cached --stat` против
  скоупа → `git add … && git commit`.

---

## Фаза 1 — фундамент (Т1–Т2)

### Task Т1: приписка моба к кольцу и один дом для числа колец

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs` (статический
  класс `Zones` рядом с `enum Zone` `:210`; **амендмент дока `enum Zone`
  `:204-210`**, который сегодня утверждает «nothing in PlayerState/MobState
  stores current zone»; зеркальный док `:522-523`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs` (`MobState`
  `:150-158`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`SpawnMob` `:1924`, `SpawnMobForTest` `:1963`, `DevSpawnMob` `:2016`,
  `HashMob` `:2537`)
- Modify: `client/Assets/Scripts/Simulation/AI/WaveSystem.cs` (`:36` удалить
  `ZoneCount`; `:320` вызов `SpawnMob`)
- Modify: `client/Assets/Scripts/Simulation/Loot/LootDrops.cs` (`:79` удалить
  второй `ZoneCount`, док `:58-59`)
- Modify: `client/Assets/Scripts/Simulation/Objectives/MatchFlowSystem.cs`
  (`:123`, `:214`)
- Test: `client/Assets/Tests/EditMode/WaveCadenceTests.cs` (**создать**),
  `client/Assets/Tests/EditMode/WorldLifecycleTests.cs` (квитанция `:141-178`)

**Interfaces:**

```csharp
// SimConfig.cs, под `public enum Zone : byte { Outer = 0, Middle = 1, Core = 2 }`.
// Статический класс, а не const на структуре конфига: рефлексивный свип
// SimConfigHashTests ходит GetFields() и видит const внутри секции
// (прецеденты той же формы в этом же файле — ItemCatalogLookup :458,
// LootTransferTimes :596).
public static class Zones
{
    public const int Count = 3;
}

// SimStates.cs, MobState — новое поле последним:
/// The ring this mob was PUT INTO by whoever spawned it -- wave bookkeeping
/// only. NOT a retinue mark: who counts as the Director's retinue is decided
/// positionally by MatchFlowSystem.LiveRetinueCount (Р215), and a core-wave
/// elite is indistinguishable from a retinue elite by this field.
/// NOT a "current zone" either -- the mob walks away from where it was born,
/// which is exactly why the value cannot be derived and has to be stored.
/// On a CLIENT this stays default: there MobState is assembled from MobRecord,
/// which does not carry it -- Presentation must not read this field.
/// A dev-key spawn is filed under Zone.Outer wherever it lands.
public Zone SpawnZone;

// Производственный спавн: зона ОБЯЗАТЕЛЬНА, умолчания нет (сторож в Step 5).
internal int SpawnMob(MobType type, float2 pos, Zone zone)

// Тест-шов: хвостовой параметр с умолчанием (иначе 124 вызова в 18 файлах
// тестов правятся механически).
internal int SpawnMobForTest(MobType type, float2 pos, Zone zone = Zone.Outer)
    => SpawnMob(type, pos, zone);
```

- ⚠ `DevSpawnMob` (`:2016`) сегодня `=> SpawnMobForTest(type, pos)` и потому
  **молча потребляет умолчание тест-шва в живом дев-билде** (находка D-C).
  Переписывается на явное `=> SpawnMobForTest(type, pos, Zone.Outer)`.
- Производственных вызовов `w.SpawnMob(` — **три**: `WaveSystem.cs:320`,
  `MatchFlowSystem.cs:123`, `:214`.
- `HashMob` получает `SpawnZone` **сразу после `Type`**.

- [ ] **Step 1 (RED):** создать `WaveCadenceTests.cs`:

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
            // Моб СРЕДНЕГО кольца ставится в точку, геометрически лежащую во
            // внешнем: приписка обязана следовать за спавнером, не за координатой.
            int id = w.SpawnMobForTest(MobType.Chaser,
                new float2(cfg.Arena.Radius - 1f, 0f), Zone.Middle);
            Assert.GreaterOrEqual(id, 0, "моб не заспавнился");
            Assert.AreEqual(Zone.Middle, w.Mobs[w.MobCount - 1].SpawnZone);
        }

        [Test]
        public void ProductionSpawn_HasNoDefaultForZone()
        {
            // Сторож Р324: удобство тест-шва не имеет права протечь в продакшен.
            var m = typeof(SimulationWorld).GetMethod("SpawnMob",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(m, "SpawnMob не найден");
            var ps = m.GetParameters();
            Assert.AreEqual(3, ps.Length, "у производственного SpawnMob должно быть три параметра");
            Assert.IsFalse(ps[2].HasDefaultValue,
                "зона в производственном SpawnMob обязана быть обязательной");
        }
    }
}
```

- [ ] **Step 2:** заглушки (`SpawnZone` без записи, третий параметр
      игнорируется) до **компиляции**; R-FILTER `WaveCadenceTests` → EXIT=2,
      наблюдаемый `Expected: Middle, But was: Outer`.
- [ ] **Step 3 (GREEN, приписка):** `SpawnMob` пишет `SpawnZone = zone`; три
      производственных call-site'а передают зону; `DevSpawnMob` — явный
      `Zone.Outer`; `HashMob` получает поле; док поля из Interfaces.
- [ ] **Step 4 (амендмент чужого дока):** `SimConfig.cs:204-210` — док
      `enum Zone` дополняется различением «current zone (не хранится, Р206)»
      против «spawn zone (хранится, потому что не выводится)». Без этого в
      репозитории останутся два противоречащих утверждения.
- [ ] **Step 5 (дедуп тройки):** `Zones.Count` заменяет `WaveSystem.cs:36` и
      `LootDrops.cs:79` (+ доки `LootDrops.cs:58-59`, `SimConfig.cs:522-523`).
- [ ] **Step 6:** R-FILTER `WaveCadenceTests` → PASS (2/2).
- [ ] **Step 7 (мутация M5, предсказание ДО прогона в
      `$SDD/task-1-mutations-predicted.md`):** в `SpawnMob` записать
      `SpawnZone = Zone.Outer` вместо параметра. Предсказание:
      `SpawnZone_IsSetByTheSpawner_NotByPosition` красный со строкой
      `Expected: Middle, But was: Outer`. Откат — **`cp` с копии и md5**, НЕ
      `git checkout` (350).
- [ ] **Step 8:** квитанция `WorldLifecycleTests.cs:141-178` пересчитывается
      **целиком свежим `typeof(X).GetFields()`** (правило файла: «re-derived,
      never incremented»): `MobState` 9 → **10**, итог 136 → **137**.
- [ ] **Step 9:** R-TEST полный → красные по таблице (три golden).
- [ ] **Step 10:** R-COMMIT `feat(app-ggvz): Т1 — приписка моба к кольцу и
      один дом для числа колец`.

### Task Т2: числа каденции в конфиге и пять правил валидации

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs` (`WaveSimConfig`)
- Modify: `client/Assets/Scripts/Data/WaveConfig.cs`
- Modify: `client/Assets/Scripts/Data/SimConfigBuilder.cs` (маппинг `:141-160`,
  валидация `:585-631`, хелперы `:1719-1810`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfigHash.cs`
- Modify: `client/Assets/Tests/EditMode/TestConfigs.cs` (`:84` — единственный
  литерал `new WaveSimConfig`)
- Modify: `client/Assets/Tests/EditMode/ZoneConfigTests.cs`,
  `client/Assets/Tests/EditMode/ConfigTests.cs` (`AssertWaveEqual` `:1474`),
  `client/Assets/Tests/EditMode/SimConfigHashTests.cs` (`:50-51`)

**Interfaces:**

```csharp
// WaveSimConfig (Core/SimConfig.cs) — ДОБАВЛЯЮТСЯ; ZoneWeights и WavePause
// пока остаются (их снимает Т4, когда исчезнет последний потребитель).
public float[] WavePauseByZone;      // {20, 30, 30} — секунды, по кольцу
public int[] MaxAliveByZone;         // {150, 110, 10} — живых одновременно
public int MaxSpawnsPerZonePerTick;  // 2 — сглаживание притока (Р317)
public float DifficultyStepSeconds;  // 20 — шаг кривой сложности (Р315)

// WaveConfig (Data/WaveConfig.cs) — атрибуты обязательны по конвенции файла:
// у КАЖДОГО скаляра [Range], у массива вместо него комментарий-объяснение.
// Ranges are not expressible per element via [Range] (Unity clamps the whole
// field) -- SimConfigBuilder.Validate is the real gate, the same convention
// ZoneWeights and ArenaConfig.ZoneRadius already follow.
public float[] WavePauseByZone = { 20f, 30f, 30f };
public int[] MaxAliveByZone = { 150, 110, 10 };
[Range(1, 20)] public int MaxSpawnsPerZonePerTick = 2;
[Range(1f, 120f)] public float DifficultyStepSeconds = 20f; // sync-marker key — keep LAST

// Новый хелпер валидации рядом с ReqPositive/ReqInRange (:1719-1810):
// идиома «массив ровно N элементов» скопирована в репо ПЯТЬ раз
// (ZoneWeights :608, CellsPerMob :994, TransferSeconds :1007,
// DropChance :1037, ZoneRadius :1190) и дома не имеет.
static void ReqZoneArrayLength<T>(List<string> errors, string name, T[] a);
// Второй: «не меньше двух тиков» — прецеденты текста MatchEndPolicy.cs:103-110
// и SimConfigBuilder.cs:469-473.
static void ReqAtLeastTwoTicks(List<string> errors, string name, float seconds);
```

- ⚠ **Порог — ДВА тика, а не один** (находка A-Critical): `TicksFromSeconds`
  округляет, поэтому `TicksFromSeconds(0.02f) = round(0.6) = 1` — гвард
  «< 1 тика» не сработал бы, а пауза в один тик действительно даёт старт
  волны каждый тик. Правило: `TicksFromSeconds(x) < 2` → отказ с текстом
  «at least two ticks».
- ⚠ **Правила 2 и 4 живут ВНУТРИ `else`-ветки проверки формы** (находка
  A-Critical): иначе сумма `MaxAliveByZone[0]+[1]+[2]` считается до проверки
  длины и даёт `IndexOutOfRangeException` вместо `ArgumentException`.
- ⚠ **Правил ПЯТЬ, и пятое — про `DifficultyStepSeconds`** (находка D-C):
  v1 писала «пять», давала четыре и не давала свидетеля пятому.
- ⚠ **Ветки дефолтов при отсутствующем SO для `Wave` НЕ существует** (находка
  B): `SimConfigBuilder.Build` принимает `WaveConfig` обязательным; `:225-245`
  — ветка `Loot`. Никакой null-обработки добавлять не нужно.
- **Числа фикстур** (Р325) задаются **только в `Default()`** (`TestConfigs.cs:84`
  — единственный литерал `new WaveSimConfig`; остальные шесть вариантов от неё
  производны): `WavePauseByZone = { 2f, 3f, 3f }`, `MaxAliveByZone = { 24, 16, 8 }`,
  `MaxSpawnsPerZonePerTick = 2`, `DifficultyStepSeconds = 2f`. `BaseCount`
  фикстуры не трогается (4).
- **Маркер-ключ синка переезжает — ЧЕТЫРЕ вещи** (находка B): комментарий
  `// sync-marker key — keep LAST` на новом последнем поле; **надгробная
  пометка на уходящем** (`EliteShareOuterCap`) вида
  `// Was the sync-marker key until app-ggvz.` (прецеденты `ArenaConfig.cs:259`,
  `HeroConfig.cs:80`, `GameFeelConfig.cs:96`); аргумент
  `EditorBootstrapUtils.EnsureAssetHasKey` (правится в Т6 — окно
  рассогласования названо явно); хвостовая пометка `// … (was X, app-ggvz)`.
- `SimConfigHash`: два массива через существующие `HashFloatArray` и
  **`HashInt32Array` (`:261`, существует)**, два скаляра.

- [ ] **Step 1 (RED):** пять тестов в `ZoneConfigTests.cs`. ⚠ **Нарушение
      ставится на ВТОРОЙ элемент, нулевой остаётся легальным контролем** —
      это записанное правило файла (`ZoneConfigTests.cs:205-207`, ledger 227):
      «a loop mutated to check only the first entry cannot pass».

```csharp
// Форма скопирована с ZoneConfigTests.cs:213-220: валидация гоняется через
// ScriptableObject'ы (MakeDefaults -> BuildShipped), а не через SimConfig.
// Кортеж — семь SO: (HeroConfig, WeaponConfig, MobConfig, MobConfig,
// WaveConfig, ArenaConfig, VisibilityConfig); MatchFlowConfig в нём НЕТ.
[Test]
public void Validate_WavePauseBelowTwoTicks_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.WavePauseByZone = new[] { 20f, 0.02f, 30f };   // нарушение на ВТОРОМ
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Wave.WavePauseByZone[1]"));
    Assert.That(ex.Message, Does.Contain("at least two ticks"));
}

[Test]
public void Validate_DifficultyStepBelowTwoTicks_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.DifficultyStepSeconds = 0.02f;
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Wave.DifficultyStepSeconds"));
    Assert.That(ex.Message, Does.Contain("at least two ticks"));
}

[Test]
public void Validate_ZeroZoneCeiling_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.MaxAliveByZone = new[] { 150, 0, 10 };         // нарушение на ВТОРОМ
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Wave.MaxAliveByZone[1]"));
}

[Test]
public void Validate_WrongZoneArrayLength_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.MaxAliveByZone = new[] { 150, 110 };
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("exactly 3 elements"));
}

[Test]
public void Validate_CeilingsPlusDirectorReserveAboveMaxMobs_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.MaxAliveByZone = new[] { a.MaxMobs, 1, 1 };    // строго больше потолка
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("must not exceed Arena.MaxMobs"));
}

[Test]
public void Validate_CeilingsExactlyAtMaxMobs_IsLegal()
{
    // Граничный случай легален — свидетель для мутации `>` -> `>=`.
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    int reserve = 3;                                  // Flow.DirectorReserveSlots
    wv.MaxAliveByZone = new[] { a.MaxMobs - reserve - 2, 1, 1 };
    Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
}

[Test]
public void Validate_ZeroSpawnsPerZonePerTick_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    wv.MaxSpawnsPerZonePerTick = 0;
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Wave.MaxSpawnsPerZonePerTick"));
}
```

- [ ] **Step 2:** заглушки полей → R-FILTER `ZoneConfigTests` → EXIT=2, семь
      наблюдаемых «исключение не брошено» (и один `DoesNotThrow`, который
      обязан быть зелёным уже здесь).
- [ ] **Step 3 (GREEN):** поля в `WaveSimConfig` и `WaveConfig` с атрибутами;
      маппинг; два новых хелпера; пять правил (2 и 4 — внутри `else`).
- [ ] **Step 4:** числа фикстур в `TestConfigs.Default()`; `AssertWaveEqual`
      расширяется в связке; `SimConfigHashTests` (`:50-51`) получает два новых
      массива через `AssertInt32ArrayFieldAffectsHash`/
      `AssertFloatArrayFieldAffectsHash` и два скаляра.
- [ ] **Step 5:** R-FILTER `ZoneConfigTests` + `ConfigTests` +
      `SimConfigHashTests` → PASS.
- [ ] **Step 6 (мутация M13 — ПЯТЬ мутаций, по одной на правило; предсказания
      ДО прогона):** (1) порог `< 2` → `< 1` для паузы → жертва
      `Validate_WavePauseBelowTwoTicks_Throws` (`TicksFromSeconds(0.02) = 1`,
      отказ пропадёт); (2) то же для `DifficultyStepSeconds`; (3) нижняя
      граница потолка `1` → `0` → жертва `Validate_ZeroZoneCeiling_Throws`;
      (4) `>` → `>=` в кросс-полевом правиле → жертва
      `Validate_CeilingsExactlyAtMaxMobs_IsLegal`; (5) снять
      `ReqZoneArrayLength` → жертва `Validate_WrongZoneArrayLength_Throws`
      **с падением на `IndexOutOfRangeException`, а не на ассерте** — это и
      есть доказательство, что порядок правил несущий.
- [ ] **Step 7:** R-TEST полный → красные по таблице.
- [ ] **Step 8:** R-COMMIT `feat(app-ggvz): Т2 — числа каденции в конфиге и
      пять правил валидации`.

**Гейт фазы 1:** R-TEST по таблице; ГЕЙТ-ЛОГ без `error CS`; свип кириллицы
(с исключением ассертов) пуст; `bd note` по каждому таску; push ветки.

---

## Фаза 2 — состояние и каденция (Т3–Т5)

### Task Т3: три экземпляра `WaveState`, тики, агрегат

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs` (`WaveState`
  `:332-358` **и его док `:317-331`**, который описывает девятиполевую
  матрицу и после таска противоречил бы коду)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`_wave` `:143`, конструктор `:293`, `WaveRef` `:1026`, **док `MatchRef`
  `:1033`** («same ref-return pattern as WaveRef»), `CaptureSnapshot` `:2166`,
  `SaveState` `:2238`, `RestoreState` `:2328`, `HashWave` `:2603`,
  `SetWaveForTest` `:2363`, **новый шов `ClearMobsForTest`**, док `:518`
  («wave-state including WaveIndex is left untouched»))
- Modify: `client/Assets/Scripts/Simulation/Core/WorldSave.cs` (`:55`)
- Modify: `client/Assets/Scripts/Simulation/AI/WaveSystem.cs` (только
  `PendingRef` `:251-268` и обращения к полям — каденция в Т4)
- Modify: `client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs`
  (**док `ReadWave` `:2163-2168`**, перечисляющий девять `Pending*` и
  `PhaseTimer`; код `:2179-2184` компилируется без правок — он пишет только
  `Phase`/`WaveIndex`/`AliveCount`)
- Test: `client/Assets/Tests/EditMode/WaveCadenceTests.cs`,
  `client/Assets/Tests/EditMode/InterpolationBufferTests.cs` (филлер
  `:925-940` — девять `Pending*` и `PhaseTimer`),
  `client/Assets/Tests/EditMode/SnapshotAssemblerTests.cs` (`:485-487` —
  `w.WaveRef` становится методом),
  `client/Assets/Tests/EditMode/WorldLifecycleTests.cs` (свип и квитанция)

**Interfaces:**

```csharp
// SimStates.cs — семь полей. WaveIndex ОСТАЁТСЯ (Р334), но перестаёт быть
// счётчиком: Т4 присваивает туда шаг сложности из часов захода.
public struct WaveState
{
    public WavePhase Phase;
    public int WaveIndex;                 // difficulty step of the wave running here
    public int PhaseTicks, AliveCount;    // WHOLE TICKS (Р316/R-178/урок 348)
    public int PendingChaser, PendingGunner, PendingElite;

    public int PendingTotal => PendingChaser + PendingGunner + PendingElite;
}

// SimulationWorld.cs
WaveState[] _waves;                                   // Zones.Count, один new в конструкторе
internal ref WaveState WaveRef(Zone zone) => ref _waves[(int)zone];
internal void SetWaveForTest(Zone zone, in WaveState w) => _waves[(int)zone] = w;

/// Test-only: takes every mob off the arena. NEW seam -- no existing one
/// expresses it (_mobCount is private and its only decrement lives in
/// DamageMob), and the cadence tests need an emptied ring to observe a clear.
internal void ClearMobsForTest();

// WaveSystem.cs — долг снова три поля, зона ушла в индекс экземпляра.
internal static ref int PendingRef(ref WaveState w, MobType type);
```

- **`CaptureSnapshot` кладёт в `RenderSnapshot.Wave` АГРЕГАТ:** `Phase =
  Active`, если активно хоть одно кольцо; `WaveIndex` = **максимум** по трём
  (монотонен по построению, Р334); `PhaseTicks` = минимум среди активных;
  `AliveCount` и три `Pending` — суммы. Массива в снимке не появляется →
  ни алиасинга, ни нового ключа в `ArrayCountField`.
- **`WorldSave.Waves`** — массив; аллокация в инициализаторе `SaveState` по
  образцу `Mobs = new MobState[_mobs.Length]` (`:2222`), копирование
  **`System.Array.Copy`** в обе стороны (идиома `SimulationWorld`:
  `:2141`, `:2249`, `:2312`) — **никогда присваиванием ссылки**.
- **`HashWave` ×3** подряд `Outer → Middle → Core`, на том же месте
  последовательности (`:2470`).

- [ ] **Step 1 (RED):** два теста в `WaveCadenceTests.cs`:

```csharp
[Test]
public void SaveState_DoesNotAliasTheLiveWaveArray()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, 100);
    WorldSave save = w.SaveState();
    int before = save.Waves[(int)Zone.Outer].PendingTotal;

    WaveState outer = w.WaveRef(Zone.Outer);
    outer.PendingChaser += 99;
    w.SetWaveForTest(Zone.Outer, outer);

    Assert.AreEqual(before, save.Waves[(int)Zone.Outer].PendingTotal,
        "сохранённое состояние алиасит живой массив волн");
}

[Test]
public void Snapshot_CarriesTheWorldAggregate_NotTheFirstRing()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    var frame = new RenderSnapshot(in cfg);
    TestWorlds.IdleTicks(w, 100);
    w.CaptureSnapshot(frame);

    int sum = w.WaveRef(Zone.Outer).AliveCount + w.WaveRef(Zone.Middle).AliveCount
        + w.WaveRef(Zone.Core).AliveCount;
    int maxIndex = math.max(w.WaveRef(Zone.Outer).WaveIndex,
        math.max(w.WaveRef(Zone.Middle).WaveIndex, w.WaveRef(Zone.Core).WaveIndex));
    Assert.AreEqual(sum, frame.Wave.AliveCount, "агрегат не суммирует живых");
    Assert.AreEqual(maxIndex, frame.Wave.WaveIndex, "агрегат не берёт максимум шага");
}
```

- [ ] **Step 2:** заглушки (`WaveRef(Zone)`, новая форма `WaveState`,
      `ClearMobsForTest` пустой) до **компиляции**; R-FILTER
      `WaveCadenceTests` → EXIT=2, два наблюдаемых FAIL.
- [ ] **Step 3 (GREEN, хранение):** массив из трёх, `WaveRef(Zone)`,
      `SetWaveForTest(Zone, …)`, `ClearMobsForTest`.
- [ ] **Step 4 (GREEN, сохранение и хеш):** `WorldSave.Waves` с аллокацией и
      `Array.Copy` в обе стороны; `HashWave` ×3; агрегат в `CaptureSnapshot`.
- [ ] **Step 5 (доки, которые иначе станут ложью):** `WaveState` `:317-331`,
      `MatchRef` `:1033`, `SimulationWorld.cs:518`, `ReadWave` `:2163-2168`.
- [ ] **Step 6 (механическая правка ломающихся тестов):** филлер
      `InterpolationBufferTests.cs:925-940` (девять `Pending*` + `PhaseTimer`
      → три + `PhaseTicks`, плюс `WaveIndex` остаётся);
      `SnapshotAssemblerTests.cs:485-487` (`w.WaveRef` → `w.WaveRef(Zone.Outer)`).
- [ ] **Step 7:** `WorldLifecycleTests` — свип **по каждой зоне отдельно**
      (`for (int z = 0; z < Zones.Count; z++)` с `SetWaveForTest((Zone)z, …)`),
      иначе `HashWave`, усечённый до `waves[0]`, прошёл бы тест; квитанция
      целиком: `WaveState` 13 → **7 × 3 = 21**, `MobState` 10 (из Т1),
      итог пересчитать свежим чтением.
- [ ] **Step 8 (мутации M7 и M12; предсказания ДО прогона):** M7 — в
      `SaveState` присвоить `Waves = _waves` ссылкой → жертва
      `SaveState_DoesNotAliasTheLiveWaveArray` (`Expected: N, But was: N+99`);
      M12 — хешировать только `waves[Outer]` → жертва: свип
      `WorldLifecycleTests` по зонам Middle/Core.
- [ ] **Step 9:** R-COMPILE → EXIT=0, ГЕЙТ-ЛОГ **без единого `error CS`**;
      затем R-TEST полный → красные по таблице (три golden).
- [ ] **Step 10:** R-COMMIT `refactor(app-ggvz): Т3 — три экземпляра WaveState,
      таймер в тиках, агрегат в снимке`.

### Task Т4: каденция — таймер, часы сложности, зачистка

**Files:**
- Modify: `client/Assets/Scripts/Simulation/AI/WaveSystem.cs` (`Update`
  `:41-92`, `StartWave` `:111-183`; **удалить** `ZonelessWeights` `:34`,
  `SplitByZones` `:216-240`, переезд бюджета ядра `:146-150`, устаревший док
  `AliveCount` `:85-90`, док класса `:6-24`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs` (**удалить**
  `ZoneWeights`, `WavePause`; доки `:181`, `:187` про `WaveIndex`)
- Modify: `client/Assets/Scripts/Data/WaveConfig.cs` (**удалить** те же два
  поля; доки `:17-18`, `:38`; **док `:5-6`** «Field defaults mirror
  TestConfigs.Default().Wave» становится ложным после Т2 — переписать)
- Modify: `client/Assets/Scripts/Data/SimConfigBuilder.cs` (маппинг `:142`,
  `:154`; валидация `:586`, `:603-631`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfigHash.cs` (`:133`, `:140`)
- Modify: `client/Assets/Scripts/Networking/Protocol/SnapshotEvents.cs`
  (**док `:192`** — `WaveIndex` события теперь несёт шаг сложности)
- Test: `WaveCadenceTests.cs`, `WaveTests.cs`, `WaveScalingTests.cs`,
  `WaveZoneTests.cs`, `EliteAndDirectorTests.cs` (`:721`), `ConfigTests.cs`
  (`AssertWaveEqual` `:1474`, `:1496-1498`), `ZoneConfigTests.cs` (`:213-220`,
  `:661-672` — удалить), `SimConfigHashTests.cs` (`:50-51` — строка
  `"ZoneWeights"`), `HotTweakTests.cs`, `AllocationTests.cs` (`:63`)

**Interfaces:**

```csharp
// Единственный дом кривой сложности. Имя по конвенции чистых отображений
// репо (MobConfigFor, EliteShareFor, MaxHpFor, VisualScaleFor).
internal static int DifficultyStepFor(int tick, in WaveSimConfig cfg);
// = 1 + max(0, tick - TicksFromSeconds(cfg.FirstWaveDelay))
//         / TicksFromSeconds(cfg.DifficultyStepSeconds)
// Делитель гарантирован валидацией Т2 (>= 2 тиков), поэтому гвард здесь —
// не max(1, …) в проде, а `if (stepTicks <= 0) return 1;` по прецеденту
// MatchFlowSystem.cs:160-161 с той же записанной причиной.
```

- **Тик `Update`:** ранний выход `Targeting.NearestAlivePlayer` (`:50`) **не
  трогается**; один проход по мобам считает живых по `SpawnZone` в
  `System.Span<int> alive = stackalloc int[Zones.Count]` (в файле нет
  `using System;` — квалификатор обязателен, как на `:120-121`); далее цикл
  `Outer → Middle → Core`: неактивное кольцо (беззонная арена → активен
  только `Outer`; `Core` при `w.Match.Phase != MatchPhase.Farm`)
  **замораживается идемпотентно** (`Phase = Waiting`, `PhaseTicks = 0`, три
  долга в ноль) и пропускается; иначе `PhaseTicks--`; при `<= 0` —
  `StartWave`; при `Active` — спавн долга (порядок архетипов R-50) и проверка
  зачистки `PendingTotal == 0 && alive[z] == 0` → `WorldStats.WavesCleared++`,
  `Emit(WaveCleared, …, wave.WaveIndex)`, `Phase = Waiting`,
  `PhaseTicks = TicksFromSeconds(cfg.WavePauseByZone[z])`; в конце
  `wave.AliveCount = alive[z]`.
- **`StartWave(зона)`:** `wave.WaveIndex = DifficultyStepFor(w.Tick, in cfg)`
  (**присваивание, не инкремент** — Р334);
  `count = CountForTest(in cfg, wave.WaveIndex - 1, w.PlayerCount)`; доли по
  `EliteShareFor(зона, wave.WaveIndex, in cfg)` и существующей `GunnerShare`;
  **присваивание** долга (Р305); `Emit(WaveStarted, …, wave.WaveIndex)`;
  `Phase = Active`; `PhaseTicks = TicksFromSeconds(cfg.WavePauseByZone[зона])`.

- [ ] **Step 1 (RED):** восемь тестов в `WaveCadenceTests.cs`:

```csharp
static int OuterPause(in SimConfig cfg) =>
    SimulationWorld.TicksFromSeconds(cfg.Wave.WavePauseByZone[(int)Zone.Outer]);
static int FirstDelay(in SimConfig cfg) =>
    SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay);

[Test]
public void SecondWaveArrives_WithoutASingleKill()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + OuterPause(in cfg) / 2);
    Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Outer).Phase);
    int aliveAfterFirst = w.WaveRef(Zone.Outer).AliveCount;
    Assert.Greater(aliveAfterFirst, 0, "первая волна не родила ни одного моба");

    TestWorlds.IdleTicks(w, OuterPause(in cfg) + 2);
    Assert.Greater(w.WaveRef(Zone.Outer).AliveCount, aliveAfterFirst,
        "вторая волна не пришла: сегодня очередь двигает только полный вайп арены");
}

[Test]
public void Rings_TickIndependently()
{
    SimConfig cfg = TestConfigs.Default();   // паузы фикстуры {2, 3, 3}
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);
    Assert.AreNotEqual(w.WaveRef(Zone.Outer).PhaseTicks,
        w.WaveRef(Zone.Middle).PhaseTicks, "кольца тикают одним таймером");
}

[Test]
public void ClearingARing_RestartsItsOwnTimer_AndLeavesNeighboursAlone()
{
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);

    // Соседям оставляем долг, чтобы вычистилось РОВНО внешнее кольцо.
    foreach (Zone z in new[] { Zone.Middle, Zone.Core })
    {
        WaveState s = w.WaveRef(z);
        s.PendingChaser = 99;
        w.SetWaveForTest(z, s);
    }
    WaveState outer = w.WaveRef(Zone.Outer);
    outer.PendingChaser = outer.PendingGunner = outer.PendingElite = 0;
    w.SetWaveForTest(Zone.Outer, outer);
    w.ClearMobsForTest();

    int clearedBefore = w.WorldStatsRef.WavesCleared;
    int middleBefore = w.WaveRef(Zone.Middle).PhaseTicks;
    int coreBefore = w.WaveRef(Zone.Core).PhaseTicks;
    w.Tick(default);

    Assert.AreEqual(clearedBefore + 1, w.WorldStatsRef.WavesCleared);
    Assert.AreEqual(WavePhase.Waiting, w.WaveRef(Zone.Outer).Phase);
    Assert.AreEqual(OuterPause(in cfg), w.WaveRef(Zone.Outer).PhaseTicks,
        "окно тишины должно быть ПОЛНЫМ, а не остатком");
    Assert.AreEqual(middleBefore - 1, w.WaveRef(Zone.Middle).PhaseTicks,
        "зачистка чужого кольца сдвинула таймер среднего");
    Assert.AreEqual(coreBefore - 1, w.WaveRef(Zone.Core).PhaseTicks);
}

[Test]
public void ClearIsNotCounted_WhileAnyMobOfThatRingLives()
{
    // Негативная половина: жертва мутации M2.
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);
    WaveState outer = w.WaveRef(Zone.Outer);
    outer.PendingChaser = outer.PendingGunner = outer.PendingElite = 0;
    w.SetWaveForTest(Zone.Outer, outer);          // долг закрыт, мобы ЖИВЫ
    int before = w.WorldStatsRef.WavesCleared;
    w.Tick(default);
    Assert.AreEqual(before, w.WorldStatsRef.WavesCleared,
        "кольцо засчитано вычищенным при живых мобах");
}

[Test]
public void UnspawnedDebt_IsOverwrittenByTheNextWave_NotAccumulated()
{
    // Тест 6 спеки: жертва мутации M11.
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxAliveByZone = new[] { 1, 16, 8 };   // внешнее кольцо почти сразу у потолка
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);
    int debtAfterFirst = w.WaveRef(Zone.Outer).PendingTotal;
    Assert.Greater(debtAfterFirst, 0, "долг не образовался — фикстура не давит потолком");

    TestWorlds.IdleTicks(w, OuterPause(in cfg) + 2);
    Assert.LessOrEqual(w.WaveRef(Zone.Outer).PendingTotal,
        cfg.Wave.MaxMobsPerWave,
        "долг копится вместо перезаписи: он обязан быть не больше одной волны");
}

[Test]
public void WaveIndex_FollowsTheClock_NotTheNumberOfWavesStarted()
{
    // Тесты 2а и 15 спеки: жертва мутации M9. Зачистка ОТОДВИГАЕТ старт,
    // поэтому счётчик волн и часы расходятся — и поле обязано идти за часами.
    SimConfig cfg = TestConfigs.Default();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);

    WaveState outer = w.WaveRef(Zone.Outer);
    outer.PendingChaser = outer.PendingGunner = outer.PendingElite = 0;
    w.SetWaveForTest(Zone.Outer, outer);
    w.ClearMobsForTest();
    w.Tick(default);                                  // зачистка: таймер с полного

    // Дотикать до следующего старта внешнего кольца и поймать его тик.
    int tick = 0;
    for (int i = 0; i < OuterPause(in cfg) + 4; i++)
    {
        w.Tick(default);
        tick++;
        if (w.WaveRef(Zone.Outer).Phase == WavePhase.Active) break;
    }
    Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Outer).Phase, "волна не пришла");
    Assert.AreEqual(WaveSystem.DifficultyStepFor(w.Tick, in cfg.Wave),
        w.WaveRef(Zone.Outer).WaveIndex,
        "номер волны отстал от часов — значит он всё ещё счётчик");
}

[Test]
public void ZonelessArena_RunsOnlyTheOuterRing()
{
    // Беззонная фикстура строится ОТ Default(): OpenField() наследует от
    // Quiet(), а та отодвигает первую волну на 1e6 секунд (TestConfigs.cs:372).
    SimConfig cfg = TestConfigs.Default();
    cfg.Arena.ZoneRadius = System.Array.Empty<float>();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 4);
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
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 4);
    w.MatchRef.Phase = MatchPhase.DirectorActive;   // существующий ref-шов
    w.Tick(default);
    Assert.AreEqual(WavePhase.Waiting, w.WaveRef(Zone.Core).Phase);
    Assert.AreEqual(0, w.WaveRef(Zone.Core).PhaseTicks);
    Assert.AreEqual(0, w.WaveRef(Zone.Core).PendingTotal);
}
```

- [ ] **Step 2:** заглушка `DifficultyStepFor` → **константа 1** (не «почти
      реализация»); R-FILTER `WaveCadenceTests` → EXIT=2, восемь наблюдаемых
      FAIL.
- [ ] **Step 3 (GREEN, каденция):** `DifficultyStepFor`, переписанные
      `Update`/`StartWave`/`PendingRef`.
- [ ] **Step 4 (удаление):** `SplitByZones`, `ZonelessWeights`, переезд
      бюджета ядра, `ZoneWeights` и `WavePause` во всех шести местах
      (`SimConfig.cs`, `WaveConfig.cs`, `SimConfigBuilder.cs:142,154,586,603-631`,
      `SimConfigHash.cs:133,140`).
- [ ] **Step 5 (доки, которые иначе станут ложью):** `WaveSystem` док класса
      `:6-24`, `SimConfig.cs:181,187`, `WaveConfig.cs:5-6,17-18,38`,
      `SnapshotEvents.cs:192`.
- [ ] **Step 6 (правка ломающихся тестов, поимённо):** `WaveZoneTests` —
      удалить `SplitByZones_*` и `CoreBudgetMovesToMiddle_TotalUnchanged`,
      переписать `PendingRef_*`, `Debt_IsNeverLostOnRounding`,
      `CoreLosesItsWaveBudget_AfterActivation`,
      `CoreDoesNotRegainBudget_AfterTheDirectorDies`; `ZoneConfigTests`
      `:213-220`, `:661-672` — удалить; `ConfigTests.AssertWaveEqual` —
      снять два поля; `SimConfigHashTests:50-51` — снять строку
      `"ZoneWeights"`; `WaveTests` и `WaveScalingTests` — на три кольца,
      включая `NoAlivePlayers_WaveDirectorFreezes_…` (`:222`, **расширяется,
      не дублируется**) и четыре ручные фикстуры `new WaveSimConfig`;
      `EliteAndDirectorTests:721`.
- [ ] **Step 7 (горячая правка — кейс, которого требует спека §3.2):** в
      `HotTweakTests` — `HotTweak_WavePauseChange_LeavesArmedTimersRunning`
      (правка `WavePauseByZone` через `ApplyConfig` не перезаряжает уже
      заряженные таймеры) — это **принятое** поведение, и тест делает его
      решением, а не дрейфом.
- [ ] **Step 8 (аллокации):** расширить существующий
      `AllocationTests.SaturatedTrio_TicksWithoutAllocations` (`:63`) —
      **новый тест не заводить**: скан живых и `stackalloc int[Zones.Count]`
      не аллоцируют.
- [ ] **Step 9 (мутации M1/M2/M3/M6/M9/M11; предсказания ДО прогона):**
      M1 — снять `PhaseTicks--` в `Active` (жертва
      `SecondWaveArrives_WithoutASingleKill`); M2 — снять `alive[z] == 0`
      (жертва `ClearIsNotCounted_WhileAnyMobOfThatRingLives`); M3 —
      `PhaseTicks = 0` на зачистке (жертва
      `ClearingARing_RestartsItsOwnTimer_…`, `Expected: 60, But was: 0`);
      **M6 — снять гвард неактивного кольца: жертв ДВЕ**
      (`CoreFreezes_WhenTheDirectorIsAwake` И `ZonelessArena_RunsOnlyTheOuterRing`);
      M9 — `wave.WaveIndex++` вместо присваивания шага (жертва
      `WaveIndex_FollowsTheClock_…`); M11 — накапливать долг (жертва
      `UnspawnedDebt_IsOverwrittenByTheNextWave_…`).
- [ ] **Step 10:** R-COMPILE → чисто; R-TEST полный → красные по таблице;
      **время прогона записать** (§4 спеки: рост более чем вдвое против
      ~40–60 с — находка, а не норма).
- [ ] **Step 11:** ГЕЙТ-КОДОГЕН → пусто.
- [ ] **Step 12:** R-COMMIT `feat(app-ggvz): Т4 — независимая каденция волн по
      кольцам и сложность от часов захода`.

### Task Т5: потолок численности кольца и сглаживание притока

**Files:**
- Modify: `client/Assets/Scripts/Simulation/AI/WaveSystem.cs`
  (`SpawnPendingOfType` `:292-324`, вызов из `Update`)
- Test: `WaveCadenceTests.cs`, `WaveTests.cs`, `WaveScalingTests.cs`
  (счётные ассерты ломаются от сглаживания — см. Step 6)

**Interfaces:**

```csharp
// Сигнатура МЕНЯЕТСЯ (v1 ошибочно писала «новых публичных имён не появляется»):
// метод зовётся по разу на КАЖДЫЙ архетип, поэтому счётчик спавнов обязан быть
// ref-параметром, иначе MaxSpawnsPerZonePerTick превратится в «N на архетип».
static void SpawnPendingOfType(SimulationWorld w, ref WaveState wave,
    in WaveSimConfig cfg, Zone zone, MobType type,
    System.Span<int> alive, ref int spawnedThisTick);
```

- Оба гварда стоят **ВНУТРИ** цикла попыток, рядом с существующим резервом
  Директора (`:316`) — иначе потолок переезжается внутри одного архетипа:

```csharp
if (alive[(int)zone] >= cfg.MaxAliveByZone[(int)zone]) return;   // долг остаётся
if (spawnedThisTick >= cfg.MaxSpawnsPerZonePerTick) return;      // долг остаётся
```

- Успешный спавн делает `alive[(int)zone]++` и `spawnedThisTick++`.
- Обоснование «гвард перед поиском места; `MobSpawnsSkipped` не трогаем,
  потому что это физический потолок арены» **уже написано** на `:305-312` —
  сослаться, а не повторять.

- [ ] **Step 1 (RED):** пять тестов в `WaveCadenceTests.cs`.
      ⚠ Потолок берётся **строго ниже размера волны**: при фикстурных
      `BaseCount 4` и одном игроке `CountForTest = round((4 + 2·0)·1) = 4`,
      поэтому потолок 4 закрыл бы долг и тест бы лгал (находка A-Critical).

```csharp
[Test]
public void RingAtItsCeiling_DoesNotSpawn_AndKeepsItsDebt()
{
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxAliveByZone = new[] { 2, 16, 8 };     // строго ниже волны (4)
    var w = new SimulationWorld(7, cfg);
    int skippedBefore = w.WorldStatsRef.MobSpawnsSkipped;
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 30);

    Assert.LessOrEqual(w.WaveRef(Zone.Outer).AliveCount, 2, "потолок кольца перееден");
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
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 30);
    Assert.LessOrEqual(w.WaveRef(Zone.Outer).AliveCount, 1);
    Assert.Greater(w.WaveRef(Zone.Middle).AliveCount, 1,
        "среднее кольцо остановилось из-за чужого потолка");
}

[Test]
public void WaveDoesNotOvershootTheCeiling_WithinASingleTick()
{
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxAliveByZone = new[] { 3, 16, 8 };
    cfg.Wave.MaxSpawnsPerZonePerTick = 64;            // сглаживание намеренно снято
    var w = new SimulationWorld(7, cfg);
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 4);
    Assert.LessOrEqual(w.WaveRef(Zone.Outer).AliveCount, 3,
        "инкремент alive внутри тика потерян — волна перелетела потолок");
}

[Test]
public void WaveArrivesGradually_NotInASingleTick()
{
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxSpawnsPerZonePerTick = 1;
    var w = new SimulationWorld(7, cfg);
    // РОВНО до тика старта: волна работает свой долг в тот же тик, поэтому
    // лишняя итерация дала бы второй спавн (находка A-Critical).
    TestWorlds.IdleTicks(w, FirstDelay(in cfg));
    Assert.AreEqual(1, w.WaveRef(Zone.Outer).AliveCount);
    Assert.Greater(w.WaveRef(Zone.Outer).PendingTotal, 0);
}

[Test]
public void RingWhoseCeilingIsBelowItsWave_NeitherHangsNorClears()
{
    // Тест 12 спеки — инвариант ядра, и он RED здесь, а не после мутаций.
    SimConfig cfg = TestConfigs.Default();
    cfg.Wave.MaxAliveByZone = new[] { 24, 16, 1 };
    var w = new SimulationWorld(7, cfg);
    int cleared = w.WorldStatsRef.WavesCleared;
    TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 120);
    Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Core).Phase);
    Assert.Greater(w.WaveRef(Zone.Core).PendingTotal, 0, "долг обязан сохраняться");
    Assert.LessOrEqual(w.WaveRef(Zone.Core).AliveCount, 1);
    Assert.AreEqual(cleared, w.WorldStatsRef.WavesCleared,
        "кольцо с потолком ниже волны не может быть вычищено — это инвариант ядра");
}
```

- [ ] **Step 2:** R-FILTER `WaveCadenceTests` → EXIT=2, пять красных.
- [ ] **Step 3 (GREEN):** два гварда внутри цикла, инкременты, `ref`-счётчик,
      новая сигнатура `SpawnPendingOfType`.
- [ ] **Step 4:** R-FILTER `WaveCadenceTests` → PASS.
- [ ] **Step 5 (мутации M4/M8/M10; предсказания ДО прогона):** M4 — `>=` →
      `>` в гварде потолка (жертва `RingAtItsCeiling_…`, население 3 при
      потолке 2); M8 — снять `alive[]++` (жертва
      `WaveDoesNotOvershootTheCeiling_…`); M10 — снять гвард
      `MaxSpawnsPerZonePerTick` (жертва `WaveArrivesGradually_…`,
      `AliveCount` станет равен размеру волны).
- [ ] **Step 6 (счётные тесты волн, ломающиеся от сглаживания):**
      `WaveTests.FirstWave_SpawnsAfterDelay_WithBaseCount` (`:11-20`) считает
      `MobCount == BaseCount` через `delay + 2` тика — при сглаживании за два
      тика приедет не вся волна; переписать на «долг закрылся за
      `ceil(count / MaxSpawnsPerZonePerTick)` тиков». То же для счётных
      ассертов `WaveScalingTests`.
- [ ] **Step 7:** R-TEST полный → красные по таблице; время прогона записать.
- [ ] **Step 8:** R-COMMIT `feat(app-ggvz): Т5 — потолок численности кольца и
      сглаживание притока`.

**Гейт фазы 2:** R-TEST по таблице; одиннадцать мутаций фазы убиты и
предсказания сверены; два фазовых ревьюера (Explore); `bd note`; push.

---

## Фаза 3 — данные и экран (Т6–Т7)

### Task Т6: числа в `.asset` и мёртвые ссылки

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/Geometry.cs` (`:322-347` —
  тексты двух отказов `ZoneSpawnRingRadius`, ссылающиеся на удалённые
  `ZonelessWeights`/`SplitByZones`)
- Modify: `client/Assets/Scripts/Simulation/Loot/ContainerStore.cs` (`:144-148`),
  `client/Assets/Scripts/Data/LootConfig.cs` (`:19`),
  `client/Assets/Scripts/Data/SimConfigBuilder.cs` (`:199`)
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
  (новый `ApplyWaveCadence`; аргумент `EnsureAssetHasKey` `:798`)
- Modify (через бутстрап, руками — НЕТ): `client/Assets/Data/WaveConfig.asset`

**Interfaces:**

```csharp
// ДВА РАЗНЫХ МЕХАНИЗМА, смешивать нельзя:
// 1) появление/исчезновение полей — маркер-ключ:
//    EditorBootstrapUtils.EnsureAssetHasKey(wave, $"{DataDir}/WaveConfig.asset",
//        "DifficultyStepSeconds");   // was EliteShareOuterCap, app-ggvz
// 2) правка СУЩЕСТВУЮЩИХ значений — одноразовый гейт на СТАРОМ значении
//    (правило 413), по образцу ApplyPlaytestOneArena (:2369).
//    ⚠ ВОЗВРАЩАЕТ bool: от этого флага зависят SetDirty/SaveAssets у
//    вызывающего (`waveChanged |= ApplyWaveCadence(wave);`, прецедент :717).
static bool ApplyWaveCadence(WaveConfig wave)   // только SetIfDifferent:
    // BaseCount 4 -> 16 (owner decision К5)
    // EliteShareOuterGrowth 0.02 -> 0.007 (Р311: elite ceiling on minute 12)
```

- ⚠ **Ключ гейта — `"ZoneWeights:"`** (Р319), не `"BaseCount: 4"`: гейт
  читается подстрокой, и `"BaseCount: 4"` совпал бы с будущим `BaseCount: 40`
  — первый же тюнинг владельца затёр бы его число обратно на 16.
- ⚠ **Ключ физически переживает удаление поля из C#** (проверено ревью по
  доку `EditorBootstrapUtils.cs:258-269`): Unity пишет текущий набор полей
  только когда что-то помечает ассет грязным, а `-runTests -batchmode`
  `SaveAssets` не зовёт. Единственный путь потерять ключ — владелец открыл
  Editor и сохранил проект; Step 1 это ловит.

- [ ] **Step 1 (проверка ключа ПЕРЕД работой):**
      `grep -c "^  ZoneWeights:" client/Assets/Data/WaveConfig.asset` → **1**.
      **Ноль** — переходить на построчный ключ `"\n  BaseCount: 4\n"` (перевод
      строки якорит совпадение, `BaseCount: 40` не матчится) и записать замену
      в отчёт таска.
- [ ] **Step 2:** переписать четыре мёртвые ссылки: два текста отказа
      `Geometry.ZoneSpawnRingRadius` (обоснование переводится с
      «`ZonelessWeights` + `SplitByZones`» на «неактивное кольцо заморожено,
      долг до `Middle`/`Core` на беззонной арене не доходит»),
      `ContainerStore.cs:148`, `LootConfig.cs:19`, `SimConfigBuilder.cs:199`.
- [ ] **Step 3:** `ApplyWaveCadence` (bool) + переезд аргумента маркер-ключа.
- [ ] **Step 4:** R-APPLY → EXIT=0. Диффа **две ветки** по результату Step 1:
      (а) ключ был — `git diff -- client/Assets/Data/` показывает ровно
      `BaseCount: 16`, `EliteShareOuterGrowth: 0.007`, исчезнувшие
      `ZoneWeights`/`WavePause`, появившиеся `WavePauseByZone [20,30,30]`,
      `MaxAliveByZone [150,110,10]`, `MaxSpawnsPerZonePerTick: 2`,
      `DifficultyStepSeconds: 20`; (б) ассет уже был переcериализован —
      старых ключей нет, новые приехали из C#-инициализаторов, и дифф
      показывает только две правки `SetIfDifferent`.
- [ ] **Step 5:** коммит артефактов → **R-IDEM** → пусто.
- [ ] **Step 6:** R-TEST полный → красные по таблице.
- [ ] **Step 7:** R-COMMIT `feat(app-ggvz): Т6 — числа каденции в .asset,
      мёртвые ссылки на удалённый бюджет зон переписаны`.

### Task Т7: вспышка номера волны в HUD

**Files:**
- Modify: `client/Assets/Scripts/Presentation/HudController.cs` (`:202`
  строка волны, `LateUpdate` `:245`, `HandleWorldRestarted` `:364-384`)
- Modify: `client/Assets/Scripts/Data/GameFeelConfig.cs` (новое поле +
  переезд маркера с `ContainerVisualScale` `:546` — те же **четыре** вещи)
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
  (`SetRef`, `EnsureAssetHasKey` `:788`)
- Test: `client/Assets/Tests/EditMode/HudPhaseLineTests.cs`

**Interfaces:**

```csharp
// Чистый статический шов: новая ветка продакшена обязана получить свидетеля в
// том же таске (369/383), а прецедент чистого шва в HUD уже есть
// (HudPhaseLineTests тестирует PhaseWord/Clock без подъёма MonoBehaviour).
public static float WaveAnnounceTimerAfter(int previousWaveNumber, int waveNumber,
    float timerNow, float announceSeconds, float deltaSeconds);
// номер вырос -> announceSeconds (ПЕРЕЗАРЯДКА, не очередь);
// иначе        -> math.max(0f, timerNow - deltaSeconds).
```

- Строка `_waveText` продолжает показывать `«ВОЛНА » + curr.Wave.WaveIndex` —
  теперь это мировой монотонный шаг сложности (агрегат Т3). **Правок в
  `NetworkSimBackend`, `SnapshotAssembler` и `LongRunHarness` не требуется**
  (Р334).
- Вспышка — смена цвета строки на `WaveAnnounceSeconds`; таймер декрементится
  **в `LateUpdate`** (как `_pickupMaskTimer` `:245`), чтобы не появился третий
  паттерн затухания.
- Виджет читаемый → **`raycastTarget = false`** (правило 34), и бутстрап чинит
  **уже существующий** объект, а не только вновь созданный.
- Гашение на `WorldRestarted` — в существующий список (`:364-384`).

- [ ] **Step 1 (RED):** в `HudPhaseLineTests.cs`:

```csharp
[Test]
public void WaveAnnounce_RearmsOnGrowth_AndDecaysOtherwise()
{
    Assert.AreEqual(1.5f, HudController.WaveAnnounceTimerAfter(3, 4, 0.2f, 1.5f, 0.016f), 1e-4f);
    Assert.AreEqual(1.484f, HudController.WaveAnnounceTimerAfter(4, 4, 1.5f, 1.5f, 0.016f), 1e-4f);
    Assert.AreEqual(0f, HudController.WaveAnnounceTimerAfter(4, 4, 0.01f, 1.5f, 0.016f), 1e-4f);
}
```

- [ ] **Step 2:** R-FILTER `HudPhaseLineTests` → EXIT=2.
- [ ] **Step 3 (GREEN):** шов + его использование; поле
      `WaveAnnounceSeconds` (дефолт 1.5, `[Range(0f, 10f)]`) последним в
      `GameFeelConfig` + четыре вещи переезда маркера.
- [ ] **Step 4:** R-APPLY → EXIT=0; сверить **набор `m_Name`** в `Main.unity`
      до и после (правило 14); порядок отрисовки читать **из сцены** (423).
- [ ] **Step 5:** коммит артефактов → **R-IDEM** → пусто.
- [ ] **Step 6:** R-TEST полный → красные по таблице.
- [ ] **Step 7:** R-COMMIT `feat(app-ggvz): Т7 — вспышка номера волны в HUD`.

**Гейт фазы 3:** R-TEST по таблице; R-IDEM сошёлся дважды; ГЕЙТ-КОДОГЕН пуст;
свип кириллицы (кроме ассертов) и британизмов пуст; два ревьюера; push;
jsonl-chore.

---

## Фаза 4 — перепин и гейт (Т8) → веха В4

### Task Т8: перепин №4, замеры, образ, закрытие

**Files:**
- Modify: `client/Assets/Tests/EditMode/DeterminismTests.cs` (`:345`, `:986`,
  `:1114`, `:1269`)
- Create: `$SDD/task-ggvz-report.md`

- [ ] **Step 1:** R-TEST полный **до** перепина; три `But was: <N>` из xml
      разбором питоном.
- [ ] **Step 2 (R-GOLDEN, санкция №4 — решение владельца К9):** три hex +
      десятичные дубли + обоснование, называющее **пять** причин сдвига:
      форма `WaveState` и три экземпляра в хеше; таймер в целых тиках; шаг
      сложности от часов; сглаживание притока; `MobState.SpawnZone`.
      **В том же коммите** поправить обоснование перепина №2 (`:986`),
      которое называет `ZoneWeights {0.45,0.45,0.10}` причиной прошлого
      сдвига.
- [ ] **Step 3:** R-TEST полный → **красных НОЛЬ**; `total` глазами; **время
      прогона в отчёт**.
- [ ] **Step 4:** снять **новый** md5 `DeterminismTests.cs` и записать в
      отчёт (прежний `02906aadbb2574b409ded7342f29ec75` мёртв).
- [ ] **Step 5:** R-COMMIT **отдельным коммитом** (R-23)
      `test(app-ggvz): перепин golden №4 — каденция волн по кольцам`.
- [ ] **Step 6:** **шесть** целей R-BUILD ФОНОМ; вердикт каждой — по строке
      «Exiting batchmode successfully».
- [ ] **Step 7:** R-IMAGE + доставка + сверка метки ревизии; сверка сборки,
      отдаваемой владельцу, **содержимым** (`strings -a`, литералы —
      `strings -a -el`, 418).
- [ ] **Step 8 (замер, §7 п. 4 спеки):** стенд втроём, `--cpus=1 --memory=1g`.
      Снять: CPU, `tickAvg`/`tickMax`, `framesMissing`, **`DroppedEntities`**,
      долю видимых мобов в кадре, `MobSpawnsSkipped`,
      `PickupSpawnsSkipped`/`ContainerSpawnsSkipped`. ⚠ Трафик мерить тоже,
      но он ничего не докажет: при `SnapshotMaxBytes 1000` и
      `MobRecordBytes 9` в кадр влезает ~60–80 записей мобов, порог 40 КБ/с
      структурно недостижим.
- [ ] **Step 9 (ГЕЙТ Р333 — решающее правило владельца):** если замер
      вынуждает опустить **суммарное живое население ниже 150** — **СТОП**:
      задачи производительности (индекс `id → слот` в сборщике, сетка
      расталкивания, снятие копии `MobSimConfig`) исполняются **ДО** вехи В4,
      а не после. Иначе владелец получит арену жиже той, ради которой задача
      затевалась.
- [ ] **Step 10 (амендменты — DoD §7 п. 6):** три строки спеки §9 передать в
      пачку Ф9: `bd note app-z8v` с текстом амендментов ADR-001 §3.1,
      ADR-002 §8 и словаря ADR-003 §9 («волна», «кольцо», «партия») плюс
      записью об отмене Р253. **ADR «по месту» не править** (урок 250).
- [ ] **Step 11 (side-quest'ы — DoD §7 п. 7):**

```bash
cd "$APP_REPO"
bd create "Мёртвый байт aliveCount в блоке волны: насыщается на 255 при населении 270 и никем не читается — расширить или снять при следующем подъёме протокола" -t task -p 3
bd create "Звук анонса хозяина: канон ADR-001 §3.1 называет анонс голосом, аудио-ассета нет" -t task -p 3
bd create "SnapshotAssembler.MobSlotOf — линейный скан по MobCount на каждый видимый id, O(MobCount^2) на соединение" -t task -p 2
bd create "Пространственная сетка для SeparationSystem: честный перебор пар без broad-phase" -t task -p 2
bd create "Снятие копии MobSimConfig в горячих циклах (Р335, отложено до замера)" -t task -p 2
bd create "Обрезка бумажных потолков MaxMobs/MaxProjectiles/MaxEventsPerFrame/MaxPickups после замера" -t task -p 3
# для каждого: bd dep add <new> app-ggvz --type discovered-from
```

- [ ] **Step 12:** отчёт в `$SDD/task-ggvz-report.md`; `bd note app-ggvz`
      коротко; `bd close app-ggvz` с evidence; **`bd export -o
      .beads/issues.jsonl`**; push; jsonl-chore из `$APP_REPO`.
- [ ] **Step 13 (СТОП):** **веха В4 — живой забег владельца** по восьми
      пунктам §5 спеки. Стенд ботов её не заменяет (417).

**Гейт фазы 4:** ноль красных; шесть сборок зелены; образ на хосте сверен
меткой; замер записан со всеми величинами Step 8; гейт Р333 пройден или
сработал; амендменты переданы; шесть side-quest'ов заведены; `app-ggvz`
закрыт с evidence; деревья чисты, обе ветки запушены и сверены `ls-remote`.

---

## Декомпозиция bd (создать ДО Т1, после апрува плана)

```bash
cd "$APP_REPO"
bd create "Т1: приписка моба к кольцу и один дом для числа колец"       -t task -p 0
bd create "Т2: числа каденции в конфиге и пять правил валидации"        -t task -p 0
bd create "Т3: три экземпляра WaveState, тики, агрегат в снимке"        -t task -p 0
bd create "Т4: каденция — таймер, часы сложности, зачистка"             -t task -p 0
bd create "Т5: потолок численности кольца и сглаживание притока"        -t task -p 0
bd create "Т6: числа в .asset и мёртвые ссылки"                         -t task -p 0
bd create "Т7: вспышка номера волны в HUD"                              -t task -p 0
bd create "Т8: перепин golden №4, замеры, образ, веха В4"               -t task -p 0
# для каждого: bd dep add <ТN> app-ggvz --type parent-child
# цепочка:     bd dep add <ТN+1> <ТN>
```

## Распределение мутаций спеки §4 по таскам

| Мутация | Таск / шаг | Названная жертва |
|---|---|---|
| M1 таймер не тикает в `Active` | Т4 Step 9 | `SecondWaveArrives_WithoutASingleKill` |
| M2 снять `alive == 0` из зачистки | Т4 Step 9 | `ClearIsNotCounted_WhileAnyMobOfThatRingLives` |
| M3 `PhaseTicks = 0` на зачистке | Т4 Step 9 | `ClearingARing_RestartsItsOwnTimer_…` |
| M4 `>=` → `>` в потолке | Т5 Step 5 | `RingAtItsCeiling_…` |
| M5 `SpawnZone` всегда `Outer` | Т1 Step 7 | `SpawnZone_IsSetByTheSpawner_NotByPosition` |
| M6 снять гвард неактивного кольца | Т4 Step 9 | **две**: `CoreFreezes_…` и `ZonelessArena_…` |
| M7 массив волн ссылкой в `SaveState` | Т3 Step 8 | `SaveState_DoesNotAliasTheLiveWaveArray` |
| M8 снять `alive[]++` | Т5 Step 5 | `WaveDoesNotOvershootTheCeiling_…` |
| M9 `WaveIndex++` вместо шага часов | Т4 Step 9 | `WaveIndex_FollowsTheClock_…` |
| M10 снять `MaxSpawnsPerZonePerTick` | Т5 Step 5 | `WaveArrivesGradually_…` |
| M11 накапливать долг | Т4 Step 9 | `UnspawnedDebt_IsOverwrittenByTheNextWave_…` |
| M12 хешировать только `waves[Outer]` | Т3 Step 8 | свип `WorldLifecycleTests` по зонам |
| M13 ослабить правила `Validate` (**пять мутаций**) | Т2 Step 6 | по одной жертве на правило |

## Отклонения от спеки (правило 22)

1. **Спека §10 называла семь тасков, план даёт восемь** — Т3 разделён на
   «состояние» и «каденцию», потому что объединённый таск не помещался ни в
   один цикл тестов (находка D-Important: четыре его шага были по 5–15 шагов
   каждый).
2. **`MaxSpawnsPerTick` переименован в `MaxSpawnsPerZonePerTick`** (находка
   B-Minor): счётчик обнуляется на кольцо, и имя обязано это говорить.

## Что исправил self-review плана (v1 → v2)

- **Слом компиляции четырёх сборок** снят решением Р334 (спека v4):
  `WaveIndex` остаётся в состоянии и получает шаг часов, поэтому
  `HudController`, `NetworkSimBackend.ReadWave`, `SnapshotAssembler` и
  `LongRunHarness` **не трогаются вовсе**, а `RenderSnapshot.WaveNumber`
  отменён. Оставшиеся ломающиеся файлы (`InterpolationBufferTests`,
  `SnapshotAssemblerTests`) перенесены в Т3, где ломаются.
- **Восемь арифметических дефектов в тестах:** порог «одного тика» не
  срабатывал (`TicksFromSeconds(0.02) = 1`) → «два тика»; правило суммы
  индексировало массив до проверки длины → внутрь `else`; беззонная фикстура
  через `OpenField()` тикала бы 30 млн раз (`Quiet()` ставит
  `FirstWaveDelay = 1e6`) → строится от `Default()`; зачистка засчитывалась
  трижды → соседям оставляется долг; потолок 4 равнялся размеру волны 4 →
  потолок 2; `i < first + 1` давал второй спавн → `i < first`.
- **Четыре отсутствовавших свидетеля мутаций:** M7 (алиасинг), M11
  (накопление долга), M2 (негативная половина зачистки), M9 (тавтология
  `f(x) == f(x)` заменена настоящим тестом с отодвинутым стартом).
- **Четыре пропущенных требования спеки:** сторож умолчания `SpawnMob`,
  кейс `HotTweakTests`, расширение `AllocationTests`, правило валидации №5.
- **Р327 отменён (Р335):** второй дом «архетип → радиус» уже существует и
  запиннен, в парном цикле расталкивания читается два поля а не радиус, а у
  предложенной альтернативы не было свидетеля. Работа ушла под гейт Р333.
- **Конвенции:** нарушение в тестах валидации переехало на **второй** элемент
  массива (правило файла, ledger 227); маркер-ключ переезжает **четырьмя**
  вещами, включая надгробную пометку; новым полям SO прописаны `[Range]`;
  числа фикстур задаются **один раз** в `Default()`; `ApplyWaveCadence`
  возвращает `bool`; `EnsureAssetHasKey` квалифицирован
  `EditorBootstrapUtils.`; `DifficultyStepFor` — по конвенции имён.
- **Ложные утверждения v1 сняты:** «ветка дефолтов при отсутствующем SO»
  (её нет — `:225-245` про `Loot`); «`EnsureAssetHasKey` переписывает и
  сохраняет ассет» (она только `SetDirty`); «все четыре производственных
  call-site'а `SpawnMob`» (их три); «127 вызовов» (124); «новых публичных
  имён не появляется» в Т5 (сигнатура `SpawnPendingOfType` меняется).
- **Гейт «красных ровно три» заменён таблицей ожиданий по таскам**, и в ней
  явно назван четвёртый красный на Т4 (`SimConfigHashTests`, держит имя
  удаляемого поля строкой).
- **Т8 достроен:** амендменты в пачку Ф9, шесть side-quest'ов командами,
  `bd close` + `bd export`, и гейт Р333 между замером и вехой.
