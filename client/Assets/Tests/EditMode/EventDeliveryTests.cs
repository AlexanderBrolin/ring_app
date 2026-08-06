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
    ///
    /// Phase Ф5 fix-wave (two phase reviews) added fixture 4c
    /// (MobSpawned_RequiresCurrentTickSet — the mirror image of 4b, and the
    /// reason the "which tick's set" contract is PER-KIND rather than one
    /// blanket rule) and pulled `EntityId` apart from `PlayerIndex` in the two
    /// PlayerDied/PlayerDamaged fixtures: while the two fields carried the
    /// same value, ShouldDeliver could read either one and every assertion
    /// here still passed, so the VICTIM-by-PlayerIndex convention was prose,
    /// not contract.
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
            // Visible/Audible never consult `observerIndex` at all.
            var observerSet = new VisibilitySet(TestWorlds.Capacity(cfg));
            observerSet.Add(VisibilityIds.ForPlayer(1));

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 1, w, observerSet, cfg.Visibility, out float2 ownerPos),
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
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, w, observerSet, cfg.Visibility, out float2 refusedPos),
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

            const byte victimIndex = 1;
            // Phase Ф5 fix-wave (item 9): EntityId is deliberately pulled
            // APART from PlayerIndex here. SimulationWorld emits these two
            // fields with the SAME value for PlayerDied (KillPlayer passes the
            // victim index to both), and while they agreed in this fixture the
            // owner carve-out below could read either one and still pass:
            // `observerIndex == ev.PlayerIndex` -> `== ev.EntityId` survived
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

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, victimIndex, w, observerSet, cfg.Visibility, out float2 ownPos),
                "a player's own death must reach them even when the ordinary visibility gate would refuse it — "
                + "the owner carve-out is keyed on PlayerIndex (the VICTIM), never on EntityId");
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

            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, setA, cfg.Visibility, out float2 deliveredPos),
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
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, w, setAfterDeath, cfg.Visibility, out _),
                "the CURRENT tick's set must not deliver — this is exactly the trap the previous-tick contract exists to avoid");

            // Positive witness: the PREVIOUS tick's set (still holding the
            // subject as visible-now, from before the death) delivers
            // correctly.
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, setBeforeDeath, cfg.Visibility, out float2 deliveredPos),
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
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, w, setBeforeSpawn, cfg.Visibility, out float2 refusedPos),
                "the PREVIOUS tick's set must NOT deliver a MobSpawned — this is exactly the mirror trap of 4b, "
                + "and the reason the caller picks the tick per KIND");
            Assert.AreEqual(float2.zero, refusedPos,
                "a `false` return must carry `default` (zero), same contract as every other refusing branch");

            // Positive arm: the CURRENT tick's set — the one this kind's
            // subject actually lives in — delivers.
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, setAfterSpawn, cfg.Visibility, out float2 deliveredPos),
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
            // Task 21 fix-round 1 (I-1): named variable, not `out _` — pins
            // the same "`deliveredPos` is `default` on `false`" contract on
            // the Audible channel's OUTER-range refusal branch.
            Assert.IsFalse(EventRelevance.ShouldDeliver(evFar, 0, w, observerSet, cfg.Visibility, out float2 refusedPos),
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
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, lingering, cfg.Visibility, out float2 exactPos));
            Assert.AreEqual(pos, exactPos, "an actor merely LINGERING (Contains true, LingerOf > 0) must still get the EXACT position");

            // Half 2 — genuinely not tracked at all: same event, same
            // distance (well within HearRadius), but the actor is absent.
            var untracked = new VisibilitySet(TestWorlds.Capacity(cfg));
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
            var mobVisibleOnly = new VisibilitySet(TestWorlds.Capacity(cfg));
            mobVisibleOnly.Add(mobId);
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, mobVisibleOnly, cfg.Visibility, out float2 pos1),
                "MobDied must be delivered by the MOB's own visibility, not its killer's");
            Assert.AreEqual(pos, pos1);

            // Half 2 (the discriminating counterpart the brief demands): the
            // KILLER is visible, the MOB is not — the same bug would now find
            // the killer's id present and wrongly DELIVER.
            var killerVisibleOnly = new VisibilitySet(TestWorlds.Capacity(cfg));
            killerVisibleOnly.Add(killerVisId);
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, w, killerVisibleOnly, cfg.Visibility, out _),
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
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 0, w, correctSet, cfg.Visibility, out float2 pos1),
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
            Assert.IsFalse(EventRelevance.ShouldDeliver(ev, 0, w, wrongSet, cfg.Visibility, out _),
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
            Assert.IsFalse(EventRelevance.ShouldDeliver(evInvisible, 0, w, observerSet, cfg.Visibility, out float2 refusedPos),
                "a Visible-channel event for an invisible subject must not be delivered, even when it is audible");
            Assert.AreEqual(float2.zero, refusedPos,
                "a `false` return must carry `default` (zero), never the invisible subject's position");

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
            // Rule 9 (task-21-brief.md): observerIndex may point at a dead
            // player (post-mortem observation, Р70) — ShouldDeliver must not
            // special-case that away, and must resolve the observer's own
            // position the same plain way VisibilitySystem.Compute does
            // (w.PlayerAt, no Alive gate).
            //
            // Task 21 fix-round 1 (I-2): observe from index 1, not 0 — EVERY
            // call site in this file that reaches `w.PlayerAt(observerIndex)`
            // happened to pass observerIndex == 0 (lines noted by the
            // review), so a mutant hardcoding `w.PlayerAt(0)` in place of
            // `w.PlayerAt(observerIndex)` agreed with the correct code on
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

            Assert.DoesNotThrow(() => EventRelevance.ShouldDeliver(ev, 1, w, observerSet, cfg.Visibility, out _),
                "a dead observer must not make ShouldDeliver throw");
            Assert.IsTrue(EventRelevance.ShouldDeliver(ev, 1, w, observerSet, cfg.Visibility, out float2 pos),
                "a dead observer must still resolve its OWN position for the audibility gate, not silently fail closed");
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(actorPos, cfg.Visibility), pos);
        }
    }
}
