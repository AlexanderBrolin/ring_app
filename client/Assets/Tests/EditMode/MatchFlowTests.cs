using NUnit.Framework;
using Ring.Networking.Server;
using Ring.Simulation.Core;
using Ring.Simulation.Objectives;
using Ring.Simulation.Visibility;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 1 (spec Ф1 "economy of the run", errata E-1/E-2/E-6 D-I2):
    /// the first task of the extraction-loop stage declares the whole new
    /// composition of world/match state (MatchState, MatchPhase, PlayerState.
    /// Extracted/ExtractKind and Ф1's other new fields — see SimStates.cs;
    /// coordinator fix-round Ф3 review m4: this used to also point at
    /// WorldLifecycleTests.PendingHashFields, removed unconditionally at the
    /// Т6 re-pin, historical reference dropped) and the third reason a match
    /// can end: a run everyone walked away from, `MatchEndReason.
    /// AllPlayersResolved`.
    ///
    /// THE PHASE MACHINE ITSELF ARRIVED WITH Т21 and is exercised below the
    /// "Stage 3 Т21" divider: activation by a collector entering the core
    /// (Р299), the one-way latch, the Director's death tick, the gate delay,
    /// and Ended outranking all of it. Still inert above that divider, and
    /// still genuinely inert in the world: nothing sets Extracted or ticks
    /// ExtractTimer yet — that half is Т23's. LootTimer and RepairTimer have
    /// since left that list: Т17 gave LootTimer Loot.LootOps, Т19 gave
    /// RepairTimer the same file's Use behavior, and both carry their own
    /// coverage in LootOpsTests — nothing in THIS file starts either channel,
    /// so every fixture here still reads both as zero.
    /// This file only pins the SHAPE of the new state and the ONE piece of
    /// behavior errata E-1's scope line allows: MatchEndPolicy's priority
    /// between a wipe and a resolved run, and MatchServer's own counting of
    /// "active" players that feeds it.
    ///
    /// `MatchEndPolicy.Evaluate`'s CANONICAL SIGNATURE is four parameters —
    /// `(worldTick, alivePlayers, activePlayers, anyExtracted)`, per the
    /// errata's own resolution of the plan's two candidate shapes:
    /// `alivePlayers` still answers "is anyone literally standing",
    /// `activePlayers` (alive AND not yet extracted) and `anyExtracted`
    /// together tell a wipe apart from a run everyone finished by dying,
    /// extracting, or some mix of the two. `MatchServer` counts both after
    /// `TickAll`, the same tick the old `alivePlayers` count was already
    /// taken on (spec §3.10).
    public class MatchFlowTests
    {
        [Test]
        public void NewWorld_StartsInFarmPhase()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            Assert.AreEqual(MatchPhase.Farm, w.Match.Phase);
            Assert.AreEqual(0, w.Match.DirectorDeathTick);
        }

        /// Errata E-6 "TDD и полнота" D-I2: the plan's own invariant test
        /// (`p.Alive = false; p.Extracted = true; Assert.IsFalse(Alive &&
        /// Extracted);`) was VACUOUS — it wrote `Alive = false` itself and
        /// then asked whether `false && true` is false, a tautology no
        /// production edit could ever fail. `Extracted` has no writer at all
        /// in Т1 (Т23 is the first), so the invariant itself cannot be
        /// broken yet — what CAN be proven now, and needs to stay proven, is
        /// the two halves this test actually checks: a fresh world's
        /// `Extracted`/`ExtractKind` start clear, and the one real actor in
        /// this task that changes `Alive` — death, `KillPlayer` — does NOT
        /// also set `Extracted`. Subject is player 1, not player 0 (lesson
        /// 227).
        [Test]
        public void Death_DoesNotSetExtracted_NorExtractKind()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);

            // Premise, checked for real: every freshly-constructed player
            // starts alive, not extracted, with no extraction route — the
            // baseline the death check below needs in order to mean anything.
            for (int i = 0; i < w.PlayerCount; i++)
            {
                PlayerState fresh = w.PlayerAt(i);
                Assert.IsTrue(fresh.Alive, $"premise: player {i} must start alive");
                Assert.IsFalse(fresh.Extracted, $"premise: player {i} must start not extracted");
                Assert.AreEqual(0, fresh.ExtractKind, $"premise: player {i} must start with ExtractKind 0");
            }

            // Killed through the existing DamagePlayer seam (DeathTests' own
            // convention for a victim other than player 0 — KillPlayerForTest
            // is hardcoded to player 0, see its own doc), overkill damage so
            // KillPlayer's death bookkeeping runs unconditionally.
            w.DamagePlayer(1, ProjectileIds.NoOwner, cfg.Hero.MaxHp + 1f, w.PlayerAt(1).Pos,
                HitZone.Body, new float2(1f, 0f));

            PlayerState dead = w.PlayerAt(1);
            Assert.IsFalse(dead.Alive, "premise: the overkill damage must actually have killed the player");

            // The non-vacuous assertion. Nothing in Т1 writes Extracted/
            // ExtractKind anywhere — KillPlayer's own death bookkeeping
            // (SimulationWorld.cs) never touches either field — so this
            // passes today because the path is genuinely untouched, not
            // because nothing tried. It exists to go red the moment a future
            // task's KillPlayer starts setting Extracted on the death path,
            // which is exactly the confusion a separate bit (Р223) exists to
            // prevent: dying is not extracting.
            Assert.IsFalse(dead.Extracted,
                "death must not set Extracted — Р223 keeps death and extraction as separate bits");
            Assert.AreEqual(0, dead.ExtractKind, "death must not set ExtractKind either");
        }

        [Test]
        public void Resolved_OutranksAllDead_WhenSomeoneExtracted()
        {
            var policy = new MatchEndPolicy(maxDurationTicks: 1000);
            // Two died, one extracted: nobody is alive, nobody is active
            // (alive AND not extracted) — but the run was resolved, not wiped.
            Assert.AreEqual(MatchEndReason.AllPlayersResolved,
                policy.Evaluate(10, 0, 0, anyExtracted: true));
        }

        /// Ф1 fix-round (review B-I-6): the OTHER half of the priority
        /// `Resolved_OutranksAllDead_WhenSomeoneExtracted` above pins. Every
        /// `anyExtracted: true` call in this file ran at tick 10 against a
        /// limit of 1000, so a refactor that moved the duration check to the
        /// top of `Evaluate` survived the whole suite — and the difference is
        /// visible from OUTSIDE the process: the last collector extracting on
        /// the boundary tick would exit with code 4, "ran out of time", instead
        /// of 0, "played out" (§3.11). Same shape as
        /// `AllDeadWinsOverMaxDuration` (`MatchLifecycleTests`) gives the
        /// neighboring pair.
        [Test]
        public void Resolved_OutranksMaxDuration_OnTheBoundaryTick()
        {
            const int Limit = 100;
            var policy = new MatchEndPolicy(maxDurationTicks: Limit);

            // Premise, checked for real: this tick genuinely IS the duration
            // boundary — with nobody resolved, the very same call answers
            // MaxDurationReached. Without it the assertion below could pass on
            // a tick the timer never fired at.
            Assert.AreEqual(MatchEndReason.MaxDurationReached,
                policy.Evaluate(Limit, 1, 1, anyExtracted: false),
                "premise: tick == maxDurationTicks must be the boundary the timer fires on");

            Assert.AreEqual(MatchEndReason.AllPlayersResolved,
                policy.Evaluate(Limit, 0, 0, anyExtracted: true),
                "a run everyone finished outranks the clock even when both are true on the same tick");
            Assert.AreEqual(0, MatchEndPolicy.ExitCodeFor(
                    policy.Evaluate(Limit, 0, 0, anyExtracted: true)),
                "…and that is worth an exit code of 0, not the timer's 4");
        }

        [Test]
        public void AllDead_WhenNobodyExtracted()
        {
            var policy = new MatchEndPolicy(maxDurationTicks: 1000);
            Assert.AreEqual(MatchEndReason.AllPlayersDead,
                policy.Evaluate(10, 0, 0, anyExtracted: false));
        }

        [Test]
        public void ResolvedExitCode_IsZero()
            => Assert.AreEqual(0, MatchEndPolicy.ExitCodeFor(MatchEndReason.AllPlayersResolved));

        // --- Stage 3 Т21: the phase machine itself (spec §3.4/§3.5, Р219/Р256/
        // Р299). Everything above this line predates it and stays as it was;
        // everything below drives MatchFlowSystem, the last step of TickAll.

        /// Zoned fixture for the phase machine: Open() keeps the two zone
        /// boundaries (owner decision R-76) and spawns its three players out on
        /// the ring, i.e. in Zone.Outer — so a fixture only ever reaches the
        /// core by SAYING it does, which is what every activation test below
        /// then states explicitly.
        ///
        /// `gateDelayTicks` is a TICK count turned into the seconds the config
        /// actually carries — fixture arithmetic off SimulationWorld.TickDt, so
        /// the tests below can count ticks (10) instead of waiting out the
        /// shipped 90 seconds (2700 ticks) while still exercising the very
        /// conversion the production path performs.
        static SimConfig FlowFixture(int gateDelayTicks = 10)
        {
            var cfg = TestConfigs.Open();
            cfg.Flow.GateDelaySeconds = gateDelayTicks * SimulationWorld.TickDt;
            return cfg;
        }

        static SimulationWorld FlowWorld(in SimConfig cfg) => new SimulationWorld(1, cfg, playerCount: 3);

        /// Idle ticks — the phase machine reads positions and mob liveness, so
        /// no input is needed to advance it.
        static void Idle(SimulationWorld w, int ticks = 1) => TestWorlds.IdleTicks(w, ticks);

        /// Since Т22 the activating transition SPAWNS the Director, so a gate
        /// test has to put him down itself: the countdown starts on the tick
        /// the liveness scan first finds him gone, and "he was never born" —
        /// which is what every gate fixture below used to lean on — is no
        /// longer a way to get there.
        static void KillTheDirector(SimulationWorld w)
        {
            for (int i = 0; i < w.MobCount; i++)
            {
                if (w.Mobs[i].Type != MobType.Director) continue;
                w.DamageMob(i, 1e9f, w.Mobs[i].Pos, HitZone.Body, float2.zero, ownerIndex: 1);
                return;
            }
            Assert.Fail("fixture premise: the activation must have produced a Director to kill");
        }

        /// A point inside the core, stated as fixture arithmetic off the very
        /// boundary Geometry.ZoneOf compares against — never a literal (the
        /// zone radii are data and may be retuned).
        static float2 InsideCore(in SimConfig cfg) => new float2(cfg.Arena.ZoneRadius[0] * 0.5f, 0f);

        /// A point in the OUTER ring, the far side of both boundaries.
        static float2 OutsideZones(in SimConfig cfg)
            => new float2((cfg.Arena.ZoneRadius[1] + cfg.Arena.Radius) * 0.5f, 0f);

        [Test]
        public void EnteringCore_ActivatesDirector()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            Assert.AreEqual(MatchPhase.Farm, w.Match.Phase, "premise: a fresh match farms");

            // Subject is player 1, not player 0 (lesson 227).
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Assert.AreEqual(Zone.Core, Geometry.ZoneOf(w.PlayerAt(1).Pos, in cfg.Arena),
                "premise: the relocation must genuinely put the collector in the core");

            Idle(w);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase);
        }

        [Test]
        public void StayingOutOfCore_NeverActivates()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            // Deliberately deep into the run: the activation has no clock
            // behind it at all (Р299 replaced DirectorActivationSeconds), so
            // "nobody entered" must still read as Farm long after any timer
            // would have fired.
            Idle(w, 300);
            for (int i = 0; i < w.PlayerCount; i++)
            {
                Assert.AreNotEqual(Zone.Core, Geometry.ZoneOf(w.PlayerAt(i).Pos, in cfg.Arena),
                    $"premise: player {i} must have stayed out of the core");
            }
            Assert.AreEqual(MatchPhase.Farm, w.Match.Phase);
        }

        [Test]
        public void ActivationIsIrreversible_AfterLeavingCore()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase, "premise: the latch must have caught");

            TestWorlds.RelocatePlayerForTest(w, 1, OutsideZones(in cfg));
            Idle(w, 5);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase,
                "the latch is one-way (Р299): crossing back out must not un-activate the Director");
        }

        [Test]
        public void DeadPlayerInCore_DoesNotActivate()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            w.DamagePlayer(1, ProjectileIds.NoOwner, cfg.Hero.MaxHp + 1f, w.PlayerAt(1).Pos,
                HitZone.Body, new float2(1f, 0f));
            Assert.IsFalse(w.PlayerAt(1).Alive, "premise: the overkill damage must have killed the collector");

            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w, 5);
            Assert.AreEqual(MatchPhase.Farm, w.Match.Phase,
                "a corpse lying in the core is not a collector who walked in (Р299: LIVE and not extracted)");
        }

        [Test]
        public void ExtractedPlayerInCore_DoesNotActivate()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            PlayerState p = w.PlayerAt(1);
            p.Extracted = true;
            p.Pos = InsideCore(in cfg);
            w.SetPlayerForTest(1, p);
            Assert.IsTrue(w.PlayerAt(1).Alive,
                "premise: Extracted is a separate bit from Alive (Р223) — this fixture needs both set the way " +
                "an extraction leaves them only in the Extracted half, so the Alive gate cannot be what passes");

            Idle(w, 5);
            Assert.AreEqual(MatchPhase.Farm, w.Match.Phase,
                "somebody who already left the raid cannot trigger its endgame (Р299)");
        }

        [Test]
        public void ZonelessArena_NeverActivates()
        {
            // Lesson 315 / R-53: a zoneless arena is a LEGAL input, and
            // Geometry.ZoneOf carries no bounds guard of its own — it indexes
            // ZoneRadius[0]/[1] and throws on such an arena. This is the first
            // battle reader of ZoneOf in Objectives, so the guard belongs to it.
            // OpenField() is exactly that arena (Ф5-0), and its player stands at
            // the very origin an arena WITH zones would call the core.
            var cfg = TestConfigs.OpenField();
            Assert.Less(cfg.Arena.ZoneRadius.Length, 2, "premise: the fixture must really be zoneless");
            var w = FlowWorld(in cfg);
            Assert.AreEqual(float2.zero, w.PlayerAt(0).Pos,
                "premise: the fixture puts its players where a zoned arena would have its core");

            Assert.DoesNotThrow(() => Idle(w, 5),
                "a zoneless arena must not reach Geometry.ZoneOf at all");
            Assert.AreEqual(MatchPhase.Farm, w.Match.Phase, "no zones, no core, no activation");
        }

        [Test]
        public void ActivationSeesThisTicksMovement_NotLastTicks()
        {
            // Р256 п.1, the half of the tick order Т21 can prove on its own:
            // the phase machine runs at the END of the tick, so a collector who
            // walks across the boundary DURING this tick activates on THIS
            // tick, not the next one. (The other half — a channel completing on
            // the activation tick still extracting — needs the channel itself
            // and belongs to Т23.)
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            // Ten ticks of top speed outside the boundary — a run-up, because a
            // collector starting from rest is acceleration-limited and covers
            // far less than MaxSpeed * TickDt on its first tick (the fixture
            // arithmetic this test carried before the RED run assumed
            // otherwise, and the premise caught it).
            float runUp = cfg.Hero.MaxSpeed * SimulationWorld.TickDt * 10f;
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(cfg.Arena.ZoneRadius[0] + runUp, 0f));
            Assert.AreEqual(Zone.Middle, Geometry.ZoneOf(w.PlayerAt(1).Pos, in cfg.Arena),
                "premise: the collector must start OUTSIDE the core");

            var inputs = new SimInput[w.PlayerCount];
            inputs[1] = new SimInput { MoveDir = new float2(-1f, 0f) }; // straight at the center
            bool crossed = false;
            for (int i = 0; i < 200 && !crossed; i++)
            {
                w.TickAll(inputs);
                crossed = Geometry.ZoneOf(w.PlayerAt(1).Pos, in cfg.Arena) == Zone.Core;
                if (!crossed)
                {
                    Assert.AreEqual(MatchPhase.Farm, w.Match.Phase,
                        "the phase must not run AHEAD of the crossing either — nobody is in the core yet");
                }
            }
            Assert.IsTrue(crossed, "premise: the collector must reach the core inside the tick budget");
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase,
                "the phase machine reads POST-movement positions — it is the last step of TickAll (Р256), " +
                "so the tick that carries a collector across the boundary is the tick that activates");
        }

        [Test]
        public void ActivationEvent_GoesToEveryone_WithoutPosition()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            w.ClearEvents();
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.DirectorActivated, out SimEvent ev),
                "activation must announce itself — all three collectors learn the early portals just closed");
            Assert.AreEqual(DeliveryChannel.All, EventRelevance.ChannelFor(SimEventKind.DirectorActivated),
                "spec §3.4/Р28: everyone, like WaveStarted");
            Assert.AreEqual(float2.zero, ev.Pos,
                "…and WITHOUT a position: the All channel carries none, or the event would hand every " +
                "observer the position of whoever walked in");
        }

        [Test]
        public void ActivationEmitsExactlyOnce_NotEveryTick()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            w.ClearEvents();
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w, 5); // the collector stays in the core for every one of them

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.DirectorActivated),
                "the transition fires the event, not the standing condition — a collector camping the core " +
                "must not re-announce the Director on every tick");
        }

        [Test]
        public void DirectorDeath_StampsItsTick_AndAnnouncesToEveryone()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase, "premise: activated");

            // A LIVE Director must keep the death tick at 0 for as long as he
            // stands. Since Т22 the activation itself puts him there (the
            // fixture no longer spawns one by hand — that would now make two),
            // and these ticks are what keep the liveness scan honest: without
            // them it could be deleted outright and the suite would stay green
            // — the shape of lesson 345.
            for (int i = 0; i < 3; i++)
            {
                Idle(w);
                Assert.AreEqual(0, w.Match.DirectorDeathTick,
                    "a Director who still stands has no death tick (SimStates' own contract)");
                Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.DirectorDied),
                    "…and nothing may announce his death while he is alive");
            }

            w.ClearEvents();
            KillTheDirector(w);
            for (int i = 0; i < w.MobCount; i++)
            {
                Assert.AreNotEqual(MobType.Director, w.Mobs[i].Type,
                    "premise: the overkill must have removed the Director (his retinue stays — " +
                    "the scan is about HIM, not about an empty world)");
            }
            Idle(w);

            Assert.AreEqual(w.CurrentTick, w.Match.DirectorDeathTick,
                "the death tick is stamped on the tick the scan first finds him gone — it is the ONLY thing " +
                "the phase state stores about him (Р218/Р219a)");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.DirectorDied, out SimEvent ev));
            Assert.AreEqual(DeliveryChannel.All, EventRelevance.ChannelFor(SimEventKind.DirectorDied));
            Assert.AreEqual(float2.zero, ev.Pos, "…without a position, same as the activation");
        }

        [Test]
        public void GateStaysClosed_UntilTheDelayHasFullyElapsed()
        {
            const int DelayTicks = 10;
            var cfg = FlowFixture(DelayTicks);
            var w = FlowWorld(in cfg);
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w); // activate — and, since Т22, spawn him
            KillTheDirector(w);

            Idle(w);
            int deathTick = w.Match.DirectorDeathTick;
            Assert.AreNotEqual(0, deathTick, "premise: the death tick must be stamped before the countdown means anything");

            // One tick SHORT of the delay — the boundary is >=, so this must
            // still be closed.
            Idle(w, DelayTicks - 1);
            Assert.AreEqual(deathTick + DelayTicks - 1, w.CurrentTick, "premise: arithmetic of the wait itself");
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase,
                "the window of sharing at the corpse is GateDelaySeconds long, not one tick shorter");
        }

        [Test]
        public void GateOpens_OnTheDelayBoundaryTick()
        {
            const int DelayTicks = 10;
            var cfg = FlowFixture(DelayTicks);
            var w = FlowWorld(in cfg);
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w);            // activate (and spawn him, since Т22)
            KillTheDirector(w);
            Idle(w);            // the scan finds him gone and stamps the tick
            int deathTick = w.Match.DirectorDeathTick;
            Assert.AreNotEqual(0, deathTick, "premise: death stamped");

            Idle(w, DelayTicks);
            Assert.AreEqual(deathTick + DelayTicks, w.CurrentTick, "premise: exactly the delay has passed");
            Assert.AreEqual(MatchPhase.GateOpen, w.Match.Phase);
        }

        /// The conversion of GateDelaySeconds into whole ticks, pinned AT THE
        /// SHIPPED NUMBER rather than at a fixture's convenient one — 90
        /// seconds, the value TestConfigs mirrors from the C# defaults and the
        /// `.asset` carries into the game.
        ///
        /// WHY IT EXISTS (Т21, found by a surviving mutant): every other gate
        /// test states its delay as N * TickDt, where the seconds-to-ticks
        /// quotient lands exactly on N and every plausible rounding rule
        /// agrees. 90 seconds does not: 90 / (1/30) computed in floats is not
        /// exactly 2700, so floor and round disagree by a whole tick there and
        /// nothing in the suite could tell them apart. The expected tick count
        /// below is stated by hand — 90 s at 30 ticks per second — and NOT
        /// re-derived from the production formula (lesson 324: a witness must
        /// not take its number from the same home as the code it watches).
        [Test]
        public void GateDelay_AtTheShippedNumber_IsAWholeNumberOfTicks()
        {
            const int ShippedDelayTicks = 2700; // 90 s * 30 ticks/s, by hand
            var cfg = TestConfigs.Open();
            Assert.AreEqual(90f, cfg.Flow.GateDelaySeconds, 1e-6f,
                "premise: this test is about the SHIPPED delay — if the fixture stops mirroring it, " +
                "the number below stops meaning anything");

            var w = FlowWorld(in cfg);
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w);            // activate (and spawn him, since Т22)
            KillTheDirector(w);
            Idle(w);            // the scan finds him gone and stamps the tick
            int deathTick = w.Match.DirectorDeathTick;
            Assert.AreNotEqual(0, deathTick, "premise: death stamped");

            Idle(w, ShippedDelayTicks - 1);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase,
                "one tick short of 2700 the window of sharing is still running");

            Idle(w);
            Assert.AreEqual(MatchPhase.GateOpen, w.Match.Phase,
                "…and at 2700 ticks exactly the gate opens");
        }

        [Test]
        public void GateNeverCloses_OnceOpen()
        {
            const int DelayTicks = 10;
            var cfg = FlowFixture(DelayTicks);
            var w = FlowWorld(in cfg);
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w);
            KillTheDirector(w);
            Idle(w, 1 + DelayTicks);
            Assert.AreEqual(MatchPhase.GateOpen, w.Match.Phase, "premise: the gate must be open first");

            // Everything that could plausibly "undo" it: the collector leaves
            // the core, and a Director walks the arena again.
            TestWorlds.RelocatePlayerForTest(w, 1, OutsideZones(in cfg));
            w.SpawnMobForTest(MobType.Director, float2.zero);
            Idle(w, 20);
            Assert.AreEqual(MatchPhase.GateOpen, w.Match.Phase,
                "the gate stays open to the end of the raid (spec §3.5) — nothing short of Ended moves it");
        }

        [Test]
        public void EndedFreezesTheMachine_NoActivation()
        {
            var cfg = FlowFixture();
            var w = FlowWorld(in cfg);
            w.SetMatchForTest(new MatchState { Phase = MatchPhase.Ended });
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w, 5);
            Assert.AreEqual(MatchPhase.Ended, w.Match.Phase,
                "Ended is checked FIRST (Р256): a raid that is over does not start its endgame");
        }

        [Test]
        public void EndedOutranksGateOpen_OnTheSameTick()
        {
            const int DelayTicks = 10;
            var cfg = FlowFixture(DelayTicks);
            var w = FlowWorld(in cfg);
            TestWorlds.RelocatePlayerForTest(w, 1, InsideCore(in cfg));
            Idle(w);
            KillTheDirector(w);
            Idle(w);
            int deathTick = w.Match.DirectorDeathTick;
            Assert.AreNotEqual(0, deathTick, "premise: death stamped");
            Idle(w, DelayTicks - 1);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase,
                "premise: one tick short of the gate, so the NEXT tick is the one both outcomes want");

            // The raid ends on that very tick (Т24 is what writes Ended in
            // production — MatchEndPolicy lives in Ring.Networking and the
            // simulation cannot see it, coordinator R-172).
            w.SetMatchForTest(new MatchState { Phase = MatchPhase.Ended, DirectorDeathTick = deathTick });
            Idle(w);
            Assert.AreEqual(MatchPhase.Ended, w.Match.Phase,
                "Ended wins the tie (Р256 п.3) — the gate cannot open on a raid that already ended");
        }

        [Test]
        public void EndReasonValues_AreStableOnTheWire()
        {
            Assert.AreEqual(0, (byte)MatchEndReason.None);
            Assert.AreEqual(1, (byte)MatchEndReason.AllPlayersDead);
            Assert.AreEqual(2, (byte)MatchEndReason.MaxDurationReached);
            Assert.AreEqual(3, (byte)MatchEndReason.AllPlayersResolved);
        }
    }
}
