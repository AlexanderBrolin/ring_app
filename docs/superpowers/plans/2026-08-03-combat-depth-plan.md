# План имплементации: Боёвка-глубина — 3D-прицел, зоны, Буст-мувмент (app-n6g)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development.
> Утверждено владельцем: implementer per task = **sonnet** (механика по готовым
> формулам), **opus** — Т6 и Т15 (тонкая математика хитрега/оружия) и финал-ревью
> ветки; ревьюеры фаз = sonnet; **верификация всех вердиктов, прогоны R-TEST/
> R-COMPILE и гейты — main-агент (fable), не на веру.** Шаги — чекбоксы `- [ ]`.

**Goal:** двухрежимный прицел (ПКМ-луч / от бедра), зоны поражения
голова/тело/ноги у мобов и носителя, ресурс «Буст» (дэш/слайд/рикошет/связки),
обломки по зоне и вектору, упреждающий замах чейзера — по спеке
`docs/superpowers/specs/2026-08-03-combat-depth-spec.md` (**v5**).

**Architecture:** вертикаль ТОЛЬКО у снарядов и хит-объёмов (`Height/VelZ` +
пояса из SO); движение остаётся 2D. Все новые механики — детерминированные
поля `PlayerState`/`ProjectileState` + чистые системы `Ring.Simulation`;
Presentation потребляет снапшот/события/`World.Config` (каналы Э1). Баланс —
только SO → `SimConfigBuilder`. Golden пересеивается в каждом сим-таске,
финальная константа — Т16.

**Tech Stack:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics 1.3.x;
новых пакетов нет (CR 9).

**Спека:** v5 (Д1–Д15; два раунда self-review). **Статус плана:** v4 —
глубина по эталону Фазы Б/Э1; правки ДВУХ раундов план-ревью:
PA1–PA17/PB1–PB12/PC1–PC16/PD1–PD22 (раунд 1, v2) и
QA1–QA18/QB1–QB21/QC1–QC21/QD1–QD17 (раунд 2 по v3: тест-швы без
лямбд, `Spread` public-класс вместо internal-формулы, семантика точки
прицела с прокси, `MovementResult`-контракт, гонка маркера ганнера,
локальный `aimHeld` golden-сценария, вехи чекбоксами, гейты Г6/Г7).

## Global Constraints (каждый таск обязан соблюдать)

- Пути: `WT="/home/brolin/Documents/!_MY_Proj/The Ring/app/.worktrees/feature-app-n6g-combat-depth"`
  (cwd всех команд; worktree СУЩЕСТВУЕТ — не пересоздавать);
  `APP_REPO="/home/brolin/Documents/!_MY_Proj/The Ring/app"` (bd — ТОЛЬКО отсюда);
  `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH=<scratchpad ТЕКУЩЕЙ сессии>` — задать на старте, в логи/xml ниже.
- **Запретный список:** не менять `client/Packages/**`, `.gitattributes`,
  `client/CLAUDE.md`, `.github/CODEOWNERS`, контент паков вне `_Ring/`
  (ASSETS-001 §4.1), `client/ProjectSettings/**` КРОМЕ `TagManager.asset`
  (слой `AimProxy`, Т19; `DynamicsManager` НЕ трогать — матрица коллизий
  runtime-вызовом, B3). `client/Assets/Data/*.asset` руками не редактировать:
  новые ключи доставляют ТОЛЬКО маркер-механизм + `ApplyGunnerZoneDefaults`
  (Т17); **правка существующих чисел ассетов запрещена** (балансовые PR —
  рука владельца на вехах: радиус снаряда, `GunnerVisualScale 0.4→≈0.76`).
  `Main.unity` — только через `StageOneSceneBootstrap.Apply`.
- **Simulation меняется** (суть пакета) — но строго TDD (CR2) и без
  UnityEngine (CR1; только `Unity.Mathematics`).
- **ГЕЙТ-ОТКАТ (после КАЖДОГО Unity-прогона):** `git status --porcelain --
  client/Packages client/Assets/Settings .gitattributes client/ProjectSettings
  "client/Assets/TextMesh Pro"` (QB17) → дифф `TagManager.asset` допустим
  **только начиная с Т19 и только в его коммите** (до Т19 — откат, QB8);
  иной дрифт → `git checkout -- <пути>`; TMP-самопис
  `LiberationSans SDF - Fallback.asset` → откатывать всегда (урок 32).
- **ГЕЙТ-ЛОГ (после каждого batchmode):** `grep -E "error CS|Shader error|
  Failed to import|Error while importing|NullReferenceException|Exception"
  <лог>` → пусто (кроме явно ожидаемых таском строк).
- **ГЕЙТ-META:** каждому новому не-`.meta` файлу **и папке** (`_Ring/
  Animations/`, `_Ring/Gibs/` — Т23/Т24) соответствует `<path>.meta`
  (QB16); генерятся ближайшим Unity-прогоном; несопоставленный → стоп.
- **Тест-швы состояния (QA1):** лямбда-перегрузок нет; канон —
  `var p = w.Player; p.X = …; w.SetPlayerForTest(p);` (эталон
  `WorldLifecycleTests.cs:56`); чтение мобов/снарядов — существующие
  `w.Mobs[i]` / `w.Projectiles[i]` (internal-массивы, QC5).
- Русские пояснения в сниппетах плана при переносе в `.cs` ПЕРЕВОДЯТСЯ
  на английский (QB6; правило эталонов).
- **RED-дисциплина (PA17):** тест не компилируется из-за отсутствующих
  полей/сигнатур → сначала пустые поля/заглушки до КОМПИЛЯЦИИ, затем
  наблюдаемый FAIL ассерта. Ошибка компиляции ≠ RED.
- **Числа в тестах (PD5):** ожидания — ТОЛЬКО фикстурными выражениями
  (`cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac`), не литералами `.asset`
  (расхождение `TestConfigs` ↔ `.asset` санкционировано C27). Где нужна
  конкретная арифметика (дистанция дэша) — явная фикстура В ТЕСТЕ.
- Новые SO-поля: `[Range(min, max)]` с осмысленным верхом (`[Min]` в проекте
  не существует — PA10); маркер-поле каждого SO — ПОСЛЕДНЕЕ в классе +
  комментарий `// sync-marker key — keep LAST` (PB9).
- Словарь в прозе/UI: «Буст», «зоны поражения», «хит-объём»; `Stamina`/
  `HitZone` — только код (PB8). Идентификаторы/комментарии `.cs` — английские.
- bd: клейм фазового сабтаска на старте фазы, `bd note app-n6g` после каждого
  таска, `bd close` сабтаска с evidence; jsonl-дрифт —
  `chore(app-n6g): jsonl-дрифт beads — Фаза ГN` из `$APP_REPO` в main.
- Коммиты: `feat|test|fix|refactor|chore|docs(app-n6g): …` (рус.) + трейлер
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед каждым —
  секрет-чек `git status --short --untracked-files=all | grep -E
  '\.(env|pem|key)$|secrets/'` → пусто.
- Unity-API сверять по `client/Library/PackageCache/**` (Context7 в прошлых
  сессиях недоступен — проверить, фолбэк curl). batchmode не гонять при
  открытом Editor'е владельца: `ps aux | grep -i "[U]nity/Hub/Editor.*projectpath"`.

## Runbook

- **R-TEST:** `cd "$WT" && "$UNITY" -runTests -batchmode -projectPath client
  -testPlatform EditMode -testResults "$SCRATCH/t.xml" -logFile "$SCRATCH/t.log";
  echo EXIT=$?` → EXIT=0, в xml `failed="0"` (БЕЗ `-quit`!) + ГЕЙТ-ОТКАТ.
  Ожидаемый счётчик растёт по фазам (старт 93).
- **R-FILTER `<Класс>`:** R-TEST + `-testFilter "Ring.Simulation.Tests.<Класс>"`.
- **R-COMPILE:** `cd "$WT" && "$UNITY" -batchmode -quit -projectPath client
  -logFile "$SCRATCH/c.log"; echo EXIT=$?` → EXIT=0 + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-APPLY-<X>:** `cd "$WT" && "$UNITY" -batchmode -quit -projectPath client
  -executeMethod Ring.Editor.<X>.Apply -logFile "$SCRATCH/apply-<X>.log";
  echo EXIT=$?` (X ∈ StageOneSceneBootstrap | ThirdPartyImportBootstrap |
  ThirdPartyAnimatorBootstrap | AssetPreviewSceneBootstrap; свой лог на
  прогон — ПБ15/QB7) → EXIT=0 + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ. Без `<X>` = 
  StageOneSceneBootstrap.
- **R-IDEM:** повторный R-APPLY → `git status --porcelain -- client/` пуст и
  `git diff -- client/` пуст (мерить ПОСЛЕ коммита артефактов — урок А6).
- **R-GOLDEN (перепин):** R-FILTER `DeterminismTests` → из лога/xml взять
  `But was: <N>` теста `GoldenHash_ScriptedScenario` → вписать константу
  (`DeterminismTests.cs:146`, hex) + **обновить десятичный
  дубль-комментарий и однострочное обоснование перепина (какой таск/почему —
  QB15)** → повторный R-FILTER → PASS. Старый хеш Э1
  `0x39B4C57694AD8770UL`; каждый перепин — строкой в bd note таска.
- **R-BUILD-<X>:** `cd "$WT" && RING_BUILD_ROOT="$SCRATCH/builds" "$UNITY"
  -batchmode -quit -projectPath client -executeMethod
  Ring.Editor.BuildCommands.Build<X> -logFile "$SCRATCH/b<X>.log"; echo EXIT=$?`
  (X ∈ LinuxServer | WindowsClient).
- **R-COMMIT:** секрет-чек → ГЕЙТ-META → `git add <файлы+meta> && git commit
  -m "<msg>" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"`.

---

## Фаза Г1 — данные и RNG (Т1–Т3)

### Task Т1: конфиг-поля зон и дул (Hero + Mob)

**Files:**
- Modify: `client/Assets/Scripts/Data/HeroConfig.cs`, `client/Assets/Scripts/Data/MobConfig.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs`
- Modify: `client/Assets/Scripts/Data/SimConfigBuilder.cs`
- Modify: `client/Assets/Tests/EditMode/TestConfigs.cs`, `client/Assets/Tests/EditMode/ConfigTests.cs`

**Interfaces:**
- Produces (потребители Т4–Т15): `HeroSimConfig.{LegsTop, BodyTop, HeadTop,
  LegsDamageMult, BodyDamageMult, HeadDamageMult, SlideProfileTop,
  MuzzleHeight, SlideMuzzleHeight, MaxAimHeight}` (float);
  `MobSimConfig.{LegsTop, BodyTop, HeadTop, LegsDamageMult, BodyDamageMult,
  HeadDamageMult, MuzzleHeight, SwingLeadFactor, SwingLeadMaxMeters}`.
- Дефолты C#-классов (= `TestConfigs` = будущий автосинк): Hero
  `0.55 / 1.35 / 1.75`, мульты `0.75 / 1.0 / 1.7`, `SlideProfileTop 0.55`,
  `MuzzleHeight 1.0`, `SlideMuzzleHeight 0.45`, `MaxAimHeight 3.8`;
  `MobConfig` (класс = чейзер): `0.60 / 1.45 / 1.85`, мульты `0.75/1.0/1.7`,
  **`MuzzleHeight 0.95` — дефолт класса** (чейзер поле не читает; иначе
  Validate-правило D5 уронит существующий `Build_DefaultAssets_...` — PA11),
  `SwingLeadFactor 1.0`, `SwingLeadMaxMeters 2.0`. Ганнер-значения в
  `.asset` (1.10/2.70/3.50) — Т17 (`ApplyGunnerZoneDefaults`); **но
  `TestConfigs.Default().Gunner` получает башню УЖЕ ЗДЕСЬ (QA4):**
  `LegsTop 1.10, BodyTop 2.70, HeadTop 3.50`, мульты `0.75/1.0/1.7`,
  `MuzzleHeight 0.95` (gunner-блок `TestConfigs.cs:27–32` — отдельные
  значения, не класс-дефолты; вся геометрия Т6 держится на башне) + те же
  поля — в gunner-блок копирования
  `Build_DefaultAssets_MatchesTestConfigsBaseline` (блок присваивает
  gunner-SO значения из baseline ПЕРЕД `Build` — тест не зависит от
  `.asset` и не ждёт Т17; A3).
- Валидация (добавить в `SimConfigBuilder.Validate`, единый `ArgumentException`
  со списком): общий приватный `ValidateZones(List<string> errors, string who,
  float legs, float body, float head, float legsMult, float bodyMult,
  float headMult)` — `0 < legs < body < head`, мульты ≥ 0 (ОДНО тело на
  Hero/Chaser/Gunner — PC5); `SlideProfileTop`: `0 < x ≤ Hero.BodyTop`,
  `Hero.LegsTop ≤ x`, **`x + Gunner.ProjectileRadius < Gunner.MuzzleHeight`**
  (D5); `Hero.MuzzleHeight ≤ Hero.HeadTop`; `SlideMuzzleHeight ≤
  SlideProfileTop`; `MaxAimHeight ≥ max(Hero.HeadTop, Chaser.HeadTop,
  Gunner.HeadTop)`.

- [ ] **Step 1 (RED):** в `ConfigTests.cs` добавить (сообщение проверяется
  `Does.Contain` — иначе зелёнку даст чужое правило, PD14):

