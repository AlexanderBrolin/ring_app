# План имплементации: Боёвка-глубина (app-n6g)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development
> (рекомендовано) или superpowers:executing-plans. Шаги — чекбоксы `- [ ]`.

**Цель:** двухрежимный прицел (ПКМ-луч / от бедра) + зоны поражения
голова/тело/ноги + Буст-мувмент (дэш/слайд/рикошет/связки) + обломки +
упреждающий замах чейзера — по спеке
`docs/superpowers/specs/2026-08-03-combat-depth-spec.md` (**v5**).
**Статус плана:** v2 — правки self-review по протоколу `review_plan.md`
(субагенты A/B/C/D: PA1–PA17, PB1–PB12, PC1–PC16, PD1–PD22 внесены).

**Архитектура:** вертикаль только у снарядов и хит-объёмов; движение 2D; новые
механики — детерминированные поля `PlayerState`/`ProjectileState` + чистые
системы; Presentation потребляет события/снапшот/`World.Config`. Баланс — SO.

**Стек:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics; без новых пакетов.

## Global Constraints (действуют в каждом таске)

- **Спека v5 — источник формул**; при конфликте план проигрывает спеке
  (стейл-мест не осталось: RNG-в-прицеле, `PredictPos(+maxSpeed)`, экран
  ≈4.8 м, `GunnerVisualScale` — спека уже v5-правленая).
- CR1: `Simulation/**` без UnityEngine. CR2: TDD. CR6: баланс только в SO.
- **RED-дисциплина (PA17):** если тест не компилируется из-за отсутствующих
  полей/сигнатур — сначала добавить пустые поля/заглушки, добиться
  КОМПИЛЯЦИИ и наблюдаемого **FAIL ассерта**; ошибка компиляции как
  RED-свидетельство не принимается.
- **Числа в тестах (PD5):** ожидания выражать через фикстуру
  (`cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac`), не литералами из
  `.asset` — `TestConfigs` и `.asset` расходятся санкционированно (C27).
- **Golden:** каждый сим-таск, меняющий поведение, перепинивает
  `GoldenHash_ScriptedScenario` (`DeterminismTests.cs:146`); старый хеш Э1
  `0x39B4C57694AD8770` зафиксирован здесь и в спеке §7. Перепин: прогон →
  actual из лога → вписать → прогон PASS.
- Тесты: `cd <worktree> && "$UNITY" -runTests -batchmode -projectPath client
  -testPlatform EditMode -testResults <scratch>/tN.xml -logFile <scratch>/tN.log
  -testFilter "<Full.Class>"` (БЕЗ `-quit`; перед прогоном
  `ps aux | grep -i "[U]nity/Hub/Editor.*projectpath"` пуст; таймаут ~300 с).
- Коммиты: `feat|test|fix|refactor|docs(app-n6g): …` русским + трейлер
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; **новые `.cs`
  добавлять в git ВМЕСТЕ с их `.cs.meta`** (CR8, PB6); секрет-чек перед
  каждым коммитом.
- Словарь: проза — «Буст», «зоны поражения»/«хит-объём»; `Stamina`/`HitZone`
  — только код в бэктиках (PB8).
- Новые SO-поля: атрибут `[Range(min, max)]` с осмысленным верхом (конвенция
  всех SO; `[Min]` в проекте не используется — PA10/PB1); маркер-поле каждого
  SO объявляется ПОСЛЕДНИМ + комментарий `// sync-marker key — keep LAST`
  (PB9).
- После каждого таска — `bd note app-n6g "<таск>: done, <evidence>"`.

Файловая карта: Create `Simulation/Combat/HitZones.cs` (только статик-класс),
`Presentation/AimRayView.cs`, `Presentation/GibView.cs`, тесты в
`client/Assets/Tests/EditMode/`: `ProjectileHeightTests.cs`,
`HitZoneTests.cs`, `StaminaTests.cs`, `SlideTests.cs`, `DashRicochetTests.cs`.
Modify (ключевое): `Core/SimStates.cs` (+`HitZone` enum — PB3),
`Simulation/AI/Targeting.cs` (PB4/PD21 — существует), остальное по таскам.

---

## Фаза 1 — сим-ядро: данные, RNG, снаряды, зоны

### Т1: конфиг-поля зон и дул (Hero + Mob)

**Files:** Modify `Scripts/Data/HeroConfig.cs`, `MobConfig.cs`,
`Simulation/Core/SimConfig.cs`, `Scripts/Data/SimConfigBuilder.cs`;
Test `Tests/EditMode/ConfigTests.cs`, `TestConfigs.cs`.
**Produces:** `HeroSimConfig.{LegsTop 0.55, BodyTop 1.35, HeadTop 1.75,
LegsDamageMult 0.75, BodyDamageMult 1.0, HeadDamageMult 1.7, SlideProfileTop
0.55, MuzzleHeight 1.0, SlideMuzzleHeight 0.45, MaxAimHeight 3.8}`;
`MobSimConfig.{LegsTop, BodyTop, HeadTop, LegsDamageMult, BodyDamageMult,
HeadDamageMult, MuzzleHeight, SwingLeadFactor, SwingLeadMaxMeters}`.
Дефолты C#-класса `MobConfig` = чейзер (0.60/1.45/1.85, мульты 0.75/1.0/1.7,
**`MuzzleHeight = 0.95f` — дефолт класса, чейзер поле не использует** —
PA11, `SwingLeadFactor 1.0`, `SwingLeadMaxMeters 2.0`); ганнер-значения
(1.10/2.70/3.50) — Т17.

