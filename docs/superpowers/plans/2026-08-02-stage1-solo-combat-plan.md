# План имплементации: Этап 1 «Боёвка соло, без сети» (app-88s)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** играбельная соло-боёвка: моментум+дэш, снаряды, 2 моба, волны от seed,
HP/смерть/рестарт, полный game-feel-чеклист ADR-001 §10 — DoD: плейтест владельца
«5 минут стрелять приятно».

**Architecture:** детерминированная симуляция в `Ring.Simulation` (тики 30 Гц,
plain-структуры, единый RNG, FNV-хеш состояния, свип-коллизии кругов); слой
`Ring.Data` (SO баланса → иммутабельный `SimConfig`); слой `Ring.Presentation`
(accumulator-раннер на unscaled-времени, интерполяция снапшотов по ID, событийный
game feel, пулы персистентной косметики).

**Tech Stack:** Unity 6000.3.21f1 (6.3 LTS), URP 17.3, Unity.Mathematics,
Input System 1.20 (project-wide actions), uGUI+TMP, Unity Test Framework (NUnit).

**Спека:** `docs/superpowers/specs/2026-08-02-stage1-solo-combat-spec.md` (v2).

## Global Constraints

- Рабочий каталог всех команд — worktree:
  `WT=/home/brolin/Documents/!_MY_Proj/The Ring/.worktrees/app-88s-stage1-solo-combat`;
  `UNITY=$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity`;
  `SCRATCH=/tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/d9fc2538-34b4-45f2-81de-1db0927e059b/scratchpad`.
- `client/Assets/Scripts/Simulation/**`: без UnityEngine (только Unity.Mathematics);
  `Mathf`/`System.Math`/`[BurstCompile]` запрещены (спека §3.3). `Time.timeScale`
  не использует никто (§3.2).
- TDD для Simulation: RED → verify FAIL → GREEN → verify PASS → commit per task.
- Единый RNG `_rng` мира; отдельные `Unity.Mathematics.Random` запрещены (§3.3).
- Баланс — только в SO (`client/Assets/Data`); `TickDt` — единственный источник dt.
- Пакеты не добавляются (Critical Rule 9). Словарь ADR-003 §9 — включая идентификаторы.
- Код, идентификаторы и комментарии в .cs-файлах — АНГЛИЙСКИЕ (CLAUDE.md §Языки);
  русские пояснения в сниппетах этого плана при переносе в файлы ПЕРЕВОДЯТСЯ
  исполнителем на английский; русский остаётся в коммитах, UI-строках и тексте плана.
- Коммиты: `feat(app-88s): …`/`test(…)`/`chore(…)`, русский, трейлер
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед коммитом секрет-чек
  `git status --short --untracked-files=all | grep -E '\.(env|pem|key)$|secrets/'` — пуст.
- Unity-API сверять с офиц. доками curl'ом / исходниками `client/Library/PackageCache`
  (Context7 недоступен — lesson 9 Э0).
- Этап 2 (FishNet/сеть/Docker) не начинать.

## Runbook (стандартные команды; в тасках — ссылки)

- **R-TEST** (все EditMode-тесты):
  `cd "$WT" && "$UNITY" -runTests -batchmode -projectPath client -testPlatform EditMode -testResults "$SCRATCH/t.xml" -logFile "$SCRATCH/t.log"; echo EXIT=$?`
  exit 0 = все зелёные; exit 2 = есть провалы (смотреть `$SCRATCH/t.xml`).
- **R-FILTER** (подмножество, быстрее): R-TEST + `-testFilter "Ring.Simulation.Tests.<Fixture>"`.
- **R-COMPILE** (смоук компиляции):
  `cd "$WT" && "$UNITY" -batchmode -quit -projectPath client -logFile "$SCRATCH/c.log"; grep -E "error CS|Exception" "$SCRATCH/c.log" | head; echo EXIT=$?`
  Ожидание: EXIT=0, grep пуст.
- **R-BUILD-<X>**: `cd "$WT" && RING_BUILD_ROOT="$SCRATCH/builds" "$UNITY" -batchmode -quit -projectPath client -executeMethod Ring.Editor.BuildCommands.Build<X> -logFile "$SCRATCH/b.log"; echo EXIT=$?` (X ∈ LinuxServer|WindowsClient|LinuxClient).
- **R-COMMIT**: секрет-чек → `git add <файлы> && git commit -m "<msg>" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"`.

---

## Phase 1 — Каркас (спека §3.1–3.3, §3.7, §3.9; таск 1 из §4)

### Task 1: asmdef-слои Ring.Data / Ring.Presentation + Amendment A7 + CODEOWNERS

**Files:**
- Create: `client/Assets/Scripts/Data/Data.asmdef` (имена файлов — как в Э0:
  без префикса `Ring.`)
- Create: `client/Assets/Scripts/Presentation/Presentation.asmdef`
- Modify: `client/Assets/Tests/EditMode/Simulation.Tests.asmdef` (добавить Ring.Data)
- Modify: `docs/adr/ADR-002-Разработка.md` (§10: Amendment A7 — решение Р1)
- Modify: `.github/CODEOWNERS` (строка `/client/Assets/Scripts/Data/` + фикс
  мёртвого пути `/client/Tests/` → `/client/Assets/Tests/`)

**Interfaces:**
- Produces: сборки `Ring.Data` и `Ring.Presentation` — все последующие таски кладут
  код в них. `Ring.Presentation` сразу ссылается на UI/TMP/URP-сборки: HUD (Task 14),
  винетка (Task 25) и `DecalProjector` (Task 27) без них не соберутся, а повторных
  правок asmdef план не содержит.
- Ссылка `Editor.asmdef → Ring.Data` НЕ добавляется (уточнение спеки §3.1:
  editor-кода на Ring.Data в этапе нет — hot-tweak живёт в `OnValidate` самих SO;
  мёртвые зависимости запрещены Пятью правилами №3).

- [ ] **Step 1:** `client/Assets/Scripts/Data/Data.asmdef` (полный набор полей —
  формат как у существующего `Simulation.asmdef`):

