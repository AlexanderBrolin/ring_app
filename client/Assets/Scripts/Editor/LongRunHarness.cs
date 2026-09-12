using System;
using System.Globalization;
using Ring.Data;
using Ring.Networking.Server;   // TickTimeAccumulator — the home already exists.
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
    ///
    /// Cost per tick (app-94sk Ф-0): the two trailing CSV columns and the final
    /// line report what ONE `world.Tick` costs in wall-clock milliseconds. They
    /// exist because the hit-by-model pass makes the narrow phase go from one
    /// circle to 9-15 capsules while the broad phase grows x12.2-12.7 by area,
    /// and a baseline taken AFTER that change has nothing to compare against.
    /// ⛔ THIS MEASURES THE EDITOR/MONO PROCESS ON ONE IMMORTAL BOT. The numbers
    /// are comparable BETWEEN RUNS OF THIS HARNESS and nothing else — not with
    /// the headless IL2CPP image, whose own tick cost `ServerBootstrap` already
    /// logs through the very same `TickTimeAccumulator`. Comparing across the
    /// two contours is a category error, and it is said here rather than left
    /// for the reader to discover.
    /// Frame time is still out of scope (see the paragraph above): this is the
    /// cost of the SIMULATION step, not of a rendered frame.
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
            // TWO accumulators, not one. The WINDOW (reset at every print) is what
            // two runs compare against each other; the WHOLE-run one denies a
            // regression the chance to hide inside a single phase of the 20
            // minutes. Both are the same class that meters the live server tick
            // (`MatchServer._tickTime`), so the two contours' numbers at least
            // have the same SHAPE — see the class doc for why they must not be
            // compared by value.
            var sw = new System.Diagnostics.Stopwatch();
            var window = new TickTimeAccumulator();
            var whole = new TickTimeAccumulator();

            // ⛔ `CultureInfo.InvariantCulture` IS PASSED, NOT ASSUMED — every
            // line this harness prints, exactly the call shape `ServerBootstrap.
            // LogMatchSummary` uses for the very same accumulator's numbers.
            // String interpolation picks up `CurrentCulture`, and on this
            // workstation that is comma-decimal: `9,2971` inside a COMMA-separated
            // line splits one column into two. Measured, not feared — the first
            // baseline run of this instrument printed exactly that, and the fault
            // is older than the ms/tick columns: `PlayerHp` is a float too, and
            // only the immortal-bot override (`1E+09`, no separator) hid it.
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "LongRunHarness: starting {0}-tick run (20 min @ 30 Hz, worldSeed={1}, " +
                "inputSeed={2}, Hero.MaxHp overridden to {3} — immortal bot, cap/memory " +
                "stress only, see class doc).",
                TotalTicks, WorldSeed, InputSeed, cfg.Hero.MaxHp));
            // PlayerAlive/PlayerHp/Kills/WaveIndex are extra columns beyond the
            // brief's minimum set — kept even with the immortal-bot override so a
            // regression in the override (or a future change to it) stays visible
            // in the log instead of silently reverting to the frozen-world case.
            // The two ms/tick columns go LAST on purpose: anything already parsing
            // this CSV by column index keeps working.
            Debug.Log("LongRunHarness,tick,MobCount,ProjectileCount,EventCount," +
                "DroppedEvents,MobSpawnsSkipped,ProjectileSpawnsSkipped,GCTotalMemory," +
                "PlayerAlive,PlayerHp,Kills,WaveIndex,MsPerTickAvg,MsPerTickMax");

            for (int i = 1; i <= TotalTicks; i++)
            {
                sw.Restart();
                world.Tick(Scripted(ref rng));
                sw.Stop();
                // Same input the live caller feeds it (`MatchServer.OnPostTick`):
                // `Elapsed.TotalMilliseconds` is a double, so "intermediate
                // numbers are double" is satisfied by the type itself.
                double tickMs = sw.Elapsed.TotalMilliseconds;
                window.Record(tickMs);
                whole.Record(tickMs);

                if (i % LogInterval == 0)
                {
                    world.CaptureSnapshot(snapshot);
                    // Stage 2 Task 5: Kills is personal (world.Stats == StatsAt(0),
                    // the harness's single bot); MobSpawnsSkipped/ProjectileSpawnsSkipped
                    // moved to WorldStats (world-scoped, shared arena caps).
                    MatchStats stats = world.Stats;
                    WorldStats worldStats = world.WorldStats;
                    long mem = GC.GetTotalMemory(false);
                    Debug.Log(string.Format(CultureInfo.InvariantCulture,
                        "LongRunHarness,{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12:F4},{13:F4}",
                        i, snapshot.MobCount, snapshot.ProjectileCount,
                        world.EventCount, world.DroppedEvents,
                        worldStats.MobSpawnsSkipped, worldStats.ProjectileSpawnsSkipped, mem,
                        snapshot.Player.Alive, snapshot.Player.Hp, stats.Kills,
                        snapshot.Wave.WaveIndex,
                        window.AverageMs, window.MaxMs));
                    // The window closes here: the next line describes the next
                    // two sim-minutes, not everything since tick 1. The max is
                    // the point — an average hides a single spike, a maximum
                    // does not.
                    window.Reset();
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

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "LongRunHarness: completed {0} ticks, final StateHash=0x{1:X16}, " +
                "avg {2:F4} ms/tick, max {3:F4} ms over {4} ticks.",
                TotalTicks, world.StateHash(), whole.AverageMs, whole.MaxMs, whole.Count));
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
