using System.Collections.Generic;
using NUnit.Framework;
using Ring.Simulation.Core;
using Ring.Simulation.Visibility;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 21 (spec §3.7, Р28): server-side event DELIVERY rules — for
    /// a given SimEvent and observer, is it delivered at all, and with what
    /// (possibly coarsened) position. Unlike VisibilitySystem.Compute,
    /// EventRelevance.ShouldDeliver never computes its own visibility — it is a
    /// pure function of its explicit arguments, trusting the caller's
    /// `observerSet` as a fully-formed input (task-21-brief.md rule 1: the
    /// predicate is `observerSet.Contains(id)`, together with linger, never a
    /// re-derived distance check). Every fixture below therefore builds the
    /// exact VisibilitySet it wants to test against — either by hand via
    /// VisibilitySet.Add, or by driving a real VisibilitySystem.Compute call
    /// first when the fixture's whole point is a Compute-level property (the
    /// hysteresis band).
    ///
    /// carryover-t21.md's two traps this file exists to pin: (1) MobDied's
    /// subject is the MOB's own EntityId, never the killer riding along on
    /// PlayerIndex (the ATTACKER convention, §5 of SimEvent.PlayerIndex's own
    /// doc) — MobDied_UsesMobIdentity_NotKillerIdentity; (2) the exact-vs-
    /// coarse position decision for an Audible-channel event is
    /// `Contains(id)`, INCLUDING a merely-lingering entity, not
    /// `LingerOf(id) == 0` — AudiblePos_ExactForVisible's own "half 1".
    public class EventDeliveryTests
    {
        static int Capacity(in SimConfig cfg) => cfg.Arena.MaxMobs + cfg.Arena.MaxPlayers;

        // --- 1: StaminaDenied_OnlyToOwner ---

        [Test]
        public void StaminaDenied_OnlyToOwner()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);

            // Deliberately NOT grid-aligned (default HearPositionGridMeters is
            // 3): a mutant that quantized this "private feedback" position
            // instead of forwarding it verbatim is caught by the exact-value
            // assertion below.
            var ev = new SimEvent { Kind = SimEventKind.StaminaDenied, PlayerIndex = 1, Pos = new float2(12.37f, -7.51f) };

            // Witness set (task-21-brief.md discipline): contains the OWNER's
            // own synthetic id but NOT the neighbour's — the OPPOSITE of what a
            // Visible/Audible-channel gate would need to deliver to the owner
            // and withhold from the neighbour. A mutant that (incorrectly)
            // routed StaminaDenied through the Visible/Audible machinery
            // instead of a plain ownership check would pass the owner
            // assertion below by coincidence but fail the neighbour one, since
            // Visible/Audible never consult `observerIndex` at all.
            var observerSet = new VisibilitySet(Capacity(cfg));
            observerSet.Add(VisibilityIds.ForPlayer(1));

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 1, w, observerSet, cfg.Visibility, out float2 ownerPos),
                "the event's own owner must receive it");
            Assert.AreEqual(ev.Pos, ownerPos,
                "StaminaDenied is private feedback: the delivered position is the raw, EXACT one, never quantized");

            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, w, observerSet, cfg.Visibility, out _),
                "a neighbour must not receive another player's StaminaDenied — it would leak Stamina economy");
        }

        // --- 2: WaveEvents_ToAllWithoutPosition ---

        [Test]
        public void WaveEvents_ToAllWithoutPosition()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            // Empty for every observer: All-channel delivery must not consult
            // visibility at all — an implementation that (incorrectly) gated
            // on it would find nothing here and wrongly withhold delivery.
            var emptySet = new VisibilitySet(Capacity(cfg));

            foreach (SimEventKind kind in new[] { SimEventKind.WaveStarted, SimEventKind.WaveCleared })
            {
                // Nonzero and off-centre, mirroring WaveSystem's own
                // "nearest-alive-player" event position (spec Р28: "сегодня
                // позиция берётся у нулевого игрока" — delivering it would
                // leak that player's location to everyone).
                var ev = new SimEvent { Kind = kind, Pos = new float2(9.5f, -3.25f), EntityId = 4 };

                for (int observerIndex = 0; observerIndex < w.PlayerCount; observerIndex++)
                {
                    Assert.IsTrue(EventRelevance.ShouldDeliver(ev, observerIndex, w, emptySet, cfg.Visibility, out float2 pos),
                        $"{kind} must reach every observer, regardless of visibility");
                    Assert.AreEqual(float2.zero, pos, $"{kind} must never leak its position (spec Р28 forbids it explicitly)");
                }
            }
        }

        // --- 3: OwnDeath_AlwaysDelivered ---

        [Test]
        public void OwnDeath_AlwaysDelivered()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);

            var ev = new SimEvent
            {
                Kind = SimEventKind.PlayerDied,
                PlayerIndex = 1, // VICTIM convention (SimEvent.PlayerIndex's own doc)
                EntityId = 1,
                Pos = new float2(-4f, 21f)
            };

            // The gate an ordinary Visible-channel event would apply: the
            // victim's own synthetic id is absent — as if the victim died
            // behind a wall from every observer's point of view, itself
            // included (task-21-brief.md's own "мёртвый за стеной" phrasing).
            var observerSet = new VisibilitySet(Capacity(cfg));

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 1, w, observerSet, cfg.Visibility, out float2 ownPos),
                "a player's own death must reach them even when the ordinary visibility gate would refuse it");
            Assert.AreEqual(ev.Pos, ownPos);

            // Negative counterpart (Task 19/20 discipline: no negative assert
            // without checking it isn't a blanket rule): a DIFFERENT observer,
            // facing the exact same absent-from-the-set victim, must NOT get
            // it — the special case is scoped to the owner only.
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, w, observerSet, cfg.Visibility, out _),
                "a bystander must still go through the ordinary visibility gate for someone ELSE's death");
        }

        // --- 4: MobDied_DeliveredInHysteresisBand ---

        [Test]
        public void MobDied_DeliveredInHysteresisBand()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f)); // clearly visible

            var setA = new VisibilitySet(Capacity(cfg));
            var setB = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0: visible
            Assert.IsTrue(setB.Contains(mobId), "test setup: must start visible");

            // Move into the hysteresis band (Р81): past the plain SightRadius
            // but still within SightRadius + ExitHysteresis — the exact 3 m
            // gap where a delivery rule keyed on "subscribed to this mob's own
            // spawn projectile" instead of "mob's own visibility" would
            // already have stopped delivering, while VisibilitySystem itself
            // still tracks the mob as visible-now.
            float hysteresisDist = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis * 0.5f;
            MobState m = w.Mobs[0];
            m.Pos = new float2(hysteresisDist, 0f);
            w.SetMobForTest(0, m);

            VisibilitySystem.Compute(w, 0, cfg.Visibility, setB, setA); // tick 1: still inside the widened band
            Assert.IsTrue(setA.Contains(mobId), "test setup: must still read visible-now within the hysteresis band");
            Assert.AreEqual(0, setA.LingerOf(mobId), "test setup: must be visible NOW, not merely lingering");

            // The mob dies exactly on THIS tick — SimulationWorld's own
            // swap-remove would already have erased it from a freshly
            // recomputed CURRENT set (carryover-t21.md #1) — `setA` above,
            // this tick's own visibility result computed BEFORE the death
            // would remove the mob, is exactly the set the caller is
            // contractually required to pass in (see ShouldDeliver's own doc).
            var ev = new SimEvent
            {
                Kind = SimEventKind.MobDied,
                EntityId = mobId,
                MobType = MobType.Chaser,
                PlayerIndex = 0, // ATTACKER — irrelevant to a mob-identity delivery decision
                Pos = m.Pos
            };

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, setA, cfg.Visibility, out float2 deliveredPos),
                "a mob that dies while still tracked visible-now in the hysteresis band must have its death delivered");
            Assert.AreEqual(ev.Pos, deliveredPos);
        }

        // --- 5: DashEvent_AudibleWithCoarsePos ---

        [Test]
        public void DashEvent_AudibleWithCoarsePos()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            // Geometry.SpawnPosFor puts a 2-player world's player 0 on the
            // spawn ring (52, 0), not the origin — pinned to float2.zero
            // explicitly so the HearRadius arithmetic below is exact instead
            // of incidentally close.
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            var observerSet = new VisibilitySet(Capacity(cfg)); // actor not visible to observer 0

            // Within HearRadius but not grid-aligned, so a raw-position mutant
            // is visibly distinguishable from a correctly-coarsened one.
            var audiblePos = new float2(cfg.Visibility.HearRadius - 1f, 0.7f);
            Assert.IsTrue(VisibilitySystem.IsAudible(float2.zero, audiblePos, cfg.Visibility),
                "test setup: this position must actually sit within HearRadius of the observer");

            var evAudible = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 1, Pos = audiblePos };
            Assert.IsTrue(EventRelevance.ShouldDeliver(evAudible, 0, w, observerSet, cfg.Visibility, out float2 pos),
                "an invisible but audible actor's dash must still be delivered");
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(audiblePos, cfg.Visibility), pos);
            Assert.AreNotEqual(audiblePos, pos,
                "test setup: the coarsened position must actually differ from the raw one, or this cannot tell exact from coarse");

            // Negative counterpart: beyond even HearRadius, the event is
            // dropped entirely — an Audible-channel event is not "eventually
            // delivered no matter what", it has its own outer range gate too.
            var farPos = new float2(cfg.Visibility.HearRadius + 5f, 0f);
            var evFar = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 1, Pos = farPos };
            Assert.IsFalse(EventRelevance.ShouldDeliver(evFar, 0, w, observerSet, cfg.Visibility, out _),
                "an actor beyond even HearRadius must not have this event delivered at all");
        }

        // --- 6: AudiblePos_ExactForVisible ---

        [Test]
        public void AudiblePos_ExactForVisible()
        {
            // Carried over from Task 20 (carryover-t21.md #4): QuantizeAudiblePos
            // itself takes no visibility flag — "exact for a visible source" is
            // this seam's own rule, not that function's.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            // Geometry.SpawnPosFor puts a 2-player world's player 0 on the
            // spawn ring (52, 0), not the origin — pinned explicitly so both
            // halves below are audible for a stated, exact reason rather than
            // an incidental one.
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            int actorId = VisibilityIds.ForPlayer(1);
            // Deliberately not grid-aligned (default grid 3 m): a coarsened
            // position visibly differs from this exact one below.
            var pos = new float2(10f, 0f);
            Assert.AreNotEqual(pos, VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility),
                "test setup: this position must actually move under quantization, or the two halves below cannot be told apart");

            var ev = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 1, Pos = pos };

            // Half 1 — "visible" per carryover-t21.md #5 (Р132): Contains(id)
            // alone, INCLUDING an entity merely LINGERING (LingerOf > 0) after
            // a recent LoS break — not Contains(id) && LingerOf(id) == 0. A
            // linger-blind implementation (gating on LingerOf == 0 instead)
            // would treat this actor as not visible and wrongly fall through
            // to the coarsened branch below — exactly the divergence the
            // coordinator's decision rules out (Р19: a lingering entity is
            // still replicated with its exact position in the state block, so
            // coarsening its EVENTS' position too is both pointless and
            // inconsistent with the two channels).
            var lingering = new VisibilitySet(Capacity(cfg));
            lingering.Add(actorId, cfg.Visibility.LingerTicks);
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, lingering, cfg.Visibility, out float2 exactPos));
            Assert.AreEqual(pos, exactPos, "an actor merely LINGERING (Contains true, LingerOf > 0) must still get the EXACT position");

            // Half 2 — genuinely not tracked at all: same event, same
            // distance (well within HearRadius), but the actor is absent.
            var untracked = new VisibilitySet(Capacity(cfg));
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, untracked, cfg.Visibility, out float2 coarsePos));
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility), coarsePos,
                "an actor absent from the visibility set entirely must get the COARSENED position, never the exact one");
            Assert.AreNotEqual(exactPos, coarsePos, "the two halves must actually produce DIFFERENT positions, or this test cannot tell them apart");
        }

        // --- 7: MobDied_UsesMobIdentity_NotKillerIdentity ---

        [Test]
        public void MobDied_UsesMobIdentity_NotKillerIdentity()
        {
            // The ATTACKER trap (task-21-brief.md, table Р28 footnote): MobDied's
            // PlayerIndex is the SHOOTER (SimulationWorld.DamageMob's own
            // `ownerIndex` argument), not the subject of visibility — the
            // subject is the mob itself, addressed by EntityId.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            const int mobId = 7; // arbitrary — ShouldDeliver never queries the world for it, only the passed-in set
            const byte killerIndex = 1;
            int killerVisId = VisibilityIds.ForPlayer(killerIndex);

            var pos = new float2(3f, 4f);
            var ev = new SimEvent
            {
                Kind = SimEventKind.MobDied,
                EntityId = mobId,
                MobType = MobType.Chaser,
                PlayerIndex = killerIndex, // ATTACKER convention — NOT the subject
                Pos = pos
            };

            // Half 1: the MOB is visible, its KILLER is not — a subject-identity
            // bug using ev.PlayerIndex (the killer) instead of ev.EntityId (the
            // mob) would find the killer's id absent and wrongly REFUSE delivery.
            var mobVisibleOnly = new VisibilitySet(Capacity(cfg));
            mobVisibleOnly.Add(mobId);
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, mobVisibleOnly, cfg.Visibility, out float2 pos1),
                "MobDied must be delivered by the MOB's own visibility, not its killer's");
            Assert.AreEqual(pos, pos1);

            // Half 2 (the discriminating counterpart the brief demands): the
            // KILLER is visible, the MOB is not — the same bug would now find
            // the killer's id present and wrongly DELIVER.
            var killerVisibleOnly = new VisibilitySet(Capacity(cfg));
            killerVisibleOnly.Add(killerVisId);
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, w, killerVisibleOnly, cfg.Visibility, out _),
                "the killer being visible must NOT be enough — the mob itself, identified by EntityId, is what must be checked");
        }

        // --- 8: ProjectileKinds_ThrowAsDeferred ---

        [Test]
        public void ProjectileKinds_ThrowAsDeferred()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var observerSet = new VisibilitySet(Capacity(cfg));

            var projectileKinds = new[]
            {
                SimEventKind.ProjectileFired, SimEventKind.ProjectileHit,
                SimEventKind.ProjectileBlocked, SimEventKind.ProjectileExpired
            };

            foreach (SimEventKind kind in projectileKinds)
            {
                Assert.AreEqual(DeliveryChannel.None, EventRelevance.ChannelFor(kind),
                    $"{kind} must route to None — its delivery is decided by Task 28's SnapshotAssembler, not here");

                var ev = new SimEvent { Kind = kind };
                Assert.Throws<System.ArgumentException>(() =>
                        EventRelevance.ShouldDeliver(ev, 0, w, observerSet, cfg.Visibility, out _),
                    $"{kind} must THROW rather than silently return false — a silent false would make a future " +
                    "Task 28 caller drop every projectile event on an oversight, indistinguishable from a correct " +
                    "'nobody nearby' answer (task-21-brief.md: None means 'decided elsewhere', not 'nobody')");
            }
        }

        // --- 9: Invisible_VisibleChannelEvent_NotDelivered ---

        [Test]
        public void Invisible_VisibleChannelEvent_NotDelivered()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            const int visibleMobId = 1;
            const int invisibleMobId = 2;

            var observerSet = new VisibilitySet(Capacity(cfg));
            observerSet.Add(visibleMobId); // positive witness: the OTHER mob really is visible

            // Within HearRadius, so an implementation that confused Visible
            // with Audible (delivering anyway, coarsened, because it's in
            // earshot) would wrongly pass this — Visible is strictly
            // stricter than Audible by spec Р28's own wording ("не видим →
            // не доставляем вовсе").
            var audibleButInvisiblePos = new float2(cfg.Visibility.HearRadius - 1f, 0f);
            Assert.IsTrue(VisibilitySystem.IsAudible(float2.zero, audibleButInvisiblePos, cfg.Visibility),
                "test setup: this position must actually be within earshot, or the fixture proves nothing about Visible-vs-Audible strictness");

            var evInvisible = new SimEvent
            {
                Kind = SimEventKind.MobSpawned, MobType = MobType.Chaser,
                EntityId = invisibleMobId, Pos = audibleButInvisiblePos
            };
            Assert.IsFalse(EventRelevance.ShouldDeliver(evInvisible, 0, w, observerSet, cfg.Visibility, out _),
                "a Visible-channel event for an invisible subject must not be delivered, even when it is audible");

            var evVisible = new SimEvent
            {
                Kind = SimEventKind.MobSpawned, MobType = MobType.Chaser,
                EntityId = visibleMobId, Pos = new float2(2f, 2f)
            };
            Assert.IsTrue(EventRelevance.ShouldDeliver(evVisible, 0, w, observerSet, cfg.Visibility, out float2 pos),
                "witness: a genuinely visible subject's MobSpawned must actually be delivered");
            Assert.AreEqual(evVisible.Pos, pos);
        }

        // --- 10: ChannelFor_HandlesEveryKind ---

        [Test]
        public void ChannelFor_HandlesEveryKind()
        {
            // Table Р28, restated as an explicit expected map that is
            // deliberately NOT derived from EventRelevance itself — a bare
            // Enum.GetValues + Assert.DoesNotThrow loop would only prove
            // ChannelFor didn't crash, and would happily agree with a
            // uniformly-wrong implementation (e.g. everything routed to
            // Visible). Comparing against this independent oracle catches a
            // WRONG channel for an EXISTING kind, while the ContainsKey guard
            // below still means a FUTURE kind with no entry HERE fails
            // loudly — the "урок 86: contract by assertion, not prose"
            // discipline task-21-brief.md calls out by name.
            var expected = new Dictionary<SimEventKind, DeliveryChannel>
            {
                [SimEventKind.StaminaDenied] = DeliveryChannel.Owner,
                [SimEventKind.PlayerDashed] = DeliveryChannel.Audible,
                [SimEventKind.PlayerSlideStarted] = DeliveryChannel.Audible,
                [SimEventKind.DashRicocheted] = DeliveryChannel.Audible,
                [SimEventKind.MobSpawned] = DeliveryChannel.Visible,
                [SimEventKind.MobDied] = DeliveryChannel.Visible,
                [SimEventKind.PlayerDamaged] = DeliveryChannel.Visible,
                [SimEventKind.PlayerDied] = DeliveryChannel.Visible,
                [SimEventKind.WaveStarted] = DeliveryChannel.All,
                [SimEventKind.WaveCleared] = DeliveryChannel.All,
                [SimEventKind.ProjectileFired] = DeliveryChannel.None,
                [SimEventKind.ProjectileHit] = DeliveryChannel.None,
                [SimEventKind.ProjectileBlocked] = DeliveryChannel.None,
                [SimEventKind.ProjectileExpired] = DeliveryChannel.None
            };

            foreach (SimEventKind kind in System.Enum.GetValues(typeof(SimEventKind)))
            {
                Assert.IsTrue(expected.ContainsKey(kind),
                    $"{kind} has no entry in this test's own Р28 table — a new SimEventKind must be added HERE " +
                    "(and to ChannelFor's switch), not left to fall through to a silent default");
                Assert.AreEqual(expected[kind], EventRelevance.ChannelFor(kind), $"{kind} must route to its Р28 channel");
            }
        }

        // --- 11: DeadObserver_ResolvesOwnPositionForAudibility ---

        [Test]
        public void DeadObserver_ResolvesOwnPositionForAudibility()
        {
            // Rule 9 (task-21-brief.md): observerIndex may point at a dead
            // player (post-mortem observation, Р70) — ShouldDeliver must not
            // special-case that away, and must resolve the observer's own
            // position the same plain way VisibilitySystem.Compute does
            // (w.PlayerAt, no Alive gate).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            var deadObserverPos = new float2(1000f, 1000f); // far from the origin — unmissable if ignored
            TestWorlds.RelocatePlayerForTest(w, 0, deadObserverPos);
            w.KillPlayerNoDamage(0);
            Assert.IsFalse(w.PlayerAt(0).Alive, "test setup: observer must actually be dead");

            // Within HearRadius OF THE DEAD OBSERVER's own position, but
            // enormously far from the world origin — a mutant that (wrongly)
            // treated a dead observer's position as float2.zero instead of
            // reading it would compute a distance in the thousands and wrongly
            // refuse delivery.
            var actorPos = deadObserverPos + new float2(5f, 0f);
            var ev = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 1, Pos = actorPos };
            var observerSet = new VisibilitySet(Capacity(cfg)); // actor not visible — audibility path only

            Assert.DoesNotThrow(() => EventRelevance.ShouldDeliver(ev, 0, w, observerSet, cfg.Visibility, out _),
                "a dead observer must not make ShouldDeliver throw");
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, observerSet, cfg.Visibility, out float2 pos),
                "a dead observer must still resolve its OWN position for the audibility gate, not silently fail closed");
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(actorPos, cfg.Visibility), pos);
        }
    }
}