```csharp
[Test]
public void Validate_ZoneOrderViolated_Throws()
{
    var hero = ScriptableObject.CreateInstance<HeroConfig>();
    hero.LegsTop = 1.0f; hero.BodyTop = 0.5f; // нарушение порядка поясов
    var ex = Assert.Throws<ArgumentException>(() => BuildWith(hero));
    Assert.That(ex.Message, Does.Contain("LegsTop"));
}

[Test]
public void Validate_SlideProfileAboveGunnerMuzzle_Throws()
{
    var hero = ScriptableObject.CreateInstance<HeroConfig>();
    // NB (QA2/QD3): у свежего MobConfig ProjectileRadius = 0 (чейзер-дефолты),
    // поэтому берём 1.0: 1.0 + 0 >= MuzzleHeight(0.95) — правило D5 нарушено,
    // при этом 1.0 <= Hero.BodyTop (1.35) — прочие правила молчат.
    hero.SlideProfileTop = 1.0f;
    var ex = Assert.Throws<ArgumentException>(() => BuildWith(hero));
    Assert.That(ex.Message, Does.Contain("SlideProfileTop"));
}
```

  где `BuildWith(hero)` — существующий локальный хелпер `ConfigTests`
  (создаёт остальные SO дефолтными и зовёт `SimConfigBuilder.Build`); если
  хелпера нет — добавить по образцу `Build_DefaultAssets_ProducesValidConfig`.
- [ ] **Step 2:** добавить ПОЛЯ (без валидации) в SO + sim-структуры + маппинг
  `SimConfigBuilder` (иначе тесты не компилируются — RED-дисциплина);
  R-FILTER `ConfigTests` → **FAIL двух новых ассертов** (исключение не
  бросается).
- [ ] **Step 3 (GREEN-1):** SO-поля с `[Range]`: `LegsTop/BodyTop/HeadTop
  [Range(0.05f, 5f)]`, мульты `[Range(0f, 5f)]`, `MuzzleHeight/
  SlideMuzzleHeight [Range(0f, 5f)]`, `MaxAimHeight [Range(1f, 6f)]`,
  `SwingLeadFactor [Range(0f, 2f)]`, `SwingLeadMaxMeters [Range(0f, 6f)]`.
  Маркер-дисциплина: последним полем `MobConfig` объявить `SwingLeadMaxMeters`
  + `// sync-marker key — keep LAST` (Hero-маркер появится в Т2).
- [ ] **Step 4 (GREEN-2):** валидация через `ValidateZones` + правила выше;
  `TestConfigs.Default()` — те же значения; `AssertHeroEqual`/`AssertMobEqual`
  дополнить всеми новыми полями; в `Build_DefaultAssets_MatchesTestConfigsBaseline`
  расширить блок копирования полей ганнера (A3: свежий `MobConfig` несёт
  чейзер-дефолты — gunner-слот собирается пофейльно без этого).
- [ ] **Step 5:** R-FILTER `ConfigTests` → PASS; R-TEST → 93 + новые, 0 failed
  (golden НЕ меняется — конфиг-поля ещё никем не читаются).
- [ ] **Step 6:** R-COMMIT `feat(app-n6g): Т1 — конфиг зон поражения и дул`.

### Task Т2: конфиг-поля Буст/слайд/прицел/разброс

**Files:** те же + `client/Assets/Scripts/Data/WeaponConfig.cs`.

**Interfaces:**
- Produces: `HeroSimConfig.{StaminaMax 90, DashStaminaCost 48,
  SlideStaminaCost 13, LinkedDashStaminaCost 16, StaminaRegenPerSec 22,
  StaminaRegenDelay 0.72, SlideSpeed 13.5, SlideDuration 0.52,
  SlideSteerRadPerSec 1.2, SlideMinSpeedFrac 0.75, RunUpSeconds 1.18,
  RunUpDecayMult 3.0, SlideBufferWindow 0.15, LinkWindowSeconds 0.25,
  PostDashSlideWindow 0.32, SlideWallStopDot 0.7, RicochetRetention 0.8,
  AimMoveSpeedFrac 0.8, AimSlideSpeedMult 0.5, AimSettleSeconds 0.5}`;
  `WeaponSimConfig.{CanFireWhileSlide true, SpreadRunMult 1.5,
  SpreadSlideMult 2.0, RunSpreadSpeedFrac 0.5}`.
- Валидация: `StaminaMax > 0`; `StaminaRegenPerSec > 0`; цены `> 0` и
  `≤ StaminaMax`; `LinkedDashStaminaCost ≤ DashStaminaCost`; `SlideSpeed > 0`;
  `SlideDuration > 0`; `RunUpSeconds > 0`; `RunUpDecayMult ≥ 0`;
  `SlideMinSpeedFrac ∈ (0,1]`; `SlideWallStopDot ∈ [-1,1]`;
  `RicochetRetention ∈ [0,1]`; `AimMoveSpeedFrac ∈ (0,1]` и **строго
  `> SlideMinSpeedFrac`** (D15); `AimSlideSpeedMult ∈ (0,1]`;
  `AimSettleSeconds > 0`; `SpreadRunMult/SpreadSlideMult ≥ 1`;
  `RunSpreadSpeedFrac ∈ [0,1]`; окна ≥ 0.
- Маркеры: `HeroConfig` — `AimSettleSeconds` последним + keep-LAST;
  `WeaponConfig` — `RunSpreadSpeedFrac` последним + keep-LAST.

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void Validate_ZeroStaminaRegen_Throws()
{
    var hero = ScriptableObject.CreateInstance<HeroConfig>();
    hero.StaminaRegenPerSec = 0f;
    var ex = Assert.Throws<ArgumentException>(() => BuildWith(hero));
    Assert.That(ex.Message, Does.Contain("StaminaRegenPerSec"));
}

[Test]
public void Validate_AimFracNotAboveSlideFrac_Throws()
{
    var hero = ScriptableObject.CreateInstance<HeroConfig>();
    hero.AimMoveSpeedFrac = hero.SlideMinSpeedFrac; // равенство — тоже нарушение (строгое >)
    var ex = Assert.Throws<ArgumentException>(() => BuildWith(hero));
    Assert.That(ex.Message, Does.Contain("AimMoveSpeedFrac"));
}
```

- [ ] **Step 2:** поля-заглушки → R-FILTER `ConfigTests` → FAIL ассертов.
- [ ] **Step 3 (GREEN):** `[Range]`-диапазоны, накрывающие дефолты
  (`StaminaMax [Range(1,300)]`, `SlideSpeed [Range(0.1,40)]`,
  `AimSettleSeconds [Range(0.05,2)]` и т.п.), валидация, lockstep
  `TestConfigs`/`Assert*Equal`.
- [ ] **Step 4:** R-FILTER `ConfigTests` → PASS; R-TEST → 0 failed.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т2 — конфиг Буста/слайда/прицела`.

### Task Т3: RNG-split `_spreadRng`/`_waveRng`

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`,
  `client/Assets/Scripts/Simulation/Core/WorldSave.cs`,
  `client/Assets/Scripts/Simulation/Combat/WeaponSystem.cs`,
  `client/Assets/Scripts/Simulation/AI/WaveSystem.cs`
- Modify: `client/Assets/Tests/EditMode/DeterminismTests.cs`,
  `client/Assets/Tests/EditMode/WorldLifecycleTests.cs`

**Interfaces:**
- Produces: `internal ref Unity.Mathematics.Random SpreadRng` / `WaveRng`
  (потребители: `WeaponSystem` Т15, `WaveSystem` — сразу). Сид:
  `uint folded = (uint)(seed ^ (seed >> 32));`
  `_spreadRng = new Random(Fold(folded ^ 0xB5297A4Du));`
  `_waveRng = new Random(Fold(folded ^ 0x68E31DA4u));` — **u-суффиксы (PA9)**,
  `Fold(x) => x == 0 ? 0x9E3779B9u : x` (ноль-guard Э1).
- **Удаляются:** `_rng`, `internal ref Random Rng`, `WorldSave.Rng`, их строки
  в `SaveState`/`RestoreState`/`StateHash` (PA16/PC9) — потребителей не
  остаётся. Хеш-порядок: `tick → _spreadRng.state → _waveRng.state → …`.
  Док-комментарии `SimulationWorld` («single shared Random») и `WaveSystem`
  («w.Rng.NextFloat») переписать под два потока.

- [ ] **Step 1 (RED):** в `DeterminismTests.cs` (правки QA9/QD4: короткая
  жизнь снаряда — пули не долетают до кольца спавна и не мешают счёту HP/
  слотов; сравнение — на ТИКЕ спавна волны, до расхождения AI от чужих
  entity-id; чтение — существующий internal-массив `w.Mobs`, QC5):

```csharp
[Test]
public void SpreadDrawDoesNotShiftWaves()
{
    // Same seed; world A fires for 100 ticks, world B stays idle.
    // Split streams: composition/positions of the FIRST wave must match at spawn tick.
    var cfg = TestConfigs.Default();
    cfg.Weapon.ProjectileLifetime = 0.2f; // ~7 m, never reaches the spawn ring (QA9)
    var a = new SimulationWorld(7, cfg);
    var b = new SimulationWorld(7, cfg);
    var fire = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
    var idle = new SimInput();
    int spawnTick = -1;
    for (int i = 0; i < 100; i++)
    {
        a.Tick(fire); b.Tick(idle);
        if (spawnTick < 0 && b.MobCount > 0) { spawnTick = i; break; } // QD4: compare AT spawn
    }
    Assert.GreaterOrEqual(spawnTick, 0, "wave never spawned");
    Assert.AreEqual(b.MobCount, a.MobCount);
    for (int m = 0; m < a.MobCount; m++)
    {
        Assert.AreEqual(b.Mobs[m].Type, a.Mobs[m].Type);
        Assert.AreEqual(b.Mobs[m].Pos.x, a.Mobs[m].Pos.x, 1e-4f);
        Assert.AreEqual(b.Mobs[m].Pos.y, a.Mobs[m].Pos.y, 1e-4f);
    }
}
```
- [ ] **Step 2:** R-FILTER `DeterminismTests` → FAIL (общий поток сдвинут
  стрельбой).
- [ ] **Step 3 (GREEN):** два потока + удаление старого + `WorldSave`/хеш;
  R-GOLDEN (перепин №1 — сид-схема изменилась).
- [ ] **Step 4:** R-FILTER `DeterminismTests` + `WorldLifecycleTests` → PASS;
  R-TEST → 0 failed.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т3 — раздельные RNG-потоки оружия и волн`.

**Гейт фазы Г1:** R-TEST полный (total ≈ 98: 93 + Т1/Т2/Т3-тесты — QD17,
сверять атрибут `total` в xml); push ветки; jsonl-chore (QB10);
`bd note app-n6g "Г1 done: тесты N, golden <хеш>"`; `bd close` сабтаска Г1.

---

## Фаза Г2 — снаряды и зоны поражения (Т4–Т8)

### Task Т4: 3D-поля снаряда + сигнатура спавна

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs`,
  `.../Core/SimulationWorld.cs`, `.../Combat/ProjectileSystem.cs`,
  `.../Combat/WeaponSystem.cs`, `.../AI/MobAiSystem.cs`
- Create: `client/Assets/Tests/EditMode/ProjectileHeightTests.cs` (+ `.meta`)
- Modify: `client/Assets/Tests/EditMode/ProjectileTests.cs` (7 вызовов),
  `WorldLifecycleTests.cs:46`, `DeathTests.cs:54`

**Interfaces:**
- Produces: `ProjectileState + Height, PrevHeight, VelZ` (float; в
  `HashProjectile` и `WorldSave` — структуры целиком, авто);
  `SpawnProjectile(ProjectileOwner owner, float2 pos, float2 vel,
  float height, float velZ, float damage, float radius, float ttl)` +
  тест-двойник `SpawnProjectileForTest(...)` той же формы.
- Семантика: в ветке «без попадания» `ProjectileSystem`:
  `proj.PrevHeight = proj.Height; proj.Height += proj.VelZ * dt;` рядом с
  `Pos = target`. `WeaponSystem` временно спавнит `(hero.MuzzleHeight, 0f)`;
  ганнер — `(cfg.MuzzleHeight, 0f)`. Девять тест-вызовов — `height: 1f,
  velZ: 0f` (PA8-счёт).

- [ ] **Step 1 (RED):** `ProjectileHeightTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ProjectileHeightTests
    {
        [Test]
        public void Projectile_WithVelZ_AdvancesHeightPerTick()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.SpawnProjectileForTest(ProjectileOwner.Player,
                new float2(0f, 0f), new float2(10f, 0f),
                height: 1f, velZ: -3f, damage: 1f, radius: 0.1f, ttl: 5f);
            w.Tick(new SimInput());
            var p = w.GetProjectileForTest(0);
            Assert.AreEqual(1f - 3f * SimulationWorld.TickDt, p.Height, 1e-5f);
            Assert.AreEqual(1f, p.PrevHeight, 1e-5f);
        }
    }
}
```

  (геттер `GetProjectileForTest(int)` — добавить рядом с `SetProjectileForTest`,
  если отсутствует.)
- [ ] **Step 2:** поля/сигнатуры-заглушки + 9 call-sites → R-FILTER
  `ProjectileHeightTests` → FAIL ассерта высоты.
- [ ] **Step 3 (GREEN):** интеграция высоты; `HashProjectile` + 3 поля;
  R-GOLDEN (поля вошли в хеш).
- [ ] **Step 4:** R-TEST полный → 0 failed (рефлекшн-тест сам форсирует хеш).
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т4 — высота снаряда в состоянии и
  спавне` (+ `.meta` нового тест-файла).

