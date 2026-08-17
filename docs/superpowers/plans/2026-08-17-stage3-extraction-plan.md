# План имплементации: Этап 3 «Extraction-петля» — три зоны, Директор, лут, выход (app-35g)

> **For agentic workers:** REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.
> Распределение моделей (предложение агента, утверждает владелец): implementer
> per task = **sonnet** для механики по готовым формулам; **opus** — Т7 (примитив
> дуги), Т17–Т18 (ядро лутания и гонки), Т21 (машина фазы), Т25–Т27 (кодек,
> видимость, бюджет), Т32 (окно инвентаря); **fable** — концептуальные развилки
> и ревью фаз. Ревьюеры фаз = 2 × Explore (спека-соответствие + качество/
> арифметика). **Верификация всех вердиктов, прогоны R-TEST/R-COMPILE, гейты и
> вехи — main-агент лично, не на веру.** Шаги — чекбоксы `- [ ]`.

**Goal:** превратить сетевую арену Этапа 2 в заход с петлёй извлечения — три
зоны со стенами-дугами и дверями, элита и минимальный Директор, конечный
боезапас из энергоячеек, тир-лут в рюкзаке, серверно-авторитетное лутание,
ранние порталы и створ, результат в формате будущей меты — по спеке
`docs/superpowers/specs/2026-08-17-stage3-extraction-spec.md` (**v3**).

**Architecture:** всё игровое остаётся в `Ring.Simulation` чистым C# (CR 1) —
зоны как функция радиуса, подбираемое и контейнеры как массивы с капом по
образцу мобов, фаза захода как машина из двух хранимых полей. Networking не
получает игровой логики: пять новых блоков снапшота и одно надёжное сообщение
лута, всё остальное — существующие механизмы (кодек, квантизация, фильтр
видимости, бюджет и усечение). Presentation узнаёт о новом мире через тот же
`RenderSnapshot` и тот же `ViewRegistry`; окно инвентаря рисует `Presentation`,
а сетевой запрос шлёт `PresentationNet` — граница asmdef не двигается.

**Tech Stack:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics, FishNet
4.7.2, MetaVoiceChat 4.2, Docker + Docker Hub. **Новых пакетов этап не вводит**
(CR 9).

**Статус плана:** **v2 — после self-review по `review_plan.md`** (четыре субагента: 10 Critical, 53 Important, ~49 Minor). ⚠ **ПЛАН ПРОТИВ ERRATA В КОНЦЕ ФАЙЛА — ВЕРИТЬ ERRATA** (урок 124): три структурных блока (E-1 состав состояния против санкций golden, E-2 `MatchFlowConfig`, E-3 переписанный Т24) меняют границы тасков и читаются ДО первого таска.

**Спека:** `docs/superpowers/specs/2026-08-17-stage3-extraction-spec.md` **v3**
(С1–С30, Р206–Р300; self-review по `review_spec.md` четырьмя субагентами —
8 Critical, 30 Important, 26 Minor, §6a).

---

## Global Constraints (каждый таск обязан соблюдать)

- **Пути:** `APP_REPO="/home/brolin/Documents/!_MY_Proj/The Ring/app"`
  (bd — ТОЛЬКО отсюда); `WT="$APP_REPO/.worktrees/feature-app-5nu-stage2-network"`
  — cwd всех команд; ветка `feature/app-35g-stage3-extraction` от `origin/main`
  **уже создана** в существующем worktree и не пересоздаётся (внутри 334 файла
  SDD Этапа 2, они гитигнорятся — **worktree не удалять**);
  `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH=<scratchpad ТЕКУЩЕЙ сессии>` — задать на старте.
- **Стартовые счётчики:** **987** EditMode-тестов; соло-golden
  `0x5BD8AC0DE1D0C454UL` (`DeterminismTests.cs:503`), мультиплеерный
  `0x136FA6114112E44FUL` (`:575`), md5 файла `fa8cf2ba63622c794c7744f38bad57dc`.
- **Санкции на движение golden — РОВНО ДВЕ, обе внутри этапа** (спека С28/§4):
  - **перепин №1** — конец Ф1 (Т6), «состав состояния»: боезапас, подбираемое,
    рюкзак, `MatchState`, `OwnerEntityId`. Двигаются **оба** golden;
  - **перепин №2** — конец Ф2 (Т12), «арена и обитатели»: радиус 113, зоны,
    дуги, двери, кольцо спавна, капы, зонный бюджет, элита и Директор.
    Двигаются **оба** golden;
  - **третий golden — НОВАЯ константа** (Т36), не перепин: санкции не тратит.
  - Любое иное движение любой из трёх констант — **стоп и вопрос владельцу**.
- **Запретный список:** не менять `client/CLAUDE.md`, `.github/CODEOWNERS`,
  `.gitattributes`, контент паков вне `_Ring/`, `client/ProjectSettings/**`
  кроме того, что правят бутстрапы. **`client/Assets/Data/*.asset` руками не
  редактировать** — доставка только через бутстрап (Т12/Т13).
- **Simulation меняется** — строго TDD (CR 2), без UnityEngine (CR 1).
- **Два источника чисел** (спека §0, Р56): `.asset` — числа игры; C#-дефолты и
  `TestConfigs` — числа тестов. **Ожидания в тестах — только фикстурными
  выражениями**; литерал из `.asset` в тесте = находка ревью.
- **Орфография идентификаторов — американская**; британские формы — находка.
- **ГЕЙТ-ОТКАТ (после КАЖДОГО Unity-прогона):**
  `git status --porcelain -- client/Packages client/Assets/Settings
  .gitattributes client/ProjectSettings "client/Assets/TextMesh Pro"` → пусто;
  иной дрифт → `git checkout -- <пути>`; TMP-самопис откатывать всегда (урок 32).
  Два принятых отклонения Этапа 2 (`ProjectSettings.asset` символы FishNet,
  `Physics2DSettings.asset` пересериализация) остаются принятыми.
- **ГЕЙТ-ЛОГ (после каждого batchmode):** `grep -E "error CS|Shader error|
  Failed to import|Error while importing|NullReferenceException|Exception"
  <лог>` → пусто (кроме явно ожидаемых таском строк).
- **ГЕЙТ-META:** каждому новому не-`.meta` файлу **и папке** соответствует
  `<path>.meta`; **только под `client/Assets/**`** (Р199).
- **ГЕЙТ-КОДОГЕН (после любого таска с проводной структурой):**
  `strings -a client/Library/ScriptAssemblies/Ring.Networking.dll | grep -E
  "Comparer___|GWrite___Unity|GRead___Unity"` → **ПУСТО**; то же для
  `Ring.Presentation.Net.dll` и `MetaVoiceChat.dll`. Новая проводная структура с
  полем `float2` обязана получить `[CustomComparer]` в
  `Networking/Protocol/MathCodegenSupport.cs` (спека Э2 Р110/Р157).
- **RED-дисциплина:** тест не компилируется из-за отсутствующих полей/сигнатур
  → сначала заглушки до КОМПИЛЯЦИИ, затем наблюдаемый FAIL ассерта. Ошибка
  компиляции ≠ RED. Заглушка — **КОНСТАНТА**, а не «почти реализация».
- **Мутация на каждую ветку** (уроки 244/245): в брифе таска исполнитель
  называет мутацию ДО написания теста; подопытный — **второй** элемент (227);
  эквивалентный мутант доказывается ДРУГОЙ мутацией.
- **Тест-швы состояния:** канон — `var p = w.PlayerAt(i); p.X = …;
  w.SetPlayerForTest(i, p);`. Существующие хелперы переиспользуются:
  `TestEvents.TryFirstOf`, `TestWorlds.Saturated/SpawnMobsAt/FireAimed3D/
  RunUntilProjectilesDie/ClearFirstWave`, `TestConfigs.Default/Open/Quiet/
  RegenFixture`. Новые параметры существующих хелперов — **только хвостовыми с
  умолчанием**.
- **Новые SO:** `[CreateAssetMenu]`, `[Range(min, max)]` с осмысленным верхом,
  дефолты, `OnValidate() => RingDataChanged.Raise()` под `#if UNITY_EDITOR`;
  маркер-поле — **ПОСЛЕДНЕЕ** в классе + `// sync-marker key — keep LAST`.
  ⚠ **Маркер существующего SO переезжает** при дописывании полей (спека Р285).
- **Словарь ADR-003 §9 + A1/A3:** проза и UI — «сборщик», «носитель», «хозяин»,
  «Директор», «свита», «створ», «заход», «энергоячейка», «ремкомплект»,
  «контейнер», «тайник», «ядро памяти»; код — `Player`, `Retinue`, `Gate`,
  `Pickup`, `RepairKit`, `Container`, `Cache`, `MemoryCore`. Комментарии `.cs` —
  английские (урок 44); свип кириллицы — пункт каждого фазового ревью.
- **bd:** сабтаски фаз создаются ДО Т1 (раздел «Декомпозиция bd» в конце);
  клейм сабтаска на старте фазы, `bd note app-35g` КОРОТКО после каждого таска,
  эвиденс — **файлом в `$SDD`**; `bd close` сабтаска с evidence; после каждого
  `bd close` — явный `bd export -o .beads/issues.jsonl` (урок 236); jsonl-дрифт —
  `chore(app-35g): jsonl-дрифт beads — Фаза ФN` из `$APP_REPO` в main.
- **Коммиты:** `feat|test|fix|refactor|chore|docs(app-35g): …` (рус.) + трейлер
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед каждым —
  секрет-чек `git status --short --untracked-files=all | grep -E
  '\.(env|pem|key)$|secrets/'` → пусто. Перед коммитом сверять
  `git diff --cached --stat` со скоупом таска (урок 225).
- batchmode не гонять при открытом Editor'е владельца
  (`ps aux | grep -i "[U]nity/Hub/Editor.*projectpath"`); перед прогоном —
  проверка/удаление stale `client/Temp/UnityLockfile` (урок 39); запуск —
  `timeout -k 30 900` foreground. **Сборка — ФОНОМ** (199), код читать в логе
  самого скрипта (234/243).

## Runbook

- **R-TEST:** `cd "$WT" && timeout -k 30 900 "$UNITY" -runTests -batchmode
  -projectPath client -testPlatform EditMode -testResults "$SCRATCH/t.xml"
  -logFile "$SCRATCH/t.log"; echo EXIT=$?` → EXIT=0, в xml `failed="0"`
  (БЕЗ `-quit`) + ГЕЙТ-ОТКАТ. **`total` читать ГЛАЗАМИ** (169); старт 987.
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
  однострочное обоснование перепина** → повторный R-FILTER → PASS.
  **Разрешён только в Т6 и Т12.**
- **R-BUILD-`<X>`:** `cd "$WT" && RING_BUILD_ROOT="$SCRATCH/builds"
  timeout -k 30 900 "$UNITY" -batchmode -quit -projectPath client
  -executeMethod Ring.Editor.BuildCommands.Build<X> -logFile "$SCRATCH/b<X>.log";
  echo EXIT=$?` (X ∈ `LinuxServer` | `LinuxClient` | `WindowsClient` |
  `LinuxServerDev` | `LinuxClientDev` | `WindowsClientDev`). **Гонится ФОНОМ**,
  код выхода читается в логе.
- **R-IMAGE:** `cd "$WT" && client/docker/build.sh [--no-push]` → в выводе
  размер и sha; push только с ЧИСТОГО дерева.
- **R-STAND (стенд без человека, 230):** `./Ring -batchmode -nographics
  -ring-connect <хост>:7777 -ring-player-id pN -ring-join-token tN
  -ring-latency off -logFile <лог>`; троих собирать в ОДНОМ 120-секундном окне
  (240); повтор `playerId` = `DuplicatePlayer` навсегда.
- **R-COMMIT:** секрет-чек → ГЕЙТ-META → `git diff --cached --stat` против
  скоупа → `git add <файлы+meta> && git commit -m "<msg>" -m "<трейлер>"`.

---

## Фаза Ф1 — экономика захода (Т1–Т6) → перепин golden №1

Цель фазы — внести **весь** новый состав состояния мира одним заходом, чтобы
golden сдвинулся ровно один раз. Ни арены, ни зон, ни контейнеров здесь ещё
нет: только поля, их поведение и их место в хеше.

### Task Т1: `MatchState`, `Extracted` и третья причина конца захода

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs`,
  `.../Core/SimulationWorld.cs`, `.../Core/WorldSave.cs`,
  `.../Core/RenderSnapshot.cs`,
  `client/Assets/Scripts/Networking/Server/MatchEndPolicy.cs`,
  `client/Assets/Scripts/Networking/Server/MatchServer.cs`
- Create: `client/Assets/Tests/EditMode/MatchFlowTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
public enum MatchPhase : byte { Farm = 0, DirectorActive = 1, GateOpen = 2, Ended = 3 }

public struct MatchState
{
    public MatchPhase Phase;
    public int DirectorDeathTick;   // 0 = Директор жив или не активирован
}

// SimulationWorld
public MatchState Match => _match;
internal ref MatchState MatchRef => ref _match;
internal void SetMatchForTest(in MatchState m);

// PlayerState
public bool Extracted;              // рядом с Alive; инвариант: !(Alive && Extracted)

// MatchEndPolicy — ТРЕТЬЕ значение добавляется В КОНЕЦ пиненного перечисления
public enum MatchEndReason : byte
{ None = 0, AllPlayersDead = 1, MaxDurationReached = 2, AllPlayersResolved = 3 }
public MatchEndReason Evaluate(int worldTick, int alivePlayers, int activePlayers);
public static int ExitCodeFor(MatchEndReason reason);   // AllPlayersResolved -> 0
```

- **Приоритет причин:** `AllPlayersResolved` проверяется **раньше**
  `AllPlayersDead` (спека §3.10). Сегодня код проверяет смерть первой
  (`MatchEndPolicy.cs:100`) — порядок меняется.
- «Активный» = живой И не извлечённый. `MatchServer` считает оба числа после
  `TickAll` и передаёт их обоим параметрами.
- **Хеш пока не трогаем** — `MatchState` входит в него в Т6.

- [ ] **Step 1 (RED):** `MatchFlowTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Networking.Server;
using Ring.Simulation.Core;

public class MatchFlowTests
{
    [Test]
    public void NewWorld_StartsInFarmPhase()
    {
        var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
        Assert.AreEqual(MatchPhase.Farm, w.Match.Phase);
        Assert.AreEqual(0, w.Match.DirectorDeathTick);
    }

    [Test]
    public void ExtractedIsNotAlive_AndNotBothAtOnce()
    {
        var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
        var p = w.PlayerAt(1);
        p.Alive = false; p.Extracted = true;
        w.SetPlayerForTest(1, p);
        Assert.IsFalse(w.PlayerAt(1).Alive && w.PlayerAt(1).Extracted);
    }

    [Test]
    public void Resolved_OutranksAllDead_WhenSomeoneExtracted()
    {
        var policy = new MatchEndPolicy(maxDurationTicks: 1000);
        // двое погибли, один извлёкся: живых 0, активных 0
        Assert.AreEqual(MatchEndReason.AllPlayersResolved, policy.Evaluate(10, 0, 0));
    }

    [Test]
    public void AllDead_WhenNobodyExtracted()
    {
        var policy = new MatchEndPolicy(maxDurationTicks: 1000);
        Assert.AreEqual(MatchEndReason.AllPlayersDead, policy.Evaluate(10, 0, 0, anyExtracted: false));
    }

    [Test]
    public void ResolvedExitCode_IsZero()
        => Assert.AreEqual(0, MatchEndPolicy.ExitCodeFor(MatchEndReason.AllPlayersResolved));