- [ ] RED: в `ConfigTests` — `Validate_ZoneOrderViolated_Throws`:
  `Assert.Throws<ArgumentException>` + **`Does.Contain("LegsTop")` по тексту
  ошибки** (PD14); `Validate_SlideProfileAboveGunnerMuzzle_Throws`
  (`SlideProfileTop 0.9` ⇒ `0.9 + 0.15 ≥ 0.95`, `Does.Contain("SlideProfileTop")`);
  `Zones_ClassifyLegsBodyHead_AtBoundaries`-заготовку НЕ писать (зоны — Т6).
- [ ] Заглушки полей → компиляция → прогон `ConfigTests` → FAIL ассертов.
- [ ] GREEN-1: поля SO (`[Range]`, маркер-поля последними + keep-LAST) +
  sim-структуры + маппинг → компиляция.
- [ ] GREEN-2: правила `Validate` §3.5 (пояса per-архетип через общий
  `ValidateZones(errors, name, legs, body, head)` — три копии запрещены,
  PC5) + `TestConfigs.Default()` + `AssertHeroEqual`/`AssertMobEqual` +
  gunner-блок `Build_DefaultAssets_MatchesTestConfigsBaseline` (A3).
- [ ] Прогон `ConfigTests` целиком → PASS.
- [ ] Commit `feat(app-n6g): Т1 — конфиг зон поражения и дул (+.meta новых файлов нет)`.

### Т2: конфиг-поля Буст/слайд/прицел/разброс

**Files:** те же + `WeaponConfig.cs`.
**Produces:** `HeroSimConfig.{StaminaMax 90, DashStaminaCost 48,
SlideStaminaCost 13, LinkedDashStaminaCost 16, StaminaRegenPerSec 22,
StaminaRegenDelay 0.72, SlideSpeed 13.5, SlideDuration 0.52,
SlideSteerRadPerSec 1.2, SlideMinSpeedFrac 0.75, RunUpSeconds 1.18,
RunUpDecayMult 3.0, SlideBufferWindow 0.15, LinkWindowSeconds 0.25,
PostDashSlideWindow 0.32, SlideWallStopDot 0.7, RicochetRetention 0.8,
AimMoveSpeedFrac 0.8, AimSlideSpeedMult 0.5, AimSettleSeconds 0.5}`;
`WeaponSimConfig.{CanFireWhileSlide true, SpreadRunMult 1.5,
SpreadSlideMult 2.0, RunSpreadSpeedFrac 0.5}`.

- [ ] RED: `Validate_ZeroStaminaRegen_Throws`
  (`Does.Contain("StaminaRegenPerSec")`),
  `Validate_AimFracNotAboveSlideFrac_Throws` (равенство 0.75 — строгое `>`,
  `Does.Contain("AimMoveSpeedFrac")`).
- [ ] Заглушки → прогон → FAIL ассертов.
- [ ] GREEN: поля + маппинг + полный список валидаций §3.5 + lockstep
  `TestConfigs`/`Assert*Equal`.
- [ ] Прогон `ConfigTests` → PASS.
- [ ] Commit `feat(app-n6g): Т2 — конфиг Буста/слайда/прицела`.

### Т3: RNG-split `_spreadRng`/`_waveRng`

**Files:** Modify `Core/SimulationWorld.cs`, `Core/WorldSave.cs`,
`Combat/WeaponSystem.cs`, `AI/WaveSystem.cs`; Test `DeterminismTests.cs`,
`WorldLifecycleTests.cs`.
**Produces:** `internal ref Random SpreadRng` / `WaveRng`; сид
`folded ^ 0xB5297A4Du` / `folded ^ 0x68E31DA4u` (**u-суффиксы — PA9**),
ноль-guard как у Э1. **Старые `_rng`/`Rng`/`WorldSave.Rng` и их строки в
`SaveState`/`RestoreState`/`StateHash` УДАЛЯЮТСЯ** — потребителей не
остаётся; док-комментарии `SimulationWorld` («single shared Random») и
`WaveSystem` («w.Rng.NextFloat») переписываются (PA16/PC9).

- [ ] RED: `SpreadDrawDoesNotShiftWaves` — два мира, один seed, один — 100
  тиков `FireHeld`, другой — без; состав/позиции первой волны идентичны.
- [ ] Прогон `DeterminismTests` → FAIL.
- [ ] GREEN: два потока, hash-порядок `tick → spreadRng.state →
  waveRng.state → …`; перепин golden.
- [ ] Прогон `DeterminismTests` + `WorldLifecycleTests` → PASS.
- [ ] Commit `feat(app-n6g): Т3 — раздельные RNG-потоки оружия и волн`.

### Т4: 3D-поля снаряда + сигнатура спавна

**Files:** Modify `Core/SimStates.cs` (`ProjectileState + Height, PrevHeight,
VelZ`), `Core/SimulationWorld.cs` (`SpawnProjectile(owner, pos, vel, height,
velZ, damage, radius, ttl)` + тест-двойник + `HashProjectile`),
`Combat/ProjectileSystem.cs` (`PrevHeight = Height; Height += VelZ * dt` в
ветке «без попадания»), `Combat/WeaponSystem.cs` (`hero.MuzzleHeight, 0f`),
`AI/MobAiSystem.cs` (ганнер: `cfg.MuzzleHeight, 0f`); Test: **9**
call-sites (`ProjectileTests` :96/:103/:118/:120/:122/:146/:156,
`WorldLifecycleTests:46`, `DeathTests:54`) — `height: 1f, velZ: 0f`.

