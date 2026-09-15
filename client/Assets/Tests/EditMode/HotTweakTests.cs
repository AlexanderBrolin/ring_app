using System.Collections.Generic;
using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class HotTweakTests
    {
        [Test]
        public void ApplyConfig_ClampsHpDown_KeepsTimersInRange()
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            w.Tick(new SimInput { DashRequested = true }); // active cooldown — П-12(a)
            var next = TestConfigs.Default();
            next.Hero.MaxHp = 50f;
            w.ApplyConfig(next);
            Assert.LessOrEqual(w.Player.Hp, 50f);
            Assert.GreaterOrEqual(w.Player.DashCooldown, 0f);
            Assert.LessOrEqual(w.Player.DashCooldown, next.Hero.DashCooldown);
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

        /// Т4 (app-ggvz, spec §3.2): retuning Wave.WavePauseByZone mid-match
        /// does NOT re-arm a timer that is already counting. The new number
        /// takes effect the next time the ring reloads its own timer, which is
        /// at a wave start or at a clear and nowhere else.
        ///
        /// THIS IS ACCEPTED BEHAVIOR, AND THIS TEST IS WHAT MAKES IT A
        /// DECISION RATHER THAN DRIFT. The alternative — snapping every live
        /// timer to the new value — would let the owner's slider stall a wave
        /// that was two ticks from arriving, or fire three rings at once the
        /// moment he lowered the number, which is exactly the surprise a hot
        /// tweak must not produce during a playtest. It also matches what
        /// ApplyConfig does everywhere else: it clamps magnitudes against the
        /// new ceilings and never re-seeds a running countdown.
        [Test]
        public void HotTweak_WavePauseChange_LeavesArmedTimersRunning()
        {
            SimConfig c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            TestWorlds.IdleTicks(w, 10);
            int armed = w.WaveRef(Zone.Outer).PhaseTicks;
            Assert.Greater(armed, 0, "premise: the outer ring's timer is mid-countdown");

            SimConfig next = c;
            // Far longer than the fixture's own {2, 3, 3}s, so "unchanged" and
            // "reloaded" cannot possibly read the same number.
            next.Wave.WavePauseByZone = new[] { 30f, 30f, 30f };
            w.ApplyConfig(next);

            Assert.AreEqual(armed, w.WaveRef(Zone.Outer).PhaseTicks,
                "правка пауз перезарядила уже заряженный таймер: горячая правка меняет " +
                "СЛЕДУЮЩЕЕ окно тишины, а не то, что уже идёт");

            // ...and the new number IS what the ring reloads with, the first
            // time it reloads at all. `armed` more ticks land exactly on the
            // first wave, and a wave start is a reload.
            TestWorlds.IdleTicks(w, armed);
            Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Outer).Phase,
                "premise: the first wave of the outer ring has started");
            Assert.AreEqual(
                SimulationWorld.TicksFromSeconds(next.Wave.WavePauseByZone[(int)Zone.Outer]),
                w.WaveRef(Zone.Outer).PhaseTicks,
                "новая пауза не применилась при первой же перезарядке таймера кольца");
        }

        /// Т5 (app-ggvz, spec §3.2/§4 — the SECOND hot-tweak case the spec
        /// asks for, beside the pause one above): lowering
        /// Wave.MaxAliveByZone BELOW a ring's standing population freezes that
        /// ring's spawning until natural attrition brings it back under the new
        /// number, and REMOVES NOBODY.
        ///
        /// THIS IS ACCEPTED BEHAVIOR AND THIS TEST IS WHAT MAKES IT A DECISION.
        /// The alternative — culling the excess on ApplyConfig — would delete
        /// live mobs out from under the players mid-fight because the owner
        /// dragged a slider, and it would do it to mobs that are already
        /// engaged. Freezing instead is also what ApplyConfig does everywhere
        /// else: it clamps what a NEW value governs and never retroactively
        /// undoes what the old one produced.
        ///
        /// The debt assertion at the end is what makes this a witness rather
        /// than a description: without it, "the population did not grow" would
        /// also be true of a ring that simply had no wave to seat. The ring
        /// DID start its next wave, owes it in full, and is refused every tick.
        [Test]
        public void HotTweak_MaxAliveLoweredBelowPopulation_FreezesSpawn_AndRemovesNoMobs()
        {
            SimConfig c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            int start = SimulationWorld.TicksFromSeconds(c.Wave.FirstWaveDelay);
            int cap = c.Wave.MaxSpawnsPerZonePerTick;
            int perRing = WaveSystem.CountForTest(in c.Wave, 0, w.PlayerCount);
            TestWorlds.IdleTicks(w, start + (perRing + cap - 1) / cap);

            int standing = w.WaveRef(Zone.Outer).AliveCount;
            int onArena = w.MobCount;
            Assert.AreEqual(perRing, standing,
                "premise: the outer ring has seated its whole first wave, so there is a "
                + "population to lower the ceiling BELOW");

            SimConfig next = c;
            next.Wave.MaxAliveByZone = new[]
                { 1, c.Wave.MaxAliveByZone[1], c.Wave.MaxAliveByZone[2] };
            w.ApplyConfig(next);

            Assert.AreEqual(onArena, w.MobCount,
                "понижение потолка удалило мобов с арены: горячая правка меняет то, что кольцу " +
                "ПОЗВОЛЕНО впредь, и никогда не отменяет уже рождённое");

            // Past the ring's own next wave: it starts, owes its whole debt,
            // and is refused every single tick by the new ceiling.
            TestWorlds.IdleTicks(w,
                SimulationWorld.TicksFromSeconds(c.Wave.WavePauseByZone[(int)Zone.Outer]) + 4);

            Assert.AreEqual(standing, w.WaveRef(Zone.Outer).AliveCount,
                "кольцо продолжило спавнить поверх понижённого потолка: спавн обязан замереть " +
                "до естественной убыли");
            Assert.Greater(w.WaveRef(Zone.Outer).PendingTotal, 0,
                "кольцо не пыталось спавнить вовсе — тогда «население не выросло» ничего не " +
                "доказывает");
        }

        [Test]
        public void ApplyConfig_ArenaTopologyChange_Throws()
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            var next = TestConfigs.Default();
            next.Arena.Radius = 20f;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        /// Reflective clamp-pass (QC7 — home of ApplyConfig's clamp contract,
        /// not WorldLifecycleTests): every float field of PlayerState is pinned
        /// at 1e6 through the canon test-seam, ApplyConfig runs with reduced
        /// maxima, then each field is checked by reflection against a local
        /// field->ceiling map below. A field with no map entry fails LOUDLY —
        /// that's the point: a newly declared PlayerState float field must get
        /// a line here in the SAME task that adds it, whether ApplyConfig
        /// clamps it (map to that ceiling) or deliberately leaves it alone (map
        /// to float.PositiveInfinity, a documented "not clamped", not an
        /// oversight — see RecoilOffset below, re-clamped every tick by
        /// WeaponSystem against RecoilMaxRad instead of by ApplyConfig).
        /// Populated by Task 9 (Stamina, StaminaRegenDelayTimer). Extended by
        /// Task 10 (SlideTimer et al.), Task 11 (LinkWindowTimer), Task 12
        /// (DashSpeedCur), Task 14 (AimSettleTimer) and app-88jb Т7
        /// (Tilt/TiltVel, both deliberately unclamped — see their own entry
        /// for why the collector's spring has no ceiling to migrate to) — add
        /// a line here as part of that task's GREEN step, not as an
        /// afterthought.
        ///
        /// Stage 2 Task 10 widened the pass from float-only to float AND int
        /// fields. Until then an int PlayerState field was skipped silently by
        /// the `FieldType != typeof(float)` filter, so the task's two new
        /// tick counters (DashRequestCooldownTicks / SlideRequestCooldownTicks
        /// — the first int fields the struct ever had) would have slipped
        /// through the "no map entry fails LOUDLY" guarantee entirely. Ceilings
        /// stay a float map: every int ceiling in play is small and exactly
        /// representable, and the comparison is the same "<= its new maximum".
        ///
        /// Stage 3 Task 1 extended it again: LootTimer/RepairTimer/
        /// ExtractTimer/LootTargetContainerId map to float.PositiveInfinity
        /// (declared inert that task, no ApplyConfig clamp until Т17/Т19/Т23
        /// give them behavior — Т17 has since paid that debt for LootTimer,
        /// which now maps to the longest transfer in the new tier table, and
        /// Т19 paid it for RepairTimer, whose own map entry a few lines below
        /// carries the reasoning; only ExtractTimer is still owed, by Т23),
        /// and `typeof(byte)` joined
        /// `unmeasuredFieldTypes` below for ExtractKind/LootTargetSlot — the
        /// struct's first byte fields, small discriminants rather than
        /// magnitudes.
        [Test]
        public void ApplyConfig_ReflectiveClampPass_EveryFloatFieldWithinNewMax()
        {
            var cfg = TestConfigs.Default();
            var next = TestConfigs.Default();
            next.Hero.MaxHp = 40f;
            next.Hero.DashDuration = 0.05f;
            next.Hero.DashCooldown = 0.4f;
            next.Hero.DashIframes = 0.05f;
            next.Hero.DashBufferWindow = 0.05f;
            next.Hero.StaminaMax = 20f;
            next.Hero.StaminaRegenDelay = 0.2f;
            next.Hero.EdgeRequestMinTicks = 2; // reduced from TestConfigs' 3 — the clamp must bite
            next.Weapon.FireInterval = 0.04f;

            // Stage 3 Task 17: the loot channel's ceiling is an AGGREGATE over
            // the new config's own tier table, not a single named number like
            // every other ceiling in this map — a running channel's target
            // tier is not recoverable at hot-tweak time (the container may
            // already be gone), so the longest transfer any tier can ask for
            // is the only honest bound. Computed HERE, by this test's own
            // loop, deliberately NOT through the production aggregate
            // (LootTransferTimes.Longest): a test that reused the very
            // function under test would move with it and stop being able to
            // see it break.
            float longestTransfer = 0f;
            foreach (float seconds in next.Loot.TransferSeconds)
                longestTransfer = math.max(longestTransfer, seconds);
            Assert.Greater(longestTransfer, 0f,
                "premise: the fixture's tier table must contain a real transfer time, or the " +
                "LootTimer ceiling below would be a vacuous zero");

            var ceilingByField = new Dictionary<string, float>
            {
                ["Hp"] = next.Hero.MaxHp,
                ["Stamina"] = next.Hero.StaminaMax,
                ["StaminaRegenDelayTimer"] = next.Hero.StaminaRegenDelay,
                ["DashTimer"] = next.Hero.DashDuration,
                ["DashCooldown"] = next.Hero.DashCooldown,
                ["IframeTimer"] = next.Hero.DashIframes,
                ["DashBufferTimer"] = next.Hero.DashBufferWindow,
                // Task 12: ricochet-decayed dash speed clamps to the new
                // DashSpeed ceiling, same contract as the dash timers above.
                ["DashSpeedCur"] = next.Hero.DashSpeed,
                // app-88jb Т22 (Р443): the collision tax on a slide is subtracted
                // FROM SlideSpeed, so a hot-tweak that lowers that speed must not
                // leave a penalty larger than what it is subtracted from.
                ["SlideSpeedPenalty"] = next.Hero.SlideSpeed,
                ["FireCooldown"] = next.Weapon.FireInterval,
                ["RecoilOffset"] = float.PositiveInfinity, // clamped by WeaponSystem, not ApplyConfig
                // Stage 3 Task 2: the magazine clamps down to the new AmmoMax
                // ceiling, same contract as FireCooldown above.
                ["Ammo"] = next.Weapon.AmmoMax,
                // Task 10: slide timers.
                ["SlideTimer"] = next.Hero.SlideDuration,
                ["SlideBufferTimer"] = next.Hero.SlideBufferWindow,
                ["RunUpTimer"] = next.Hero.RunUpSeconds,
                ["PostDashSlideTimer"] = next.Hero.PostDashSlideWindow,
                ["LinkWindowTimer"] = next.Hero.LinkWindowSeconds,
                // Task 14: aim-settle progress.
                ["AimSettleTimer"] = next.Hero.AimSettleSeconds,
                // Stage 2 Task 10: the two edge-request tick counters.
                ["DashRequestCooldownTicks"] = next.Hero.EdgeRequestMinTicks,
                ["SlideRequestCooldownTicks"] = next.Hero.EdgeRequestMinTicks,
                // Stage 3 Task 17: LootTimer got its behavior (LootOps.Begin
                // sets it from Loot.TransferSeconds), so it gets its ceiling
                // here — the "add a line as part of that task's GREEN step"
                // discipline this test's own doc asks for, paid on the task
                // that doc named. ExtractTimer stays uncapped until Т23 does
                // the same for it.
                ["LootTimer"] = longestTransfer,
                // Stage 3 Task 19: RepairTimer got its behavior too
                // (LootOps.Begin arms it from Loot.RepairKitChannelSeconds),
                // so it gets a real ceiling here. Read DIRECTLY off `next`,
                // not through a locally-recomputed aggregate the way
                // `longestTransfer` above is (lesson 324's own concern):
                // this is the ONE difference from LootTimer's neighbor —
                // there is exactly one repair-kit channel length in the
                // config, a single named number, not a per-tier table to
                // reduce. Reading the SAME field ApplyConfig's own clamp
                // reads is not "the test shares logic with the code under
                // test" the way calling LootTransferTimes.Longest would be
                // (that IS a computation this test would otherwise
                // duplicate) — it is simply the config's one source of
                // truth for the ceiling, exactly like `["Hp"] = next.Hero.
                // MaxHp` above reads Hp's ceiling directly. A mutation that
                // swaps ApplyConfig's clamp target for a wrong field, or
                // drops the clamp line outright, still moves only ONE side
                // of this comparison.
                ["RepairTimer"] = next.Loot.RepairKitChannelSeconds,
                ["ExtractTimer"] = float.PositiveInfinity,
                // LootTargetContainerId: an entity id, not a magnitude —
                // nothing for ApplyConfig to ever clamp it against.
                ["LootTargetContainerId"] = float.PositiveInfinity,
                // ⛔ THE COLLECTOR'S TILT PAIR IS NOT IN THIS MAP, AND ITS
                // ABSENCE IS THE ENTRY (app-94sk T5a). app-88jb Т7 mapped
                // both to `float.PositiveInfinity` here; T5a made them
                // `float2`, `float2` is listed in `unmeasuredFieldTypes`
                // below, and the sweep therefore `continue`s past them before
                // it ever looks a ceiling up. Two entries that can never be
                // read is exactly the dead code this file has no reverse
                // guard for — nothing asserts the map's size or its key set —
                // so they are gone rather than left to look deliberate.
                //
                // WHAT THEY SAID IS STILL TRUE AND IS WHY NOTHING REPLACED
                // THEM. ApplyConfig does not clamp a collector's tilt because
                // there is nothing to clamp it AGAINST: Т6 gave the mob pass a
                // ceiling only because `MobSimConfig.TiltFallAngle` is a
                // config value that MOVES on a tweak, while `HeroSimConfig`
                // carries no such angle and is never to be given one (Р377,
                // ADR-001 §9 — a round may not take control away from a
                // player). The every-tick bound is `TiltSystem`'s collector
                // pass, which drags any magnitude back to exactly zero
                // through `Impact.SpringStep`'s `RestEpsilon` snap in a finite
                // number of ticks — the same "documented not-clamped" shape
                // this map's own doc spells out for `RecoilOffset`. A literal
                // PI ceiling was considered and refused at Т7: it would need a
                // matching branch in ApplyConfig's PLAYER loop that no test
                // could ever kill (nothing the game can do produces a lean
                // past PI — the arsenal's largest impulse peaks near 0.6 rad),
                // and it would put a bare geometric constant into production
                // balance code against CRITICAL RULE 6.
                // app-88jb Т24 (spec §3.6): the rewind slot is an ADDRESS,
                // not a magnitude -- there is no "too large" value of it to
                // clamp towards, only a valid one and an invalid one, and
                // validity is owned by PositionHistory's occupancy set rather
                // than by a balance number. (RULING 133 replaced the free list
                // this comment first named with that set, in the same commit
                // that wrote the comment -- PositionHistory now says "THE
                // ALLOCATOR IS A SET, NOT A LIST" in as many words.)
                // ApplyConfig leaves the field alone on purpose: a hot-tweak
                // changes numbers, and every cap the ring is sized from is one
                // ArenaTopologyMatches refuses to hot-tweak at all --
                // Arena.RewindCapTicks included, see
                // HotTweak_RewindCapChange_Throws.
                // Same form and same reasoning as the two entries above.
                ["HistorySlot"] = float.PositiveInfinity,
                // app-8dv (spec §3.2/§3.4): the two shot counters. Neither is a
                // magnitude with a config ceiling to clamp towards -- BurstShots
                // is a position inside a burst, reset by the trigger coming up
                // and by nothing else, and ShotOrdinal is an IDENTITY, the
                // shot's number in the match. ApplyConfig leaves both alone on
                // purpose: a hot-tweak changes numbers, and clamping an identity
                // would hand a later shot a key the journal has already seen.
                // Same form and same reasoning as Tilt/TiltVel/HistorySlot
                // above, and PositiveInfinity for the same reason -- the assert
                // below measures float/int fields against a ceiling, so "no
                // ceiling" has to be spelled as a value it can compare with.
                ["BurstShots"] = float.PositiveInfinity,
                ["ShotOrdinal"] = float.PositiveInfinity,
            };

            var w = new SimulationWorld(5, cfg);
            object boxedPlayer = w.Player;
            foreach (var field in typeof(PlayerState).GetFields())
            {
                if (field.FieldType == typeof(float)) field.SetValue(boxedPlayer, 1e6f);
                else if (field.FieldType == typeof(int)) field.SetValue(boxedPlayer, 1_000_000);
            }
            w.SetPlayerForTest((PlayerState)boxedPlayer);

            w.ApplyConfig(next);

            // Fix-round 1 (M-3): field types this pass deliberately does NOT
            // measure, each for a stated reason. Anything outside both this set
            // and the measured types below is a hard failure — extending the
            // pass to int (Stage 2 Task 10) would otherwise have left the exact
            // same silent hole for the next new type (a byte, an enum) that the
            // old float-only filter left for int.
            var unmeasuredFieldTypes = new HashSet<System.Type>
            {
                // Headings and positions (Pos, Vel, AimPoint, DashDir, SlideDir):
                // ApplyConfig has no per-axis ceiling to clamp them to — arena
                // containment is Geometry's job, every tick, not a hot-tweak's.
                typeof(float2),
                // Alive, Extracted: state flags, not magnitudes — nothing to clamp.
                typeof(bool),
                // Stage 3 Task 1: byte discriminants (ExtractKind: which
                // extraction route; LootTargetSlot: which backpack slot a
                // loot channel targets) — small fixed-range values, not
                // magnitudes with a config-driven ceiling ApplyConfig would
                // ever clamp against.
                typeof(byte),
            };

            foreach (var field in typeof(PlayerState).GetFields())
            {
                float actual;
                if (field.FieldType == typeof(float)) actual = (float)field.GetValue(w.Player);
                else if (field.FieldType == typeof(int)) actual = (int)field.GetValue(w.Player);
                else
                {
                    if (!unmeasuredFieldTypes.Contains(field.FieldType))
                    {
                        Assert.Fail($"PlayerState.{field.Name} has type {field.FieldType.Name}, " +
                            "which this clamp-pass neither measures (float, int) nor lists in " +
                            "unmeasuredFieldTypes as deliberately unmeasured — decide which it is " +
                            "in the SAME task that declares the field, and say so here.");
                    }
                    continue;
                }
                Assert.IsTrue(ceilingByField.TryGetValue(field.Name, out float ceiling),
                    $"PlayerState.{field.Name} is a new float/int field with no clamp-pass " +
                    "entry in ApplyConfig_ReflectiveClampPass_EveryFloatFieldWithinNewMax's " +
                    "ceilingByField map — add a line mapping it to its ApplyConfig ceiling, " +
                    "or to float.PositiveInfinity if ApplyConfig intentionally leaves it unclamped.");
                Assert.LessOrEqual(actual, ceiling,
                    $"PlayerState.{field.Name} exceeded its ApplyConfig ceiling after hot-tweak");
            }
        }

        [Test]
        public void ApplyConfig_LoweringTheFallAngle_DoesNotStandTheFallenUp()
        {
            // THE MOB PHASE of the hot tweak (app-88jb Т6, finding D-I5).
            // WRITTEN AHEAD OF IT: before Т6, ApplyConfig had no mob pass at
            // all -- its only loop ran over _players, which is also why the
            // reflective clamp pass above reflects over PlayerState and over
            // nothing else. Т6 ADDED that pass, and the asserts below are the
            // witnesses of it; the line anchor is deliberately gone, because
            // anchors rot and this file has paid for that before.
            // ⚠ THE LIST CARRIES NO COUNT, deliberately: it opened as "two
            // halves, one witness each" and has been three since Т6 and five
            // since app-94sk T5a, because every task that added a claim added
            // it at the bottom and left the number at the top. What belongs
            // here is whatever ApplyConfig's mob pass promises:
            //   * a mob already down does NOT get up retroactively when the
            //     threshold is lowered -- otherwise a balance edit would
            //     resurrect bodies;
            //   * its tilt DOES clamp into the new maximum, the same
            //     clamp-down-to-the-new-ceiling contract every player
            //     magnitude in ApplyConfig already keeps;
            //   * and so does the StateTimer of an already-downed body, into
            //     the new DownedSeconds -- otherwise a shortened window would
            //     leave a mob lying past an end it can never reach;
            //   * the clamp is by LENGTH and it SHORTENS rather than turns:
            //     a lean off the axis keeps its heading (T5a);
            //   * and the body it clamped is STILL STANDING one tick later,
            //     which is the promise the other four are only evidence for.
            // THE THIRD ASSERT LIVES HERE RATHER THAN IN A TEST OF ITS OWN
            // (implementer's call, coordinator's open question of Step 4):
            // this method already builds the exact fixture that witness needs
            // -- a mob IN Downed, with a live StateTimer, and a `tighter`
            // config to migrate onto -- so a separate test would restate the
            // whole setup for one assertion (rule 2). Without it the timer
            // clamp would be a branch with no victim: deleting its line leaves
            // every other assertion here green.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            w.SpawnMobForTest(MobType.Gunner, new float2(6f, 0f));
            var m = w.Mobs[0];
            m.Hp = 1e6f; m.Ai = MobAiState.Downed; m.StateTimer = 0.1f;
            // ⛔ ON ONE AXIS, EXPLICITLY (app-94sk T5a). `m.Tilt = 1.2f` still
            // COMPILES against a `float2` — `Unity.Mathematics` has an
            // implicit `float -> float2` — and would silently mean (1.2, 1.2),
            // a lean of length 1.697 instead of the 1.2 this fixture states.
            m.Tilt = new float2(1.2f, 0f);
            w.SetMobForTest(0, m);

            // THE SECOND BODY IS THE VECTOR HALF OF THE SAME CLAIM (T5a): a
            // lean that is NOT on one axis. It is what tells a clamp by LENGTH
            // apart from a clamp per COMPONENT, and only the first of those is
            // the rule this method's own name states.
            //
            // ⛔ THE NUMBERS ARE CHOSEN BY MEASUREMENT, NOT FOR LOOKS, and
            // they carry TWO failures at once (float32 replica, 200 000
            // directions per angle):
            //   * per COMPONENT, (0.5, 0.1) against the new angle 0.4 comes
            //     out (0.4, 0.1) — length 0.4123, above the very angle the
            //     tweak just set, and turned from 11.31° to 14.04°;
            //   * by LENGTH but with NO HEADROOM, the scale reads back
            //     0.40000004 — three ulps over — and `TiltSystem`, which
            //     measures with a square root, fells the body on the next
            //     tick. That direction is one of the 17.9% that overshoot;
            //     the (1.2, 0.6) this fixture used to carry is one of the
            //     57% that land exactly on the ceiling and could not have
            //     caught it.
            w.SpawnMobForTest(MobType.Gunner, new float2(9f, 0f));
            var diagonal = w.Mobs[1];
            diagonal.Hp = 1e6f; diagonal.Ai = MobAiState.Idle;
            float2 givenLean = new float2(0.5f, 0.1f);
            diagonal.Tilt = givenLean;
            w.SetMobForTest(1, diagonal);

            // SimConfig and MobSimConfig are both structs (SimConfig.cs:740,
            // :118), so this is a copy by value and `cfg` keeps the old angle.
            SimConfig tighter = cfg;
            tighter.Gunner.TiltFallAngle = 0.4f;
            // The window is shortened BELOW the timer the mob is carrying
            // (0.1 s above), so the clamp has to bite -- a ceiling the value
            // already fits under would witness nothing.
            tighter.Gunner.DownedSeconds = 0.05f;
            w.ApplyConfig(tighter);

            Assert.AreEqual(MobAiState.Downed, w.Mobs[0].Ai, "хот-твик поднял упавшего");
            Assert.LessOrEqual(math.length(w.Mobs[0].Tilt), tighter.Gunner.TiltFallAngle,
                "крен не заклампен в новый максимум");

            // Expectations as the fixture's own values, never re-typed
            // literals: the ceiling is the angle set above, and the heading is
            // read out of the very variable the body was leaned with.
            //
            // ⛔ NO TOLERANCE ON THE CEILING, and that is the point of the
            // assert. `TiltSystem` asks `length > angle` with no slack at all,
            // so a fixture that allowed itself even 1e-5 here would be green
            // on a clamp whose result the NEXT TICK treats as past the
            // threshold.
            float2 clamped = w.Mobs[1].Tilt;
            Assert.LessOrEqual(math.length(clamped), tighter.Gunner.TiltFallAngle,
                "крен по двум осям не заклампен по ДЛИНЕ до значения, которое порог примет");
            Assert.AreEqual(math.atan2(givenLean.y, givenLean.x),
                math.atan2(clamped.y, clamped.x), 1e-4f,
                "кламп ПОВЕРНУЛ крен вместо того, чтобы укоротить его");
            // Expectation stated as the fixture's own field, never as a
            // repeated literal (two-sources-of-numbers rule).
            Assert.LessOrEqual(w.Mobs[0].StateTimer, tighter.Gunner.DownedSeconds,
                "таймер лежачего не заклампен в новое окно DownedSeconds");

            // ⛔⛔ AND THE PROMISE ITSELF, WHICH EVERY ASSERT ABOVE IS ONLY
            // EVIDENCE FOR: one tick later the clamped body is STILL STANDING.
            // Until app-94sk T5a this fixture never ticked the world at all,
            // so "a balance edit must not fell bodies" — the sentence
            // ApplyConfig's own comment is written around — had no witness
            // whatsoever; it inspected the clamp's output and trusted that
            // `TiltSystem` would agree with it. It does not agree for free:
            // the clamp scales in float32 and the threshold re-measures with a
            // square root, and without the headroom `ApplyConfig` scales into,
            // THIS line is the one that goes red.
            w.Tick(default);
            Assert.AreNotEqual(MobAiState.Downed, w.Mobs[1].Ai,
                "заклампенное тело уронил СЛЕДУЮЩИЙ ТИК — длина, которую вернул кламп, "
                + "читается порогом как «за порогом»");
        }

        [Test]
        public void HotTweak_WallChange_Throws()
        {
            // Stage 2 Task 14 (spec §3.3): ArenaTopologyMatches grows a wall
            // comparison mirroring the existing obstacle one — same
            // WallCount, only a coordinate moves.
            var c = TestConfigs.Default();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(5f, -5f) };
            c.Arena.WallB = new[] { new float2(5f, 5f) };
            c.Arena.WallHalfWidth = new[] { 1f };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.WallB = new[] { new float2(5f, 6f) }; // same count/half-width, moved coordinate
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_WallHalfWidthChange_Throws()
        {
            // Carryover-t14.md #2 (Task 12 review): comparing only WallA/
            // WallB and skipping WallHalfWidth would let a corridor-width
            // tuning pass as a hot-tweak while Depenetrate keeps pushing
            // bodies out to the OLD width — same A/B here, only the width changes.
            var c = TestConfigs.Default();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(5f, -5f) };
            c.Arena.WallB = new[] { new float2(5f, 5f) };
            c.Arena.WallHalfWidth = new[] { 1f };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.WallHalfWidth = new[] { 1.5f };
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_BarrierTopChange_Throws()
        {
            // Stage 2 Task 46 (bd app-r8x): the interior barriers' modelled
            // height is arena topology exactly like WallHalfWidth above is.
            // Raising or lowering it mid-match changes which shots the geometry
            // stops, and ApplyConfig has no way to reconcile rounds already in
            // flight against the old height — the same mine Task 14 closed for
            // corridor width, one field over.
            var c = TestConfigs.Default();
            c.Arena.BarrierTop = 3f;
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.BarrierTop = 1.5f;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_CapChange_Throws()
        {
            // Coordinator addition: one of the three per-match entity caps
            // (MaxMobs/MaxProjectiles/MaxEventsPerFrame) added to
            // ArenaTopologyMatches — without this, resizing a cap mid-match
            // would pass as a hot-tweak even though the backing arrays it
            // sized at construction can't grow. This particular fixture
            // exercises MaxProjectiles; MaxMobs and MaxEventsPerFrame get
            // their own dedicated throws below (I-6, fix-round T14) — before
            // this round only MaxProjectiles had any coverage at all.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxProjectiles = c.Arena.MaxProjectiles + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxMobsChange_Throws()
        {
            // I-6 (fix-round T14): MaxMobs had no dedicated coverage before
            // this round — only MaxProjectiles was exercised, by
            // HotTweak_CapChange_Throws above. This matters concretely from
            // Task 16 onward: carryover-t14.md #3 predicts the .asset's
            // MaxMobs moving 64->96 will make an old-generation config's
            // hot-tweak throw here, by design — this test pins the
            // mechanism that makes that true.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxMobs = c.Arena.MaxMobs + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_RewindCapChange_Throws()
        {
            // app-88jb Т24 fix-round (review I-4): RULING 134 put
            // Arena.RewindCapTicks into ArenaTopologyMatches, and it was the
            // ONE member of that comparator with no named witness — twenty-four
            // others have one, so deleting the line passed all 1709 tests.
            //
            // WHAT THE MUTANT COSTS IS NOT COSMETIC, which is why the line is
            // worth a test of its own. PositionHistory's rows are sized to
            // RewindCapTicks + 1 in the world's constructor and are never
            // resized. Let a hot-tweak of 5 -> 10 through and the config claims
            // a ten-tick window while the ring still holds six rows (T-5 … T,
            // app-gtj6); from Т27/Т28 every shot rewound deeper than the fifth
            // tick reads a row
            // belonging to another tick, and reads it SILENTLY — no throw, no
            // counter, just hits landing where nobody stood.
            // Same shape as the entity caps above, and for the same reason:
            // the field sizes an array at construction, so the only correct
            // answer to a change is a restart.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.RewindCapTicks = c.Arena.RewindCapTicks + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxEventsPerFrameChange_Throws()
        {
            // I-6 (fix-round T14): same gap as HotTweak_MaxMobsChange_Throws
            // above, for MaxEventsPerFrame. carryover-t14.md #3 predicts the
            // .asset's MaxEventsPerFrame moving 256->512 at Task 16 will hit
            // exactly this throw for an old-generation config.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxEventsPerFrame = c.Arena.MaxEventsPerFrame + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxPickupsChange_Throws()
        {
            // Stage 3 Task 3: MaxPickups joins the per-match entity caps
            // above (MaxMobs/MaxProjectiles/MaxEventsPerFrame) — same
            // "backing array sized at construction, cannot grow mid-match"
            // reasoning, first coverage for this cap alongside those three.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxPickups = c.Arena.MaxPickups + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_InventoryCapacityChange_Throws()
        {
            // Stage 3 Task 4 (owner decision R-19, spec Р286): backpack
            // slot-point capacity is topology — items are discrete, so a
            // hot-tweak lowering InventoryCapacity below a player's
            // currently occupied slot points has no sound continuous
            // reconciliation (unlike Hp/Stamina, which just clamp down).
            // ANY change forces a restart, same "ANY change throws" rule as
            // HotTweak_MaxPlayersChange_Throws documents.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Hero.InventoryCapacity = c.Hero.InventoryCapacity + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxInventoryItemsChange_Throws()
        {
            // Stage 3 Task 4 (owner decision R-19, spec Р287): MaxInventoryItems
            // sizes Loot.Inventory's own backing array at construction — same
            // "backing array sized at construction, cannot grow mid-match"
            // reasoning as MaxPickups/MaxMobs/MaxProjectiles above
            // (HotTweak_MaxPickupsChange_Throws precedent, Stage 3 Task 3).
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Hero.MaxInventoryItems = c.Hero.MaxInventoryItems + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void CatalogChange_ThrowsOnApplyConfig()
        {
            // Stage 3 Task 13 (spec §3.7 Р264, coordinator R-87): the item
            // catalog is topology — its length and every record's SlotCost
            // decide what a wire byte Id and an occupied slot point MEAN in
            // a live world (same "backing array sized at construction,
            // cannot grow mid-match" class of reasoning as MaxContainerSlots
            // above, plus a meaning-of-the-bytes argument InventoryCapacity's
            // own test doesn't need). Second element (lesson 227), not the
            // first — TestConfigs.Default().Items carries five records.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            var items = (ItemDef[])c.Items.Clone();
            items[1].SlotCost += 1;
            next.Items = items;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_PlayerSpawnRingFracChange_Throws()
        {
            // I-6 (fix-round T14): PlayerSpawnRingFrac had NO coverage at
            // all before this round, despite ArenaTopologyMatches comparing
            // it right alongside MaxPlayers (Stage 2 Task 14) — it defines
            // spawn geometry at construction time the same way Radius does,
            // so a hot-tweak changing it must be rejected the same way.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.PlayerSpawnRingFrac = c.Arena.PlayerSpawnRingFrac + 0.05f;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void ArcTopologyChange_ThrowsOnApplyConfig()
        {
            // Stage 3 Task 9 (spec Р287): ArenaTopologyMatches grows a
            // zone-wall/door comparison mirroring the existing interior-wall
            // one (HotTweak_WallChange_Throws above) — Т4 already extended
            // this same method once; this widens it again rather than adding
            // a second comparator (rule 2).
            var c = TestConfigs.Default();
            c.Arena.ZoneWallCount = 1;
            c.Arena.ZoneWallRadius = new[] { 65f };
            c.Arena.ZoneWallHalfWidth = new[] { 1f };
            c.Arena.ZoneWallDoorStart = new[] { 0 };
            c.Arena.ZoneWallDoorCount = new[] { 1 };
            c.Arena.DoorCenterRad = new[] { 0f };
            c.Arena.DoorFreeWidth = new[] { 4f };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.DoorFreeWidth = new[] { 5f }; // same wall/door count, moved door width
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void ZoneRadiusChange_ThrowsOnApplyConfig()
        {
            // Spec §3.13 (Р286/Р287): plan text for Т9 named only "arc/door
            // arrays + caps" for ArenaTopologyMatches — an omission against
            // the spec, found by the coordinator reading the diff (same
            // shape as Т8's R-39). ZoneRadius feeds Geometry.ZoneOf, which
            // decides loot tier and wave zone budget — a hot-tweak moving a
            // boundary mid-match would silently change that semantic without
            // a restart. Fixture numbers are test-only (not the shipped
            // 65/92), same "two sources of numbers" discipline as the rest
            // of this file.
            var c = TestConfigs.Default();
            c.Arena.ZoneRadius = new[] { 10f, 20f };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.ZoneRadius = new[] { 10f, 25f }; // second element (lesson 227), not the first
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void PortalChange_ThrowsOnApplyConfig()
        {
            // Spec §3.13/§3.15 (Р186/Р287): portals are topology for the
            // same reason BarrierTop is (HotTweak_BarrierTopChange_Throws
            // above, Stage 2 Task 46 precedent) — the CLIENT draws them from
            // its own copy of the config, so a hot-tweak moving one would
            // desync the picture from the server exactly the way an
            // unchecked BarrierTop change did (the lesson Р186 records).
            var c = TestConfigs.Default();
            c.Arena.ExtractPos = new[] { new float2(5f, 5f), new float2(-5f, -5f) };
            c.Arena.ExtractZone = new byte[] { 0, 1 };
            c.Arena.ExtractKind = new byte[] { 0, 1 };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.ExtractPos = new[] { new float2(5f, 5f), new float2(-5f, -1f) }; // second portal (lesson 227), moved
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void PickupCapChange_ThrowsOnApplyConfig()
        {
            // Stage 3 Task 9 (spec Р287): ArenaTopologyMatches grows
            // MaxContainers into the per-match entity cap comparison, same
            // "backing array sized at construction, cannot grow mid-match"
            // reasoning as MaxPickups (HotTweak_MaxPickupsChange_Throws
            // above) and MaxMobs/MaxProjectiles/MaxEventsPerFrame before it.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxContainers = c.Arena.MaxContainers + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void ContainerSlotCapChange_ThrowsOnApplyConfig()
        {
            // Sibling cap Р287 requires alongside MaxContainers — each
            // per-match cap gets its own dedicated witness, same "MaxMobs/
            // MaxProjectiles/MaxEventsPerFrame/MaxPickups are four separate
            // tests, not one shared assertion" convention this file already
            // follows (R-42 mutation-per-branch: a comparator that checks
            // MaxContainers but not MaxContainerSlots needs its OWN failing
            // test to be caught — added beyond the plan's literal single
            // name for exactly this reason, see task-9 report).
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxContainerSlots = c.Arena.MaxContainerSlots + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxPlayersChange_Throws()
        {
            // Carryover-t14.md #1 (deferred from Task 4's review, M-4): a
            // hot-tweak lowering MaxPlayers below the match's actual live
            // player count must not silently succeed — ArenaTopologyMatches
            // now compares MaxPlayers like any other topology field, so ANY
            // change (not only a dedicated "< PlayerCount" special case)
            // forces the restart path instead of leaving the world's player
            // array longer than its own new cap. Renamed in fix-round T14
            // (M-6, was HotTweak_MaxPlayersBelowPlayerCount_Throws): the old
            // name promised a narrow "specifically below player count"
            // semantic this test never actually isolated — the fixture
            // below is one instance of the "ANY change throws" rule the
            // comment above already documents, nothing about it is specific
            // to the below-count case.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c, playerCount: 3); // uses the full MaxPlayers(3) cap
            var next = c;
            next.Arena.MaxPlayers = 2; // below the match's actual 3 live players
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }
    }
}