    [Test]
    public void EndReasonValues_AreStableOnTheWire()
    {
        Assert.AreEqual(0, (byte)MatchEndReason.None);
        Assert.AreEqual(1, (byte)MatchEndReason.AllPlayersDead);
        Assert.AreEqual(2, (byte)MatchEndReason.MaxDurationReached);
        Assert.AreEqual(3, (byte)MatchEndReason.AllPlayersResolved);
    }
}
```

  ⚠ Сигнатура `Evaluate` в тесте выше показана в двух формах — исполнитель
  выбирает ОДНУ и приводит оба теста к ней. Каноническая:
  `Evaluate(int worldTick, int alivePlayers, int activePlayers, bool anyExtracted)`.
  «Активных нет и кто-то извлёкся» → `AllPlayersResolved`; «активных нет и никто
  не извлёкся» → `AllPlayersDead`.

- [ ] **Step 2:** заглушки (`MatchPhase`, `MatchState`, поле `Extracted`,
  четвёртый параметр `Evaluate` с телом `return MatchEndReason.None;`) →
  R-FILTER `MatchFlowTests` → **FAIL ассертов**, не ошибка компиляции.
- [ ] **Step 3 (GREEN):** реализация полей и приоритета причин; `MatchServer`
  считает активных и передаёт `anyExtracted`.
- [ ] **Step 4:** R-FILTER `MatchFlowTests` + `MatchLifecycleTests` +
  `DeterminismTests` → PASS, **оба golden на месте**; R-TEST → 987 + 6.
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т1 — фаза захода, Extracted и третья
  причина конца`.

### Task Т2: боезапас — счётчик, трата в обоих путях, аварийный синтез

**Files:**
- Modify: `.../Core/SimStates.cs` (поле `Ammo`), `.../Core/SimConfig.cs`
  (`WeaponSimConfig`), `.../Combat/WeaponSystem.cs`,
  `.../Core/SimulationWorld.cs` (`ApplyConfig` кламп),
  `client/Assets/Scripts/Data/WeaponConfig.cs`,
  `client/Assets/Tests/EditMode/TestConfigs.cs`
- Create: `client/Assets/Tests/EditMode/AmmoTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
// PlayerState
public int Ammo;                     // в ВЫСТРЕЛАХ

// WeaponSimConfig
public int ShotsPerCell;             // 10
public int AmmoStart;                // 120
public int AmmoMax;                  // 400
public float EmergencyFireInterval;  // 1.25 — интервал при Ammo == 0

// WeaponSystem — два публичных члена НЕ РАСТУТ в числе, растёт их тело
public static bool CanFire(in PlayerState p, in SimInput input, in WeaponSimConfig weapon);
public static bool WouldFireThisTick(in PlayerState p, in SimInput input, in WeaponSimConfig weapon);
internal static float IntervalFor(in PlayerState p, in WeaponSimConfig weapon)
    => p.Ammo > 0 ? weapon.FireInterval : weapon.EmergencyFireInterval;
```

- **Правило интервала (спека Р261):** интервал выбирается по `Ammo` **ДО**
  списания — последний патрон уходит с обычным интервалом, следующий выстрел
  уже аварийный.
- **Трата — в общем теле**, которым пользуются и `Update` (сервер), и
  `AdvanceNoSpawn` (предсказание): `if (p.Ammo > 0) p.Ammo--;`. Аварийный
  выстрел боезапас не тратит и не начисляет.
- **Кламп при подборе:** когда `Ammo` переходит из 0 в положительное,
  `p.FireCooldown = math.min(p.FireCooldown, weapon.FireInterval)`.
- `ApplyConfig`: `p.Ammo = math.min(p.Ammo, next.Weapon.AmmoMax)`.
- Конструктор мира: `Ammo = config.Weapon.AmmoStart`.

- [ ] **Step 1 (RED):** `AmmoTests.cs`:

```csharp
[Test]
public void StartsWithConfiguredAmmo()
{
    var cfg = TestConfigs.Open();
    var w = new SimulationWorld(1, cfg);
    Assert.AreEqual(cfg.Weapon.AmmoStart, w.Player.Ammo);
}

[Test]
public void EveryShotSpendsExactlyOne()
{
    var cfg = TestConfigs.Open();
    var w = new SimulationWorld(1, cfg);
    int before = w.Player.Ammo;
    var input = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
    int shots = 0;
    for (int t = 0; t < 30; t++)
    {
        bool fires = WeaponSystem.WouldFireThisTick(w.Player, input, cfg.Weapon);
        w.Tick(input);
        if (fires) shots++;
    }
    Assert.Greater(shots, 0);
    Assert.AreEqual(before - shots, w.Player.Ammo);
}

[Test]
public void AtZero_FiresOnEmergencyInterval_AndSpendsNothing()
{
    var cfg = TestConfigs.Open();
    var w = new SimulationWorld(1, cfg);
    var p = w.Player; p.Ammo = 0; w.SetPlayerForTest(p);
    var input = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
    int fired = 0;
    int ticks = (int)math.ceil(cfg.Weapon.EmergencyFireInterval / SimulationWorld.TickDt) + 2;
    for (int t = 0; t < ticks; t++)
    {
        if (WeaponSystem.WouldFireThisTick(w.Player, input, cfg.Weapon)) fired++;
        w.Tick(input);
    }
    Assert.AreEqual(1, fired, "аварийный режим — один выстрел за интервал");
    Assert.AreEqual(0, w.Player.Ammo);
}

[Test]
public void LastRound_UsesNormalInterval_NextOneIsEmergency()
{
    var cfg = TestConfigs.Open();
    var w = new SimulationWorld(1, cfg);
    var p = w.Player; p.Ammo = 1; p.FireCooldown = 0f; w.SetPlayerForTest(p);
    Assert.AreEqual(cfg.Weapon.FireInterval, WeaponSystem.IntervalFor(w.Player, cfg.Weapon), 1e-6f);
    w.Tick(new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) });
    Assert.AreEqual(0, w.Player.Ammo);
    Assert.AreEqual(cfg.Weapon.EmergencyFireInterval, WeaponSystem.IntervalFor(w.Player, cfg.Weapon), 1e-6f);
}

[Test]
public void RefillClampsEmergencyCooldownDown()
{
    var cfg = TestConfigs.Open();
    var w = new SimulationWorld(1, cfg);
    var p = w.Player; p.Ammo = 0; p.FireCooldown = cfg.Weapon.EmergencyFireInterval;
    w.SetPlayerForTest(p);
    w.AddAmmoForTest(0, cfg.Weapon.ShotsPerCell);
    Assert.LessOrEqual(w.Player.FireCooldown, cfg.Weapon.FireInterval);
}
```

- [ ] **Step 2:** заглушки (`Ammo`, четыре поля конфига, `IntervalFor => 0f`,
  `AddAmmoForTest`) → R-FILTER `AmmoTests` → **FAIL ассертов**.
- [ ] **Step 3 (GREEN):** реализация; `WeaponConfig.cs` получает четыре поля с
  `[Range]`, **маркер-ключ переезжает** на последнее поле класса.
- [ ] **Step 4 (мутация — обязательный шаг):** убрать `p.Ammo--` из
  `AdvanceNoSpawn`, оставив в `Update` → `PredictionParityTests` обязан
  покраснеть. Вернуть. Записать наблюдение в отчёт таска.
- [ ] **Step 5:** R-FILTER `AmmoTests`+`WeaponTests`+`PredictionParityTests`+
  `DeterminismTests` → PASS, **оба golden на месте** (в golden-сценарии
  `AmmoStart` заведомо больше числа выстрелов за 1000 тиков — проверить
  арифметикой в отчёте, иначе перепин случится здесь, а он разрешён только Т6);
  R-TEST.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т2 — боезапас и аварийный синтез`.

### Task Т3: подбираемое — сущность, дроп, авто-подбор, TTL, счётчик

**Files:**
- Create: `client/Assets/Scripts/Simulation/Loot/PickupSystem.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/PickupTests.cs` (+ `.meta`)
- Modify: `.../Core/SimStates.cs` (`PickupState`, `WorldStats`),
  `.../Core/SimulationWorld.cs`, `.../Core/SimConfig.cs`,
  `client/Assets/Scripts/Data/ArenaConfig.cs`, `.../Data/HeroConfig.cs`,
  `client/Assets/Tests/EditMode/TestConfigs.cs`

**Interfaces:**

```csharp
public enum PickupKind : byte { EnergyCell = 0 }   // Data (красные) — эпик «Рост носителя»

public struct PickupState
{
    public int Id;
    public float2 Pos;
    public PickupKind Kind;
    public int Amount;        // int, НЕ ushort — рефлексивный свип хеша не знает ushort (Р258)
    public float Ttl;
}

// SimulationWorld
internal PickupState[] Pickups { get; }
internal int PickupCount { get; }
internal int SpawnPickup(PickupKind kind, float2 pos, int amount);  // -1 при капе + счётчик
internal void RemovePickupAt(int index);                            // swap-remove
internal void SetPickupForTest(int index, in PickupState p);
public void AddAmmoForTest(int playerIndex, int shots);             // шов Т2

// WorldStats
public int PickupSpawnsSkipped;      // новое поле рядом с MobSpawnsSkipped

// ArenaSimConfig: MaxPickups (256)
// HeroSimConfig: PickupRadius (2)
// LootSimConfig (появится в Т13) временно НЕ нужен: числа дропа берутся из
//   MobSimConfig.CellsOnDeath — по одному полю на архетип, доставка в Т12
```

- **Порядок в тике:** подбор — **последним шагом `TickAll`**, после боя.
  Собирает **живой и не извлечённый** игрок; порядок — по возрастанию индекса
  игрока, затем по индексу слота.
- **TTL:** `Ttl` уменьшается на `TickDt`; при `≤ 0` — swap-remove без события.
- **Дроп:** в `DamageMob` при смерти — `SpawnPickup(EnergyCell, pos,
  MobConfigFor(type).CellsOnDeath)`; в `KillPlayer` — `floor(Ammo * 0.5 /
  ShotsPerCell)`, минимум 1 при `Ammo > 0`.

- [ ] **Step 1 (RED):** `PickupTests.cs` — семь тестов:

```csharp
[Test] public void MobDeath_DropsConfiguredCells();
[Test] public void PlayerDeath_DropsHalfOfCarriedAmmo_AtLeastOne();
[Test] public void WalkingOver_PicksUpAndAddsAmmo();
[Test] public void TwoPlayersOnOneCell_LowerIndexWins();
[Test] public void DeadInSameTick_PicksNothing();
[Test] public void CapReached_SkipsAndCounts();     // подопытный — ВТОРОЙ пикап (227)
[Test] public void TtlExpiry_RemovesWithoutEvent();
```

  Каждый — с явной фикстурой чисел в теле теста (конвенция C14), без литералов
  из `.asset`.

- [ ] **Step 2:** заглушки → R-FILTER `PickupTests` → **FAIL ассертов**.
- [ ] **Step 3 (GREEN):** `PickupSystem.Update(SimulationWorld w)` — TTL и
  подбор; вызов последним в `TickAll`; дроп в двух местах.
- [ ] **Step 4 (мутация):** снять гвард «не извлечён» в подборе → тест
  `DeadInSameTick_PicksNothing` обязан покраснеть; вернуть.
- [ ] **Step 5:** R-FILTER `PickupTests`+`AmmoTests`+`DeterminismTests` → PASS,
  golden на месте (в `TestConfigs` `CellsOnDeath = 0`, поэтому сценарий не
  меняется — проверить и записать); R-TEST.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т3 — энергоячейки, дроп и авто-подбор`.

### Task Т4: рюкзак — слот-очки, швы, состояние

**Files:**
- Create: `client/Assets/Scripts/Simulation/Loot/Inventory.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/InventoryTests.cs` (+ `.meta`)
- Modify: `.../Core/SimulationWorld.cs`, `.../Core/WorldSave.cs`,
  `.../Core/SimConfig.cs`, `client/Assets/Scripts/Data/HeroConfig.cs`,
  `client/Assets/Tests/EditMode/TestConfigs.cs`

**Interfaces:**

```csharp
// Плоское хранилище рядом с _matchStats: byte-идентификаторы предметов
// SimulationWorld
public int InventoryCountOf(int playerIndex);
public byte InventoryItemAt(int playerIndex, int slot);
public int InventoryUsedSlots(int playerIndex);          // сумма SlotCost
internal bool TryAddItem(int playerIndex, byte itemId);  // false при нехватке слот-очков
internal bool TryRemoveItemAt(int playerIndex, int slot, out byte itemId);
internal void SetInventoryForTest(int playerIndex, params byte[] items);

// HeroSimConfig
public int InventoryCapacity;    // 8 слот-очков
public int MaxInventoryItems;    // 16 — потолок массива
```

- **Каталог предметов ещё не существует** (он в Т13). До него `SlotCost`
  берётся из временного шва `Inventory.SlotCostOf(byte itemId) => 1` с
  комментарием `// TEMPORARY (T4 -> T13): the real cost comes from ItemCatalog`.
  В Т13 шов заменяется чтением каталога, а тесты Т4 переписываются под каталог
  **в том же таске** — это записанный долг, а не забытый.
- Рюкзак **не в `PlayerState`** (Р232): структура копируется целиком в
  `ReconcileData`, массив внутри сломал бы «ноль аллокаций».

- [ ] **Step 1 (RED):** `InventoryTests.cs`:

```csharp
[Test] public void EmptyInventory_HasZeroUsedSlots();
[Test] public void AddingItems_AccumulatesUsedSlots();
[Test] public void AddBeyondCapacity_Refused_AndInventoryUnchanged();
[Test] public void RemoveAt_ReturnsItem_AndFreesSlots();
[Test] public void RemoveFromEmptySlot_ReturnsFalse();
[Test] public void InventoriesOfPlayers_DoNotMix();   // подопытный — игрок 1
```

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN):** реализация + `WorldSave`.
- [ ] **Step 4:** R-FILTER `InventoryTests`+`WorldLifecycleTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т4 — рюкзак в слот-очках`.

### Task Т5: friendly fire мобов и `OwnerEntityId` снаряда

**Files:**
- Modify: `.../Core/SimStates.cs` (`ProjectileState.OwnerEntityId`),
  `.../Core/SimulationWorld.cs` (`SpawnProjectile`), `.../AI/MobAiSystem.cs`,
  `.../Combat/ProjectileSystem.cs`, `.../Combat/WeaponSystem.cs`,
  `client/Assets/Tests/EditMode/ProjectileTests.cs`
- Create: `client/Assets/Tests/EditMode/MobFriendlyFireTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
public struct ProjectileState { … public int OwnerEntityId; }  // 0 = ничей

internal int SpawnProjectile(ProjectileOwner owner, byte ownerIndex, int ownerEntityId,
    float2 pos, float2 vel, float height, float velZ, float damage, float radius, float ttl);
```

- **Гейт снимается:** в `ProjectileSystem` цикл сбора мобов (`:82`) перестаёт
  быть под `if (proj.Owner == ProjectileOwner.Player)`; исключается моб, чей
  `Id == proj.OwnerEntityId`.
- **Скретч не растёт:** `MaxMobs + MaxPlayers + 3` уже покрывает союз.
- **Зачёт не трогается:** мобий снаряд несёт `OwnerIndex = NoOwner`, гварды
  `IncrementShotsHit`/`IncrementKills` уже гасят начисление; появляется
  `MobDied` с `PlayerIndex = NoOwner` — потребители обязаны это выдержать.

- [ ] **Step 1 (RED):** `MobFriendlyFireTests.cs`:

```csharp
[Test]
public void GunnerRound_DamagesAnotherMob()
{
    var cfg = TestConfigs.Open();
    var w = new SimulationWorld(1, cfg, playerCount: 1);
    int shooter = w.SpawnMobForTest(MobType.Gunner, new float2(-5f, 0f));
    int victim  = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));   // ВТОРОЙ элемент (227)
    float hpBefore = w.MobHpForTest(victim);
    w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(-4f, 0f), new float2(40f, 0f),
        height: 1f, velZ: 0f, damage: 8f, radius: 0.1f, ttl: 1f,
        ownerIndex: ProjectileIds.NoOwner, ownerEntityId: shooter);
    for (int t = 0; t < 10; t++) w.Tick(default);
    Assert.Less(w.MobHpForTest(victim), hpBefore);
}

[Test] public void MobRound_DoesNotDamageItsOwnShooter();
[Test] public void MobKilledByMob_CreditsNobody();          // Kills не выросли ни у кого
[Test] public void MobDiedEvent_FromFriendlyFire_HasNoOwnerIndex();
```

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN):** снять гейт, прокинуть
  `ownerEntityId` из `MobAiSystem` (`m.Id`) и из `WeaponSystem` (0).
