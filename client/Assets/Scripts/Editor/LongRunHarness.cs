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
    /// Known limitation (observed, not fixed — see Task 31 report): the T29
    /// Scripted() bot is a random walk with no evasion, so at current balance
    /// numbers it typically dies a couple of minutes in; once dead, WeaponSystem
    /// stops (spec §3.12) and surviving mobs go idle, so most of the 36000 ticks
    /// run with a frozen world rather than genuinely sustained 20-minute combat.
    /// The logged PlayerAlive/PlayerHp/Kills/WaveIndex columns make that visible
    /// instead of silently conflating "stable" with "idle". The counters/memory
    /// checks below are still meaningful for what they DO cover (no exceptions,
    /// no unbounded growth up to whatever load was reached); reaching the Arena
    /// hard caps under real 20-minute survival is not exercised by this run.
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
            var snapshot = new RenderSnapshot(in cfg.Arena);
            var rng = new Unity.Mathematics.Random(InputSeed);

            Debug.Log($"LongRunHarness: starting {TotalTicks}-tick run " +
                $"(20 min @ 30 Hz, worldSeed={WorldSeed}, inputSeed={InputSeed}).");
            // PlayerAlive/PlayerHp/Kills/WaveIndex are extra columns beyond the
            // brief's minimum set — the scripted bot (random walk, no dodge logic)
            // can die well before 20 minutes are up, at which point WeaponSystem
            // stops running and mobs go idle (spec §3.12): MobCount/ProjectileCount
            // then read as "frozen", not "stabilized under sustained fire". These
            // columns make that distinction visible in the log instead of silently
            // conflating the two.
            Debug.Log("LongRunHarness,tick,MobCount,ProjectileCount,EventCount," +
                "DroppedEvents,MobSpawnsSkipped,ProjectileSpawnsSkipped,GCTotalMemory," +
                "PlayerAlive,PlayerHp,Kills,WaveIndex");

            for (int i = 1; i <= TotalTicks; i++)
            {
                world.Tick(Scripted(ref rng));

                if (i % LogInterval == 0)
                {
                    world.CaptureSnapshot(snapshot);
                    MatchStats stats = world.Stats;
                    long mem = GC.GetTotalMemory(false);
                    Debug.Log($"LongRunHarness,{i},{snapshot.MobCount},{snapshot.ProjectileCount}," +
                        $"{world.EventCount},{world.DroppedEvents}," +
                        $"{stats.MobSpawnsSkipped},{stats.ProjectileSpawnsSkipped},{mem}," +
                        $"{snapshot.Player.Alive},{snapshot.Player.Hp},{stats.Kills},{snapshot.Wave.WaveIndex}");
                }

                // Mirrors SimulationRunner.Update's per-frame ClearEvents() cadence
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
        /// standing still. No evasion logic, by design (matches T29 exactly) — see
        /// the class doc's "Known limitation" note for what that means at scale.
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

        static SimConfig BuildBattleConfig()
        {
            HeroConfig hero = Load<HeroConfig>("HeroConfig");
            WeaponConfig weapon = Load<WeaponConfig>("WeaponConfig");
            MobConfig chaser = Load<MobConfig>("MobChaserConfig");
            MobConfig gunner = Load<MobConfig>("MobGunnerConfig");
            WaveConfig wave = Load<WaveConfig>("WaveConfig");
            ArenaConfig arena = Load<ArenaConfig>("ArenaConfig");
            return SimConfigBuilder.Build(hero, weapon, chaser, gunner, wave, arena);
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