- [ ] RED: `ProjectileHeightTests.Projectile_WithVelZ_AdvancesHeightPerTick`
  (`height 1f, velZ −3f`, 1 тик ⇒ `Height == 1f − 3f * SimulationWorld.TickDt`).
- [ ] Заглушки → прогон → FAIL ассерта.
- [ ] GREEN + все call-sites; перепин golden.
- [ ] Полный EditMode (без фильтра) → 93 + новые PASS.
- [ ] Commit `feat(app-n6g): Т4 — высота снаряда в состоянии и спавне`
  (+ `.cs.meta` нового тест-файла).

### Т5: перебор кандидатов min-scan + скретч (refactor-фаза Т4)

**Files:** Modify `Combat/ProjectileSystem.cs`, `Core/SimulationWorld.cs`
(скретч `_projCandidates` размером **`Arena.MaxMobs + 3`** — барьер + игрок
+ пол; порядок: 0 = барьер, 1..N = мобы по индексу, дальше игрок/пол — PA15;
вне `SaveState`/`StateHash`, комментарий по образцу `_sepForces`).

- [ ] **BASELINE** (не RED — PD22): прогон `ProjectileTests` +
  `DeterminismTests` ДО правки — зелёные.
- [ ] Рефактор: повторный min-scan с исключением, тай-брейк — меньший индекс
  (строгое `<` Э1 сохраняется). Ветка «отклонить и продолжить» до Т6 —
  мёртвый код, активируется первым высотным тестом Т6 (осознанно).
- [ ] Прогон `ProjectileTests`, `DeterminismTests` (**golden НЕ меняется** —
  бит-в-бит), `AllocationTests` (0 GC, без делегатов сортировки) → PASS.
- [ ] Commit `refactor(app-n6g): Т5 — перебор кандидатов min-scan без
  сортировки` (тип `refactor` разрешён Global Constraints — PB12).

### Т6: высотный hit/no-hit + зоны + урон с множителями