### Task Т5: перебор кандидатов min-scan + скретч (refactor-фаза Т4)

**Files:** Modify `.../Combat/ProjectileSystem.cs`, `.../Core/SimulationWorld.cs`.

**Interfaces:**
- Produces: `readonly (float t, int kind, int index)[] _projCandidates` +
  `internal … ProjCandidates => _projCandidates` (конвенция `_sepForces`/
  `SepForces` — QB13) — преаллоцированный скретч размером **`Arena.MaxMobs + 3`** (барьер + игрок +
  пол — PA15), комментарий-исключение из `SaveState`/`StateHash` по образцу
  `_sepForces`. Семантика прохода: собрать кандидатов (0 = барьер, 1..N =
  мобы по индексу, дальше игрок; пол добавит Т7) → повторный выбор
  минимального `t` строгим `<` среди неисключённых (тай-брейк — меньший слот,
  bit-в-бит Э1) → кандидат может быть «отвергнут» (Т6: высота) — исключается
  и скан повторяется. Сортировок/делегатов нет (AllocationTests).

- [ ] **Step 1 (BASELINE — не RED, PD22):** R-FILTER `ProjectileTests` +
  `DeterminismTests` → зелёные (фиксация).
- [ ] **Step 2:** рефактор прохода. Ветка «отвергнуть и продолжить» до Т6 —
  мёртвая (активирует первый высотный тест Т6; осознанно).
- [ ] **Step 3:** R-FILTER `ProjectileTests`, `DeterminismTests` (**golden НЕ
  меняется** — поведение бит-в-бит), `AllocationTests` → PASS; R-TEST полный.
- [ ] **Step 4:** R-COMMIT `refactor(app-n6g): Т5 — перебор кандидатов
  min-scan без сортировки`.

### Task Т6: высотный хитрег + зоны + урон с множителями (implementer: opus)

**Files:**
- Modify: `.../Core/SimStates.cs` (**`public enum HitZone : byte { None, Legs,
  Body, Head }` — в Core рядом с `MobType` (PA1/PD2/PB3)** +
  `MatchStats.HeadshotKills`), `.../Core/SimEvents.cs`, `.../Core/Geometry.cs`,
  `.../Core/SimulationWorld.cs`, `.../Combat/ProjectileSystem.cs`,
  `.../AI/MobAiSystem.cs`
- Create: `client/Assets/Scripts/Simulation/Combat/HitZones.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/HitZoneTests.cs` (+ `.meta`)
- Modify: `ProjectileHeightTests.cs`, `GeometryTests.cs`, `EventTests.cs`

**Interfaces:**
- Produces:

```csharp
// Core/SimStates.cs (рядом с MobType)
public enum HitZone : byte { None = 0, Legs = 1, Body = 2, Head = 3 }

// Combat/HitZones.cs — СКАЛЯРНЫЕ сигнатуры (одно тело на Hero и мобов, PC5);
// класс INTERNAL: вне сборки никому не нужен, Presentation читает SimEvent.Zone (QB14)
internal static class HitZones
{
    // h клампится в [0, headTop]: «чирк по макушке» = Head, подрез у пола = Legs (D3)
    public static HitZone Classify(float h, float legsTop, float bodyTop, float headTop);
    public static float MultFor(HitZone zone, float legsMult, float bodyMult, float headMult);
    // пересечение интервала высот снаряда на хорде с [-radius, top + radius] (M14/C8)
    public static bool Overlaps(float hEnter, float hExit, float radius, float top);
}

// Core/Geometry.cs — выход из круга для клипа высоты по хорде (PD10).
// NB: решает ту же квадратику, что SegmentCircle — ДВЕ реализации намеренно
// (SweepArena — горячий путь, golden Т5 бит-в-бит); менять только парой (QC18)
public static bool SegmentCircleInterval(float2 p0, float2 p1, float padR,
    float2 c, float cR, out float tEnter, out float tExit);

// Core/SimulationWorld.cs (+ HashStats явно — HeadshotKills, QA17/QB21)
internal void DamageMob(int index, float dmg, float2 pos, HitZone zone, float2 dir);
internal void DamagePlayer(float dmg, float2 pos, HitZone zone, float2 dir);
// Emit — ДВА optional-параметра, 11 существующих call-site'ов не трогаются (PC16/QC20):
internal void Emit(SimEventKind kind, float2 pos, int entityId, MobType mobType,
    float amount, ProjectileOwner owner = ProjectileOwner.Player,
    HitZone zone = HitZone.None, float2 hitDir = default);

// Core/SimEvents.cs
public struct SimEvent { /* + */ public HitZone Zone; public float2 HitDir; }
```

- Семантика: `ProjectileSystem` считает `hEnter/hExit` по `[tEnter, tExit]`
  свипа; `!Overlaps` → кандидат исключается, скан продолжается (дальняя цель
  достижима — M5); множитель применяется ДО эмита (`Amount` = урон после
  множителя — C28); `HeadshotKills` — добивающее `Zone == Head`, инкремент
  через хелпер с гвардом `Alive` (B13). Кулак чейзера
  (`MobAiSystem`): `zone = Body`, множитель НЕ применяется,
  `dir = math.normalizesafe(p.Pos - m.Pos, new float2(1,0))`. Рябь сигнатур:
  `KillPlayerForTest` (PD9), док `SimEvents` «unused (0)» — обновить.

- [ ] **Step 1 (RED, GeometryTests):**

```csharp
[Test]
public void SegmentCircleInterval_ReturnsEnterAndExit()
{
    bool hit = Geometry.SegmentCircleInterval(
        new float2(-2f, 0f), new float2(2f, 0f), 0f,
        float2.zero, 1f, out float tEnter, out float tExit);
    Assert.IsTrue(hit);
    Assert.AreEqual(0.25f, tEnter, 1e-4f); // вход в круг на x=-1
    Assert.AreEqual(0.75f, tExit, 1e-4f);  // выход на x=+1
}
// + кейсы: касательная (tEnter≈tExit), старт внутри круга (tEnter=0), промах (false)
```

- [ ] **Step 2 (RED, пакет).** `HitZoneTests.cs` + `ProjectileHeightTests.cs`.
  Геометрия Д15 (числа НЕ менять — посчитаны по входу свипа, PA6):

```csharp
public class HitZoneTests
{
    [Test]
    public void Zones_ClassifyLegsBodyHead_AtBoundaries() // PD11
    {
        var c = TestConfigs.Default().Chaser;
        Assert.AreEqual(HitZone.Legs, HitZones.Classify(c.LegsTop - 1e-4f, c.LegsTop, c.BodyTop, c.HeadTop));
        Assert.AreEqual(HitZone.Body, HitZones.Classify(c.LegsTop,         c.LegsTop, c.BodyTop, c.HeadTop));
        Assert.AreEqual(HitZone.Body, HitZones.Classify(c.BodyTop - 1e-4f, c.LegsTop, c.BodyTop, c.HeadTop));
        Assert.AreEqual(HitZone.Head, HitZones.Classify(c.BodyTop,         c.LegsTop, c.BodyTop, c.HeadTop));
        Assert.AreEqual(HitZone.Head, HitZones.Classify(c.HeadTop + 0.2f,  c.LegsTop, c.BodyTop, c.HeadTop)); // кламп
        Assert.AreEqual(HitZone.Legs, HitZones.Classify(-0.05f,            c.LegsTop, c.BodyTop, c.HeadTop)); // кламп
    }

    [Test]
    public void GunnerHeadshot_IsOneshot() // Д15: 12 × 1.7 = 20.4 ≥ 20 — через фикстуру
    {
        var cfg = TestConfigs.Open();
        Assert.GreaterOrEqual(cfg.Weapon.Damage * cfg.Gunner.HeadDamageMult, cfg.Gunner.MaxHp);
        // спавн ганнера, снаряд в пояс головы, один тик → MobCount == 0,
        // MobDied.Zone == Head, Stats.HeadshotKills == 1 (логика «добивающее» — QD13)
    }

    [Test] public void ChaserHeadshot_TwoShots() { /* 30 HP: после 1-го хед-хита жив, после 2-го мёртв */ }
    [Test] public void LegsHit_AmountIsLegsMult() { /* Amount == Damage * LegsDamageMult (C28+PD11) */ }
    [Test] public void Fist_ZoneBody_NoMult() { /* телеграф-удар: PlayerDamaged.Zone==Body, урон == ContactDamage */ }
    [Test] public void Hit_Amount_IsPostMultiplier() { /* ProjectileHit.Amount == урон ПОСЛЕ множителя */ }
}
```

  В `ProjectileHeightTests`:

```csharp
[Test]
public void GunnerHeadOverCrowd_HitFromFarChaser() // Д15-геометрия + M5 (PD8)
{
    // чейзер (6.5, 0) — отвергается по высоте (вход свипа x=5.88: h=2.37 > 1.85+0.12),
    // ганнер (9, 0) — умирает от попадания в пояс головы [2.70, 3.50]
    var w = SpawnPair(chaserX: 6.5f, gunnerX: 9f);
    FireAimedFrom(w, origin: float2.zero, muzzleH: 1f, targetH: 3.1f, targetX: 9f);
    RunUntilProjectilesDie(w);
    Assert.AreEqual(1, w.MobCount);                       // чейзер жив
    Assert.AreEqual(MobType.Chaser, w.GetMobForTest(0).Type);
}

[Test]
public void CloseChaser_ScreensGunnerHead()
{
    // чейзер (2, 0): траектория на входе свипа h≈1.32 < 1.97 — чейзер съедает выстрел
    var w = SpawnPair(chaserX: 2f, gunnerX: 9f);
    FireAimedFrom(w, origin: float2.zero, muzzleH: 1f, targetH: 3.1f, targetX: 9f);
    RunUntilProjectilesDie(w);
    Assert.AreEqual(2, w.MobCount); // никто не умер: чейзер ранен (Body/Head по клампу), ганнер цел
}

[Test] public void Graze_AtHeadTopPlusRadius_HitsAsHead() { /* h = HeadTop + Radius - 1e-4 ⇒ хит, Zone==Head */ }
[Test] public void EqualT_TieBreaksLowerIndex() { /* два моба, равный t ⇒ побеждает меньший слот (C7) */ }
```

  Хелперы — в **`TestWorlds`** (существующий дом shared-фикстур, QC8; НЕ
  локальные статики — потребители: `ProjectileHeightTests` Т6/Т7/Т15,
  `HitZoneTests` Т6, `SlideTests` Т11): `TestWorlds.SpawnMobsAt(world,
  params (MobType type, float2 pos)[])`, `TestWorlds.FireAimed3D(world,
  float2 origin, float muzzleH, float2 targetXY, float targetH)` (тестовый
  спавн по 3D-нормали × `ProjectileSpeed`), `TestWorlds.
  RunUntilProjectilesDie(world, maxTicks = 120)`. `HitZoneTests.cs` — шапка
  `using Ring.Simulation.Core; using Ring.Simulation.Combat;` (QA17).
- [ ] **Step 3:** заглушки типов → R-FILTER `GeometryTests`,`HitZoneTests`,
  `ProjectileHeightTests` → FAIL ассертов.
- [ ] **Step 4 (GREEN):** по Interfaces выше; R-GOLDEN.
- [ ] **Step 5:** R-TEST полный → 0 failed.
- [ ] **Step 6:** R-COMMIT `feat(app-n6g): Т6 — зоны поражения, множители,
  события с зоной` (+ `.meta` двух новых файлов).

### Task Т7: пол-кандидат

**Files:** Modify `.../Combat/ProjectileSystem.cs`; Test
`ProjectileHeightTests.cs`, `EventTests.cs`.

**Interfaces:**
- Семантика: при `VelZ < 0` пол — кандидат с
  `t_floor = (proj.Radius - proj.Height) / (proj.VelZ * dt)` (клип в `[0,1]`),
  участвует в общем min-scan; исход — `ProjectileBlocked`:
  `Amount = высота контакта`, **пол → `HitDir = float2.zero`; стена →
  `HitDir = normal` из `SweepArena`** (D12/C5 — никаких «≈0»-эвристик).

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void FloorHit_BlocksAtFloorPoint_MobBehindUnharmed() // D4
{
    // наклонный вниз выстрел: касание пола на ~4 м, моб на 6 м — жив
    var w = SpawnSingleMob(MobType.Chaser, x: 6f);
    FireAimedFrom(w, origin: float2.zero, muzzleH: 1f, targetH: 0f, targetX: 4f);
    RunUntilProjectilesDie(w);
    Assert.AreEqual(1, w.MobCount);
    Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked));
    // пол: HitDir == zero, Amount ≈ Radius
}

