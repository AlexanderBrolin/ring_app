using System.Collections.Generic;
using NUnit.Framework;
using Ring.Data;
using Ring.Networking.Protocol;
using Ring.Networking.Server;
using Ring.Simulation.Core;
using Ring.Simulation.Visibility;
using Unity.Mathematics;
using UnityEngine;

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
    ///
    /// Phase Ф5 fix-wave (two phase reviews) added fixture 4c
    /// (MobSpawned_RequiresCurrentTickSet — the mirror image of 4b, and the
    /// reason the "which tick's set" contract is PER-KIND rather than one
    /// blanket rule) and pulled `EntityId` apart from `PlayerIndex` in the two
    /// PlayerDied/PlayerDamaged fixtures: while the two fields carried the
    /// same value, ShouldDeliver could read either one and every assertion
    /// here still passed, so the VICTIM-by-PlayerIndex convention was prose,
    /// not contract.
    ///
    /// Task 42b split `ShouldDeliver`'s single `observerIndex` into
    /// `identityIndex`/`viewpointIndex`. Every PRE-EXISTING CALL SITE OF
    /// `ShouldDeliver` below passes the SAME value for both, so none of them
    /// pins new behavior. The fixtures that DO pass genuinely different
    /// indices are 19-23 (added by Task 42b, at the end of this file) and,
    /// already before it, fixture 18 — which reaches the split through
    /// `SnapshotAssembler.BuildFor`, whose two index parameters predate this
    /// task, rather than through the seam.
    public class EventDeliveryTests
    {
        // Task 21 fix-round 1 (M-2): Capacity moved to TestWorlds — see its
        // own doc there — so this file and VisibilityTests share one
        // definition instead of two byte-identical copies.

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
            // Visible/Audible never consult the identity index at all.
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg));
            observerSet.Add(VisibilityIds.ForPlayer(1));

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 1, 1, w, observerSet, cfg.Visibility, out float2 ownerPos),
                "the event's own owner must receive it");
            Assert.AreEqual(ev.Pos, ownerPos,
                "StaminaDenied is private feedback: the delivered position is the raw, EXACT one, never quantized");

            // Task 21 fix-round 1 (I-1): named variable, not `out _` — pins
            // the "deliveredPos is `default` on a `false` return" contract
            // on the Owner channel's OWN refusal branch. Before the fix-round,
            // ShouldDeliver assigned `deliveredPos = ev.Pos` unconditionally,
            // BEFORE checking ownership, so this refusal handed back the
            // neighbour's EXACT position through `out` even while returning
            // `false` — a caller that trusts `out` without checking the
            // `bool` first (a plausible per-connection assembler pattern,
            // Task 28) would still leak it. `out _` could never catch that.
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, 0, w, observerSet, cfg.Visibility, out float2 refusedPos),
                "a neighbour must not receive another player's StaminaDenied — it would leak Stamina economy");
            Assert.AreEqual(float2.zero, refusedPos,
                "a `false` return must carry `default` (zero), never the leaked owner's exact position");
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
            var emptySet = new VisibilitySet(TestWorlds.Capacity(cfg));

            foreach (SimEventKind kind in new[] { SimEventKind.WaveStarted, SimEventKind.WaveCleared })
            {
                // Nonzero and off-centre, mirroring WaveSystem's own
                // "nearest-alive-player" event position (spec Р28 notes that
                // today this position is simply taken from a player — so
                // delivering it would leak that player's location to everyone).
                var ev = new SimEvent { Kind = kind, Pos = new float2(9.5f, -3.25f), EntityId = 4 };

                for (int observerIndex = 0; observerIndex < w.PlayerCount; observerIndex++)
                {
                    Assert.IsTrue(EventRelevance.ShouldDeliver(ev, observerIndex, observerIndex, w, emptySet, cfg.Visibility, out float2 pos),
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

            const byte victimIndex = 1;
            // Phase Ф5 fix-wave (item 9): EntityId is deliberately pulled
            // APART from PlayerIndex here. SimulationWorld emits these two
            // fields with the SAME value for PlayerDied (KillPlayer passes the
            // victim index to both), and while they agreed in this fixture the
            // owner carve-out below could read either one and still pass:
            // `identityIndex == ev.PlayerIndex` -> `== ev.EntityId` survived
            // the whole run. Diverging them costs the fixture nothing (nothing
            // in ShouldDeliver reads EntityId for this kind, which is exactly
            // the claim under test) and makes the VICTIM-by-PlayerIndex
            // convention observable for the first time.
            var ev = new SimEvent
            {
                Kind = SimEventKind.PlayerDied,
                PlayerIndex = victimIndex, // VICTIM convention (SimEvent.PlayerIndex's own doc)
                EntityId = victimIndex + 1,
                Pos = new float2(-4f, 21f)
            };

            // The gate an ordinary Visible-channel event would apply: the
            // victim's own synthetic id is absent — as if the victim died
            // behind a wall from every observer's point of view, itself
            // included (task-21-brief.md's own "dead behind a wall" phrasing).
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg));

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, victimIndex, victimIndex, w, observerSet, cfg.Visibility, out float2 ownPos),
                "a player's own death must reach them even when the ordinary visibility gate would refuse it — "
                + "the owner carve-out is keyed on PlayerIndex (the VICTIM), never on EntityId");
            Assert.AreEqual(ev.Pos, ownPos);

            // Negative counterpart (Task 19/20 discipline: no negative assert
            // without checking it isn't a blanket rule): a DIFFERENT observer,
            // facing the exact same absent-from-the-set victim, must NOT get
            // it — the special case is scoped to the owner only.
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, 0, w, observerSet, cfg.Visibility, out _),
                "a bystander must still go through the ordinary visibility gate for someone ELSE's death");
        }

        // --- 4: MobDied_DeliveredInHysteresisBand ---

        [Test]
        public void MobDied_DeliveredInHysteresisBand()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f)); // clearly visible

            var setA = new VisibilitySet(TestWorlds.Capacity(cfg));
            var setB = new VisibilitySet(TestWorlds.Capacity(cfg));
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

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, 0, w, setA, cfg.Visibility, out float2 deliveredPos),
                "a mob that dies while still tracked visible-now in the hysteresis band must have its death delivered");
            Assert.AreEqual(ev.Pos, deliveredPos);
        }

        // --- 4b: MobDied_DeliveredViaPreviousTickSet_NotCurrentTick (Task 21 fix-round 1, I-4) ---

        [Test]
        public void MobDied_DeliveredViaPreviousTickSet_NotCurrentTick()
        {
            // I-4 / carryover-t21.md #1: SimulationWorld.DamageMob
            // swap-removes a dead mob's slot in the SAME tick it dies
            // (SimulationWorld.cs:595) — VisibilitySystem.Compute only ever
            // iterates LIVE mobs (w.Mobs/w.MobCount), so a freshly
            // recomputed CURRENT-tick set can never contain a corpse's id at
            // all, no matter how visible it was a moment before it died. The
            // caller (Task 28's SnapshotAssembler) is contractually required
            // to pass the set from the tick BEFORE the death instead.
            // MobDied_DeliveredInHysteresisBand above never actually kills
            // its mob (it stays alive in w.Mobs for the whole test), so this
            // contract was previously unfalsifiable: a future "optimization"
            // that recomputed the set inside the caller instead of threading
            // the previous tick's through would pass every existing test in
            // this file yet break delivery for every MobDied, always. This
            // test kills a real mob through the DamageMob seam (internal,
            // visible to tests via InternalsVisibleTo) to make that trap
            // observable.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f)); // clearly visible

            var setEmpty = new VisibilitySet(TestWorlds.Capacity(cfg));
            var setBeforeDeath = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setEmpty, setBeforeDeath); // tick N: visible
            Assert.IsTrue(setBeforeDeath.Contains(mobId), "test setup: must start visible");

            // Kills the mob outright — Hp comfortably exceeded, one hit.
            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, default, ownerIndex: 0);
            Assert.AreEqual(0, w.MobCount, "test setup: the mob must actually be gone from the world");

            // Tick N+1's own "current" set — computed AFTER the corpse's
            // swap-remove, exactly as the real per-tick caller would.
            var setAfterDeath = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setBeforeDeath, setAfterDeath);
            Assert.IsFalse(setAfterDeath.Contains(mobId),
                "test setup: the corpse must be gone from the CURRENT-tick set entirely (not even lingering) — Compute never visits an id that no longer exists in the world");

            var ev = new SimEvent
            {
                Kind = SimEventKind.MobDied,
                EntityId = mobId,
                MobType = MobType.Chaser,
                PlayerIndex = 0, // ATTACKER — irrelevant to a mob-identity delivery decision
                Pos = new float2(5f, 0f)
            };

            // Negative witness: the trap is real — the CURRENT tick's own
            // set genuinely refuses delivery for a mob that was visible right
            // up until the tick it died.
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, 0, w, setAfterDeath, cfg.Visibility, out _),
                "the CURRENT tick's set must not deliver — this is exactly the trap the previous-tick contract exists to avoid");

            // Positive witness: the PREVIOUS tick's set (still holding the
            // subject as visible-now, from before the death) delivers
            // correctly.
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, 0, w, setBeforeDeath, cfg.Visibility, out float2 deliveredPos),
                "the PREVIOUS tick's set — from before the death — must deliver");
            Assert.AreEqual(ev.Pos, deliveredPos);
        }

        // --- 4c: MobSpawned_RequiresCurrentTickSet (Phase Ф5 fix-wave, I-9) ---

        [Test]
        public void MobSpawned_RequiresCurrentTickSet()
        {
            // I-9, the exact mirror image of 4b above, and the reason the
            // "which tick's set" contract is PER-KIND rather than one blanket
            // rule (Р140, carryover-t28.md §8б): MobSpawned rides the SAME
            // Visible channel as MobDied, but its subject did not EXIST in the
            // previous tick, so the previous tick's set refuses it as
            // categorically as the current tick's set refuses a corpse.
            //
            // Until this fixture, only the MobDied half was pinned. A caller
            // that read the contract as "MobDied needs the previous tick, so
            // pass the previous tick everywhere" — the natural
            // over-generalization of 4b — would have broken every MobSpawned
            // delivery, always, with the whole suite green. Both arms are
            // asserted here for the same reason 4b asserts both of its own.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);

            // Tick N: the world has no mobs at all yet.
            var setEmpty = new VisibilitySet(TestWorlds.Capacity(cfg));
            var setBeforeSpawn = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setEmpty, setBeforeSpawn);

            // Tick N+1: the mob spawns, plainly inside SightRadius with clear
            // LoS (open arena) — it is visible from the very first tick of its
            // existence, and invisible in every set computed before it.
            var spawnPos = new float2(5f, 0f);
            int mobId = w.SpawnMobForTest(MobType.Chaser, spawnPos);
            Assert.IsFalse(setBeforeSpawn.Contains(mobId),
                "test setup: the PREVIOUS tick's set cannot contain a mob that did not exist yet");

            var setAfterSpawn = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setBeforeSpawn, setAfterSpawn);
            Assert.IsTrue(setAfterSpawn.Contains(mobId),
                "test setup: the CURRENT tick's set must see the freshly spawned mob");

            var ev = new SimEvent
            {
                Kind = SimEventKind.MobSpawned,
                EntityId = mobId,
                MobType = MobType.Chaser,
                Pos = spawnPos
            };

            // Negative arm: the trap is real — reusing MobDied's own
            // previous-tick set here refuses delivery to everyone.
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, 0, w, setBeforeSpawn, cfg.Visibility, out float2 refusedPos),
                "the PREVIOUS tick's set must NOT deliver a MobSpawned — this is exactly the mirror trap of 4b, "
                + "and the reason the caller picks the tick per KIND");
            Assert.AreEqual(float2.zero, refusedPos,
                "a `false` return must carry `default` (zero), same contract as every other refusing branch");

            // Positive arm: the CURRENT tick's set — the one this kind's
            // subject actually lives in — delivers.
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, 0, w, setAfterSpawn, cfg.Visibility, out float2 deliveredPos),
                "the CURRENT tick's set must deliver a MobSpawned for a mob the observer can see");
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
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg)); // actor not visible to observer 0

            // Within HearRadius but not grid-aligned, so a raw-position mutant
            // is visibly distinguishable from a correctly-coarsened one.
            var audiblePos = new float2(cfg.Visibility.HearRadius - 1f, 0.7f);
            Assert.IsTrue(VisibilitySystem.IsAudible(float2.zero, audiblePos, cfg.Visibility),
                "test setup: this position must actually sit within HearRadius of the observer");

            var evAudible = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 1, Pos = audiblePos };
            Assert.IsTrue(EventRelevance.ShouldDeliver(evAudible, 0, 0, w, observerSet, cfg.Visibility, out float2 pos),
                "an invisible but audible actor's dash must still be delivered");
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(audiblePos, cfg.Visibility), pos);
            Assert.AreNotEqual(audiblePos, pos,
                "test setup: the coarsened position must actually differ from the raw one, or this cannot tell exact from coarse");

            // Negative counterpart: beyond even HearRadius, the event is
            // dropped entirely — an Audible-channel event is not "eventually
            // delivered no matter what", it has its own outer range gate too.
            var farPos = new float2(cfg.Visibility.HearRadius + 5f, 0f);
            var evFar = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 1, Pos = farPos };
            // Task 21 fix-round 1 (I-1): named variable, not `out _` — pins
            // the same "`deliveredPos` is `default` on `false`" contract on
            // the Audible channel's OUTER-range refusal branch.
            Assert.IsFalse(EventRelevance.ShouldDeliver(evFar, 0, 0, w, observerSet, cfg.Visibility, out float2 refusedPos),
                "an actor beyond even HearRadius must not have this event delivered at all");
            Assert.AreEqual(float2.zero, refusedPos,
                "a `false` return must carry `default` (zero), never a raw or coarsened position");
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
            var lingering = new VisibilitySet(TestWorlds.Capacity(cfg));
            lingering.Add(actorId, cfg.Visibility.LingerTicks);
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, 0, w, lingering, cfg.Visibility, out float2 exactPos));
            Assert.AreEqual(pos, exactPos, "an actor merely LINGERING (Contains true, LingerOf > 0) must still get the EXACT position");

            // Half 2 — genuinely not tracked at all: same event, same
            // distance (well within HearRadius), but the actor is absent.
            var untracked = new VisibilitySet(TestWorlds.Capacity(cfg));
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, 0, w, untracked, cfg.Visibility, out float2 coarsePos));
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
            var mobVisibleOnly = new VisibilitySet(TestWorlds.Capacity(cfg));
            mobVisibleOnly.Add(mobId);
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, 0, w, mobVisibleOnly, cfg.Visibility, out float2 pos1),
                "MobDied must be delivered by the MOB's own visibility, not its killer's");
            Assert.AreEqual(pos, pos1);

            // Half 2 (the discriminating counterpart the brief demands): the
            // KILLER is visible, the MOB is not — the same bug would now find
            // the killer's id present and wrongly DELIVER.
            var killerVisibleOnly = new VisibilitySet(TestWorlds.Capacity(cfg));
            killerVisibleOnly.Add(killerVisId);
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, 0, w, killerVisibleOnly, cfg.Visibility, out _),
                "the killer being visible must NOT be enough — the mob itself, identified by EntityId, is what must be checked");
        }

        // --- 7b: PlayerDamaged_UsesVictimSyntheticId_NotEntityId (Task 21 fix-round 1, I-3) ---

        [Test]
        public void PlayerDamaged_UsesVictimSyntheticId_NotEntityId()
        {
            // I-3: the Visible-channel subject for PlayerDamaged/PlayerDied
            // must be VisibilityIds.ForPlayer(ev.PlayerIndex) (the VICTIM's
            // SYNTHETIC id), never ev.EntityId (or the victim index read as a
            // raw id) directly — SimulationWorld's own DamagePlayer really
            // does emit PlayerDamaged with EntityId == victimIndex, a small
            // non-negative number that lives in the MOB id space (every real
            // mob id is >= 1, SimulationWorld.cs's own `_nextEntityId`
            // counter). A subject-identity bug that consulted ev.EntityId
            // instead of the synthetic id would silently collide with an
            // observer's own "can I see mob #victimIndex" answer — exactly
            // the class of leak VisibilityIds (Р20) exists to rule out.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            const byte victimIndex = 1;
            // Phase Ф5 fix-wave (item 9): EntityId is deliberately pulled
            // APART from PlayerIndex. SimulationWorld does emit both with the
            // victim's index for this kind, and while this fixture agreed with
            // it, VisibleSubjectId could read EITHER field and still pass —
            // `VisibilityIds.ForPlayer(ev.PlayerIndex)` -> `ForPlayer(ev.EntityId)`
            // survived the whole run. Diverging them here is what makes the
            // VICTIM-by-PlayerIndex convention observable at all; the
            // production pairing is unaffected, since nothing in ShouldDeliver
            // reads EntityId for a PlayerDamaged/PlayerDied — which is exactly
            // the property under test.
            const int foreignEntityId = victimIndex + 1;
            var pos = new float2(6f, -2f);
            var ev = new SimEvent
            {
                Kind = SimEventKind.PlayerDamaged,
                PlayerIndex = victimIndex,     // VICTIM convention (SimEvent.PlayerIndex's own doc)
                EntityId = foreignEntityId,    // deliberately NOT the victim index — see above
                Pos = pos
            };

            // Half 1: the CORRECT subject id (the victim's SYNTHETIC player
            // id) is in the set.
            var correctSet = new VisibilitySet(TestWorlds.Capacity(cfg));
            correctSet.Add(VisibilityIds.ForPlayer(victimIndex));
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, 0, w, correctSet, cfg.Visibility, out float2 pos1),
                "PlayerDamaged must be delivered by the victim's SYNTHETIC player id, derived from PlayerIndex");
            Assert.AreEqual(pos, pos1);

            // Half 2 (the discriminating counterpart the fix-round demands):
            // every OTHER id the two fields could plausibly be turned into is
            // present at once, and none of them may deliver —
            //   * the victim index as a RAW/real id (the id a mob numbered
            //     victimIndex would occupy): kills `return ev.PlayerIndex`;
            //   * EntityId as a RAW id: kills `return ev.EntityId`, which is
            //     what the pre-fix-round code shape would have been;
            //   * EntityId run through the SYNTHETIC mapping: kills
            //     `VisibilityIds.ForPlayer(ev.EntityId)`, the mutation the two
            //     fields' former equality hid completely.
            var wrongSet = new VisibilitySet(TestWorlds.Capacity(cfg));
            wrongSet.Add(victimIndex);
            wrongSet.Add(foreignEntityId);
            wrongSet.Add(VisibilityIds.ForPlayer(foreignEntityId));
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, 0, w, wrongSet, cfg.Visibility, out _),
                "neither field read the wrong way may deliver — only ForPlayer(PlayerIndex), the victim's "
                + "synthetic player id, counts as this event's subject");
        }

        // --- 8: ProjectileKinds_ThrowAsDeferred ---

        [Test]
        public void ProjectileKinds_ThrowAsDeferred()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg));

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
                        EventRelevance.ShouldDeliver(ev, 0, 0, w, observerSet, cfg.Visibility, out _),
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

            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg));
            observerSet.Add(visibleMobId); // positive witness: the OTHER mob really is visible

            // Within HearRadius, so an implementation that confused Visible
            // with Audible (delivering anyway, coarsened, because it's in
            // earshot) would wrongly pass this — Visible is strictly
            // stricter than Audible by spec Р28's own wording: a subject the
            // observer cannot see is not delivered AT ALL, not delivered
            // coarsely.
            var audibleButInvisiblePos = new float2(cfg.Visibility.HearRadius - 1f, 0f);
            Assert.IsTrue(VisibilitySystem.IsAudible(float2.zero, audibleButInvisiblePos, cfg.Visibility),
                "test setup: this position must actually be within earshot, or the fixture proves nothing about Visible-vs-Audible strictness");

            var evInvisible = new SimEvent
            {
                Kind = SimEventKind.MobSpawned, MobType = MobType.Chaser,
                EntityId = invisibleMobId, Pos = audibleButInvisiblePos
            };
            // Task 21 fix-round 1 (I-1): named variable, not `out _` — pins
            // the same "`deliveredPos` is `default` on `false`" contract on
            // the Visible channel's own refusal branch.
            Assert.IsFalse(EventRelevance.ShouldDeliver(evInvisible, 0, 0, w, observerSet, cfg.Visibility, out float2 refusedPos),
                "a Visible-channel event for an invisible subject must not be delivered, even when it is audible");
            Assert.AreEqual(float2.zero, refusedPos,
                "a `false` return must carry `default` (zero), never the invisible subject's position");

            var evVisible = new SimEvent
            {
                Kind = SimEventKind.MobSpawned, MobType = MobType.Chaser,
                EntityId = visibleMobId, Pos = new float2(2f, 2f)
            };
            Assert.IsTrue(EventRelevance.ShouldDeliver(evVisible, 0, 0, w, observerSet, cfg.Visibility, out float2 pos),
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
            // loudly — the "Урок 86: contract by assertion, not prose"
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
            // Rule 9 (task-21-brief.md): the viewpoint index may point at a
            // dead player (post-mortem observation, Р70) — ShouldDeliver must
            // not special-case that away, and must resolve that viewpoint's
            // own position the same plain way VisibilitySystem.Compute does
            // (w.PlayerAt, no Alive gate). This fixture passes the same index
            // for both roles, which is the ordinary case of a dead player
            // still looking out of its own body.
            //
            // Task 21 fix-round 1 (I-2): observe from index 1, not 0 — EVERY
            // call site in this file that reaches `w.PlayerAt(...)` for the
            // hearing distance happened to pass index 0 (lines noted by the
            // review), so a mutant hardcoding `w.PlayerAt(0)` in place of the
            // real lookup agreed with the correct code on
            // every one of them and survived the whole run. Here the ACTOR is
            // player 0 (left at its default two-player spawn-ring position,
            // Geometry.SpawnPosFor — never relocated) and the DEAD OBSERVER
            // is player 1 (relocated far away, then killed) — the opposite
            // pairing of the original fixture. Observing from index 1 makes
            // the hardcoded-0 mutant read player 0's own nearby spawn-ring
            // position instead of player 1's relocated one, producing a
            // distance far outside HearRadius and a WRONG refusal.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            var deadObserverPos = new float2(1000f, 1000f); // far from the origin — unmissable if ignored
            TestWorlds.RelocatePlayerForTest(w, 1, deadObserverPos);
            w.KillPlayerNoDamage(1);
            Assert.IsFalse(w.PlayerAt(1).Alive, "test setup: observer must actually be dead");

            // Within HearRadius OF THE DEAD OBSERVER's own position, but
            // enormously far from the world origin — a mutant that (wrongly)
            // treated a dead observer's position as float2.zero instead of
            // reading it would compute a distance in the thousands and wrongly
            // refuse delivery.
            var actorPos = deadObserverPos + new float2(5f, 0f);
            var ev = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 0, Pos = actorPos };
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg)); // actor not visible — audibility path only

            Assert.DoesNotThrow(() => EventRelevance.ShouldDeliver(ev, 1, 1, w, observerSet, cfg.Visibility, out _),
                "a dead observer must not make ShouldDeliver throw");
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 1, 1, w, observerSet, cfg.Visibility, out float2 pos),
                "a dead observer must still resolve its OWN position for the audibility gate, not silently fail closed");
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(actorPos, cfg.Visibility), pos);
        }

        // --- 12: ProjectileHit_CarriesKillingProjectileId (Stage 2 Task 28, §2.3) ---

        [Test]
        public void ProjectileHit_CarriesKillingProjectileId_AndMobDiedDoesNot()
        {
            // task-28-brief §2.3. Spec §3.8 wants ProjectileEndedNet keyed on
            // the ROUND's id, and table Р28 routes it "to everyone who got that
            // round's spawn" — both need the projectile's id at the moment it
            // ends. Every other ending carries it already
            // (ProjectileBlocked/ProjectileExpired emit with
            // `EntityId = proj.Id`), but the mob-hit branch spends EntityId on
            // the VICTIM (`mob.Id`, ProjectileSystem.cs's HitMob case), so the
            // round's own identity was lost at the emit and the assembler had
            // no way to close the subscription it opened on the spawn.
            //
            // SecondaryEntityId is the minimal fix: a second participant slot,
            // written today by exactly one emit site. The negative half below
            // is the boundary of that decision — MobDied does NOT get the id
            // (DamageMob has no projectile in scope; threading one through it
            // would widen the change into the damage API for no consumer), so
            // the assembler's own MobDied/spawn-subscription union joins
            // through the event BUFFER instead (§2.4 item 4).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);

            // A Gunner one-shot at head height: ProjectileHit and MobDied land
            // in the SAME tick, which is exactly the pairing under test.
            Assert.GreaterOrEqual(cfg.Weapon.Damage * cfg.Gunner.HeadDamageMult, cfg.Gunner.MaxHp,
                "fixture premise: this must be a one-shot kill, so both events land in one tick");
            var targetPos = new float2(1f, 0f); // under one tick of travel — the round lands next tick
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, targetPos));
            float headBand = 0.5f * (cfg.Gunner.BodyTop + cfg.Gunner.HeadTop);
            int projectileId = TestWorlds.FireAimed3D(w, float2.zero, headBand, targetPos, headBand);
            Assert.Greater(projectileId, 0, "fixture premise: the round must actually have spawned");
            int mobId = w.Mobs[0].Id;
            Assert.AreNotEqual(projectileId, mobId,
                "fixture premise: the round's id and the mob's id must differ, or this test cannot tell them apart");

            w.ClearEvents();
            w.Tick(default);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent hit),
                "test setup: the round must have hit the mob");
            Assert.AreEqual(mobId, hit.EntityId,
                "EntityId keeps its meaning — the VICTIM — so no existing consumer moves");
            Assert.AreEqual(projectileId, hit.SecondaryEntityId,
                "SecondaryEntityId must carry the ROUND's own id: without it ProjectileEndedNet has no key "
                + "and the spawn subscription can never be closed");

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.MobDied, out SimEvent died),
                "test setup: the mob must have died from that same round");
            Assert.AreEqual(mobId, died.EntityId, "MobDied's subject is still the mob itself");
            Assert.AreEqual(0, died.SecondaryEntityId,
                "the recorded boundary of this change: MobDied carries NO projectile id (0 = none), "
                + "which is why the assembler joins death to round through the tick's event buffer");
        }

        // ================= Stage 2 Task 28: assembler-level ROUTING =================
        //
        // EventRelevance is a pure per-event predicate; the rules below are the
        // ones it deliberately does NOT own — projectile relevance by trajectory
        // (Р32, channel `None`), the spawn-subscription set that addresses a
        // round's ending (Р28), the union that carries a mob's death to whoever
        // watched the tracer, the ShotHeard/ProjectileSpawned split of a single
        // shot, and the identity-versus-viewpoint split of the former single index
        // (carryover-t28.md §5). They live in SnapshotAssembler and are observed
        // here through assembled frames.

        const ushort AsmEpoch = 0x4D31;    // 19761 — both bytes nonzero and different

        static NetConfig AsmNet(int maxBytes = 1000, int eventBudget = 16, int redundancyTicks = 4)
        {
            var net = ScriptableObject.CreateInstance<NetConfig>();
            net.SnapshotMaxBytes = maxBytes;
            net.SnapshotEventBudget = eventBudget;
            net.EventRedundancyTicks = redundancyTicks;
            return net;
        }

        static AssembledFrame BuildOne(SnapshotAssembler asm, SimulationWorld w, in SimConfig cfg,
            int connection, int identityIndex, int viewpointIndex)
        {
            asm.BeginTick(w);
            int bytes = asm.BuildFor(connection, identityIndex, viewpointIndex, AsmEpoch);
            return AssembledFrame.Decode(asm.BufferFor(connection), bytes, cfg);
        }

        // --- 13: ProjectileRelevance_ByTrajectory (plan) ---

        [Test]
        public void ProjectileRelevance_ByTrajectory()
        {
            // Р32, and the regression on the rule it replaced. Rule v1 asked
            // "is the point of fire visible", which left the whole band from
            // SightRadius (45) out to the round's own reach — 45 + 52.5 m at
            // the shipped Weapon numbers — in which a PvP round could kill a
            // player who was never sent so much as a tracer. The rule is the
            // whole SEGMENT `spawnPos -> spawnPos + vel * lifetime` against
            // SightRadius, one ClosestPointOnSegment.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            float speed = cfg.Weapon.ProjectileSpeed;
            float reach = speed * cfg.Weapon.ProjectileLifetime;
            var muzzle = new float2(50f, 0f);
            Assert.Greater(math.length(muzzle), cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the point of fire itself must be INVISIBLE, or rule v1 would pass too");
            Assert.Greater(math.length(muzzle) - reach, -cfg.Visibility.SightRadius,
                "fixture premise: the arithmetic below stays inside the round's own reach");

            // Inbound: straight back down the +X axis, so the segment sweeps
            // through the observer's own position.
            w.SpawnProjectileForTest(ProjectileOwner.Player, muzzle, new float2(-speed, 0f),
                cfg.Hero.MuzzleHeight, 0f, cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime);

            var asm = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            AssembledFrame inbound = BuildOne(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, inbound.CountOf(SnapshotEventKind.ProjectileSpawned),
                "a round whose trajectory sweeps the observer must be delivered even though its muzzle "
                + "flash is far outside sight — this is exactly the hole Р32 closes");

            // Outbound from the same invisible muzzle: nothing on the segment
            // ever comes close, so nothing is delivered.
            var w2 = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w2, 0, float2.zero);
            w2.SpawnProjectileForTest(ProjectileOwner.Player, muzzle, new float2(speed, 0f),
                cfg.Hero.MuzzleHeight, 0f, cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime);

            var asm2 = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            AssembledFrame outbound = BuildOne(asm2, w2, cfg, 0, 0, 0);
            Assert.AreEqual(0, outbound.CountOf(SnapshotEventKind.ProjectileSpawned),
                "a round flying AWAY from the observer must not be delivered — otherwise the filter is "
                + "'anything within a round's reach', which is most of the arena");
            Assert.AreEqual(1, outbound.CountOf(SnapshotEventKind.ShotHeard),
                "witness: the same shot IS audible, so the frame is not simply empty");
        }

        // --- 14: ProjectileShooter_AlwaysOnItsOwnTrajectory ---

        [Test]
        public void ProjectileShooter_GetsItsOwnRound_WithNoSpecialCase()
        {
            // task-28-brief §2.5 claims the shooter passes the trajectory
            // filter for free, because the segment BEGINS at the shooter's own
            // muzzle — so no owner carve-out is written. A claim like that is
            // worth exactly as much as the test that pins it.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, 55f));

            float speed = cfg.Weapon.ProjectileSpeed;
            // Player 0 fires straight up +Y, i.e. away from nothing in
            // particular; the round's own owner must still see it.
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(0.5f, 0f), new float2(0f, speed),
                cfg.Hero.MuzzleHeight, 0f, cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ownerIndex: 0);

            var asm = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            AssembledFrame own = BuildOne(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, own.CountOf(SnapshotEventKind.ProjectileSpawned),
                "a player must always receive its own round — the segment starts at its own muzzle");
            Assert.IsTrue(own.TryFirstOf(SnapshotEventKind.ProjectileSpawned, out int idx));
            Assert.AreEqual((byte)0, own.Payloads[idx].PlayerIndex,
                "and the payload names the shooter, so the client can tell its own tracer from another's");
        }

        // --- 15: ProjectileEnded_GoesToSpawnSubscribers (plan) ---

        [Test]
        public void ProjectileEnded_GoesToSpawnSubscribers()
        {
            // Р28: a round's ending goes to everyone who received its SPAWN,
            // by id, with no visibility check on the point of contact — that
            // point is the past, not a live track on a target. The set is
            // per-connection, and it closes when the ending is queued.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            // Connection 1's viewpoint is nowhere near the round.
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, -60f));

            float speed = cfg.Weapon.ProjectileSpeed;
            var muzzle = new float2(20f, 0f);
            int roundId = w.SpawnProjectileForTest(ProjectileOwner.Player, muzzle, new float2(speed, 0f),
                cfg.Hero.MuzzleHeight, 0f, cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime);

            var asm = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 2);
            asm.BeginTick(w);
            var subscriber = AssembledFrame.Decode(asm.BufferFor(0), asm.BuildFor(0, 0, 0, AsmEpoch), cfg);
            var bystander = AssembledFrame.Decode(asm.BufferFor(1), asm.BuildFor(1, 1, 1, AsmEpoch), cfg);
            Assert.AreEqual(1, subscriber.CountOf(SnapshotEventKind.ProjectileSpawned),
                "test setup: connection 0 must be on the round's trajectory");
            Assert.AreEqual(0, bystander.CountOf(SnapshotEventKind.ProjectileSpawned),
                "test setup: connection 1 must NOT be, or the negative half below proves nothing");

            // The round dies far from both, on a point neither can see.
            var contact = new float2(0f, 62f);
            Assert.Greater(math.distance(contact, float2.zero),
                cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the contact point is invisible even to the subscriber");
            w.ClearEvents();
            w.Emit(SimEventKind.ProjectileBlocked, contact, roundId, default, 1.1f, hitDir: new float2(0f, -1f));

            asm.BeginTick(w);
            var endSub = AssembledFrame.Decode(asm.BufferFor(0), asm.BuildFor(0, 0, 0, AsmEpoch), cfg);
            var endBy = AssembledFrame.Decode(asm.BufferFor(1), asm.BuildFor(1, 1, 1, AsmEpoch), cfg);

            Assert.AreEqual(1, endSub.CountOf(SnapshotEventKind.ProjectileEnded),
                "the subscriber gets the ending even though the point of contact is invisible to it");
            Assert.IsTrue(endSub.TryFirstOf(SnapshotEventKind.ProjectileEnded, out int e));
            Assert.AreEqual(roundId, endSub.Payloads[e].Id, "and the payload names the round that ended");
            Assert.AreEqual(ProjectileEndKind.Blocked, endSub.Payloads[e].EndKind);
            Assert.AreEqual(0, endBy.CountOf(SnapshotEventKind.ProjectileEnded),
                "a connection that never received the spawn must not receive the ending");

            // The subscription closed with that ending: a second one for the
            // same id finds nobody.
            w.ClearEvents();
            w.Emit(SimEventKind.ProjectileExpired, contact, roundId, default, 0f);
            asm.BeginTick(w);
            var again = AssembledFrame.Decode(asm.BufferFor(0), asm.BuildFor(0, 0, 0, AsmEpoch), cfg);
            Assert.AreEqual(0, again.CountOf(SnapshotEventKind.ProjectileEnded),
                "the subscription is closed by the ending it was waiting for — a round cannot end twice");
        }

        // --- 16: MobDied routing, all three arms ---

        [Test]
        public void MobDied_ByVisibility_ByKillingRoundSubscription_AndNeitherForAContactKill()
        {
            // Table Р28 routes MobDied "by the mob's own visibility, UNION the
            // round subscription". The union is what covers the 3 m band where
            // a mob is still tracked through exit hysteresis (SightRadius +
            // ExitHysteresis) while its spawn was only delivered out to
            // SightRadius — and, more visibly, the observer who watched a
            // tracer fly off and is owed its outcome.
            //
            // The killing round is joined through the tick's own event buffer:
            // the last ProjectileHit naming this mob before its death carries
            // the round in SecondaryEntityId. A contact kill has no such hit,
            // so it falls back to plain visibility — which is correct, not a
            // gap: nobody watched a tracer that never existed.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, -60f));

            float speed = cfg.Weapon.ProjectileSpeed;
            var muzzle = new float2(20f, 0f);
            int roundId = w.SpawnProjectileForTest(ProjectileOwner.Player, muzzle, new float2(speed, 0f),
                cfg.Hero.MuzzleHeight, 0f, cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime);

            var asm = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 2);
            asm.BeginTick(w);
            Assert.AreEqual(1,
                AssembledFrame.Decode(asm.BufferFor(0), asm.BuildFor(0, 0, 0, AsmEpoch), cfg)
                    .CountOf(SnapshotEventKind.ProjectileSpawned),
                "test setup: connection 0 subscribes to the round");
            Assert.AreEqual(0,
                AssembledFrame.Decode(asm.BufferFor(1), asm.BuildFor(1, 1, 1, AsmEpoch), cfg)
                    .CountOf(SnapshotEventKind.ProjectileSpawned),
                "test setup: connection 1 does not");

            // The round kills a mob neither connection can see.
            var deathPos = new float2(60f, 0f);
            Assert.Greater(math.distance(deathPos, float2.zero),
                cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the mob dies out of sight of BOTH connections");
            const int mobId = 733;
            w.ClearEvents();
            w.Emit(SimEventKind.ProjectileHit, deathPos, mobId, MobType.Chaser, 12f,
                zone: HitZone.Body, hitDir: new float2(1f, 0f), playerIndex: 0,
                secondaryEntityId: roundId);
            w.Emit(SimEventKind.MobDied, deathPos, mobId, MobType.Chaser, 12f,
                zone: HitZone.Body, hitDir: new float2(1f, 0f), playerIndex: 0);

            asm.BeginTick(w);
            var watcher = AssembledFrame.Decode(asm.BufferFor(0), asm.BuildFor(0, 0, 0, AsmEpoch), cfg);
            var stranger = AssembledFrame.Decode(asm.BufferFor(1), asm.BuildFor(1, 1, 1, AsmEpoch), cfg);

            Assert.AreEqual(1, watcher.CountOf(SnapshotEventKind.MobDied),
                "the union arm: a subscriber to the KILLING round is owed the outcome even with the "
                + "mob itself invisible (the join reads ProjectileHit.SecondaryEntityId, never EntityId, "
                + "which holds the victim)");
            Assert.AreEqual(0, stranger.CountOf(SnapshotEventKind.MobDied),
                "and a connection with neither sight of the mob nor the round's spawn gets nothing");

            // A contact kill of an invisible mob reaches nobody — there is no
            // round to have been watched.
            var w2 = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w2, 0, float2.zero);
            var asm2 = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            BuildOne(asm2, w2, cfg, 0, 0, 0);   // one frame so a previous-tick set exists
            w2.ClearEvents();
            w2.Emit(SimEventKind.MobDied, deathPos, mobId, MobType.Chaser, 15f,
                zone: HitZone.Body, playerIndex: ProjectileIds.NoOwner);
            Assert.AreEqual(0, BuildOne(asm2, w2, cfg, 0, 0, 0).CountOf(SnapshotEventKind.MobDied),
                "a contact kill out of sight has no tracer behind it, so the union adds nothing");

            // Visibility arm, with the mob in plain sight: the ordinary route
            // still works, so the two assertions above are about the UNION and
            // not about MobDied being broken.
            var w3 = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w3, 0, float2.zero);
            int liveMob = w3.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            var asm3 = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            BuildOne(asm3, w3, cfg, 0, 0, 0);   // tick N: the mob is tracked
            w3.DamageMob(0, 1e9f, new float2(5f, 0f), HitZone.Body, default, ownerIndex: 0);
            Assert.AreEqual(0, w3.MobCount, "test setup: the mob is really gone from the world");
            Assert.AreEqual(1, BuildOne(asm3, w3, cfg, 0, 0, 0).CountOf(SnapshotEventKind.MobDied),
                "the visibility arm reads the PREVIOUS tick's set — the corpse is swap-removed the same "
                + "tick it dies, so a current-tick set could never hold it and no death would ever ship");
            Assert.Greater(liveMob, 0);
        }

        // --- 17: ShotHeard position, both source rails ---

        [Test]
        public void ShotHeard_ExactForAVisibleShooter_CoarseOtherwise_AndAlwaysCoarseForAMobsRound()
        {
            var cfg = TestConfigs.Open();
            float sight = cfg.Visibility.SightRadius;
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            var asm = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            float speed = cfg.Weapon.ProjectileSpeed;

            // Tick N: shooter comfortably in sight, so the exit hysteresis is
            // armed for tick N+1.
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(sight - 3f, 0f));
            BuildOne(asm, w, cfg, 0, 0, 0);

            // Tick N+1: shooter inside the hysteresis band — still VISIBLE, but
            // its round's trajectory (fired outbound) never comes within
            // SightRadius, so this observer hears the shot instead of seeing
            // the tracer. That is the only configuration in which a visible
            // shooter produces a ShotHeard at all: the segment starts at the
            // muzzle, so a shooter inside plain SightRadius always yields the
            // tracer instead.
            var bandPos = new float2(sight + cfg.Visibility.ExitHysteresis * 0.5f, 0f);
            TestWorlds.RelocatePlayerForTest(w, 1, bandPos);
            w.ClearEvents();
            w.SpawnProjectileForTest(ProjectileOwner.Player, bandPos, new float2(speed, 0f),
                cfg.Hero.MuzzleHeight, 0f, cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ownerIndex: 1);

            AssembledFrame seen = BuildOne(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(0, seen.CountOf(SnapshotEventKind.ProjectileSpawned),
                "test setup: the outbound round must NOT qualify on trajectory");
            Assert.AreEqual(1, seen.CountOf(SnapshotEventKind.ShotHeard));
            Assert.IsTrue(seen.TryFirstOf(SnapshotEventKind.ShotHeard, out int i1));
            Assert.That(math.distance(seen.Events[i1].Pos, bandPos), Is.LessThan(0.01f),
                "a VISIBLE shooter's report carries the exact muzzle position — the shooter's body is "
                + "already replicated at full precision, so coarsening protects nothing");
            Assert.AreEqual((byte)1, seen.Payloads[i1].PlayerIndex, "and names the shooter");

            // A mob's round: the event carries no shooter at all (EntityId is
            // the round), so there is no source to check visibility on and the
            // position is ALWAYS coarsened.
            var w2 = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w2, 0, float2.zero);
            var mobMuzzle = new float2(0f, sight + 5f);
            Assert.Greater(math.length(mobMuzzle), sight + cfg.Visibility.ExitHysteresis);
            Assert.LessOrEqual(math.length(mobMuzzle), cfg.Visibility.HearRadius);
            w2.SpawnProjectileForTest(ProjectileOwner.Mob, mobMuzzle, new float2(0f, cfg.Gunner.ProjectileSpeed),
                cfg.Gunner.MuzzleHeight, 0f, cfg.Gunner.ProjectileDamage, cfg.Gunner.ProjectileRadius,
                cfg.Gunner.ProjectileLifetime, ownerIndex: ProjectileIds.NoOwner);

            var asm2 = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            AssembledFrame mobShot = BuildOne(asm2, w2, cfg, 0, 0, 0);
            Assert.AreEqual(1, mobShot.CountOf(SnapshotEventKind.ShotHeard));
            Assert.IsTrue(mobShot.TryFirstOf(SnapshotEventKind.ShotHeard, out int i2));
            Assert.That(math.distance(mobShot.Events[i2].Pos,
                    VisibilitySystem.QuantizeAudiblePos(mobMuzzle, cfg.Visibility)),
                Is.LessThan(0.01f), "a mob's report is always coarsened");
            Assert.Greater(math.distance(mobShot.Events[i2].Pos, mobMuzzle), 0.01f,
                "witness: the coarse position really does differ from the true one");
            Assert.AreEqual(ProjectileIds.NoOwner, mobShot.Payloads[i2].PlayerIndex,
                "and it names no shooter, which is precisely why it can never be exact");
        }

        // --- 18: identity vs viewpoint (carryover-t28.md §5) ---

        [Test]
        public void OwnerChannel_FollowsIdentity_WhileTheVisibilitySetFollowsViewpoint()
        {
            // carryover-t28.md §5. `observerIndex` USED TO carry two roles at
            // once — WHO this connection is (the Owner channel, the own-death
            // carve-out) and WHERE it looks from (the visibility set). While a
            // player watches its own body the two agree; spectating (Р70/Р88)
            // splits them, and the assembler must not merge them back.
            //
            // The split is expressed as two parameters. Since Task 42b BOTH
            // reach `ShouldDeliver`: `identityIndex` answers its ownership
            // questions, `viewpointIndex` feeds Compute AND the seam's own
            // hearing read. This fixture pins
            // the two channels that stay on IDENTITY (Owner, the own-death
            // carve-out); the Audible branch's own earshot fix — the seam's
            // known limit until Task 42b, which measured hearing distance from
            // the IDENTITY's own position instead of the viewpoint's — is
            // pinned separately below, in the Task 42b section of this file.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, 100f));    // the viewpoint, far away
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, -100f));   // and a third body, elsewhere
            Assert.Greater(math.distance(w.PlayerAt(0).Pos, w.PlayerAt(1).Pos),
                cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the identity's own body is INVISIBLE from the viewpoint");

            var asm = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);

            // Owner channel: player 0's own private feedback reaches it while
            // it looks through player 1's eyes.
            w.Emit(SimEventKind.StaminaDenied, w.PlayerAt(0).Pos, 0, default, 12f, playerIndex: 0);
            // ... and another player's identical event does not.
            w.Emit(SimEventKind.StaminaDenied, w.PlayerAt(1).Pos, 0, default, 12f, playerIndex: 1);

            AssembledFrame f = BuildOne(asm, w, cfg, 0, identityIndex: 0, viewpointIndex: 1);
            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.StaminaDenied),
                "exactly one StaminaDenied — the connection's OWN, keyed on identity, never on viewpoint");

            // Own death: delivered to its owner even though the viewpoint
            // cannot see the body, while a third party's death at the same
            // invisible distance is not.
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDied, w.PlayerAt(0).Pos, 0, default, 0f,
                zone: HitZone.Body, playerIndex: 0);
            w.Emit(SimEventKind.PlayerDied, w.PlayerAt(2).Pos, 2, default, 0f,
                zone: HitZone.Body, playerIndex: 2);

            AssembledFrame d = BuildOne(asm, w, cfg, 0, identityIndex: 0, viewpointIndex: 1);
            Assert.AreEqual(1, d.CountOf(SnapshotEventKind.PlayerDied),
                "one death only: one's own always arrives (Р28's carve-out), a stranger's still needs "
                + "the viewpoint to have seen it");
            Assert.IsTrue(d.TryFirstOf(SnapshotEventKind.PlayerDied, out int di));
            Assert.AreEqual((byte)0, d.Payloads[di].PlayerIndex, "and it is the connection's OWN death");
        }

        // ================= Task 42b: identity vs viewpoint earshot =================
        //
        // Task 42a made `identityIndex`/`viewpointIndex` genuinely diverge for
        // the first time (MatchServer.OnSpectateRequest can move a slot's
        // viewpoint away from its identity); the five fixtures below are the
        // ones that pin what that split means for event delivery. Four of them
        // could not exist before it at all, because ShouldDeliver only ever
        // took ONE index; the fifth (23) goes through the assembler's own
        // direct ShotHeard route, which never used the seam. Each pins one of
        // the two things that
        // change (the Audible branch's hearing position) or must NOT change
        // (the Owner channel, the own-death carve-out) under the split.

        // --- 19: AudibleChannel_MeasuresFromViewpoint_NotIdentity (Task 42b) ---

        [Test]
        public void AudibleChannel_MeasuresFromViewpoint_NotIdentity()
        {
            // The defect this task closes (recorded in SnapshotAssembler's own
            // class doc, WAS/NOW): a dead observer spectating a teammate across the
            // arena must hear gunfire around the TEAMMATE, not around its own
            // corpse. Distances are fixture expressions off HearRadius (Р56),
            // never literals, so the arithmetic states its own premise instead
            // of hiding it in a magic number.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            var farPos = new float2(cfg.Visibility.HearRadius + 1f, 0f);    // outside earshot
            var closePos = new float2(cfg.Visibility.HearRadius - 1f, 0f); // inside earshot
            TestWorlds.RelocatePlayerForTest(w, 0, farPos);    // identity: far from the shot
            TestWorlds.RelocatePlayerForTest(w, 1, closePos);  // viewpoint: close to the shot
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, 5000f)); // the actor, elsewhere entirely

            var ev = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 2, Pos = float2.zero };
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg)); // actor not visible — hearing path only

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, identityIndex: 0, viewpointIndex: 1, w, observerSet,
                    cfg.Visibility, out float2 pos),
                "the viewpoint (player 1) is within HearRadius of the shot — it must be delivered even though "
                + "the identity's own body (player 0) sits far outside it");
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(ev.Pos, cfg.Visibility), pos);

            // Positive witness (Урок 129): the SAME call with the two indices
            // merged back onto the identity — exactly what every call site did
            // before this task — must refuse, proving the delivery above
            // actually exercises the new parameter rather than some other path
            // that happens to always say yes.
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, identityIndex: 0, viewpointIndex: 0, w, observerSet,
                    cfg.Visibility, out float2 refusedPos),
                "witness: with viewpointIndex folded back onto identityIndex (player 0's own far position), "
                + "the same shot must NOT be delivered — otherwise the positive result above proves nothing "
                + "about which parameter is actually read");
            Assert.AreEqual(float2.zero, refusedPos);
        }

        // --- 20: AudibleChannel_RefusesWhenViewpointIsFar_EvenIfIdentityIsClose (Task 42b) ---

        [Test]
        public void AudibleChannel_RefusesWhenViewpointIsFar_EvenIfIdentityIsClose()
        {
            // Mirror of the fixture above. An implementation that swapped the
            // two new parameters (identityIndex feeding the hearing check
            // instead of viewpointIndex, silently unchanged from before this
            // task) would still pass 19's positive half by reading the CLOSE
            // identity there — both mirrors are required to pin a parameter
            // swap in both directions (this task's own brief, §2.4).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            var closePos = new float2(cfg.Visibility.HearRadius - 1f, 0f); // inside earshot
            var farPos = new float2(cfg.Visibility.HearRadius + 1f, 0f);   // outside earshot
            TestWorlds.RelocatePlayerForTest(w, 0, closePos); // identity: close to the shot
            TestWorlds.RelocatePlayerForTest(w, 1, farPos);   // viewpoint: far from the shot
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, 5000f)); // the actor, elsewhere entirely

            var ev = new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 2, Pos = float2.zero };
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg));

            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, identityIndex: 0, viewpointIndex: 1, w, observerSet,
                    cfg.Visibility, out float2 refusedPos),
                "the viewpoint (player 1) is outside HearRadius — it must be refused even though the "
                + "identity's own body (player 0) sits comfortably within earshot");
            Assert.AreEqual(float2.zero, refusedPos);

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, identityIndex: 0, viewpointIndex: 0, w, observerSet,
                    cfg.Visibility, out float2 pos),
                "witness: with viewpointIndex folded back onto identityIndex (player 0's own close position), "
                + "the same shot IS delivered — the refusal above is really about the far viewpoint, not some "
                + "other gate");
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(ev.Pos, cfg.Visibility), pos);
        }

        // --- 21: OwnerChannel_StaysOnIdentity_EvenAcrossSplitIndices (Task 42b) ---

        [Test]
        public void OwnerChannel_StaysOnIdentity_EvenAcrossSplitIndices()
        {
            // This task's own decision record, §2.2's first "must NOT change"
            // item: Owner is private feedback belonging to the IDENTITY, never
            // the viewpoint — keying it on viewpointIndex would leak the
            // spectated player's Stamina economy to whoever is spectating them
            // (spec Р28).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg));

            // Half 1: the event belongs to the IDENTITY (player 0) while the
            // viewpoint is player 1 — must still be delivered.
            var ownEvent = new SimEvent { Kind = SimEventKind.StaminaDenied, PlayerIndex = 0, Pos = new float2(3f, -1f) };
            Assert.IsTrue(EventRelevance.ShouldDeliver(ownEvent, identityIndex: 0, viewpointIndex: 1, w,
                    observerSet, cfg.Visibility, out float2 ownPos),
                "the identity's OWN StaminaDenied must reach it, even while its viewpoint looks through "
                + "another player");
            Assert.AreEqual(ownEvent.Pos, ownPos);

            // Half 2 (the discriminating counterpart): the event belongs to the
            // VIEWPOINT (player 1), not the identity — a bug that keyed Owner
            // on viewpointIndex instead would wrongly deliver this.
            var viewpointsOwnEvent = new SimEvent { Kind = SimEventKind.StaminaDenied, PlayerIndex = 1, Pos = new float2(3f, -1f) };
            Assert.IsFalse(EventRelevance.ShouldDeliver(viewpointsOwnEvent, identityIndex: 0, viewpointIndex: 1, w,
                    observerSet, cfg.Visibility, out float2 refusedPos),
                "the VIEWPOINT's own StaminaDenied must NOT reach this connection — Owner is keyed on "
                + "identity, never on viewpoint, or a spectator would learn the spectated player's Stamina "
                + "economy (Р28)");
            Assert.AreEqual(float2.zero, refusedPos);
        }

        // --- 22: OwnDeathCarveOut_StaysOnIdentity_EvenAcrossSplitIndices (Task 42b) ---

        [Test]
        public void OwnDeathCarveOut_StaysOnIdentity_EvenAcrossSplitIndices()
        {
            // §2.2's second "must NOT change" item: "my own death" is a
            // property of identity, not of where the connection currently
            // looks — the carve-out must not silently move onto viewpointIndex.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg)); // absent from the set on both halves

            // Half 1: PlayerDied belongs to the IDENTITY (player 0) — reaches
            // it even though the ordinary visibility gate (empty set) would
            // refuse it, and even while the viewpoint is player 1.
            var ownDeath = new SimEvent
            {
                Kind = SimEventKind.PlayerDied, PlayerIndex = 0, EntityId = 1, Pos = new float2(-4f, 21f)
            };
            Assert.IsTrue(EventRelevance.ShouldDeliver(ownDeath, identityIndex: 0, viewpointIndex: 1, w,
                    observerSet, cfg.Visibility, out float2 ownPos),
                "the identity's own PlayerDied must reach it via the carve-out, even while its viewpoint is "
                + "another player");
            Assert.AreEqual(ownDeath.Pos, ownPos);

            // Half 2 (the discriminating counterpart): PlayerDied belongs to
            // the VIEWPOINT (player 1), not the identity — a bug that keyed
            // the carve-out on viewpointIndex instead would wrongly deliver
            // this too, defeating the ordinary visibility gate for someone
            // else's death.
            var viewpointsDeath = new SimEvent
            {
                Kind = SimEventKind.PlayerDied, PlayerIndex = 1, EntityId = 2, Pos = new float2(-4f, 21f)
            };
            Assert.IsFalse(EventRelevance.ShouldDeliver(viewpointsDeath, identityIndex: 0, viewpointIndex: 1, w,
                    observerSet, cfg.Visibility, out float2 refusedPos),
                "the VIEWPOINT's own PlayerDied must NOT bypass the visibility gate — the carve-out is "
                + "identity-only, never viewpoint");
            Assert.AreEqual(float2.zero, refusedPos);
        }

        // --- 23: ShotHeard_MeasuresFromViewpoint_NotIdentity (Task 42b, assembler-level) ---

        [Test]
        public void ShotHeard_MeasuresFromViewpoint_NotIdentity()
        {
            // §2.3's third call-site fix: SnapshotAssembler.RouteEvents routes
            // ShotHeard DIRECTLY (past EventRelevance — one SimEvent feeds two
            // wire events with different rules), so its own hearing-distance
            // read needed its own fix and is not exercised by fixtures 19-20
            // above at all. Beyond this task's own four-fixture minimum: a
            // production line changed with no discriminating test would be
            // exactly the silent gap Task 42b exists to close for the OTHER
            // Audible branch.
            var cfg = TestConfigs.Open();
            var muzzle = float2.zero;
            var farPos = new float2(cfg.Visibility.HearRadius + 1f, 0f);   // identity: outside earshot
            var closePos = new float2(cfg.Visibility.HearRadius - 1f, 0f); // viewpoint: inside earshot
            // Fired AWAY from both candidate observer positions (+X), so the
            // trajectory's closest approach to either one stays at the muzzle
            // itself — comfortably beyond SightRadius + ExitHysteresis for
            // both, meaning ProjectileSpawned never qualifies and this
            // fixture isolates the ShotHeard hearing check alone.
            Assert.Greater(cfg.Visibility.HearRadius - 1f, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the close position must still be outside the tracer's own trajectory gate");

            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, farPos);
            TestWorlds.RelocatePlayerForTest(w, 1, closePos);
            w.SpawnProjectileForTest(ProjectileOwner.Mob, muzzle, new float2(-cfg.Gunner.ProjectileSpeed, 0f),
                cfg.Gunner.MuzzleHeight, 0f, cfg.Gunner.ProjectileDamage, cfg.Gunner.ProjectileRadius,
                cfg.Gunner.ProjectileLifetime, ownerIndex: ProjectileIds.NoOwner);

            var asm = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            AssembledFrame close = BuildOne(asm, w, cfg, 0, identityIndex: 0, viewpointIndex: 1);
            Assert.AreEqual(0, close.CountOf(SnapshotEventKind.ProjectileSpawned),
                "fixture premise: the tracer itself must not qualify, or this is not isolating ShotHeard");
            Assert.AreEqual(1, close.CountOf(SnapshotEventKind.ShotHeard),
                "the viewpoint (player 1) is within HearRadius of the muzzle — the shot must be heard even "
                + "though the identity's own body (player 0) sits far outside it");

            // Witness: the same world, but the viewpoint folded back onto the
            // (far) identity — exactly the pre-Task-42b call shape — refuses.
            var w2 = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w2, 0, farPos);
            TestWorlds.RelocatePlayerForTest(w2, 1, closePos);
            w2.SpawnProjectileForTest(ProjectileOwner.Mob, muzzle, new float2(-cfg.Gunner.ProjectileSpeed, 0f),
                cfg.Gunner.MuzzleHeight, 0f, cfg.Gunner.ProjectileDamage, cfg.Gunner.ProjectileRadius,
                cfg.Gunner.ProjectileLifetime, ownerIndex: ProjectileIds.NoOwner);
            var asm2 = new SnapshotAssembler(cfg, AsmNet(), connectionCount: 1);
            AssembledFrame folded = BuildOne(asm2, w2, cfg, 0, identityIndex: 0, viewpointIndex: 0);
            Assert.AreEqual(0, folded.CountOf(SnapshotEventKind.ShotHeard),
                "witness: with viewpointIndex folded back onto identityIndex (player 0's own far position), "
                + "the same shot must NOT be heard — otherwise the positive result above proves nothing about "
                + "which position is actually read");
        }
    }
}