- [ ] **Step 4 (мутация):** убрать исключение стрелка → тест
  `MobRound_DoesNotDamageItsOwnShooter` обязан покраснеть; вернуть.
- [ ] **Step 5:** R-FILTER `MobFriendlyFireTests`+`ProjectileTests`+
  `HitZoneTests`+`PvpDamageTests` → PASS; R-TEST. **Golden сдвинется** —
  это ожидаемо и фиксируется в Т6, здесь константы НЕ трогать (тест
  `DeterminismTests` временно красный, отмечается в bd note как ожидаемый).
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т5 — friendly fire мобов`.

### Task Т6: канонический порядок хеша, `WorldSave`, `RenderSnapshot` → **перепин golden №1**

**Files:**
- Modify: `.../Core/SimulationWorld.cs` (`StateHash`, `SaveState`,
  `RestoreState`, `CaptureSnapshot`), `.../Core/WorldSave.cs`,
  `.../Core/RenderSnapshot.cs`,
  `client/Assets/Tests/EditMode/DeterminismTests.cs`,
  `client/Assets/Tests/EditMode/WorldLifecycleTests.cs`

**Interfaces:** канонический порядок (спека Р294):

```
tick → spreadRng → waveRng → lootRng → nextEntityId → playerCount
→ players[0..n) → mobCount+mobs → projectileCount+projectiles
→ pickupCount+pickups → containerCount+containers+containerSlots
→ wave → matchState → worldStats → stats[0..n) → inventories[0..n)
```

- **`_lootRng` заводится здесь** (Р230): `new Random(Fold(folded ^ 0x1B56C4E9u))`,
  рядом с двумя существующими потоками; потребитель придёт в Т15.
- **Контейнеры в хеше — с нулевым счётчиком** (их тип появится в Т14): позиция
  в порядке занимается сразу, чтобы перепина №3 не потребовалось. Пока
  `containerCount == 0`, шаг добавляет только константу нуля.
- `WorldLifecycleTests`: снять временные skip-list'ы, если заводились; ветка
  `int` в `Bump` уже есть, `ushort` не появляется (Р258).

- [ ] **Step 1:** внести все новые поля в `StateHash`/`SaveState`/`RestoreState`/
  `CaptureSnapshot`/`RenderSnapshot.CopyFrom` в каноническом порядке.
- [ ] **Step 2:** R-FILTER `WorldLifecycleTests` → рефлексивный свип обязан
  пройти на ВСЕХ новых полях (если поле забыто — свип красный и это находка).
- [ ] **Step 3 (доказательство живости):** временно вынуть `Ammo` из хеша →
  `WorldLifecycleTests` обязан покраснеть с именем поля; вернуть. Повторить для
  `MatchState.Phase` и для `inventories`. Записать в отчёт.
- [ ] **Step 4 (R-GOLDEN, санкция №1):** R-FILTER `DeterminismTests` → взять
  оба `But was:` → вписать обе hex-константы, обновить десятичные дубли и
  однострочное обоснование:
  `// T6 (Stage 3 re-pin #1, sanctioned): ammo, pickups, inventories, match
  state, projectile owner entity id entered the canonical hash order.`
- [ ] **Step 5:** R-FILTER `DeterminismTests` → PASS; R-TEST → **987 + новые**,
  `total` ГЛАЗАМИ.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т6 — состав состояния в хеше,
  перепин golden №1` — **отдельным коммитом**, ничего кроме перепина.

**Гейт фазы Ф1:** R-TEST полный зелёный; **ровно один** перепин обеих констант,
обоснование в `DeterminismTests`; R-BUILD-`LinuxServer` (релизная цель
обязательна — Р191) EXIT=0; ГЕЙТ-КОДОГЕН пуст; два фазовых ревьюера; push;
jsonl-chore; `bd close` сабтаска Ф1 + `bd export`.
**Стоп-условие:** если golden сдвинулся раньше Т6 — стоп и разбор, а не перепин
по дороге.

---

## Фаза Ф2 — арена трёх зон и её обитатели (Т7–Т12) → перепин golden №2

### Task Т7: примитив дуги в `Geometry` (opus)

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/Geometry.cs`
- Create: `client/Assets/Tests/EditMode/ZoneGeometryTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
// Дуга = кольцо радиуса R полуширины halfW с угловыми вырезами-дверями.
// Дверь j задана центром doorCenter[j] (радианы) и СВОБОДНОЙ шириной
// doorFreeWidth[j] (метры). Косяки — круги радиуса halfW в углах выреза.

public static bool OverlapsArc(float2 p, float radius, float ringR, float halfW,
    System.ReadOnlySpan<float> doorCenter, System.ReadOnlySpan<float> doorFreeWidth);

public static bool SegmentArc(float2 p0, float2 p1, float padR, float ringR, float halfW,
    System.ReadOnlySpan<float> doorCenter, System.ReadOnlySpan<float> doorFreeWidth,
    out float t, out float2 normal);

public static bool PushOutOfArc(ref float2 pos, float radius, float ringR, float halfW,
    System.ReadOnlySpan<float> doorCenter, System.ReadOnlySpan<float> doorFreeWidth,
    out float2 normal);
```

- **Арифметика тела кольца — через существующую `SegmentCircleInterval`**
  (спека §3.2, находка B-I2): интервал против внешней окружности `R + halfW`
  МИНУС интервал против внутренней `R − halfW`. Это даёт все четыре пересечения
  и работает одинаково для свипа и для луча LoS.
  ⚠ **`SegmentCircle` в лоб звать нельзя** — у неё ветка «старт внутри →
  `t = 0`» (`Geometry.cs:26`), и для тела внутри кольца она вернула бы контакт
  в его собственной точке.
- **Косяк — круг** (спека Р246): центры косяков — на радиусе `R`, по краям
  выреза; полный угловой вырез = `(doorFreeWidth + 2·halfW) / R`.
- **Депенетрация** — выбор ближней грани и затем существующие
  `PushOutOfCircle(ref pos, radius, float2.zero, R + halfW, out n)` наружу либо
  `ClampInsideRing(ref pos, radius, R − halfW, out n)` внутрь; нормаль —
  существующая `RingWallNormal(contact)` и её отрицание.
- **Ни одной новой формулы расстояния** — только композиция существующих.

- [ ] **Step 1 (RED):** `ZoneGeometryTests.cs` — двенадцать тестов:

```csharp
[Test] public void ApproachFromOutside_HitsOuterFace();
[Test] public void ApproachFromInside_HitsInnerFace();
[Test] public void ThroughDoor_NoContact();
[Test] public void TangentialDriftIntoJamb_HitsJambCircle();   // дыра Р246
[Test] public void JambNormal_PointsAwayFromJambCentre();
[Test] public void PushOutOfArc_FromInsideBody_ChoosesNearerFace();
[Test] public void PushOutOfArc_NeverCrossesToTheOtherSide();  // ключевой инвариант
[Test] public void OverlapsArc_TrueInsideBody_FalseInDoor();
[Test] public void SegmentArc_RayThroughDoor_IsNotBlocked();
[Test] public void SegmentArc_RayThroughBody_IsBlocked();
[Test] public void ZeroDoors_BehavesAsFullRing();
[Test] public void BodyWiderThanDoor_AlwaysContacts();
```

  Все фикстуры — явные числа в теле теста (`ringR = 10f`, `halfW = 1f`, дверь
  шириной 4 м в центре `0f`, тело радиуса `0.5f`).

- [ ] **Step 2:** заглушки (`out t = 0; out normal = default; return false;`) →
  R-FILTER `ZoneGeometryTests` → **FAIL ассертов**.
- [ ] **Step 3 (GREEN):** реализация композицией существующих функций.
- [ ] **Step 4 (мутация, обязательная):** убрать проверку косяков → тест
  `TangentialDriftIntoJamb_HitsJambCircle` обязан покраснеть; вернуть. Затем
  убрать угловой тест двери → `ThroughDoor_NoContact` обязан покраснеть;
  вернуть. Обе мутации записать в отчёт таска.
- [ ] **Step 5:** R-FILTER `ZoneGeometryTests`+`GeometryTests`+
  `WallGeometryTests`+`DeterminismTests` → PASS, **golden на месте** (дуг в
  `TestConfigs` ещё нет — `ZoneWallCount == 0`); R-TEST.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т7 — дуговой барьер с дверями и косяками`.

### Task Т8: зоны и порталы в `ArenaSimConfig`, `ZoneOf`, валидация

**Files:**
- Modify: `.../Core/SimConfig.cs`, `.../Core/Geometry.cs` (`ZoneOf`),
  `client/Assets/Scripts/Data/ArenaConfig.cs`, `.../Data/SimConfigBuilder.cs`,
  `client/Assets/Tests/EditMode/TestConfigs.cs`,
  `client/Assets/Tests/EditMode/ConfigTests.cs`
- Create: `client/Assets/Tests/EditMode/ZoneConfigTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
public enum Zone : byte { Outer = 0, Middle = 1, Core = 2 }

// ArenaSimConfig
public float[] ZoneRadius;          // {65, 92}: две границы, три зоны
public int ZoneWallCount;
public float[] ZoneWallRadius;
public float[] ZoneWallHalfWidth;
public int[] ZoneWallDoorStart;     // индекс первой двери стены в общих массивах
public int[] ZoneWallDoorCount;
public float[] DoorCenterRad;
public float[] DoorFreeWidth;
public float2[] ExtractPos;
public byte[] ExtractZone;          // Zone
public byte[] ExtractKind;          // 0 = Portal, 1 = Gate
public float ExtractRadius;         // 8
public int MaxPickups, MaxContainers, MaxContainerSlots;

// Geometry
public static Zone ZoneOf(float2 pos, in ArenaSimConfig arena);
```