[Test] public void WallBlock_CarriesNormalAndHeight() { /* стена: HitDir != zero, Amount == высота контакта */ }
```

- [ ] **Step 2:** R-FILTER → FAIL. **Step 3 (GREEN)** + R-GOLDEN.
- [ ] **Step 4:** R-FILTER + `EventTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т7 — пол как кандидат хитрега`.

### Task Т8: `SimInput.AimHeight/AimHeld/SlideRequested` + `Sanitize`

**Files:** Modify `.../Core/SimInput.cs`, `.../Core/SimulationWorld.cs`;
Test `DeterminismTests.cs`.

**Interfaces:**
- Produces: `SimInput + float AimHeight; bool AimHeld; bool SlideRequested`.
  `SimInputFrame.ForTick`: `SlideRequested` — edge только на тик 0 (паттерн
  `DashRequested`); `AimHeld` — уровень на все тики. `Sanitize`: не-конечное
  `AimHeight` → `Hero.MuzzleHeight`; кламп `[0, MaxAimHeight]`; при
  `!AimHeld` симуляция высоту игнорирует (потребитель — Т15).

- [ ] **Step 1 (RED):** в `HostileInput_StateStaysFinite_AndDeterministic`
  добавить в генерацию враждебного ввода `AimHeight = float.NaN` /
  `float.PositiveInfinity`, `AimHeld = true`; наблюдаемый кламп — через
  тест-шов `internal SimInput SanitizeForTest(in SimInput raw)` (обёртка
  `Sanitize`; `InternalsVisibleTo` уже открыт — QA6/QD14):

```csharp
[Test]
public void Sanitize_ClampsAimHeight_AndMapsNaNToMuzzle()
{
    var cfg = TestConfigs.Open();
    var w = new SimulationWorld(1, cfg);
    var over = w.SanitizeForTest(new SimInput { AimHeld = true, AimHeight = cfg.Hero.MaxAimHeight + 5f });
    Assert.AreEqual(cfg.Hero.MaxAimHeight, over.AimHeight, 1e-5f);  // clamp (fixture expr - PA2)
    var nan = w.SanitizeForTest(new SimInput { AimHeld = true, AimHeight = float.NaN });
    Assert.AreEqual(cfg.Hero.MuzzleHeight, nan.AimHeight, 1e-5f);   // NaN -> muzzle height
}
```
- [ ] **Step 2:** R-FILTER `DeterminismTests` → FAIL/компиляция → заглушки →
  FAIL. **Step 3 (GREEN)** — golden НЕ меняется (высоту никто не читает).
- [ ] **Step 4:** R-FILTER → PASS; R-TEST. **Step 5:** R-COMMIT
  `feat(app-n6g): Т8 — прицельные входы и санитайз`.

**Гейт фазы Г2:** R-TEST полный (total ≈ +16 к Г1 — QD17); push;
jsonl-chore; bd note (golden текущий); `bd close` сабтаска Г2.

---

## Фаза Г3 — Буст-мувмент (Т9–Т14)

### Task Т9: Буст-ядро

**Files:**
- Modify: `.../Core/SimStates.cs`, `.../Core/SimulationWorld.cs`
  (**`HashPlayer` — явно, PB10**), `.../Core/SimEvents.cs` (`StaminaDenied`),
  `.../Movement/PlayerMovementSystem.cs`
- Create: `client/Assets/Tests/EditMode/StaminaTests.cs` (+ `.meta`)
- Modify: `HotTweakTests.cs` (рефлексивный кламп-проход — QC7)

**Interfaces:**
- Produces: `PlayerState + float Stamina, StaminaRegenDelayTimer`;
  конструктор мира: `Stamina = config.Hero.StaminaMax` (рядом с `Hp`);
  смерть: `Stamina` замораживается, `StaminaRegenDelayTimer = 0`;
  `SimEventKind.StaminaDenied` (`Pos` = игрок, `Amount` = недостающая цена).
  Дэш: гейт `Stamina ≥ DashStaminaCost` (иначе — Denied-событие не чаще
  1 раза на взвод буфера), списание, `StaminaRegenDelayTimer =
  StaminaRegenDelay`; реген `+StaminaRegenPerSec * dt` при
  `StaminaRegenDelayTimer == 0` и вне дэша (условие «и вне слайда» добавит
  Т10), кламп `StaminaMax`. **Сигнатура `Update` меняется здесь:**
  `bool` → структ `MovementResult { DashStarted, DashDenied }` (расширяется
  Т10/Т12 — единый контракт «система → мир», QC12); call-site
  `SimulationWorld.Tick:81` эмитит `PlayerDashed`/`StaminaDenied` по полям.
  `ApplyConfig`-клампы: `Stamina → [0, StaminaMax]`,
  `StaminaRegenDelayTimer → [0, StaminaRegenDelay]`.
  **Рефлексивный кламп-проход — в `HotTweakTests`** (дом `ApplyConfig` —
  QC7; НЕ в `WorldLifecycleTests`): выставить все float-поля `PlayerState` =
  1e6 (канон-шов QA1) → `ApplyConfig(уменьшенные максимумы)` → рефлексией
  проверить каждое поле против карты `поле → максимум` (локальный словарь
  теста; поле без записи в карте → падение с понятным сообщением).
  **Карта пополняется в Т10 (`SlideTimer` и др.), Т11 (`LinkWindowTimer`),
  Т12 (`DashSpeedCur`), Т14 (`AimSettleTimer`) — обязательной строкой их
  GREEN-шагов (QC7).** `RegenFixture()` заводится в Т10 (первый потребитель
  — QB12).

- [ ] **Step 1 (RED):** `StaminaTests.cs`:

```csharp
public class StaminaTests
{
    static SimInput Dash => new SimInput { MoveDir = new float2(1,0), DashRequested = true };

    [Test]
    public void StartsAtFullStamina()
    {
        var cfg = TestConfigs.Open();
        Assert.AreEqual(cfg.Hero.StaminaMax, new SimulationWorld(1, cfg).Player.Stamina);
    }

    [Test]
    public void Dash_CostsStamina()
    {
        var cfg = TestConfigs.Open();
        var w = new SimulationWorld(1, cfg);
        w.Tick(Dash);
        Assert.AreEqual(cfg.Hero.StaminaMax - cfg.Hero.DashStaminaCost, w.Player.Stamina, 1e-3f);
    }

    [Test]
    public void Dash_InsufficientStamina_DeniedWithEvent()
    {
        var cfg = TestConfigs.Open();
        var w = new SimulationWorld(1, cfg);
        var p = w.Player;                                 // canon test-seam (QA1)
        p.Stamina = cfg.Hero.DashStaminaCost - 1f;
        w.SetPlayerForTest(p);
        w.Tick(Dash);
        Assert.AreEqual(0, w.Stats.DashesUsed);
        Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.StaminaDenied));
        // Amount события = недостающая цена (QD8-ассерт §3.4)
    }

    [Test]
    public void Regen_WaitsDelayThenRefills()
    {
        var cfg = TestConfigs.Open();
        var w = new SimulationWorld(1, cfg);
        w.Tick(Dash);
        int delayTicks = (int)math.ceil(cfg.Hero.StaminaRegenDelay / SimulationWorld.TickDt);
        for (int i = 0; i < delayTicks - 2; i++) w.Tick(new SimInput());
        float beforeRegen = w.Player.Stamina;               // задержка ещё идёт
        for (int i = 0; i < 30; i++) w.Tick(new SimInput());
        Assert.Greater(w.Player.Stamina, beforeRegen);      // реген пошёл
    }

    // Regen_FrozenDuringSlide_OnFixture — пишется в Т10 (слайда ещё нет; QB12).
    // Точечный HotTweak-тест НЕ заводится: кламп покрывает рефлексивный
    // проход в HotTweakTests (QC7).
}
```

- [ ] **Step 2:** заглушки → R-FILTER `StaminaTests` → FAIL ассертов
  (слайд-кейс — `[Ignore]` до Т10? НЕТ: `Regen_FrozenDuringSlide_OnFixture`
  писать в Т10, здесь только заготовку НЕ добавлять — тест-файл без него).
- [ ] **Step 3 (GREEN)** + рефлексивный кламп-проход; R-GOLDEN.
- [ ] **Step 4:** R-FILTER `StaminaTests`+`HotTweakTests`+`DashTests`+
  `WorldLifecycleTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т9 — ресурс Буст (код Stamina)`
  (+ `.meta` нового `StaminaTests.cs` — QB20).

### Task Т10: слайд — гейт, старт, тик, выход

**Files:**
- Modify: `.../Core/SimStates.cs` (`+ SlideTimer, SlideDir, SlideBufferTimer,
  RunUpTimer, PostDashSlideTimer` + `MatchStats.SlidesUsed`),
  `.../Movement/PlayerMovementSystem.cs`, `.../Core/SimulationWorld.cs`
  (смерть/клампы/`SlidesUsed`/`HashPlayer`/`HashStats`),
  **`.../Core/SimEvents.cs` (`PlayerSlideStarted` — PB5)**
- Create: `client/Assets/Tests/EditMode/SlideTests.cs` (+ `.meta`)
- Modify: `StaminaTests.cs` (+`Regen_FrozenDuringSlide_OnFixture`)

**Interfaces:**
- Produces: слайд-механика §3.3 v5. **`LinkWindowTimer` заводится ЗДЕСЬ**
  (поле + декремент + кламп + смерть + карта клампов; открытие окна — тоже
  здесь; потребление/стеновое обнуление — Т11; QB3/QA12/QD9).
  **Контракт «система → мир» — единый структ (QC12), рикошет-поля заполнит
  Т12:**

```csharp
public struct MovementResult                       // заводится в Т9 (DashStarted, DashDenied),
{                                                  // расширяется Т10/Т12
    public bool DashStarted, DashDenied;           // Т9
    public bool SlideStarted, SlideDenied;         // Т10
    public bool Ricocheted; public float2 RicochetPos, RicochetNormal; // Т12
}
public static MovementResult Update(ref PlayerState p, in SimInput input, in SimConfig cfg);
// call-site единственный: SimulationWorld.Tick (:81) — эмитит события/статы по полям результата.
```

  Слайд — ЗВЕНО существующей if-цепочки `Update` (QC11: реальная структура
  — `if (DashTimer>0) … else if (дэш-старт) … else …`; отдельные if-блоки
  после цепочки запрещены — двойная запись `Vel` за тик):

```csharp
// timers (canonical math.max(0, t - dt); buffer = DashBufferTimer latch pattern):
p.SlideBufferTimer = input.SlideRequested ? hero.SlideBufferWindow
                                          : math.max(0f, p.SlideBufferTimer - dt);
p.PostDashSlideTimer = math.max(0f, p.PostDashSlideTimer - dt);   // B10
p.LinkWindowTimer   = math.max(0f, p.LinkWindowTimer - dt);       // QA12
// run-up accrues outside dash/slide, decays below threshold (M9/C32):
bool moving = math.length(p.Vel) >= hero.SlideMinSpeedFrac * hero.MaxSpeed;
if (p.DashTimer <= 0f && p.SlideTimer <= 0f)
    p.RunUpTimer = moving ? math.min(p.RunUpTimer + dt, hero.RunUpSeconds)
                          : math.max(0f, p.RunUpTimer - hero.RunUpDecayMult * dt);

