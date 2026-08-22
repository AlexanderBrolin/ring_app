using System;
using System.Linq;
using FishNet.Broadcast;
using FishNet.Serializing;
using NUnit.Framework;
using Ring.Networking;
using Ring.Networking.Protocol;
using Ring.Networking.Server;
using Ring.Presentation.Net;
using Ring.Server;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т24 (spec §3.10, errata E-3): how a raid ends for each
    /// collector, and the record that leaves the process.
    ///
    /// NOTHING HERE BUILDS A SECOND RESULT MECHANISM (errata E-3). The raid's
    /// numbers are captured once, by `MatchServer.BuildSummary`, into the
    /// `MatchSummary` that already existed since Stage 2 Task 40; this task
    /// adds four flat arrays to it and reads the rest out of the `MatchStats`
    /// the summary already carries. What this file can test is therefore what
    /// the task actually owns: the PURE decisions behind those arrays —
    /// `MatchEndPolicy.OutcomeFor`, `MatchServer.CreditsCarriedOut`/
    /// `LootCarriedOut`/`SurvivedTicksFor`, `MatchProgress.Observe`,
    /// `MatchServer.EndedNetFor` and the per-player log fragment. The FishNet
    /// wiring that calls them is the half `MatchServer`'s own class doc
    /// declares out of unit-test reach, exactly as for the broadcast loop
    /// beside it.
    ///
    /// THE FIFTH OUTCOME IS THE OWNER'S, AND IT IS LORE, NOT BOOKKEEPING
    /// (owner decision R-194). A collector who is still alive when the raid's
    /// clock runs out has no outcome among the plan's four: he did not die, he
    /// did not extract, and his socket never dropped. In the world, the
    /// factory closes the communication corridor the operator's link to his
    /// shell runs through — the AI has been buying that time with waves since
    /// it detected the intrusion — so the operator loses the shell where it
    /// stands, with everything in its backpack. `Stranded` names that, and it
    /// is why `CreditsCarriedOut` is gated on `Extracted` rather than simply
    /// summing whatever the backpack holds: nothing left the factory.
    public class ResultsTests
    {
        /// The SECOND player is the subject (lesson 227) — a fixture whose
        /// subject is slot 0 cannot tell "the right slot" from "the first
        /// slot".
        const int Subject = 1;

        /// A backpack worth telling apart: three DIFFERENT catalog entries, so
        /// a sum that dropped or doubled one of them is visible, and a total
        /// no single item could produce on its own.
        static readonly byte[] Backpack = { 1, 2, 3 };

        /// The world-scoped half of a summary, which the result fields
        /// have nothing to say about — zeros here keep the fixtures below
        /// about the numbers they actually test.
        static readonly WorldStats Nothing = default;

        static SimulationWorld World(in SimConfig cfg) => new SimulationWorld(1, cfg, playerCount: 3);

        static void Stand(SimulationWorld w, int index, float2 pos)
            => TestWorlds.RelocatePlayerForTest(w, index, pos);

        /// Prices are read from the fixture's OWN catalog, never restated —
        /// `.asset` numbers in a test are a review finding (spec §0), and a
        /// retune of the catalog must move the expectation with it.
        static int Credit(in SimConfig cfg, byte id) => ItemCatalogLookup.Find(id, cfg.Items).CreditValue;

        static int BackpackWorth(in SimConfig cfg)
        {
            int total = 0;
            for (int i = 0; i < Backpack.Length; i++) total += Credit(in cfg, Backpack[i]);
            return total;
        }

        /// Walks the subject out through an early portal with a stocked
        /// backpack — the ordinary "greedy farm" ending of spec §3.5.
        static SimulationWorld ExtractedThroughAPortal(in SimConfig cfg, int channelTicks)
        {
            SimulationWorld w = World(in cfg);
            w.SetInventoryForTest(Subject, Backpack);
            Stand(w, Subject, TestWorlds.EarlyPortalPos(in cfg));
            TestWorlds.IdleTicks(w, channelTicks);
            Assert.IsTrue(w.PlayerAt(Subject).Extracted, "premise: the channel completed and he is out");
            return w;
        }

        // ------------------------------------------------------------------
        // Credits and loot: what actually left the factory
        // ------------------------------------------------------------------

        [Test]
        public void CreditsSumOverInventory()
        {
            SimConfig cfg = TestWorlds.ExitFixture();
            SimulationWorld w = World(in cfg);
            w.SetInventoryForTest(Subject, Backpack);

            int expected = BackpackWorth(in cfg);
            Assert.Greater(expected, 0, "premise: the fixture's catalog prices these three above zero");
            Assert.AreEqual(expected, w.InventoryCreditsOf(Subject),
                "the backpack is worth the sum of its items' own catalog prices — resolved through " +
                "ItemCatalogLookup, the ONE home of id -> record (R-89), not a second search");
            Assert.AreEqual(0, w.InventoryCreditsOf(2),
                "…and an empty backpack is worth nothing, without a special case saying so");
        }

        [Test]
        public void CreditsCarriedOut_AreZero_ForACollectorWhoNeverGotOut()
        {
            SimConfig cfg = TestWorlds.ExitFixture();
            SimulationWorld w = World(in cfg);
            w.SetInventoryForTest(Subject, Backpack);

            Assert.Greater(w.InventoryCreditsOf(Subject), 0, "premise: the backpack is not empty");
            Assert.AreEqual(0, MatchServer.CreditsCarriedOut(w, Subject),
                "a backpack still standing inside the factory is worth nothing to the operator — " +
                "spec §3.10 counts what was CARRIED OUT, and nothing was");
        }

        [Test]
        public void CreditsCarriedOut_AreTheBackpack_ForAnExtractedCollector()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = TestWorlds.ExitFixture(ChannelTicks);
            SimulationWorld w = ExtractedThroughAPortal(in cfg, ChannelTicks);

            Assert.AreEqual(BackpackWorth(in cfg), MatchServer.CreditsCarriedOut(w, Subject),
                "a collector who walked out is credited with exactly what he walked out with");
        }

        [Test]
        public void LootCarriedOut_IsTheBackpack_ForAnExtractedCollector_AndEmptyForEveryoneElse()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = TestWorlds.ExitFixture(ChannelTicks);
            SimulationWorld w = ExtractedThroughAPortal(in cfg, ChannelTicks);
            w.SetInventoryForTest(2, Backpack);

            Assert.AreEqual(Backpack, MatchServer.LootCarriedOut(w, Subject),
                "the record names the items themselves, in the order they were carried — the tier " +
                "and the price are the catalog's to answer, not a second copy traveling beside them");
            Assert.IsEmpty(MatchServer.LootCarriedOut(w, 2),
                "…while a collector still inside the factory carried nothing out, however full his back is");
        }

        // ------------------------------------------------------------------
        // The outcome itself
        // ------------------------------------------------------------------

        [Test]
        public void OutcomeIsExtractedEarly_ForPortalExit()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = TestWorlds.ExitFixture(ChannelTicks);
            SimulationWorld w = ExtractedThroughAPortal(in cfg, ChannelTicks);
            PlayerState p = w.PlayerAt(Subject);

            Assert.AreEqual(MatchOutcome.ExtractedEarly,
                MatchEndPolicy.OutcomeFor(p.Alive, p.Extracted, p.ExtractKind, disconnectKilled: false),
                "an early portal is ExtractedEarly — and the two encodings are joined HERE, by a " +
                "world that actually ran, so PlayerState.ExtractKind's own 1/2 can never drift " +
                "away from this enum silently");
        }

        [Test]
        public void OutcomeIsExtractedCore_ForGateExit()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = TestWorlds.ExitFixture(ChannelTicks);
            cfg.Flow.GateDelaySeconds = 0f;
            SimulationWorld w = World(in cfg);
            TestWorlds.OpenTheGate(w, in cfg);
            Stand(w, Subject, TestWorlds.GatePos(in cfg));
            TestWorlds.IdleTicks(w, ChannelTicks);
            PlayerState p = w.PlayerAt(Subject);
            Assert.IsTrue(p.Extracted, "premise: the gate channel completed");

            Assert.AreEqual(MatchOutcome.ExtractedCore,
                MatchEndPolicy.OutcomeFor(p.Alive, p.Extracted, p.ExtractKind, disconnectKilled: false),
                "the gate pays differently from an early portal, and the two are told apart by the " +
                "exit's KIND rather than by the zone it happens to stand in (plan Т24)");
        }

        [Test]
        public void OutcomeIsDied_ForCorpse()
        {
            SimConfig cfg = TestWorlds.ExitFixture();
            SimulationWorld w = World(in cfg);
            w.DamagePlayer(Subject, ProjectileIds.NoOwner, cfg.Hero.MaxHp + 1f,
                w.PlayerAt(Subject).Pos, HitZone.Body, new float2(1f, 0f));
            PlayerState p = w.PlayerAt(Subject);
            Assert.IsFalse(p.Alive, "premise: he is a corpse");
            Assert.IsFalse(p.Extracted, "premise: dying is not extracting");

            Assert.AreEqual(MatchOutcome.Died,
                MatchEndPolicy.OutcomeFor(p.Alive, p.Extracted, p.ExtractKind, disconnectKilled: false),
                "a body that fell in the arena died, and the backpack it dropped is somebody else's now");
        }

        [Test]
        public void DisconnectedIsDistinctFromDied()
        {
            SimConfig cfg = TestWorlds.ExitFixture();
            SimulationWorld w = World(in cfg);
            w.KillPlayerNoDamage(Subject);
            PlayerState p = w.PlayerAt(Subject);
            Assert.IsFalse(p.Alive, "premise: the disconnect killed him (Р271) — the corpse stays and loots");

            Assert.AreEqual(MatchOutcome.Disconnected,
                MatchEndPolicy.OutcomeFor(p.Alive, p.Extracted, p.ExtractKind, disconnectKilled: true),
                "the world cannot tell these two apart — both are corpses, and KillPlayerNoDamage is " +
                "the same seam — so WHO ended the raid is the server's own memory, not a state field");
            Assert.AreEqual(MatchOutcome.Died,
                MatchEndPolicy.OutcomeFor(p.Alive, p.Extracted, p.ExtractKind, disconnectKilled: false),
                "…and that memory is the ONLY difference: the distinction is diagnostic (Р271), so a " +
                "corpse nobody's connection abandoned is an ordinary death");
        }

        [Test]
        public void OutcomeIsStranded_WhenTheCommunicationWindowCloses()
        {
            SimConfig cfg = TestWorlds.ExitFixture();
            SimulationWorld w = World(in cfg);
            PlayerState p = w.PlayerAt(Subject);
            Assert.IsTrue(p.Alive, "premise: he is alive and well, standing in the arena");
            Assert.IsFalse(p.Extracted, "premise: …and he never took an exit");

            Assert.AreEqual(MatchOutcome.Stranded,
                MatchEndPolicy.OutcomeFor(p.Alive, p.Extracted, p.ExtractKind, disconnectKilled: false),
                "MaxDurationReached is the ONE end that leaves live bodies in the arena, and the " +
                "record must not call them dead: the factory closed the corridor the operator's link " +
                "runs through, so the shell is lost where it stands (owner decision R-194)");
        }

        [Test]
        public void MatchOutcome_ValuesAreStableOnTheWire()
        {
            // Pinned literals, not a re-derivation of the enum: MatchEndedNet
            // carries this as a single byte, so reordering the members would
            // silently change the meaning of a result already in flight
            // between builds compiled from different sources — the same
            // discipline MatchEndReason and HandshakeRefusal already carry.
            // Stranded is APPENDED, exactly as Т1 appended AllPlayersResolved.
            Assert.AreEqual(0, (byte)MatchOutcome.Died);
            Assert.AreEqual(1, (byte)MatchOutcome.ExtractedEarly);
            Assert.AreEqual(2, (byte)MatchOutcome.ExtractedCore);
            Assert.AreEqual(3, (byte)MatchOutcome.Disconnected);
            Assert.AreEqual(4, (byte)MatchOutcome.Stranded);
        }

        // ------------------------------------------------------------------
        // How long the raid lasted for one collector
        // ------------------------------------------------------------------

        [Test]
        public void SurvivedTicks_StopAtTheExtraction_NotAtTheEndOfTheRaid()
        {
            Assert.AreEqual(300, MatchServer.SurvivedTicksFor(alive: false, extracted: true,
                    extractedTick: 300, deathTick: 0, finalTick: 27000),
                "a collector who walked out on the tenth second did not survive the whole raid — the " +
                "other two kept playing for another quarter of an hour, and the record may not " +
                "credit him with it");
        }

        [Test]
        public void SurvivedTicks_StopAtTheDeath_ForACorpse()
        {
            Assert.AreEqual(120, MatchServer.SurvivedTicksFor(alive: false, extracted: false,
                    extractedTick: 0, deathTick: 120, finalTick: 27000),
                "a corpse survived until it fell — DeathTick is already the world's own answer, and " +
                "reading it here is why no second field records the same moment (Р151)");
        }

        [Test]
        public void SurvivedTicks_RunToTheEnd_ForACollectorStillStanding()
        {
            Assert.AreEqual(27000, MatchServer.SurvivedTicksFor(alive: true, extracted: false,
                    extractedTick: 0, deathTick: 0, finalTick: 27000),
                "a man still on his feet when the corridor closed survived the entire raid — the " +
                "only outcome whose length is the raid's own");
        }

        // ------------------------------------------------------------------
        // The server's own per-slot memory of when a collector left
        // ------------------------------------------------------------------

        [Test]
        public void Observe_StampsTheTickACollectorLeft_AndNeverRestampsIt()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = TestWorlds.ExitFixture(ChannelTicks);
            SimulationWorld w = World(in cfg);
            Stand(w, Subject, TestWorlds.EarlyPortalPos(in cfg));
            var extractedTick = new int[w.PlayerCount];

            // Ticked exactly as MatchServer does it: step the world, then
            // observe what that step left behind.
            for (int t = 0; t < ChannelTicks + 4; t++)
            {
                TestWorlds.IdleTicks(w);
                MatchProgress.Observe(w, w.CurrentTick, extractedTick, out _, out _, out _);
            }

            Assert.IsTrue(w.PlayerAt(Subject).Extracted, "premise: he left partway through the window");
            Assert.AreEqual(ChannelTicks, extractedTick[Subject],
                "the tick he left is stamped ON that tick and never moved again — without it his " +
                "survived time would be the whole raid's, since extraction (unlike death) stamps " +
                "nothing in the world's own MatchStats");
            Assert.AreEqual(0, extractedTick[2],
                "…and a collector who never left is never stamped at all");
        }

        [Test]
        public void Observe_ReportsWhoIsLeftInTheRaid()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = TestWorlds.ExitFixture(ChannelTicks);
            SimulationWorld w = World(in cfg);
            Stand(w, Subject, TestWorlds.EarlyPortalPos(in cfg));
            var extractedTick = new int[w.PlayerCount];
            TestWorlds.IdleTicks(w, ChannelTicks);

            MatchProgress.Observe(w, w.CurrentTick, extractedTick,
                out int alive, out int active, out bool anyExtracted);

            PlayerState gone = w.PlayerAt(Subject);
            Assert.IsFalse(gone.Alive && gone.Extracted,
                "premise, and the invariant spec §3.5 states: a collector cannot be alive AND " +
                "extracted — leaving takes the body out of the arena");

            Assert.AreEqual(2, alive, "the man who walked out is no longer a live body in the arena");
            Assert.AreEqual(2, active,
                "…and 'active' — alive and not yet extracted — is therefore the same two. The two " +
                "counts agree BECAUSE of the invariant above, not by coincidence: what actually " +
                "separates a wipe from a resolved raid is the flag below");
            Assert.IsTrue(anyExtracted,
                "…SOMEBODY got out, and that is what turns AllPlayersDead into AllPlayersResolved " +
                "(Р223) once the arena empties");
        }

        // ------------------------------------------------------------------
        // The one production writer of MatchPhase.Ended
        // ------------------------------------------------------------------

        [Test]
        public void MarkMatchEnded_EndsTheRaid_AndFreezesTheMachine()
        {
            SimConfig cfg = TestWorlds.ExitFixture();
            SimulationWorld w = World(in cfg);
            Assert.AreNotEqual(MatchPhase.Ended, w.Match.Phase, "premise: the raid is running");

            w.MarkMatchEnded();

            Assert.AreEqual(MatchPhase.Ended, w.Match.Phase,
                "the end of a raid is MatchEndPolicy's decision and it lives in Ring.Networking, " +
                "which the simulation neither sees nor can — so the ONE production writer of Ended " +
                "is this seam, and Т24 is its only caller (coordinator R-172)");

            // Everything the machine would otherwise do, offered to it at once.
            Stand(w, Subject, TestWorlds.InsideCore(in cfg));
            TestWorlds.IdleTicks(w, 3);
            Assert.AreEqual(MatchPhase.Ended, w.Match.Phase,
                "…and a raid that is over does not start its endgame, whatever the arena looks like");
        }

        // ------------------------------------------------------------------
        // What leaves the process
        // ------------------------------------------------------------------

        [Test]
        public void EndedNetFor_CopiesEveryResultField()
        {
            // Every value DISTINCT, for the reason EndedNetFor_CopiesEveryStat
            // states: a run of same-typed assignments is exactly the shape
            // where a swapped pair compiles and misreports for good.
            var mine = new MatchStats { Kills = 11, AmmoSpent = 41, CellsPicked = 42 };
            var other = new MatchStats { Kills = 91, AmmoSpent = 92, CellsPicked = 93 };
            byte[] myLoot = { 3, 4 };
            var summary = new MatchSummary(MatchEndReason.AllPlayersResolved, epoch: 7,
                finalTick: 4242, in Nothing, droppedEvents: 31,
                new[] { other, mine }, new[] { new NetStats(), new NetStats() },
                new[] { MatchOutcome.Died, MatchOutcome.ExtractedCore },
                new[] { 51, 52 },
                new[] { new byte[0], myLoot },
                new[] { 61, 62 });

            MatchEndedNet net = MatchServer.EndedNetFor(in summary, slot: 1);

            Assert.AreEqual((byte)MatchOutcome.ExtractedCore, net.Outcome, "Outcome");
            Assert.AreEqual(52, net.CreditsTotal, "CreditsTotal");
            Assert.AreEqual(myLoot, net.Loot, "Loot");
            Assert.AreEqual(41, net.AmmoSpent, "AmmoSpent — read out of MatchStats, not out of a second array");
            Assert.AreEqual(42, net.CellsPicked, "CellsPicked — likewise");
            Assert.AreEqual(62, net.SurvivedSeconds, "SurvivedSeconds");
        }


        [Test]
        public void LogLine_ContainsEveryContractField()
        {
            SimConfig cfg = TestWorlds.ExitFixture();
            var stats = new MatchStats
            {
                Kills = 11, HeadshotKills = 12, ShotsFired = 13, ShotsHit = 14,
                DashesUsed = 15, SlidesUsed = 16, DeathTick = 17, DamageTaken = 18.5f,
                AmmoSpent = 19, CellsPicked = 20,
            };

            string line = MatchSummaryLog.PlayerLine(slot: 1, playerId: "dev-1a2b3c4d",
                MatchOutcome.ExtractedCore, in stats, survivedSeconds: 21, creditsTotal: 22,
                Backpack, cfg.Items);

            // Spec §3.10's record, field by field — this line IS the future
            // `match_players` row, so every column it promises has to be on it.
            // The KEYS are pinned as literals (an operator greps them and the
            // panel app-7ss will parse them, exactly the HandshakeLog
            // argument); the outcome NAME is derived from the enum, so no
            // second mapping table can drift away from it.
            Assert.That(line, Does.Contain("player[1]"));
            Assert.That(line, Does.Contain("playerId=dev-1a2b3c4d"));
            Assert.That(line, Does.Contain("result=" + MatchOutcome.ExtractedCore));
            Assert.That(line, Does.Contain("kills=11"));
            Assert.That(line, Does.Contain("headshotKills=12"));
            Assert.That(line, Does.Contain("shotsFired=13"));
            Assert.That(line, Does.Contain("shotsHit=14"));
            Assert.That(line, Does.Contain("dashesUsed=15"));
            Assert.That(line, Does.Contain("slidesUsed=16"));
            Assert.That(line, Does.Contain("deathTick=17"));
            Assert.That(line, Does.Contain("damageTaken=18.5"));
            Assert.That(line, Does.Contain("ammoSpent=19"));
            Assert.That(line, Does.Contain("cellsPicked=20"));
            Assert.That(line, Does.Contain("survivedSeconds=21"));
            Assert.That(line, Does.Contain("creditsTotal=22"));

            // The loot rides as id:tier:credit triples, so the reader needs no
            // catalog of its own to make sense of the row.
            Assert.That(line, Does.Contain("loot=[1:1:" + Credit(in cfg, 1)
                + "|2:2:" + Credit(in cfg, 2) + "|3:3:" + Credit(in cfg, 3) + "]"));
        }

        [Test]
        public void LogLine_SaysEmptyRatherThanNothing_WhenNobodyCarriedAnythingOut()
        {
            SimConfig cfg = TestWorlds.ExitFixture();
            var stats = new MatchStats();

            string line = MatchSummaryLog.PlayerLine(slot: 0, playerId: "p0",
                MatchOutcome.Stranded, in stats, survivedSeconds: 900, creditsTotal: 0,
                new byte[0], cfg.Items);

            Assert.That(line, Does.Contain("loot=[]"),
                "an empty hold is a MEASURED fact and says so — the dash this project reserves for " +
                "a number nobody measured (app-mi4's bytesUp) would be a different claim entirely");
            Assert.That(line, Does.Contain("result=" + MatchOutcome.Stranded));
        }

        // ------------------------------------------------------------------
        // Stage 3 Т34 (spec §3.10/§3.11, Р270): the PUBLIC scoreboard
        //
        // TWO MESSAGES, AND THE SECOND IS A SUBSET RATHER THAN A COPY.
        // `MatchEndedNet` above is personal and stays personal — accuracy,
        // damage taken, shots and kills are built per connection out of that
        // connection's own slot. `MatchResultsNet` goes to EVERYONE, so it may
        // carry only what a raid is entitled to know about its members: how
        // each one's raid ended and what he walked out with. The tests below
        // are what stops the public message quietly growing into the private
        // one.
        // ------------------------------------------------------------------

        /// Three seats with a DISTINCT number in every column, the same rule
        /// `EndedNetFor_CopiesEveryResultField` follows: a builder that swapped
        /// two same-typed arrays would round-trip perfectly and misreport a
        /// player's raid for good.
        static MatchSummary ThreeSeatSummary()
        {
            var stats = new MatchStats[3];
            for (int i = 0; i < stats.Length; i++)
                stats[i] = new MatchStats { Kills = 10 + i, AmmoSpent = 60 + i, CellsPicked = 70 + i };

            return new MatchSummary(MatchEndReason.MaxDurationReached, epoch: 7,
                finalTick: 4321, in Nothing, droppedEvents: 0, stats,
                new[] { new NetStats(), new NetStats(), new NetStats() },
                new[] { MatchOutcome.ExtractedEarly, MatchOutcome.Died, MatchOutcome.Stranded },
                new[] { 111, 222, 333 },
                new[] { new byte[] { 1, 2 }, new byte[0], new byte[0] },
                new[] { 81, 82, 83 });
        }

        [Test]
        public void ResultsNet_IsABroadcastStruct()
        {
            // `IBroadcast` is an empty marker and `Broadcast<T>` is constrained
            // to structs, so a class here compiles and fails only at the send.
            Assert.IsTrue(typeof(MatchResultsNet).IsValueType,
                "MatchResultsNet must be a struct — FishNet's Broadcast<T> takes structs.");
            Assert.IsTrue(typeof(IBroadcast).IsAssignableFrom(typeof(MatchResultsNet)),
                "MatchResultsNet must implement IBroadcast.");
        }

        [Test]
        public void ResultsNet_SurvivesTheFishNetWireRoundTrip()
        {
            TestSerializers.EnsureRegistered();
            MatchResultsNet source = MatchServer.ResultsNetFrom(ThreeSeatSummary());

            Assert.IsNotNull(GenericWriter<MatchResultsNet>.Write,
                "FishNet's codegen must have produced a writer — without one the scoreboard "
                + "never leaves the server");
            Assert.IsNotNull(GenericReader<MatchResultsNet>.Read, "…and a matching reader");

            var writer = new Writer();
            writer.Write(source);
            var reader = new Reader(writer.GetArraySegment(), null);
            MatchResultsNet back = reader.Read<MatchResultsNet>();

            Assert.AreEqual(source.MatchEpoch, back.MatchEpoch, "MatchEpoch");
            Assert.AreEqual(source.Reason, back.Reason, "Reason");
            Assert.AreEqual(source.FinalTick, back.FinalTick, "FinalTick");
            CollectionAssert.AreEqual(source.Outcome, back.Outcome,
                "every seat's ending, in seat order");
            CollectionAssert.AreEqual(source.CreditsTotal, back.CreditsTotal,
                "every seat's credits, in the same seat order");
        }

        [Test]
        public void PublicSubsetCarriesNoAccuracy()
        {
            // NAMED BY THE PROPERTY THEY SHARE, NOT ONE BY ONE. Р270's rule is
            // "what a shot was worth stays private", and the way to hold it is
            // to FORBID the fields rather than to enumerate today's message: a
            // counter added to `MatchEndedNet` next year is caught by this list
            // the moment somebody copies it across.
            string[] forbidden =
            {
                "Kills", "HeadshotKills", "ShotsFired", "ShotsHit", "DamageTaken",
                "DashesUsed", "SlidesUsed", "DeathTick", "AmmoSpent", "CellsPicked", "Loot",
            };
            string[] present = typeof(MatchResultsNet).GetFields().Select(f => f.Name).ToArray();

            foreach (string field in forbidden)
            {
                CollectionAssert.DoesNotContain(present, field,
                    $"{field} is private to a collector — the public board may not carry it");
            }
        }

        [Test]
        public void PublicSubsetCarriesTheThreeThingsItOwes()
        {
            // The positive witness beside the negative one above (lesson 129):
            // without it, "carry nothing at all" would satisfy the forbidding
            // test perfectly.
            string[] present = typeof(MatchResultsNet).GetFields().Select(f => f.Name).ToArray();
            CollectionAssert.Contains(present, "Outcome", "how each raid ended is public");
            CollectionAssert.Contains(present, "CreditsTotal",
                "what each collector carried out is public");
            CollectionAssert.Contains(present, "MatchEpoch",
                "the epoch, or a client cannot discard a board from a match it has left");
        }

        [Test]
        public void ResultsNetFrom_CopiesEverySeatInSeatOrder()
        {
            MatchSummary summary = ThreeSeatSummary();
            MatchResultsNet results = MatchServer.ResultsNetFrom(summary);

            Assert.AreEqual(3, results.Outcome.Length,
                "one entry per seat, and the seat IS the index — a slot field beside it could disagree");
            Assert.AreEqual(3, results.CreditsTotal.Length);
            for (int slot = 0; slot < 3; slot++)
            {
                Assert.AreEqual((byte)summary.Outcome[slot], results.Outcome[slot],
                    $"seat {slot}'s ending");
                Assert.AreEqual(summary.CreditsTotal[slot], results.CreditsTotal[slot],
                    $"seat {slot}'s credits");
            }

            Assert.AreEqual(7, results.MatchEpoch, "MatchEpoch");
            Assert.AreEqual(4321u, results.FinalTick, "FinalTick");
            Assert.AreEqual((byte)MatchEndReason.MaxDurationReached, results.Reason, "Reason");
        }

        [Test]
        public void BothMessagesComeFromTheSameSummary()
        {
            // Errata E-3: one source of truth for the raid's numbers. A seat's
            // credits must read the same whether it learns them from its own
            // personal message or off the public board — two builders reading
            // two places is exactly how those answers start to differ.
            MatchSummary summary = ThreeSeatSummary();
            MatchResultsNet results = MatchServer.ResultsNetFrom(summary);

            for (int slot = 0; slot < 3; slot++)
            {
                MatchEndedNet personal = MatchServer.EndedNetFor(in summary, slot);
                Assert.AreEqual(personal.CreditsTotal, results.CreditsTotal[slot],
                    $"seat {slot}: the personal message and the board must agree on credits");
                Assert.AreEqual(personal.Outcome, results.Outcome[slot],
                    $"seat {slot}: …and on how the raid ended");
                Assert.AreEqual(personal.MatchEpoch, results.MatchEpoch,
                    $"seat {slot}: …and on which match this was");
            }
        }

        // ------------------------------------------------------------------
        // The board itself: the crossing from the wire's byte to the screen's
        // word, done once on the side of Р180 that may see both.
        // ------------------------------------------------------------------

        [Test]
        public void EveryOutcomeOwnsAWordOnTheBoard()
        {
            // Reflective over the domain: a sixth outcome added later is named
            // by this test rather than printed as the fifth one's word.
            foreach (MatchOutcome outcome in Enum.GetValues(typeof(MatchOutcome)))
            {
                Assert.IsNotEmpty(MatchResultsBoard.WordFor(outcome),
                    $"{outcome} has no word on the board");
            }
        }

        [Test]
        public void EveryOutcomeWordIsDistinct()
        {
            // Two endings sharing a word would be a board that cannot be read:
            // a collector who was cut off and one who stayed behind lost their
            // raid in different ways, and only one of them may come back for
            // the pack.
            string[] words = Enum.GetValues(typeof(MatchOutcome))
                .Cast<MatchOutcome>()
                .Select(MatchResultsBoard.WordFor)
                .ToArray();
            CollectionAssert.AllItemsAreUnique(words);
        }

        [Test]
        public void UnknownOutcome_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MatchResultsBoard.WordFor((MatchOutcome)99),
                "an outcome the table does not know must say so, not borrow a neighbor's word");
        }

        [Test]
        public void Board_HasALinePerSeat_AndMarksThisClientsOwn()
        {
            MatchResultsNet results = MatchServer.ResultsNetFrom(ThreeSeatSummary());

            string board = MatchResultsBoard.Format(in results, localSlot: 1);
            string[] lines = board.Split('\n');

            Assert.AreEqual(3, lines.Length, "one line per seat");
            // The seat number a human reads is one-based — a place at a table,
            // not an offset.
            Assert.That(lines[0], Does.Contain("СБОРЩИК 1"));
            Assert.That(lines[1], Does.Contain("СБОРЩИК 2"));
            Assert.That(lines[2], Does.Contain("СБОРЩИК 3"));

            Assert.That(lines[1], Does.StartWith("▶"), "the local seat is marked");
            Assert.That(lines[0], Does.Not.StartWith("▶"), "…and nobody else's is");
            Assert.That(lines[2], Does.Not.StartWith("▶"));
        }

        [Test]
        public void Board_CarriesTheOutcomeAndTheCreditsOfEachSeat()
        {
            MatchSummary summary = ThreeSeatSummary();
            MatchResultsNet results = MatchServer.ResultsNetFrom(summary);

            string[] lines = MatchResultsBoard.Format(in results, localSlot: 0).Split('\n');
            for (int slot = 0; slot < 3; slot++)
            {
                Assert.That(lines[slot],
                    Does.Contain(MatchResultsBoard.WordFor(summary.Outcome[slot])),
                    $"seat {slot}: its own ending");
                Assert.That(lines[slot], Does.Contain(summary.CreditsTotal[slot].ToString()),
                    $"seat {slot}: its own credits");
            }
        }

        [Test]
        public void Board_IsNullBeforeAnyResultsHaveArrived()
        {
            // `null` rather than "", because the screen has to tell "the raid
            // has not ended" from "the raid ended with nobody in it".
            Assert.IsNull(MatchResultsBoard.Format(default, localSlot: 0));
        }

        [Test]
        public void Board_DrawsTheSeatsBothArraysDescribe_RatherThanThrowing()
        {
            // A decoder that never throws (Р82) can hand this a message whose
            // two arrays disagree. Taking the shorter is the honest answer for
            // a board that lost bytes; throwing would take the whole screen
            // away over a cosmetic disagreement.
            var ragged = new MatchResultsNet
            {
                Outcome = new[] { (byte)MatchOutcome.Died, (byte)MatchOutcome.Stranded },
                CreditsTotal = new[] { 5 },
            };

            string board = MatchResultsBoard.Format(in ragged, localSlot: 0);
            Assert.AreEqual(1, board.Split('\n').Length,
                "only the seat both arrays describe is drawn");
        }
    }
}
