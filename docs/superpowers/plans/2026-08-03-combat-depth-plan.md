# План имплементации: Боёвка-глубина (app-n6g)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development
> (рекомендовано) или superpowers:executing-plans. Шаги — чекбоксы `- [ ]`.

**Цель:** двухрежимный прицел (ПКМ-луч / от бедра) + хитзоны голова/тело/ноги +
Буст-мувмент (дэш/слайд/рикошет/связки) + обломки + упреждающий замах чейзера —
по спеке `docs/superpowers/specs/2026-08-03-combat-depth-spec.md` (v4).

**Архитектура:** вертикаль только у снарядов и хит-объёмов; движение 2D;
все новые механики — детерминированные поля `PlayerState`/`ProjectileState` +
чистые системы; Presentation потребляет события/снапшот. Баланс — SO → 
`SimConfigBuilder`.

**Стек:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics; без новых пакетов.

## Global Constraints (действуют в каждом таске)

- Спека v4 — единственный источник формул; при конфликте план проигрывает спеке.
- CR1: `Simulation/**` без UnityEngine (только Unity.Mathematics). CR2: TDD
  RED→FAIL→GREEN→PASS→commit. CR6: ни одного балансового числа в коде.
- **Golden:** каждый сим-таск, меняющий поведение, перепинивает
  `GoldenHash_ScriptedScenario` (константа в `DeterminismTests.cs:146`);
  старый хеш Э1 `0x39B4C57694AD8770` уже зафиксирован здесь и в спеке §7.
  Перепин = запустить тест, взять actual из лога, вписать, перезапустить PASS.
- Тесты: `cd <worktree> && "$UNITY" -runTests -batchmode -projectPath client
  -testPlatform EditMode -testResults /tmp/claude-1000/.../scratchpad/t<N>.xml
  -logFile /tmp/claude-1000/.../scratchpad/t<N>.log -testFilter "<Full.Class[.Method]>"`
  (БЕЗ `-quit`; exit 0 = зелёный; перед прогоном
  `ps aux | grep -i "[U]nity/Hub/Editor.*projectpath"` — Editor владельца
  закрыт; таймаут ~300 с). `$UNITY=~/Unity/Hub/Editor/6000.3.21f1/Editor/Unity`.
- Коммиты: `feat|test|fix(app-n6g): …` русским + трейлер
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед коммитом
  секрет-чек `git status --short --untracked-files=all | grep -E '\.(env|pem|key)$|secrets/'`.
- Словарь: код `Stamina`/`HitZone`/`slide` (англ.); user-facing строки — «Буст».
- После каждого таска — `bd note app-n6g "<таск>: done, <evidence>"`.

Файловая карта (Create): `Simulation/Combat/HitZones.cs` (enum + классификация),
`Simulation/AI/Targeting.cs` (+PredictPos), `Presentation/AimRayView.cs`,
`Presentation/GibView.cs`; Tests: `ProjectileHeightTests.cs`, `HitZoneTests.cs`,
`StaminaTests.cs`, `SlideTests.cs`, `DashRicochetTests.cs`. Остальное — Modify
(перечислено в тасках).

---

## Фаза 1 — сим-ядро: данные, RNG, снаряды, зоны

### Т1: конфиг-поля зон и дул (Hero + Mob)