bool slideGate = p.RunUpTimer >= hero.RunUpSeconds || p.PostDashSlideTimer > 0f;
if (p.DashTimer > 0f)                     { /* dash tick (unchanged + T12 ricochet) */ }
else if (p.DashBufferTimer > 0f && p.DashCooldown <= 0f
         && p.SlideTimer <= 0f)           // QD10: no dash start while sliding
{ /* dash start: Т9-гейт Буста; связка-скидка/обход кулдауна — Т11 */ }
else if (p.SlideTimer > 0f)               // slide tick — link of the SAME chain (QC11)
{
    p.SlideTimer = math.max(0f, p.SlideTimer - dt);
    float2 want = math.lengthsq(input.MoveDir) > 1e-6f ? math.normalize(input.MoveDir) : p.SlideDir;
    p.SlideDir = Geometry.RotateTowards(p.SlideDir, want, hero.SlideSteerRadPerSec * dt); // QC19
    p.Vel = p.SlideDir * hero.SlideSpeed;              // AimHeld-мульт — Т14
    if (p.SlideTimer <= 0f) p.LinkWindowTimer = hero.LinkWindowSeconds; // штатный выход (C22: вынос)
    MoveWithCollisions(...);                            // стеновое гашение — Т11
}
else if (p.SlideBufferTimer > 0f && slideGate)          // slide start
{
    if (p.Stamina >= hero.SlideStaminaCost)
    {
        p.Stamina -= hero.SlideStaminaCost;
        p.StaminaRegenDelayTimer = hero.StaminaRegenDelay;
        p.SlideTimer = hero.SlideDuration;
        p.SlideBufferTimer = 0f; p.PostDashSlideTimer = 0f; p.RunUpTimer = 0f; // M2
        p.SlideDir = math.lengthsq(input.MoveDir) > 1e-6f ? math.normalize(input.MoveDir)
            : math.lengthsq(p.Vel) > 1e-6f ? math.normalize(p.Vel)
            : math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f)); // D6
        result.SlideStarted = true;   // мир: PlayerSlideStarted + SlidesUsed++
    }
    else result.SlideDenied = true;   // мир: StaminaDenied (QD8; поле в MovementResult)
    // недобор: латч живёт своё окно, перепроверка каждый тик (C11)
}
else { /* обычный MoveTowards-моментум (существующая ветка) */ }
```

  `Geometry.RotateTowards(float2 from, float2 to, float maxRad)` — public в
  `Geometry` (дом 2D-математики; тестируется в `GeometryTests` — QC19).
  Реген Т9 дополняется условием «и `SlideTimer <= 0f`» (QD10). Смерть:
  `SlideTimer/SlideBufferTimer/RunUpTimer/PostDashSlideTimer/LinkWindowTimer
  = 0` рядом с `DashTimer` (M11/QD9). Пост-дэш окно ставится в тик перехода
  `DashTimer → 0` (C13). Клампы `ApplyConfig` (вкл. `LinkWindowTimer →
  LinkWindowSeconds`) + строки в карту рефлексивного теста Т9.
  `TestConfigs.RegenFixture()` (`SlideDuration 0.9, StaminaRegenDelay 0.3`
  — M16) заводится здесь + тест `Regen_FrozenDuringSlide_OnFixture`
  (перенос из Т9 — QB12). `StaminaDenied` слайда — не чаще 1 раза на взвод
  буфера (симметрия с дэшем).

- [ ] **Step 1 (RED):** `SlideTests.cs` — 10 тестов (семантика — по одному
  ассерт-ядру):
  - `Slide_RequiresRunUpOrPostDash` — запрос без разгона → `SlidesUsed == 0`;
    после `RunUpSeconds` бега → слайд стартует;
  - `PostDash_OpensSlideWindow` — дэш → конец → слайд в окне 0.32 без разгона;
  - `RunUp_DecaysBelowThreshold` — K отсчитывается от ПЕРВОГО тика, где
    `|Vel| < SlideMinSpeedFrac × MaxSpeed` (замерить в тесте: после снятия
    ввода скорость падает до порога ~2 тика — QA16), затем
    `RunUpTimer` падает на `RunUpDecayMult × K × dt` (фикстурно);
  - `Slide_InsufficientStamina_Denied` — Буст ниже цены слайда при полном
    разгоне: слайда нет, `StaminaDenied` с `Amount` = недостающее (QD8);
  - `Slide_ResetsRunUp_NoChain` (M2) — слайд→слайд немедленно → отказ;
  - `Slide_MutualExclusionWithDash` (C7) — слайд-запрос в дэше буферится,
    дэш-запрос в слайде не срабатывает;
  - `SlideDir_FallbackToAim_WhenIdle` (D6) — `MoveDir=0, Vel=0` →
    `SlideDir` к прицелу, слайд не «на месте»;
  - `Slide_SteerRateIsClamped` (PD7) — разворот инпута на 180° → за тик
    `SlideDir` повернулся ≤ `SlideSteerRadPerSec * dt` (по углу);
  - `Slide_ExitKeepsMomentum` (C22) — после `SlideDuration` `|Vel|` ==
    `SlideSpeed`, затем спад к `MaxSpeed`;
  - `Death_ClearsSlideState` (M11) — смерть в слайде → все слайд-таймеры 0
    (вкл. `LinkWindowTimer`);
  - `SlideBuffer_FiresWhenRegenCoversCost` (PD12, `RegenFixture`) — Буст чуть
    ниже цены, запрос → в течение буфер-окна реген добирает → слайд стартует;
  - `Regen_FrozenDuringSlide_OnFixture` (M16, перенос из Т9 — QB12);
  - `SlideStarted_EventCarriesPosAndDir` — `PlayerSlideStarted.Pos` = позиция
    старта, `HitDir == SlideDir` (payload §3.4 — QD13; `EventTests.cs` в
    Files).
- [ ] **Step 2:** заглушки → R-FILTER `SlideTests` → FAIL ассертов.
- [ ] **Step 3 (GREEN)** по сниппету; R-GOLDEN.
- [ ] **Step 4:** R-FILTER `SlideTests`+`StaminaTests`+`MovementTests`+
  `DashTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т10 — слайд: гейт разгона и
  жизненный цикл` (+ `.meta`).

### Task Т11: стена, окно связки, слайд-профиль

**Files:**
- Modify: `.../Movement/PlayerMovementSystem.cs` (**`MoveWithCollisions` →
  `out bool hit, out float2 normal, out float2 contact`** — первый контакт,
  QA8/QC13; discard'ы: `PlayerMovementSystem.UpdateDead`,
  `MobAiSystem.ApplyMotion` — B11),
  `.../Core/SimStates.cs` (`+ LinkWindowTimer`), `.../Combat/ProjectileSystem.cs`
  (профиль), `.../Core/SimulationWorld.cs` (кламп/`HashPlayer`)
- Modify: `SlideTests.cs`, `HitZoneTests.cs`, `StaminaTests.cs`

**Interfaces:**
- `MoveWithCollisions` → `out bool hit, out float2 normal, out float2
  contact` (**три out'а — QA8/QC13: `contact` нужен рикошет-событию Т12**;
  discard-вызовы `UpdateDead` и `MobAiSystem.ApplyMotion` — на три `_`).
- Гашение о стену — в слайд-ветке после `MoveWithCollisions`:
  `hit && math.dot(-normal, p.SlideDir) > hero.SlideWallStopDot` ⇒
  `SlideTimer = 0`, `Vel = math.normalizesafe(p.Vel, p.SlideDir) *
  hero.MaxSpeed`, `RunUpTimer = 0`, **`LinkWindowTimer = 0` — окно связки
  НЕ открывается и гасится, если уже было открыто в этом тике** (M3/QA12).
  Штатный выход открывает окно (Т10). Дэш в окне: цена
  `LinkedDashStaminaCost`, остаток `DashCooldown` игнорируется,
  `LinkWindowTimer = 0` (окно потребляется — C6), кулдаун ставится заново.
  Слайд-профиль: в `ProjectileSystem` для игрока при
  `player.SlideTimer > 0` верхняя граница = `SlideProfileTop` (вместо
  `HeadTop`) в `Overlaps` — ганнер-горизонталь (0.95) проходит выше.

- [ ] **Step 1 (RED):**
  - `WallStop_KillsSlide_NoLinkWindow` (M3) — слайд в стену «в лоб»:
    `SlideTimer == 0` и последующий дэш стоит ПОЛНУЮ цену;
  - `SlideAlongWall_Continues` — под острым углом слайд доживает таймер;
  - `LinkedDash_DiscountAndCooldownBypass_ConsumesWindow` (C6) — после
    штатного слайда дэш в окне: списано `LinkedDashStaminaCost`, кулдаун
    прежнего дэша не мешает; повторный дэш-запрос сразу после — **ассерт
    `Stats.DashesUsed` не растёт и Буст не меняется** (окно потреблено,
    кулдаун снова держит — QA14);
  - `PerfectChain_CostsExactly_StaminaMax` — дэш→слайд→связка-дэш→слайд:
    суммарно `DashStaminaCost + 2*SlideStaminaCost + LinkedDashStaminaCost`
    == `StaminaMax` (фикстурно; Д5 «ровно две связки»);
  - `GunnerShot_MissesSlidingHero` (M13) — горизонтальный снаряд на
    `Gunner.MuzzleHeight` пролетает над слайдящим (профиль 0.55+0.15 < 0.95);
  - `SlidingHero_HitOnlyBelowProfile` — снаряд на 0.3 — попадает, `Zone==Legs`.
- [ ] **Step 2:** R-FILTER → FAIL. **Step 3 (GREEN)**; R-GOLDEN.
- [ ] **Step 4:** R-FILTER `SlideTests`+`StaminaTests`+`HitZoneTests`+
  `MobAiTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т11 — стена, окно связки,
  слайд-профиль`.

### Task Т12: рикошет дэша

**Files:**
- Modify: `.../Core/SimStates.cs` (`+ DashSpeedCur`),
  `.../Movement/PlayerMovementSystem.cs`, `.../Core/SimulationWorld.cs`
  (`DashRicocheted`, кламп, `HashPlayer`), `.../Core/SimEvents.cs`
- Create: `client/Assets/Tests/EditMode/DashRicochetTests.cs` (+ `.meta`)
- Modify: `DashTests.cs` (перенацел `DashIntoObstacle_StopsAtSurface_NoTunnel`
  → `DashIntoObstacle_Ricochets_NoTunnel` — A9)

**Interfaces:**
- Семантика (условие — У ВЫЗЫВАЮЩЕГО, `math.reflect` напрямую, собственный
  `Geometry.Reflect` НЕ заводится — PC10): в дэш-ветке
  `p.Vel = p.DashDir * p.DashSpeedCur` (старт дэша: `DashSpeedCur =
  hero.DashSpeed`); после `MoveWithCollisions(out hit, out normal,
  out contact)` (`contact` — из Т11-сигнатуры, QA8):

```csharp
if (hit && math.dot(p.DashDir, normal) < 0f && !result.Ricocheted) // <=1 per tick (M8)
{
    p.DashDir = math.reflect(p.DashDir, normal);        // mirror (D9)
    p.DashSpeedCur *= hero.RicochetRetention;
    result.Ricocheted = true;                           // MovementResult (QC12)
    result.RicochetPos = contact; result.RicochetNormal = normal;
}
// reflected vector applies FROM THE NEXT tick (D16): contact tick is already
// resolved by the slide inside MoveWithCollisions
```

- [ ] **Step 1 (RED):** `DashRicochetTests.cs` — вся арифметика на ЯВНОЙ
  фикстуре в тестах (C14/PD5):

```csharp
static SimConfig Fixture()
{
    var cfg = TestConfigs.Open();
    cfg.Hero.DashSpeed = 30f; cfg.Hero.DashDuration = 0.09f; // 4 движ-тика = 4.0 м
    cfg.Hero.RicochetRetention = 0.8f;
    return cfg;
}
[Test] public void Ricochet_MirrorsDashDir_NextTick() { /* стена x=2: тик контакта — прижат, следующий — Vel.x < 0 (D16) */ }
[Test] public void Ricochet_AppliesRetention() { /* после отскока |Vel| == 30*0.8 (фикстура) */ }
[Test] public void Ricochet_KeepsIframes() { /* IframeTimer > 0 после отскока */ }
[Test] public void Ricochet_OncePerTick() { /* угол между стеной и препятствием: 1 событие на тик */ }
[Test] public void Ricochet_EmitsEventWithNormal() { /* DashRicocheted.HitDir == нормаль поверхности */ }
[Test] public void Dash_CoversFixtureMetres() { /* без препятствий: смещение за дэш == 4.0 м ± 1e-3 (M7-лестница видима) */ }
```

- [ ] **Step 2:** R-FILTER → FAIL. **Step 3 (GREEN)**; R-GOLDEN.
- [ ] **Step 4:** R-FILTER `DashRicochetTests`+`DashTests`+`MovementTests` →
  PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т12 — зеркальный рикошет дэша`
  (+ `.meta`).

### Task Т13: упреждающий замах чейзера

**Files:**
- Modify: `client/Assets/Scripts/Simulation/AI/Targeting.cs` (**Modify —
  файл существует, PD21**), `.../AI/MobAiSystem.cs`
- Modify: `client/Assets/Tests/EditMode/MobAiTests.cs`

**Interfaces:**
- Produces:

```csharp
// AI/Targeting.cs — рядом с AimWithLead (который НЕ переиспользуется: он про
// перехват снарядом заданной скорости — B12)
public static float2 PredictPos(float2 pos, float2 vel, float maxSpeed,
    float seconds, float factor, float maxLead)
{
    float2 lead = vel;
    float len = math.length(lead);
    if (len > maxSpeed) lead *= maxSpeed / len;          // дэш не выманивает (A4/D2)
    float2 offset = lead * (seconds * factor);
    float offLen = math.length(offset);
    if (offLen > maxLead) offset *= maxLead / offLen;    // кап дистанции упреждения
    return pos + offset;
}
```

  `MobAiSystem.UpdateChaser`: вход в `Telegraph` —
  `math.distance(m.Pos, Targeting.PredictPos(player.Pos, player.Vel,
  w.Config.Hero.MaxSpeed, cfg.TelegraphSeconds, cfg.SwingLeadFactor,
  cfg.SwingLeadMaxMeters)) ≤ cfg.AttackRange` — **сигнатура `UpdateChaser`
  НЕ меняется: `w` уже в параметрах и уже читает `w.Config.Hero.Radius`
  (QA11)**. Удар (re-validate через `TelegraphSeconds`) НЕ меняется —
  честный промах.

- [ ] **Step 1 (RED):** в `MobAiTests`:
  - `Chaser_TelegraphsAheadOfRunner_AndConnects` — игрок бежит на чейзера с
    `MaxSpeed`: телеграф стартует РАНЬШЕ входа в `AttackRange` и удар
    попадает (прогноз);
  - `Chaser_Standing_FarPlayer_NoTelegraph` (D8) — стоячий игрок на
    `AttackRange + SwingLeadMaxMeters + 0.5`; фикстура `cfg.Chaser.MaxSpeed
    = 0` (чейзер не доезжает и не превращает тест в гонку тиков — QA15) →
    телеграфа нет за 60 тиков;
  - `Chaser_DashDoesNotBaitFromAfar` (A4) — дэш (Vel = DashSpeed) в сторону
    чейзера с 6 м → телеграфа нет (лид клампится MaxSpeed);
  - `Chaser_LeadClampedByMaxMeters` — бег с MaxSpeed: вход в телеграф не
    дальше `AttackRange + SwingLeadMaxMeters` (фикстурно);
  - `SwingLeadZero_EntryTickEqualsE1Rule` (D9) — `SwingLeadFactor = 0`: тик
    входа в `Telegraph` == первый тик `dist ≤ AttackRange` (две симуляции,
    один seed, сравнение тиков).
