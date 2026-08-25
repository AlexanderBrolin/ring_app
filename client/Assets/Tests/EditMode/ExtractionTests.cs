using NUnit.Framework;
using Ring.Simulation.Core;
using Ring.Simulation.Objectives;
using Ring.Simulation.Visibility;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т23 (spec §3.5 Р221/Р222/Р223): the exits themselves — the two
    /// early portals, the gate at the core, and the channel a collector has to
    /// hold to leave the raid with what he carries.
    ///
    /// THE FIELDS ARE NOT NEW (errata E-1): PlayerState.ExtractTimer and
    /// .ExtractKind were declared in Т1 and hashed at the sanctioned Т6 re-pin,
    /// precisely so this task could add BEHAVIOR without moving either golden.
    /// What arrives here is the behavior and nothing else.
    ///
    /// Fixture: TestConfigs.Open() ships the real exit layout — two portals out
    /// in the outer ring, one in the middle, and the gate at the arena center
    /// (ExtractKind {0,0,0,1}, ExtractRadius 8). Positions are read from the
    /// config, never restated, so an owner retune of the layout moves these
    /// tests with it.
    public class ExtractionTests
    {
        const int Subject = 1; // the SECOND player is the subject (lesson 227)

        // Т24 lifted the exit-layout resolution and the short-channel fixture
        // to TestWorlds the moment a second class (ResultsTests) needed the
        // same four helpers; this file delegates, the same way MatchFlowTests'
        // own Idle now delegates to TestWorlds.IdleTicks (rule 2).
        static float2 EarlyPortalPos(in SimConfig cfg) => TestWorlds.EarlyPortalPos(in cfg);
        static float2 GatePos(in SimConfig cfg) => TestWorlds.GatePos(in cfg);
        static SimConfig Fixture(int channelTicks = 6) => TestWorlds.ExitFixture(channelTicks);

        static SimulationWorld World(in SimConfig cfg) => new SimulationWorld(1, cfg, playerCount: 3);

        static void Stand(SimulationWorld w, int index, float2 pos)
            => TestWorlds.RelocatePlayerForTest(w, index, pos);

        /// A blow through the production path — the only path that runs the
        /// guards this file is about (dead first, i-frames second, and only
        /// then the channel cancel).
        static void Hit(SimulationWorld w, int index, float dmg)
            => w.DamagePlayer(index, ProjectileIds.NoOwner, dmg, w.PlayerAt(index).Pos,
                HitZone.Body, new float2(1f, 0f), hitHeight: 0f);

        [Test]
        public void ChannelGrows_OnlyInsideRadiusOfAnOpenPortal()
        {
            SimConfig cfg = Fixture();
            var w = World(in cfg);
            float2 portal = EarlyPortalPos(in cfg);
            Stand(w, Subject, portal);
            Stand(w, 2, portal + new float2(cfg.Arena.ExtractRadius * 2f, 0f)); // well outside

            TestWorlds.IdleTicks(w, 3);

            Assert.AreEqual(3f * SimulationWorld.TickDt, w.PlayerAt(Subject).ExtractTimer, 1e-5f,
                "standing in an open portal grows the channel by exactly one tick per tick");
            Assert.AreEqual(0f, w.PlayerAt(2).ExtractTimer, 1e-6f,
                "…and standing outside its radius grows nothing at all");
        }

        [Test]
        public void ChannelResetsToZero_OnAppliedDamage()
        {
            SimConfig cfg = Fixture();
            var w = World(in cfg);
            Stand(w, Subject, EarlyPortalPos(in cfg));
            TestWorlds.IdleTicks(w, 3);
            Assert.Greater(w.PlayerAt(Subject).ExtractTimer, 0f, "premise: the channel is running");

            Hit(w, Subject, 5f);

            Assert.AreEqual(0f, w.PlayerAt(Subject).ExtractTimer, 1e-6f,
                "damage RESETS the channel, it does not pause it (С24/Р222) — and it does so through " +
                "the one AbortChannels home, not a second copy of the rule");
        }

        [Test]
        public void ChannelSurvives_IframeAbsorbedHit()
        {
            SimConfig cfg = Fixture();
            var w = World(in cfg);
            Stand(w, Subject, EarlyPortalPos(in cfg));
            TestWorlds.IdleTicks(w, 3);
            float before = w.PlayerAt(Subject).ExtractTimer;
            Assert.Greater(before, 0f, "premise: the channel is running");

            PlayerState p = w.PlayerAt(Subject);
            p.IframeTimer = cfg.Hero.DashIframes;
            w.SetPlayerForTest(Subject, p);
            Hit(w, Subject, 5f);

            Assert.AreEqual(before, w.PlayerAt(Subject).ExtractTimer, 1e-6f,
                "a blow the i-frames swallowed was never APPLIED, so it may not break the channel — " +
                "the same asymmetry by which it earns the shooter no credit (Р127/Р222)");
        }

        [Test]
        public void ChannelResets_WhenSteppingOut()
        {
            SimConfig cfg = Fixture();
            var w = World(in cfg);
            float2 portal = EarlyPortalPos(in cfg);
            Stand(w, Subject, portal);
            TestWorlds.IdleTicks(w, 3);
            Assert.Greater(w.PlayerAt(Subject).ExtractTimer, 0f, "premise: the channel is running");

            Stand(w, Subject, portal + new float2(cfg.Arena.ExtractRadius * 2f, 0f));
            TestWorlds.IdleTicks(w);

            Assert.AreEqual(0f, w.PlayerAt(Subject).ExtractTimer, 1e-6f,
                "stepping out of the radius zeroes the channel — progress is not banked");
        }

        [Test]
        public void Completing_MarksExtracted_LeavesNoCorpse_AndAnnouncesIt()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = Fixture(ChannelTicks);
            var w = World(in cfg);
            Stand(w, Subject, EarlyPortalPos(in cfg));
            int containersBefore = w.ContainerCount;

            TestWorlds.IdleTicks(w, ChannelTicks);

            PlayerState p = w.PlayerAt(Subject);
            Assert.IsTrue(p.Extracted, "the channel completed: the collector is out of the raid");
            Assert.IsFalse(p.Alive, "…and no longer a live body in the arena");
            Assert.AreEqual((byte)1, p.ExtractKind, "…through an EARLY portal (1), not the gate");
            Assert.AreEqual(containersBefore, w.ContainerCount,
                "extraction leaves NO corpse and nothing to loot — that is the whole difference " +
                "between walking out and dying (spec §3.5)");
            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.PlayerDied),
                "…and it is not a death, so nothing may announce one");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerExtracted, out SimEvent ev),
                "…while the extraction itself IS announced");
            Assert.AreEqual(Subject, ev.PlayerIndex, "…naming who left");
            // Ф5 gate, review B-5: EntityId carries the slot too, exactly as
            // PlayerDied does for the same kind of subject. A literal 0 there
            // was indistinguishable from "player 0 left" — and 0 is a legal
            // slot, so the wire consumer of Т29 would have credited every
            // extraction in the raid to the first player. Asserted against
            // the SECOND player for that very reason (lesson 227).
            Assert.AreEqual(Subject, ev.EntityId,
                "…and naming him in EntityId as well, the same convention PlayerDied follows");
        }

        [Test]
        public void GateExit_IsMarkedAsTheGate_NotAsAnEarlyPortal()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = Fixture(ChannelTicks);
            cfg.Flow.GateDelaySeconds = 0f;
            var w = World(in cfg);
            OpenTheGate(w, in cfg);
            Stand(w, Subject, GatePos(in cfg));

            TestWorlds.IdleTicks(w, ChannelTicks);

            Assert.IsTrue(w.PlayerAt(Subject).Extracted, "premise: the gate channel completed");
            Assert.AreEqual((byte)2, w.PlayerAt(Subject).ExtractKind,
                "the gate is a DIFFERENT exit (ExtractedCore), and Т24 pays it differently — " +
                "the two are told apart by the exit's KIND, not by the zone it stands in");
        }

        [Test]
        public void ClosedPortal_NeverGrowsChannel_AndZeroesWhatItHad()
        {
            SimConfig cfg = Fixture();
            var w = World(in cfg);
            float2 portal = EarlyPortalPos(in cfg);
            Stand(w, Subject, portal);
            TestWorlds.IdleTicks(w, 2);
            Assert.Greater(w.PlayerAt(Subject).ExtractTimer, 0f, "premise: the early portal was open");

            // Player 2 walks into the core: the Director wakes and the early
            // portals shut on everybody, including the man already standing in one.
            Stand(w, 2, TestWorlds.InsideCore(in cfg));
            TestWorlds.IdleTicks(w, 2);

            Assert.AreNotEqual(MatchPhase.Farm, w.Match.Phase, "premise: the raid has been activated");
            Assert.AreEqual(0f, w.PlayerAt(Subject).ExtractTimer, 1e-6f,
                "a closed portal grows nothing and keeps nothing — the first man into the core " +
                "locks the door on the other two (Р299), which is the price the decision carries");
            Assert.IsFalse(w.PlayerAt(Subject).Extracted, "…so nobody leaves through it");
        }

        [Test]
        public void GateIsClosed_BeforeTheDirectorDies()
        {
            SimConfig cfg = Fixture();
            var w = World(in cfg);
            Stand(w, 2, TestWorlds.InsideCore(in cfg)); // activate
            TestWorlds.IdleTicks(w);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase, "premise: the Director stands");

            Stand(w, Subject, GatePos(in cfg));
            TestWorlds.IdleTicks(w, 3);

            Assert.AreEqual(0f, w.PlayerAt(Subject).ExtractTimer, 1e-6f,
                "between the activation and the gate opening there is NO way out of the raid at all " +
                "(Р220) — standing on the gate early buys nothing");
        }

        [Test]
        public void ExtractedPlayer_StopsBeingProcessed_ByTheChannel()
        {
            const int ChannelTicks = 6;
            SimConfig cfg = Fixture(ChannelTicks);
            var w = World(in cfg);
            Stand(w, Subject, EarlyPortalPos(in cfg));
            TestWorlds.IdleTicks(w, ChannelTicks);
            Assert.IsTrue(w.PlayerAt(Subject).Extracted, "premise: he is out");

            TestWorlds.IdleTicks(w, 5);

            Assert.AreEqual(0f, w.PlayerAt(Subject).ExtractTimer, 1e-6f,
                "an extracted collector is not in the raid: his channel neither runs nor restarts");
            Assert.IsFalse(w.PlayerAt(Subject).Alive, "…and he does not come back to life on the exit pad");
        }

        [Test]
        public void DeathAbortsTheChannel_ThroughTheSameHome()
        {
            SimConfig cfg = Fixture();
            var w = World(in cfg);
            Stand(w, Subject, EarlyPortalPos(in cfg));
            TestWorlds.IdleTicks(w, 3);
            Assert.Greater(w.PlayerAt(Subject).ExtractTimer, 0f, "premise: the channel is running");

            Hit(w, Subject, cfg.Hero.MaxHp + 1f);

            Assert.AreEqual(0f, w.PlayerAt(Subject).ExtractTimer, 1e-6f,
                "death cancels the channel too — a corpse mid-channel would carry stale state into " +
                "the digest and WorldSave, which is why AbortChannels is the one home for it");
            Assert.IsFalse(w.PlayerAt(Subject).Extracted, "…and dying is not extracting");
        }

        [Test]
        public void HotTweak_ClampsARunningChannel_ToTheNewLength()
        {
            SimConfig cfg = Fixture(channelTicks: 20);
            var w = World(in cfg);
            Stand(w, Subject, EarlyPortalPos(in cfg));
            TestWorlds.IdleTicks(w, 10);
            float before = w.PlayerAt(Subject).ExtractTimer;
            Assert.Greater(before, 0f, "premise: the channel is running");

            SimConfig shorter = cfg;
            shorter.Flow.ExtractChannelSeconds = 3f * SimulationWorld.TickDt;
            w.ApplyConfig(shorter);

            Assert.LessOrEqual(w.PlayerAt(Subject).ExtractTimer, shorter.Flow.ExtractChannelSeconds,
                "a live timer may never exceed the length it is measured against after a hot-tweak — " +
                "the same clamp every other channel timer already gets in ApplyConfig");
        }

        [Test]
        public void PlayerExtracted_IsRoutedLikeADeath_NotDroppedOnTheFloor()
        {
            // EventRelevance.ChannelFor throws on a kind it does not know, and
            // ChannelFor_HandlesEveryKind walks the whole enum — so a new kind
            // that skipped its routing would leave the suite red (R-171's own
            // precedent, Т21). Stated here as well because the CHOICE matters:
            // an extraction is something the other two SEE happen, like a death.
            Assert.AreEqual(DeliveryChannel.Visible,
                EventRelevance.ChannelFor(SimEventKind.PlayerExtracted));
        }

        [Test]
        public void ChannelCompletingOnTheActivationTick_StillGetsOut()
        {
            // Spec §3.5 Р256 п.1, stated as a promise and therefore owed a
            // witness: the extraction channel ticks BEFORE the phase machine,
            // so a collector who fills his last tick on the very tick a
            // companion steps into the core still leaves — the portal shuts
            // from the NEXT tick. This is the one test that pins the ORDER of
            // the two systems inside TickAll; reversed, the door would close
            // retroactively on a man already through it.
            const int ChannelTicks = 6;
            SimConfig cfg = Fixture(ChannelTicks);
            var w = World(in cfg);
            Stand(w, Subject, EarlyPortalPos(in cfg));
            TestWorlds.IdleTicks(w, ChannelTicks - 1);
            Assert.IsFalse(w.PlayerAt(Subject).Extracted, "premise: one tick short of the end");
            Assert.AreEqual(MatchPhase.Farm, w.Match.Phase, "premise: the portals are still open");

            // The other collector walks into the core on this very tick.
            Stand(w, 2, TestWorlds.InsideCore(in cfg));
            TestWorlds.IdleTicks(w);

            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase,
                "premise: the raid activated on this same tick");
            Assert.IsTrue(w.PlayerAt(Subject).Extracted,
                "the man who finished his channel on the activation tick is OUT — the closing of the " +
                "portals takes effect from the next tick, not backwards (Р256 п.1)");
        }

        [Test]
        public void Extraction_LeavesNoRunningTimers_ThroughTheSameHomeDeathUses()
        {
            // Errata E-6/C-I9: extraction and death both call
            // SimulationWorld.ClearCombatTimers, because the reason those
            // fields are cleared is "he is no longer fighting", not "he died".
            // Every one of them is hashed, so a body that left mid-dash or
            // mid-transfer would carry stale state into the digest.
            const int ChannelTicks = 6;
            SimConfig cfg = Fixture(ChannelTicks);
            var w = World(in cfg);
            Stand(w, Subject, EarlyPortalPos(in cfg));

            PlayerState armed = w.PlayerAt(Subject);
            armed.DashTimer = cfg.Hero.DashDuration;
            armed.IframeTimer = cfg.Hero.DashIframes;
            armed.LootTimer = 1f;
            armed.LootTargetContainerId = 7;
            armed.RepairTimer = 1f;
            w.SetPlayerForTest(Subject, armed);

            TestWorlds.IdleTicks(w, ChannelTicks);

            PlayerState p = w.PlayerAt(Subject);
            Assert.IsTrue(p.Extracted, "premise: he is out");
            Assert.AreEqual(0f, p.DashTimer, 1e-6f, "a collector who left the raid is not mid-dash");
            Assert.AreEqual(0f, p.IframeTimer, 1e-6f, "…nor mid-i-frames");
            Assert.AreEqual(0f, p.LootTimer, 1e-6f, "…nor mid-transfer");
            Assert.AreEqual(0, p.LootTargetContainerId, "…and the transfer target goes with the timer");
            Assert.AreEqual(0f, p.RepairTimer, 1e-6f, "…nor mid-repair");
        }

        /// Walks the raid to GateOpen — lifted to TestWorlds by Т24 alongside
        /// the layout helpers above; this file delegates.
        static void OpenTheGate(SimulationWorld w, in SimConfig cfg) => TestWorlds.OpenTheGate(w, in cfg);
    }
}