```json
{
    "name": "Ring.Data",
    "rootNamespace": "Ring.Data",
    "references": ["Ring.Simulation", "Unity.Mathematics"],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 2:** `client/Assets/Scripts/Presentation/Presentation.asmdef`:

```json
{
    "name": "Ring.Presentation",
    "rootNamespace": "Ring.Presentation",
    "references": ["Ring.Simulation", "Ring.Data", "Unity.InputSystem",
                   "Unity.Mathematics", "UnityEngine.UI", "Unity.TextMeshPro",
                   "Unity.RenderPipelines.Universal.Runtime"],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 3:** В `Simulation.Tests.asmdef` добавить `"Ring.Data"` в `references`
  (сейчас `["Ring.Simulation","UnityEngine.TestRunner","UnityEditor.TestRunner"]`).
- [ ] **Step 4:** ADR-002 §10 — дописать Amendment (Р1 санкционирован апрувом спеки):

```markdown
- **A7 (2026-08-02).** Слой `client/Assets/Scripts/Data/` (asmdef `Ring.Data`:
  SO-классы баланса + конвертация в plain SimConfig) — дополняет структуру §3;
  server-ownership.
```

  `.github/CODEOWNERS`: добавить `/client/Assets/Scripts/Data/ @AlexanderBrolin`;
  заменить битый `/client/Tests/` на `/client/Assets/Tests/`.
- [ ] **Step 5:** R-COMPILE — EXIT=0, ошибок нет; Unity сгенерит `.meta` — закоммитить вместе.
- [ ] **Step 6:** R-COMMIT `feat(app-88s): asmdef-слои Ring.Data/Ring.Presentation, Amendment A7, CODEOWNERS`.

### Task 2: StateHash64 — float-перегрузки (TDD)

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/StateHash64.cs`
- Create: `client/Assets/Tests/EditMode/StateHashTests.cs`

**Interfaces:**
- Produces: `StateHash64.Add(ulong hash, float v)`, `Add(ulong hash, float2 v)`,
  `Add(ulong hash, int v)`, `Add(ulong hash, bool v)` — используются миром в Task 5+.
- Гарантия: golden-vector Э0 (`0xA8C7F832281A39C5`) не меняется.

- [ ] **Step 1 (RED):** `StateHashTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class StateHashTests
    {
        [Test]
        public void FloatAdd_MatchesRawBits()
        {
            ulong viaFloat = StateHash64.Add(StateHash64.Begin(), 1.5f);
            ulong viaBits = StateHash64.Add(StateHash64.Begin(), (ulong)math.asuint(1.5f));
            Assert.AreEqual(viaBits, viaFloat);
        }

        [Test]
        public void NegativeZero_NormalizedToPositiveZero()
        {
            Assert.AreEqual(StateHash64.Add(StateHash64.Begin(), 0f),
                            StateHash64.Add(StateHash64.Begin(), -0f));
        }

        [Test]
        public void Float2_HashesBothComponentsInOrder()
        {
            ulong h = StateHash64.Add(StateHash64.Begin(), new float2(1f, 2f));
            ulong manual = StateHash64.Add(StateHash64.Add(StateHash64.Begin(), 1f), 2f);
            Assert.AreEqual(manual, h);
            Assert.AreNotEqual(h, StateHash64.Add(StateHash64.Begin(), new float2(2f, 1f)));
        }

        [Test]
        public void BoolAndInt_Distinct()
        {
            Assert.AreNotEqual(StateHash64.Add(StateHash64.Begin(), true),
                               StateHash64.Add(StateHash64.Begin(), false));
            Assert.AreNotEqual(StateHash64.Add(StateHash64.Begin(), 1),
                               StateHash64.Add(StateHash64.Begin(), 2));
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `StateHashTests` — FAIL (методов нет), ошибки компиляции тестов = корректный RED.
- [ ] **Step 3 (GREEN):** дописать в `StateHash64`:

```csharp
using Unity.Mathematics;
// в класс StateHash64:
        public static ulong Add(ulong hash, float value)
        {
            if (value == 0f) value = 0f; // -0.0 -> +0.0 (канонизация битов)
            return Add(hash, (ulong)math.asuint(value));
        }

        public static ulong Add(ulong hash, float2 value)
        {
            return Add(Add(hash, value.x), value.y);
        }

        public static ulong Add(ulong hash, int value) => Add(hash, (ulong)(uint)value);

        public static ulong Add(ulong hash, bool value) => Add(hash, value ? 1UL : 0UL);
```

- [ ] **Step 4:** R-FILTER `StateHashTests` — PASS; R-FILTER `DeterminismTests` — PASS
  (golden-vector жив).
- [ ] **Step 5:** R-COMMIT `feat(app-88s): float/int/bool-перегрузки StateHash64 с канонизацией -0.0`.

### Task 3: SimConfig, SimInput, состояния, миграция API SimulationWorld (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Core/SimConfig.cs`
- Create: `client/Assets/Scripts/Simulation/Core/SimInput.cs`
- Create: `client/Assets/Scripts/Simulation/Core/SimStates.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
- Modify: `client/Assets/Tests/EditMode/DeterminismTests.cs`
- Create: `client/Assets/Tests/EditMode/TestConfigs.cs`

**Interfaces:**
- Produces (все типы — `Ring.Simulation.Core`):

```csharp
public struct HeroSimConfig { public float MaxSpeed, Accel, Friction, Radius, MaxHp,
    DashSpeed, DashDuration, DashCooldown, DashIframes, DashBufferWindow; }
public struct WeaponSimConfig { public float FireInterval, ProjectileSpeed, ProjectileRadius,
    ProjectileLifetime, Damage, SpreadRad, RecoilPerShotRad, RecoilRecoveryRadPerSec,
    RecoilMaxRad, MuzzleOffset; public bool CanFireWhileDash; }
public struct MobSimConfig { public float MaxSpeed, Accel, Radius, MaxHp, ContactDamage,
    AttackRange, TelegraphSeconds, AttackCooldown, PreferredRange, RangeTolerance,
    StrafeSpeed, FireInterval, ProjectileSpeed, ProjectileRadius, ProjectileLifetime,
    ProjectileDamage, LeadFactor, SeparationRadius, SeparationStrength, AvoidLookahead; }
public struct WaveSimConfig { public float FirstWaveDelay, WavePause, SpawnRingInset,
    MinSpawnDistanceToPlayer; public int BaseCount, CountGrowth, MaxMobsPerWave,
    MaxSpawnAttempts, FallbackSlots; public float GunnerShareBase, GunnerShareGrowth; }
public struct ArenaSimConfig { public float Radius; public int ObstacleCount;
    public float2[] ObstaclePos; public float[] ObstacleRadius;
    public int MaxMobs, MaxProjectiles, MaxEventsPerFrame; }
public struct SimConfig { public HeroSimConfig Hero; public WeaponSimConfig Weapon;
    public MobSimConfig Chaser, Gunner; public WaveSimConfig Wave; public ArenaSimConfig Arena; }
public struct SimInput { public float2 MoveDir, AimPoint; public bool FireHeld, DashRequested; }
public struct PlayerState { public float2 Pos, Vel, AimPoint, DashDir; public float RecoilOffset,
    Hp, DashTimer, DashCooldown, IframeTimer, DashBufferTimer, FireCooldown; public bool Alive; }
public enum MobType : byte { Chaser = 0, Gunner = 1 }
public enum MobAiState : byte { Idle, Chase, Telegraph, Recover, Reposition, Fire }
public struct MobState { public int Id; public MobType Type; public float2 Pos, Vel;
    public float Hp, StateTimer, FireCooldown; public MobAiState Ai; public int StrafeSign; }
public enum ProjectileOwner : byte { Player = 0, Mob = 1 }
public struct ProjectileState { public int Id; public ProjectileOwner Owner;
    public float2 Pos, PrevPos, Vel; public float Damage, Radius, Ttl; }
public enum WavePhase : byte { Waiting = 0, Active = 1 }
public struct WaveState { public WavePhase Phase; public int WaveIndex, PendingChasers,
    PendingGunners, AliveCount; public float PhaseTimer; }
public struct MatchStats { public int Kills, WavesCleared, ShotsFired, ShotsHit,
    DashesUsed, MobSpawnsSkipped, ProjectileSpawnsSkipped, DeathTick;
    public float DamageTaken; }
// капы наблюдаемы раздельно (спека §3.15): что упёрлось — видно в DevOverlay
```

- `SimulationWorld`: `SimulationWorld(long seed, in SimConfig config)`,
  `void Tick(in SimInput input)`, `ulong StateHash()`, `int CurrentTick`,
  `MatchStats Stats`, `PlayerState Player` (копия `_players[0]`).
- `TestConfigs.Default()` — plain `SimConfig` c числами по споке (тестовый эталон,
  без SO). Санитизация ввода — внутри `Tick` (спека §3.8).

- [ ] **Step 1 (RED):** `TestConfigs.cs` (хелпер, используется всеми симуляционными тестами):

```csharp
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public static class TestConfigs
    {
        public static SimConfig Default()
        {
            return new SimConfig
            {
                Hero = new HeroSimConfig { MaxSpeed = 7f, Accel = 40f, Friction = 30f,
                    Radius = 0.45f, MaxHp = 100f, DashSpeed = 22f, DashDuration = 0.15f,
                    DashCooldown = 1.2f, DashIframes = 0.2f, DashBufferWindow = 0.15f },
                Weapon = new WeaponSimConfig { FireInterval = 0.12f, ProjectileSpeed = 35f,
                    ProjectileRadius = 0.12f, ProjectileLifetime = 1.5f, Damage = 12f,
                    SpreadRad = 0.026f, RecoilPerShotRad = 0.006f,
                    // recovery MUST be below RecoilPerShotRad / FireInterval (0.05),
                    // otherwise recoil never accumulates and the cone is dead
                    RecoilRecoveryRadPerSec = 0.03f, RecoilMaxRad = 0.07f,
                    MuzzleOffset = 0.6f, CanFireWhileDash = false },
                Chaser = new MobSimConfig { MaxSpeed = 5.2f, Accel = 30f, Radius = 0.5f,
                    MaxHp = 30f, ContactDamage = 15f, AttackRange = 1.1f,
                    TelegraphSeconds = 0.35f, AttackCooldown = 0.9f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f },
                Gunner = new MobSimConfig { MaxSpeed = 4f, Accel = 25f, Radius = 0.5f,
                    MaxHp = 20f, PreferredRange = 9f, RangeTolerance = 1.5f, StrafeSpeed = 3f,
                    FireInterval = 1.6f, ProjectileSpeed = 14f, ProjectileRadius = 0.15f,
                    ProjectileLifetime = 3f, ProjectileDamage = 8f, LeadFactor = 0.8f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f },
                Wave = new WaveSimConfig { FirstWaveDelay = 2.5f, WavePause = 4f,
                    SpawnRingInset = 2f, MinSpawnDistanceToPlayer = 8f, BaseCount = 4,
                    CountGrowth = 2, MaxMobsPerWave = 24, MaxSpawnAttempts = 16,
                    FallbackSlots = 24, GunnerShareBase = 0.2f, GunnerShareGrowth = 0.05f },
                Arena = DefaultArena()
            };
        }

        public static ArenaSimConfig DefaultArena()
        {
            return new ArenaSimConfig
            {
                Radius = 35f, ObstacleCount = 5,
                ObstaclePos = new[] { new float2(10f, 4f), new float2(-8f, 9f),
                    new float2(2f, -12f), new float2(-13f, -6f), new float2(14f, -9f) },
                ObstacleRadius = new[] { 2.2f, 1.8f, 2.5f, 2.0f, 1.6f },
                MaxMobs = 64, MaxProjectiles = 256, MaxEventsPerFrame = 256
            };
        }

        /// Default config with waves pushed out of reach: movement/combat
        /// fixtures must never meet wave mobs (long runs would kill the player).
        /// Wave scenarios use Default() explicitly (WaveTests only).
        public static SimConfig Quiet()
        {
            var c = Default();
            c.Wave.FirstWaveDelay = 1e6f;
            return c;
        }

        /// Quiet arena without obstacles — open-field movement/combat tests.
        public static SimConfig Open()
        {
            var c = Quiet();
            c.Arena.ObstacleCount = 0;
            c.Arena.ObstaclePos = System.Array.Empty<float2>();
            c.Arena.ObstacleRadius = System.Array.Empty<float>();
            return c;
        }
    }
}
```

  Обновить `DeterminismTests.cs` — хелпер и pinned-тесты переходят на новое API
  (поведенческие ожидания НЕ меняются):

```csharp
        static ulong HashAfterTicks(long seed, int ticks)
        {
            var world = new SimulationWorld(seed, TestConfigs.Default());
            var idle = default(SimInput);
            for (int i = 0; i < ticks; i++)
                world.Tick(idle);
            return world.StateHash();
        }
```

  Добавить в `DeterminismTests` новый тест враждебного ввода (спека §3.13 п.6):

```csharp
        [Test]
        public void HostileInput_StateStaysFinite_AndDeterministic()
        {
            static ulong Run()
            {
                var w = new SimulationWorld(7, TestConfigs.Default());
                var nan = new SimInput
                {
                    MoveDir = new float2(float.NaN, float.PositiveInfinity),
                    AimPoint = new float2(1e9f, float.NegativeInfinity),
                    FireHeld = true, DashRequested = true
                };
                var tooLong = new SimInput { MoveDir = new float2(100f, -50f) };
                for (int i = 0; i < 50; i++) w.Tick(nan);
                for (int i = 0; i < 50; i++) w.Tick(tooLong); // finite over-length dir
                for (int i = 0; i < 50; i++) w.Tick(default); // zero moveDir
                var p = w.Player;
                Assert.IsTrue(math.all(math.isfinite(p.Pos)) && math.all(math.isfinite(p.Vel)));
                return w.StateHash();
            }
            Assert.AreEqual(Run(), Run()); // two independent worlds, same hash
        }
```

- [ ] **Step 2:** R-FILTER `DeterminismTests` — FAIL/не компилируется (нового API нет) = RED.
- [ ] **Step 3 (GREEN):** создать `SimConfig.cs`, `SimInput.cs`, `SimStates.cs` со
  структурами из Interfaces (дословно). Переписать `SimulationWorld.cs`:

```csharp
using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deterministic world: fixed-dt ticks, single RNG seeded from match-config.
    /// No UnityEngine (asmdef: noEngineReferences) — Critical Rule 1.
    public sealed class SimulationWorld
    {
        /// ADR-002 T5: simulation runs at 30 Hz. Единственный источник dt.
        public const float TickDt = 1f / 30f;

        int _tick;
        Random _rng;
        SimConfig _config;
        readonly PlayerState[] _players = new PlayerState[1];
        MatchStats _stats;

        public int CurrentTick => _tick;
        public MatchStats Stats => _stats;
        public PlayerState Player => _players[0];
        public SimConfig Config => _config;

        public SimulationWorld(long seed, in SimConfig config)
        {
            uint folded = (uint)(seed ^ (seed >> 32));
            // Unity.Mathematics.Random rejects seed 0.
            _rng = new Random(folded == 0 ? 0x9E3779B9u : folded);
            _config = config;
            _players[0] = new PlayerState { Hp = config.Hero.MaxHp, Alive = true };
        }

        public void Tick(in SimInput rawInput)
        {
            SimInput input = Sanitize(rawInput);
            _tick++;
            _rng.NextUInt(); // every tick consumes RNG so an idle world still hashes alive
            _players[0].AimPoint = input.AimPoint;
        }

        SimInput Sanitize(in SimInput raw)
        {
            SimInput s = raw;
            if (!math.all(math.isfinite(s.MoveDir))) s.MoveDir = float2.zero;
            float lsq = math.lengthsq(s.MoveDir);
            if (lsq > 1f) s.MoveDir /= math.sqrt(lsq);
            if (!math.all(math.isfinite(s.AimPoint))) s.AimPoint = _players[0].AimPoint;
            float2 rel = s.AimPoint - _players[0].Pos;
            float maxR = _config.Arena.Radius * 2f;
            if (math.lengthsq(rel) > maxR * maxR)
                s.AimPoint = _players[0].Pos + math.normalizesafe(rel) * maxR;
            return s;
        }

        /// Canonical order (спека §3.3): tick → rng → nextEntityId → players →
        /// mobs → projectiles → wave → stats. Сущности добавятся в Phase 2+.
        public ulong StateHash()
        {
            ulong h = StateHash64.Begin();
            h = StateHash64.Add(h, (ulong)_tick);
            h = StateHash64.Add(h, _rng.state);
            h = HashPlayer(h, in _players[0]);
            h = HashStats(h, in _stats);
            return h;
        }

        static ulong HashPlayer(ulong h, in PlayerState p)
        {
            h = StateHash64.Add(h, p.Pos); h = StateHash64.Add(h, p.Vel);
            h = StateHash64.Add(h, p.AimPoint); h = StateHash64.Add(h, p.DashDir);
            h = StateHash64.Add(h, p.RecoilOffset); h = StateHash64.Add(h, p.Hp);
            h = StateHash64.Add(h, p.DashTimer); h = StateHash64.Add(h, p.DashCooldown);
            h = StateHash64.Add(h, p.IframeTimer); h = StateHash64.Add(h, p.DashBufferTimer);
            h = StateHash64.Add(h, p.FireCooldown); h = StateHash64.Add(h, p.Alive);
            return h;
        }

        static ulong HashStats(ulong h, in MatchStats s)
        {
            h = StateHash64.Add(h, s.Kills); h = StateHash64.Add(h, s.WavesCleared);
            h = StateHash64.Add(h, s.ShotsFired); h = StateHash64.Add(h, s.ShotsHit);
            h = StateHash64.Add(h, s.DashesUsed);
            h = StateHash64.Add(h, s.MobSpawnsSkipped);
            h = StateHash64.Add(h, s.ProjectileSpawnsSkipped);
            h = StateHash64.Add(h, s.DeathTick); h = StateHash64.Add(h, s.DamageTaken);
            return h;
        }
    }
}
```

  Примечание: `_lastNoise` Э0 удалён — «живость» хеша обеспечивает `_rng.NextUInt()`
  в тике + rng.state в хеше; pinned-тесты Э0 продолжают проходить.
- [ ] **Step 4:** R-TEST — все зелёные (7 Э0-тестов на новом API + StateHashTests + hostile input).
- [ ] **Step 5:** R-COMMIT `feat(app-88s): SimConfig/SimInput/состояния, миграция API мира, санитизация ввода`.

### Task 4: FixedStepAccumulator + раздача кадрового ввода по тикам (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Core/FixedStepAccumulator.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimInput.cs` (класс `SimInputFrame`)
- Create: `client/Assets/Tests/EditMode/AccumulatorTests.cs`

**Interfaces:**
- Produces: `FixedStepAccumulator` (`Ring.Simulation.Core` — чистый C#, переиспользуется
  headless-сервером Э2): `int Advance(float dt)` (число тиков к исполнению),
  `float Alpha` (0..1 для интерполяции), `float DroppedTime`, `void Reset()`.
  Кап кадра — `MaxFrameTime = 0.25f`.
- `SimInputFrame.ForTick(in SimInput frame, int tickIndex) → SimInput` — раздача
  кадрового сэмпла по под-тикам: удерживаемые значения копируются во все тики,
  edge-защёлка `DashRequested` — только в тик 0 (спека §3.2; обязательный тест
  «2 тика в кадре → один дэш» живёт здесь, в чистом C#, а не в MonoBehaviour).
  `SimulationRunner` (Task 7) обязан использовать этот метод.

- [ ] **Step 1 (RED):** `AccumulatorTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class AccumulatorTests
    {
        [Test]
        public void AccumulatesFractionsAcrossFrames()
        {
            var acc = new FixedStepAccumulator();
            int total = 0;
            for (int i = 0; i < 30; i++) total += acc.Advance(1f / 60f); // 0.5 c
            Assert.AreEqual(15, total); // 0.5 / (1/30)
        }

        [Test]
        public void BigFrame_ManyTicks_NoLoss()
        {
            var acc = new FixedStepAccumulator();
            // 0.21 c / (1/30) = 6.3 — с запасом от float-границы (0.2f — ровно на ней)
            Assert.AreEqual(6, acc.Advance(0.21f));
            Assert.That(acc.Alpha, Is.InRange(0f, 1f));
        }

        [Test]
        public void FrameInput_EdgeLatchConsumedByFirstTickOnly()
        {
            var frame = new SimInput { FireHeld = true, DashRequested = true };
            Assert.IsTrue(SimInputFrame.ForTick(frame, 0).DashRequested);
            Assert.IsFalse(SimInputFrame.ForTick(frame, 1).DashRequested); // один дэш на кадр
            Assert.IsTrue(SimInputFrame.ForTick(frame, 1).FireHeld); // held — во все тики
        }

        [Test]
        public void FrameSpike_CappedAndReported()
        {
            var acc = new FixedStepAccumulator();
            int n = acc.Advance(2f);
            Assert.AreEqual((int)(0.25f / SimulationWorld.TickDt), n); // 7
            Assert.AreEqual(1.75f, acc.DroppedTime, 1e-4f);
        }

        [Test]
        public void SameTotalTime_SameTickCount_RegardlessOfFraming()
        {
            var a = new FixedStepAccumulator(); var b = new FixedStepAccumulator();
            int na = 0, nb = 0;
            for (int i = 0; i < 100; i++) na += a.Advance(0.0177f);
            for (int i = 0; i < 59; i++) nb += b.Advance(0.03f);
            Assert.AreEqual(53, na); // 1.77 c
            Assert.AreEqual(53, nb); // 1.77 c
        }

        [Test]
        public void Reset_ClearsAccumulatorAndAlpha()
        {
            var acc = new FixedStepAccumulator();
            acc.Advance(0.02f);
            acc.Reset();
            Assert.AreEqual(0f, acc.Alpha);
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `AccumulatorTests` — RED.
- [ ] **Step 3 (GREEN):** `FixedStepAccumulator.cs`:

```csharp
namespace Ring.Simulation.Core
{
    /// Gaffer-style fixed timestep accumulator. Пойдёт и в headless-сервер Э2.
    public sealed class FixedStepAccumulator
    {
        public const float MaxFrameTime = 0.25f;

        float _acc;

        public float DroppedTime { get; private set; }
        public float Alpha => _acc / SimulationWorld.TickDt;

        public int Advance(float dt)
        {
            if (dt > MaxFrameTime) { DroppedTime += dt - MaxFrameTime; dt = MaxFrameTime; }
            if (dt < 0f) dt = 0f;
            _acc += dt;
            int ticks = (int)(_acc / SimulationWorld.TickDt);
            _acc -= ticks * SimulationWorld.TickDt;
            if (_acc < 0f) _acc = 0f; // float rounding on exact-boundary frames
            return ticks;
        }

        public void Reset() { _acc = 0f; DroppedTime = 0f; }
    }
}
```

  В `SimInput.cs` дописать:

```csharp
    /// Distributes one frame sample over N sub-ticks: held values copy to every
    /// tick, the dash edge-latch fires on tick 0 only (spec §3.2).
    public static class SimInputFrame
    {
        public static SimInput ForTick(in SimInput frame, int tickIndex)
        {
            SimInput si = frame;
            si.DashRequested = frame.DashRequested && tickIndex == 0;
            return si;
        }
    }
```

- [ ] **Step 4:** R-FILTER `AccumulatorTests` — PASS.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): FixedStepAccumulator — кап кадра, alpha, независимость от нарезки`.

### Task 5: события, снапшот, Save/Restore, гигиена хеша (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Core/SimEvents.cs`
- Create: `client/Assets/Scripts/Simulation/Core/RenderSnapshot.cs`
- Create: `client/Assets/Scripts/Simulation/Core/WorldSave.cs`
- Create: `client/Assets/Scripts/Simulation/AssemblyInfo.cs`
  (`[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ring.Simulation.Tests")]`
  — тесты видят internal-члены штатно; публичных `*ForTest`-обёрток в боевом API
  мира НЕ заводим: в билд Э2 они бы уехали как читы)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
- Create: `client/Assets/Tests/EditMode/EventTests.cs`
- Create: `client/Assets/Tests/EditMode/WorldLifecycleTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum SimEventKind : byte { ProjectileFired, ProjectileHit, ProjectileBlocked,
    ProjectileExpired, MobSpawned, MobDied, PlayerDamaged, PlayerDashed, PlayerDied,
    WaveStarted, WaveCleared }
public struct SimEvent { public SimEventKind Kind; public int Tick; public float2 Pos;
    public int EntityId; public MobType MobType; public float Amount; }
// SimulationWorld:
public int EventCount { get; }            // события с последнего ClearEvents
public SimEvent GetEvent(int i);
public void ClearEvents();
public int DroppedEvents { get; }         // переполнения буфера (кумулятивно)
internal void Emit(SimEventKind kind, float2 pos, int entityId, MobType mobType, float amount);
public void CaptureSnapshot(RenderSnapshot target);  // без аллокаций
public WorldSave SaveState();             // глубокая копия (аллоцирует — вне тика)
public void RestoreState(WorldSave save);
internal void SetPlayerForTest(in PlayerState p);   // швы теста «каждое поле в хеше»
internal void SetStatsForTest(in MatchStats s);
// RenderSnapshot: public int Tick; public PlayerState Player; public int MobCount;
//   public MobState[] Mobs; public int ProjectileCount; public ProjectileState[] Projectiles;
//   public WaveState Wave; public MatchStats Stats;
//   public RenderSnapshot(in ArenaSimConfig arena) — предаллокация под капы.
```

- Внутренние массивы мобов/снарядов появляются здесь (пустые до Phase 5/6):
  `MobState[] _mobs; int _mobCount; ProjectileState[] _projectiles; int _projectileCount;
  WaveState _wave; int _nextEntityId = 1;` + буфер `SimEvent[] _events; int _eventCount;`.
  Хеш расширяется: `…rng → nextEntityId → player → mobCount+мобы → projCount+снаряды →
  wave → stats` (полный канонический порядок спеки §3.3; поля мобов/снарядов — в
  порядке объявления структур).

- [ ] **Step 1 (RED):** `EventTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class EventTests
    {
        [Test]
        public void Emit_RecordsKindTickAndPayload()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.Tick(default); // tick = 1
            w.Emit(SimEventKind.PlayerDashed, new float2(1f, 2f), 0, default, 0f);
            Assert.AreEqual(1, w.EventCount);
            SimEvent e = w.GetEvent(0);
            Assert.AreEqual(SimEventKind.PlayerDashed, e.Kind);
            Assert.AreEqual(1, e.Tick);
            Assert.AreEqual(new float2(1f, 2f), e.Pos);
        }

        [Test]
        public void ClearEvents_ResetsCount()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.Emit(SimEventKind.WaveStarted, float2.zero, 1, default, 0f);
            w.ClearEvents();
            Assert.AreEqual(0, w.EventCount);
        }

        [Test]
        public void Overflow_DropsDeterministicallyWithoutGrowth()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(1, cfg);
            int cap = cfg.Arena.MaxEventsPerFrame;
            for (int i = 0; i < cap + 10; i++)
                w.Emit(SimEventKind.ProjectileFired, float2.zero, i, default, 0f);
            Assert.AreEqual(cap, w.EventCount);
            Assert.AreEqual(10, w.DroppedEvents);
        }
    }
}
```

  `WorldLifecycleTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class WorldLifecycleTests
    {
        [Test]
        public void SaveRestore_ReplaysToSameHash()
        {
            var w = new SimulationWorld(42, TestConfigs.Default());
            var input = new SimInput { FireHeld = true };
            for (int i = 0; i < 100; i++) w.Tick(input);
            WorldSave save = w.SaveState();
            for (int i = 0; i < 500; i++) w.Tick(input);
            ulong straight = w.StateHash();
            w.RestoreState(save);
            for (int i = 0; i < 500; i++) w.Tick(input);
            Assert.AreEqual(straight, w.StateHash());
        }

        [Test]
        public void TwoWorldsSameSeed_NoStaticState()
        {
            ulong a = Run(42); ulong b = Run(42);
            Assert.AreEqual(a, b);
            static ulong Run(long seed)
            {
                var w = new SimulationWorld(seed, TestConfigs.Default());
                for (int i = 0; i < 300; i++) w.Tick(default);
                return w.StateHash();
            }
        }

        [Test]
        public void EveryPlayerAndStatsFieldAffectsHash() // спека §3.13 п.12 / §3.3
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            w.Tick(default);
            WorldSave save = w.SaveState();
            ulong baseline = w.StateHash();
            foreach (var field in typeof(PlayerState).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Player;
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetPlayerForTest((PlayerState)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"PlayerState.{field.Name} не в хеше");
            }
            foreach (var field in typeof(MatchStats).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Stats;
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetStatsForTest((MatchStats)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"MatchStats.{field.Name} не в хеше");
            }
            // аналогичные проходы для MobState/ProjectileState/WaveState добавляются
            // в Task 16/22 швами SetMobForTest/SetProjectileForTest/SetWaveForTest
        }

        static object Bump(object v) => v switch
        {
            float f => f + 1f,
            int i => i + 1,
            bool b => !b,
            Unity.Mathematics.float2 f2 => f2 + new Unity.Mathematics.float2(1f, 0f),
            _ => throw new System.NotSupportedException(v.GetType().Name)
        };

        [Test]
        public void Snapshot_CopiesPlayerAndCounts()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(5, cfg);
            w.Tick(default);
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(w.CurrentTick, snap.Tick);
            Assert.AreEqual(w.Player.Pos, snap.Player.Pos);
            Assert.AreEqual(0, snap.MobCount);
        }
    }
}
```

  (Тесты зовут `internal`-метод `w.Emit(kind, pos, id, default, amount)` напрямую —
  доступ даёт `InternalsVisibleTo` из AssemblyInfo.cs этого таска.)
- [ ] **Step 2:** R-FILTER `EventTests|WorldLifecycleTests` — RED.
- [ ] **Step 3 (GREEN):** `SimEvents.cs` (enum+struct из Interfaces), `RenderSnapshot.cs`:

```csharp
namespace Ring.Simulation.Core
{
    /// Preallocated render view of one tick. Matching by entity Id (спека §3.7).
    public sealed class RenderSnapshot
    {
        public int Tick;
        public PlayerState Player;
        public int MobCount;
        public MobState[] Mobs;
        public int ProjectileCount;
        public ProjectileState[] Projectiles;
        public WaveState Wave;
        public MatchStats Stats;

        public RenderSnapshot(in ArenaSimConfig arena)
        {
            Mobs = new MobState[arena.MaxMobs];
            Projectiles = new ProjectileState[arena.MaxProjectiles];
        }
    }
}
```

  `WorldSave.cs` — те же поля + rng/tick/nextEntityId/players (глубокие копии массивов);
  в мире: массивы, `Emit` (кап + `DroppedEvents++`), `CaptureSnapshot` (Array.Copy по
  счётчикам), `SaveState`/`RestoreState` (копии в обе стороны), расширенный `StateHash()`
  (порядок из Interfaces; мобы: Id, Type, Pos, Vel, Hp, StateTimer, FireCooldown, Ai,
  StrafeSign; снаряды: Id, Owner, Pos, PrevPos, Vel, Damage, Radius, Ttl; wave: Phase,
  WaveIndex, PendingChasers, PendingGunners, AliveCount, PhaseTimer).
- [ ] **Step 4:** R-TEST — PASS (все).
- [ ] **Step 5:** R-COMMIT `feat(app-88s): событийный буфер с капом, RenderSnapshot, Save/RestoreState, полный хеш`.

### Task 6: Ring.Data — SO-классы и SimConfigBuilder с валидацией (TDD)

**Files:**
- Create: `client/Assets/Scripts/Data/HeroConfig.cs`, `WeaponConfig.cs`, `MobConfig.cs`,
  `WaveConfig.cs`, `ArenaConfig.cs`, `GameFeelConfig.cs`, `CameraConfig.cs`
- Create: `client/Assets/Scripts/Data/SimConfigBuilder.cs`
- Create: `client/Assets/Tests/EditMode/ConfigTests.cs`

**Interfaces:**
- Produces (`Ring.Data`): SO-классы `HeroConfig : ScriptableObject` и т.д. — поля
  зеркалят соответствующие `*SimConfig` (`[Range]` на числах, `[CreateAssetMenu]`);
  `ArenaConfig` дополнительно: `Obstacle[] Obstacles` (`struct Obstacle { Vector2 Pos;
  float Radius; }`), `MaxMobs/MaxProjectiles/MaxEventsPerFrame`;
  `GameFeelConfig`/`CameraConfig` — только Presentation-числа (§3.11/спека), в
  `SimConfig` не входят.
- `SimConfigBuilder.Build(HeroConfig, WeaponConfig, MobConfig chaser, MobConfig gunner,
  WaveConfig, ArenaConfig) → SimConfig` — конвертация + `Validate` (бросает
  `System.ArgumentException` с перечнем нарушений): NaN/отрицательные, нулевые радиусы,
  препятствия внутри арены, точка спавна игрока (центр) свободна, `MaxMobs > 0` и т.д.
- `SimConfigBuilder.Migrate(ref SimulationWorld-стейт…)` НЕ здесь — миграция hot-tweak
  живёт в `SimulationWorld.ApplyConfig` (Task 7).

- [ ] **Step 1 (RED):** `ConfigTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    public class ConfigTests
    {
        static (HeroConfig, WeaponConfig, MobConfig, MobConfig, WaveConfig, ArenaConfig) MakeDefaults()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            var weapon = ScriptableObject.CreateInstance<WeaponConfig>();
            var chaser = ScriptableObject.CreateInstance<MobConfig>();
            var gunner = ScriptableObject.CreateInstance<MobConfig>();
            var wave = ScriptableObject.CreateInstance<WaveConfig>();
            var arena = ScriptableObject.CreateInstance<ArenaConfig>();
            return (hero, weapon, chaser, gunner, wave, arena);
        }

        [Test]
        public void Build_DefaultAssets_ProducesValidConfig()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a);
            Assert.Greater(cfg.Hero.MaxSpeed, 0f);
            Assert.AreEqual(a.Obstacles.Length, cfg.Arena.ObstacleCount);
        }

        [Test]
        public void Build_ObstacleOutsideArena_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Obstacles = new[] { new ArenaConfig.Obstacle
                { Pos = new Vector2(100f, 0f), Radius = 2f } };
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }

        [Test]
        public void Build_NegativeSpeed_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            h.MaxSpeed = -1f;
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }

        [Test]
        public void Build_ObstacleOverSpawnPoint_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Obstacles = new[] { new ArenaConfig.Obstacle
                { Pos = Vector2.zero, Radius = 2f } };
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `ConfigTests` — RED.
- [ ] **Step 3 (GREEN):** SO-классы: сериализуемые поля с дефолтами = числам
  `TestConfigs.Default()` (единый источник стартового баланса — SO; `TestConfigs`
  остаётся независимым эталоном тестов). Пример (`HeroConfig.cs`, остальные аналогично):

```csharp
using UnityEngine;

namespace Ring.Data
{
    [CreateAssetMenu(menuName = "Ring/Hero Config", fileName = "HeroConfig")]
    public sealed class HeroConfig : ScriptableObject
    {
        [Range(0.1f, 30f)] public float MaxSpeed = 7f;
        [Range(1f, 200f)] public float Accel = 40f;
        [Range(1f, 200f)] public float Friction = 30f;
        [Range(0.1f, 2f)] public float Radius = 0.45f;
        [Range(1f, 1000f)] public float MaxHp = 100f;
        [Range(1f, 60f)] public float DashSpeed = 22f;
        [Range(0.05f, 1f)] public float DashDuration = 0.15f;
        [Range(0.1f, 10f)] public float DashCooldown = 1.2f;
        [Range(0f, 1f)] public float DashIframes = 0.2f;
        [Range(0f, 0.5f)] public float DashBufferWindow = 0.15f;
    }
}
```

  `SimConfigBuilder.Build` — маппинг 1:1 в структуры + `Validate(in SimConfig)`
  (собирает нарушения в список, бросает одним `ArgumentException`). Проверки:
  все float конечны и в допустимых знаках; радиусы > 0; для каждого препятствия
  `length(pos) + r ≤ Arena.Radius`; препятствие не накрывает центр (спавн игрока):
  `length(pos) > r + Hero.Radius + 1`; капы > 0.
- [ ] **Step 4:** R-FILTER `ConfigTests` — PASS; R-TEST — все зелёные.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): Ring.Data — SO-классы баланса и SimConfigBuilder с валидацией`.

### Task 7: ApplyConfig (hot-tweak миграция) + SimulationRunner + сцена (TDD + PlayMode-смоук)

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs` (ApplyConfig)
- Create: `client/Assets/Scripts/Presentation/SimulationRunner.cs`
- Create: `client/Assets/Scripts/Presentation/InputSampler.cs` (заглушка до Task 11:
  возвращает `default(SimInput)`; полная версия — Phase 4)
- Modify: `client/Assets/Scenes/Main.unity` (объект `Simulation` с раннером; SO-ассеты)
- Create: `client/Assets/Data/HeroConfig.asset`, `WeaponConfig.asset`,
  `MobChaserConfig.asset`, `MobGunnerConfig.asset`, `WaveConfig.asset`,
  `ArenaConfig.asset`, `GameFeelConfig.asset`, `CameraConfig.asset` (через Editor
  или YAML руками — дефолты классов)
- Create: `client/Assets/Tests/EditMode/HotTweakTests.cs`

**Interfaces:**
- Produces: `SimulationWorld.ApplyConfig(in SimConfig next)` — атомарно на границе
  тика (вызывается только между тиками): `Hp = min(Hp, next.Hero.MaxHp)`, все таймеры
  клампятся в `[0, соотв. максимум]`, wave-state сохраняет индекс; **топология арены
  не меняется** — при отличии радиуса/препятствий бросает `ArgumentException`
  (Presentation в этом случае делает рестарт, спека §3.9).
- `SimulationRunner` (MonoBehaviour): поля-ссылки на 6 SO + `GameFeelConfig`/`CameraConfig`;
  публичные `RenderSnapshot Prev, Curr`, `float Alpha`, `SimulationWorld World`,
  `long Seed`, `bool ConfigTweaked`, `event System.Action TicksFlushed` (после пачки
  тиков, до `ClearEvents`), `event System.Action WorldRestarted`,
  `void Restart(long seed)`, `void RestartNewSeed()`, `void RequestApplyConfig()`.
- Update-петля (дословно — инвариант спеки §3.2):

```csharp
        void Update()
        {
            SimInput frame = _sampler.SampleFrame();
            int ticks = _acc.Advance(Time.unscaledDeltaTime);
            for (int i = 0; i < ticks; i++)
            {
                _world.Tick(SimInputFrame.ForTick(frame, i)); // защёлка — первому тику
                (Prev, Curr) = (Curr, Prev);
                _world.CaptureSnapshot(Curr);
            }
            Alpha = _acc.Alpha;
            if (ticks > 0)
            {
                TicksFlushed?.Invoke();
                _world.ClearEvents();
                _sampler.ClearLatches();
            }
        }
```

- [ ] **Step 1 (RED):** `HotTweakTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class HotTweakTests
    {
        [Test]
        public void ApplyConfig_ClampsHpDown_KeepsTimersInRange()
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            w.Tick(default);
            var next = TestConfigs.Default();
            next.Hero.MaxHp = 50f;
            w.ApplyConfig(next);
            Assert.LessOrEqual(w.Player.Hp, 50f);
        }

        [Test]
        public void ApplyConfig_SameSequence_SameHash()
        {
            ulong Run()
            {
                var w = new SimulationWorld(9, TestConfigs.Default());
                for (int i = 0; i < 50; i++) w.Tick(default);
                var next = TestConfigs.Default(); next.Hero.MaxSpeed = 9f;
                w.ApplyConfig(next);
                for (int i = 0; i < 50; i++) w.Tick(default);
                return w.StateHash();
            }
            Assert.AreEqual(Run(), Run());
        }

        [Test]
        public void ApplyConfig_ArenaTopologyChange_Throws()
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            var next = TestConfigs.Default();
            next.Arena.Radius = 20f;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `HotTweakTests` — RED.
- [ ] **Step 3 (GREEN):** `ApplyConfig` в мире (сравнение топологии: Radius,
  ObstacleCount, поэлементно позиции/радиусы; клампы по Interfaces). `SimulationRunner`
  + `InputSampler`-заглушка по Interfaces (`Restart`: `SimConfigBuilder.Build` →
  `new SimulationWorld` → двойной `CaptureSnapshot`; `RestartNewSeed`:
  `Restart(System.Environment.TickCount64)`; `RequestApplyConfig`: флаг, применяемый
  перед следующей пачкой тиков + `ConfigTweaked = true`; исключение топологии →
  `Restart(Seed)`).
- [ ] **Step 4:** R-TEST — PASS. Создать 8 SO-ассетов в `client/Assets/Data` +
  объект `Simulation` в `Main.unity` со ссылками (через открытый Editor/MCP;
  запасной путь — текстовый YAML: guid'ы скриптов из соответствующих `.meta`).
- [ ] **Step 5:** PlayMode-смоук: Play в Editor (или MCP) — консоль без ошибок,
  `World.CurrentTick` растёт (видно дебаг-строкой `DevOverlay` Task 24 позже; на
  этом шаге достаточно `Debug.Log` раз в 30 тиков — удалить перед коммитом).
  R-COMPILE — чисто.
- [ ] **Step 6:** R-COMMIT `feat(app-88s): ApplyConfig-миграция, SimulationRunner на unscaled-времени, SO-ассеты, сцена`.

---

## Phase 2 — Геометрия и движение (спека §3.4; таск 2 из §4)

### Task 8: Geometry — аналитика кругов и свипов (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Core/Geometry.cs`
- Create: `client/Assets/Tests/EditMode/GeometryTests.cs`

**Interfaces:**
- Produces (`Ring.Simulation.Core.Geometry`, static; единственный геометрический
  модуль — переиспользуют движение, дэш, снаряды, AI):

```csharp
public const float Skin = 1e-3f;
public static bool CircleOverlap(float2 aPos, float aR, float2 bPos, float bR);
public static bool SegmentCircle(float2 p0, float2 p1, float padR, float2 c, float cR, out float t);
public static bool SegmentRingWall(float2 p0, float2 p1, float padR, float ringR, out float t);
public static bool PushOutOfCircle(ref float2 pos, float r, float2 c, float cR, out float2 normal);
public static bool ClampInsideRing(ref float2 pos, float r, float ringR, out float2 normal);
public static float2 Slide(float2 vel, float2 normal);
public static float2 Rotate(float2 v, float rad);
// arena-level helpers — ЕДИНСТВЕННЫЙ обход препятствий+стены на весь проект:
// их переиспользуют движение (T9), дэш (T10), снаряды (T16), LoS (T18), separation (T20)
public static bool SweepArena(float2 p0, float2 p1, float padR, in ArenaSimConfig arena,
    bool includeWall, out float t, out float2 normal);
public static void Depenetrate(ref float2 pos, ref float2 vel, float radius,
    in ArenaSimConfig arena, int iterations);
```

- [ ] **Step 1 (RED):** `GeometryTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class GeometryTests
    {
        [Test]
        public void SegmentCircle_FastSegment_HitsSmallCircle()
        {
            // отрезок 2 м «сквозь» цель r=0.5 — свип обязан поймать (анти-туннель)
            bool hit = Geometry.SegmentCircle(new float2(-1f, 0f), new float2(1f, 0f),
                0.1f, new float2(0f, 0f), 0.5f, out float t);
            Assert.IsTrue(hit);
            Assert.That(t, Is.InRange(0f, 0.5f));
        }

        [Test]
        public void SegmentCircle_Miss_ReturnsFalse()
        {
            Assert.IsFalse(Geometry.SegmentCircle(new float2(-1f, 2f), new float2(1f, 2f),
                0.1f, float2.zero, 0.5f, out _));
        }

        [Test]
        public void SegmentCircle_StartInside_HitsAtZero()
        {
            Assert.IsTrue(Geometry.SegmentCircle(float2.zero, new float2(1f, 0f),
                0.1f, float2.zero, 0.5f, out float t));
            Assert.AreEqual(0f, t);
        }

        [Test]
        public void SegmentRingWall_ExitFromInside_Found()
        {
            Assert.IsTrue(Geometry.SegmentRingWall(new float2(34f, 0f), new float2(36f, 0f),
                0.45f, 35f, out float t));
            Assert.That(t, Is.InRange(0f, 1f));
        }

        [Test]
        public void PushOut_SeparatesAndReportsNormal()
        {
            float2 pos = new float2(1.5f, 0f);
            bool pushed = Geometry.PushOutOfCircle(ref pos, 0.5f, float2.zero, 2f, out float2 n);
            Assert.IsTrue(pushed);
            Assert.Greater(math.length(pos), 2.5f);
            Assert.AreEqual(1f, n.x, 1e-3f);
        }

        [Test]
        public void ClampInsideRing_PullsBackAndNormalInward()
        {
            float2 pos = new float2(36f, 0f);
            Assert.IsTrue(Geometry.ClampInsideRing(ref pos, 0.45f, 35f, out float2 n));
            Assert.Less(math.length(pos), 34.56f);
            Assert.AreEqual(-1f, n.x, 1e-3f);
        }

        [Test]
        public void Slide_RemovesOnlyIntoComponent()
        {
            float2 v = Geometry.Slide(new float2(1f, -1f), new float2(0f, 1f));
            Assert.AreEqual(new float2(1f, 0f), v);
            // движение ОТ поверхности не режется
            Assert.AreEqual(new float2(1f, 1f), Geometry.Slide(new float2(1f, 1f), new float2(0f, 1f)));
        }

        [Test]
        public void Rotate_QuarterTurn()
        {
            float2 r = Geometry.Rotate(new float2(1f, 0f), math.PI / 2f);
            Assert.AreEqual(0f, r.x, 1e-5f);
            Assert.AreEqual(1f, r.y, 1e-5f);
        }
    }
}
```
- [ ] **Step 2:** R-FILTER `GeometryTests` — RED.
- [ ] **Step 3 (GREEN):** `Geometry.cs`:

```csharp
using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Analytic 2D geometry shared by movement, dash, projectiles and AI LoS.
    public static class Geometry
    {
        public const float Skin = 1e-3f;

        public static bool CircleOverlap(float2 aPos, float aR, float2 bPos, float bR)
        {
            float r = aR + bR;
            return math.lengthsq(bPos - aPos) < r * r;
        }

        /// Swept circle (segment p0→p1, inflated by padR) vs static circle; t ∈ [0,1].
        public static bool SegmentCircle(float2 p0, float2 p1, float padR,
            float2 c, float cR, out float t)
        {
            t = 0f;
            float2 d = p1 - p0;
            float2 f = p0 - c;
            float r = padR + cR;
            float a = math.dot(d, d);
            if (a < 1e-12f) return math.lengthsq(f) < r * r;
            if (math.lengthsq(f) < r * r) return true; // старт внутри → t=0
            float b = 2f * math.dot(f, d);
            float cc = math.dot(f, f) - r * r;
            float disc = b * b - 4f * a * cc;
            if (disc < 0f) return false;
            float t0 = (-b - math.sqrt(disc)) / (2f * a);
            if (t0 < 0f || t0 > 1f) return false;
            t = t0;
            return true;
        }

        /// Exit through the ring wall from inside; solves |p0 + d·t| = ringR − padR.
        public static bool SegmentRingWall(float2 p0, float2 p1, float padR,
            float ringR, out float t)
        {
            t = 0f;
            float limit = ringR - padR;
            float2 d = p1 - p0;
            float a = math.dot(d, d);
            if (a < 1e-12f) return false;
            float b = 2f * math.dot(p0, d);
            float c = math.dot(p0, p0) - limit * limit;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return false;
            float t1 = (-b + math.sqrt(disc)) / (2f * a);
            if (t1 < 0f || t1 > 1f) return false;
            t = t1;
            return true;
        }

        public static bool PushOutOfCircle(ref float2 pos, float radius,
            float2 c, float cR, out float2 normal)
        {
            normal = float2.zero;
            float2 delta = pos - c;
            float r = radius + cR;
            float distSq = math.lengthsq(delta);
            if (distSq >= r * r) return false;
            float dist = math.sqrt(distSq);
            normal = dist > 1e-6f ? delta / dist : new float2(1f, 0f);
            pos = c + normal * (r + Skin);
            return true;
        }

        public static bool ClampInsideRing(ref float2 pos, float radius,
            float ringR, out float2 normal)
        {
            normal = float2.zero;
            float limit = ringR - radius;
            float distSq = math.lengthsq(pos);
            if (distSq <= limit * limit) return false;
            float dist = math.sqrt(distSq);
            float2 outward = dist > 1e-6f ? pos / dist : new float2(1f, 0f);
            pos = outward * (limit - Skin);
            normal = -outward;
            return true;
        }

        /// Remove the velocity component pointing into the surface.
        public static float2 Slide(float2 vel, float2 normal)
        {
            float into = math.dot(vel, normal);
            return into < 0f ? vel - normal * into : vel;
        }

        public static float2 Rotate(float2 v, float rad)
        {
            float s = math.sin(rad), c = math.cos(rad);
            return new float2(c * v.x - s * v.y, s * v.x + c * v.y);
        }

        /// First contact along p0→p1 vs all obstacles (and optionally the wall).
        /// Returns t ∈ [0,1] and the surface normal at the contact point.
        public static bool SweepArena(float2 p0, float2 p1, float padR,
            in ArenaSimConfig arena, bool includeWall, out float t, out float2 normal)
        {
            t = 1f; normal = float2.zero; bool hit = false;
            for (int o = 0; o < arena.ObstacleCount; o++)
                if (SegmentCircle(p0, p1, padR, arena.ObstaclePos[o],
                        arena.ObstacleRadius[o], out float to) && to < t)
                {
                    t = to; hit = true;
                    normal = math.normalizesafe(
                        math.lerp(p0, p1, to) - arena.ObstaclePos[o], new float2(1f, 0f));
                }
            if (includeWall && SegmentRingWall(p0, p1, padR, arena.Radius, out float tw)
                && tw < t)
            {
                t = tw; hit = true;
                normal = -math.normalizesafe(math.lerp(p0, p1, tw), new float2(1f, 0f));
            }
            return hit;
        }

        /// Iterative depenetration from obstacles and the wall; slides velocity.
        public static void Depenetrate(ref float2 pos, ref float2 vel, float radius,
            in ArenaSimConfig arena, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                bool any = false;
                for (int o = 0; o < arena.ObstacleCount; o++)
                    if (PushOutOfCircle(ref pos, radius, arena.ObstaclePos[o],
                            arena.ObstacleRadius[o], out float2 n))
                    { vel = Slide(vel, n); any = true; }
                if (ClampInsideRing(ref pos, radius, arena.Radius, out float2 wn))
                { vel = Slide(vel, wn); any = true; }
                if (!any) break;
            }
        }
    }
}
```

  Дополнительный тест в `GeometryTests`:

```csharp
        [Test]
        public void SweepArena_ReportsNearestContactWithNormal()
        {
            var arena = TestConfigs.DefaultArena(); // препятствие (10,4) r=2.2
            bool hit = Geometry.SweepArena(new float2(6f, 4f), new float2(14f, 4f), 0.45f,
                arena, includeWall: false, out float t, out float2 n);
            Assert.IsTrue(hit);
            Assert.That(t, Is.InRange(0f, 1f));
            Assert.Less(n.x, 0f); // нормаль навстречу движению
        }
```

- [ ] **Step 4:** R-FILTER `GeometryTests` — PASS.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): Geometry — свипы, выталкивание, скольжение (общий модуль)`.

### Task 9: движение игрока — моментум + collide-and-slide (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Movement/PlayerMovementSystem.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs` (вызов в Tick)
- Create: `client/Assets/Tests/EditMode/MovementTests.cs`

**Interfaces:**
- Produces (`Ring.Simulation.Movement.PlayerMovementSystem`, internal static):
  `static void Update(ref PlayerState p, in SimInput input, in SimConfig cfg)` —
  моментум (без дэша: дэш — Task 10 туда же), перемещение свипом + до 3 итераций
  push/slide; `static float2 MoveTowards(float2 cur, float2 target, float maxDelta)`;
  `static void MoveWithCollisions(ref float2 pos, ref float2 vel, float2 target,
  float radius, in ArenaSimConfig arena)` (переиспользуют мобы в Phase 6).
- Порядок Tick с этого таска: sanitize → tick++ → PlayerMovementSystem.Update →
  (оружие Task 15) → (мобы Task 18–20) → (снаряды Task 16) → (волны Task 22).
  `_rng.NextUInt()`-заглушку «живости» из Task 3 УДАЛИТЬ, когда в тике появится
  реальное потребление RNG (Task 15, разброс) — до того оставить.

- [ ] **Step 1 (RED):** `MovementTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class MovementTests
    {
        static SimulationWorld World() => new SimulationWorld(1, TestConfigs.Open());

        static SimInput Move(float x, float y)
            => new SimInput { MoveDir = new float2(x, y) };

        [Test]
        public void HoldRight_AcceleratesToMaxSpeed()
        {
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(Move(1f, 0f)); // 2 c — хватает разогнаться
            Assert.AreEqual(TestConfigs.Open().Hero.MaxSpeed, w.Player.Vel.x, 0.05f);
            Assert.Greater(w.Player.Pos.x, 5f);
        }

        [Test]
        public void ReleaseInput_FrictionStopsPlayer()
        {
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(Move(1f, 0f));
            for (int i = 0; i < 60; i++) w.Tick(default);
            Assert.AreEqual(0f, math.length(w.Player.Vel), 0.05f);
        }

        [Test]
        public void Wall_StopsAndSlides()
        {
            var w = World();
            for (int i = 0; i < 400; i++) w.Tick(Move(1f, 0f)); // упереться в стену
            float2 atWall = w.Player.Pos;
            Assert.AreEqual(35f - TestConfigs.Open().Hero.Radius, math.length(atWall), 0.05f);
            for (int i = 0; i < 30; i++) w.Tick(Move(1f, 1f)); // диагональ у стены → скользит
            Assert.Greater(w.Player.Pos.y, atWall.y + 0.5f);
            Assert.LessOrEqual(math.length(w.Player.Pos), 35f - 0.44f);
        }

        [Test]
        public void Obstacle_BlocksAndSlides_NoSpeedGain()
        {
            var cfg = TestConfigs.Quiet(); // препятствие (10,4) r=2.2, волны выключены
            var w = new SimulationWorld(1, cfg);
            for (int i = 0; i < 600; i++)
            {
                w.Tick(Move(1f, 0.4f));
                float speed = math.length(w.Player.Vel);
                Assert.LessOrEqual(speed, cfg.Hero.MaxSpeed + 1e-3f); // скольжение не ускоряет
                Assert.IsFalse(Geometry.CircleOverlap(w.Player.Pos, cfg.Hero.Radius - 0.01f,
                    new float2(10f, 4f), 2.2f), "игрок внутри препятствия");
            }
            // скольжение реально продвигает: застывший у препятствия игрок — провал
            Assert.Greater(w.Player.Pos.y, 1.5f, "не обогнул препятствие — застрял");
        }

        [Test]
        public void CornerWallPlusObstacle_NoStuckNoTunnel()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(33f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 1.5f };
            var w = new SimulationWorld(1, cfg);
            float2 start = w.Player.Pos;
            for (int i = 0; i < 500; i++)
            {
                w.Tick(Move(1f, 0.05f));
                Assert.IsTrue(math.all(math.isfinite(w.Player.Pos)));
                Assert.LessOrEqual(math.length(w.Player.Pos), 35f - 0.44f);
            }
            Assert.Greater(math.distance(w.Player.Pos, start), 10f,
                "залип в углу стена+препятствие — не скользит");
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `MovementTests` — RED.
- [ ] **Step 3 (GREEN):** `PlayerMovementSystem.cs`:

```csharp
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Movement
{
    internal static class PlayerMovementSystem
    {
        public static void Update(ref PlayerState p, in SimInput input, in SimConfig cfg)
        {
            float dt = SimulationWorld.TickDt;
            if (math.lengthsq(input.MoveDir) > 1e-6f)
                p.Vel = MoveTowards(p.Vel, input.MoveDir * cfg.Hero.MaxSpeed, cfg.Hero.Accel * dt);
            else
                p.Vel = MoveTowards(p.Vel, float2.zero, cfg.Hero.Friction * dt);
            float2 target = p.Pos + p.Vel * dt;
            MoveWithCollisions(ref p.Pos, ref p.Vel, target, cfg.Hero.Radius, cfg.Arena);
        }

        public static float2 MoveTowards(float2 cur, float2 target, float maxDelta)
        {
            float2 d = target - cur;
            float lsq = math.lengthsq(d);
            if (lsq <= maxDelta * maxDelta) return target;
            return cur + d / math.sqrt(lsq) * maxDelta;
        }

        /// Collide-and-slide (спека §3.4): sweep to contact, step off by Skin,
        /// slide the velocity AND the remaining motion, retry ≤3 times.
        /// (Наивный вариант «свип, потом депенетрация» НЕ работает: свип
        /// останавливает ровно на поверхности, депенетрация не срабатывает,
        /// скорость не режется — тело замирает у стены. Найдено self-review.)
        public static void MoveWithCollisions(ref float2 pos, ref float2 vel,
            float2 target, float radius, in ArenaSimConfig arena)
        {
            for (int iter = 0; iter < 3; iter++)
            {
                if (!Geometry.SweepArena(pos, target, radius, arena, true,
                        out float t, out float2 n))
                { pos = target; break; }
                float2 contact = math.lerp(pos, target, t);
                pos = contact + n * Geometry.Skin;
                vel = Geometry.Slide(vel, n);
                target = pos + Geometry.Slide(target - contact, n);
            }
            // страховка от стартовых перекрытий/смены конфига
            Geometry.Depenetrate(ref pos, ref vel, radius, arena, 1);
        }
    }
}
```

  В `SimulationWorld.Tick` после sanitize/tick++: `PlayerMovementSystem.Update(ref _players[0], in input, in _config);`
- [ ] **Step 4:** R-TEST — PASS (все, включая детерминизм-прогоны).
- [ ] **Step 5:** R-COMMIT `feat(app-88s): моментум и collide-and-slide игрока`.

## Phase 3 — Дэш (спека §3.4; таск 3 из §4)

### Task 10: дэш — i-frames, кулдаун, буфер, свип (TDD)

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Movement/PlayerMovementSystem.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
- Create: `client/Assets/Tests/EditMode/DashTests.cs`

**Interfaces:**
- Produces: в `PlayerMovementSystem.Update` — фаза дэша до моментума; событие
  `PlayerDashed` эмитит мир (система возвращает `bool dashStarted` — сигнатура
  меняется на `static bool Update(ref PlayerState p, in SimInput input, in SimConfig cfg)`);
  `MatchStats.DashesUsed++` в мире при старте.
- Семантика (спека §3.4): `DashRequested` взводит `DashBufferTimer = DashBufferWindow`;
  старт при `DashBufferTimer > 0 && DashCooldown ≤ 0 && DashTimer ≤ 0`; направление —
  `MoveDir`, при нулевом — к `AimPoint`; во время дэша `Vel = DashDir * DashSpeed`,
  обычное управление игнорируется; `IframeTimer = DashIframes` со старта.

- [ ] **Step 1 (RED):** `DashTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class DashTests
    {
        static readonly SimInput DashRight = new SimInput
            { MoveDir = new float2(1f, 0f), DashRequested = true };
        static readonly SimInput HoldRight = new SimInput { MoveDir = new float2(1f, 0f) };

        [Test]
        public void Dash_OverridesVelocityForDuration()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            Assert.AreEqual(TestConfigs.Open().Hero.DashSpeed, w.Player.Vel.x, 0.01f);
            Assert.Greater(w.Player.DashTimer, 0f);
        }

        [Test]
        public void Dash_CooldownBlocksSecondDash()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            for (int i = 0; i < 6; i++) w.Tick(HoldRight); // дэш кончился (0.15 c = 4.5 тика)
            w.Tick(DashRight);                              // кулдаун 1.2 c ещё идёт
            Assert.AreEqual(1, w.Stats.DashesUsed);
            for (int i = 0; i < 40; i++) w.Tick(HoldRight); // кулдаун прошёл
            w.Tick(DashRight);
            Assert.AreEqual(2, w.Stats.DashesUsed);
        }

        [Test]
        public void DashBuffer_LatchedRequestFiresWhenCooldownEnds()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            for (int i = 0; i < 40; i++) w.Tick(HoldRight);
            // запрос за ~3 тика до конца кулдауна — буфер 0.15 c (4.5 тика) доносит его
            var w2 = new SimulationWorld(1, TestConfigs.Open());
            w2.Tick(DashRight);                       // тик 1: дэш; кулдаун 1.2 c = 36 тиков
            for (int i = 0; i < 32; i++) w2.Tick(HoldRight); // тики 2..33
            w2.Tick(DashRight);                       // тик 34: кулдаун ещё жив — в буфер
            Assert.AreEqual(1, w2.Stats.DashesUsed);  // немедленного дэша нет
            for (int i = 0; i < 4; i++) w2.Tick(HoldRight); // кулдаун истекает — буфер срабатывает
            Assert.AreEqual(2, w2.Stats.DashesUsed);
        }

        [Test]
        public void ZeroMoveDir_DashesTowardAim()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(new SimInput { AimPoint = new float2(0f, 10f), DashRequested = true });
            Assert.Greater(w.Player.Vel.y, 0f);
        }

        [Test]
        public void Iframes_ActiveDuringWindowThenExpire()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            Assert.Greater(w.Player.IframeTimer, 0f);
            for (int i = 0; i < 7; i++) w.Tick(HoldRight); // 0.2 c = 6 тиков
            Assert.AreEqual(0f, w.Player.IframeTimer);
        }

        [Test]
        public void DashIntoObstacle_StopsAtSurface_NoTunnel()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(2f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 0.6f }; // дэш-шаг 0.73 м > диаметра нет, но свип обязан
            var w = new SimulationWorld(1, cfg);
            for (int i = 0; i < 10; i++) w.Tick(DashRight);
            Assert.Less(w.Player.Pos.x, 2f - 0.6f); // остановился до центра препятствия
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `DashTests` — RED.
- [ ] **Step 3 (GREEN):** в `PlayerMovementSystem.Update` (новая сигнатура `bool`):