**Files:** Modify `Core/SimStates.cs` (**`public enum HitZone : byte { None,
Legs, Body, Head }` — в Core, рядом с `MobType`; PA1/PD2/PB3** +
`MatchStats.HeadshotKills`); Create `Simulation/Combat/HitZones.cs` —
`public static class HitZones` со СКАЛЯРНЫМИ сигнатурами (PC5):
`Classify(float h, float legsTop, float bodyTop, float headTop)` (кламп h в
`[0, headTop]`), `Overlaps(float hEnter, float hExit, float radius, float top)`;
Modify `Core/Geometry.cs` (+`SegmentCircleInterval(..., out tEnter, out
tExit)` — PD10), `Combat/ProjectileSystem.cs` (интервал высот по
`[tEnter, tExit]`, отклонение кандидата продолжает скан),
`Core/SimulationWorld.cs` (`DamageMob/DamagePlayer(+zone, +dir)`, мульт ДО
эмита через `MultFor(zone, legs, body, head)` в `HitZones`, `HeadshotKills`
через `IncrementX`-хелпер, **`KillPlayerForTest`** — PD9/PA12),
`Core/SimEvents.cs` (`SimEvent + HitZone Zone, float2 HitDir`; `Emit` —
**два optional-параметра** `zone = HitZone.None, hitDir = default`, все ~12
call-site'ов не трогаются — PC16; док «unused (0)» обновить),
`AI/MobAiSystem.cs` (кулак: Body, без мульта, dir); Test Create
`HitZoneTests.cs` (+meta), Modify `ProjectileHeightTests.cs`,
`GeometryTests.cs`, `EventTests.cs`.

- [ ] RED-1 (`GeometryTests`): `SegmentCircleInterval_EnterExit` (пересечение,
  касательная, старт внутри — PD10).
- [ ] RED-2 (пакет): `GunnerHeadOverCrowd_HitFromFarChaser` (чейзер **(6.5,
  0)**, ганнер (9,0), спавн ЯВНО из (0,0) высота 1.0, наклон в голову
  ганнера ~3.1 — чейзер отвергается по высоте, умирает ганнер — PA6; этот же
  тест = M5 «отвергнутый не затеняет» — PD8);
  `CloseChaser_ScreensGunnerHead` (чейзер (2,0) — съедает);
  `Graze_AtHeadTopPlusRadius_HitsAsHead`;
  `Zones_ClassifyLegsBodyHead_AtBoundaries` (h = LegsTop−ε / LegsTop /
  BodyTop−ε / BodyTop — PD11); `LegsHit_AmountIs075` (PD11);
  `EqualT_TieBreaksLowerIndex`; `GunnerHeadshot_IsOneshot`
  (`w.Config.Weapon.Damage * cfg.Gunner.HeadDamageMult ≥ cfg.Gunner.MaxHp`
  — через фикстуру); `ChaserHeadshot_TwoShots`; `Hit_Amount_IsPostMultiplier`;
  `Fist_ZoneBody_NoMult`.
- [ ] Заглушки → прогон двух классов → FAIL ассертов.
- [ ] GREEN по формулам §3.2; перепин golden.
- [ ] Полный EditMode → PASS.
- [ ] Commit `feat(app-n6g): Т6 — зоны поражения, множители, события с зоной`.

### Т7: пол-кандидат

**Files:** Modify `Combat/ProjectileSystem.cs` (`t_floor = (proj.Radius −
Height) / (VelZ * dt)` при `VelZ < 0` — кандидат в общем порядке;
`ProjectileBlocked`: `Amount` = высота контакта, пол — `HitDir = (0,0)`,
стены — `HitDir = normal` из `SweepArena`); Test `ProjectileHeightTests.cs`,
`EventTests.cs`.

- [ ] RED: `FloorHit_BlocksAtFloorPoint_MobBehindUnharmed`;
  `WallBlock_CarriesNormalAndHeight`.
- [ ] Прогон → FAIL.
- [ ] GREEN; перепин golden.
- [ ] Прогон класса + `EventTests` → PASS.
- [ ] Commit `feat(app-n6g): Т7 — пол как кандидат хитрега`.

### Т8: `SimInput.AimHeight/AimHeld/SlideRequested` + `Sanitize`

**Files:** Modify `Core/SimInput.cs`, `Core/SimulationWorld.cs` (`Sanitize`);
Test `DeterminismTests.cs`.

- [ ] RED: `HostileInput_...` + `AimHeight = float.NaN, AimHeld = true` (мир
  конечен/детерминирован); `Sanitize_ClampsAimHeight` — `5f →
  cfg.Hero.MaxAimHeight` (**3.8 через фикстуру, не литерал 2.2** — PA2/PD4).
- [ ] Прогон `DeterminismTests` → FAIL.
- [ ] GREEN (симуляция высоту ещё не читает; golden не меняется).
- [ ] Прогон → PASS.
- [ ] Commit `feat(app-n6g): Т8 — прицельные входы и санитайз`.

---

## Фаза 2 — сим-мувмент: Буст, слайд, рикошет, замах, режимы огня

### Т9: Буст-ядро

**Files:** Modify `Core/SimStates.cs` (`PlayerState + Stamina,
StaminaRegenDelayTimer`), `Core/SimulationWorld.cs` (init `Stamina =
StaminaMax`; смерть-заморозка; `ApplyConfig`-клампы; `Emit(StaminaDenied…)`;
**`HashPlayer` — явно в Files, PB10**), `Core/SimEvents.cs` (`StaminaDenied`),
`Movement/PlayerMovementSystem.cs`; Test Create `StaminaTests.cs` (+meta);
Modify `HotTweakTests.cs`, `TestConfigs.cs` (+ вариант `RegenFixture()`:
`SlideDuration 0.9, StaminaRegenDelay 0.3` — M16), `WorldLifecycleTests.cs`
(+ **рефлексивный проход: после `ApplyConfig` с уменьшенными максимумами ни
одно float-поле `PlayerState` не выше нового максимума** — PC11, закрывает
PD13 структурно).

- [ ] RED: `StartsAtFullStamina`; `Dash_CostsStamina`
  (`StaminaMax − DashStaminaCost` через фикстуру);
  `Dash_InsufficientStamina_DeniedWithEvent`; `Regen_WaitsDelayThenRefills`;
  `Regen_FrozenDuringSlide_OnFixture` (`RegenFixture`);
  `HotTweak_ClampsStaminaToNewMax`; рефлексивный кламп-тест.
- [ ] Заглушки → прогон `StaminaTests` → FAIL ассертов.
- [ ] GREEN; перепин golden.
- [ ] Прогон `StaminaTests`+`HotTweakTests`+`DashTests`+`WorldLifecycleTests`
  → PASS.
- [ ] Commit `feat(app-n6g): Т9 — ресурс Буст (код Stamina)`.

### Т10: слайд — гейт, старт, тик, выход

**Files:** Modify `Core/SimStates.cs` (`+ SlideTimer, SlideDir,
SlideBufferTimer, RunUpTimer, PostDashSlideTimer` + `MatchStats.SlidesUsed`),
`Movement/PlayerMovementSystem.cs`, `Core/SimulationWorld.cs` (смерть,
клампы, `SlidesUsed`, `Emit(PlayerSlideStarted…)`, `HashPlayer`/`HashStats`),
**`Core/SimEvents.cs` (`PlayerSlideStarted` — PB5)**; Test Create
`SlideTests.cs` (+meta).

- [ ] RED: `Slide_RequiresRunUpOrPostDash`; `RunUp_DecaysBelowThreshold`
  (через `RunUpDecayMult`); `Slide_ResetsRunUp_NoChain` (M2);
  `Slide_MutualExclusionWithDash` (C7); `SlideDir_FallbackToAim_WhenIdle`
  (D6); `Slide_ExitKeepsMomentum` (C22); `Death_ClearsSlideState` (M11);
  `Slide_EmitsStartEvent`; **`Slide_SteerRateIsClamped`** (разворот инпута →
  изменение `SlideDir` за тик ≤ `SlideSteerRadPerSec × dt` — PD7);
  **`SlideBuffer_FiresWhenRegenCoversCost`** (буфер-добор регеном — PD12,
  на `RegenFixture`).
- [ ] Заглушки → прогон → FAIL ассертов.
- [ ] GREEN (канон таймеров `math.max(0, t−dt)` включая `PostDashSlideTimer`
  — B10); перепин golden.
- [ ] Прогон `SlideTests`+`MovementTests`+`DashTests` → PASS.
- [ ] Commit `feat(app-n6g): Т10 — слайд: гейт разгона и жизненный цикл`.

### Т11: слайд у стены + окно связки + слайд-профиль

**Files:** Modify `Movement/PlayerMovementSystem.cs` (`MoveWithCollisions` →
`out bool hit, out float2 normal`; discard-вызовы `PlayerMovementSystem:60`,
`MobAiSystem:209`), `Core/SimStates.cs` (`+ LinkWindowTimer`),
`Combat/ProjectileSystem.cs` (слайд-профиль игрока),
`Core/SimulationWorld.cs` (клампы/`HashPlayer`); Test `SlideTests.cs`,
`HitZoneTests.cs`, `StaminaTests.cs`.

- [ ] RED: `WallStop_KillsSlide_NoLinkWindow` (M3);
  `SlideAlongWall_Continues`; `LinkedDash_DiscountAndCooldownBypass_ConsumesWindow`
  (C6); `PerfectChain_CostsExactly_StaminaMax` (48+13+16+13 через фикстуру);
  `GunnerShot_MissesSlidingHero` (M13); `SlidingHero_HitOnlyBelowProfile`.
- [ ] Заглушки → прогон → FAIL ассертов.
- [ ] GREEN; перепин golden.
- [ ] Прогон `SlideTests`+`StaminaTests`+`HitZoneTests`+`MobAiTests` → PASS.
- [ ] Commit `feat(app-n6g): Т11 — стена, окно связки, слайд-профиль`.

### Т12: рикошет дэша

**Files:** Modify `Core/SimStates.cs` (`+ DashSpeedCur`),
`Movement/PlayerMovementSystem.cs` (условие `dot(DashDir, normal) < 0` — **у
вызывающего, `math.reflect` из Unity.Mathematics напрямую, собственный
`Geometry.Reflect` НЕ заводится** — PC10; отражение применяется со
следующего тика; ≤1/тик, остальные итерации — `Geometry.Slide`),
`Core/SimulationWorld.cs` (`Emit(DashRicocheted…)`, кламп `DashSpeedCur`,
`HashPlayer`), `Core/SimEvents.cs` (`DashRicocheted`); Test Create
`DashRicochetTests.cs` (+meta); Modify `DashTests.cs`
(`DashIntoObstacle_StopsAtSurface_NoTunnel` → `..._Ricochets_NoTunnel` — A9).

- [ ] RED (вся арифметика — на явной фикстуре `DashDuration 0.09 / DashSpeed
  30` внутри тестов — C14/PD5): `Ricochet_MirrorsDashDir_NextTick` (D16);
  `Ricochet_AppliesRetention` (`DashSpeed × RicochetRetention` из фикстуры);
  `Ricochet_KeepsIframes`; `Ricochet_OncePerTick`;
  `Ricochet_EmitsEventWithNormal`; `Dash_CoversFixtureMetres` (4.0 м).
- [ ] Заглушки → прогон → FAIL ассертов.
- [ ] GREEN; перепин golden.
- [ ] Прогон `DashRicochetTests`+`DashTests`+`MovementTests` → PASS.
- [ ] Commit `feat(app-n6g): Т12 — зеркальный рикошет дэша`.

### Т13: упреждающий замах чейзера

**Files:** Modify `Simulation/AI/Targeting.cs` (**Modify — PD21**;
`public static float2 PredictPos(float2 pos, float2 vel, float maxSpeed,
float seconds, float factor, float maxLead)` — кламп длины `vel` по
`maxSpeed`, кламп смещения по `maxLead`; сигнатура согласована со спекой
v5), `AI/MobAiSystem.cs` (вход в `Telegraph` по `predicted`); Test
`MobAiTests.cs`.

- [ ] RED: `Chaser_TelegraphsAheadOfRunner_AndConnects`;
  `Chaser_Standing_FarPlayer_NoTelegraph` (D8);
  `Chaser_DashDoesNotBaitFromAfar` (кламп `maxSpeed` — A4);
  `Chaser_LeadClampedByMaxMeters`;
  `SwingLeadZero_EntryTickEqualsE1Rule` (D9).
- [ ] Заглушки → прогон `MobAiTests` → FAIL ассертов.
- [ ] GREEN; перепин golden.
- [ ] Прогон `MobAiTests` целиком → PASS.
- [ ] Commit `feat(app-n6g): Т13 — упреждающий замах чейзера`.

### Т14: прицельный режим в движении (кап, слайд-мульт, сведение)

**Files:** Modify `Core/SimStates.cs` (`+ AimSettleTimer`),
`Movement/PlayerMovementSystem.cs`, `Core/SimulationWorld.cs` (кламп,
`HashPlayer`); Test `MovementTests.cs`, `SlideTests.cs`.

- [ ] RED: `AimHeld_CapsRunSpeed` (стационар =
  `cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac` — **фикстурное выражение,
  не 6.0** — PA5/PD5); `AimReleased_RestoresMaxSpeed` (D8);
  `AimHeld_SlowsSlide_SameTick`
  (`cfg.Hero.SlideSpeed * cfg.Hero.AimSlideSpeedMult`);
  `RunUp_ReachableUnderAimCap` (0.8 > 0.75 — сравнение долей);
  `AimSettle_GrowsAndDecays` (декэй ×2).
- [ ] Заглушки → прогон → FAIL ассертов.
- [ ] GREEN; перепин golden.
- [ ] Прогон `MovementTests`+`SlideTests` → PASS.
- [ ] Commit `feat(app-n6g): Т14 — кап и слайд-штраф прицельного режима`.

### Т15: два режима огня в `WeaponSystem`

**Files:** Modify `Combat/WeaponSystem.cs` — формулы §3.2 v5 дословно; при
этом **хип-разброс выносится в public-хелпер** (например
`WeaponSystem.HipSpreadRadians(in WeaponSimConfig, in PlayerState, in
HeroSimConfig)`), который позже переиспользует `CrosshairView` — одна
формула на сим и вьюху (PC6). Aim: `a_aim = RecoilOffset + SpreadRad ×
(1 − AimSettleTimer/AimSettleSeconds)`; hip: `a = (SpreadRad + RecoilOffset)
× moveMult`; draw из `SpreadRng` в обоих режимах при `a > 0`; `muzzleH =
SlideTimer > 0 ? SlideMuzzleHeight : MuzzleHeight`; `vel3 =
normalizesafe(...) * ProjectileSpeed`; `spawnPos2D = p.Pos + dir2D ×
(MuzzleOffset + overshoot × length(vel3.xy))`; гейт `CanFireWhileSlide`.
Test `ProjectileHeightTests.cs`, `WeaponTests.cs`.

- [ ] RED: `AimedShot_HitsExactPoint_IncludingFloor` (сведён, спрея нет);
  `AimedShot_FullSpeed3D` (K10); `HipShot_HorizontalAtMuzzleHeight`;
  `HipSpread_RunAndSlideMultipliers` (граница `RunSpreadSpeedFrac` — D8);
  `FirstAimTick_SpreadNotZero` (C2); `AimedSpray_HasSpread` (Д15);
  `Recoil_AccumulatesAndDecays_InAimMode` (D8);
  `SlideFire_FromSlideMuzzleHeight`.
- [ ] Заглушки → прогон → FAIL ассертов.
- [ ] GREEN; перепин golden.
- [ ] Прогон `WeaponTests`+`ProjectileHeightTests`+`HitZoneTests` → PASS.
- [ ] Commit `feat(app-n6g): Т15 — двухрежимный огонь`.

### Т16: golden-сценарий и финальная сим-сверка

**Files:** Modify `DeterminismTests.cs` (`Scripted`: `SlideRequested` ~5%,
`AimHeld` — залипающий уровень ~3%/тик, **`AimHeight` вариация 0–3.8** —
пояса башни-головы ганнера [2.70, 3.50] обязаны попадать в сценарий —
PA2/PD4).

- [ ] RED: обновлённый сценарий → golden FAIL (ожидаемо).
- [ ] Перепин константы — ФИНАЛЬНЫЙ (значение — в описание PR).
- [ ] Полный EditMode без фильтра → 0 failed.
- [ ] `bd note app-n6g "Фаза 2 закрыта: golden <хеш>, N тестов"`.
- [ ] Commit `test(app-n6g): Т16 — golden покрывает слайд и оба режима огня`.

---

## Фаза 3 — Presentation/Editor (гейт: компиляция + Apply×2-идемпотентность + вехи)

### Т17: GameFeel-поля + маркер-ключи SO + ганнер-значения

**Files:** Modify `Scripts/Data/GameFeelConfig.cs` (новые поля §3.5;
**`GunnerVisualScale` НЕ трогать — существует** (PA3/PC1/PD1); маркер
`AimDotScale` последним + keep-LAST, старый док у `CasingEjectSpeedMax`
снять), `Editor/EditorBootstrapUtils.cs` (`EnsureAssetHasKey(so, assetPath,
markerField)`; **инлайн-проверка GameFeel-маркера в бутстрапе заменяется
вызовом хелпера** — PC-нит), `Editor/StageOneSceneBootstrap.cs`:
`EnsureAssetHasKey` для GameFeel/Hero/Weapon/Chaser/Gunner-ассетов; **НОВЫЙ
`ApplyGunnerZoneDefaults(gunner)`** — только поля §3.5 (пояса 1.10/2.70/3.50,
мульты, `MuzzleHeight 0.95`, `SwingLead*`-без-использования), гейт
`gunnerCreated || !markerPresent`; **старый `ApplyGunnerDefaults` остаётся
строго под `gunnerCreated`** (F-5 не регрессирует — PA4/PB2/PC3).

- [ ] Реализация полей + хелпера (компиляция).
- [ ] `ApplyGunnerZoneDefaults` + вызовы маркеров (компиляция).
- [ ] Batchmode Apply ×2 → второй `git status` по `Assets/Data` пуст;
  в YAML ассетов появились новые ключи, ганнер — зонные значения; ручные
  поля ганнера Э1 (`MaxHp` и др.) НЕ изменились.
- [ ] Commit `feat(app-n6g): Т17 — доставка SO-полей маркер-ключами`.

### Т18: инпут (бинды + сэмплер)

**Files:** Modify `Assets/InputSystem_Actions.inputactions` (`Gameplay/Dash`
kb space→leftShift, геймпад/XR не трогаем; `Gameplay/Slide` space +
buttonEast; `Gameplay/AimHold` rightButton + leftTrigger),
`Presentation/InputSampler.cs` (`SlideRequested`-латч: подписка в `Enable`,
`ClearLatches`; `AimHeld = _aimHold.IsPressed()` без Was-Pressed — C16),
`Presentation/SimulationRunner.cs` (проброс).

- [ ] Реализация.
- [ ] **Смоук-проверка биндов в batchmode:** временный вызов из Apply —
  `asset.FindAction("Gameplay/<X>", throwIfNotFound: true)` для
  Move/Aim/Fire/Dash/Slide/AimHold (PD18) → лог чист.
- [ ] Commit `feat(app-n6g): Т18 — бинды слайда и прицела`.

### Т19: прицел — слой, прокси, провайдер

**Files:** Modify `Presentation/AimProvider.cs` (константа
`public const int AimProxyLayer = 10` — дом константы, бутстрап заимствует
(PC15); `[SerializeField] SimulationRunner _runner` (**+`SetRef` в
бутстрапе — PA8/PD16**); `TryAimProxy`: `Physics.Raycast`, маска
`1 << AimProxyLayer`, `maxDistance = _runner.World.Config.Arena.Radius * 2f`
(тот же множитель, что `Sanitize` — PC15), триггеры,
`QueryTriggerInteraction.Collide`; `Physics.IgnoreLayerCollision` в `Awake`
по прецеденту гильз — B3; при `AimHeld`: прокси-точка либо пол/`AimHeight=0`),
`Editor/StageOneSceneBootstrap.cs` (`EnsureAimProxyLayer`; прокси-чайлды
трёх префабов из SO-поясов, голова × `AimProxyHeadRadiusFrac`;
**self-heal прокси под ранним возвратом `PrefabVisualsMatch`** — идиома
слоя гильз/fillSprite, PC2; `localScale` ганнера НЕ трогаем — PC1).

- [ ] `EnsureAimProxyLayer` + константа (компиляция).
- [ ] `AimProvider`: `_runner`+`SetRef`, `TryAimProxy`, порядок
  LateUpdate + `Physics.SyncTransforms` (C15) (компиляция).
- [ ] Бутстрап: прокси-чайлды + self-heal (компиляция).
- [ ] Apply ×2 → идемпотентно; прокси в существующих префабах на диске
  (проверить YAML `MobChaserView.prefab`).
- [ ] Commit `feat(app-n6g): Т19 — слой и прокси 3D-прицела`.

### Т20: визуал прицела (луч, конус, маркер)

**Files:** Create `Presentation/AimRayView.cs` (**только LineRenderer**
дуло→точка, `AimRayAlpha/Width`; живёт при `AimHeld`; материал —
`GetOrCreateUnlitMaterial`); Modify `Presentation/CrosshairView.cs` (конус
— **радиус строго по хип-формуле через хелпер Т15
`WeaponSystem.HipSpreadRadians` — без `settleFactor`** (PC6/PD15); конус
скрыт при `AimHeld`; `_marker` — круглый мини-диск, при `AimHeld`
переключает scale × `AimDotScale`/материал и служит точкой прицела —
**второй маркер не заводится, PC8**), `Editor/StageOneSceneBootstrap.cs`.

- [ ] `AimRayView` + бутстрап-создание (компиляция).
- [ ] `CrosshairView`: хелпер-формула + режимы (компиляция).
- [ ] Apply ×2 идемпотентно.
- [ ] Commit `feat(app-n6g): Т20 — луч прицела и честный круг разброса`
  (+ `.cs.meta`).

### Т21: снаряды/дуло/предсказание/декали в вьюхах

**Files:** Modify `Presentation/SimulationRunner.cs` (**новое свойство
`RenderMuzzleHeight`** = `RenderCurr.Player.SlideTimer > 0 ?
SlideMuzzleHeight : MuzzleHeight` из `World.Config.Hero` — единственный дом
тернара, PC7; `WouldFireThisFrame` + `CanFireWhileSlide` из
`World.Config.Weapon` — B7), `Presentation/ViewRegistry.cs` (интерполяция
`PrevHeight→Height`, удалить `ProjectileOffset`; `TracerScale`),
`Presentation/MuzzleFlashView.cs` (**оба чтения `MuzzleLiftY` :97 и :132**
→ `_runner.RenderMuzzleHeight` — PC7), `Presentation/AimRayView.cs` (начало
луча — `RenderMuzzleHeight`), `Presentation/PersistentPropsDirector.cs`
(гильзы `:220` → `RenderMuzzleHeight`; декали `ProjectileBlocked` — стена по
`HitDir`/высоте `Amount`, пол (`HitDir == 0`) плашмя; **удалить
`ComputeBlockNormal`/`SafeNormalize`/поле `_arena` + его `SetRef` — нормаль
теперь из события, двух домов не остаётся, PC4**; class-doc обновить),
`Scripts/Data/GameFeelConfig.cs` (док-комментарий про `ProjectileOffset` —
PB11; `MuzzleLiftY` пометить неиспользуемым). `AudioDirector` высоту НЕ
читает — из списка исключён (PC7).

- [ ] `RenderMuzzleHeight` + `WouldFireThisFrame` (компиляция).
- [ ] `ViewRegistry` высота + офсет-удаление (компиляция).
- [ ] Дульные потребители ×3 + `AimRayView` (компиляция).
- [ ] Декали по событию + удаление `ComputeBlockNormal` (компиляция).
- [ ] Полный EditMode (сим не тронут — регресс) → PASS; Apply ×2.
- [ ] Commit `feat(app-n6g): Т21 — вьюхи снарядов, дула и декалей в 3D`.

### Т22: HUD «Буст» + deny + зонный фидбек → ВЕХИ В1+В2

**Files:** Modify `Presentation/HudController.cs` (`_staminaFill` +
пульс deny), `Editor/StageOneSceneBootstrap.cs` (третий `GetOrCreateBar`
«Буст» + `SetRef` — B8), `Presentation/GameFeelDirector.cs` (хитстоп ×
`HeadHitstopScale` по `Zone == Head`), `Presentation/AudioDirector.cs`
(питч ± `ZoneHitPitchOffset`; звук deny; звук рикошета),
`Presentation/PersistentPropsDirector.cs` (искра рикошета — через
существующий `_blockSparkPool` с `LookRotation(HitDir)`, шестой пул не
заводится — PC13; пыль слайда — burst `SlideDustBurstCount` на
`PlayerSlideStarted` по прецеденту `HitSparkBurstCount`),
`Presentation/DevOverlay.cs` (`SlidesUsed`/`HeadshotKills` — A16).

- [ ] HUD-полоска + бутстрап (компиляция).
- [ ] Deny-пульс + звук (компиляция).
- [ ] Зонный хитстоп/питч (компиляция).
- [ ] Рикошет-искра + слайд-пыль через существующие пулы (компиляция).
- [ ] DevOverlay строки; Apply ×2; полный EditMode-регресс → PASS.
- [ ] Commit `feat(app-n6g): Т22 — HUD Буста и фидбек зон` →
  **ВЕХА В1 «Руки»**, затем **ВЕХА В2 «Прицел»** (контент В2 готов с Т21 —
  PD19; балансовый PR В2: радиус снаряда, `GunnerVisualScale 0.4 → ≈0.76` —
  рука владельца) → фикс-волны.

### Т23: слайд-анимация куклы

**Files:** импорт Mixamo `Running Slide` → `_Ring/Animations/` (ASSETS-001
§4.2, `CREDITS.md` + «no redistribution» — A10; **строка-amendment в
ASSETS-001 со ссылкой на ADR-002 A10** — PB12); Modify
`Presentation/PlayerVisual.cs` (слайд-стейт по `SlideTimer > 0`; фолбэк —
процедурный присед; гизмо + `ContextMenu Capture` — B15),
`Editor/ThirdPartyAnimatorBootstrap.cs`.

- [ ] Импорт + CREDITS + ASSETS-001-строка.
- [ ] Аниматор-стейт + `PlayerVisual` (компиляция; Apply ×2).
- [ ] Commit `feat(app-n6g): Т23 — слайд-анимация Сборщика`.

### Т24: обломки (после `app-1zf`) → ВЕХА В3

**Files:** сначала `app-1zf` (разбор FBX → bd note + close); Create
`Presentation/GibView.cs` (+meta; Rigidbody-часть, слой
`PersistentPropsDirector.CasingsLayer`; **settle-условие ПЕРЕНОСИТСЯ
ДОСЛОВНО из `CasingView` — freeze по скорости + hard-cap, не голый таймер
(урок app-4qc) — PC14**; `GibPhysicsSeconds`); Modify
`Presentation/PersistentPropsDirector.cs` (пятый `RingBuffer<GibView>` +
`Clear()` — D10; `HandleMobDied` по `Zone`/`HitDir`: хедшот — голова
импульсом `GibHeadImpulseSpeed` (VelocityChange), перебор — разброс
`GibExplosionSpeed`), `Presentation/CorpseView.cs` (ветка «без головы» —
B5); меши → `_Ring/Gibs/` (LFS).

- [ ] `app-1zf`: результат в bd, `bd close app-1zf`.
- [ ] `GibView` + пул + `Clear` (компиляция).
- [ ] `HandleMobDied`-ветки + `CorpseView` (компиляция; Apply ×2).
- [ ] Полный EditMode-регресс → PASS.
- [ ] Commit `feat(app-n6g): Т24 — обломки мехов по зоне и вектору` →
  **ВЕХА В3 «Мясо»** → фикс-волны.

### Т25: финальные гейты и закрытие

- [ ] Полный EditMode → 0 failed (счётчик + golden в bd note).
- [ ] Сборки: `RING_BUILD_ROOT=$RING_ROOT/builds "$UNITY" -batchmode -quit …
  BuildLinuxServer`, затем `BuildWindowsClient` → exit 0.
- [ ] Финал-ревью (opus) → фикс-волна → `superpowers:
  finishing-a-development-branch` → PR (`gh pr create`) → merge.
- [ ] bd: `bd close app-nco` (В3-evidence), note `app-n6g`;
  **разблокировка Э2: `bd dep remove app-5nu app-n6g`** (или close эпика —
  решение владельца) — PD20.
- [ ] jsonl-дрифт — chore-коммит в main; уборка worktree по
  `finishing-a-development-branch`.

---

## Self-review плана (v2, выполнен)

- Покрытие спеки v5 — карта субагента D: все разделы §3.2–§3.6/§4/§5/§7
  имеют таски; пробелы PD1–PD22 закрыты правками выше (PD1 —
  `GunnerVisualScale` через балансовый PR В2; PD6 — Presentation-таски
  разбиты на подшаги с компиляцией между).
- Типы сквозные: `HitZone` живёт в `Core/SimStates.cs` (Т6) и виден
  Presentation; `HipSpreadRadians` (Т15) → `CrosshairView` (Т20);
  `RenderMuzzleHeight` (Т21) → `MuzzleFlashView`/гильзы/`AimRayView`;
  `MoveWithCollisions out` (Т11) → Т12; `PredictPos(+maxSpeed)` = спека v5.
- Плейсхолдеров нет; formuлы — спека §3.2/§3.3 v5 дословно.