- [ ] **Step 2:** R-FILTER `MobAiTests` → FAIL. **Step 3 (GREEN)**; R-GOLDEN.
- [ ] **Step 4:** R-FILTER `MobAiTests` целиком → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т13 — упреждающий замах чейзера`.

### Task Т14: прицельный режим в движении (кап, слайд-мульт, сведение)

**Files:** Modify `.../Core/SimStates.cs` (`+ AimSettleTimer`),
`.../Movement/PlayerMovementSystem.cs`, `.../Core/SimulationWorld.cs`
(кламп `[0, AimSettleSeconds]`, `HashPlayer`); Test `MovementTests.cs`,
`SlideTests.cs`.

**Interfaces:**
- Семантика: `AimSettleTimer = input.AimHeld
  ? math.min(t + dt, hero.AimSettleSeconds) : math.max(0f, t - 2f * dt)`
  (декэй ×2). Кап бега: целевая скорость `MoveTowards` при `AimHeld` =
  `hero.MaxSpeed * hero.AimMoveSpeedFrac` (дэша не касается). Слайд-тик:
  `p.Vel = p.SlideDir * hero.SlideSpeed *
  (input.AimHeld ? hero.AimSlideSpeedMult : 1f)` — с ТОГО ЖЕ тика (A11).
  **Смерть: `AimSettleTimer = 0` рядом с прочими; строка в карту клампов
  `HotTweakTests` (`→ AimSettleSeconds`) — QD9/QC7.**

- [ ] **Step 1 (RED):**
  - `AimHeld_CapsRunSpeed` — стационарная скорость под ПКМ ==
    `cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac` (фикстурно — PA5);
  - `AimReleased_RestoresMaxSpeed` (D8);
  - `AimHeld_SlowsSlide_SameTick` — ПКМ на тике N слайда: `|Vel|` на тике N ==
    `SlideSpeed * AimSlideSpeedMult`;
  - `RunUp_ReachableUnderAimCap` — `AimMoveSpeedFrac > SlideMinSpeedFrac` ⇒
    под капом разгон копится (слайд достижим);
  - `AimSettle_GrowsAndDecaysTwiceAsFast`.
- [ ] **Step 2:** R-FILTER → FAIL. **Step 3 (GREEN)**; R-GOLDEN.
- [ ] **Step 4:** R-FILTER `MovementTests`+`SlideTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т14 — кап и слайд-штраф
  прицельного режима`.

**Гейт фазы Г3:** R-TEST (total ≈ +32 к Г2 — QD17); push; jsonl-chore;
bd note; `bd close` сабтаска Г3.

---

## Фаза Г4 — режимы огня и golden (Т15–Т16)

### Task Т15: два режима огня в `WeaponSystem` (implementer: opus)

**Files:** Modify `.../Combat/WeaponSystem.cs`; Test
`ProjectileHeightTests.cs`, `WeaponTests.cs`.

**Interfaces:**
- Produces: **`public static class Spread`** (новый файл-сосед в
  `Simulation/Combat/Spread.cs` — `WeaponSystem` остаётся `internal`, а
  формулу читает и `CrosshairView` из `Ring.Presentation`; QA3/QB1/QC1/QD2):

```csharp
namespace Ring.Simulation.Combat
{
    /// Single home of the hip-fire spread formula: consumed by WeaponSystem
    /// (authoritative shots) and CrosshairView (honest reticle) — PC6.
    public static class Spread
    {
        public static float HipRadians(in WeaponSimConfig weapon, in PlayerState p,
            in HeroSimConfig hero)
        {
            float moveMult = p.SlideTimer > 0f ? weapon.SpreadSlideMult
                : math.length(p.Vel) >= weapon.RunSpreadSpeedFrac * hero.MaxSpeed
                    ? weapon.SpreadRunMult
                    : 1f;
            return (weapon.SpreadRad + p.RecoilOffset) * moveMult;
        }
    }
}
```

  Ветка выстрела (замена нынешних строк ~39–46; спека §3.2 v5 дословно):

```csharp
var hero = w.Config.Hero;                       // QC21: Update declares only cfg = w.Config.Weapon
float muzzleH = p.SlideTimer > 0f ? hero.SlideMuzzleHeight : hero.MuzzleHeight;
float a; float3 vel3;
if (input.AimHeld)
{
    float settle = p.AimSettleTimer / hero.AimSettleSeconds;         // [0..1]
    a = p.RecoilOffset + cfg.SpreadRad * (1f - settle);              // Д15: спрей всегда
    float2 baseDir2 = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
    float3 target3 = new float3(input.AimPoint, input.AimHeight);
    float3 muzzle3 = new float3(p.Pos + baseDir2 * cfg.MuzzleOffset, muzzleH);
    vel3 = math.normalizesafe(target3 - muzzle3, new float3(baseDir2, 0f)) * cfg.ProjectileSpeed;
}
else
{
    a = Spread.HipRadians(in cfg, in p, in hero);
    float2 dir2 = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
    vel3 = new float3(dir2 * cfg.ProjectileSpeed, 0f);               // горизонталь Э1
}
if (a > 0f)                                                          // draw в ОБОИХ режимах (v5)
{
    float angle = w.SpreadRng.NextFloat(-a, a);
    float2 rotated = Geometry.Rotate(vel3.xy, angle);                // вращение вокруг вертикали (K10)
    vel3 = math.normalizesafe(new float3(rotated, vel3.z), vel3) * cfg.ProjectileSpeed;
}
float2 dir2D = math.normalizesafe(vel3.xy, math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f)));
float horizSpeed = math.length(vel3.xy);
float2 spawnPos = p.Pos + dir2D * (cfg.MuzzleOffset + overshoot * horizSpeed);  // K9
float height = muzzleH + overshoot * vel3.z;
w.SpawnProjectile(ProjectileOwner.Player, spawnPos, vel3.xy, height, vel3.z,
    cfg.Damage, cfg.ProjectileRadius, cfg.ProjectileLifetime);
```

  Гейт `CanFireWhileSlide` — рядом с существующим `CanFireWhileDash`.
  `RecoilOffset` копится/распадается как в Э1 в обоих режимах.

- [ ] **Step 1 (RED):**
  - `AimedShot_HitsExactPoint_IncludingFloor` — сведён (`AimSettleTimer` =
    max тест-швом), `RecoilOffset = 0`: выстрел в точку пола →
    `ProjectileBlocked` в ней (±0.05);
  - `AimedShot_FullSpeed3D` (K10) — при угле и разбросе
    `|(Vel, VelZ)| == ProjectileSpeed` (1e-3);
  - `HipShot_HorizontalAtMuzzleHeight` — `VelZ == 0`, `Height == MuzzleHeight`;
  - `HipSpread_RunAndSlideMultipliers` (D8) — `Spread.HipRadians` на
    стоячем/бегущем/слайдящем: ×1 / ×`SpreadRunMult` / ×`SpreadSlideMult`,
    граница `RunSpreadSpeedFrac` включительно;
  - `FirstAimTick_SpreadNotZero` (C2) — тик 1 `AimHeld` c `SpreadRad > 0` ⇒
    эффективный разброс > 0 (через дисперсию направлений N выстрелов);
  - `AimedSpray_HasSpread` (Д15) — сведён, `RecoilOffset > 0` ⇒ разброс > 0;
  - `Recoil_AccumulatesAndDecays_InAimMode` (D8);
  - `SlideFire_FromSlideMuzzleHeight` — в слайде `Height == SlideMuzzleHeight`.
- [ ] **Step 2:** R-FILTER `WeaponTests`+`ProjectileHeightTests` → FAIL.
- [ ] **Step 3 (GREEN)** по сниппету; R-GOLDEN.
- [ ] **Step 4:** R-FILTER `WeaponTests`+`ProjectileHeightTests`+
  `HitZoneTests` → PASS; R-TEST.
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т15 — двухрежимный огонь`.

### Task Т16: golden-сценарий и финальная сим-сверка

**Files:** Modify `DeterminismTests.cs` (`Scripted`).

- [ ] **Step 1:** сигнатура сценария меняется на `Scripted(ref Random rng,
  ref bool aimHeld)`; `bool aimHeld = false;` объявляется ЛОКАЛЬНО в
  `RunScripted` рядом с `var rng = …` (**никаких статик-полей: `RunScripted`
  зовётся трижды, состояние утечёт между прогонами и golden станет
  порядкозависимым — QA5/QB5/QD5**). В сценарий добавить: `SlideRequested =
  rng.NextFloat() < 0.05f`; переключение `aimHeld` с шансом 3%/тик;
  `AimHeight = rng.NextFloat(0f, 3.8f)` — **пояса башни-головы [2.70, 3.50]
  достижимы** (PA2/PD4).
- [ ] **Step 2:** R-FILTER `DeterminismTests` → golden FAIL (ожидаемо) →
  R-GOLDEN — **ФИНАЛЬНЫЙ перепин** (значение — в bd note и в будущий PR).
- [ ] **Step 3:** R-TEST полный → 0 failed; счётчик тестов зафиксировать.
- [ ] **Step 4:** R-COMMIT `test(app-n6g): Т16 — golden покрывает слайд и оба
  режима огня`.

**Гейт фазы Г4:** R-TEST (total ≈ +9 к Г3 — QD17); push; jsonl-chore;
`bd note app-n6g "Г4: golden final <хеш>, тестов <N>"`; `bd close`
сабтаска Г4.

---

## Фаза Г5 — Presentation: данные, инпут, прицел (Т17–Т21)

### Task Т17: GameFeel-поля + маркер-ключи SO + ганнер-значения

**Files:**
- Modify: `client/Assets/Scripts/Data/GameFeelConfig.cs`,
  `client/Assets/Scripts/Editor/EditorBootstrapUtils.cs`,
  `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`

**Interfaces:**
- `GameFeelConfig` — новые поля (все `[Range]`; **`GunnerVisualScale` НЕ
  трогать — существует со значением 0.4, PA3/PC1/PD1**): `TracerScale 0.7`,
  `SlideDustBurstCount 14`, `SlideWallSparkBurstCount 10` (burst-модель —
  PC13), `RicochetSparkCount 12`, `StaminaBarFullColor`, `StaminaBarLowColor`,
  `StaminaBarLowThreshold 0.25`, `StaminaDeniedPulseSeconds 0.2`,
  `HeadHitstopScale 1.4`, `ZoneHitPitchOffset 0.06`, `GibHeadImpulseSpeed 6`,
  `GibExplosionSpeed 4`, `GibPartsFifoLimit 24`, `GibPhysicsSeconds 3`,
  `AimProxyHeadRadiusFrac 0.5`, `AimRayAlpha 0.35`, `AimRayWidth 0.03`,
  `AimDotScale 0.15` — маркер, ПОСЛЕДНИМ + keep-LAST (старый док у
  `CasingEjectSpeedMax` снять). **`RicochetSparkCount` и
  `SlideWallSparkBurstCount` НЕ заводятся** (QC3: burst-каунт запечён в
  префаб из `BlockSparkBurstCount`, `PlayParticle` его не параметризует;
  QC4/QD12: потребителя нет — спека §3.5 поправлена).
- `EditorBootstrapUtils.EnsureAssetHasKey(Object so, string assetPath,
  string markerField)` — извлечение инлайн-проверки бутстрапа (`File.
  ReadAllText(...).Contains` → `EditorUtility.SetDirty`); инлайн-вариант
  ЗАМЕНЯЕТСЯ вызовом (PC-нит).
- `StageOneSceneBootstrap`: вызовы `EnsureAssetHasKey` для
  `GameFeelConfig.asset` (`"AimDotScale"`), `HeroConfig.asset`
  (`"AimSettleSeconds"`), `WeaponConfig.asset` (`"RunSpreadSpeedFrac"`),
  `MobChaserConfig.asset` / `MobGunnerConfig.asset` (`"SwingLeadMaxMeters"`).
  **Порядок против гонки маркера (QD6): `bool gunnerMarkerPresent`
  снимается ДО всех `EnsureAssetHasKey`/`SetDirty`/`SaveAssets`.**
  **НОВЫЙ `ApplyGunnerZoneDefaults(MobConfig gunner)`** — `SetIfDifferent`
  ТОЛЬКО по новым полям: `LegsTop 1.10, BodyTop 2.70, HeadTop 3.50`, мульты
  `0.75/1.0/1.7`, `MuzzleHeight 0.95` (`SwingLead*` не трогает — у ганнера
  игнорируются, A15); гейт: `gunnerCreated || !gunnerMarkerPresent`.
  **Старый `ApplyGunnerDefaults` — строго под `gunnerCreated`** (регресс F-5
  запрещён — PA4/PB2/PC3).

- [ ] **Step 1:** поля + хелпер + `ApplyGunnerZoneDefaults` → R-COMPILE.
- [ ] **Step 2:** R-APPLY → ГЕЙТ-ЛОГ; YAML-ассерты: новые ключи в пяти
  `.asset`; `MobGunnerConfig.asset` содержит `LegsTop: 1.1` и
  `HeadTop: 3.5` (QD6); `GunnerVisualScale: 0.4` НЕ изменился; ручные
  числа ганнера Э1 (`MaxHp: 20` и т.д.) НЕ изменились.
- [ ] **Step 3:** R-IDEM (второй Apply — пустой diff).
- [ ] **Step 4:** R-COMMIT `feat(app-n6g): Т17 — доставка SO-полей
  маркер-ключами` (включая изменённые `.asset` + сцену, если dirty).

### Task Т18: инпут — бинды и сэмплер

**Files:** Modify `client/Assets/InputSystem_Actions.inputactions`,
`client/Assets/Scripts/Presentation/InputSampler.cs`,
`client/Assets/Scripts/Presentation/SimulationRunner.cs`.