```csharp
        public static bool Update(ref PlayerState p, in SimInput input, in SimConfig cfg)
        {
            float dt = SimulationWorld.TickDt;
            var hero = cfg.Hero;
            p.DashBufferTimer = input.DashRequested
                ? hero.DashBufferWindow
                : math.max(0f, p.DashBufferTimer - dt);
            p.DashCooldown = math.max(0f, p.DashCooldown - dt);
            p.IframeTimer = math.max(0f, p.IframeTimer - dt);
            bool started = false;
            if (p.DashTimer > 0f)
            {
                p.DashTimer = math.max(0f, p.DashTimer - dt);
                p.Vel = p.DashDir * hero.DashSpeed;
            }
            else if (p.DashBufferTimer > 0f && p.DashCooldown <= 0f)
            {
                float2 dir = math.lengthsq(input.MoveDir) > 1e-6f
                    ? math.normalizesafe(input.MoveDir)
                    : math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                p.DashDir = dir;
                p.DashTimer = hero.DashDuration;
                p.DashCooldown = hero.DashCooldown;
                p.IframeTimer = hero.DashIframes;
                p.DashBufferTimer = 0f;
                p.Vel = dir * hero.DashSpeed;
                started = true;
            }
            else
            {
                p.Vel = math.lengthsq(input.MoveDir) > 1e-6f
                    ? MoveTowards(p.Vel, input.MoveDir * hero.MaxSpeed, hero.Accel * dt)
                    : MoveTowards(p.Vel, float2.zero, hero.Friction * dt);
            }
            float2 target = p.Pos + p.Vel * dt;
            MoveWithCollisions(ref p.Pos, ref p.Vel, target, hero.Radius, cfg.Arena);
            return started;
        }
```

  В мире: `if (PlayerMovementSystem.Update(...)) { _stats.DashesUsed++;
  Emit(SimEventKind.PlayerDashed, _players[0].Pos, 0, default, 0f); }`
