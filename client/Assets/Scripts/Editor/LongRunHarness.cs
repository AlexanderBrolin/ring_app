using System;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Ring.Editor
{
    /// Task 31 (П-10) dev tool: a real 20-minute PlayMode session can't run headless
    /// in batchmode (no render/no input device), so this substitutes a long,
    /// scripted-input SIMULATION-only run to check the thing П-10 actually cares
    /// about — that Arena caps (MobCount/ProjectileCount/events) stabilize under
    /// load instead of growing unboundedly, and that managed memory doesn't climb
    /// monotonically once past warm-up. The fps/render side of П-10 is covered
    /// separately by the owner's PlayMode playtests (milestone 4 + Linux-client
    /// smoke) — this harness has no opinion on frame time.
    ///
    /// Builds its SimConfig from the actual battle SO assets in Assets/Data (NOT
    /// TestConfigs) on purpose: the point is to exercise whatever numbers are
    /// currently tuned in the project, the same source SimulationRunner itself
    /// reads at play time, so a hot-tweak drift would show up here too.
    ///
    /// Immortal bot, deliberately (Task 31 review round): the T29 Scripted() input
    /// is a random walk with no evasion, so at real balance numbers the bot died
    /// a couple of minutes in — once dead, WeaponSystem stops (spec §3.12) and
    /// surviving mobs go idle, so ~94% of a first-pass 36000-tick run sat on a
    /// frozen world instead of testing anything. This harness's whole point is a
    /// cap/memory stress test, not a balance/survivability check, so after
    /// building the config from the real assets, `Hero.MaxHp` is overwritten to
    /// 1e9f (effectively unkillable) purely for THIS run — every other number
    /// (movement, weapon, mob, wave, arena) stays exactly what's tuned in
    /// Assets/Data. That keeps the bot fighting continuously for the full 20
    /// minutes so waves actually ramp and caps get exercised, without touching
    /// balance assets or Simulation. StateHash determinism is unaffected — the
    /// override happens on the plain SimConfig struct before SimulationWorld
    /// construction, so it's just another (fixed) config value.
    public static class LongRunHarness
    {
        const string DataDir = "Assets/Data";
        const int TotalTicks = 36000; // 20 min @ 30 Hz (spec §3.15 / П-10).
        const int LogInterval = 3600; // every 2 sim-minutes.
        const long WorldSeed = 42;
        const uint InputSeed = 20260802u;

        [MenuItem("Ring/Dev/Long Run (36000 ticks, П-10)")]
        public static void Run36000()
        {
            SimConfig cfg = BuildBattleConfig();
            var world = new SimulationWorld(WorldSeed, in cfg);
            var snapshot = new RenderSnapshot(in cfg);
            var rng = new Unity.Mathematics.Random(InputSeed);

            Debug.Log($"LongRunHarness: starting {TotalTicks}-tick run " +
                $"(20 min @ 30 Hz, worldSeed={WorldSeed}, inputSeed={InputSeed}, " +
                $"Hero.MaxHp overridden to {cfg.Hero.MaxHp} — immortal bot, cap/memory " +
                $"stress only, see class doc).");
            // PlayerAlive/PlayerHp/Kills/WaveIndex are extra columns beyond the
            // brief's minimum set — kept even with the immortal-bot override so a
            // regression in the override (or a future change to it) stays visible
            // in the log instead of silently reverting to the frozen-world case.
            Debug.Log("LongRunHarness,tick,MobCount,ProjectileCount,EventCount," +
                "DroppedEvents,MobSpawnsSkipped,ProjectileSpawnsSkipped,GCTotalMemory," +
                "PlayerAlive,PlayerHp,Kills,WaveIndex");

            for (int i = 1; i <= TotalTicks; i++)
            {
                world.Tick(Scripted(ref rng));

                if (i % LogInterval == 0)
                {
                    world.CaptureSnapshot(snapshot);
                    // Stage 2 Task 5: Kills is personal (world.Stats == StatsAt(0),
                    // the harness's single bot); MobSpawnsSkipped/ProjectileSpawnsSkipped
                    // moved to WorldStats (world-scoped, shared arena caps).
                    MatchStats stats = world.Stats;
                    WorldStats worldStats = world.WorldStats;
                    long mem = GC.GetTotalMemory(false);
                    Debug.Log($"LongRunHarness,{i},{snapshot.MobCount},{snapshot.ProjectileCount}," +
                        $"{world.EventCount},{world.DroppedEvents}," +
                        $"{worldStats.MobSpawnsSkipped},{worldStats.ProjectileSpawnsSkipped},{mem}," +
                        $"{snapshot.Player.Alive},{snapshot.Player.Hp},{stats.Kills},{snapshot.Wave.WaveIndex}");
                }

                // Mirrors the per-frame event-buffer flush SimulationRunner drives
                // through its backend (ISimBackend.EndFrame since Task 43) cadence
                // (one flush per render frame; at 30 Hz with no catch-up debt that's
                // one flush per tick) — without this, the preallocated event buffer
                // fills up within the first few ticks and DroppedEvents free-runs
                // for the rest of the 36000 ticks, which would be an artifact of
                // this harness never consuming events, not a real simulation signal.
                world.ClearEvents();
            }

            Debug.Log($"LongRunHarness: completed {TotalTicks} ticks, " +
                $"final StateHash=0x{world.StateHash():X16}.");
        }

        /// Same scripted-input shape as DeterminismTests.Scripted (Task 29) — drives
        /// movement, aiming, firing and dashing together instead of idle replay, so
        /// mobs get engaged/killed/respawned across waves instead of the player
        /// standing still. No evasion logic, by design (matches T29 exactly) — the
        /// bot still doesn't dodge, it just can't die (see the class doc's
        /// "Immortal bot" note) — so it keeps fighting for the full 20 minutes.
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

        /// See the class doc's "Immortal bot" note: every number here comes from the
        /// real battle SO assets EXCEPT Hero.MaxHp, overwritten after Build() so this
        /// one run can't die and freeze — Assets/Data itself is never touched.
        static SimConfig BuildBattleConfig()
        {
            HeroConfig hero = Load<HeroConfig>("HeroConfig");
            WeaponConfig weapon = Load<WeaponConfig>("WeaponConfig");
            MobConfig chaser = Load<MobConfig>("MobChaserConfig");
            MobConfig gunner = Load<MobConfig>("MobGunnerConfig");
            WaveConfig wave = Load<WaveConfig>("WaveConfig");
            ArenaConfig arena = Load<ArenaConfig>("ArenaConfig");
            VisibilityConfig visibility = Load<VisibilityConfig>("VisibilityConfig");
            // Stage 3 Task 12 (owner decision R-73): the harness measures the
            // REAL battle config, so it loads the two new archetype assets and
            // the match-flow one alongside the seven it already had — an
            // Elite-free long run would stop measuring the arena the game
            // actually ships.
            MobConfig elite = Load<MobConfig>("MobEliteConfig");
            MobConfig director = Load<MobConfig>("MobDirectorConfig");
            MatchFlowConfig flow = Load<MatchFlowConfig>("MatchFlowConfig");
            // Stage 3 Task 13: the catalog and loot balance sheet — same
            // "measure the real battle config" reasoning as elite/director/
            // flow above.
            ItemCatalog items = Load<ItemCatalog>("ItemCatalog");
            LootConfig loot = Load<LootConfig>("LootConfig");
            SimConfig cfg = SimConfigBuilder.Build(hero, weapon, chaser, gunner, wave, arena,
                visibility, elite, director, flow, items, loot);
            cfg.Hero.MaxHp = 1e9f;
            return cfg;
        }

        static T Load<T>(string name) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>($"{DataDir}/{name}.asset");
            if (asset == null)
            {
                throw new InvalidOperationException(
                    $"LongRunHarness: missing battle asset '{DataDir}/{name}.asset'.");
            }
            return asset;
        }
    }
}