- Массивы **пустые, никогда не `null`** — конвенция `WallA`/`WallB`.
- `ZoneWallCount == 0` даёт арену Этапа 2 буквально.
- **Валидация** (`SimConfigBuilder.Validate`): радиусы зон строго возрастают и
  меньше `Radius`; у каждой стены ≥ одной двери; двери не перекрываются;
  `DoorFreeWidth ≥ 2·(bodyRadius + Skin) + Clearance`, где `bodyRadius` —
  максимум по Hero/Chaser/Gunner/Elite/**Director** (Р247);
  `ExtractRadius > Hero.Radius`; ни один портал не лежит в теле дуги;
  `MaxContainerSlots ≥ InventoryCapacity / minSlotCost` (Р263).
- **`[Range]` расширяются** (Р284): `ArenaConfig.Radius` до 150, `MaxMobs` до
  400, `MaxProjectiles`/`MaxEventsPerFrame` до 2000; правится док `Quantize`,
  который ссылается на `[Range(5, 100)]`.

- [ ] **Step 1 (RED):** `ZoneConfigTests.cs`:

```csharp
[Test] public void ZoneOf_ReturnsCore_InsideFirstRadius();
[Test] public void ZoneOf_ReturnsMiddle_BetweenRadii();
[Test] public void ZoneOf_ReturnsOuter_BeyondSecondRadius();
[Test] public void ZoneOf_OnExactBoundary_BelongsToInnerZone();
[Test] public void Validate_RejectsNonIncreasingZoneRadii();
[Test] public void Validate_RejectsWallWithoutDoor();
[Test] public void Validate_RejectsDoorNarrowerThanBiggestBody();
[Test] public void Validate_RejectsPortalInsideArcBody();
[Test] public void Validate_RejectsContainerSlotsBelowInventoryCapacity();
```

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-FILTER `ZoneConfigTests`+`ConfigTests` → PASS; R-TEST,
  golden на месте (`TestConfigs` ещё без зон).
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т8 — зоны, двери и порталы в конфиге`.

### Task Т9: шесть потребителей дуг

**Files:**
- Modify: `.../Core/Geometry.cs` (`SweepArena`, `Depenetrate`),
  `.../AI/MobAiSystem.cs` (`SteerAround`), `.../AI/WaveSystem.cs`
  (`IsValidSpawn`), `.../AI/Targeting.cs` (`HasLineOfFire`),
  `.../Core/SimulationWorld.cs` (`ArenaTopologyMatches`),
  `client/Assets/Scripts/Presentation/GreyboxBuilder.cs` — **в Т30**, здесь НЕ
  трогаем
- Modify tests: `ZoneGeometryTests.cs`, `MobAiTests.cs`, `WaveScalingTests.cs`,
  `HotTweakTests.cs`

**Interfaces:** порядок контактов в `SweepArena` фиксируется тестом:
**круги → стадионы → дуги → кольцевая стена**. `Depenetrate` — тем же порядком.

- **`SteerAround`:** ветка дуги правит на **путевую точку в ближайшей двери**
  (по образцу Р118 — не касательная, иначе мёртвый упор), выбор двери — по
  суммарной длине обхода с тай-брейком по чётности `Id`.
- **`HasLineOfFire`:** цикл по дугам с клампом отступа
  `padR' = max(padR, −halfW)` (Р64).
- **`ArenaTopologyMatches`:** сравниваются массивы дуг и дверей, а также
  `MaxPickups`/`MaxContainers`/`MaxContainerSlots` (Р287).

- [ ] **Step 1 (RED):** дописать в `ZoneGeometryTests`:
  `SweepArena_ContactOrder_ArcAfterStadiumBeforeRing`,
  `Depenetrate_OutOfArc_Terminates`;
  в `MobAiTests`: `Chaser_FindsDoor_InsteadOfPressingIntoArc`;
  в `WaveScalingTests`: `SpawnCandidateInsideArc_IsRejected`;
  в `HotTweakTests`: `ArcTopologyChange_ThrowsOnApplyConfig`,
  `PickupCapChange_ThrowsOnApplyConfig`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)** — шесть правок.
- [ ] **Step 4 (мутация):** снять цикл по дугам в `IsValidSpawn` → тест
  `SpawnCandidateInsideArc_IsRejected` красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS, golden на месте; ГЕЙТ-КОДОГЕН.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т9 — дуги во всех потребителях геометрии`.

### Task Т10: элита и Директор — четыре архетипа

**Files:**
- Modify: `.../Core/SimStates.cs` (`MobType`), `.../Core/SimConfig.cs`
  (`SimConfig.Elite`, `SimConfig.Director`), `.../Core/SimulationWorld.cs`
  (`MobConfigFor`, стартовый `Hp` в `SpawnMob`), `.../Combat/ProjectileSystem.cs`
  (радиус цели), `.../AI/MobAiSystem.cs` (развилка FSM),
  `client/Assets/Scripts/Networking/Protocol/SnapshotBlocks.cs`
  (`MaxHpFor`, `MaxMobTypeValue`), `.../Protocol/ProtocolVersion.cs`
- Modify tests: `SnapshotCodecTests.cs` (tripwire
  `EnumDomainBounds_MatchTheSimulationEnums` — **обязан покраснеть и правится
  осознанно**), `MobAiTests.cs`
- Create: `client/Assets/Tests/EditMode/EliteAndDirectorTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
public enum MobType : byte { Chaser = 0, Gunner = 1, Elite = 2, Director = 3 }

// SimulationWorld.MobConfigFor — switch по четырём, НЕ тернар
internal MobSimConfig MobConfigFor(MobType type) => type switch
{
    MobType.Chaser   => _config.Chaser,
    MobType.Gunner   => _config.Gunner,
    MobType.Elite    => _config.Elite,
    MobType.Director => _config.Director,
    _ => throw new System.ArgumentOutOfRangeException(nameof(type), type, "unknown archetype"),
};

// SnapshotBlocks — тем же switch
public static float MaxHpFor(MobType type, in SimConfig cfg);
public const byte MaxMobTypeValue = (byte)MobType.Director;
// ProtocolVersion.Current: 2 -> 3 (основание — рост домена MobType, Р282)
```

- **Элита ходит по существующей FSM** — новых `MobAiState` не появляется,
  `MaxMobAiStateValue` не двигается (Р214). У элиты задействованы все шесть
  состояний: `Chase`/`Telegraph`/`Recover` от чейзера и
  `Reposition`/`Fire` от ганнера, переключение по дистанции.
- **Свита — не тип**: это `Elite`, спавнящаяся Директором (Т22).

- [ ] **Step 1 (RED):** `EliteAndDirectorTests.cs`:

```csharp
[Test] public void MobConfigFor_ReturnsOwnConfig_ForEachOfFourArchetypes();
[Test] public void MobConfigFor_UnknownArchetype_Throws();
[Test] public void SpawnedElite_StartsWithEliteMaxHp();       // подопытный — Elite, не Chaser
[Test] public void SpawnedDirector_StartsWithDirectorMaxHp();
[Test] public void EliteUsesAllSixAiStates_OverDistanceSweep();
[Test] public void ProjectileGather_UsesArchetypeRadius_ForElite();
```

  плюс в `SnapshotCodecTests`: `MaxHpFor_DecodesAgainstOwnArchetypeCap`,
  `EnumDomainBounds_MatchTheSimulationEnums` обновляется на `Director` **с
  комментарием, называющим причину**.

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**; `ProtocolVersion.Current`
  → 3 + строка в журнале `HISTORY` файла.
- [ ] **Step 4 (мутация):** в `SpawnMob` вернуть тернар `Chaser ? … : Gunner`
  → `SpawnedElite_StartsWithEliteMaxHp` красный; вернуть switch.
- [ ] **Step 5:** R-TEST → PASS; ГЕЙТ-КОДОГЕН пуст.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т10 — элита и Директор, ProtocolVersion 3`.

### Task Т11: зонный бюджет волн и матрица долга

**Files:**
- Modify: `.../Core/SimStates.cs` (`WaveState` — матрица долга),
  `.../Core/SimulationWorld.cs` (`HashWave`), `.../AI/WaveSystem.cs`,
  `.../Core/SimConfig.cs` (`WaveSimConfig`),
  `client/Assets/Scripts/Data/WaveConfig.cs`,
  `client/Assets/Tests/EditMode/WaveScalingTests.cs`, `WaveTests.cs`
- Create: `client/Assets/Tests/EditMode/WaveZoneTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
public struct WaveState
{
    public WavePhase Phase;
    public int WaveIndex, AliveCount;
    public float PhaseTimer;
    // 3 зоны × 3 архетипа волны (Chaser, Gunner, Elite) — плоский массив 9
    public unsafe fixed int Pending[9];   // либо int p0..p8, если fixed нежелателен
}

// WaveSimConfig
public float[] ZoneWeights;            // {0.45, 0.45, 0.10}
public float EliteShareMiddle;         // 0.35
public float EliteShareOuterGrowth;    // 0.02 за волну, потолок 0.25

// WaveSystem — существующая CountForTest НЕ меняется
internal static void SplitByZones(int total, System.ReadOnlySpan<float> weights,
    System.Span<int> perZone);   // наибольшие остатки, сумма == total
```

- ⚠ `fixed`-массив требует `unsafe`; если проект его не разрешает — девять
  именованных полей `PendingOuterChaser … PendingCoreElite`. Исполнитель
  выбирает по факту настроек компиляции и записывает выбор в отчёт таска.
- **Спавн-кольцо зоны** — `внешняя граница зоны − SpawnRingInset` (Р249).
- **Доли внутри зоны** суммируются в единицу: сначала отделяется элита, остаток
  делится существующей формулой `GunnerShare`.

- [ ] **Step 1 (RED):** `WaveZoneTests.cs`:

```csharp
[Test] public void SplitByZones_SumEqualsTotal_ForEveryTotalFromOneToFifty();
[Test] public void SplitByZones_IsDeterministic_ForEqualRemainders();
[Test] public void OuterZone_GetsNoElite_OnFirstWave();
[Test] public void OuterZone_EliteShareGrows_WithWaveIndex_UpToCap();
[Test] public void MiddleZone_MixSumsToOne();
[Test] public void CoreZone_SpawnsOnlyElite();
[Test] public void SpawnRing_OfZone_IsInsideThatZone();
[Test] public void Debt_IsNeverLostOnRounding();
```

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**; `HashWave` расширяется
  матрицей долга.
- [ ] **Step 4 (мутация):** заменить наибольшие остатки на простое усечение →
  `SplitByZones_SumEqualsTotal_...` красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS. Golden **сдвинется** (форма `WaveState`) —
  константы здесь НЕ трогать, перепин в Т12.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т11 — зонный бюджет волн`.

### Task Т12: доставка чисел в `.asset`, `TestConfigs`, раскладка → **перепин golden №2**

**Files:**
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
  (одноразовый apply-блок Этапа 3), `client/Assets/Data/ArenaConfig.asset`,
  `WaveConfig.asset`, `WeaponConfig.asset`, `HeroConfig.asset`,
  `NetConfig.asset`, `GameFeelConfig.asset` (через бутстрап, руками — нет),
  `client/Assets/Tests/EditMode/TestConfigs.cs`,
  `client/Assets/Tests/EditMode/DeterminismTests.cs`
- Create: `client/Assets/Data/MobEliteConfig.asset`,
  `client/Assets/Data/MobDirectorConfig.asset` (через бутстрап)

**Interfaces:** санкционированные правки существующих чисел (спека §3.13):

| Ассет | Правка |
|---|---|
| `ArenaConfig` | `Radius 65 → 113`, `PlayerSpawnRingFrac 0.8 → 0.92`, `MaxMobs 96 → 288`, `MaxProjectiles 384 → 1024`, `MaxEventsPerFrame 512 → 1024` |
| `WaveConfig` | `MaxMobsPerWave 36 → 72` |
| `NetConfig` | `MatchMaxDurationSeconds 1800 → 900` |

- ⚠ **Гейт `stageTwoPending` закрыт навсегда** (Р120) — правки существующих
  ключей через него **не доедут**. Заводится **новый** одноразовый блок
  `ApplyStageThreeBalance`, гейтированный признаком по **тексту ассета**:
  `bool stageThreePending = !File.ReadAllText($"{DataDir}/ArenaConfig.asset").Contains("ZoneRadius:");`
- **Маркер-ключи переезжают** (Р285) на новые последние поля
  `ArenaConfig`/`HeroConfig`/`WeaponConfig`/`WaveConfig`; синхронно правятся
  call-site'ы `EnsureAssetHasKey` в `StageOneSceneBootstrap`.
- `TestConfigs.DefaultArena()` получает зоны и дуги; `TestConfigs.Open()`
  **зануляет и дуги тоже** (иначе «открытая арена» перестанет быть открытой).

- [ ] **Step 1:** написать `ApplyStageThreeBalance` + фабрики двух новых
  `MobConfig`-ассетов по образцу `ApplyGunnerZoneDefaults`.
- [ ] **Step 2:** R-APPLY-`StageOneSceneBootstrap` → EXIT=0; `git diff --
  client/Assets/Data/` показывает **ровно** санкционированный список.
- [ ] **Step 3:** R-IDEM (после коммита артефактов) → пусто.
- [ ] **Step 4:** обновить `TestConfigs` (зоны, дуги, капы, два новых
  `MobSimConfig`) → R-FILTER `ConfigTests` → PASS.
- [ ] **Step 5 (R-GOLDEN, санкция №2):** оба `But was:` → обе константы +
  десятичные дубли + обоснование:
  `// T12 (Stage 3 re-pin #2, sanctioned): arena radius 113, three zones with
  arcs and doors, spawn ring 0.92, world caps, zonal wave budget, elite and
  director archetypes.`
- [ ] **Step 6:** R-TEST → PASS, `total` ГЛАЗАМИ.
- [ ] **Step 7:** R-COMMIT `feat(app-35g): Т12 — арена трёх зон в данных,
  перепин golden №2`.

**Гейт фазы Ф2:** R-TEST зелёный; **ровно один** перепин обеих констант;
R-BUILD-`LinuxServer` + R-BUILD-`WindowsClient` EXIT=0; ГЕЙТ-КОДОГЕН пуст;
R-IDEM сошёлся; два фазовых ревьюера; push; jsonl-chore; `bd close` + `bd export`.

---

## Фаза Ф3 — предметы и контейнеры (Т13–Т16)

С этой фазы и до Ф8 **golden не двигается**: `TestConfigs` держит
`CrateCount 0`, нулевые шансы предметного дропа и не заводит Директора, поэтому
эволюция golden-сценария не меняется. Любой сдвиг — стоп.

### Task Т13: каталог предметов и `LootConfig`

**Files:**
- Create: `client/Assets/Scripts/Data/ItemCatalog.cs` (+ `.meta`),
  `client/Assets/Scripts/Data/LootConfig.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/ItemCatalogTests.cs` (+ `.meta`)
- Modify: `.../Core/SimConfig.cs`, `.../Data/SimConfigBuilder.cs`,
  `.../Simulation/Loot/Inventory.cs` (снять временный `SlotCostOf`),
  **все дома перечисления SO** (Р283, пересчитать перед началом):
  `SimConfigBuilder.Build`, `ServerBootstrap`, `SimulationRunner` (×2 call-site),
  `StageOneSceneBootstrap`, `ClientNetworkBootstrap`, `LongRunHarness`,
  `ConfigTests`, `StageTwoSceneBootstrap`
- Modify tests: `InventoryTests.cs` (переписать под каталог), `ConfigTests.cs`

**Interfaces:**

```csharp
public enum ItemKind : byte { Trophy = 0, RepairKit = 1 }

public struct ItemDef
{
    public byte Id;
    public byte Tier;         // 1..4; 0 = ремкомплект (вне тиров)
    public byte SlotCost;
    public ushort CreditValue;
    public ItemKind Kind;
}

// SimConfig
public ItemDef[] Items;       // копия каталога, ≤ 255 записей

// LootSimConfig
public float[] DropChance;          // [архетип, зона] → шанс, плоский 4×3
public int CrateCount, CacheCountMiddle, CacheCountCore;
public float RepairKitChance;       // 0.25
public int[] CellsPerMob;           // {1, 1, 4, 20} — индекс MobType
public float CorpseCellFraction;    // 0.5
public float RepairKitHealAmount;   // 40
public float RepairKitChannelSeconds; // 2
public float[] TransferSeconds;     // по тирам {0.3, 0.6, 0.9, 1.2}
public int LootSpawnAttempts, LootFallbackSlots;   // 16 / 24
public float PickupTtlSeconds, ContainerTtlSeconds; // 120 / 180
```

- **Каталог — топология** (Р264): входит в `ArenaTopologyMatches`, смена
  требует рестарта мира.
- Шесть стартовых записей: Т1 (1 слот, 15 кр), Т2 (2, 60), Т3 (3, 200),
  Т4 ядро памяти (4, 1000), ремкомплект (1, 0), плюс резерв.

- [ ] **Step 1 (RED):** `ItemCatalogTests.cs` — `CatalogIsCopiedIntoSimConfig`,
  `SlotCostComesFromCatalog_NotFromStub`, `Validate_RejectsDuplicateItemId`,
  `Validate_RejectsZeroSlotCost`, `CatalogChange_ThrowsOnApplyConfig`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**; **снять временный
  `SlotCostOf`** из Т4 и переписать `InventoryTests` под реальные стоимости.
- [ ] **Step 4:** R-APPLY + R-IDEM для двух новых ассетов.
- [ ] **Step 5:** R-TEST → PASS, golden на месте.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т13 — каталог предметов и настройки лута`.

### Task Т14: контейнер как единый тип

**Files:**
- Create: `client/Assets/Scripts/Simulation/Loot/ContainerStore.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/LootContainerTests.cs` (+ `.meta`)
- Modify: `.../Core/SimStates.cs`, `.../Core/SimulationWorld.cs`,
  `.../Core/WorldSave.cs`, `.../Core/RenderSnapshot.cs`

**Interfaces:**

```csharp
public enum ContainerKind : byte
{ Ground = 0, Crate = 1, Cache = 2, MobCorpse = 3, PlayerCorpse = 4 }

public struct ContainerState
{
    public int Id;
    public float2 Pos;
    public ContainerKind Kind;
    public byte SlotCount;
    public float Ttl;          // 0 = не истекает (ящик, тайник, труп сборщика)
}

// SimulationWorld
internal int SpawnContainer(ContainerKind kind, float2 pos, System.ReadOnlySpan<byte> items);
internal void RemoveContainerAt(int index);      // swap-remove ПЕРЕНОСИТ и блок слотов
internal bool TryTakeFromContainer(int containerId, int slot, out byte itemId);
internal byte ContainerSlotAt(int containerIndex, int slot);   // 0 = пусто
```

- **Содержимое** — плоский массив `byte[MaxContainers * MaxContainerSlots]`,
  адресуемый **позицией контейнера в массиве**; swap-remove обязан переносить и
  блок слотов (Р229 — иначе исчезнувший ящик оставит содержимое новому жильцу
  индекса).
- `WorldStats.ContainerSpawnsSkipped` — по образцу `MobSpawnsSkipped`.

- [ ] **Step 1 (RED):** `LootContainerTests.cs`:

```csharp
[Test] public void SpawnedContainer_HoldsGivenItems();
[Test] public void SwapRemove_DoesNotTransferSlotsToNeighbour();  // ВТОРОЙ контейнер
[Test] public void TakeFromEmptySlot_ReturnsFalse();
[Test] public void CapReached_SkipsAndCounts();
[Test] public void TtlZero_NeverExpires_ForCrateAndPlayerCorpse();
[Test] public void TtlExpiry_RemovesGroundContainer();
```

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4 (мутация):** убрать перенос блока слотов в `RemoveContainerAt` →
  `SwapRemove_DoesNotTransferSlotsToNeighbour` красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS, golden на месте (контейнеров в `TestConfigs`
  нет, счётчик в хеше уже стоит с Т6 и равен нулю).
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т14 — контейнер как единый тип`.

### Task Т15: расстановка контейнеров от `_lootRng`

**Files:** Modify `.../Simulation/Loot/ContainerStore.cs`,
`.../Core/SimulationWorld.cs` (конструктор), `LootContainerTests.cs`.

**Interfaces:** `internal static void PlaceStartingContainers(SimulationWorld w)`
— зовётся конструктором **после** депенетрации игроков.

- **Завершаемость по образцу волн** (Р262): до `LootSpawnAttempts` кандидатов
  из `_lootRng`, отбраковка по перекрытию с кругом, стадионом, дугой, дверью и
  другим контейнером; затем **RNG-free** сетка `LootFallbackSlots` фиксированных
  углов; не нашлось — счётчик `ContainerSpawnsSkipped`.
- Зона задаёт и число, и тир содержимого.

- [ ] **Step 1 (RED):** `SameSeed_SamePlacement`,
  `ChangingCrateCount_DoesNotMoveWaveSpawns` (**доказательство смысла третьего
  потока**), `BlockedArena_TerminatesAndCounts`, `NoContainerInsideArcOrDoor`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-TEST → PASS; golden на месте (`CrateCount 0` в `TestConfigs`).
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т15 — расстановка контейнеров от seed`.

### Task Т16: дроп предметов и трупы как контейнеры

**Files:** Modify `.../Core/SimulationWorld.cs` (`DamageMob`, `KillPlayer`),
`.../Simulation/Loot/ContainerStore.cs`, `LootContainerTests.cs`,
`PickupTests.cs`.

**Interfaces:** таблица дропа читается как `LootConfig.DropChance[archetype, zone]`;
тир предмета — **тир зоны смерти** (Р228/Р215).

- Директор роняет: 20 ячеек рассыпом, **три** контейнера Т3 по 1–2 предмета и
  **отдельный** контейнер с ядром памяти Т4.
- Труп сборщика — контейнер с **полным** рюкзаком (`Ttl = 0`) плюс рассып ячеек.

- [ ] **Step 1 (RED):** `EliteInMiddle_DropsTierTwo`,
  `EliteInCore_DropsTierThree` (свита без своего признака — Р215),
  `Director_DropsExactlyOneMemoryCore`, `PlayerCorpse_HoldsWholeInventory`,
  `MobCorpse_AppearsOnlyWhenItemDropped`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4 (мутация):** заменить «тир от зоны» на «тир от архетипа» →
  `EliteInCore_DropsTierThree` красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS, golden на месте.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т16 — дроп предметов и трупы-контейнеры`.

**Гейт фазы Ф3:** R-TEST зелёный, **golden неподвижны**; R-BUILD-`LinuxServer`;
два фазовых ревьюера; push; jsonl-chore; `bd close` + `bd export`.

---

## Фаза Ф4 — лутание (Т17–Т20)

### Task Т17: ядро лутания — три операции и девять проверок (opus)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Loot/LootOps.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/LootOpsTests.cs` (+ `.meta`)
- Modify: `.../Core/SimStates.cs` (`PlayerState.LootTimer`,
  `LootTargetContainerId`, `LootTargetSlot`, `InventoryOpen`),
  `.../Core/SimulationWorld.cs`

**Interfaces:**

```csharp
public enum LootOp : byte { Take = 0, Drop = 1, Use = 2 }

public enum LootRefusal : byte
{
    None = 0, DeadOrExtracted = 1, WindowClosed = 2, UnknownOp = 3,
    NoSuchContainer = 4, SlotOutOfRange = 5, SlotEmpty = 6,
    InventoryIndexOutOfRange = 7, TooFar = 8, NotEnoughSlots = 9, Busy = 10,
    Dashing = 11,
}

public static class LootOps
{
    /// Единственный дом всех девяти проверок. Чистая функция: не мутирует мир,
    /// только отвечает, законна ли операция ПРЯМО СЕЙЧАС.
    public static LootRefusal Validate(SimulationWorld w, int playerIndex,
        LootOp op, int containerId, int slot, in SimInput input);

    /// Начинает операцию: ставит LootTimer и цель. Предполагает Validate == None.
    public static void Begin(SimulationWorld w, int playerIndex,
        LootOp op, int containerId, int slot);

    /// Тик: досчитывает таймеры и завершает операции ПОВТОРНОЙ проверкой.
    public static void Update(SimulationWorld w);
}
```

- **Девять проверок** в порядке §3.8: жив и не извлечён → флаг окна поднят в
  инпуте → `Op` известен → контейнер существует и слот непуст → `slot` в
  `[0, SlotCount)` → `inventoryIndex` в диапазоне и принадлежит игроку → дистанция
  `≤ LootRadius` → хватает слот-очков → перенос не идёт.
  Для `Use` дополнительно: **не в дэше И не в слайде**.
- **Флаг окна — часть инпута** (Р239): `SimInput.InventoryOpen`; проверка №2
  делает цену лута серверной, а не «честноклиентской» (Р265 п.2).
- **`LootTargetContainerId` хранит `Id`, не индекс** (Р266).
- `LootOps.Update` зовётся в `TickAll` **после боя и до подбора ячеек**.

- [ ] **Step 1 (RED):** `LootOpsTests.cs` — по тесту на каждую из одиннадцати
  причин отказа плюс позитив:

```csharp
[Test] public void Validate_Ok_ForLegalTake();
[Test] public void Validate_RefusesDeadPlayer();
[Test] public void Validate_RefusesExtractedPlayer();
[Test] public void Validate_RefusesWhenWindowFlagIsDown();     // ключевой — Р265 п.2
[Test] public void Validate_RefusesUnknownOp();
[Test] public void Validate_RefusesMissingContainer();
[Test] public void Validate_RefusesSlotOutOfRange();           // slot = 255
[Test] public void Validate_RefusesEmptySlot();
[Test] public void Validate_RefusesInventoryIndexOutOfRange();
[Test] public void Validate_RefusesWhenTooFar();
[Test] public void Validate_RefusesWhenNotEnoughSlots();
[Test] public void Validate_RefusesWhenTransferAlreadyRunning();
[Test] public void Validate_RefusesUse_WhileSliding();
```

- [ ] **Step 2:** заглушки (`Validate => LootRefusal.None`) → R-FILTER
  `LootOpsTests` → **FAIL ассертов**.
- [ ] **Step 3 (GREEN):** реализация всех девяти в одном месте.
- [ ] **Step 4 (мутация):** снять проверку флага окна → тест
  `Validate_RefusesWhenWindowFlagIsDown` красный; вернуть. Затем снять
  проверку диапазона слота → соответствующий тест красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS, golden на месте.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т17 — серверные операции лута и отказы`.

### Task Т18: таймер переноса, завершение и гонка (opus)

**Files:** Modify `.../Simulation/Loot/LootOps.cs`, `LootOpsTests.cs`.

**Interfaces:** завершение в тике обнуления `LootTimer` — **повторная**
`Validate` по тем же девяти условиям; при отказе операция отменяется молча
(клиент увидит по снапшоту). Завершения нескольких игроков в одном тике —
**по возрастанию индекса игрока** (Р267).

- **Урон перенос НЕ прерывает** (в отличие от канала выхода) — это записанное
  решение спеки §3.8, и тест обязан его пиновать, иначе следующий ревьюер
  «исправит» асимметрию.
- Предмет **остаётся в контейнере** всё время переноса.

- [ ] **Step 1 (RED):**

```csharp
[Test] public void ItemStaysInContainer_UntilTransferCompletes();
[Test] public void TransferCompletes_AfterTierSpecificDelay();
[Test] public void TwoTakesOnSameSlot_LowerIndexWins_OtherGetsSlotEmpty();
[Test] public void TransferAborts_WhenPlayerWalksOutOfRange();
[Test] public void TransferAborts_WhenWindowCloses();
[Test] public void DamageDoesNotAbortTransfer();               // асимметрия с каналом
[Test] public void ContainerEmptiedMidTransfer_AbortsWithSlotEmpty();
```

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4 (мутация):** снять повторную проверку в тике завершения →
  `ContainerEmptiedMidTransfer_AbortsWithSlotEmpty` красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS. **Step 6:** R-COMMIT
  `feat(app-35g): Т18 — таймер переноса и послотовая гонка`.

### Task Т19: ремкомплект

**Files:** Modify `.../Simulation/Loot/LootOps.cs`, `.../Core/SimStates.cs`
(`PlayerState.RepairTimer`), `LootOpsTests.cs`.

**Interfaces:** `Use` на предмете `Kind == RepairKit` запускает канал
`RepairKitChannelSeconds`; **урон канал прерывает** (в отличие от переноса) —
по тому же правилу, что канал выхода, и обнуление вешается в `DamagePlayer`
после обоих гвардов. По завершении: `Hp = min(Hp + RepairKitHealAmount, MaxHp)`,
предмет удаляется.

- [ ] **Step 1 (RED):** `RepairKit_HealsAndIsConsumed`,
  `RepairKit_ChannelResetByDamage`, `RepairKit_AbsorbedHitDoesNotReset`
  (i-frames — симметрия с Р127), `RepairKit_DoesNotOverheal`,
  `RepairKit_RefusedWhileDashing`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**. **Step 4:** R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т19 — ремкомплект`.

### Task Т20: флаг окна в инпуте — замедление и запрет огня

**Files:** Modify `.../Core/SimInput.cs`, `.../Core/SimInputSanitizer.cs`,
`.../Movement/PlayerMovementSystem.cs`, `.../Combat/WeaponSystem.cs`
(`CanFire`), `client/Assets/Scripts/Networking/Protocol/InputCodec.cs`,
`client/Assets/Tests/EditMode/InputCodecTests.cs`,
`client/Assets/Tests/EditMode/PredictionParityTests.cs`.

**Interfaces:** `SimInput.InventoryOpen` (bool) → **свободный бит 4** байта
флагов `ReplicateData` (биты 0–3 заняты, 4–7 свободны — проверено). Размер
инпута **не растёт**, остаётся 8 Б.

- **Замедление** — существующим `Hero.AimMoveSpeedFrac`, второго числа не
  заводим (Р239).
- **`CanFire`** получает слагаемое `!p.InventoryOpen`… ⚠ флаг живёт в
  `SimInput`, а не в `PlayerState`, поэтому в `CanFire` он приходит через уже
  имеющийся параметр `in SimInput input`.
- **Санитизация** гасит флаг у мёртвого, извлечённого, в дэше и в слайде.

- [ ] **Step 1 (RED):** в `InputCodecTests` — `InventoryOpenFlag_RoundTrips`,
  `SizeBytes_IsStillEight`; в `PredictionParityTests` —
  `OpenWindow_SlowsMovement_IdenticallyInPrediction`; в `LootOpsTests` —
  `Sanitizer_ClearsWindowFlag_WhileDashing`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4 (мутация):** снять замедление только в `PlayerPrediction` →
  `PredictionParityTests` красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS; ГЕЙТ-КОДОГЕН пуст (менялась проводная
  структура `ReplicateData`).
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т20 — флаг окна инвентаря в инпуте`.

**Гейт фазы Ф4:** R-TEST зелёный, golden неподвижны; ГЕЙТ-КОДОГЕН;
R-BUILD-`LinuxServer`; два фазовых ревьюера; push; jsonl-chore; `bd close`.

---

## Фаза Ф5 — фаза захода, Директор и выход (Т21–Т24)

### Task Т21: машина фазы — активация входом в ядро (opus)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Objectives/MatchFlowSystem.cs`
  (+ `.meta`)
- Modify: `.../Core/SimulationWorld.cs` (`TickAll` — порядок), `MatchFlowTests.cs`

**Interfaces:**

```csharp
internal static class MatchFlowSystem
{
    /// Зовётся ПОСЛЕДНИМ шагом TickAll — после боя, после LootOps, после
    /// канала выхода и после подбора ячеек (Р256).
    public static void Update(SimulationWorld w);
}
```

- **Переход `Farm → DirectorActive`** — `ZoneOf(pos) == Zone.Core` у любого
  живого и не извлечённого игрока (Р299). Латч односторонний.
- **Переход `DirectorActive → GateOpen`** — Директора нет в `_mobs` **и**
  `CurrentTick − DirectorDeathTick ≥ GateDelaySeconds · TickRate`.
- **`Ended`** — от `MatchEndPolicy`, проверяется первым (Р256 п.2/п.3).
- События `DirectorActivated` и `DirectorDied` — **всем, без позиции**.

- [ ] **Step 1 (RED):** в `MatchFlowTests`:

```csharp
[Test] public void EnteringCore_ActivatesDirector();
[Test] public void StayingOutOfCore_NeverActivates_EvenAtMatchEnd();
[Test] public void ActivationIsIrreversible_AfterLeavingCore();
[Test] public void DeadPlayerInCore_DoesNotActivate();
[Test] public void ExtractedPlayerInCore_DoesNotActivate();
[Test] public void GateOpens_AfterDelayFromDirectorDeath();
[Test] public void GateNeverCloses_OnceOpen();
[Test] public void ExtractionCompletingOnActivationTick_Succeeds();  // Р256 п.1
[Test] public void EndedOutranksGateOpen_OnTheSameTick();            // Р256 п.3
[Test] public void ActivationEvent_GoesToEveryone_WithoutPosition();
```

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4 (мутация):** переставить `MatchFlowSystem.Update` перед каналом
  выхода → `ExtractionCompletingOnActivationTick_Succeeds` красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS, golden на месте (в `TestConfigs` ядро есть, но
  скриптованный golden-игрок в него не заходит — **проверить и записать**).
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т21 — фаза захода и активация входом в ядро`.

### Task Т22: Директор — спавн, свита, резерв слотов, leash

**Files:** Modify `.../Simulation/Objectives/MatchFlowSystem.cs`,
`.../AI/MobAiSystem.cs` (leash), `.../AI/WaveSystem.cs` (резерв слотов),
`EliteAndDirectorTests.cs`, `WaveZoneTests.cs`.

**Interfaces:**

```csharp
// MatchFlowConfig -> SimConfig.Flow
public struct MatchFlowSimConfig
{
    public float GateDelaySeconds;        // 90
    public float ExtractChannelSeconds;   // 20
    public int   RetinueCount;            // 2
    public float RetinueRespawnSeconds;   // 25
    public int   DirectorReserveSlots;    // 3 = 1 + RetinueCount
}
```

- **Резерв держится весь заход** (Р254): волновой спавн останавливается на
  `MaxMobs − DirectorReserveSlots`.
- **Leash** (Р248): цель Директора клампится радиусом ядра — при цели вне ядра
  он идёт к ближайшей точке границы, а не наружу.
- Добор свиты по `RetinueRespawnSeconds`, пока Директор жив; при упоре в кап —
  долг, повторяемый следующим тиком.

- [ ] **Step 1 (RED):** `DirectorSpawnsAtCore_OnActivation`,
  `RetinueSpawnsWithDirector`, `RetinueIsRefilledOverTime`,
  `WorldAtCap_StillSpawnsDirector` (**мир забит волнами под кап**),
  `DirectorNeverLeavesCore`, `CoreLosesWaveBudget_AfterActivation`,
  `CoreDoesNotRegainBudget_AfterDirectorDeath` (Р253).
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4 (мутация):** убрать резерв слотов → `WorldAtCap_StillSpawnsDirector`
  красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS. **Step 6:** R-COMMIT
  `feat(app-35g): Т22 — Директор, свита и резерв слотов`.

### Task Т23: порталы, створ и канал выхода

**Files:**
- Create: `client/Assets/Scripts/Simulation/Objectives/ExtractionSystem.cs`
  (+ `.meta`), `client/Assets/Tests/EditMode/ExtractionTests.cs` (+ `.meta`)
- Modify: `.../Core/SimStates.cs` (`PlayerState.ExtractTimer`),
  `.../Core/SimulationWorld.cs` (`DamagePlayer` — обнуление, `TickAll` —
  порядок), `.../Core/SimEvents.cs` (`PlayerExtracted`)

**Interfaces:**

```csharp
internal static class ExtractionSystem
{
    /// Зовётся ПОСЛЕ боя и ДО MatchFlowSystem.Update.
    public static void Update(SimulationWorld w);
    public static bool IsPortalOpen(in MatchState m, byte extractKind);
}
```

- **Канал:** живой, не извлечённый, портал его зоны открыт, `dist ≤
  ExtractRadius` → `ExtractTimer += TickDt`; иначе **обнуляется**.
- **Урон обнуляет** — вешается в `DamagePlayer` **после** обоих гвардов
  (`!Alive`, i-frames): поглощённый удар канал не рвёт (Р222, симметрия с Р127).
- **Извлечение:** `ExtractTimer ≥ Flow.ExtractChannelSeconds` → `Alive = false`,
  `Extracted = true`, событие `PlayerExtracted`, **труп не создаётся**.

- [ ] **Step 1 (RED):** `ExtractionTests.cs`:

```csharp
[Test] public void ChannelGrows_OnlyInsideRadiusOfOpenPortal();
[Test] public void ChannelResetsToZero_OnAppliedDamage();
[Test] public void ChannelSurvives_IframeAbsorbedHit();
[Test] public void ChannelResets_WhenSteppingOut();
[Test] public void Completing_MarksExtracted_AndLeavesNoCorpse();
[Test] public void ClosedPortal_NeverGrowsChannel();
[Test] public void GateIsClosed_BeforeDirectorDeath();
[Test] public void ExtractedPlayer_IsNotAlive_AndNotActive();
```

- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4 (мутация):** перенести обнуление канала выше гварда i-frames →
  `ChannelSurvives_IframeAbsorbedHit` красный; вернуть.
- [ ] **Step 5:** R-TEST → PASS. **Step 6:** R-COMMIT
  `feat(app-35g): Т23 — порталы, створ и канал выхода`.

### Task Т24: конец захода и запись результата

**Files:**
- Create: `client/Assets/Scripts/Server/MatchResult.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/ResultsTests.cs` (+ `.meta`)
- Modify: `client/Assets/Scripts/Networking/Server/MatchServer.cs`,
  `client/Assets/Scripts/Networking/Protocol/MatchEndedNet.cs`

**Interfaces:**

```csharp
public enum MatchOutcome : byte
{ Died = 0, ExtractedEarly = 1, ExtractedCore = 2, Disconnected = 3 }

public readonly struct PlayerResult
{
    public readonly string PlayerId;
    public readonly MatchOutcome Outcome;
    public readonly int CreditsTotal;
    public readonly int Kills, HeadshotKills, ShotsFired, ShotsHit;
    public readonly float DamageTaken;
    public readonly int AmmoSpent, CellsPicked, SurvivedSeconds;
    public readonly byte[] Loot;          // id предметов рюкзака на момент исхода
}

public static class MatchResult
{
    public static PlayerResult[] Build(SimulationWorld w, MatchRoster roster,
        int matchTicks, int tickRate);
    /// Одна структурная строка на игрока — в формате будущей match_players
    /// (ADR-002 §6) и тела /internal/match-result (§7).
    public static string ToLogLine(in PlayerResult r);
}
```

- `ExtractedCore` — извлечение через створ, `ExtractedEarly` — через ранний
  портал; различает **вид портала**, а не зона.
- **Отключившийся** — `KillPlayerNoDamage`, исход `Disconnected`, труп
  остаётся и лутается (Р271).

- [ ] **Step 1 (RED):** `ResultsTests.cs` — `CreditsSumOverInventory`,
  `OutcomeIsExtractedCore_ForGateExit`, `OutcomeIsExtractedEarly_ForPortalExit`,
  `OutcomeIsDied_ForCorpse`, `DisconnectedIsDistinctFromDied`,
  `LogLine_ContainsEveryContractField`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-TEST → PASS. **Step 5:** R-COMMIT
  `feat(app-35g): Т24 — результат захода в формате будущей меты`.

**Гейт фазы Ф5:** R-TEST зелёный, golden неподвижны; R-BUILD-`LinuxServer`;
два фазовых ревьюера; push; jsonl-chore; `bd close`.

---

## Фаза Ф6 — провод (Т25–Т29)

### Task Т25: пять новых блоков снапшота (opus)

**Files:**
- Modify: `client/Assets/Scripts/Networking/Protocol/SnapshotBlocks.cs`,
  `.../Protocol/SnapshotWriter.cs`, `.../Protocol/SnapshotReader.cs`,
  `client/Assets/Tests/EditMode/SnapshotCodecTests.cs`

**Interfaces:**

```csharp
public enum SnapshotBlockKind : byte
{
    None = 0, Players = 1, Liveness = 2, Mobs = 3, Wave = 4, Events = 5,
    Match = 6, Self = 7, Pickups = 8, Containers = 9, ContainerSlots = 10,
}

public const int MatchBlockPayloadBytes = 4;      // фаза u8, таймер u16, флаги u8
public const int PickupRecordBytes = 7;           // id u16, поз u16×2, вид u8
public const int ContainerRecordBytes = 7;        // id u16, поз u16×2, вид+пуст u8
// Self:           слот-очки u8, число предметов u8, id предметов u8×N
// ContainerSlots: id u16, маска занятости u8, id занятых предметов u8×N
```

- **`Liveness` растёт до двух байт** — вторая маска `extractedMask` (Р257).
- **`Self` не дублирует `ReconcileData`** (Р276): боезапас, таймеры и
  `Extracted` — поля `PlayerState` и едут реконсиляцией; в `Self` только
  рюкзак и слот-очки.
- **Маска занятости слотов обязательна** (Р277) — иначе после частичного
  лутания клиентская нумерация разойдётся с серверной.
- Каждому новому блоку — **пара «Write + …BlockBytes»** (калькулятор бюджета),
  как у существующих пяти.

- [ ] **Step 1 (RED):** в `SnapshotCodecTests` — round-trip каждого блока,
  `MalformedLength` на каждом, `DestinationTooSmall`, `MalformedContent` на
  неизвестном `ContainerKind`/`PickupKind`, `ProtocolVersion_Current_IsThree`,
  `SnapshotBlockKind_ValuesArePinned` (десять значений),
  `LivenessBlock_CarriesTwoMasks`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-TEST → PASS; ГЕЙТ-КОДОГЕН пуст.
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т25 — пять новых блоков снапшота`.

### Task Т26: видимость новых сущностей (opus)

**Files:** Modify `.../Simulation/Visibility/VisibilitySystem.cs`,
`.../Visibility/VisibilitySet.cs`, `.../Networking/Server/SnapshotAssembler.cs`,
`client/Assets/Tests/EditMode/TestWorlds.cs`, `VisibilityTests.cs`.

**Interfaces:**

```csharp
// SnapshotAssembler.Connection — ТРИ отдельных набора вместо одного (Р268 п.2)
VisibilitySet MobsPrevious, MobsCurrent;
VisibilitySet PickupsPrevious, PickupsCurrent;
VisibilitySet ContainersPrevious, ContainersCurrent;

// Ёмкости считаются раздельно и БЕЗ смешения пространств id:
//   мобы:        MaxMobs + MaxPlayers
//   подбираемое: MaxPickups
//   контейнеры:  MaxContainers
// TestWorlds.Capacity(in SimConfig) правится синхронно (второй дом формулы).

// VisibilitySystem
public static void ComputePickups(SimulationWorld w, int observerIndex,
    VisibilitySet previous, VisibilitySet current, float targetRadius);
public static void ComputeContainers(SimulationWorld w, int observerIndex,
    VisibilitySet previous, VisibilitySet current, float targetRadius);
```

- **Три набора, а не теговое пространство** (Р268 п.2): знаковый трюк уже занят
  игроками (`VisibilityIds.ForPlayer(i) => -(i + 1)`), второй слой тегов сделал
  бы `MobSlotOf` неразбираемым.
- **Радиус цели** для не-мобов — параметром (`PickupRadiusForVisibility`,
  `ContainerRadiusForVisibility` из `.asset`), потому что `MobConfigFor` для них
  не существует (Р268 п.3).
- **`VisibilitySet.Add` получает гвард ёмкости** и счётчик отказов — сегодня он
  пишет без проверки границ (`VisibilitySet.cs:80-85`).

- [ ] **Step 1 (RED):** `VisibilityTests` — `PickupBehindArc_IsNotVisible`,
  `PickupBeyondSightRadius_IsNotVisible`, `ContainerHysteresisAndLinger`,
  `SetOverflow_IsRefusedAndCounted_NotThrown`,
  `PickupIdSpace_DoesNotCollideWithMobs` (**ключевой** — Р268 п.2).
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4 (мутация):** вернуть один общий набор на все три класса →
  `PickupIdSpace_DoesNotCollideWithMobs` красный; вернуть три.
- [ ] **Step 5:** R-TEST → PASS. **Step 6:** R-COMMIT
  `feat(app-35g): Т26 — видимость подбираемого и контейнеров`.

### Task Т27: бюджет кадра и обобщённое усечение (opus)

**Files:** Modify `.../Networking/Server/SnapshotAssembler.cs`,
`SnapshotCodecTests.cs`, `client/Assets/Scripts/Data/NetConfig.cs`.

**Interfaces:** порядок бюджетирования (Р243):
`заголовок → Self → Match → Players → Liveness → Wave → ContainerSlots →
Mobs → Containers → Pickups → Events`.

- **Мобы раньше наземного мусора** — иначе в тесном кадре ячейки и пустые ящики
  вытеснят картину угрозы.
- **Усечение по расстоянию обобщается на три класса** (Р217/Р268 п.4):
  кандидатные массивы, `DropFarthestCandidate` и `_capture.*` перестают быть
  моб-специфичными.
- **Фиксированный потолок кадра** пересчитывается с `Match` и `Self` — это
  единственный throw при подъёме сервера (Р279).
- **Порог эскалации** (Р280): счётчик отброшенных сущностей выводится в
  дев-оверлей; превышение 1% кадров на клиента за матч на вехе В2 → немедленно
  заводится задача дельта-снапшотов.

- [ ] **Step 1 (RED):** `WorstCaseFrame_RecomputedWithNewBlocks`,
  `MobsAreBudgetedBeforePickupsAndContainers`,
  `TruncationDropsFarthest_ForEachOfThreeClasses`,
  `FixedCeiling_ThrowsWhenSelfBlockDoesNotFit`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-TEST → PASS. **Step 5:** R-COMMIT
  `feat(app-35g): Т27 — порядок бюджета и усечение трёх классов`.

### Task Т28: надёжный канал лут-запросов

**Files:**
- Create: `client/Assets/Scripts/Networking/Protocol/LootNet.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/LootProtocolTests.cs` (+ `.meta`)
- Modify: `.../Networking/Server/MatchServer.cs`,
  `client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs`

**Interfaces:**

```csharp
public struct LootRequestNet : IBroadcast
{
    public ushort MatchEpoch;
    public byte Op;
    public int ContainerId;
    public byte Slot;
}

public struct LootResultNet : IBroadcast
{
    public ushort MatchEpoch;
    public byte Op;
    public int ContainerId;
    public byte Slot;      // эхо адреса запроса — иначе отказ не привязать к слоту
    public byte Code;      // LootRefusal
}
```

- **Эпоха обязательна** (Р237/Р292): запрос или ответ чужой эпохи отбрасывается
  — тем же правилом, каким это делают `SnapshotQueue.Admit` и `EventDedup`.
- Канал — `Reliable`, класс «жизненный цикл» таблицы Р27.
- Клиент **не предсказывает** лут: гасит слот «призраком» до ответа.

- [ ] **Step 1 (RED):** `LootProtocolTests.cs` — round-trip обеих структур,
  `ForeignEpochRequest_IsIgnored`, `ResultEchoesRequestAddress`,
  `RequestFromDeadPlayer_IsRefusedWithCode`,
  `RequestFromExtractedPlayer_IsRefusedWithCode`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-TEST → PASS; **ГЕЙТ-КОДОГЕН обязателен** — заведены две
  новые проводные структуры.
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т28 — надёжный канал лут-запросов`.

### Task Т29: каталог событий — пять домов

**Files:** Modify `.../Networking/Protocol/SnapshotEvents.cs`,
`.../Simulation/Visibility/EventRelevance.cs`, `SnapshotCodecTests.cs`,
`EventDeliveryTests.cs`.

**Interfaces:** новые виды — `PickupTaken`, `DirectorActivated`, `DirectorDied`,
`PlayerExtracted`, `ContainerEmptied`. **Пять мест, каждое по построению
бросает на неучтённом виде** (Р281): `SnapshotEventKind`,
`SnapshotEvents.PriorityOf` (`default: throw`),
`SnapshotEvents.PayloadBytesFor` (`default: throw`),
`SnapshotEvents.MaxPayloadBytes` (им сайзятся три буфера в
`SnapshotAssembler.Connection`), `EventRelevance.ChannelFor`.

- Доставка: `DirectorActivated`/`DirectorDied` — `All` без позиции;
  `PickupTaken` — `Owner`; `PlayerExtracted` — `All`; `ContainerEmptied` —
  `Visible`.

- [ ] **Step 1 (RED):** `EveryNewKind_HasPriorityAndPayloadSize`,
  `EveryNewKind_HasDeliveryChannel`, `DirectorEvents_GoToEveryone_NoPosition`,
  `PickupTaken_GoesOnlyToOwner`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-TEST → PASS. **Step 5:** R-COMMIT
  `feat(app-35g): Т29 — каталог новых событий на проводе`.

**Гейт фазы Ф6:** R-TEST зелёный, golden неподвижны; ГЕЙТ-КОДОГЕН пуст на трёх
сборках; R-BUILD-`LinuxServer` + R-BUILD-`LinuxClient`; два фазовых ревьюера;
push; jsonl-chore; `bd close`.

---

## Фаза Ф7 — Presentation (Т30–Т35) → веха В1

Зона `Presentation/` — клиентского трека, но он не активен: ведёт server-сессия
(прецедент Этапа 2). `client/CLAUDE.md` и CODEOWNERS **не трогаем**.

### Task Т30: грейбокс зон — дуги, двери, тонировка пола

**Files:** Modify `client/Assets/Scripts/Presentation/GreyboxBuilder.cs`,
`client/Assets/Scripts/Data/GameFeelConfig.cs`.

**Interfaces:** `void BuildZoneWalls()` — рядом с существующими `BuildWall()`
(кольцевая стена арены) и `BuildWallSegments()` (интерьерные стадионы).

- ⚠ **Стены-сегменты уже рисуются** с Т46 Этапа 2 (`BuildContent()` их зовёт,
  `GreyboxBuilder.cs:154`), коллайдеры на `CosmeticsLayer`, слой в маске
  `AimProvider`. Новая работа — **только дуги**.
- **Шаг сегментации меша** — из арифметики стрелки (Р273):
  `segments = ceil(PI / acos(1 - MeshSagMeters / R))`, `MeshSagMeters` 0.05 в
  `GameFeelConfig`. Магической константы в коде быть не должно.
- Косяки дверей рисуются цилиндрами радиуса `HalfWidth` — иначе картинка
  разойдётся с коллизией Т7.
- Пол зон тонируется тремя цветами `GameFeelConfig`.

- [ ] **Step 1:** реализация `BuildZoneWalls()` + четыре новых поля
  `GameFeelConfig` (`ZoneTintOuter/Middle/Core`, `MeshSagMeters`), маркер-ключ
  переезжает.
- [ ] **Step 2:** R-APPLY-`StageTwoSceneBootstrap` → EXIT=0; R-IDEM.
- [ ] **Step 3:** R-COMPILE + R-TEST → 0 регрессий.
- [ ] **Step 4 (визуальная проверка, ручная):** PlayMode — дуги видны, двери
  проходимы глазом, прицельный луч упирается в дугу и **не подсвечивает моба за
  ней**. Это же снимает недостающий эвиденс `app-1ru` (нота задачи от
  2026-08-17) — приложить скриншот в `$SDD`.
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т30 — дуги зон и двери в грейбоксе`.

### Task Т31: вьюхи подбираемого, контейнеров, элиты и Директора

**Files:** Modify `client/Assets/Scripts/Presentation/ViewRegistry.cs`,
`.../Presentation/PersistentPropsDirector.cs`, `.../Presentation/MobVisual.cs`,
`.../Presentation/MobView.cs`, `.../Presentation/CorpseView.cs`,
`client/Assets/Scripts/Editor/StageTwoSceneBootstrap.cs`,
`client/Assets/Scripts/Data/GameFeelConfig.cs`.

**Interfaces:** четыре новых пула и четыре префаба во `ViewRegistry`
(`_elitePool`, `_directorPool`, `_pickupPool`, `_containerPool`); все ветвления
по `MobType` переводятся с тернаров на четырёхзначные таблицы (полный список —
спека Р251).

- Модели: элита и свита — `Enemy_QuadShell`/`Enemy_Trilobite`/`Enemy_EyeDrone`
  (ASSETS-001 §2.2), Директор — самый крупный робот пака со скейлом ×1.5–2
  (§2.3); контейнеры — `Prop_Crate`/`Prop_Chest`/`Prop_Locker`/`Prop_Ammo`;
  подбираемое — примитив со свечением.
- `GameFeelConfig` получает `EliteVisualScale`, `DirectorVisualScale`.
- `Clear()` возвращает все четыре пула — иначе рестарт течёт.

- [ ] **Step 1:** реализация; **Step 2:** R-APPLY + R-IDEM;
- [ ] **Step 3:** R-COMPILE + R-TEST; **Step 4:** PlayMode-смоук: четыре
  архетипа различимы силуэтом, ячейки видно, ящики видно.
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т31 — вьюхи новых сущностей`.

### Task Т32: окно инвентаря по Tab (opus)

**Files:**
- Create: `client/Assets/Scripts/Presentation/InventoryWindowController.cs`
  (+ `.meta`)
- Modify: `client/Assets/Scripts/Presentation/InputSampler.cs`,
  `client/Assets/InputSystem_Actions.inputactions`,
  `client/Assets/Tests/EditMode/InputActionsTests.cs`,
  `client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs` (отправка
  `LootRequestNet`), `client/Assets/Scripts/Editor/StageTwoSceneBootstrap.cs`

**Interfaces:** окно — две панели по краям экрана (справа рюкзак, слева
источник), мир между ними виден, **паузы нет**. Открытие — новое действие
`Gameplay/Inventory` (`<Keyboard>/tab`), закрытие — Tab/Esc/дэш/слайд/выход за
`LootRadius`.

- ⚠ `InputActionsTests.GameplayActions_AllResolveByName` пинит **шесть**
  действий поимённо — новое седьмое обязано быть внесено туда же (Р/находка
  B-M2), иначе сторож покраснеет не по делу.
- Прогресс переноса — полоска на слоте; отказ — цвет и короткий звук **по коду
  из `LootResultNet`**, привязанный к слоту через эхо адреса (Т28).

- [ ] **Step 1 (RED):** `InputActionsTests` — `InventoryAction_ResolvesByName`,
  `GameplayActions_AreSeven`.
- [ ] **Step 2:** заглушка действия → FAIL. **Step 3 (GREEN):** действие в
  ассете + `InputSampler` заполняет `SimInput.InventoryOpen`.
- [ ] **Step 4:** окно и отправка запроса; **Step 5:** R-COMPILE + R-TEST.
- [ ] **Step 6:** PlayMode-смоук: окно открывается, шаг замедляется, стрельба
  недоступна, дэш закрывает окно.
- [ ] **Step 7:** R-COMMIT `feat(app-35g): Т32 — окно инвентаря по Tab`.

### Task Т33: HUD — боезапас, фаза, канал выхода

**Files:** Modify `client/Assets/Scripts/Presentation/HudController.cs`,
`client/Assets/Scripts/Editor/StageTwoSceneBootstrap.cs`.

**Interfaces:** три новых элемента — счётчик боезапаса (число + полоска
`Ammo / AmmoMax`, отдельная пометка аварийного режима), строка фазы
(«фарм» / «Директор» / «створ открыт») с остатком времени захода, кольцо
прогресса канала выхода вокруг фигурки.

- **Реакция на `PickupTaken` — немедленная** (Р275): подбор не предсказывается,
  и ждать реконсиляции значит показывать аварийный режим лишние ~RTT.

- [ ] **Step 1:** реализация; **Step 2:** R-APPLY + R-IDEM; **Step 3:**
  R-COMPILE + R-TEST; **Step 4:** PlayMode-смоук.
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т33 — HUD боезапаса, фазы и канала`.

### Task Т34: экран результатов

**Files:** Modify `client/Assets/Scripts/Presentation/DeathOverlayController.cs`,
`client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs`,
`client/Assets/Scripts/Networking/Protocol/MatchEndedNet.cs`;
Create `client/Assets/Scripts/Networking/Protocol/MatchResultsNet.cs` (+ `.meta`).

**Interfaces:** **два сообщения** (Р270): персональное `MatchEndedNet`
(дополняется своим лутом и кредитами — **плоскими полями**, как требует его
собственный док) и общая рассылка `MatchResultsNet` с **публичным
подмножеством**: слот, исход, кредиты. Точность, урон, выстрелы и убийства
остаются приватными.

- [ ] **Step 1 (RED):** `SnapshotCodecTests` — round-trip `MatchResultsNet`;
  `ResultsTests` — `PublicSubsetCarriesNoAccuracy`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**; ГЕЙТ-КОДОГЕН.
- [ ] **Step 4:** оверлей результатов; **Step 5:** R-COMPILE + R-TEST.
- [ ] **Step 6:** R-COMMIT `feat(app-35g): Т34 — экран результатов`.

### Task Т35: рестарт — полный сброс нового состояния

**Files:** Modify `client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs`,
`.../Presentation/ViewRegistry.cs` (`Clear`),
`.../Presentation/InventoryWindowController.cs`,
`.../Networking/Server/SnapshotAssembler.cs` (пересоздание трёх наборов),
`client/Assets/Tests/EditMode/MatchLifecycleTests.cs`.

**Interfaces:** список сброса Этапа 2 дополняется четырьмя пунктами (Р291):
вьюхи подбираемого и контейнеров возвращаются в пулы; окно инвентаря
закрывается принудительно (флаг окна гасится); HUD боезапаса, фазы и кольца
обнуляется; экран результатов снимается. На сервере — пересоздание **трёх**
`VisibilitySet` на соединение и `_lootRng` из того же seed (Р290).

- [ ] **Step 1 (RED):** `MatchLifecycleTests` —
  `RestartRecreatesAllThreeVisibilitySets`,
  `RestartResetsLootRngToSameSeed`, `ForeignEpochLootResult_IsDropped`.
- [ ] **Step 2:** заглушки → FAIL. **Step 3 (GREEN)**.
- [ ] **Step 4:** R-TEST → PASS; PlayMode: рестарт host-mode оставляет чистый
  кадр (ни висящих ящиков, ни открытого окна).
- [ ] **Step 5:** R-COMMIT `feat(app-35g): Т35 — сброс нового состояния на рестарте`.

**Гейт фазы Ф7 → веха В1 «Арена и обитатели»:** R-TEST зелёный; шесть целей
сборки; host-mode + второй клиент локально; смотрим: три зоны читаются, двери
проходимы игроком и мобами, элита в среднем кольце, вход в ядро поднимает
Директора, боезапас тратится и пополняется, аварийный синтез ощущается,
лутание работает, канал выхода рвётся уроном. **Тюнинг-лист:** раскладка дверей
и препятствий, `ZoneWeights`, числа элиты и Директора, экономика боезапаса,
`GateDelaySeconds`.

---

## Фаза Ф8 — петля и гейт (Т36–Т38) → вехи В2 и В3

### Task Т36: третий golden «петля извлечения»

**Files:** Modify `client/Assets/Tests/EditMode/TestConfigs.cs` (новая
именованная фикстура `Extraction()`), `DeterminismTests.cs`.

**Interfaces:** сценарий — трое, **18 000 тиков (600 с)**, полная фикстура: три
зоны, контейнеры, ненулевые шансы дропа; скриптованный ввод **заводит одного
сборщика в ядро на 120-й секунде**, чтобы активация Директора, его смерть и
открытие створа попали внутрь сценария (Р295).

- Это **новая константа**, а не перепин: санкции С28 не тратит.
- Смоук «два мира на одном seed дают равный хеш на полной фикстуре» вводится
  **с Ф3** и здесь только подтверждается (Р296).

- [ ] **Step 1:** написать фикстуру и сценарий; прогнать дважды — хеши равны.
- [ ] **Step 2:** запинить константу с обоснованием в комментарии.
- [ ] **Step 3:** R-TEST → PASS, `total` ГЛАЗАМИ; два существующих golden
  **не двигаются** (проверить поимённо).
- [ ] **Step 4:** R-COMMIT `test(app-35g): Т36 — третий golden петли извлечения`.

### Task Т37: сборки, образ, замеры на хосте → веха В2

**Files:** Modify `client/docker/` при необходимости; эвиденс — файлами в `$SDD`.

- [ ] **Step 1:** шесть целей сборки **ФОНОМ**, коды выхода читать в логе
  скрипта (234/243); `error CS` = 0.
- [ ] **Step 2:** R-IMAGE с чистого дерева → push; доставка на хост по
  `docs/deploy.md`.
- [ ] **Step 3:** прогон втроём через контейнер; **замер трафика дев-оверлеем
  на клиента** (порог 40 КБ/с) и CPU под `--cpus=1 --memory=1g`.
- [ ] **Step 4:** если счётчик отброшенных сущностей превышает **1% кадров** —
  немедленно завести задачу дельта-снапшотов (Р280) через
  `bd create` + `bd dep add … discovered-from app-35g`.
- [ ] **Step 5:** эвиденс файлом в `$SDD`; короткая `bd note`; сервер **погасить**
  (`docker compose down` — «Up» ≠ «мы запустили»).

### Task Т38: лаг-гейт и плейтест втроём → веха В3

**Files:** эвиденс — файлами в `$SDD`.

**Долг Этапа 2 закрывается здесь фактом** (спека §3.14, С30).

- [ ] **Step 1:** дев-сборки с **обеих** сторон линка (Р192), симулятор 80/5.
- [ ] **Step 2:** восемь пунктов лаг-гейта §3.14 Этапа 2: PvP-попадания против
  ориентира 1.4 м; хедшоты по ганнеру; окна связки дэш↔слайд; слайд под выстрел
  ганнера; смерть в дэше/слайде без отката тела; выстрел в кадре смерти;
  медиана поправки реконсиляции (> 0.25 м разбирается); трассеры.
- [ ] **Step 3:** новые механики под лагом: канал выхода под уроном, окно
  инвентаря в бою, гонка двоих на трупе, подбор ячеек на бегу.
- [ ] **Step 4:** в отчёте — **оба** числа задержки: заданное 80 и измеренное
  (~150–167, Р202).
- [ ] **Step 5:** **плейтест втроём живыми людьми** — фог, звук за стеной, PvP,
  вся петля целиком.
- [ ] **Step 6:** эвиденс файлом в `$SDD`; `bd note`; вехи В2 и В3 принимает
  владелец.

**Гейт фазы Ф8:** оба замера в эвиденсе; восемь пунктов пройдены; плейтест
втроём проведён; push; jsonl-chore; `bd close`.

---

## Фаза Ф9 — финализация (Т39–Т40)

### Task Т39: амендменты ADR одной пачкой

**Files:** Modify `docs/adr/ADR-001-Концепт.md` (§14), `ADR-002-Разработка.md`
(§10), `ADR-003-Сеттинг.md` (§11).

**Восемь амендментов** (спека §9) — **только амендментом, правка «по месту»
запрещена** (урок 250):

- **ADR-001 A4** — ранние порталы открыты с начала и закрываются активацией
  Директора; активацию запускает **вход сборщика в ядро**, а не таймер (Р299).
- **ADR-001 A5** — труп сборщика лутается полностью до появления страховки (Э5).
- **ADR-002 A18** — состав Этапа 3: Директор со свитой, боезапас, ремкомплект,
  friendly fire мобов; ступени трёх зон вместо «градиента»; **снимается
  «ранние порталы по таймеру» и в описании Этапа 3** (находка A-9).
- **ADR-002 A19** — эпик «Рост носителя» между Э3 и Э4 (прецедент A9).
- **ADR-002 A20** — дроп сумки с убитого переносится в Э3 (прецедент A13).
- **ADR-002 A21** — подпапка `Simulation/Loot/` (прецедент A15).
- **ADR-003 A2** — энергоячейки на Э3 — сырьё патронов; топливо перегруза — с Э5.
- **ADR-003 A3** — словарь: строка «Элита ядра | Свита» **заменяется** парой
  «элита (`Elite`)» / «свита (`Retinue`)»; добавляются контейнер, тайник, ядро
  памяти, боезапас, подбираемое, рюкзак, тир, створ, ранний портал,
  **ремкомплект (`RepairKit`)**.

- [ ] **Step 1:** внести восемь амендментов; **Step 2:** вычитать, что ни один
  исходный текст ADR не тронут; **Step 3:** R-COMMIT
  `docs(app-35g): Т39 — амендменты ADR Этапа 3`.

### Task Т40: эпик «Рост носителя», PR, закрытие

- [ ] **Step 1:** `bd create -t epic "Рост носителя: энергоячейки как опыт,
  данные, уровни и карточки §7.1, типы патронов"` + `bd dep add <new> app-35g`
  (blocks) + `bd dep add app-4bi <new>` (Э4 после него).
- [ ] **Step 2:** полный R-TEST, шесть целей сборки, ГЕЙТ-КОДОГЕН — свежими
  прогонами.
- [ ] **Step 3:** `superpowers:finishing-a-development-branch` → `gh pr create`
  → merge `--squash --admin`.
- [ ] **Step 4:** `bd close app-35g` с эвиденсом + `bd export`; jsonl-chore;
  `bd close app-46m`; решение по `app-1ru` (нота от 2026-08-17).
- [ ] **Step 5:** handoff — **по команде владельца**, по `HANDOFF_PROTOCOL.md`.

---

## Декомпозиция bd

```bash
cd "$APP_REPO"
# 9 фазовых сабтасков, parent-child к эпику + blocks-цепочка по порядку
bd create "Ф1: экономика захода — боезапас, ячейки, рюкзак, FF (перепин №1)" -t task -p 1
bd create "Ф2: арена трёх зон и её обитатели (перепин №2)" -t task -p 1
bd create "Ф3: предметы и контейнеры" -t task -p 1
bd create "Ф4: лутание" -t task -p 1
bd create "Ф5: фаза захода, Директор, выход" -t task -p 1
bd create "Ф6: провод — блоки, видимость, бюджет, канал лута" -t task -p 1
bd create "Ф7: Presentation → веха В1" -t task -p 1
bd create "Ф8: петля и гейт → вехи В2 и В3" -t task -p 1
bd create "Ф9: финализация — амендменты, эпик роста, PR" -t task -p 1
# для каждого: bd dep add <ФN> app-35g --type parent-child
# цепочка:     bd dep add <ФN+1> <ФN>
# app-46m (friendly fire) уже child app-35g — закрывается в Ф1 (Т5)
```

---

## Self-review плана (выполнен, 2026-08-17)

**1. Покрытие спеки.** Пройден каждый раздел §3.1–§3.18 и §4:

| Раздел спеки | Таск |
|---|---|
| §3.2 арена и дуги | Т7, Т8, Т9, Т12, Т30 |
| §3.3 волны, элита, FF | Т5, Т10, Т11 |
| §3.4 Директор | Т22 |
| §3.5 фаза, порталы, створ | Т1, Т21, Т23 |
| §3.6 ячейки и боезапас | Т2, Т3 |
| §3.7 предметы, контейнеры, рюкзак | Т4, Т13, Т14, Т15, Т16 |
| §3.8 лутание | Т17, Т18, Т19, Т20, Т28 |
| §3.9 видимость | Т26 |
| §3.10 результат | Т24, Т34 |
| §3.11 Presentation | Т30–Т34 |
| §3.12 провод | Т25, Т27, Т29 |
| §3.13 данные | Т8, Т12, Т13 |
| §3.14 долг CR 7 | Т38 |
| §3.15 раскладка | Т12 |
| §3.16 «не половина системы» | распределён по гейтам фаз |
| §3.17 что не делаем | вне плана по построению |
| §3.18 рестарт и эпоха | Т35 |
| §4 детерминизм и golden | Т6, Т12, Т36 |

**2. Плейсхолдеров нет.** Каждый шаг несёт либо команду с ожидаемым выводом,
либо код теста, либо сигнатуру. Единственное сознательное «решает исполнитель»
— форма матрицы долга в `WaveState` (`fixed` против девяти полей, Т11) —
названо явно вместе с критерием выбора и требованием записать выбор в отчёт.

**3. Согласованность имён.** `PickupState.Amount` — `int` везде (Т3, Т6);
`LootTargetContainerId` — `Id`, не индекс (Т17, Т18); `MatchEndReason` —
третье значение в конце (Т1, Т24); `MobConfigFor` — `switch`, не тернар (Т10);
`InventoryOpen` живёт в `SimInput`, а не в `PlayerState` (Т17, Т20);
`RepairKit`/`RepairKitHealAmount`/`RepairKitChannelSeconds` — единообразно
(Т13, Т19).

**4. Санкции golden.** Движение констант разрешено ровно дважды — Т6 и Т12;
Т5 и Т11 сдвигают хеш **внутри своей фазы** и фиксируются перепином в конце
фазы, а не собственным пином. Т36 заводит третью константу, что санкций не
тратит. Каждый гейт фазы Ф3–Ф7 проверяет неподвижность обеих констант.

---

# ⚠ ERRATA ПЛАНА — self-review по `review_plan.md` (2026-08-17)

**ПЛАН ПРОТИВ ЭТОЙ СЕКЦИИ — ВЕРИТЬ ЭТОЙ СЕКЦИИ** (урок 124). Четыре read-only
субагента (A корректность кода, B конвенции, C переиспользование, D TDD и
полнота) дали **10 Critical, 53 Important, ~49 Minor**. Всё, что ниже названо
ошибкой ТЕКСТА плана, — ошибка плана, а не кода: код сверен открытием файлов.
Исполнитель читает эту секцию ДО первого таска.

## E-1. ⛔ СТРУКТУРНАЯ ПЕРЕСБОРКА Ф1 (A-C1 = D-C1, четыре ревьюера сошлись)

**Дефект.** Т14 (`WorldStats.ContainerSpawnsSkipped`), Т17 (`PlayerState.LootTimer`,
`LootTargetContainerId`, `LootTargetSlot`), Т19 (`RepairTimer`), Т23
(`ExtractTimer`), Т24 (`AmmoSpent`/`CellsPicked`/`SurvivedSeconds`, если их дом —
`MatchStats`) объявляют **хешируемые** поля в фазах Ф3–Ф5, где обе санкции уже
израсходованы. `StateHash64.Add` — FNV-1a побайтовой цепочкой: **лишний `Add`
сдвигает дайджест даже при нулевом значении**, а рефлексивный свип
`EveryPlayerAndStatsFieldAffectsHash` требует вхождения каждого поля в хеш с
момента его объявления.

**Правка (обязательная, меняет границы тасков):**

1. **Все** новые поля `PlayerState`, `WorldStats`, `MatchStats`, `ProjectileState`,
   `WaveState`, `MatchState` объявляются **в фазе Ф1**, с инертными значениями и
   без поведения. К списку Т1–Т5 добавляются: `LootTimer`,
   `LootTargetContainerId`, `LootTargetSlot`, `RepairTimer`, `ExtractTimer`,
   `ExtractKind` (byte, 0 = не извлечён — нужен Т24 для различения
   `ExtractedEarly`/`ExtractedCore`, A-I12), `WorldStats.ContainerSpawnsSkipped`,
   `MatchStats.AmmoSpent`/`CellsPicked`.
2. **Скип-лист заводится явно.** Между объявлением поля и перепином Т6 свип
   красный, поэтому Т1/Т2/Т3/Т5 получают шаг: внести имя поля во **временный**
   `PendingHashFields` (`WorldLifecycleTests`) — прецедент Этапа 2 Т7→Т10 с тем
   же комментарием-адресатом. Т6 снимает скип-лист **безусловно** и доказывает
   снятие (вынуть поле → свип называет его поимённо).
3. **Обоснование перепина №1 в `DeterminismTests` дополняется** всеми полями из
   п.1, а не только пятью из текущего текста Т6.
4. Т17/Т19/Т23/Т24 после этого несут **только поведение** — ни одного нового
   поля состояния.

## E-2. ⛔ `MatchFlowConfig` не существует нигде (B-C1 = C-I1 = D-C3)

`MatchFlowSimConfig` объявлен в Interfaces Т22, используется в Т21 (раньше!), а
SO-класса `Data/MatchFlowConfig.cs`, ассета, проводки в `SimConfigBuilder.Build`
и домов перечисления SO нет ни в одном таске — то есть `GateDelaySeconds 90`,
`ExtractChannelSeconds 20` и `DirectorReserveSlots 3` физически не доедут до игры.

**Правка:** объявление `MatchFlowSimConfig` и поля `SimConfig.Flow` переезжает в
**Т1** (вместе с `MatchState` — это одна подсистема), а вся SO-обвязка
(`Data/MatchFlowConfig.cs` со всеми требованиями Global Constraints, `.asset`
через `ApplyStageThreeBalance`, `SimConfigBuilder`, полный список домов Р283,
`ArenaTopologyMatches`) — в **Т12**, единственный таск доставки данных. Т22
только использует готовое.

## E-3. ⛔ Т24 переписывается целиком (C-C1 + B-C2)

Два независимых дефекта:

- **Дублирование.** Механизм итога захода уже есть: `MatchServer.MatchSummary`
  (`:1409`), `BuildSummary` (`:1309`), `EndedNetFor` (`:1342`),
  `ServerBootstrap.LogMatchSummary` (`:1005`), чей док прямо требует **«ONE LOG
  EVENT, NOT ONE PER PLAYER»** — а план требовал строку на игрока.
- **Цикл сборок.** `Server.asmdef` ссылается на `Ring.Networking`; обратной
  ссылки нет и быть не может. `MatchServer` (Ring.Networking) не может ни звать
  `MatchResult.Build`, ни принимать `MatchRoster` (тип `Ring.Server`).
- **Мир после `StopMatch` недоступен** — `ServerBootstrap.cs:996` говорит это
  прямым текстом.

**Правка:** Т24 = **расширение `MatchSummary`** новыми плоскими полями
(`Outcome`, `CreditsTotal`, `Loot`, `AmmoSpent`, `CellsPicked`,
`SurvivedSeconds`), снятыми в `BuildSummary` **до** `StopMatch`; `EndedNetFor`
дополняется ими же; строку лога расширяет существующий `LogMatchSummary`
(одна строка на матч, как и было). Новых файлов `MatchResult.cs`/`PlayerResult`
не заводится. Т34 строит **оба** сообщения из того же `MatchSummary` (C-M12).

## E-4. Арифметические и golden-ошибки в тестах Ф1 (A-C2, A-C3, A-C4)

- **A-C2.** Тест `AtZero_FiresOnEmergencyInterval_AndSpendsNothing` при
  `ticks = ceil(V/dt) + 2` даёт **два** выстрела (t = 0 и t = 37 при V = 1.25).
  Правка: `int ticks = (int)math.ceil(V / TickDt) - 1;` и ожидание одного.
- **A-C3.** `AmmoStart 120` уронит golden прямо в Т2: сценарий держит гашетку с
  p = 0.7 на тик, `FireInterval` 0.12 → 245–277 выстрелов за 1000 тиков. Правка:
  `TestConfigs` получает **явное фикстурное `AmmoStart = AmmoMax = 400`** с
  комментарием «число тестов, не зеркало C#-дефолта»; шаг 5 Т2 переформулируется
  из «проверить арифметикой» в «задано заведомо больше 277».
- **A-C4.** Дроп с трупа игрока съедает `_nextEntityId` (четвёртый шаг хеша), а
  в мультиплеерном сценарии игроки гибнут. Правка: Т3 оформляется как Т5 —
  «`DeterminismTests` ожидаемо красный до Т6», плюс `CorpseCellFraction = 0` в
  `TestConfigs`.

## E-5. `InventoryOpen` — противоречие плана самому себе (D-C2)

Files Т17 кладут флаг в `PlayerState`, Т20 и self-review — в `SimInput`.
Правильно — **`SimInput`** (он влияет на предсказываемое движение). Правка:
убрать `InventoryOpen` из списка `SimStates.cs` в Т17; в Т17 добавить шаг
«завести `SimInput.InventoryOpen` заглушкой, без кодека — бит провода в Т20».

## E-6. Принятые Important (исполняются в своих тасках)

**Переиспользование (C):** I2 — `FixedFrameBytes` выделяется одним домом до Т25
(сейчас потолок кадра считается дважды: `SnapshotAssembler.cs:265` и `:1203`);
I3 — ёмкость наборов видимости одним домом `CapacityFor(in ArenaSimConfig,
VisibilityClass)`, `TestWorlds.Capacity` делегирует туда; I5 — TTL-шаг и
проводная запись подбираемого/контейнеров сводятся в один дом **до** Т25;
I6 — «кандидаты → отбраковка → RNG-free сетка» выносится из `WaveSystem` в общий
`SpawnPlacement` и переиспользуется Т15; I7 — `RepairTimer` и `ExtractTimer`
получают **один** `AbortChannels(ref PlayerState)` с одним вызовом в
`DamagePlayer`; I8 — все новые таймеры вносятся в `KillPlayer` и в клампы
`ApplyConfig`; I9 — из `KillPlayer` выделяется `ClearCombatTimers`, извлечение
зовёт его же; I10 — дроп при смерти получает один дом `Loot/LootDrops.cs`,
заводимый **в Т3**; I11 — вью контейнера видов `MobCorpse`/`PlayerCorpse` не
создаётся, подсвечивается существующая труп-вьюха; I12 — кламп `1e-3f`
переезжает **внутрь** `IntervalFor`, `Advance` зовёт только его; I13 —
`StalePolicy` и `app-dut` получают дом в Т26 (три экземпляра + общее
отображение по образцу `MobTypeMemory`).

**Конвенции (B):** I1 — британские формы в именах тестов (`…Centre`,
`…Neighbour`) исправляются на американские; I2 — `MobHpForTest` не существует,
писать `w.Mobs[1].Hp`; I3 — `CellsOnDeath` (Т3) снимается в Т13 в пользу
`LootSimConfig.CellsPerMob` явным шагом; I4 — `PickupRadiusForVisibility`/
`ContainerRadiusForVisibility` получают дом в `VisibilityConfig` (Т13) с
переездом маркера; I5 — проводка `MobEliteConfig`/`MobDirectorConfig` через
`SimConfigBuilder` и дома Р283 вносится в Т12; I6 — **возвращается правило
Этапа 2**: «русские пояснения из сниппетов при переносе в `.cs` переводятся на
английский», и assert-сообщение Т2 переводится прямо в сниппете; I7 —
`ProtocolVersion_Current_IsPinnedToTwo` обновляется **в Т10**, а не в Т25.

**Корректность (A):** I1/I2 — `SpawnProjectileForTest` получает `ownerEntityId`
хвостовым с умолчанием; I3 — `Evaluate` приводится к одной канонической
четырёхпараметрной сигнатуре в Interfaces и в обоих тестах; I5 —
`allowUnsafeCode: false`, поэтому развилка Т11 решается **сейчас**: девять
именованных полей `PendingOuterChaser … PendingCoreElite`, «решает исполнитель»
снимается; I6 — `InterpolationBufferTests.cs` вносится в Files Т11 (он
инициализирует `PendingChasers`/`PendingGunners`) плюс пересчёт расписки в
`WorldLifecycleTests`; I7 — `ComputePickups`/`ComputeContainers` получают
`in VisibilitySimConfig cfg` тем же порядком параметров, что `Compute`; I8 —
`LootRadius` заводится в `LootSimConfig` (Т13) и доставляется Т12; I11 —
`DropChance` индексируется плоско `[archetype * ZoneCount + zone]`.

**TDD и полнота (D):** I2 — тест инварианта `(Alive, Extracted)` переписывается
на неваккумный и Т1 получает мутацию «поменять порядок проверок причин конца»;
I3 — заводится `ExtractedInSameTick_PicksNothing`, мутация Т3 перенацеливается
на него; I4 — Т9 получает тесты `LineOfFire_BlockedByArcBody` /
`LineOfFire_PassesThroughDoor` и мутацию на кламп отступа; I5 — Т15 получает
тест «RNG-кандидаты отбракованы, сетка нашла место»; I6 — два недостающих
`[Range]` (`MobConfig.Radius` до 4, `MaxHp` до 5000) вносятся в Т8; I8 — восемь
недостающих валидаций распределяются по Т2/Т4/Т8/Т11; I9 — **`SimConfigHash`
пополняется** зонами, дверями, порталами, каталогом, рюкзаком, боезапасом,
дропом и `Flow` (шаги в Т8/Т10/Т13/Т22 + `SimConfigHashTests`); I10 —
`ReconcileCodecTests`, `AllocationTests`, `BarrierHeightTests` привязываются к
Т2/Т17/Т23, Т3/Т18/Т25 и Т7; I13 — вьюхи порталов и створа вносятся в Т30;
I15 — Т16 и Т22 получают шаг «проверить и записать golden-нейтральность»;
I16 — перепин №2 выделяется **отдельным коммитом**, препятствия §3.15 вносятся
в санкционированную таблицу Т12, тесты раскладки назначаются Т12;
I18 — `Drop_SpawnsGroundContainerWithItem` добавляется в Т18; I19 — три теста
капа и клампа боезапаса добавляются в Т2/Т3; I20 — шаг 5 Т11 переписывается по
образцу Т5, стоп-условие Ф1 читается «сдвиг вне Т5 и Т6».

**Гранулярность (D):** Т6 Step 1 («внести все поля») и Т9 Step 3 («шесть
правок») разбиваются по группам полей и по потребителям; Т12 разделяется на
доставку данных и перепин (следствие I16а).

## E-7. Minor — приняты и исполняются по месту

Неймспейс `Ring.Simulation.Tests` в сниппетах Т1; `SnapshotBlockKind` —
одиннадцать значений с `None`, имя теста `…_AndNoneIsZero`; `LootRefusal.Dashing`
переименовывается в `DashingOrSliding`; маркер-ключи переезжают в тех же тасках,
где дописаны поля (Т3/Т4/Т8), а не в Т12; `ItemKind`/`ItemDef` живут в
`Simulation/Core`, не в `Data/`; отказ лута приезжает в окно как `LootRefusal`
(тип `Ring.Simulation`), а не как `LootResultNet`; мутации Т2 и Т20
переформулируются (`AdvanceNoSpawn` — однострочный делегат, мутировать надо
`Advance` под `worldOrNull != null` и санитайзер предсказания соответственно);
`Idle` называется шестым состоянием элиты; `SimEvents.cs` вносится в Files Т21;
`ContainerEmptied` получает эмитента (Т18); `GibView.cs` вносится в Т31; счётчик
доменов SO в Т13 разделяется на «call-site'ы `Build`» и «дома перечисления»;
блок `Pickups` — решение «`Amount` на провод не едет» записывается явно;
`bd close app-46m` числится только за Ф1; `SplitByZones(0)` покрывается тестом;
кап каталога 255 валидируется; санитайзер флага окна тестируется на всех
четырёх случаях; тесты «скан по типу», «долг свиты при капе», «при нуле живых
директор волн не тикает», `RepairKitChance`, «пустой лут», «сброс канала при
закрытии портала» добавляются в свои таски.

## E-8. False positive и осознанные отклонения

- **C-M5 «дуга = цепочка стадионов не рассмотрена»** — рассмотрена и
  отвергается **записанно**: при `R = 92` и стрелке 0.05 м нужно ~160 стадионов
  на кольцо, а `SweepArena`/`Depenetrate`/`SteerAround`/`IsValidSpawn`/
  `HasLineOfFire` линейны по числу барьеров и зовутся на каждое тело каждый тик.
  Нативная дуга — три функции против ~160 записей в горячих циклах.
- **C-M6 «грейбокс уже умеет дугу»** — принято частично: `BuildWallCap` для
  косяков переиспользуется, но `BuildWall` строит **полный** круг фиксированными
  48 сегментами; зонная дуга требует углового диапазона и выреза, то есть
  параметризации того же цикла, а не второй политики. Формула сегментации
  остаётся (она и делает 48 производным числом).
- **C-I4 «обоснование трёх наборов видимости неверно»** — по существу верно:
  id не сталкиваются (счётчик общий), настоящая причина — диспетчеризация
  класса в `WriteFrame` по знаку id. Решение «три набора» остаётся, но тест
  переименовывается в `PickupInFrame_IsNotMistakenForADeadMob`, а дефолтная
  ветка `else` в `WriteFrame` перестаёт быть «мобом по умолчанию».
- **D «спека пишет „все семь вносятся“ при восьми амендментах»** — опечатка
  спеки §9, амендментов восемь; исправляется при исполнении Т39.

