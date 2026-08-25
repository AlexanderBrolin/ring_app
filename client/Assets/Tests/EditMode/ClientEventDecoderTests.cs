using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 44d: the CLIENT's inverse of the mapping
    /// `SnapshotAssembler` applies on the way out — one wire event turned back
    /// into the `SimEvent` the whole Presentation fan-out already speaks.
    ///
    /// WHY THIS FILE EXISTS AT ALL, restated honestly (owner decision
    /// 2026-08-22, bd `app-xkir`). This doc used to say the mapping lived "in
    /// an assembly this test assembly does not reference and never will" — and
    /// the second half was already false when it was written: `Simulation.
    /// Tests.asmdef` HAS referenced `Ring.Presentation.Net` since Stage 2. The
    /// reference stays; what was wrong was the reason given for the split.
    ///
    /// THE REAL REASON IS THAT THE BACKEND CANNOT BE STOOD UP IN EDITMODE. The
    /// mapping was written in Task 44c inside `NetworkSimBackend`, which is a
    /// `NetworkBehaviour`: reaching any branch of it from a test needs a live
    /// `NetworkManager`, a started transport and a connection, none of which an
    /// EditMode test has — so every branch of that switch was unreachable by
    /// any unit test no matter what the asmdef said. Moving the KNOWLEDGE
    /// rather than the wiring is what made it testable: the decode is a pure
    /// function of a record, its bytes and the config, and the backend now only
    /// calls it. The boundary this file defends is therefore a SOFT one — keep
    /// decisions out of FishNet plumbing (lesson 376) — not an assembly wall.
    ///
    /// EVERY EXPECTATION HERE IS TAKEN FROM THE SENDER, NOT FROM THE RECEIVER.
    /// The pairs below were read off `SnapshotAssembler.BeginTick`'s own switch
    /// and its `Add*` helpers together with `SimEvent`'s per-kind field
    /// conventions; a test written from the decoder's own body would pin
    /// whatever it does today, including a swapped pair. The three places where
    /// the two enumerations genuinely do not line up — one `ProjectileFired`
    /// leaving as two wire kinds, four projectile endings arriving as one, and
    /// the victim-versus-actor conventions of `PlayerIndex` — are each asserted
    /// on both sides rather than described.
    ///
    /// FIXTURES ARE HAND-BUILT AND DELIBERATELY ASYMMETRIC. The local seat is
    /// not the seat any event names, the victim is not slot 0, and the wave
    /// index is not the round id: a number that happens to coincide with
    /// another cannot show which of the two a field was filled from.
    public class ClientEventDecoderTests
    {
        /// The tick the dedup approved this event on — never the frame's own,
        /// which is the whole reason the caller passes it in.
        const uint OriginTick = 493;

        /// This client's own seat. Not 0 (which every unfilled byte would
        /// read as) and not the seat used by any event fixture below.
        const byte LocalSlot = 1;

        /// The seat events are ABOUT. Not 0, so a field left unfilled is
        /// distinguishable from a field filled with this slot.
        const byte OtherSlot = 2;

        /// The round's own id, and the mob's — both far from 0, which is the
        /// "no entity" value `SimEvent.SecondaryEntityId`'s doc pins.
        const int RoundId = 4242;
        const int MobId = 77;

        /// Stage 3 Т32: the two raid entities that ride the wire as a u16 code
        /// (Р278). Both under 65 536 so the truncation the writer applies is
        /// the identity here — a test about the MAPPING must not accidentally
        /// be a test about truncation.
        const int PickupId = 3131;
        const int ContainerId = 909;

        /// Where the record says the event happened. The record's position is
        /// per-connection (the server may coarsen it), so it rides the record
        /// header and never the payload.
        static float2 EventPos => new float2(3.5f, -7.25f);

        static SnapshotBlocks.EventRecord Record(SnapshotEventKind kind)
            => new SnapshotBlocks.EventRecord
            {
                Kind = (byte)kind,
                Seq = 9,
                TickDelta = 0,
                Pos = EventPos,
                PayloadOffset = 0,
                PayloadLength = (byte)SnapshotEvents.PayloadBytesFor(kind),
            };

        static byte[] Buffer(SnapshotEventKind kind)
            => new byte[SnapshotEvents.PayloadBytesFor(kind)];

        /// Decodes `bytes` as `kind` and asserts the decode itself succeeded —
        /// the premise every mapping assertion below rests on.
        static SimEvent Decode(SnapshotEventKind kind, byte[] bytes, in SimConfig cfg)
        {
            Assert.IsTrue(ClientEventDecoder.TryDecode(OriginTick, Record(kind), bytes, in cfg,
                    LocalSlot, out SimEvent e, out _, out SnapshotBlockError refusal),
                $"fixture premise: a well-formed {kind} payload decodes; refusal was {refusal}");
            return e;
        }

        /// The same decode, keeping the SECOND output — the decoded payload
        /// the caller routes by. Separate from `Decode` above so the tests
        /// that are about the `SimEvent` are not made to mention it (fix-round
        /// 1, G-3).
        static SimEvent DecodeWithPayload(SnapshotEventKind kind, byte[] bytes, in SimConfig cfg,
            out SnapshotEventPayload payload)
        {
            Assert.IsTrue(ClientEventDecoder.TryDecode(OriginTick, Record(kind), bytes, in cfg,
                    LocalSlot, out SimEvent e, out payload, out SnapshotBlockError refusal),
                $"fixture premise: a well-formed {kind} payload decodes; refusal was {refusal}");
            return e;
        }

        /// Half a `Quantize.Unit` step against the same `max` the codec was
        /// given — the most a round trip through that codec can be off by
        /// (`Quantize`'s own doc: round-to-nearest never exceeds half its own
        /// cell). An expression rather than a hand-picked number, so a change
        /// to the fixture's `MaxHp`/`MaxAimHeight`/`StaminaMax` moves the
        /// tolerance with it instead of leaving it silently loose or silently
        /// impossible (fix-round 1, M-8).
        static float UnitTolerance(float max) => max / 255f / 2f;

        // ------------------------------------------------------------------
        // The two wire kinds one ProjectileFired leaves as.
        // ------------------------------------------------------------------

        [Test]
        public void ProjectileSpawned_CarriesTheRoundTheShooterAndTheFireAngle()
        {
            // Sender: AddProjectileSpawned writes ev.EntityId as the round's
            // id, ev.PlayerIndex as the shooter and the round's velocity as a
            // unit direction plus a speed. SimEvent's own doc puts the shot's
            // sim-plane velocity ANGLE in `Amount` for ProjectileFired, which
            // is what the direction has to be turned back into.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ProjectileSpawned);
            SnapshotEvents.WriteProjectileSpawned(bytes, RoundId, OtherSlot,
                new float2(0f, 1f), horizSpeed: 20f, velZ: 0f, height: 1.2f, in cfg);

            SimEvent e = Decode(SnapshotEventKind.ProjectileSpawned, bytes, in cfg);

            Assert.AreEqual(SimEventKind.ProjectileFired, e.Kind,
                "SimEvent.Kind — a spawn seen from close by IS the shot, and the whole fan-out "
                + "(muzzle flash, casing, shot sound) is subscribed to that one kind");
            Assert.AreEqual(RoundId, e.EntityId,
                "SimEvent.EntityId must be the ROUND's id for ProjectileFired — it is what the "
                + "tracer is later retired by");
            Assert.AreEqual(OtherSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be the SHOOTER (the ACTOR convention this kind carries "
                + "on the simulation side), not this client's own seat");
            Assert.AreEqual(ProjectileOwner.Player, e.Owner,
                "SimEvent.Owner must be Player for a round a player fired — the casing and the "
                + "shot clip are gated on exactly this field");
            // The tolerance here is float noise and deliberately far TIGHTER
            // than `Quantize.Dir`'s own step (2*pi/256 ~ 0.0245 rad): the
            // fixture's `(0, 1)` is exactly `pi/2`, which lands on code 192
            // and decodes back to exactly `pi/2`, so a whole step of slack
            // would accept the neighboring code as well (M-8).
            Assert.AreEqual(math.PI / 2f, e.Amount, 1e-3f,
                "SimEvent.Amount must be the fire ANGLE in radians (atan2 of the direction the "
                + "wire carried), not the speed and not zero");
        }

        [Test]
        public void ProjectileSpawned_FromAGunnersRound_IsOwnedByTheMob()
        {
            // The discriminator is the owner byte alone: the wire has no
            // separate "who" field, and `ProjectileIds.NoOwner` is what the
            // assembler writes for a mob's round.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ProjectileSpawned);
            SnapshotEvents.WriteProjectileSpawned(bytes, RoundId, ProjectileIds.NoOwner,
                new float2(1f, 0f), horizSpeed: 14f, velZ: 0f, height: 1f, in cfg);

            SimEvent e = Decode(SnapshotEventKind.ProjectileSpawned, bytes, in cfg);

            Assert.AreEqual(ProjectileOwner.Mob, e.Owner,
                "SimEvent.Owner must be Mob when the wire named no player — bd app-ai2: a gunner's "
                + "shot that reads as the player's spawns the player's shell casing and eats the "
                + "predicted-shot latch");
            Assert.AreEqual(ProjectileIds.NoOwner, e.PlayerIndex,
                "SimEvent.PlayerIndex must stay the no-owner sentinel rather than becoming a real "
                + "seat");
        }

        [Test]
        public void ShotHeard_IsTheSameSimKindWithNoRoundAndNoAngle()
        {
            // Sender: AddShotHeard writes ONE byte, the shooter's slot. There
            // is no id and no direction on the wire for the audible variant —
            // the whole point of it is that the listener cannot see the shot.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ShotHeard);
            SnapshotEvents.WriteShotHeard(bytes, OtherSlot, in cfg);

            SimEvent e = Decode(SnapshotEventKind.ShotHeard, bytes, in cfg);

            Assert.AreEqual(SimEventKind.ProjectileFired, e.Kind,
                "SimEvent.Kind — the audible variant maps back to the same sim kind, because a "
                + "connection receives one or the other for a given shot and this one exists to be "
                + "heard AS a shot");
            Assert.AreEqual(OtherSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be the shooter the one payload byte named");
            Assert.AreEqual(0, e.EntityId,
                "SimEvent.EntityId must stay 0: this variant carries no round id, and inventing "
                + "one would open a tracer nothing will ever close");
            Assert.AreEqual(0f, e.Amount, 0f,
                "SimEvent.Amount must be exactly zero — the wire carries no direction for a shot "
                + "that was only heard, and any angle here would be invented");
            Assert.AreEqual(ProjectileOwner.Player, e.Owner,
                "SimEvent.Owner must be Player for a shot a player fired — the positive witness "
                + "for the mob case beside it (fix-round 1, G-4), and the field the casing and "
                + "the shot clip are gated on");
        }

        [Test]
        public void ShotHeard_FromAGunnersRound_IsOwnedByTheMob()
        {
            // A SEPARATE LINE OF THE DECODER from the ProjectileSpawned case
            // above, and that is the whole point of this test: the audible
            // variant derives `Owner` in its own branch, so the covered branch
            // next door proves nothing about it (fix-round 1, G-4). What the
            // wrong answer costs is bd app-ai2, reached from the other side: a
            // gunner's shot that this client only HEARD, read as the player's,
            // drops the player's shell casing on the floor for the rest of the
            // match, plays the player's own shot clip out of its
            // MinSfxInterval budget, and can eat the predicted-shot latch.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ShotHeard);
            SnapshotEvents.WriteShotHeard(bytes, ProjectileIds.NoOwner, in cfg);

            SimEvent e = Decode(SnapshotEventKind.ShotHeard, bytes, in cfg);

            Assert.AreEqual(ProjectileOwner.Mob, e.Owner,
                "SimEvent.Owner must be Mob when the one payload byte named no player — "
                + "ProjectileOwner.Player is the enum's zero, so a branch that forgot this line "
                + "would report every heard gunshot as the player's own");
            Assert.AreEqual(ProjectileIds.NoOwner, e.PlayerIndex,
                "SimEvent.PlayerIndex must stay the no-owner sentinel rather than becoming a real "
                + "seat");
        }

        // ------------------------------------------------------------------
        // The one wire kind four projectile endings arrive as.
        // ------------------------------------------------------------------

        [Test]
        public void ProjectileEnded_Blocked_IsTheRoundAndItsContactHeight()
        {
            // Sender: ProjectileBlocked -> (ev.EntityId, Blocked, HitZone.None,
            // ev.Height) — and for that sim kind `Height` (app-88jb Т3) is the
            // contact height, which is the field the height must come back into.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ProjectileEnded);
            SnapshotEvents.WriteProjectileEnded(bytes, RoundId, ProjectileEndKind.Blocked,
                HitZone.None, height: 1.5f, in cfg);

            SimEvent e = Decode(SnapshotEventKind.ProjectileEnded, bytes, in cfg);

            Assert.AreEqual(SimEventKind.ProjectileBlocked, e.Kind,
                "SimEvent.Kind — a round stopped by geometry sparks on a wall, which is a "
                + "different ending from every other one this wire kind carries");
            Assert.AreEqual(RoundId, e.EntityId,
                "SimEvent.EntityId must be the ROUND for this ending — ProjectileBlocked has no "
                + "victim, so the round keeps the primary field");
            Assert.AreEqual(1.5f, e.Height, UnitTolerance(cfg.Hero.MaxAimHeight),
                "SimEvent.Height must be the contact HEIGHT for this ending, which is what the "
                + "spark is placed at");
        }

        [Test]
        public void ProjectileEnded_Expired_IsTheRoundAndNothingElse()
        {
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ProjectileEnded);
            SnapshotEvents.WriteProjectileEnded(bytes, RoundId, ProjectileEndKind.Expired,
                HitZone.None, height: 0f, in cfg);

            SimEvent e = Decode(SnapshotEventKind.ProjectileEnded, bytes, in cfg);

            Assert.AreEqual(SimEventKind.ProjectileExpired, e.Kind,
                "SimEvent.Kind — a round that simply ran out fades rather than sparking, and "
                + "confusing the two shows an impact where nothing was hit");
            Assert.AreEqual(RoundId, e.EntityId,
                "SimEvent.EntityId must be the ROUND for this ending too");
        }

        [Test]
        public void ProjectileEnded_HitMob_PutsTheRoundInTheSecondaryFieldAndLeavesTheVictimUnclaimed()
        {
            // Sender: ProjectileHit -> AddProjectileEnded(ev.SecondaryEntityId,
            // HitMob, ev.Zone, 0). The wire carries the ROUND, never the mob:
            // `SimEvent.EntityId` is the victim's id for this sim kind and the
            // victim is simply not on the wire, so 0 has to stay 0 — safe here
            // and only here, because entity ids are minted from a counter that
            // starts at 1.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ProjectileEnded);
            SnapshotEvents.WriteProjectileEnded(bytes, RoundId, ProjectileEndKind.HitMob,
                HitZone.Head, height: 0f, in cfg);

            SimEvent e = Decode(SnapshotEventKind.ProjectileEnded, bytes, in cfg);

            Assert.AreEqual(SimEventKind.ProjectileHit, e.Kind,
                "SimEvent.Kind — a round that landed on a mob, which is a different impact from "
                + "one that landed on a player");
            Assert.AreEqual(RoundId, e.SecondaryEntityId,
                "SimEvent.SecondaryEntityId must be the ROUND's id: that is the convention this "
                + "sim kind uses, and it is the id the tracer is retired by");
            Assert.AreEqual(0, e.EntityId,
                "SimEvent.EntityId must stay 0 — the victim mob is not on the wire, and putting "
                + "the round here instead would make a hitflash look the mob up by a round's id");
            Assert.AreEqual(HitZone.Head, e.Zone,
                "SimEvent.Zone must be the zone the wire carried — it picks the feedback");
        }

        [Test]
        public void ProjectileEnded_HitPlayer_IsItsOwnKindAndNotTheMobsOne()
        {
            // Sender: ProjectileHitPlayer -> AddProjectileEnded(...,
            // HitPlayer, ...). Reusing HitMob would tell the client a round
            // that landed on a player ended on a mob — the assembler's own
            // comment says so, and it is why the end kind has its own value.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ProjectileEnded);
            SnapshotEvents.WriteProjectileEnded(bytes, RoundId, ProjectileEndKind.HitPlayer,
                HitZone.Body, height: 0f, in cfg);

            SimEvent e = Decode(SnapshotEventKind.ProjectileEnded, bytes, in cfg);

            Assert.AreEqual(SimEventKind.ProjectileHitPlayer, e.Kind,
                "SimEvent.Kind — a hit on a player must not spawn a mob's flesh impact");
            Assert.AreEqual(RoundId, e.SecondaryEntityId,
                "SimEvent.SecondaryEntityId must be the ROUND's id, exactly as for the mob ending");
            Assert.AreEqual(0, e.EntityId,
                "SimEvent.EntityId must stay 0 and MUST NOT BE READ on this kind: it means the "
                + "victim's player SLOT, and slot 0 is a real seat rather than 'nobody'");
            Assert.AreEqual(HitZone.Body, e.Zone, "SimEvent.Zone must survive the mapping");
        }

        // ------------------------------------------------------------------
        // The kinds that line up one to one, and the field conventions that
        // do not.
        // ------------------------------------------------------------------

        [Test]
        public void MobSpawned_CarriesTheMobsIdAndItsArchetype()
        {
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.MobSpawned);
            SnapshotEvents.WriteMobSpawned(bytes, MobId, MobType.Gunner);

            SimEvent e = Decode(SnapshotEventKind.MobSpawned, bytes, in cfg);

            Assert.AreEqual(SimEventKind.MobSpawned, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(MobId, e.EntityId,
                "SimEvent.EntityId must be the MOB's id — the view registry rents by it");
            Assert.AreEqual(MobType.Gunner, e.MobType,
                "SimEvent.MobType must be the archetype the wire named, not the enum's zero");
        }

        [Test]
        public void MobDied_NamesTheMobInEntityIdAndTheKillerInPlayerIndex()
        {
            // The ATTACKER convention: `EntityId` is the mob that died,
            // `PlayerIndex` is whoever killed it. Swapping the two would credit
            // the kill to a mob id and place the corpse on a player slot.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.MobDied);
            SnapshotEvents.WriteMobDied(bytes, MobId, OtherSlot, HitZone.Head, in cfg);

            SimEvent e = Decode(SnapshotEventKind.MobDied, bytes, in cfg);

            Assert.AreEqual(SimEventKind.MobDied, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(MobId, e.EntityId, "SimEvent.EntityId must be the MOB that died");
            Assert.AreEqual(OtherSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be the KILLER (the ATTACKER convention for this kind)");
            Assert.AreEqual(HitZone.Head, e.Zone, "SimEvent.Zone must be the killing blow's zone");
        }

        [Test]
        public void PlayerDamaged_PutsTheVICTIMInBothPlayerFields()
        {
            // `SimEvent`'s own doc: PlayerDamaged and PlayerDied follow the
            // VICTIM convention in `PlayerIndex`, and `EntityId` mirrors it.
            // Both are asserted because a decoder that filled only one of them
            // would leave the other reading either slot 0 or the no-owner
            // sentinel — and slot 0 is a real seat.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.PlayerDamaged);
            // app-88jb Т8: every one of the three new arguments is NON-ZERO and
            // different from the others, and the shooter is LocalSlot while the
            // victim is OtherSlot. A zero would make the shooter assertion
            // below true on the struct's own default, which is the exact defect
            // this test exists to catch on the two older fields.
            SnapshotEvents.WritePlayerDamaged(bytes, OtherSlot, HitZone.Legs, amount: 25f,
                new float2(0f, 1f), impactSpeed: 28f, height: 2.35f,
                attackerIndex: LocalSlot, in cfg);

            SimEvent e = Decode(SnapshotEventKind.PlayerDamaged, bytes, in cfg);

            Assert.AreEqual(SimEventKind.PlayerDamaged, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(OtherSlot, e.EntityId,
                "SimEvent.EntityId must be the VICTIM's slot for this kind");
            Assert.AreEqual(OtherSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be the VICTIM's slot too — since Т8 the shooter has a "
                + "field of its own (asserted below), and this pair must not drift into carrying it");
            Assert.AreEqual(HitZone.Legs, e.Zone, "SimEvent.Zone");
            Assert.AreEqual(25f, e.Amount, UnitTolerance(cfg.Hero.MaxHp),
                "SimEvent.Amount must be the damage dealt, quantized against MaxHp");
            Assert.AreEqual(math.PI / 2f, math.atan2(e.HitDir.y, e.HitDir.x), 1e-3f,
                "SimEvent.HitDir must be the blow's own direction — directional feedback reads it "
                + "and has nothing else to place a spray by");
            Assert.AreEqual(LocalSlot, e.AttackerIndex,
                "SimEvent.AttackerIndex must be the SHOOTER's slot — the victim rides the two "
                + "fields above, and leaving this one unset would name collector 0 on every blow");
            Assert.AreEqual(28f, e.ImpactSpeed, UnitTolerance(cfg.Weapon.ProjectileSpeed),
                "SimEvent.ImpactSpeed must be the round's landing speed, quantized against the "
                + "SHOOTER's own scale");
            Assert.AreEqual(2.35f, e.Height, UnitTolerance(cfg.Hero.MaxAimHeight),
                "SimEvent.Height must be the blow's contact height — the impulse Т7 applies is "
                + "placed by it");
        }

        [Test]
        public void PlayerDied_PutsTheVICTIMInBothPlayerFields()
        {
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.PlayerDied);
            SnapshotEvents.WritePlayerDied(bytes, OtherSlot, HitZone.Body, in cfg);

            SimEvent e = Decode(SnapshotEventKind.PlayerDied, bytes, in cfg);

            Assert.AreEqual(SimEventKind.PlayerDied, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(OtherSlot, e.EntityId,
                "SimEvent.EntityId must be the VICTIM's slot for this kind");
            Assert.AreEqual(OtherSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be the VICTIM's slot — this is also the field the "
                + "backend reads to decide whether the death is its OWN (Р41/Р59), so a decoder "
                + "that left it at the no-owner sentinel would keep this client predicting a corpse");
            Assert.AreEqual(HitZone.Body, e.Zone, "SimEvent.Zone");
        }

        [Test]
        public void PlayerDashed_NamesTheActor()
        {
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.PlayerDashed);
            SnapshotEvents.WritePlayerDashed(bytes, OtherSlot, in cfg);

            SimEvent e = Decode(SnapshotEventKind.PlayerDashed, bytes, in cfg);

            Assert.AreEqual(SimEventKind.PlayerDashed, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(OtherSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be the ACTOR who dashed");
        }

        [Test]
        public void PlayerSlideStarted_NamesTheActorAndHasNoDirectionToGive()
        {
            // `SimEvent`'s doc says HitDir carries the slide's travel direction
            // on the simulation side; the wire's payload for this kind is one
            // byte, the actor. The honest answer is therefore zero rather than
            // a direction taken from another field.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.PlayerSlideStarted);
            SnapshotEvents.WritePlayerSlideStarted(bytes, OtherSlot, in cfg);

            SimEvent e = Decode(SnapshotEventKind.PlayerSlideStarted, bytes, in cfg);

            Assert.AreEqual(SimEventKind.PlayerSlideStarted, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(OtherSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be the ACTOR who slid");
            Assert.AreEqual(0f, math.lengthsq(e.HitDir), 0f,
                "SimEvent.HitDir must be exactly zero: the slide direction is not on the wire for "
                + "this kind, and a borrowed one would point the effect somewhere nobody slid");
        }

        [Test]
        public void DashRicocheted_CarriesTheSurfaceNormalInHitDir()
        {
            // Sender: WriteDashRicocheted(actorIndex, normal). `HitDir` is the
            // wall NORMAL for this kind, not the direction of travel.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.DashRicocheted);
            SnapshotEvents.WriteDashRicocheted(bytes, OtherSlot, new float2(0f, 1f), in cfg);

            SimEvent e = Decode(SnapshotEventKind.DashRicocheted, bytes, in cfg);

            Assert.AreEqual(SimEventKind.DashRicocheted, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(OtherSlot, e.PlayerIndex, "SimEvent.PlayerIndex must be the ACTOR");
            Assert.AreEqual(math.PI / 2f, math.atan2(e.HitDir.y, e.HitDir.x), 1e-3f,
                "SimEvent.HitDir must be the surface NORMAL the wire carried");
        }

        [Test]
        public void StaminaDenied_IsAboutTHISCLIENT_BecauseTheWireNamesNobody()
        {
            // The payload is one byte, the missing stamina: this kind reaches
            // its owner and nobody else (channel Owner, Р28), so who it is
            // about is not on the wire at all and the local seat is the only
            // honest answer. That is why the seat is a PARAMETER of the decode
            // rather than something it reads out of the bytes.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.StaminaDenied);
            SnapshotEvents.WriteStaminaDenied(bytes, amount: 20f, in cfg);

            SimEvent e = Decode(SnapshotEventKind.StaminaDenied, bytes, in cfg);

            Assert.AreEqual(SimEventKind.StaminaDenied, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(LocalSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be THIS CLIENT's own seat — the payload carries no "
                + "slot, so a decoder reading one out of it would report seat 0 for everybody");
            Assert.AreEqual(20f, e.Amount, UnitTolerance(cfg.Hero.StaminaMax),
                "SimEvent.Amount must be the stamina that was missing, quantized against StaminaMax");
        }

        [Test]
        public void WaveStarted_CarriesTheWaveIndexInEntityId()
        {
            // `EntityId` is the wave index for the two wave kinds — WaveSystem's
            // own emit sites, mirrored by the assembler.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.WaveStarted);
            SnapshotEvents.WriteWaveStarted(bytes, waveIndex: 12);

            SimEvent e = Decode(SnapshotEventKind.WaveStarted, bytes, in cfg);

            Assert.AreEqual(SimEventKind.WaveStarted, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(12, e.EntityId,
                "SimEvent.EntityId must be the WAVE index for this kind");
        }

        [Test]
        public void WaveCleared_CarriesTheWaveIndexInEntityId_AndIsNotWaveStarted()
        {
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.WaveCleared);
            SnapshotEvents.WriteWaveCleared(bytes, waveIndex: 12);

            SimEvent e = Decode(SnapshotEventKind.WaveCleared, bytes, in cfg);

            Assert.AreEqual(SimEventKind.WaveCleared, e.Kind,
                "SimEvent.Kind — the two wave kinds carry the same payload and differ only here, "
                + "so this is the one assertion that can tell them apart");
            Assert.AreEqual(12, e.EntityId,
                "SimEvent.EntityId must be the WAVE index for this kind too");
        }

        // ------------------------------------------------------------------
        // The raid's own five kinds (Stage 3 Т29 on the wire, Т32 here — bd
        // app-gggs). Every expectation is taken from the SENDER: the
        // `SimEventKind` docs' per-kind PAYLOAD paragraphs and
        // `SnapshotAssembler.BeginTick`'s own `Add*` calls, never from the
        // decoder's body.
        // ------------------------------------------------------------------

        [Test]
        public void DirectorActivated_MapsToItsOwnKind_AndFillsNoPayloadField()
        {
            // PAYLOAD: none (SimEventKind.DirectorActivated's own doc) — the
            // kind rides the All channel, which carries no position by rule
            // (Р28), so `Kind` is the entire message.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.DirectorActivated);
            SnapshotEvents.WriteDirectorActivated(bytes);

            SimEvent e = Decode(SnapshotEventKind.DirectorActivated, bytes, in cfg);

            Assert.AreEqual(SimEventKind.DirectorActivated, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(0, e.EntityId, "SimEvent.EntityId — this kind has no entity");
            Assert.AreEqual(0f, e.Amount, "SimEvent.Amount — unused for this kind");
        }

        [Test]
        public void DirectorDied_MapsToItsOwnKind_AndIsNotDirectorActivated()
        {
            // The two carry the same (empty) payload and differ only in Kind,
            // so that is the one assertion able to tell them apart — the same
            // shape the WaveStarted/WaveCleared pair above is pinned by.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.DirectorDied);
            SnapshotEvents.WriteDirectorDied(bytes);

            SimEvent e = Decode(SnapshotEventKind.DirectorDied, bytes, in cfg);

            Assert.AreEqual(SimEventKind.DirectorDied, e.Kind,
                "SimEvent.Kind — the two Director kinds share an empty payload and differ "
                + "only here");
        }

        [Test]
        public void PlayerExtracted_CarriesTheSlotInBothPlayerIndexAndEntityId()
        {
            // VICTIM convention, and the mirror is the point: SimEvent's own
            // master list puts PlayerExtracted with PlayerDamaged/PlayerDied,
            // "mirrors EntityId's convention for those three kinds", which is
            // what lets EventRelevance.VisibleSubjectId resolve all three
            // through one ForPlayer(ev.PlayerIndex).
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.PlayerExtracted);
            SnapshotEvents.WritePlayerExtracted(bytes, OtherSlot, in cfg);

            SimEvent e = Decode(SnapshotEventKind.PlayerExtracted, bytes, in cfg);

            Assert.AreEqual(SimEventKind.PlayerExtracted, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(OtherSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be the slot that walked out");
            Assert.AreEqual(OtherSlot, e.EntityId,
                "SimEvent.EntityId mirrors PlayerIndex for this kind (the three victim kinds)");
        }

        [Test]
        public void PickupTaken_CarriesTheCellIdInEntityId_AndTheReceiverAsCollector()
        {
            // Two halves, and the second is the interesting one. `EntityId` is
            // the cell's own id, truncated to the u16 code every long-lived
            // entity rides (Р278). `PlayerIndex` is the COLLECTOR — and the
            // wire does NOT carry it, because this kind rides the Owner
            // channel: the server sends it to exactly one connection, the
            // collector's, so on this side "who collected" is answered by
            // "who received", the same way StaminaDenied is already decoded.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.PickupTaken);
            SnapshotEvents.WritePickupTaken(bytes, PickupId);

            SimEvent e = Decode(SnapshotEventKind.PickupTaken, bytes, in cfg);

            Assert.AreEqual(SimEventKind.PickupTaken, e.Kind, "SimEvent.Kind");
            Assert.AreEqual(PickupId, e.EntityId,
                "SimEvent.EntityId must be the collected cell's own id");
            Assert.AreEqual(LocalSlot, e.PlayerIndex,
                "SimEvent.PlayerIndex must be THIS connection's slot: the Owner channel "
                + "delivered this record to the collector and to nobody else");
        }

        [Test]
        public void ContainerEmptied_CarriesTheContainerIdInEntityId_AndIsNotPickupTaken()
        {
            // Same u16 id payload as PickupTaken, so Kind is again the only
            // assertion that can tell the two apart. No PlayerIndex here: this
            // kind is delivered by VISIBILITY (R-236 — the assembler decides it
            // against ContainersCurrent), so the receiver is not a subject of
            // the news and filling their slot in would be a fiction.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ContainerEmptied);
            SnapshotEvents.WriteContainerEmptied(bytes, ContainerId);

            SimEvent e = Decode(SnapshotEventKind.ContainerEmptied, bytes, in cfg);

            Assert.AreEqual(SimEventKind.ContainerEmptied, e.Kind,
                "SimEvent.Kind — the two id-carrying raid kinds differ only here");
            Assert.AreEqual(ContainerId, e.EntityId,
                "SimEvent.EntityId must be the emptied container's own id");
        }

        // ------------------------------------------------------------------
        // What every decode carries regardless of kind.
        // ------------------------------------------------------------------

        [Test]
        public void EveryDecode_TakesItsTickFromTheDedupAndItsPositionFromTheRecord()
        {
            // The tick is the ABSOLUTE one the dedup handed back, never the
            // frame's own — the queue files the event under it and the render
            // clock delivers it there. The position rides the record header
            // rather than the payload because it is per-connection: the same
            // event is exact for one observer and coarsened for another.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.PlayerDashed);
            SnapshotEvents.WritePlayerDashed(bytes, OtherSlot, in cfg);

            SimEvent e = Decode(SnapshotEventKind.PlayerDashed, bytes, in cfg);

            Assert.AreEqual((int)OriginTick, e.Tick,
                "SimEvent.Tick must be the origin tick the caller passed in");
            // Exactly, with no tolerance at all: the position is a struct copy
            // out of the record and no codec stands between the two — the
            // record's own `Pos` was decoded by `TryReadEventsBlock` long
            // before this method saw it (M-8: there is no quantizer step here
            // to express a tolerance in).
            Assert.AreEqual(EventPos.x, e.Pos.x, 0f, "SimEvent.Pos.x must be the RECORD's own");
            Assert.AreEqual(EventPos.y, e.Pos.y, 0f, "SimEvent.Pos.y must be the RECORD's own");
        }

        // ------------------------------------------------------------------
        // The SECOND output: the decoded payload the caller routes by.
        // ------------------------------------------------------------------

        [Test]
        public void ProjectileSpawned_HandsBackTheRoundAndTheShooterInThePayload()
        {
            // `out payload` is not a second copy of the event, and it is not
            // decoration: `NetworkSimBackend.RouteToGhosts` confirms this
            // client's own predicted tracer with `payload.Id` and gates that
            // confirmation on `payload.PlayerIndex`. Both fields are zero in a
            // `default` payload — and `GhostProjectiles.Confirm` matches
            // POSITIONALLY against the oldest unconfirmed ghost, so a zeroed
            // seat would pair another player's round with this client's tracer
            // for every client sitting in seat 0 (fix-round 1, G-3).
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ProjectileSpawned);
            SnapshotEvents.WriteProjectileSpawned(bytes, RoundId, OtherSlot,
                new float2(0f, 1f), horizSpeed: 20f, velZ: 0f, height: 1.2f, in cfg);

            SimEvent e = DecodeWithPayload(SnapshotEventKind.ProjectileSpawned, bytes, in cfg,
                out SnapshotEventPayload payload);

            Assert.AreEqual(RoundId, payload.Id,
                "SnapshotEventPayload.Id must be the ROUND's id — the ghost registry is keyed by "
                + "it, and a zero confirms a ghost against a round that does not exist");
            Assert.AreEqual(OtherSlot, payload.PlayerIndex,
                "SnapshotEventPayload.PlayerIndex must be the SHOOTER — it is the whole gate on "
                + "confirming a tracer as this client's own");
            Assert.AreEqual(RoundId, e.EntityId,
                "witness: the SimEvent is filled as well, so the two assertions above are about "
                + "the second output rather than about a decode that failed");
        }

        [Test]
        public void ProjectileEnded_HandsBackTheRoundInThePayloadWhereTheSimEventCannot()
        {
            // The same output, for the ending — and here it is the only place
            // the round survives in a shape the caller can use without knowing
            // which ending arrived: `SimEvent.EntityId` is 0 for a HitMob
            // ending (the victim is not on the wire) and the round has moved
            // to `SecondaryEntityId`. `RouteToGhosts` retires the tracer by
            // `payload.Id` for all four endings alike.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.ProjectileEnded);
            SnapshotEvents.WriteProjectileEnded(bytes, RoundId, ProjectileEndKind.HitMob,
                HitZone.Head, height: 0f, in cfg);

            SimEvent e = DecodeWithPayload(SnapshotEventKind.ProjectileEnded, bytes, in cfg,
                out SnapshotEventPayload payload);

            Assert.AreEqual(RoundId, payload.Id,
                "SnapshotEventPayload.Id must be the ROUND's id for an ending too — it is what "
                + "the tracer is retired by, and a zero leaves it burning until its own timeout");
            Assert.AreEqual(ProjectileEndKind.HitMob, payload.EndKind,
                "SnapshotEventPayload.EndKind must survive into the payload: it is the "
                + "discriminator four sim kinds arrive under");
            Assert.AreEqual(0, e.EntityId,
                "witness: the SimEvent's own primary field IS 0 for this ending, which is exactly "
                + "why the caller reads the payload instead of the event");
        }

        // ------------------------------------------------------------------
        // Refusals — three of them, and only two are worth a word.
        // ------------------------------------------------------------------

        [Test]
        public void AKindThisBuildDoesNotMap_IsSkippedWithoutARefusal()
        {
            // Р29 forward compatibility: a newer server may add a kind, and a
            // receiver that has never heard of it must walk past the record
            // rather than call the frame hostile. `SnapshotEvents.TryReadPayload`
            // cannot make that distinction — it folds "unknown kind" into the
            // same MalformedContent it gives a known kind with bad bytes — so
            // the question has to be asked before the payload is looked at.
            var cfg = TestConfigs.Open();
            const SnapshotEventKind future = (SnapshotEventKind)200;

            Assert.IsFalse(ClientEventDecoder.IsMapped(future),
                "ClientEventDecoder.IsMapped must answer false for a kind this build has no "
                + "SimEvent for");
            Assert.IsTrue(ClientEventDecoder.IsMapped(SnapshotEventKind.PlayerDashed),
                "witness: it answers true for a kind that IS mapped, so the assertion above is "
                + "about the kind and not about the method");

            var record = new SnapshotBlocks.EventRecord
            {
                Kind = (byte)future, Seq = 1, TickDelta = 0, Pos = EventPos,
                PayloadOffset = 0, PayloadLength = 1,
            };
            Assert.IsFalse(ClientEventDecoder.TryDecode(OriginTick, record, new byte[1], in cfg,
                    LocalSlot, out _, out _, out SnapshotBlockError refusal),
                "an unmapped kind produces no SimEvent");
            Assert.AreEqual(SnapshotBlockError.None, refusal,
                "and it is NOT a refusal: reporting one here would log every frame of an "
                + "ordinary forward-compatibility skip as hostile input");
        }

        [Test]
        public void AMappedKindWithABadPayload_IsRefusedWithTheCatalogsOwnError()
        {
            // The other side of the same coin: a slot byte outside this match's
            // roster is hostile or stale input (Р82), and the catalog says so.
            var cfg = TestConfigs.Open();
            byte[] bytes = Buffer(SnapshotEventKind.PlayerDashed);
            bytes[0] = (byte)(cfg.Arena.MaxPlayers + 5);

            Assert.IsFalse(ClientEventDecoder.TryDecode(OriginTick,
                    Record(SnapshotEventKind.PlayerDashed), bytes, in cfg, LocalSlot,
                    out _, out _, out SnapshotBlockError refusal),
                "a seat outside the roster must not become a SimEvent");
            Assert.AreEqual(SnapshotBlockError.MalformedContent, refusal,
                "and it IS a refusal, which is what tells it apart from the Р29 skip above");

            // Positive witness on the SAME kind and the SAME buffer, one byte
            // apart: without it a decoder that refused everything would pass
            // the two assertions above.
            bytes[0] = OtherSlot;
            Assert.IsTrue(ClientEventDecoder.TryDecode(OriginTick,
                    Record(SnapshotEventKind.PlayerDashed), bytes, in cfg, LocalSlot,
                    out SimEvent witness, out _, out _),
                "witness: the same record with a seat INSIDE the roster decodes");
            Assert.AreEqual(OtherSlot, witness.PlayerIndex,
                "witness: and it is that seat the SimEvent names");
        }

        [Test]
        public void ARecordPointingPastTheBlock_IsRefusedRatherThanThrown()
        {
            // This method is public and its records need not have come from
            // `TryReadEventsBlock`, which is where the offset/length pair is
            // normally validated — the same precedent
            // `SnapshotBlocks.TryReadEventsBlock` states for its own u16
            // precondition. A slice past the end of the block would throw
            // inside a broadcast handler, which abandons every message batched
            // behind it in the same datagram.
            var cfg = TestConfigs.Open();
            var record = new SnapshotBlocks.EventRecord
            {
                Kind = (byte)SnapshotEventKind.PlayerDashed, Seq = 1, TickDelta = 0, Pos = EventPos,
                PayloadOffset = 4, PayloadLength = 4,
            };

            Assert.IsFalse(ClientEventDecoder.TryDecode(OriginTick, record, new byte[2], in cfg,
                    LocalSlot, out _, out _, out SnapshotBlockError refusal),
                "a record whose payload lies outside the block it came with must be refused");
            Assert.AreEqual(SnapshotBlockError.MalformedLength, refusal,
                "as a LENGTH refusal — the bytes are not wrong, there are not enough of them");
        }
    }
}