- [ ] **Step 4:** R-TEST — PASS.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): дэш — i-frames, кулдаун, буфер ввода, свип`.

## Phase 4 — Input, камера, грейбокс, HUD → веха-плейтест 1 (§3.8, §3.10; таск 4 из §4)

### Task 11: InputSystem_Actions → карта Gameplay + InputSampler + AimProvider

**Files:**
- Modify: `client/Assets/InputSystem_Actions.inputactions` (карта `Player`→`Gameplay`:
  `Attack`→`Fire`, `Jump`→`Dash`, `Look`→`Aim` с ребиндом на `<Mouse>/position`;
  удалить Interact/Crouch/Sprint/Previous/Next; карту `UI` сохранить)
- Modify: `client/Assets/Scripts/Presentation/InputSampler.cs` (полная версия)
- Create: `client/Assets/Scripts/Presentation/AimProvider.cs`

**Interfaces:**
- Produces: `InputSampler` (plain class): конструктор
  `(InputActionAsset asset, AimProvider aim)` — находит действия
  `Gameplay/Move|Aim|Fire|Dash`; `SimInput SampleFrame()`; `void ClearLatches()`;
  `void Enable()/Disable()`. Защёлка: подписка `dashAction.performed → _dashLatch = true`.
- `AimProvider` (MonoBehaviour): `float2 CurrentAimSimPos` — луч
  `Camera.ScreenPointToRay(Mouse.current.position)` в плоскость y=0
  (`t = -origin.y / dir.y`, принимается только `t > 0` и `|dir.y| > 1e-4`);
  неуспех/потеря фокуса → прошлое значение (спека §3.8). Плоскость арены: сим-`(x,y)`
  → мир-`(x, 0, y)` (константа маппинга — здесь и только здесь + в вьюхах).

- [ ] **Step 1:** Переделать `.inputactions` через Editor (открыт у владельца/MCP) или
  текстово (JSON-файл): переименования и биндинги из Files выше. `generateWrapperCode`
  остаётся 0.
- [ ] **Step 2:** `InputSampler`:

```csharp
using Ring.Simulation.Core;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// Frame-level sampler: held values per frame, edge latches consumed by tick 1.
    public sealed class InputSampler
    {
        readonly InputAction _move, _aim, _fire, _dash;
        readonly AimProvider _aimProvider;
        bool _dashLatch;

        public InputSampler(InputActionAsset asset, AimProvider aimProvider)
        {
            _move = asset.FindAction("Gameplay/Move", true);
            _aim = asset.FindAction("Gameplay/Aim", true);
            _fire = asset.FindAction("Gameplay/Fire", true);
            _dash = asset.FindAction("Gameplay/Dash", true);
            _aimProvider = aimProvider;
            _dash.performed += _ => _dashLatch = true;
        }

        public void Enable() { _move.Enable(); _aim.Enable(); _fire.Enable(); _dash.Enable(); }

        public SimInput SampleFrame() => new SimInput
        {
            MoveDir = _move.ReadValue<UnityEngine.Vector2>(),
            AimPoint = _aimProvider.CurrentAimSimPos,
            FireHeld = _fire.IsPressed() || _fire.WasPressedThisFrame(),
            DashRequested = _dashLatch || _dash.WasPressedThisFrame()
        };

        public void ClearLatches() => _dashLatch = false;
    }
}
```

- [ ] **Step 3:** `AimProvider` по Interfaces (≈25 строк); подключить самплер в
  `SimulationRunner` (заменить заглушку Task 7).
- [ ] **Step 4:** R-COMPILE чисто; PlayMode-смоук: WASD двигает `Player.Pos`
  (лог/инспектор), Space дэшит. R-TEST — зелёные.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): карта Gameplay в project-wide actions, сэмплер с защёлками, прицел в плоскость арены`.