**Files:** Modify `Scripts/Data/HeroConfig.cs`, `MobConfig.cs`,
`Scripts/Simulation/Core/SimConfig.cs` (`HeroSimConfig`, `MobSimConfig`),
`Scripts/Data/SimConfigBuilder.cs`; Test `Tests/EditMode/ConfigTests.cs`,
`TestConfigs.cs`.
**Produces:** `HeroSimConfig.{LegsTop,BodyTop,HeadTop,LegsDamageMult,
BodyDamageMult,HeadDamageMult,SlideProfileTop,MuzzleHeight,SlideMuzzleHeight,
MaxAimHeight}` (float); `MobSimConfig.{LegsTop,BodyTop,HeadTop,LegsDamageMult,
BodyDamageMult,HeadDamageMult,MuzzleHeight,SwingLeadFactor,SwingLeadMaxMeters}`.
Дефолты и валидация — спека §3.5 (чейзер 0.35/0.75/1.05, мульты 0.75/1.0/**1.0**;
ганнер 0.55/1.40/1.90, 0.75/1.0/1.5, `MuzzleHeight 0.95`).

- [ ] RED: в `ConfigTests` — `Validate_ZoneOrderViolated_Throws`
  (`LegsTop=1.0,BodyTop=0.5` → `ArgumentException`) и
  `Validate_SlideProfileAboveGunnerMuzzle_Throws`
  (`SlideProfileTop=0.9` при `Gunner.MuzzleHeight=0.95, ProjectileRadius=0.15`
  → бросает: `0.9+0.15 ≥ 0.95`).
- [ ] Прогон `-testFilter "Ring.Simulation.Tests.ConfigTests"` → FAIL
  (полей нет — компиляция чинится добавлением полей до ассертов).
- [ ] GREEN: поля в SO-классах (`[Min(0f)]` по конвенции файла) → sim-структуры
  → маппинг в `SimConfigBuilder` → правила `Validate` §3.5 (пояса per-архетип,
  `SlideProfileTop + Gunner.ProjectileRadius < Gunner.MuzzleHeight`, все);
  `TestConfigs.Default()` + `AssertHeroEqual`/`AssertMobEqual` + gunner-блок в
  `Build_DefaultAssets_MatchesTestConfigsBaseline` — те же значения (K4/A3).
- [ ] Прогон `ConfigTests` целиком → PASS.
- [ ] Commit `feat(app-n6g): Т1 — конфиг зон/дул + валидация`.

### Т2: конфиг-поля Буст/слайд/прицел/разброс

**Files:** те же + `WeaponConfig.cs`/`WeaponSimConfig`.
**Produces:** `HeroSimConfig.{StaminaMax 90, DashStaminaCost 48,
SlideStaminaCost 13, LinkedDashStaminaCost 16, StaminaRegenPerSec 22,
StaminaRegenDelay 0.72, SlideSpeed 13.5, SlideDuration 0.52,
SlideSteerRadPerSec 1.2, SlideMinSpeedFrac 0.75, RunUpSeconds 1.18,
RunUpDecayMult 3.0, SlideBufferWindow 0.15, LinkWindowSeconds 0.25,
PostDashSlideWindow 0.32, SlideWallStopDot 0.7, RicochetRetention 0.8,
AimMoveSpeedFrac 0.8, AimSlideSpeedMult 0.5, AimSettleSeconds 0.25}`;
`WeaponSimConfig.{CanFireWhileSlide true, SpreadRunMult 1.5,
SpreadSlideMult 2.0, RunSpreadSpeedFrac 0.5}`.

- [ ] RED: `Validate_ZeroStaminaRegen_Throws` (`StaminaRegenPerSec=0`),
  `Validate_AimFracNotAboveSlideFrac_Throws`
  (`AimMoveSpeedFrac=0.75 == SlideMinSpeedFrac` — строгое `>`, D15).
- [ ] Прогон `ConfigTests` → FAIL.
- [ ] GREEN: поля + маппинг + полный список валидаций §3.5 + lockstep
  `TestConfigs`/`Assert*Equal`.
- [ ] Прогон `ConfigTests` → PASS.
- [ ] Commit `feat(app-n6g): Т2 — конфиг Буста/слайда/прицела`.

### Т3: RNG-split `_spreadRng`/`_waveRng`

**Files:** Modify `Simulation/Core/SimulationWorld.cs` (поля, сид, `SaveState`/
`RestoreState`, `StateHash` канонический порядок), `WorldSave.cs`,
`Combat/WeaponSystem.cs` (`w.SpreadRng`), `AI/WaveSystem.cs` (`w.WaveRng`);
Test `DeterminismTests.cs`, `WorldLifecycleTests.cs`.
**Produces:** `internal ref Random SpreadRng`, `internal ref Random WaveRng`
(сид: `folded ^ 0xB5297A4D` / `folded ^ 0x68E31DA4`, ноль-guard как у Э1).

- [ ] RED: `SpreadDrawDoesNotShiftWaves` — два мира, один seed; в первом 100
  тиков c `FireHeld=true`, во втором — без стрельбы; после смерти всех
  снарядов/одинакового числа тиков состав первой волны (типы/позиции мобов
  при спавне) идентичен.
- [ ] Прогон `DeterminismTests` → FAIL (общий поток сдвигается).
- [ ] GREEN: два `Random`; `SaveState/RestoreState/StateHash` (порядок: tick →
  spreadRng.state → waveRng.state → …); перепин golden.
- [ ] Прогон `DeterminismTests` + `WorldLifecycleTests` → PASS.
- [ ] Commit `feat(app-n6g): Т3 — раздельные RNG-потоки оружия и волн`.

### Т4: 3D-поля снаряда + сигнатура спавна

**Files:** Modify `Core/SimStates.cs` (`ProjectileState + Height, PrevHeight,
VelZ`), `SimulationWorld.cs` (`SpawnProjectile(owner, pos, vel, height, velZ,
damage, radius, ttl)` + тест-двойник + `HashProjectile`), `WorldSave` (авто —
структуры целиком), `Combat/ProjectileSystem.cs` (интеграция
`PrevHeight = Height; Height += VelZ * dt`), `Combat/WeaponSystem.cs`
(временно `height: cfg.MuzzleHeight… → hero.MuzzleHeight, velZ: 0`),
`AI/MobAiSystem.cs` (ганнер: `cfg.MuzzleHeight, 0f`); Test: 9 call-sites
(`ProjectileTests` ×7, `WorldLifecycleTests:46`, `DeathTests:54`) —
`height: 1f, velZ: 0f`.
**Produces:** сигнатура выше; горизонтальные снаряды ведут себя как Э1.

- [ ] RED: в новом `ProjectileHeightTests` —
  `Projectile_WithVelZ_AdvancesHeightPerTick` (спавн `height 1f, velZ −3f`,
  1 тик ⇒ `Height == 1f − 3f * TickDt` с точностью 1e-5).
- [ ] Прогон `-testFilter ...ProjectileHeightTests` → FAIL (нет полей).
- [ ] GREEN + все call-sites; `EveryPlayerAndStatsFieldAffectsHash` зелёный
  сам (рефлексия); перепин golden.
- [ ] Полный EditMode-прогон (без фильтра) → 93 старых + новые PASS.
- [ ] Commit `feat(app-n6g): Т4 — высота снаряда в состоянии и спавне`.

### Т5: перебор кандидатов min-scan + скретч

**Files:** Modify `Combat/ProjectileSystem.cs`, `SimulationWorld.cs`
(преаллоцированный скретч `_projCandidates` по образцу `_sepForces`, вне
хеша/сейва); Test `ProjectileTests.cs` (без изменений — регресс),
`AllocationTests.cs`.
**Produces:** внутренний проход: собрать кандидатов (барьер/мобы/игрок) с
`t`, повторный выбор минимального `t` среди неисключённых, тай-брейк —
меньший индекс; исход Э1 бит-в-бит.

- [ ] RED-регресс: прогон `ProjectileTests` + `DeterminismTests` ДО правки —
  зелёные (фиксация базы).
- [ ] Рефактор (высотного теста ещё нет — только структура перебора).
- [ ] Прогон `ProjectileTests`, `DeterminismTests` (golden НЕ меняется —
  поведение то же), `AllocationTests` → PASS.
- [ ] Commit `feat(app-n6g): Т5 — перебор кандидатов min-scan без сортировки`.
- [ ] (RED-шага с новым тестом нет осознанно: чистый рефактор под зелёной
  базой; допустимо по TDD как refactor-фаза Т4.)

### Т6: высотный hit/no-hit + зоны + урон с множителями

**Files:** Create `Simulation/Combat/HitZones.cs`
(`enum HitZone : byte { None, Legs, Body, Head }` +
`static HitZone Classify(float h, in <cfg>)` — кламп в `[0, HeadTop]`;
`static bool Overlaps(float hEnter, float hExit, float radius, float top)`);
Modify `ProjectileSystem.cs` (интервал `[tEnter, tExit]` по высоте — вход
`Geometry.SegmentCircle` t + выход из круга; отклонение кандидата продолжает
скан), `SimulationWorld.cs` (`DamageMob(index, dmg, pos, zone, dir)`,
`DamagePlayer(dmg, pos, zone, dir)`, мульт ДО эмита, `HeadshotKills` через
хелпер с гвардом `Alive`), `Core/SimEvents.cs` (`SimEvent + HitZone Zone,
float2 HitDir`), `AI/MobAiSystem.cs` (кулак: `Body`, без мульта,
`dir = normalize(p.Pos − m.Pos)`); Test Create `HitZoneTests.cs`,
Modify `ProjectileHeightTests.cs`, `EventTests.cs`.
**Produces:** `HitZone`, сигнатуры урона; событие с зоной/вектором.

- [ ] RED (пакет, все в FAIL):
  `Shot_OverChaserHead_HitsGunnerBehind` (чейзер (2.5,0), ганнер (9,0),
  прицельный спавн снаряда тестовым спавном `height 1.0, velZ +0.072*|v|`
  → умирает ганнер, чейзер цел); `Graze_AtHeadTopPlusRadius_HitsAsHead`
  (`h = HeadTop + Radius − 1e-4` ⇒ хит, `Zone == Head`);
  `RejectedTall_DoesNotShadowFarther` (M5); `EqualT_TieBreaksLowerIndex`;
  `ChaserHead_MultIsOne` (12 урона), `GunnerHead_Mult15` (18);
  `Hit_Amount_IsPostMultiplier`.
- [ ] Прогон двух новых классов → FAIL.
- [ ] GREEN: по формулам §3.2; перепин golden.
- [ ] Полный EditMode → PASS.
- [ ] Commit `feat(app-n6g): Т6 — хитзоны, множители, события с зоной`.

### Т7: пол-кандидат

**Files:** Modify `ProjectileSystem.cs` (`t_floor = (proj.Radius − Height) /
(VelZ * dt)` при `VelZ < 0`, кандидат в общем порядке → `ProjectileBlocked`,
`Amount = высота контакта`, `HitDir = (0,0)`; стены: `HitDir = normal` из
`SweepArena`); Test `ProjectileHeightTests.cs`, `EventTests.cs`.

- [ ] RED: `FloorHit_BlocksAtFloorPoint_MobBehindUnharmed` (наклонный вниз
  снаряд, моб за точкой касания — жив; событие `ProjectileBlocked` с
  `HitDir == float2.zero`); `WallBlock_CarriesNormalAndHeight`.
- [ ] Прогон → FAIL.
- [ ] GREEN; перепин golden.
- [ ] Прогон класса + `EventTests` → PASS.
- [ ] Commit `feat(app-n6g): Т7 — пол как кандидат хитрега`.

### Т8: `SimInput.AimHeight/AimHeld/SlideRequested` + `Sanitize`

**Files:** Modify `Core/SimInput.cs` (поля; `SimInputFrame.ForTick`:
`SlideRequested` — edge тик 0, `AimHeld` — уровень), `SimulationWorld.Sanitize`
(не-конечное `AimHeight` → `Hero.MuzzleHeight`; кламп `[0, MaxAimHeight]`);
Test `DeterminismTests.cs` (`HostileInput_...` + NaN/∞ `AimHeight`),
`AccumulatorTests.cs`-стиль edge для `SlideRequested` (в `EventTests` не надо —
место: `SlideTests` появится в Т10; здесь — минимальный тест в
`DeterminismTests`).

- [ ] RED: `HostileInput_...` дополнить `AimHeight = float.NaN, AimHeld = true`
  → мир конечен и детерминирован; `Sanitize_ClampsAimHeight` (5f → 2.2f).
- [ ] Прогон `DeterminismTests` → FAIL.
- [ ] GREEN (симуляция пока высоту не читает — только санитайз; golden не
  меняется).
- [ ] Прогон → PASS.
- [ ] Commit `feat(app-n6g): Т8 — прицельные входы и санитайз`.

---

## Фаза 2 — сим-мувмент: Буст, слайд, рикошет, замах, режимы огня

### Т9: Буст-ядро

**Files:** Modify `Core/SimStates.cs` (`PlayerState + Stamina,
StaminaRegenDelayTimer`), `SimulationWorld.cs` (init `Stamina = StaminaMax`;
смерть — заморозка; `ApplyConfig` клампы; `Emit(StaminaDenied, …, amount:
недостающее)`), `Core/SimEvents.cs` (`StaminaDenied`),
`Movement/PlayerMovementSystem.cs` (дэш: гейт `Stamina ≥ DashStaminaCost`,
списание, `StaminaRegenDelayTimer = StaminaRegenDelay`; реген
`StaminaRegenPerSec * dt` при нулевой задержке и вне дэша/слайда);
Test Create `StaminaTests.cs`; Modify `HotTweakTests.cs`, `TestConfigs.cs`
(+ четвёртый вариант `RegenFixture()`: `SlideDuration 0.9, StaminaRegenDelay
0.3` — M16).
**Produces:** поля выше; дэш без Буста не срабатывает + `StaminaDenied`.

- [ ] RED: `StartsAtFullStamina`; `Dash_CostsStamina` (90→42);
  `Dash_InsufficientStamina_DeniedWithEvent`; `Regen_WaitsDelayThenRefills`;
  `Regen_FrozenDuringSlide_OnFixture` (на `RegenFixture()`);
  `HotTweak_ClampsStaminaToNewMax`.
- [ ] Прогон `StaminaTests` → FAIL.
- [ ] GREEN; перепин golden (дэш теперь тратит ресурс — сценарий дэшится).
- [ ] Прогон `StaminaTests` + `HotTweakTests` + `DashTests` → PASS.
- [ ] Commit `feat(app-n6g): Т9 — ресурс Буст (код Stamina)`.

### Т10: слайд — гейт, старт, тик, выход

**Files:** Modify `Core/SimStates.cs` (`+ SlideTimer, SlideDir, SlideBufferTimer,
RunUpTimer, PostDashSlideTimer`), `PlayerMovementSystem.cs` (канон таймеров
`math.max(0,·−dt)`; разгон: `min(+dt, RunUpSeconds)` при `|Vel| ≥ frac×Max` и
вне дэша/слайда, иначе `max(0, −RunUpDecayMult×dt)`; буфер-латч по образцу
дэша; старт: гейт (разгон ИЛИ пост-дэш) + Буст + не в дэше; `SlideDir`
фолбэк-цепочка MoveDir→Vel→`normalizesafe(AimPoint−Pos,(1,0))`; тик:
`Vel = SlideDir × SlideSpeed`, руление ≤ `SlideSteerRadPerSec×dt`; выход —
вынос без среза; `PostDashSlideTimer` ставится в тик обнуления `DashTimer`),
`SimulationWorld.cs` (смерть-очистка, клампы, `SlidesUsed`-хелпер,
`Emit(PlayerSlideStarted, pos, HitDir=SlideDir)`); Test Create `SlideTests.cs`.
**Produces:** слайд без стены/связки; `PlayerState.SlideTimer` виден Weapon/
Projectile-таскам.

- [ ] RED: `Slide_RequiresRunUpOrPostDash`; `RunUp_DecaysBelowThreshold`;
  `Slide_ResetsRunUp_NoChain` (M2: слайд→слайд сразу — отказ);
  `Slide_MutualExclusionWithDash` (C7); `SlideDir_FallbackToAim_WhenIdle`
  (D6); `Slide_ExitKeepsMomentum` (C22); `Death_ClearsSlideState` (M11);
  `Slide_EmitsStartEvent`.
- [ ] Прогон `SlideTests` → FAIL.
- [ ] GREEN; перепин golden (сценарий пока без SlideRequested — golden
  меняется только от полей в хеше).
- [ ] Прогон `SlideTests` + `MovementTests` + `DashTests` → PASS.
- [ ] Commit `feat(app-n6g): Т10 — слайд: гейт разгона и жизненный цикл`.

### Т11: слайд у стены + окно связки + слайд-профиль

**Files:** Modify `PlayerMovementSystem.cs` (`MoveWithCollisions` →
`out bool hit, out float2 normal` (первый контакт); call-sites `:60`,
`MobAiSystem:209` — discard'ы; гашение: `dot(−normal, SlideDir) >
SlideWallStopDot` ⇒ `SlideTimer=0`, `Vel = normalize(Vel)×MaxSpeed`,
`RunUpTimer=0`, БЕЗ окна; штатный выход ⇒ `LinkWindowTimer =
LinkWindowSeconds`; дэш в окне: цена `LinkedDashStaminaCost`, игнор остатка
кулдауна, окно потребляется, кулдаун ставится), `Core/SimStates.cs`
(`+ LinkWindowTimer`), `ProjectileSystem.cs` (профиль: при `SlideTimer > 0`
потолок игрока = `SlideProfileTop`); Test `SlideTests.cs`, `HitZoneTests.cs`,
`StaminaTests.cs` (экономика связки).
**Produces:** полная связка Д5/Д6; `MoveWithCollisions` с нормалью.

- [ ] RED: `WallStop_KillsSlide_NoLinkWindow` (M3);
  `SlideAlongWall_Continues` (острый угол);
  `LinkedDash_DiscountAndCooldownBypass_ConsumesWindow` (C6);
  `PerfectChain_CostsExactly90`; `GunnerShot_MissesSlidingHero` (M13);
  `SlidingHero_HitOnlyBelowProfile`.
- [ ] Прогон → FAIL.
- [ ] GREEN; перепин golden.
- [ ] Прогон `SlideTests`+`StaminaTests`+`HitZoneTests`+`MobAiTests` → PASS.
- [ ] Commit `feat(app-n6g): Т11 — стена, окно связки, слайд-профиль`.

### Т12: рикошет дэша

**Files:** Modify `Core/SimStates.cs` (`+ DashSpeedCur`),
`Core/Geometry.cs` (`static float2 Reflect(float2 dir, float2 normal)` —
guard `dot ≥ 0` возвращает dir), `PlayerMovementSystem.cs` (при
`DashTimer > 0` и hit: `DashDir = Geometry.Reflect(…)`, `DashSpeedCur ×=
RicochetRetention`, применяется со следующего тика; ≤1 отражения/тик;
дэш-тик `Vel = DashDir × DashSpeedCur`; старт `DashSpeedCur = DashSpeed`),
`SimulationWorld.cs` (`Emit(DashRicocheted, contact, HitDir=normal)`,
кламп `DashSpeedCur`), `Core/SimEvents.cs` (`DashRicocheted`);
Test Create `DashRicochetTests.cs`; Modify `DashTests.cs`
(`DashIntoObstacle_StopsAtSurface_NoTunnel` → переименовать в
`DashIntoObstacle_Ricochets_NoTunnel`, ассерт на отражение — A9),
`GeometryTests.cs` (`Reflect_Mirror`, `Reflect_GuardSameSide`).
**Produces:** `Geometry.Reflect`; событие с нормалью.

- [ ] RED: `Ricochet_MirrorsDashDir_NextTick` (D16: тик контакта — скольжение,
  следующий — отражённый вектор); `Ricochet_AppliesRetention` (0.8 →
  `DashSpeedCur 24`); `Ricochet_KeepsIframes`; `Ricochet_OncePerTick`;
  `Ricochet_EmitsEventWithNormal`; явная фикстура дистанции
  (`DashDuration 0.09, DashSpeed 30` в тесте ⇒ 4.0 м — C14).
- [ ] Прогон → FAIL.
- [ ] GREEN; перепин golden.
- [ ] Прогон `DashRicochetTests`+`DashTests`+`GeometryTests`+`MovementTests`
  → PASS.
- [ ] Commit `feat(app-n6g): Т12 — зеркальный рикошет дэша`.

### Т13: упреждающий замах чейзера

**Files:** Modify `Simulation/AI/Targeting.cs`
(`static float2 PredictPos(float2 pos, float2 vel, float maxSpeed,
float seconds, float factor, float maxLead)` — `lead = vel`, длина клампится
`maxSpeed`, смещение клампится `maxLead`), `AI/MobAiSystem.cs` (вход в
`Telegraph`: `math.distance(m.Pos, predicted) ≤ cfg.AttackRange`);
Test `MobAiTests.cs`.
**Produces:** `Targeting.PredictPos` (используется только чейзером;
`AimWithLead` не трогается — B12).

- [ ] RED: `Chaser_TelegraphsAheadOfRunner_AndConnects` (игрок бежит на
  чейзера: замах начинается раньше входа в `AttackRange`, удар попадает);
  `Chaser_Standing_FarPlayer_NoTelegraph` (D8);
  `Chaser_DashDoesNotBaitFromAfar` (лид клампится MaxSpeed — A4);
  `Chaser_LeadClampedByMaxMeters`;
  `SwingLeadZero_EntryTickEqualsE1Rule` (D9: тик входа == тик
  `dist ≤ AttackRange`).
- [ ] Прогон `MobAiTests` → FAIL.
- [ ] GREEN; перепин golden.
- [ ] Прогон `MobAiTests` целиком → PASS.
- [ ] Commit `feat(app-n6g): Т13 — упреждающий замах чейзера`.

### Т14: прицельный режим в движении (кап, слайд-мульт, settle)

**Files:** Modify `Core/SimStates.cs` (`+ AimSettleTimer`),
`PlayerMovementSystem.cs` (при `AimHeld`: целевой бег `MaxSpeed ×
AimMoveSpeedFrac`; `slideSpeed = SlideSpeed × (AimHeld ? AimSlideSpeedMult :
1)` с того же тика; `AimSettleTimer`: `+dt` при `AimHeld` до
`AimSettleSeconds`, `−2dt` иначе), `SimulationWorld.cs` (кламп);
Test `MovementTests.cs`, `SlideTests.cs`, `StaminaTests.cs` (не задевается —
регресс).

- [ ] RED: `AimHeld_CapsRunSpeed` (стационар = 6.0);
  `AimReleased_RestoresMaxSpeed` (D8); `AimHeld_SlowsSlide_SameTick` (6.75);
  `RunUp_ReachableUnderAimCap` (кап 0.8 > порога 0.75);
  `AimSettle_GrowsAndDecays`.
- [ ] Прогон → FAIL.
- [ ] GREEN; перепин golden.
- [ ] Прогон `MovementTests`+`SlideTests` → PASS.
- [ ] Commit `feat(app-n6g): Т14 — кап и слайд-штраф прицельного режима`.

### Т15: два режима огня в `WeaponSystem`

**Files:** Modify `Combat/WeaponSystem.cs` — формулы §3.2 дословно:
aim: `vel3 = normalizesafe(float3(AimPoint − muzzle2D, AimHeight − muzzleH),
fallback3) * ProjectileSpeed`; эффективный разброс `a_hip × (1 −
AimSettleTimer/AimSettleSeconds)`; hip: `VelZ = 0`, `a = (SpreadRad +
RecoilOffset) × moveMult`; `muzzleH = SlideTimer > 0 ? SlideMuzzleHeight :
MuzzleHeight`; spread-draw из `SpreadRng` только при `a > 0`; поворот
горизонтали + перенормировка на `ProjectileSpeed`; `spawnPos2D = p.Pos +
dir2D × (MuzzleOffset + overshoot × length(vel3.xy))`, `height = muzzleH +
overshoot × VelZ`; гейт `CanFireWhileSlide`; Test `ProjectileHeightTests.cs`,
`WeaponTests.cs`.

- [ ] RED: `AimedShot_HitsExactPoint_IncludingFloor` (точка пола под
  курсором → `ProjectileBlocked` ровно там); `AimedShot_FullSpeed3D`
  (`|(Vel,VelZ)| == ProjectileSpeed` при ненулевом угле и разбросе — K10);
  `HipShot_HorizontalAtMuzzleHeight`; `HipSpread_RunAndSlideMultipliers`
  (×1.5 на пороге `RunSpreadSpeedFrac`, ×2 в слайде, ×1 в покое — D8);
  `FirstAimTick_SpreadNotZero` (C2); `Recoil_AccumulatesWhileAimed` (D8);
  `SlideFire_FromSlideMuzzleHeight`.
- [ ] Прогон → FAIL.
- [ ] GREEN; перепин golden.
- [ ] Прогон `WeaponTests`+`ProjectileHeightTests`+`HitZoneTests` → PASS.
- [ ] Commit `feat(app-n6g): Т15 — двухрежимный огонь`.

### Т16: golden-сценарий и финальная сим-сверка

**Files:** Modify `DeterminismTests.cs` (`Scripted`: `SlideRequested` ~5%,
`AimHeld` — залипающий уровень, переключение ~3%/тик, `AimHeight` вариация
0–2.2 — D1/A5); Test — весь EditMode.

- [ ] RED: обновлённый сценарий → golden FAIL (ожидаемо).
- [ ] Перепин константы (это ФИНАЛЬНЫЙ перепин; значение — в описание PR).
- [ ] Полный EditMode-прогон без фильтра → все PASS, 0 failed.
- [ ] `bd note app-n6g "Фаза 2 закрыта: голден <новый хеш>, N тестов"`.
- [ ] Commit `test(app-n6g): Т16 — golden покрывает слайд и прицельный режим`.

---

## Фаза 3 — Presentation/Editor (гейт: компиляция + идемпотентность Apply + вехи)

### Т17: GameFeel-поля + маркер-ключи SO

**Files:** Modify `Scripts/Data/GameFeelConfig.cs` (все поля §3.5, маркер =
`AimDotScale` последним, док «keep LAST» переносится),
`Editor/EditorBootstrapUtils.cs` (`EnsureAssetHasKey(Object so, string
assetPath, string markerField)` — `File.ReadAllText(...).Contains` →
`SetDirty`), `Editor/StageOneSceneBootstrap.cs` (вызовы для GameFeel/Hero/
Weapon/Chaser/Gunner; `ApplyGunnerDefaults` + зонные/дульные поля §3.5,
гейт `gunnerCreated || !markerPresent` — A1).
Критерий: `Apply` дважды → второй раз `git status` по `Assets/Data` пуст
(идемпотентность); в `.asset`-файлах появились новые ключи с дефолтами.

- [ ] Реализация → batchmode `-executeMethod` Apply (сцены Э1) ×2.
- [ ] Проверка diff/идемпотентности + значений ганнера в YAML.
- [ ] Commit `feat(app-n6g): Т17 — доставка новых SO-полей маркер-ключами`.

### Т18: инпут (бинды + сэмплер)

**Files:** Modify `Assets/InputSystem_Actions.inputactions` (`Gameplay/Dash`
kb: space→leftShift; `Gameplay/Slide`: space + gamepad buttonEast;
`Gameplay/AimHold`: rightButton + leftTrigger), `Presentation/InputSampler.cs`
(`SlideRequested`-латч: подписка в `Enable`, `ClearLatches`; `AimHeld =
_aimHold.IsPressed()` БЕЗ `WasPressedThisFrame` — C16), `SimulationRunner.cs`
(проброс в `SimInput`).
Критерий: компиляция; PlayMode-смоук руками владельца НЕ требуется до вехи.

- [ ] Реализация.
- [ ] Batchmode-компиляция (Apply-прогон) без ошибок.
- [ ] Commit `feat(app-n6g): Т18 — бинды слайда и прицела`.

### Т19: прицел — прокси, провайдер, слой

**Files:** Modify `Presentation/AimProvider.cs` (`TryAimProxy(out float2,
out float)`: `Physics.Raycast`, маска `AimProxy`, `maxDistance =
Arena.Radius×2` из рантайм-конфига, `QueryTriggerInteraction.Collide`;
`CurrentAimSimPos/Height` учитывают `AimHeld` — при промахе точка пола,
height 0), `Editor/StageOneSceneBootstrap.cs` (`EnsureAimProxyLayer` по
образцу Casings; прокси-чайлды на `MobChaserView`/`MobGunnerView`/кукле:
триггер-капсулы из SO-поясов, голова × `AimProxyHeadRadiusFrac`),
`Presentation/PersistentPropsDirector.cs`-стиль `Physics.IgnoreLayerCollision`
для AimProxy (B3) — в `AimProvider.Awake`; порядок: каст в `LateUpdate`
после вьюх + `Physics.SyncTransforms` (C15).

- [ ] Реализация + Apply ×2 (идемпотентность, прокси в префабах).
- [ ] Компиляция зелёная; `PrefabVisualsMatch`-гейты живы.
- [ ] Commit `feat(app-n6g): Т19 — прокси-слой прицела и 3D-точка`.

### Т20: визуал прицела (луч, конус, маркер)

**Files:** Create `Presentation/AimRayView.cs` (LineRenderer дуло→точка +
точка-маркер; живёт при `AimHeld`; `AimRayAlpha/Width/AimDotScale`;
материал `GetOrCreateUnlitMaterial`); Modify `Presentation/CrosshairView.cs`
(конус: радиус × `moveMult` × `settleFactor`, скрыт при `AimHeld` — D19;
`_marker` — круглый мини-диск), `Editor/StageOneSceneBootstrap.cs` (создание/
ссылки).

- [ ] Реализация + Apply ×2.
- [ ] Компиляция; смоук-скрин не нужен (веха В2).
- [ ] Commit `feat(app-n6g): Т20 — луч прицела и честный круг разброса`.

### Т21: снаряды/дуло/предсказание в вьюхах

**Files:** Modify `Presentation/ViewRegistry.cs` (интерполяция
`PrevHeight→Height`, удалить `ProjectileOffset`; `TracerScale`),
`Presentation/MuzzleFlashView.cs`, `AudioDirector.cs`,
`PersistentPropsDirector.cs` (высота дула из `World.Config.Hero.MuzzleHeight`/
`SlideMuzzleHeight` по `RenderCurr.Player.SlideTimer > 0` — B6/D13),
`SimulationRunner.WouldFireThisFrame` (`CanFireWhileSlide` из
`World.Config.Weapon` — B7), декали `ProjectileBlocked` (пол/стена по
`HitDir`, высота `Amount` — K7).

- [ ] Реализация.
- [ ] Компиляция + полный EditMode (сим не тронут — регресс) → PASS.
- [ ] Commit `feat(app-n6g): Т21 — вьюхи снарядов и дула в 3D`.

### Т22: HUD «Буст» + deny + зонный фидбек + DevOverlay

**Files:** Modify `Presentation/HudController.cs` (`_staminaFill` +
`StaminaBar*`-цвета + пульс `StaminaDenied`), `Editor/StageOneSceneBootstrap.cs`
(третий `GetOrCreateBar` «Буст» + `SetRef` — B8),
`Presentation/GameFeelDirector.cs` (`HeadHitstopScale` по `Zone == Head`),
`AudioDirector.cs` (`ZoneHitPitchOffset`; звук deny; искра/звук рикошета),
`PersistentPropsDirector.cs` (рикошет-искра по нормали, слайд-пыль по
`PlayerSlideStarted`), `DevOverlay.cs` (`SlidesUsed`/`HeadshotKills` — A16).

- [ ] Реализация + Apply ×2.
- [ ] Компиляция; EditMode-регресс → PASS.
- [ ] Commit `feat(app-n6g): Т22 — HUD Буста и фидбек зон` → **ВЕХА В1**
  (плейтест владельца: руки; фикс-волны по фидбеку).

### Т23: слайд-анимация куклы

**Files:** импорт Mixamo `Running Slide` → `_Ring/Animations/` (ASSETS-001
§4.2: Humanoid-ретаргет, root motion OFF; `CREDITS.md` + строка «no
redistribution» — A10); Modify `Presentation/PlayerVisual.cs` (слайд-стейт по
`RenderCurr.Player.SlideTimer > 0`; фолбэк — процедурный присед; поза/дуло —
гизмо + `ContextMenu Capture` по образцу пушки — B15),
`Editor/ThirdPartyAnimatorBootstrap.cs` (стейт + переходы).

- [ ] Импорт + реализация + Apply ×2.
- [ ] Компиляция; вопрос клип-vs-присед — на вехе В3 (владелец).
- [ ] Commit `feat(app-n6g): Т23 — слайд-анимация Сборщика`.

### Т24: обломки (после `app-1zf`)

**Files:** сначала закрыть `app-1zf` (проверка под-мешей George/Leela —
отчёт в bd); Create `Presentation/GibView.cs` (Rigidbody-часть, слой Casings,
`GibPhysicsSeconds`-засыпание по образцу `CasingView`); Modify
`PersistentPropsDirector.cs` (пятый `RingBuffer<GibView>` + `Clear()` — D10;
`HandleMobDied`: хедшот — головной обломок импульсом `GibHeadImpulseSpeed`
по `HitDir`, перебор — разброс `GibExplosionSpeed` клиентским рандомом),
`CorpseView.cs` (ветка «без головы» — B5); меши → `_Ring/Gibs/` (LFS).

- [ ] `app-1zf`: разбор FBX → bd note + `bd close app-1zf`.
- [ ] Реализация по результату (под-меши или фолбэк-примитивы).
- [ ] Apply ×2 + компиляция + EditMode-регресс.
- [ ] Commit `feat(app-n6g): Т24 — обломки мехов по зоне и вектору` →
  **ВЕХА В3-кандидат** (после В2).

### Т25: гейты и вехи В2/В3

- [ ] Полный EditMode (все классы) → 0 failed; счётчик и golden — в bd note.
- [ ] Сборки: `RING_BUILD_ROOT=$RING_ROOT/builds "$UNITY" -batchmode -quit …
  BuildLinuxServer`, затем `BuildWindowsClient` → exit 0.
- [ ] **ВЕХА В2** (прицел: оба режима, зоны, перестрел, «в пол»; балансовый
  PR радиуса снаряда/`Gunner.MaxHp` — рукой владельца) → фикс-волны.
- [ ] **ВЕХА В3** (мясо: обломки, слайд-клип) → фикс-волны.
- [ ] Финал-ревью (opus) → `superpowers:finishing-a-development-branch` →
  PR → merge → `bd close app-nco`, note `app-n6g`.

---

## Self-review плана (выполнен)

- Покрытие спеки: §3.2 → Т1/Т4–Т8/Т13/Т15; §3.3 → Т2/Т9–Т14; §3.4 → Т6/Т7/
  Т9/Т10/Т12/Т22; §3.5 → Т1/Т2/Т17; §3.6 → Т18–Т24; §4 → RED-шаги всех
  тасков + Т16; §5 → вехи в Т22/Т25; §7 DoD → Т25. Пробелов не нашёл.
- Типы сквозные: `HitZone` (Т6) используется Т7/Т11/Т22; `MoveWithCollisions
  out`-сигнатура (Т11) — Т12; `SlideTimer` (Т10) — Т15/Т21/Т23;
  `Targeting.PredictPos` (Т13) — только чейзер. Консистентно.
- Плейсхолдеров нет; каждое «реализация» в Фазе 3 опирается на дословные
  формулы/имена спеки §3.6 (файл под рукой у исполнителя).