**Interfaces:**
- `.inputactions` (правка JSON): `Gameplay/Dash` — `<Keyboard>/space` →
  `<Keyboard>/leftShift` (геймпад `buttonSouth`/XR НЕ трогаем);
  новый `Gameplay/Slide` (Button): `<Keyboard>/space` + `<Gamepad>/buttonEast`;
  новый `Gameplay/AimHold` (Button): `<Mouse>/rightButton` +
  `<Gamepad>/leftTrigger`. Имя `AimHold` — НЕ `AimMode`/`Aim` (существует
  Value-экшен `Gameplay/Aim` — A12; `UI/RightClick` — другая мапа, не
  конфликт).
- `InputSampler`: `_slideLatch` — полный контракт Dash-латча: подписка
  `performed` В `Enable()` (пере-подписка — урок F-2), снятие в `Disable()`,
  сброс в `ClearLatches()`; `AimHeld = _aimHold.IsPressed()` — уровень, БЕЗ
  `WasPressedThisFrame` (C16). `SampleFrame` заполняет
  `SlideRequested/AimHeld/AimHeight` (высота — из `AimProvider`, Т19;
  **до Т19 — `AimHeight = float.NaN`: `Sanitize` Т8 отобразит в
  `MuzzleHeight`; доступа к конфигу у сэмплера нет и не нужен — QD11**).
- `SimulationRunner`: проброс новых полей в `SimInput` +
  `SimInputFrame.ForTick` (edge — только `SlideRequested`).

- [ ] **Step 1:** правки → R-COMPILE.
- [ ] **Step 2 (смоук биндов, PD18/QD16):** ПОСТОЯННЫЙ editor-тест, не
  временный код: `InputActionsTests.cs` (+ `.meta`) в `Tests/EditMode` —
  `asset.FindAction("Gameplay/<X>", throwIfNotFound: true)` для
  Move/Aim/Fire/Dash/Slide/AimHold; R-FILTER `InputActionsTests` → PASS.
- [ ] **Step 3:** R-IDEM (`.inputactions` стабилен при Apply — QD16).
- [ ] **Step 4:** R-COMMIT `feat(app-n6g): Т18 — бинды слайда и прицела`.

### Task Т19: прицел — слой, прокси, провайдер

**Files:** Modify `client/Assets/Scripts/Presentation/AimProvider.cs`,
`client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`,
`client/ProjectSettings/TagManager.asset` (через бутстрап).

**Interfaces:**
- `AimProvider`: **дом константы** `public const int AimProxyLayer = 10`
  (бутстрап заимствует — PC15); `[SerializeField] SimulationRunner _runner`
  (**+ `SetRef(aimSo, "_runner", runner)` в бутстрапе — PA8/PD16**);
  `Awake`: `for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(
  AimProxyLayer, i, true);` (прецедент гильз — B3);
  **Семантика точки прицела МЕНЯЕТСЯ (QA7/QD1): при `AimHeld` и попадании
  в прокси И `CurrentAimSimPos`, И `CurrentAimHeight` берутся из ОДНОЙ
  точки `hit.point` одного каста** (иначе XY — за мобом на полу, высота —
  с прокси: траектория систематически ниже цели, хедшот-башня мертва);
  при промахе — плоскостной фолбэк Э1 + `height = 0`; при `!AimHeld` —
  поведение Э1 без изменений. Кэш обоих значений — в `LateUpdate` после
  `Physics.SyncTransforms`. NRE-гвард: `if (_runner == null ||
  _runner.World == null) return;` (QA18).
  `bool TryAimProxy(out float2 simPos, out float height)`: `Physics.Raycast(
  ray, out hit, _runner.World.Config.Arena.Radius * 2f, 1 << AimProxyLayer,
  QueryTriggerInteraction.Collide)` (множитель — тот же, что в `Sanitize`,
  PC15); публичные `CurrentAimSimPos` (без изменений семантики Э1) и НОВЫЙ
  `CurrentAimHeight`: при `AimHeld` и попадании — высота точки на прокси;
  при `AimHeld` и промахе — `0f` («хоть в пол»); без `AimHeld` — «не
  используется» (сэмплер шлёт, симуляция игнорирует). Каст — в `LateUpdate`
  ПОСЛЕ записи поз вьюх + `Physics.SyncTransforms()` (C15); однокадровое
  отставание — задокументировать в class-doc (K15).
- Бутстрап: общий **`EnsureUserLayer(int slot, string name)`** (QC14 —
  логика патча TagManager одна; `EnsureCasingsLayer` становится тонкой
  обёрткой, `EnsureAimProxyLayer` — второй вызов, слот 10, отказ при
  занятом чужим именем); прокси-чайлды `AimProxy_Legs/Body/Head`
  (CapsuleCollider `isTrigger = true`, слой 10) на префабах
  `MobChaserView`/`MobGunnerView` и кукле игрока — размеры из SO-поясов
  соответствующего конфига, голова — радиус `× AimProxyHeadRadiusFrac`;
  **self-heal ПОД ранним возвратом `PrefabVisualsMatch`** (идиома слоя
  гильз/fillSprite — PC2), т.е. прокси доезжают до уже закоммиченных
  префабов; `localScale` ганнера НЕ трогать (PC1).

- [ ] **Step 1:** `EnsureAimProxyLayer` + константа + `IgnoreLayerCollision`
  → R-COMPILE.
- [ ] **Step 2:** `AimProvider`: `_runner`/`TryAimProxy`/`CurrentAimHeight` +
  порядок LateUpdate → R-COMPILE.
- [ ] **Step 3:** бутстрап: прокси-чайлды + self-heal + `SetRef` → R-APPLY →
  ГЕЙТ-ЛОГ; YAML-проверка `MobChaserView.prefab`: три прокси-чайлда, слой 10.
- [ ] **Step 4:** R-IDEM; R-TEST (сим не тронут — регресс).
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т19 — слой и прокси 3D-прицела`
  (+ `TagManager.asset` + префабы + сцена).

### Task Т20: визуал прицела — луч, конус, маркер

**Files:**
- Create: `client/Assets/Scripts/Presentation/AimRayView.cs` (+ `.meta`)
- Modify: `client/Assets/Scripts/Presentation/CrosshairView.cs`,
  `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`

**Interfaces:**
- `AimRayView` — **ТОЛЬКО LineRenderer** (2 точки: дуло модели → точка
  прицела; `AimRayAlpha`/`AimRayWidth`; `enabled` только при `AimHeld`);
  **собственной точки-маркера НЕ заводит** (PC8). Материал создаёт бутстрап
  (`GetOrCreateUnlitMaterial` — приватный статик Editor-сборки, QA10) и
  прокидывает `SetRef(aimRaySo, "_rayMaterial", mat)`. Ссылки: `_runner`,
  `_aimProvider`, `_gameFeel`, `_rayMaterial` (SetRef бутстрапом).
- `CrosshairView`: конус — радиус строго по хип-формуле через
  **`Spread.HipRadians(World.Config.Weapon, RenderCurr.Player,
  World.Config.Hero)`** (одна формула на сим и вьюху — PC6; `settleFactor`
  НЕ применяется — конус живёт только от бедра, PD15); при `AimHeld` конус
  скрыт; `_marker`: квадрат-квад → круглый мини-диск, при `AimHeld` служит
  точкой прицела (scale × `AimDotScale`, позиция — точка прицела) — второй
  маркер не заводится (PC8). **Оба док-блока `CrosshairView` (class-doc и
  `UpdateCone`) переписать под `Spread.HipRadians`** — иначе доки врут про
  источник формулы (QC16).

- [ ] **Step 1:** `AimRayView` + бутстрап-объект/провода → R-COMPILE.
- [ ] **Step 2:** `CrosshairView` (формула/режимы/круг-маркер) → R-COMPILE.
- [ ] **Step 3:** R-APPLY → R-IDEM.
- [ ] **Step 4:** R-COMMIT `feat(app-n6g): Т20 — луч прицела и честный круг
  разброса` (+ `.meta`).

### Task Т21: вьюхи снарядов/дула/декалей

**Files:** Modify `client/Assets/Scripts/Presentation/SimulationRunner.cs`,
`ViewRegistry.cs`, `MuzzleFlashView.cs`, `AimRayView.cs`,
`PersistentPropsDirector.cs`, `client/Assets/Scripts/Data/GameFeelConfig.cs`
(док-строки), **`client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
(снять `SetRef(..., "_arena", ...)` :1080 — `SetRef` бросает на
  отсутствующее поле, без этого R-APPLY упадёт; QC2)**.

**Interfaces:**
- `SimulationRunner` — НОВОЕ свойство (единственный дом тернара — PC7):
  `public float RenderMuzzleHeight => RenderCurr.Player.SlideTimer > 0f
  ? World.Config.Hero.SlideMuzzleHeight : World.Config.Hero.MuzzleHeight;`
  `WouldFireThisFrame` — гейт `CanFireWhileSlide` из **`World.Config.Weapon`**
  (не третья SO-ссылка — B7).
- `ViewRegistry`: интерполяция высоты `Mathf.Lerp(prev.PrevHeight/Height…)`
  вместо константы `ProjectileOffset` (удалить — K8); масштаб трейсера ×
  `TracerScale`.
- Потребители дульной высоты → `_runner.RenderMuzzleHeight`:
  `MuzzleFlashView` — предсказание и **игрок-ветка** события; **моб-ветка
  события — `World.Config.Gunner.MuzzleHeight`** (ганнер с Т4 стреляет с
  0.95, вспышка обязана совпасть — QC9); гильзы
  `PersistentPropsDirector.SpawnCasing`; `AimRayView` — начало луча.
  `AudioDirector` высоту НЕ читает — не трогать (PC7).
  `GameFeelConfig.MuzzleLiftY` — из кода выведен, поле остаётся
  (док-пометка «deprecated, не читается» + правка док-строки про
  `ProjectileOffset` — PB11/K18).
- Декали: `ProjectileBlocked` — стена: позиция на высоте `Amount`,
  ориентация по `HitDir`; пол (`HitDir == Vector2.zero`): декаль плашмя,
  нормаль вверх; **удалить `ComputeBlockNormal`/`SafeNormalize`/поле
  `_arena` + его `SetRef`** (нормаль теперь в событии — двух домов не
  остаётся, PC4); class-doc обновить.

- [ ] **Step 1:** `RenderMuzzleHeight` + `WouldFireThisFrame` → R-COMPILE.
- [ ] **Step 2:** `ViewRegistry` высота/трейсер → R-COMPILE.
- [ ] **Step 3:** дульные потребители + `AimRayView` → R-COMPILE.
- [ ] **Step 4:** декали по событию + удаление `ComputeBlockNormal` →
  R-COMPILE; R-APPLY → R-IDEM; R-TEST (регресс).
- [ ] **Step 5:** R-COMMIT `feat(app-n6g): Т21 — вьюхи снарядов, дула и
  декалей в 3D`.

**Гейт фазы Г5:** R-TEST (total: +InputActionsTests — QD17) + R-APPLY +
R-IDEM; push; jsonl-chore; bd note; `bd close` сабтаска Г5.

---

## Фаза Г6 — HUD и фидбек → ВЕХИ В1 + В2 (Т22)

### Task Т22: HUD «Буст», deny, зонный фидбек, DevOverlay

**Files:** Modify `client/Assets/Scripts/Presentation/HudController.cs`,
`GameFeelDirector.cs`, `AudioDirector.cs`, `PersistentPropsDirector.cs`,
`DevOverlay.cs`, `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`.

**Interfaces:**
- HUD: `_staminaFill` — третий `GetOrCreateBar("StaminaBar", …)` + `SetRef`
  (паттерн `_hpFill`/`_dashFill` — B8); заполнение —
  **`_runner.Curr.Player.Stamina`** (источник соседних баров; НЕ
  `RenderCurr` — бар не должен замирать в хитстопе, QC10) `/
  World.Config.Hero.StaminaMax` (eps-гвард как `CooldownEps`); цвет — лерп
  `StaminaBarFullColor→LowColor` ниже порога; по `StaminaDenied` — пульс
  (`StaminaDeniedPulseSeconds`). **Текстовой подписи НЕТ** (у HP/Dash её
  нет, `GetOrCreateBar` строит только Background+Fill; различение — цветом
  и позицией; «Буст» остаётся термином документации/настроек — QD7,
  решение записано).
- `GameFeelDirector`: `ProjectileHit` c `Zone == Head` → hitstop ×
  `HeadHitstopScale`.
- `AudioDirector`: питч хита ± `ZoneHitPitchOffset` (Head выше, Legs ниже);
  короткий deny-звук по `StaminaDenied`; звук рикошета по `DashRicocheted`.
- `PersistentPropsDirector`: `HandleEvent` + `DashRicocheted` → burst через
  **существующий `_blockSparkPool`** (реальная сигнатура QA13:
  `PlayParticle(_blockSparkPool, SimSpace.ToWorld(e.Pos),
  Quaternion.LookRotation(SimSpace.ToWorld(e.HitDir), Vector3.up))` +
  гвард нулевого `HitDir` — `LookRotation(zero)` пишет ошибку в лог и
  роняет ГЕЙТ-ЛОГ; каунт частиц — запечённый `BlockSparkBurstCount`, свой
  каунт не заводится — QC3/PC13); `PlayerSlideStarted` → burst пыли
  `SlideDustBurstCount` (прецедент `HitSparkBurstCount`/
  `ConfigureBurstParticles`; отдельный dust-пул + префаб — по образцу
  спарк-пулов).