### Task 12: PlayerView + интерполяция + CameraRig

**Files:**
- Create: `client/Assets/Scripts/Presentation/PlayerView.cs`
- Create: `client/Assets/Scripts/Presentation/CameraRig.cs`
- Modify: `client/Assets/Scenes/Main.unity` (капсула игрока, риг камеры)

**Interfaces:**
- Produces: `PlayerView` (MonoBehaviour): в `LateUpdate` берёт у раннера
  `Prev.Player`/`Curr.Player` и `Alpha`, ставит
  `transform.position = Vector3.Lerp(prevW, currW, alpha)` (сим→мир `(x,0,y)`),
  поворот — к `Curr.Player.AimPoint` (per-frame, спека §3.11-отклик).
- `CameraRig` (MonoBehaviour, поля из `CameraConfig`): позиция =
  `playerW + Rotate(offset) + lookAhead * (aimW - playerW)`, `lookAhead = 0.25`,
  демпфирование `Vector3.SmoothDamp` (`Damp` из SO); pitch фиксированный (55°),
  `offset = Quaternion.Euler(Pitch,0,0) * Vector3.back * Distance`. Тряска —
  добавка из `GameFeelDirector` (Task 26; до того — ноль).

- [ ] **Step 1:** Написать оба класса (полные, по Interfaces), собрать сцену:
  `Player` (Capsule, материал-эмиссив), `CameraRig` → `Main Camera` дочерней.
- [ ] **Step 2:** R-COMPILE; PlayMode-смоук: камера следует с look-ahead, угол 55°.
- [ ] **Step 3:** R-COMMIT `feat(app-88s): PlayerView с интерполяцией, камера ¾ с look-ahead`.

### Task 13: грейбокс из ArenaConfig

**Files:**
- Create: `client/Assets/Scripts/Presentation/GreyboxBuilder.cs`
- Modify: `client/Assets/Scenes/Main.unity` (объект `Arena` с билдером)
- Create: `client/Assets/Art/Materials/Floor.mat`, `Wall.mat`, `Obstacle.mat`
  (URP Lit, тёмные; у препятствий слабый эмиссив-контур)

**Interfaces:**
- Produces: `GreyboxBuilder` (MonoBehaviour, поле `ArenaConfig`): `Build()` в
  `Awake` — пол (Cylinder y=-0.5 h=1 r=Radius), стена (48 кубов по окружности,
  высота 3), препятствия (Cylinder r из конфига, высота 2). Никаких коллайдеров
  PhysX для геймплея — коллизии считает Simulation; PhysX-коллайдеры только на
  полу/стенах/препятствиях для **косметики** (гильзы Task 27 должны от них
  отскакивать) — слой `Cosmetics`.

- [ ] **Step 1:** Код билдера (полный) + материалы + объект сцены.
- [ ] **Step 2:** PlayMode-смоук: арена видна, игрок скользит вдоль препятствий
  (сим-коллизия) ровно по видимой поверхности (совпадение конфигов по построению).
- [ ] **Step 3:** R-COMMIT `feat(app-88s): грейбокс-арена из ArenaConfig`.

### Task 14: HUD-каркас + веха-плейтест 1

**Files:**
- Create: `client/Assets/Scripts/Presentation/HudController.cs`
- Modify: `client/Assets/Scenes/Main.unity` (Canvas: HP-полоса, кулдаун дэша,
  номер волны; EventSystem + InputSystemUIInputModule)

**Interfaces:**
- Produces: `HudController` (MonoBehaviour): читает `Curr.Player.Hp`,
  `Curr.Player.DashCooldown`, `Curr.Wave.WaveIndex` из раннера в `LateUpdate`;
  uGUI + TMP (пакетные, спека §3.10).

- [ ] **Step 1:** Код + Canvas в сцене (три элемента, серые полосы/текст).
- [ ] **Step 2:** R-COMPILE + R-TEST — чисто/зелёные; полный прогон.
- [ ] **Step 3:** R-COMMIT `feat(app-88s): HUD-каркас (HP, дэш, волна)`.
- [ ] **Step 4:** **ВЕХА-ПЛЕЙТЕСТ 1 (владелец): мувмент+дэш.** Hot-tweak чисел
  Hero/Camera в PlayMode. Фидбек → `bd update app-88s --notes "веха1: …"`;
  правки чисел — в SO-ассеты, коммит `chore(app-88s): числа веха-1`.

## Phase 5 — Оружие и снаряды → веха-плейтест 2 (§3.5; таск 5 из §4)

### Task 15: WeaponSystem — кулдаун с переносом, отдача, разброс, спавн (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Combat/WeaponSystem.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (вызов + `SpawnProjectile` + удалить `_rng.NextUInt()`-заглушку «живости»)
- Create: `client/Assets/Tests/EditMode/WeaponTests.cs`

**Interfaces:**
- Produces: `internal static class WeaponSystem` (`Ring.Simulation.Combat`):
  `static void Update(SimulationWorld w, ref PlayerState p, in SimInput input)` —
  internal-доступ к миру (один asmdef). Мир получает:
  `internal int SpawnProjectile(ProjectileOwner owner, float2 pos, float2 vel,
  float damage, float radius, float ttl)` — кап `MaxProjectiles` →
  `Stats.ProjectileSpawnsSkipped++` и `-1`; иначе `Emit(ProjectileFired, …)`, возврат id.
  `internal ref Random Rng => ref _rng`; `internal ref MatchStats StatsRef`.
- Семантика (спека §3.5): `FireCooldown -= dt` всегда; отдача
  `RecoilOffset -= RecoilRecovery*dt` (кламп 0); стрельба при `FireHeld && Alive &&
  (CanFireWhileDash || DashTimer≤0)`; `interval = max(FireInterval, 1e-3f)`
  (страховка от бесконечного цикла при нулевом конфиге); цикл
  `while (FireCooldown ≤ 0)`: `overshoot = min(-FireCooldown, dt)`;
  направление — **явно**:
  `float2 baseDir = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));`
  угол = `Rng.NextFloat(-a, a)`, `a = SpreadRad + RecoilOffset`;
  `float2 dir = Geometry.Rotate(baseDir, angle);` спавн на
  `p.Pos + dir*(MuzzleOffset + overshoot*ProjectileSpeed)`, скорость `dir*ProjectileSpeed`;
  `RecoilOffset = min(RecoilMax, RecoilOffset + RecoilPerShot)`;
  `ShotsFired++`; `FireCooldown += interval`. Не стреляет — `FireCooldown`
  клампится снизу нулём (после отпускания первый выстрел мгновенный).

- [ ] **Step 1 (RED):** `WeaponTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WeaponTests
    {
        static readonly SimInput Fire = new SimInput
            { AimPoint = new float2(10f, 0f), FireHeld = true };

        [Test]
        public void HoldFire_AverageRpmMatchesInterval()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            for (int i = 0; i < 300; i++) w.Tick(Fire); // 10 c
            // 10 c / 0.12 c = 83.3 → 83±1 (перенос дробного остатка, не 80 и не 90)
            Assert.That(w.Stats.ShotsFired, Is.InRange(82, 84));
        }

        [Test]
        public void Recoil_AccumulatesWhileFiring_DecaysToZeroAfter()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            float peak = 0f;
            for (int i = 0; i < 60; i++)
            {
                w.Tick(Fire);
                peak = math.max(peak, w.Player.RecoilOffset);
            }
            // отдача реально копится (recovery < скорости накопления), не фаза-лотерея
            Assert.Greater(peak, cfg.Weapon.RecoilPerShotRad * 2f);
            for (int i = 0; i < 120; i++) w.Tick(default);
            Assert.AreEqual(0f, w.Player.RecoilOffset, 1e-4f);
        }

        [Test]
        public void NoFireWhileDashing_WhenConfigForbids()
        {
            var w = new SimulationWorld(1, TestConfigs.Open()); // CanFireWhileDash=false
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), FireHeld = true,
                                  DashRequested = true, AimPoint = new float2(10f, 0f) });
            Assert.AreEqual(0, w.Stats.ShotsFired);
        }

        [Test]
        public void ProjectileCap_SkipsDeterministically()
        {
            var cfg = TestConfigs.Open();
            cfg.Weapon.ProjectileLifetime = 60f; // снаряды не умирают
            cfg.Weapon.FireInterval = 0.001f;    // залить кап мгновенно
            static ulong Run(SimConfig c2)
            {
                var w2 = new SimulationWorld(1, c2);
                for (int i = 0; i < 60; i++) w2.Tick(Fire);
                Assert.Greater(w2.Stats.ProjectileSpawnsSkipped, 0);
                return w2.StateHash();
            }
            Assert.AreEqual(Run(cfg), Run(cfg)); // деградация по капу детерминирована
        }

        [Test]
        public void FiredEvent_EmittedPerShot()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(Fire); // первый выстрел мгновенный
            int fired = 0;
            for (int i = 0; i < w.EventCount; i++)
                if (w.GetEvent(i).Kind == SimEventKind.ProjectileFired) fired++;
            Assert.AreEqual(1, fired);
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `WeaponTests` — RED.
- [ ] **Step 3 (GREEN):** `WeaponSystem` по Interfaces (полный while-цикл из спеки §3.5);
  снаряды в мире: массив, `SpawnProjectile`, `RemoveProjectileAt(int)` (swap-remove).
- [ ] **Step 4:** R-TEST — PASS (детерминизм-тесты обновят «живость»: RNG теперь
  потребляется стрельбой; idle-мир без стрельбы хеш меняет только тиком — проверить,
  что `HashChangesBetweenTicks` остался валиден: tick входит в хеш — да).
- [ ] **Step 5:** R-COMMIT `feat(app-88s): оружие — перенос остатка кулдауна, отдача, разброс, кап снарядов`.

### Task 16: ProjectileSystem — свип, матрица поражения, урон (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/Combat/ProjectileSystem.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (вызов; `DamageMob`/`DamagePlayer`/`KillMob`)
- Create: `client/Assets/Tests/EditMode/ProjectileTests.cs`

**Interfaces:**
- Produces: `internal static class ProjectileSystem`:
  `static void Update(SimulationWorld w)` — обратный проход по снарядам:
  `PrevPos = Pos`; `target = Pos + Vel*dt`; `Ttl -= dt`; свип: наименьший t среди
  стены (`SegmentRingWall`), препятствий (`SegmentCircle`) и целей по матрице
  (Player-снаряд → мобы; Mob-снаряд → игрок, только если `Alive && IframeTimer≤0`
  не проверяется здесь — i-frames применяет `DamagePlayer`); попадание в цель:
  `ProjectileHit`(мобу)/`DamagePlayer`; стена/препятствие: `ProjectileBlocked`;
  без контакта: `Pos = target`, `Ttl ≤ 0` → `ProjectileExpired` + удаление.
- Мир: `internal void DamageMob(int index, float dmg, float2 pos)` —
  `Hp -= dmg; ShotsHit++;` при `Hp ≤ 0` → `Kills++`, `Emit(MobDied)`, swap-remove;
  `internal void DamagePlayer(float dmg, float2 pos)` — `IframeTimer > 0` →
  поглощение (без события); иначе `Hp -= dmg; DamageTaken += dmg;
  Emit(PlayerDamaged)`; `Hp ≤ 0` → `Alive = false; DeathTick = tick;
  DashTimer=IframeTimer=0; Emit(PlayerDied)` (однократно).
- Тестовые швы — `internal`, тестам видны через `InternalsVisibleTo` (Task 5):
  `internal int SpawnMobForTest(MobType type, float2 pos)` — мобы без AI
  (Ai = Idle не тикается до Phase 6) = цели тестов;
  `internal int SpawnProjectileForTest(ProjectileOwner owner, float2 pos, float2 vel,
  float damage, float radius, float ttl)` — обёртка над `SpawnProjectile`.
  Для болванок вехи 2 из Presentation — отдельный дев-метод, отсутствующий в
  прод-билде: `#if UNITY_EDITOR || DEVELOPMENT_BUILD` →
  `public int DevSpawnMob(MobType type, float2 pos) => SpawnMobForTest(type, pos);`.

- [ ] **Step 1 (RED):** `ProjectileTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ProjectileTests
    {
        static readonly SimInput FireRight = new SimInput
            { AimPoint = new float2(20f, 0f), FireHeld = true };

        static SimConfig NoSpread()
        {
            var c = TestConfigs.Open();
            c.Weapon.SpreadRad = 0f; c.Weapon.RecoilPerShotRad = 0f;
            return c;
        }

        [Test]
        public void PlayerShot_KillsMob_EmitsHitAndDeath()
        {
            var w = new SimulationWorld(1, NoSpread());
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            int hits = 0, deaths = 0;
            for (int i = 0; i < 60 && deaths == 0; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                {
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileHit) hits++;
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied) deaths++;
                }
            }
            Assert.Greater(hits, 0);
            Assert.AreEqual(1, deaths);
            Assert.AreEqual(1, w.Stats.Kills);
        }

        [Test]
        public void FastProjectile_SmallTarget_NoTunnel()
        {
            var c = NoSpread();
            c.Weapon.ProjectileSpeed = 120f; // 4 м/тик >> диаметра цели 1 м
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            for (int i = 0; i < 30; i++) w.Tick(FireRight);
            Assert.Greater(w.Stats.ShotsHit, 0);
        }

        [Test]
        public void ObstacleBeforeMob_BlocksShot_NoDamage()
        {
            var c = NoSpread();
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            c.Arena.ObstacleRadius = new[] { 1.5f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(9f, 0f));
            bool blocked = false;
            for (int i = 0; i < 60; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileBlocked) blocked = true;
            }
            Assert.IsTrue(blocked);
            Assert.AreEqual(0, w.Stats.ShotsHit);
        }

        [Test]
        public void TwoTargetsOnPath_NearestDiesFirst()
        {
            var w = new SimulationWorld(1, NoSpread());
            int nearId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnMobForTest(MobType.Chaser, new float2(8f, 0f));
            int firstDeadId = -1;
            for (int i = 0; i < 90 && firstDeadId < 0; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied)
                    { firstDeadId = w.GetEvent(e).EntityId; break; }
            }
            Assert.AreEqual(nearId, firstDeadId); // ближняя цель умирает первой
        }

        [Test]
        public void MobShot_IframesAbsorb_ThenDamagePasses()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            // вражеский снаряд прямо перед игроком в кадре дэша — i-frames активны
            w.SpawnProjectileForTest(ProjectileOwner.Mob,
                w.Player.Pos + new float2(1.2f, 0f), new float2(-14f, 0f), 8f, 0.15f, 3f);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });
            w.Tick(default);
            Assert.AreEqual(c.Hero.MaxHp, w.Player.Hp); // i-frames поглотили
            for (int i = 0; i < 10; i++) w.Tick(default); // дэш и i-frames истекли
            // второй снаряд — от ТЕКУЩЕЙ позиции игрока (после дэша он сместился)
            w.SpawnProjectileForTest(ProjectileOwner.Mob,
                w.Player.Pos + new float2(1.2f, 0f), new float2(-14f, 0f), 8f, 0.15f, 3f);
            for (int i = 0; i < 4; i++) w.Tick(default);
            Assert.Less(w.Player.Hp, c.Hero.MaxHp);
        }

        [Test]
        public void MultiKillSameTick_SwapRemoveKeepsListConsistent() // спека §3.13 п.11
        {
            var w = new SimulationWorld(1, NoSpread());
            int a = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            int b = w.SpawnMobForTest(MobType.Chaser, new float2(5.2f, 0.4f));
            int c = w.SpawnMobForTest(MobType.Chaser, new float2(5.2f, -0.4f));
            Assert.IsTrue(a != b && b != c); // id стабильны и уникальны
            // три широких снаряда сносят всех в один тик — swap-remove в середине списка
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, 0f),
                new float2(35f, 0f), 100f, 0.6f, 1f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, 0.4f),
                new float2(35f, 0f), 100f, 0.6f, 1f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, -0.4f),
                new float2(35f, 0f), 100f, 0.6f, 1f);
            int died = 0;
            for (int i = 0; i < 5; i++)
            {
                w.ClearEvents();
                w.Tick(default);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied) died++;
            }
            Assert.AreEqual(3, died); // никого не потеряли и не задвоили
            var snap = new RenderSnapshot(NoSpread().Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(0, snap.MobCount);
        }

        [Test]
        public void DamageMatrix_MobShotIgnoresMobs_PlayerShotNoPiercing() // §3.5 негативы
        {
            var cfg = NoSpread();
            var w = new SimulationWorld(1, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnMobForTest(MobType.Chaser, new float2(8f, 0f));
            // вражеский снаряд летит к игроку сквозь двух мобов — мобов игнорирует
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(10f, 0f),
                new float2(-30f, 0f), 5f, 0.15f, 2f);
            for (int i = 0; i < 12; i++) w.Tick(default);
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(2, snap.MobCount);
            for (int m = 0; m < snap.MobCount; m++)
                Assert.AreEqual(cfg.Chaser.MaxHp, snap.Mobs[m].Hp); // мобы не задеты
            Assert.Less(w.Player.Hp, cfg.Hero.MaxHp);               // игрок — задет
            // пробития нет: сверхмощный снаряд игрока убивает только ближнего
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(3f, 0f),
                new float2(35f, 0f), 1000f, 0.12f, 1f);
            for (int i = 0; i < 6; i++) w.Tick(default);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(1, snap.MobCount);
            Assert.AreEqual(cfg.Chaser.MaxHp, snap.Mobs[0].Hp); // задний жив и цел
        }

        [Test]
        public void Ttl_ExpiresWithEvent()
        {
            var c = NoSpread();
            c.Weapon.ProjectileLifetime = 0.1f; // 3 тика
            var w = new SimulationWorld(1, c);
            w.Tick(FireRight);
            bool expired = false;
            for (int i = 0; i < 6; i++)
            {
                w.ClearEvents();
                w.Tick(default);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileExpired) expired = true;
            }
            Assert.IsTrue(expired);
        }
    }
}
```

  (Сигнатуры обоих швов — в Interfaces выше; `internal` + `InternalsVisibleTo`.)
- [ ] **Step 2:** R-FILTER `ProjectileTests` — RED.
- [ ] **Step 3 (GREEN):** `ProjectileSystem.Update` + `DamageMob`/`DamagePlayer` по
  Interfaces. Порядок в тике: движение игрока → оружие → (мобы, Phase 6) → снаряды →
  (волны). Убрать RNG-заглушку, если ещё жива.
- [ ] **Step 4:** R-TEST — PASS.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): снаряды — свип, матрица поражения, урон, i-frames`.

### Task 17: вьюхи снарядов/мобов по ID + болванки + базовый фидбек → веха-плейтест 2

**Files:**
- Create: `client/Assets/Scripts/Presentation/ViewRegistry.cs`
- Create: `client/Assets/Scripts/Presentation/ProjectileView.cs`, `MobView.cs`
- Create: `client/Assets/Scripts/Presentation/MuzzleFlashAndSfx.cs` (минимум вехи:
  вспышка ствола — партикл, звук выстрела/попадания с рандомом питча; полный
  game-feel — Phase 8)
- Create: `client/Assets/Audio/Placeholders/shot.wav`, `hit.wav`, `mob_death.wav`,
  `dash.wav`, `player_hit.wav` (Kenney CC0; LFS уже покрывает wav)
- Modify: `client/Assets/Scenes/Main.unity` (префабы вьюх, объект `TargetSpawner`
  вехи 2: N болванок через дев-метод мира `DevSpawnMob` (есть только в
  Editor/dev-билде) — временный MonoBehaviour `PracticeTargets`, удаляется в Phase 7)

**Interfaces:**
- Produces: `ViewRegistry` (MonoBehaviour) — на `TicksFlushed`/`LateUpdate`
  сопоставляет `Curr.Mobs[0..MobCount]`/`Curr.Projectiles[..]` с пулом вьюх
  **по Id** (спека §3.7): новый Id → взять из пула, поставить в позицию без
  интерполяции; исчезнувший Id → вернуть в пул; живой → `Lerp(prev, curr, Alpha)`
  (prev ищется по Id в `Prev`, отсутствие → curr). Словари/списки предаллоцированы,
  без аллокаций в кадре (кроме первичного наполнения пула).
- `ProjectileView`: эмиссив-сфера + `TrailRenderer` (трейсер; время жизни трейла =
  `GameFeelConfig.TracerFadeSeconds` = 0.4).
- `MobView`: капсула (Chaser — красный эмиссив-акцент, Gunner — синий; болванка —
  серый), `Flash(duration)` — реализуется сразу полноценно (MaterialPropertyBlock:
  установка `_EmissionColor` и затухание в `Update` по unscaled dt); Task 25 лишь
  подключает вызов к событиям.
- `MuzzleFlashAndSfx`: слушает события `ProjectileFired/ProjectileHit/MobDied/
  PlayerDashed/PlayerDamaged` (в `TicksFlushed` до `ClearEvents` — раннер даёт
  `World.EventCount/GetEvent`), играет звук из пула AudioSource (8 источников,
  `pitch = 1 ± GameFeelConfig.PitchRange`, Random Presentation — `UnityEngine.Random`,
  допустим вне Simulation).

- [ ] **Step 1:** Код всех четырёх классов (полный), префабы, сцена, 5 wav
  (скачать c kenney.nl — пак «Sci-Fi Sounds»/«Impact Sounds», CC0; проверить
  `git check-attr filter -- client/Assets/Audio/Placeholders/shot.wav` → lfs).
- [ ] **Step 2:** R-COMPILE + PlayMode-смоук: стрельба по болванкам — снаряды
  летят, трейсеры, звук, болванки умирают.
- [ ] **Step 3:** R-TEST — все зелёные; R-COMMIT
  `feat(app-88s): вьюхи по ID, трейсеры, плейсхолдер-звук, болванки вехи 2`.
- [ ] **Step 4:** **ВЕХА-ПЛЕЙТЕСТ 2 (владелец): стрельба по мишеням.**
  Hot-tweak Weapon/GameFeel; фидбек → bd note; числа → SO, `chore(app-88s): числа веха-2`.

---

## Phase 6 — Мобы (спека §3.6; таск 6 из §4)

### Task 18: Targeting — упреждение и линия огня (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/AI/Targeting.cs`
- Create: `client/Assets/Tests/EditMode/MobAiTests.cs` (первая часть)

**Interfaces:**
- Produces (`Ring.Simulation.AI`, public — тесты зовут напрямую):

```csharp
public static class Targeting
{
    /// Перехват: точка выстрела с упреждением leadFactor (0 — в текущую позицию).
    public static float2 AimWithLead(float2 from, float2 targetPos, float2 targetVel,
        float projSpeed, float leadFactor);
    /// Свободна ли линия огня (сегмент from→to, радиус снаряда) от препятствий.
    public static bool HasLineOfFire(float2 from, float2 to, float padR,
        in ArenaSimConfig arena);
}
```

- [ ] **Step 1 (RED):** в `MobAiTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class TargetingTests
    {
        [Test]
        public void StationaryTarget_AimsExactlyAtIt()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                float2.zero, 14f, 0.8f);
            Assert.AreEqual(1f, dir.x, 1e-4f);
            Assert.AreEqual(0f, dir.y, 1e-4f);
        }

        [Test]
        public void MovingTarget_LeadsAhead()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                new float2(0f, 5f), 14f, 1f);
            Assert.Greater(dir.y, 0.1f); // целится вперёд по ходу цели
        }

        [Test]
        public void TargetFasterThanProjectile_FallbackNoNaN()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                new float2(0f, 50f), 14f, 1f);
            Assert.IsTrue(math.all(math.isfinite(dir)));
            Assert.AreEqual(1f, math.length(dir), 1e-3f);
        }

        [Test]
        public void LineOfFire_BlockedByObstacle()
        {
            var arena = TestConfigs.DefaultArena(); // препятствие (10,4) r2.2
            Assert.IsFalse(Targeting.HasLineOfFire(new float2(10f, 0f),
                new float2(10f, 8f), 0.15f, arena));
            Assert.IsTrue(Targeting.HasLineOfFire(new float2(-20f, -20f),
                new float2(-25f, -20f), 0.15f, arena));
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `TargetingTests` — RED.
- [ ] **Step 3 (GREEN):** `Targeting.cs`:

```csharp
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.AI
{
    public static class Targeting
    {
        public static float2 AimWithLead(float2 from, float2 targetPos, float2 targetVel,
            float projSpeed, float leadFactor)
        {
            float2 toT = targetPos - from;
            float a = math.dot(targetVel, targetVel) - projSpeed * projSpeed;
            float b = 2f * math.dot(toT, targetVel);
            float c = math.dot(toT, toT);
            float t = 0f;
            if (math.abs(a) < 1e-4f)
            {
                if (math.abs(b) > 1e-6f) t = math.max(0f, -c / b);
            }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float sq = math.sqrt(disc);
                    float t1 = (-b - sq) / (2f * a);
                    float t2 = (-b + sq) / (2f * a);
                    t = t1 > 0f ? t1 : math.max(0f, t2);
                }
            }
            float2 predicted = targetPos + targetVel * (t * leadFactor);
            return math.normalizesafe(predicted - from, new float2(1f, 0f));
        }

        public static bool HasLineOfFire(float2 from, float2 to, float padR,
            in ArenaSimConfig arena)
        {
            for (int o = 0; o < arena.ObstacleCount; o++)
                if (Geometry.SegmentCircle(from, to, padR,
                        arena.ObstaclePos[o], arena.ObstacleRadius[o], out _))
                    return false;
            return true;
        }
    }
}
```

- [ ] **Step 4:** R-FILTER `TargetingTests` — PASS.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): Targeting — перехват с фолбэками и линия огня`.

### Task 19: MobAiSystem — FSM Chaser и Gunner (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/AI/MobAiSystem.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (вызов между оружием и снарядами; `MobConfigFor(MobType)`)
- Modify: `client/Assets/Tests/EditMode/MobAiTests.cs`

**Interfaces:**
- Produces: `internal static class MobAiSystem`: `static void Update(SimulationWorld w)`.
  Chaser: `Idle→Chase→Telegraph→Recover` (контакт-урон в тике удара:
  `CircleOverlap(m.Pos, cfg.AttackRange, p.Pos, hero.Radius)` — через
  `w.DamagePlayer`, i-frames уважаются); обход препятствий `SteerAround`
  (тангенс; сторона = знак векторного произведения, при 0 — чётность `Id`).
  Gunner: держит `PreferredRange±RangeTolerance` (радиальное движение с обходом),
  в допуске — страйф `StrafeSign` и выстрел при `FireCooldown ≤ 0 &&
  Targeting.HasLineOfFire`; блокировка страйфа (скорость < 10% от `StrafeSpeed`) →
  `StrafeSign` инвертируется. Мобы двигаются через
  `PlayerMovementSystem.MoveWithCollisions` (reuse). При `!p.Alive` — все в
  `Idle`, затухание скорости.
- `SpawnMobForTest` начинает выставлять `Ai = Idle`, `StrafeSign = (id & 1) == 0 ? 1 : -1`.

- [ ] **Step 1 (RED):** дополнить `MobAiTests.cs`:

```csharp
    public class MobAiTests
    {
        static readonly SimInput Idle = default;

        [Test]
        public void Chaser_ClosesDistanceToPlayer()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.SpawnMobForTest(MobType.Chaser, new float2(15f, 0f));
            float d0 = 15f;
            for (int i = 0; i < 60; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(TestConfigs.Open().Arena);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), d0 - 3f);
        }

        [Test]
        public void Chaser_TelegraphThenStrike_DamagesPlayer()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(1.0f, 0f)); // уже в AttackRange
            float hp0 = c.Hero.MaxHp;
            // ждём удара с запасом (FSM может потратить тик-два на Idle→Chase→Telegraph)
            for (int i = 0; i < 40 && w.Player.Hp >= hp0; i++) w.Tick(Idle);
            // ровно один удар: AttackCooldown 0.9 c = 27 тиков — второй не успевает
            Assert.AreEqual(hp0 - c.Chaser.ContactDamage, w.Player.Hp, 1e-3f);
        }

        [Test]
        public void Chaser_BehindObstacle_SteersAroundNotStuck()
        {
            var c = TestConfigs.Open();
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(7f, 0f) };
            c.Arena.ObstacleRadius = new[] { 2f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(14f, 0f)); // игрок в (0,0) за препятствием
            for (int i = 0; i < 300; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), 3f); // дошёл в обход
        }

        [Test]
        public void Gunner_KeepsPreferredRange_AndFiresOnlyWithLoS()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Gunner, new float2(20f, 0f));
            int fired = 0;
            for (int i = 0; i < 300; i++)
            {
                w.ClearEvents();
                w.Tick(Idle);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired) fired++;
            }
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            float dist = math.distance(snap.Mobs[0].Pos, w.Player.Pos);
            Assert.That(dist, Is.InRange(c.Gunner.PreferredRange - 2f, c.Gunner.PreferredRange + 2f));
            Assert.Greater(fired, 0);
        }

        [Test]
        public void Gunner_NoLoS_HoldsFire()
        {
            var c = TestConfigs.Open();
            c.Gunner.StrafeSpeed = 0f; // изолируем LoS-гейт: страйф вывел бы из тени за ~60 тиков
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            c.Arena.ObstacleRadius = new[] { 3f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Gunner, new float2(9f, 0f)); // в допуске дистанции, но за стеной
            int fired = 0;
            for (int i = 0; i < 120; i++)
            {
                w.ClearEvents();
                w.Tick(Idle);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired) fired++;
            }
            Assert.AreEqual(0, fired);
        }

        [Test]
        public void PlayerDead_MobsGoIdle()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            w.KillPlayerForTest();
            for (int i = 0; i < 30; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(MobAiState.Idle, snap.Mobs[0].Ai);
        }
    }
```

  (+ тест-шов `internal void KillPlayerForTest()` — `DamagePlayer(maxHp + 1, Pos)`;
  тестам виден через `InternalsVisibleTo`.)
- [ ] **Step 2:** R-FILTER `MobAiTests` — RED.
- [ ] **Step 3 (GREEN):** `MobAiSystem.cs` по Interfaces (полные FSM обоих типов,
  `SteerAround` — приватный статик системы; движение — reuse `MoveWithCollisions`).
  Порядок тика теперь: игрок(движение+дэш) → оружие → **мобы** → снаряды → (волны).
- [ ] **Step 4:** R-TEST — PASS.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): FSM ChaserDrone/GunnerDrone — телеграф, обход, LoS-гейт, страйф`.

### Task 20: SeparationSystem — расталкивание без перекоса (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/AI/SeparationSystem.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs` (вызов после MobAi)
- Modify: `client/Assets/Tests/EditMode/MobAiTests.cs`

**Interfaces:**
- Produces: `internal static class SeparationSystem`: `static void Apply(SimulationWorld w)` —
  двойной буфер `float2[] _sepForces` (предаллоцирован на `MaxMobs` в мире): по всем
  парам мобов при перекрытии кругов `SeparationRadius` — сила
  `normalizesafe(d) * (1 − dist/threshold) * strength` симметрично в обе стороны;
  применение — **добавкой к скорости** (`Vel += force`), НЕ прямой записью в `Pos`
  мимо коллизий (второй путь перемещения дал бы туннелирование); движение мобов
  остаётся единственным — через `MoveWithCollisions` в MobAiSystem; страховка —
  `Geometry.Depenetrate(…, 1)` после применения.

- [ ] **Step 1 (RED):** тест:

```csharp
        [Test]
        public void Separation_PreventsStackingSymmetrically()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(11.9f, 10f));
            w.SpawnMobForTest(MobType.Chaser, new float2(12.1f, 10f));
            w.KillPlayerForTest(); // мобы Idle — работает только separation
            for (int i = 0; i < 60; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            float dist = math.distance(snap.Mobs[0].Pos, snap.Mobs[1].Pos);
            Assert.Greater(dist, 1.0f); // растолкались
            // симметрия: центр пары не уехал
            float2 mid = (snap.Mobs[0].Pos + snap.Mobs[1].Pos) * 0.5f;
            Assert.AreEqual(12f, mid.x, 0.05f);
        }
```