- `DevOverlay`: строки `SlidesUsed`/`HeadshotKills` (A16; `DroppedEvents`
  уже есть).

- [ ] **Step 1:** HUD-бар + бутстрап → R-COMPILE.
- [ ] **Step 2:** deny-пульс + звуки → R-COMPILE.
- [ ] **Step 3:** зонный хитстоп/питч → R-COMPILE.
- [ ] **Step 4:** рикошет-искра + слайд-пыль → R-COMPILE.
- [ ] **Step 5:** DevOverlay → R-COMPILE; R-APPLY → R-IDEM; R-TEST (регресс).
- [ ] **Step 6:** R-COMMIT `feat(app-n6g): Т22 — HUD Буста и фидбек зон`.

### ВЕХА В1 «Руки» + ВЕХА В2 «Прицел» — плейтесты владельца (СТОП)

- [ ] Гейт фазы Г6 (QB10): R-TEST полный (ожидаемое total ≈ Г4-счётчик,
  сим не менялся — QD17); R-APPLY + R-IDEM; push ветки; jsonl-chore;
  `bd note app-n6g "Г6 done: вехи В1/В2 переданы владельцу"`.
- [ ] Доложить владельцу В1: Буст/слайд/рикошет/замах/HUD/deny; тюнинг-лист
  (экономика Буста, окна, `SwingLead*`, кандидат «выпад на ударе» — C10;
  кандидаты чейзера `Telegraph 0.22`/`Range 1.4` — M20); чеклист
  `DroppedEvents == 0` (DevOverlay). Играет в Editor'е, hot-tweak SO живьём.
- [ ] Доложить владельцу В2: оба режима, зоны, oneshot-башня, экран «мяса»,
  «хоть в пол», луч/круг; читаемость хедшота (C12); near-miss «в пол»
  (кандидат `AimSnapScreenRadius` — C9); **балансовый PR владельца: радиус
  снаряда, `GunnerVisualScale 0.4 → ≈0.76`**.
- [ ] Числа плейтеста → `chore(app-n6g): <SO> — числа вех В1/В2`
  (санкционированное исключение из запретного списка — QB9); R-IDEM мерить
  ПОСЛЕ этого коммита.
- [ ] Фидбек → фикс-волны (урок 34) → bd note.
- [ ] **Дальше — только по команде владельца.**

---

## Фаза Г7 — анимация и обломки → ВЕХА В3 (Т23–Т24)

### Task Т23: слайд-анимация куклы

**Files:**
- Create: `client/Assets/ThirdParty/_Ring/Animations/RunningSlide.fbx`
  (+ `.meta`; Mixamo, ADR-002 A10)
- Modify: `client/Assets/ThirdParty/CREDITS.md`,
  `docs/adr/ASSETS-001-Модели-и-анимации.md` (строка-amendment со ссылкой
  на ADR-002 A10 — PB12), `client/Assets/Scripts/Presentation/PlayerVisual.cs`,
  `client/Assets/Scripts/Editor/ThirdPartyAnimatorBootstrap.cs`,
  `client/Assets/Scripts/Presentation/AnimIds.cs`

**Interfaces:**
- Импорт по ASSETS-001 §4.2: Humanoid-ретаргет на аватар куклы, root motion
  OFF, Loop Time OFF; `CREDITS.md` — строка Mixamo + «no redistribution».
- `AnimIds` + `SlideName/Slide`-хеш; `ThirdPartyAnimatorBootstrap` — стейт
  Slide в контроллере куклы (переходы кодом не нужны — `PlayerVisual` водит
  `Play/CrossFadeInFixedTime` по хешам, дисциплина Фазы Б).
- `PlayerVisual`: вход в слайд-позу по `RenderCurr.Player.SlideTimer > 0`
  (кроссфейд), выход — обратно в локомоцию; **фолбэк** (если ретаргет
  кривой — риск Р1, решает владелец на В3): процедурный присед+наклон по
  образцу дэш-наклона; поза/дуло — гизмо + `[ContextMenu("Capture Slide
  Pose To Config")]` (паттерн урока 33 — B15).

- [ ] **Step 1:** импорт FBX + `CREDITS.md` + ASSETS-001-строка → R-COMPILE
  (импорт-лог чист).
- [ ] **Step 2:** `AnimIds`/аниматор-бутстрап → R-APPLY
  (ThirdPartyAnimatorBootstrap) → ГЕЙТ-ЛОГ.
- [ ] **Step 3:** `PlayerVisual` слайд-ветка + Capture → R-COMPILE; R-APPLY →
  R-IDEM.
- [ ] **Step 4:** R-COMMIT `feat(app-n6g): Т23 — слайд-анимация Сборщика`
  (+ `.meta` FBX И НОВОЙ ПАПКИ `_Ring/Animations/` — QB20/QB16;
  LFS-проверка: `git check-attr filter -- <fbx>` → lfs).

### Task Т24: обломки (сначала `app-1zf`)

**Files:**
- Create: `client/Assets/Scripts/Presentation/GibView.cs` (+ `.meta`);
  при положительном `app-1zf` — под-меши в
  `client/Assets/ThirdParty/_Ring/Gibs/` (+ `.meta`, LFS)
- Modify: `client/Assets/Scripts/Presentation/PersistentPropsDirector.cs`,
  `CorpseView.cs`, `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
  (префаб обломка + провода)

**Interfaces:**
- **Сначала `app-1zf`:** разобрать FBX George/Leela на предмет под-мешей по
  костям (голова/конечности отдельными мешами?); результат → bd note +
  `bd close app-1zf`. Отрицательный → фолбэк: отлёт ЦЕЛОЙ головы-меша
  (если отделима) или примитивов, решение владельца на В3 (Р2).
- `GibView`: Rigidbody-обломок, слой `PersistentPropsDirector.CasingsLayer`
  (физика уже изолирована); **settle-логика — общий хелпер `PropSettle`
  (Presentation): `static bool ShouldFreeze(Rigidbody rb, float elapsed,
  float settleSeconds)` — freeze по скорости + hard-cap (НЕ голый таймер,
  урок app-4qc); `CasingView` рефакторится на него же — одно правило
  заморозки на обе вьюхи (QC15, PC14)**; `Spawn(Vector3 pos, Mesh mesh, Material mat,
  Vector3 impulse)` — `AddForce(impulse, ForceMode.VelocityChange)` (урок 28).
- `PersistentPropsDirector`: пятый `RingBuffer<GibView>` (лимит
  `GibPartsFifoLimit`, Prewarm, регистрация в `Clear()` — D10);
  `HandleMobDied(e)`: `e.Zone == Head` → труп «без головы» +
  голова-обломок импульсом `GibHeadImpulseSpeed * SimSpace.ToWorld(e.HitDir)`;
  overkill/взрыв — разброс частей `GibExplosionSpeed` рандомными
  направлениями (`UnityEngine.Random` — клиентский рандом легален для
  косметики, прецедент гильз).
- `CorpseView.Spawn` — параметр `bool headless` (тоглит чайлд головы меха;
  ветка в существующем методе — B5).

- [ ] **Step 1:** `app-1zf` — разбор, bd note, `bd close app-1zf`.
- [ ] **Step 2:** `GibView` + пул + `Clear` → R-COMPILE.
- [ ] **Step 3:** `HandleMobDied`-ветки + `CorpseView.headless` + бутстрап →
  R-COMPILE; R-APPLY → R-IDEM; R-TEST (регресс).
- [ ] **Step 4:** R-COMMIT `feat(app-n6g): Т24 — обломки мехов по зоне и
  вектору` (+ `.meta`, LFS-проверка).

### ВЕХА В3 «Мясо» — плейтест владельца (СТОП)

- [ ] Гейт фазы Г7 (QB10): R-TEST полный; R-APPLY-<X> затронутых бутстрапов
  + R-IDEM; push; jsonl-chore; `bd note app-n6g "Г7 done: веха В3"`.
- [ ] Доложить владельцу: обломки (голова по вектору, взрыв-разброс),
  слайд-клип Mixamo или процедурный фолбэк (его слово — Р1), пыль/искры/
  звуки.
- [ ] Числа плейтеста → `chore(app-n6g): <SO> — числа вехи В3` (QB9).
- [ ] Фидбек → фикс-волны → bd note.
- [ ] **Дальше — только по команде владельца.**

---

## Фаза Г8 — финализация (Т25)

### Task Т25: сквозные гейты, финал-ревью, PR, закрытие

- [ ] **Step 1:** R-TEST полный → 0 failed (счётчик + golden — в bd note);
  R-APPLY ×4 бутстрапов + R-IDEM.
- [ ] **Step 2:** R-BUILD-LinuxServer → EXIT=0; R-BUILD-WindowsClient →
  EXIT=0; размеры — в bd note.
- [ ] **Step 3:** финал-ревью ветки (**opus**, whole-branch: классы ошибок
  уровня расхождения единиц/утёкшего editor-кода — урок 34) → фикс-волна →
  повторный R-TEST.
- [ ] **Step 4:** `superpowers:finishing-a-development-branch` → PR
  (`gh pr create`, тело: golden old→new, счётчик тестов, вехи приняты) →
  merge (`gh pr merge --squash --delete-branch`).
- [ ] **Step 5:** bd: `bd close app-nco` (evidence В3), note `app-n6g`;
  **разблокировка Э2 — `bd dep remove app-5nu app-n6g`** (или закрытие
  эпика — решение владельца; PD20); jsonl-дрифт — chore в main.
- [ ] **Step 6:** уборка worktree (`git worktree remove`), сборка владельцу
  по запросу; handoff — по команде владельца.

---

## Декомпозиция bd (создать при старте impl-сессии, после апрува плана)

`bd create` фазовых сабтасков + `bd dep add <sub> app-n6g --type parent-child`
+ blocks-цепочка Г1→Г2→…→Г8: «Г1 данные/RNG (Т1–Т3)», «Г2 снаряды/зоны
(Т4–Т8)», «Г3 Буст-мувмент (Т9–Т14)», «Г4 огонь+golden (Т15–Т16)», «Г5
Presentation-прицел (Т17–Т21)», «Г6 HUD/фидбек + вехи В1/В2 (Т22)», «Г7
анимация/обломки + веха В3 (Т23–Т24)», «Г8 финализация (Т25)». `app-1zf` —
блокирует Г7.

## Соответствие спеке (сводно)

§3.2: Т1/Т4–Т8/Т13/Т15 · §3.3: Т2/Т9–Т14 · §3.4: Т6/Т7/Т9/Т10/Т12/Т22 ·
§3.5: Т1/Т2/Т17 · §3.6: Т18–Т24 · §4: RED-шаги + Т16 · §5: вехи Т22/Т24 ·
§7 DoD: Т16/Т25 · §8 риски: Р1→Т23, Р2→Т24/app-1zf, Р3→В2, Р4→заметки
app-5nu (сделано в spec-сессии), Р5→В1.

## Соответствие находкам план-ревью (v2→v3 сохранено)

PA1/PD2/PB3→Т6 (public enum в Core) · PA2/PD4→Т8/Т16 (3.8) · PA3/PC1/PD1→
Т17/Т19/В2 (`GunnerVisualScale`) · PA4/PB2/PC3→Т17 (`ApplyGunnerZoneDefaults`)
· PA5/PD5→фикстурные выражения (Global) · PA6→Т6 (чейзер 6.5 м) · PA7/PB7→
шапка v5 · PA8/PD16→Т19 (`_runner`+SetRef) · PA9→Т3 (u) · PA10/PB1/PC12→
`[Range]` (Global) · PA11→Т1 (MuzzleHeight 0.95 дефолт) · PA12/PD9/PC16→Т6
(Emit optional, KillPlayerForTest) · PA13/PD17→Т13 (сигнатура=спека v5) ·
PA14→Т12 (фикстура) · PA15→Т5 (размер/порядок скретча) · PA16/PC9→Т3
(удаление `_rng`) · PA17→Global (RED-дисциплина) · PB5→Т10 (SimEvents) ·
PB6→Global (.meta) · PB8→Global (словарь) · PB9→Т1/Т2 (keep-LAST) · PB10→
Т9–Т14 (HashPlayer/HashStats в Files) · PB11→Т21 (док) · PB12→Global
(refactor) + Т23 (ASSETS-001) · PC2→Т19 (self-heal) · PC4→Т21
(ComputeBlockNormal удалён) · PC5→Т1/Т6 (скалярные тела) · PC6/PD15→Т15/Т20
(`HipSpreadRadians`) · PC7→Т21 (`RenderMuzzleHeight`) · PC8→Т20 (один
маркер) · PC10→Т12 (guard у вызывающего) · PC11/PD13→Т9 (рефлексивный
кламп-проход) · PC13→Т17/Т22 (burst/`_blockSparkPool`) · PC14→Т24 (settle
дословно) · PC15→Т19 (константа слоя/maxDistance) · PD3→спека v5 (RNG-draw
оба режима) · PD6→подшаги Presentation-тасков · PD7→Т10 (руление-тест) ·
PD8→Т6 (M5 через чейзер-геометрию) · PD10→Т6 (`SegmentCircleInterval`) ·
PD11→Т6 (Legs-тесты) · PD12→Т10 (буфер-добор) · PD14→Т1/Т2 (`Does.Contain`)
· PD18→Т18 (FindAction-смоук) · PD19→вехи В1+В2 после Т22 · PD20→Т25
(разблокировка app-5nu) · PD21/PB4→Т13 (Modify) · PD22→Т5 (BASELINE).