- [ ] **Step 2:** RED → **Step 3 (GREEN):** код по Interfaces → **Step 4:** R-TEST PASS.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): separation мобов двойным буфером`.

### Task 21: вьюхи мобов — телеграф и типы (Presentation)

**Files:**
- Modify: `client/Assets/Scripts/Presentation/MobView.cs`
- Modify: префабы вьюх в `client/Assets/Scenes/Main.unity`/`client/Assets/Prefabs/`

**Interfaces:**
- Produces: `MobView.Bind(in MobState m)` — цвет по `Type`; `Ai == Telegraph` →
  нарастающий эмиссив-пульс (читаемый замах — база уклонения, спека §3.6);
  Gunner в `Fire` — лёгкий «прицельный» блик.

- [ ] **Step 1:** Код + префабы. **Step 2:** PlayMode-смоук с `PracticeTargets` +
  временным спавном живых мобов (дев-кнопка). **Step 3:** R-COMMIT
  `feat(app-88s): вьюхи мобов — телеграф-пульс, типовые акценты`.

## Phase 7 — Волны, смерть, рестарт → веха-плейтест 3 (§3.6, §3.12; таск 7 из §4)

### Task 22: WaveSystem — детерминированный спавн с долгом (TDD)

**Files:**
- Create: `client/Assets/Scripts/Simulation/AI/WaveSystem.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (вызов последним в тике; `SpawnMob(MobType, float2)` — боевой, с капом `MaxMobs`
  → `MobSpawnsSkipped++`, долг волны при этом СОХРАНЯЕТСЯ (повтор не чаще раза
  в тик); `Emit(MobSpawned)`)
- Create: `client/Assets/Tests/EditMode/WaveTests.cs`

**Interfaces:**
- Produces: `internal static class WaveSystem`: `static void Update(SimulationWorld w)`:
  при `!Player.Alive` — не тикает. `Phase == Waiting`: `PhaseTimer -= dt`, по нулю —
  `StartWave` (`WaveIndex++`; `count = min(BaseCount + CountGrowth*(WaveIndex-1),
  MaxMobsPerWave)`; `gunners = (int)round(count * saturate(GunnerShareBase +
  GunnerShareGrowth*(WaveIndex-1)))`; `PendingChasers/Gunners`; `Emit(WaveStarted)`;
  `Phase = Active`). `Phase == Active`: отработка долга — для каждого pending
  (chasers, затем gunners) `TryFindSpawnPos` (до `MaxSpawnAttempts` углов из
  `w.Rng.NextFloat(0, 2π)` на радиусе `Arena.Radius - SpawnRingInset`; отбраковка:
  препятствия, живые мобы, `MinSpawnDistanceToPlayer`; фолбэк — `FallbackSlots`
  равномерных углов, первый валидный; неудача — долг остаётся); все заспавнены и
  `MobCount == 0` → `Stats.WavesCleared++`, `Emit(WaveCleared)`,
  `Phase = Waiting`, `PhaseTimer = WavePause`. Инициализация мира:
  `Phase = Waiting`, `PhaseTimer = FirstWaveDelay`.

- [ ] **Step 1 (RED):** `WaveTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WaveTests
    {
        [Test]
        public void FirstWave_SpawnsAfterDelay_WithBaseCount()
        {
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(c.Wave.BaseCount, snap.MobCount);
            Assert.AreEqual(1, snap.Wave.WaveIndex);
        }

        [Test]
        public void SpawnPositions_RespectRules()
        {
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c);
            // снапшот сразу после спавна первой волны — мобы ещё не успели сместиться
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0);
            for (int m = 0; m < snap.MobCount; m++)
            {
                float2 pos = snap.Mobs[m].Pos;
                Assert.Greater(math.distance(pos, w.Player.Pos),
                    c.Wave.MinSpawnDistanceToPlayer - 1f); // минус ≤2 тика движения
                for (int o = 0; o < c.Arena.ObstacleCount; o++)
                    Assert.IsFalse(Geometry.CircleOverlap(pos, 0.4f,
                        c.Arena.ObstaclePos[o], c.Arena.ObstacleRadius[o]));
            }
        }

        [Test]
        public void SameSeed_SameWaveComposition()
        {
            ulong Run(long seed)
            {
                var w = new SimulationWorld(seed, TestConfigs.Default());
                for (int i = 0; i < 400; i++) w.Tick(default);
                return w.StateHash();
            }
            Assert.AreEqual(Run(77), Run(77));
            Assert.AreNotEqual(Run(77), Run(78));
        }

        [Test]
        public void FullyBlockedRing_NoHang_DebtCarriesOver()
        {
            var c = TestConfigs.Open();
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.MinSpawnDistanceToPlayer = 100f; // валидных точек нет вовсе
            var w = new SimulationWorld(11, c);
            for (int i = 0; i < 60; i++) w.Tick(default); // не виснет — уже успех
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(0, snap.MobCount);
            Assert.Greater(snap.Wave.PendingChasers + snap.Wave.PendingGunners, 0);
            // долг отрабатывается, когда условия позволяют (спека §3.13 п.5)
            var relaxed = c;
            relaxed.Wave.MinSpawnDistanceToPlayer = 8f;
            w.ApplyConfig(relaxed);
            for (int i = 0; i < 60; i++) w.Tick(default);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0);
            Assert.AreEqual(0, snap.Wave.PendingChasers + snap.Wave.PendingGunners);
        }

        [Test]
        public void WaveComposition_FollowsGunnerShare()
        {
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            int gunners = 0;
            for (int m = 0; m < snap.MobCount; m++)
                if (snap.Mobs[m].Type == MobType.Gunner) gunners++;
            // волна 1: count = BaseCount = 4; gunners = round(4 × 0.2) = 1
            Assert.AreEqual(c.Wave.BaseCount, snap.MobCount);
            Assert.AreEqual(1, gunners);
        }

        [Test]
        public void MobCap_SkipsSpawnsDeterministically()
        {
            var c = TestConfigs.Default();
            c.Arena.MaxMobs = 2;
            c.Wave.BaseCount = 6;
            var w = new SimulationWorld(11, c);
            for (int i = 0; i < 200; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.LessOrEqual(snap.MobCount, 2);
            Assert.Greater(w.Stats.MobSpawnsSkipped, 0);
            static ulong Run(SimConfig cc)
            {
                var ww = new SimulationWorld(11, cc);
                for (int i = 0; i < 200; i++) ww.Tick(default);
                return ww.StateHash();
            }
            Assert.AreEqual(Run(c), Run(c)); // деградация по капу детерминирована
        }
    }
}
```

- [ ] **Step 2:** R-FILTER `WaveTests` — RED.
- [ ] **Step 3 (GREEN):** `WaveSystem.cs` + `SpawnMob` по Interfaces.
- [ ] **Step 4:** R-TEST — PASS (детерминизм всего мира с волнами).
- [ ] **Step 5:** R-COMMIT `feat(app-88s): волны от seed — долг спавна, фолбэк-сетка, капы`.

### Task 23: смерть игрока — семантика тика смерти (TDD)

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
- Create: `client/Assets/Tests/EditMode/DeathTests.cs`

**Interfaces:**
- Produces: после `Alive = false` (спека §3.12): тики продолжаются; ввод игрока
  игнорируется (движение/оружие/дэш не тикаются, `Vel` затухает трением);
  `DamagePlayer` — no-op; статы заморожены (все инкременты — через приватные
  хелперы мира с guard'ом `if (!_players[0].Alive && kind != …) return`; kills от
  долетающих снарядов после смерти не начисляются); `WaveSystem` не тикает (Task 22).
  `PlayerDied` эмитится ровно один раз.

- [ ] **Step 1 (RED):** `DeathTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class DeathTests
    {
        static SimulationWorld DeadWorld(out SimConfig c)
        {
            c = TestConfigs.Open();
            var w = new SimulationWorld(2, c);
            w.Tick(default);
            w.KillPlayerForTest();
            return w;
        }

        [Test]
        public void PlayerDied_EmittedOnce_DeathTickRecorded()
        {
            var w = DeadWorld(out _);
            int died = 0;
            for (int e = 0; e < w.EventCount; e++)
                if (w.GetEvent(e).Kind == SimEventKind.PlayerDied) died++;
            Assert.AreEqual(1, died);
            Assert.AreEqual(1, w.Stats.DeathTick);
            w.KillPlayerForTest(); // повторный урон по мёртвому
            Assert.AreEqual(1, w.Stats.DeathTick);
        }

        [Test]
        public void DeadPlayer_IgnoresInput_WorldKeepsTicking()
        {
            var w = DeadWorld(out _);
            float2 pos = w.Player.Pos;
            int t0 = w.CurrentTick;
            for (int i = 0; i < 30; i++)
                w.Tick(new SimInput { MoveDir = new float2(1f, 0f), FireHeld = true,
                                      DashRequested = true });
            Assert.AreEqual(t0 + 30, w.CurrentTick);
            Assert.AreEqual(pos, w.Player.Pos);
            Assert.AreEqual(0, w.Stats.ShotsFired);
            Assert.AreEqual(0, w.Stats.DashesUsed);
        }

        [Test]
        public void StatsFrozen_ProjectileKillAfterDeath_NotCounted()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(2, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(3f, 0f),
                new float2(35f, 0f), 100f, 0.12f, 2f);
            w.KillPlayerForTest();
            for (int i = 0; i < 10; i++) w.Tick(default); // снаряд долетает и убивает
            Assert.AreEqual(0, w.Stats.Kills); // в счёт захода не идёт
        }
    }
}
```

- [ ] **Step 2:** RED → **Step 3 (GREEN):** guard'ы в мире по Interfaces →
  **Step 4:** R-TEST PASS.
- [ ] **Step 5:** R-COMMIT `feat(app-88s): семантика смерти — мир доигрывает, статы заморожены`.

### Task 24: рестарт, оверлей смерти, пауза, дев-оверлей → веха-плейтест 3

**Files:**
- Create: `client/Assets/Scripts/Presentation/DeathOverlayController.cs`
- Create: `client/Assets/Scripts/Presentation/PauseController.cs`
- Create: `client/Assets/Scripts/Presentation/DevOverlay.cs`
- Modify: `client/Assets/Scripts/Presentation/SimulationRunner.cs`,
  `ViewRegistry.cs`, `MuzzleFlashAndSfx.cs` (подписка `WorldRestarted` — очистка)
- Modify: `client/Assets/Scenes/Main.unity` (панели)
- Modify: `client/Assets/Scripts/Presentation/PracticeTargets.cs` — УДАЛИТЬ
  (болванки вехи 2 больше не нужны — волны боевые)

**Interfaces:**
- Produces: `DeathOverlayController`: по событию `PlayerDied` → показать панель
  через `GameFeelDirector.ForceEndHitstop()` (Task 25; до него — сразу); подписи
  по словарю мира: «Заход №», «Утилизировано», «Циклов волн», «Время на объекте»,
  «Точность», «Дэшей», «Урона получено», «seed» (+ пометка «прогон с правками»
  при `ConfigTweaked`); клавиши активны через 0.5 с: `R` — `RestartNewSeed()`,
  `Shift+R` — `Restart(Seed)` (тот же), UI-кнопка = `UI/Submit`.
- `PauseController`: Escape → `Runner.Paused = true` (Update раннера: при паузе
  `_acc.Reset()` и не тикает; `Time.timeScale` НЕ трогается); меню
  продолжить/рестарт/выход (`Application.Quit`).
- `DevOverlay` (уголок): fps, `CurrentTick`, mobs/projectiles counts,
  `DroppedEvents`, `Stats.MobSpawnsSkipped`/`ProjectileSpawnsSkipped`, seed, поле «принудительный seed» +
  кнопка Restart. Счётчик дропов > 0 подсвечивается красным (спека §3.7 —
  тихая потеря запрещена).
- `SimulationRunner.WorldRestarted` — все вьюхи/пулы/директоры чистятся
  (`ViewRegistry.Clear()`, звук `StopAll`, буферы событий дренированы).

- [ ] **Step 1:** Код всех контроллеров + панели в сцене + подписки очистки.
- [ ] **Step 2:** R-COMPILE; PlayMode-смоук: заход → волны давят → смерть →
  оверлей с метриками → R рестартит начисто (счётчики в DevOverlay обнулились),
  Shift+R воспроизводит тот же заход (одинаковый первый спавн), Escape — пауза.
- [ ] **Step 3:** R-TEST — зелёные; R-COMMIT
  `feat(app-88s): смерть-оверлей с метриками, рестарт new/same seed, пауза, дев-оверлей`.
- [ ] **Step 4:** **ВЕХА-ПЛЕЙТЕСТ 3 (владелец): мобы+волны.** Hot-tweak
  Mob×2/Wave; фидбек → bd note; числа → SO, `chore(app-88s): числа веха-3`.

## Phase 8 — Game-feel-пасс → веха-плейтест 4 = DoD (§3.11, §3.15; таск 8 из §4)

### Task 25: GameFeelDirector — hitstop и вспышка цели

**Files:**
- Create: `client/Assets/Scripts/Presentation/GameFeelDirector.cs`
- Modify: `client/Assets/Scripts/Presentation/ViewRegistry.cs`, `PlayerView.cs`,
  `MobView.cs` (учёт `HitstopActive` — интерполяция замирает), `SimulationRunner.cs`
  (директор дренит события до `ClearEvents`)

**Interfaces:**
- Produces: `GameFeelDirector` (MonoBehaviour, поле `GameFeelConfig`):
  `bool HitstopActive`; `void ForceEndHitstop()`; на `TicksFlushed`:
  `ProjectileHit` → `TriggerHitstop(HitstopSeconds)` (таймер переустанавливается,
  НЕ суммируется) + `MobView.Flash(FlashDuration)` цели (по `EntityId`) +
  `AddTrauma(TraumaHit)`; `MobDied` → `AddTrauma(TraumaDeath)`; `PlayerDamaged` →
  `AddTrauma(TraumaPlayerHit)` + винетка (UI Image alpha-пульс); `PlayerDied` →
  `ForceEndHitstop()`. Бюджет: скользящее окно 1 с, суммарный hitstop ≤
  `MaxHitstopRatio` — сверх лимита триггер игнорируется. Скоуп `HitstopScope`
  (enum в `GameFeelConfig`: TargetOnly | FullFrame): TargetOnly — замирает только
  вьюха цели; FullFrame — вся интерполяция (`ViewRegistry`+`PlayerView`+камера).
  Таймер тикает `Time.unscaledDeltaTime`; симуляция не останавливается (§3.2/§3.11).

- [ ] **Step 1:** Код директора + правки вьюх (`if (feel.HitstopActive &&
  scope==FullFrame) return;` в `LateUpdate` — держим прошлый кадр).
- [ ] **Step 2:** PlayMode-смоук: попадание ощутимо «застывает» кадр на 40 мс,
  звук/тряска живут; hold-огонь не слайд-шоу (бюджет работает).
- [ ] **Step 3:** R-COMMIT `feat(app-88s): hitstop Presentation-only с бюджетом, вспышка цели, винетка`.

### Task 26: тряска trauma² + честный прицел с конусом отдачи

**Files:**
- Modify: `client/Assets/Scripts/Presentation/GameFeelDirector.cs` (trauma-модель)
- Modify: `client/Assets/Scripts/Presentation/CameraRig.cs` (приём смещения)
- Create: `client/Assets/Scripts/Presentation/CrosshairView.cs`
- Modify: `client/Assets/Scenes/Main.unity` (прицел-маркер; `Cursor.visible=false`)

**Interfaces:**
- Produces: `GameFeelDirector.ShakeOffset` (Vector3): `trauma` копится событиями
  (кламп 1), гаснет `TraumaDecayPerSec`; смещение = `ShakeAmplitude * trauma² *
  (perlin(t*Freq), perlin(t*Freq+17))` (`Mathf.PerlinNoise`, unscaled-время).
  `CameraRig`: `position += ShakeOffset`.
- `CrosshairView`: маркер в мировой точке `Curr.Player.AimPoint` (per-frame из
  `AimProvider` — без тик-квантования); кольцо-конус радиусом
  `tan(SpreadRad + Curr.Player.RecoilOffset) * дистанция` — игрок видит фактический
  разброс (спека §3.5/§3.11); системный курсор скрыт.

- [ ] **Step 1:** Код + сцена. **Step 2:** PlayMode-смоук: тряска читается, не
  укачивает (кламп); конус дышит от огня. **Step 3:** R-COMMIT
  `feat(app-88s): trauma-тряска, прицел с конусом фактического разброса`.

### Task 27: персистентная косметика — гильзы, декали, трупы, партикли, звук-лимиты

**Files:**
- Create: `client/Assets/Scripts/Presentation/PersistentPropsDirector.cs`
- Create: `client/Assets/Scripts/Presentation/AudioDirector.cs`
  (замена `MuzzleFlashAndSfx` — переименовать и расширить)
- Create: `client/Assets/Prefabs/Casing.prefab`, `Decal.prefab`, `Corpse*.prefab`,
  партикль-префабы (muzzle, hit-spark, block-spark, death-burst)
- Modify: Renderer-ассеты `client/Assets/Settings/PC_Renderer.asset`,
  `Mobile_Renderer.asset` (добавить **Decal Renderer Feature** — через Editor)
- Modify: `client/Assets/Scenes/Main.unity`

**Interfaces:**
- Produces: `PersistentPropsDirector` (поля из `GameFeelConfig`):
  - гильзы: пул `MaxCasings=1024`, FIFO; спавн на `ProjectileFired` (PhysX rigidbody,
    случайный импульс — `UnityEngine.Random`); через `CasingPhysicsSeconds=1.5` —
    `isKinematic=true` + слой без взаимных коллизий; живут до конца захода;
  - декали: кольцевой буфер `MaxDecals=512` `DecalProjector` на `ProjectileBlocked`
    (позиция события, нормаль — от центра препятствия);
  - трупы: пул `MaxCorpses=64` на `MobDied` — замороженная копия вьюхи (лечь на бок,
    эмиссив гаснет), FIFO;
  - партикли: пулы систем — muzzle (`ProjectileFired`), искры (`ProjectileHit`),
    блок-искры (`ProjectileBlocked`), взрыв (`MobDied`);
  - `Clear()` на `WorldRestarted` — всё в пулы (спека §3.12).
- `AudioDirector`: пул из 16 `AudioSource`; на тип SFX — кап `VoicesPerSfx=6`
  одновременных + `MinSfxInterval=0.03 c` (анти-фазинг); питч `1±PitchRange`;
  `StopAll()` на рестарте.

- [ ] **Step 1:** Editor-шаг: Decal Renderer Feature в оба Renderer-ассета
  (доки URP 17 curl'ом — точное имя фичи `DecalRendererFeature`); префабы.
- [ ] **Step 2:** Код директоров (полный), подписки.
- [ ] **Step 3:** PlayMode-смоук 5+ минут hold-огня: гильзы устилают пол и НЕ
  исчезают (до FIFO-лимита), декали на препятствиях, трупы лежат, fps стабилен
  (Stats-панель Unity); DevOverlay — счётчики упираются в капы, не растут.
- [ ] **Step 4:** R-TEST + R-COMPILE; R-COMMIT
  `feat(app-88s): персистентная косметика с FIFO-пулами, партикли, звук-лимиты`.

### Task 28: hot-tweak по OnValidate + мгновенный косметический выстрел → DoD-итерации

**Files:**
- Modify: `client/Assets/Scripts/Data/*.cs` (OnValidate → событие)
- Create: `client/Assets/Scripts/Data/RingDataChanged.cs`
- Modify: `client/Assets/Scripts/Presentation/SimulationRunner.cs`,
  `AudioDirector.cs`/`GameFeelDirector.cs` (опция `ImmediateMuzzleFeedback`)

**Interfaces:**
- Produces: `Ring.Data.RingDataChanged`: `static event System.Action Changed;
  static void Raise()`; в каждом SO:
  `#if UNITY_EDITOR void OnValidate() => RingDataChanged.Raise(); #endif`.
  Раннер: подписка → `RequestApplyConfig()` (Simulation-конфиги; исключение
  топологии арены → `Restart(Seed)` + лог) — Presentation-конфиги (`GameFeel`/
  `Camera`) читаются каждый кадр напрямую, событие не нужно. Весь путь —
  `#if UNITY_EDITOR || DEVELOPMENT_BUILD` (спека §3.9).
- `ImmediateMuzzleFeedback` (bool в `GameFeelConfig`, дефолт true): вспышка+звук
  выстрела играются в кадре нажатия (по `SampleFrame().FireHeld` при готовом
  кулдауне — эвристика кадра), авторитетный снаряд — событием тика (спека §3.11).

- [ ] **Step 1:** Код. **Step 2:** PlayMode-смоук: правка `WeaponConfig.FireInterval`
  в инспекторе на лету меняет темп без перезапуска; оверлей помечает прогон.
- [ ] **Step 3:** R-TEST — зелёные (HotTweakTests уже покрывают миграцию);
  R-COMMIT `feat(app-88s): hot-tweak через OnValidate, мгновенный косметический отклик выстрела`.
- [ ] **Step 4:** **ВЕХА-ПЛЕЙТЕСТ 4 = DoD (владелец): «5 минут стрелять приятно»**,
  Editor PlayMode. Итерации: числа Weapon/GameFeel/Mob/Wave hot-tweak'ом, фидбек →
  bd note, каждая итерация чисел — `chore(app-88s): DoD-итерация N`. Не приятно —
  возвращаемся к механикам (новые bd-таски через `discovered-from`), Этап 2 не
  начинается. По вердикту «приятно» — контрольный смоук-заход в Linux-клиенте
  (R-BUILD-LinuxClient → запуск бинарника).

## Phase 9 — Финализация (§3.14; таск 9 из §4)

### Task 29: финальные тесты детерминизма — скриптованный ввод, golden, аллокации (TDD)

**Files:**
- Modify: `client/Assets/Tests/EditMode/DeterminismTests.cs`
- Create: `client/Assets/Tests/EditMode/AllocationTests.cs`

**Interfaces:**
- Produces: скриптованный ввод — генератор в тестах (отдельный `Random` в тестовом
  коде разрешён — это не Simulation):

```csharp
        static SimInput Scripted(ref Unity.Mathematics.Random rng)
        {
            return new SimInput
            {
                MoveDir = rng.NextFloat2Direction() * rng.NextFloat(),
                AimPoint = rng.NextFloat2(new float2(-30f, -30f), new float2(30f, 30f)),
                FireHeld = rng.NextFloat() < 0.7f,
                DashRequested = rng.NextFloat() < 0.05f
            };
        }
```

- [ ] **Step 1 (RED/GREEN):** тесты:
  - `ScriptedRun_SameSeed_SameHash`: мир `TestConfigs.Default()` (волны живые),
    ввод от `Random(123)`, 1000 тиков ×2 прогона → хеши равны; seed 43 → другой хеш.
  - `GoldenHash_ScriptedScenario`: прогон seed 42/input 123/1000 тиков; первый запуск —
    вывести хеш в лог assert'ом `Assert.AreEqual(0UL, hash)` (упадёт, показав
    значение), вписать фактическое значение константой, перезапустить — PASS.
    Пин против молчаливой смены симуляции (спека §3.13 п.14).
  - `AllocationTests.Tick_DoesNotAllocateGC`: насыщенный мир (спавн мобов до капа
    тест-швом, hold-огонь 100 тиков разгона), затем
    `Assert.That(() => { for (int i = 0; i < 1000; i++) w.Tick(input); },
    Is.Not.AllocatingGCMemory());`
    Обязательные using'и файла (`AllocatingGCMemory` — extension-метод, полная
    квалификация НЕ компилируется, CS1061):
    `using UnityEngine.TestTools.Constraints;`
    `using Is = UnityEngine.TestTools.Constraints.Is;`
- [ ] **Step 2:** R-TEST — все зелёные; найденные аллокации чинить (предаллокация),
  не ослаблять тест.
- [ ] **Step 3:** R-COMMIT `test(app-88s): golden-хеш, скриптованный детерминизм, ноль GC-аллокаций`.

### Task 30: (перенесён в Task 1 по self-review плана)

Amendment A7 и правки CODEOWNERS выполняются в Task 1: репо не должно жить весь
этап со слоем вне ADR-002 §3 и незащищённым `Scripts/Data/`. Отдельного таска нет.

### Task 31: верификация, PR, закрытие

- [ ] **Step 1:** Полный свежий прогон: R-TEST (exit 0, счётчик тестов в xml) +
  R-BUILD-LinuxServer + R-BUILD-WindowsClient + R-BUILD-LinuxClient (все EXIT=0,
  артефакты `file`-чеком) + `git status --short` пуст (батчинг-фикс ze1 работает).
- [ ] **Step 2:** `superpowers:verification-before-completion` — свод evidence в
  bd notes эпика (вывод команд).
- [ ] **Step 3:** `superpowers:finishing-a-development-branch`:
  `git push -u origin feature/app-88s-stage1-solo-combat` → `gh pr create` (тело:
  скоуп, DoD-вердикт владельца, evidence) → ревью-субагент по diff →
  `gh pr merge --squash --delete-branch`.
- [ ] **Step 4:** `bd close app-88s --reason "<evidence>"`; jsonl-дрифт —
  chore-коммит в main; уборка worktree (`git worktree remove` после merge);
  handoff — по команде владельца.

---

## Порядок и вехи (сводно)

Phase 1 (T1–7) → Phase 2 (T8–9) → Phase 3 (T10) → Phase 4 (T11–14, **веха 1**) →
Phase 5 (T15–17, **веха 2**) → Phase 6 (T18–21) → Phase 7 (T22–24, **веха 3**) →
Phase 8 (T25–28, **веха 4 = DoD**) → Phase 9 (T29–31).
Каждая веха — короткая PlayMode-сессия владельца, фидбек в bd note эпика,
итерации чисел — hot-tweak + chore-коммиты SO.

## Соответствие спеке (самопроверка покрытия; сверено self-review субагентом D)

§3.1→T1; §3.2→T4 (включая обязательный тест защёлки `SimInputFrame`),T7;
§3.3→T2,T3,T5 (включая тест «каждое поле в хеше»),T29 + трасса tick→hash — П-9;
§3.4→T8,T9,T10; §3.5→T15,T16 (включая негативы матрицы поражения); §3.6→T18–22;
§3.7→T5,T17 (+router П-1); §3.8→T3(санитизация),T11 (+минимальный прицел в T12 —
П-6); §3.9→T6 (+тест полноты маппинга П-4),T7,T28; §3.10→T12,T13,T14;
§3.11→T17,T25,T26,T27,T28 (+параметры П-7); §3.12→T23,T24 (5 рестартов — П-8);
§3.13: пп.1–4→T16/T18, п.5→T22, п.6→T3, п.7→T5/T15/T22, пп.8–9→T5, п.10→T29,
п.11→T16, п.12→T5, п.13–14→T29; §3.14→T1(A7/CODEOWNERS)+T31;
§3.15→T5,T15,T22,T27 + 20-минутный прогон в T31 (П-10).

---

## Приложение П — правки self-review, интегрируемые при исполнении

Обязательные точечные правки, не вынесенные в тела тасков (оркестратор передаёт
релевантные пункты вместе с текстом таска исполнителю; «Task N» = куда применять).

- **П-1 (T17, архитектура событий).** Единственный подписчик `TicksFlushed` —
  новый `SimEventRouter` (Create в T17): один проход по буферу, фан-аут строго в
  порядке `GameFeelDirector → PersistentPropsDirector → AudioDirector →
  ViewRegistry (ретайр вьюх) → DeathOverlayController`; прямые подписки остальных
  классов на `TicksFlushed` запрещены. Владелец жизненного цикла `MobView` — только
  `ViewRegistry`; труп в T27 — собственный префаб `PersistentPropsDirector`,
  вьюха моба ему не передаётся.
- **П-2 (T17, звук).** Класс сразу называется `AudioDirector` (минимальная версия:
  пул 8 источников, питч), файла `MuzzleFlashAndSfx.cs` НЕ существует; T27 только
  расширяет `AudioDirector` (лимит голосов, `MinSfxInterval`, `StopAll`);
  правка Files T17/T24/T27 соответственно.
- **П-3 (T12).** Создать `SimSpace` (`static Vector3 ToWorld(float2)` /
  `static float2 ToSim(Vector3)`) — ЕДИНСТВЕННАЯ точка маппинга сим↔мир; все
  вьюхи/камера/прицел/пропсы используют только его. Одновременно: единственный
  per-frame источник прицела — `AimProvider.CurrentAimSimPos` (его читают
  `PlayerView` и `CrosshairView`); из снапшота берётся только `RecoilOffset`.
  Плюс минимальный прицел-маркер (без конуса) переносится в T12 — вехи 1–3 не
  играются с системным курсором; конус отдачи остаётся в T26.
- **П-4 (T6).** Полные поля `GameFeelConfig`: `HitstopSeconds=0.04`,
  `HitstopScope (enum TargetOnly|FullFrame) = FullFrame`, `MaxHitstopRatio=0.35`,
  `HitstopCatchUpSeconds=0.05`, `FlashDuration=0.08`, `TraumaHit=0.2`,
  `TraumaDeath=0.35`, `TraumaPlayerHit=0.45`, `TraumaDecayPerSec=1.2`,
  `ShakeAmplitude=0.35`, `ShakeFrequency=22`, `PitchRange=0.12`,
  `TracerFadeSeconds=0.4`, `CasingPhysicsSeconds=1.5`, `MaxCasings=1024`,
  `MaxDecals=512`, `MaxCorpses=64`, `VoicesPerSfx=6`, `MinSfxInterval=0.03`,
  `ImmediateMuzzleFeedback=true`, `ExtrapolateLocalPlayer=false`;
  `CameraConfig`: `PitchDeg=55 [50..60]`, `Distance=18`, `LookAhead=0.25`,
  `Damp=0.15`. Литералы этих величин в T12/T25–T28 заменяются чтением SO.
  Дополнительный тест `ConfigTests.Build_DefaultAssets_MatchesTestConfigsBaseline`:
  поэлементное сравнение `SimConfigBuilder.Build(дефолтные SO)` с
  `TestConfigs.Default()` (полнота маппинга §3.13; ловит дрейф двух источников
  баланса; отдельный ассерт — chaser-ассет попадает в `cfg.Chaser`, gunner — в
  `cfg.Gunner`). `ArenaConfig`: вложенный `[System.Serializable] public struct
  Obstacle { public Vector2 Pos; public float Radius; }`, поле
  `public Obstacle[] Obstacles = {…дефолты…}` с инициализатором; литерал зазора
  спавна в валидации → поле `SpawnClearance` (`[Range(0.5f,5f)] = 1f`).
- **П-5 (T7).** Заглушка `InputSampler` — безаргументный конструктор, поле
  `_sampler` инициализируется в `Awake`; T11 меняет конструктор на
  `(InputActionAsset, AimProvider)` и правит `Awake`. В `SampleFrame`
  `DashRequested = _dashLatch` (без дублирующего `WasPressedThisFrame` — защёлка
  через `performed` уже ловит нажатие внутри кадра); `FireHeld = IsPressed() ||
  WasPressedThisFrame()` (вторая часть — тап короче кадра, оставить с
  комментарием); `Disable()` реализуется и отписывает `performed`-хендлер;
  `Mouse.current.position` читается `.ReadValue()`.
- **П-6 (T24).** Заголовок оверлея — строка словаря «Носитель потерян»;
  подпись «Циклов волн» → «Волн отражено»; «Заход №» → «Заход» (номер не
  считаем). Клавиши R/Shift+R/Escape — прямой опрос `Keyboard.current` только в
  дев-контроллерах, под `#if UNITY_EDITOR || DEVELOPMENT_BUILD` (зафиксированное
  исключение из §3.8; UI-кнопка — через `UI/Submit`). DevOverlay дополнительно
  показывает `FixedStepAccumulator.DroppedTime` и раздельные
  `MobSpawnsSkipped`/`ProjectileSpawnsSkipped`.
- **П-7 (T25/T27).** Hitstop — одна точка: `SimulationRunner.RenderAlpha`
  (= `Alpha`, но замораживается директором) — вьюхи читают только его, локальные
  `if (HitstopActive)` в трёх классах не пишутся. Параметры «догона» после
  hitstop (`HitstopCatchUpSeconds`) и `ExtrapolateLocalPlayer` — из
  `GameFeelConfig` (П-4). Пулы: обычные «взять/вернуть» —
  `UnityEngine.Pool.ObjectPool<T>`; свой код — только один общий FIFO-буфер
  `RingBuffer<T>` для гильз/декалей/трупов (не три копии).
- **П-8 (T24).** Критерий смоука: **5 рестартов подряд**, после каждого счётчики
  DevOverlay и активные звуки возвращаются к базовым; память не растёт
  (Profiler). `GreyboxBuilder` подписывается на `WorldRestarted` и перестраивает
  грейбокс (смена топологии арены = рестарт — спека §3.9).
- **П-9 (T24).** DevOverlay показывает текущий `StateHash()` (hex) + тумблер
  «лог tick→hash в файл» под `#if UNITY_EDITOR || DEVELOPMENT_BUILD`
  (спека §3.3, отладка расхождений).
- **П-10 (T31).** Перед PR — 20-минутный прогон с непрерывным огнём (спека §3.15):
  fps не ниже цели, счётчики упёрлись в лимиты и не растут,
  `Profiler.GetTotalAllocatedMemoryLong()` в начале/конце — в evidence bd.
- **П-11 (T21/T25/T26).** Перед каждым R-COMMIT этих тасков — минимум R-COMPILE
  (PlayMode-смоук остаётся основным критерием). Слепленные чекбоксы
  «RED → GREEN → PASS» в T20/T21/T23/T26 исполняются как ОТДЕЛЬНЫЕ TDD-шаги
  (verify FAIL и verify PASS — обязательные прогоны, галочка одна — шагов пять).
  «Дев-кнопка» спавна в T21 — кнопка `DevOverlay`-заготовки, зовущая
  `World.DevSpawnMob` (доступна с T17).
- **П-12 (T7).** `HotTweakTests` дополнить: (а) кулдауны/таймеры после
  `ApplyConfig` в `[0, новый максимум]` (уменьшить `DashCooldown` в конфиге при
  активном кулдауне); (б) `WaveIndex` сохраняется (мир с начавшейся волной —
  через `Default()` + 100 тиков). Тест порядка событий: два `Emit` подряд →
  `GetEvent(0/1)` в порядке эмиссии (T5).
- **П-13 (T29).** Хелперы `TestWorlds.Saturated(out SimConfig)` (насыщенный мир
  для alloc/golden) и `TestEvents.CountOf(w, kind)` — вместо копий setup-кода
  по тестовым файлам; `WorldSave`/`CaptureSnapshot`/`RestoreState` используют
  общий приватный `CopyEntities` (T5).
- **П-14 (T31).** Тело PR завершается строкой
  `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **П-15 (общее).** Пороговые числа в новых тестах по возможности выводить из
  `cfg.*`/`TestConfigs`, а не литералами (существующие литералы тестов из тел
  тасков допустимы — они синхронны с `TestConfigs`, который на вехах НЕ правится:
  вехи правят SO-ассеты; расхождение ловит тест `MatchesTestConfigsBaseline` П-4).
