using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Protocol
{
    /// Wire event KINDS carried in the `kind` byte of an Events-block record
    /// (Stage 2 Task 28, spec §3.7 table Р28 / §3.8, task-28-brief §2.2). Task
    /// 27 left that byte deliberately opaque — the catalog belongs to whoever
    /// PRODUCES events, and that is the snapshot assembler.
    ///
    /// THESE ARE NOT `SimEventKind`. The two enumerations describe different
    /// things and deliberately do not line up:
    ///   * one `SimEventKind.ProjectileFired` becomes TWO wire events —
    ///     `ProjectileSpawned` for whoever the round flies near (Р32) and
    ///     `ShotHeard` for whoever merely hears it (Р28) — with different
    ///     addressees and different `seq` values;
    ///   * four sim kinds (`ProjectileHit`, `ProjectileHitPlayer`,
    ///     `ProjectileBlocked`, `ProjectileExpired`) collapse into ONE
    ///     `ProjectileEnded`,
    ///     discriminated by `ProjectileEndKind` below. There is deliberately no
    ///     separate "projectile hit" wire kind: the tracer's end and the
    ///     shooter's hitmarker are the same fact seen from two sides, and
    ///     `endKind` + `zone` carry everything either side needs. A mob's death
    ///     from that round rides its own `MobDied`; a player's rides
    ///     `PlayerDied`.
    ///
    /// `None = 0` MUST NEVER BE WRITTEN — the same refusal-sentinel contract
    /// `SnapshotBlockKind.None` carries, for the same reason: a consumer that
    /// only inspects the returned kind must be able to tell "nothing was
    /// decoded" from "a real event arrived". Pinned literally by
    /// SnapshotCodecTests.SnapshotEventKind_ValuesArePinned_AndNoneIsZero.
    ///
    /// AN UNKNOWN KIND IS NOT AN ERROR (Р29). A receiver that meets a kind it
    /// has never heard of skips the record by the length arithmetic Task 27's
    /// `TryReadEventsBlock` already performs (each record declares its own
    /// payload length), and simply never asks this file to decode it. Growing
    /// this enum is therefore not a breaking format change; changing an
    /// existing kind's VALUE or payload layout is, and needs a
    /// `ProtocolVersion` bump.
    public enum SnapshotEventKind : byte
    {
        None = 0,
        ProjectileSpawned = 1,
        ProjectileEnded = 2,
        ShotHeard = 3,
        MobSpawned = 4,
        MobDied = 5,
        PlayerDamaged = 6,
        PlayerDied = 7,
        PlayerDashed = 8,
        PlayerSlideStarted = 9,
        DashRicocheted = 10,
        StaminaDenied = 11,
        WaveStarted = 12,
        WaveCleared = 13,

        // Stage 3 Т29 (spec §3.12 Р281): the raid's own catalog. APPENDED,
        // never renumbered — a client already in flight would read a
        // different meaning out of the same byte.
        //
        // The first three have existed on the SIMULATION side since Т21/Т23
        // and were emitted into a wire catalog that had no entry for them,
        // so `SnapshotAssembler` dropped them silently; these five lines are
        // what ends that.
        DirectorActivated = 14,
        DirectorDied = 15,
        PlayerExtracted = 16,
        PickupTaken = 17,
        ContainerEmptied = 18,

        /// Stage 3 Т30 (spec §3.7, coordinator Ruling 234): a round mirrored
        /// off static geometry and FLEW ON — the contact point plus the
        /// surface normal, so the client can put a spark where the hit
        /// happened instead of watching a tracer change direction for no
        /// visible reason.
        ///
        /// APPENDED PAST `ContainerEmptied`, NOT INSERTED BESIDE THE OTHER
        /// `Projectile*` VALUES, and that is the same rule the five Т29 kinds
        /// above were added under: this byte is a wire value, so renumbering
        /// an existing kind changes what a client ALREADY IN FLIGHT reads out
        /// of the same byte — a `ProjectileEnded` decoded as a `ShotHeard`,
        /// with a payload length that no longer matches. Growing the catalog
        /// at the top is the "ADDING" case Р29 declares compatible; moving a
        /// value is the case that needs a `ProtocolVersion` bump.
        ///
        /// ⚠ IT IS NOT AN ENDING, unlike every other `Projectile*` value in
        /// this enum. `ProjectileEnded` closes the round's per-connection
        /// spawn subscription; this one must leave it open, or the ending
        /// that follows reaches nobody — see `SimEventKind.ProjectileRicocheted`
        /// for the same warning from the simulation's side.
        ProjectileRicocheted = 19,
    }

    /// How a round's flight ended, carried inside a `ProjectileEnded` payload
    /// (task-28-brief §2.2). `None = 0` is never written, same contract as
    /// `SnapshotEventKind.None`: a zero here would be indistinguishable from an
    /// uninitialized payload byte, and the client kills its tracer differently
    /// per ending (a spark on a wall, a fade on expiry, a flesh impact on a
    /// mob, and — Stage 2 Task 44a — one on a player).
    public enum ProjectileEndKind : byte
    {
        None = 0,
        Blocked = 1,
        Expired = 2,
        HitMob = 3,

        /// Stage 2 Task 44a: the round ended on a PLAYER. Its own value rather
        /// than a reused `HitMob` because the client picks its impact feedback
        /// off this byte — a hit on a player must not spawn a mob's flesh
        /// impact, and `SimEventKind.ProjectileHitPlayer`'s own doc explains
        /// why the two are separate on the simulation side too. Costs ZERO
        /// extra wire bytes: `SnapshotEventKind.ProjectileEnded` already
        /// carries this discriminator in a byte that had room.
        HitPlayer = 4,
    }

    /// Bit meanings of the snapshot header's `flags` byte (SnapshotWriter's
    /// layout, byte 7). Task 26 and Task 27 both declined to assign a bit,
    /// correctly: neither had a producer or a consumer for one. Task 28 has
    /// both, so bit 0 is claimed here.
    ///
    /// `Truncated` means: THIS FRAME DROPPED AT LEAST ONE ENTITY TO STAY INSIDE
    /// `NetConfig.SnapshotMaxBytes` (Р62). Without it the receiver cannot tell
    /// "that mob left my view" from "that mob was cut for room" — the first
    /// must retire the view, the second must keep it and coast (Tasks 32/37).
    ///
    /// DEFERRED EVENTS DO NOT SET IT. Carrying an event into the next frame is
    /// the ordinary working mode of the budget (Р61), not a degradation, and it
    /// has its own counters (`NetStats.DroppedEvents`). Folding the two into
    /// one bit would make the flag fire on almost every busy frame and mean
    /// nothing.
    ///
    /// NEITHER DOES A CONTAINER'S CUT INTERIOR (Stage 3 Task 27, coordinator
    /// R-222). "Entity" is the word this bit is defined on: a box whose
    /// ContainerSlots record did not fit is still IN the frame, at its own
    /// position, with its own "already looted" flag — what it lost is a
    /// detail available on demand over the reliable loot channel, so the
    /// receiver has nothing to retire and nothing to coast. Setting the bit
    /// for it would tell Tasks 32/37 an entity went missing when none did.
    ///
    /// Bits 1-7 are FREE and NOT ASSIGNED.
    public static class SnapshotHeaderFlags
    {
        public const byte Truncated = 1 << 0;
        // bits 1..7 are free and NOT assigned.
    }

    /// One decoded event payload, shaped like `SimEvent` itself: ONE struct
    /// whose fields mean different things per `Kind`, rather than thirteen
    /// small structs (task-28-brief §2.2). The precedent is deliberate —
    /// `SimEvent` already carries `Amount`/`Owner`/`Zone`/`HitDir` under
    /// exactly this "unused for every other kind" convention, and a consumer
    /// that switches on `Kind` reads the same shape on both sides of the wire.
    ///
    /// Every float here is the DECODED value, never a wire code — same rule as
    /// `SnapshotBlocks.PlayerRecord`.
    public struct SnapshotEventPayload
    {
        /// The kind this payload was decoded AS. Set on every successful
        /// decode, so a consumer that stores payloads cannot lose track of
        /// which fields are meaningful.
        public SnapshotEventKind Kind;

        /// Entity id as a LOSSY u16 wire code — `ProjectileSpawned`,
        /// `ProjectileEnded`, `MobSpawned`, `MobDied`. Truncation follows the
        /// precedent set by `SnapshotBlocks.MobRecord.Id` (task-27-brief §2.8):
        /// two source ids exactly 65536 apart produce the identical code and
        /// the original can never be recovered. Mapping a code back to a live
        /// entity is the receiver's job (Task 32).
        public int Id;

        /// The player slot this event names — the shooter for
        /// `ProjectileSpawned`/`ShotHeard` (`ProjectileIds.NoOwner` for a mob's
        /// round), the killer for `MobDied`, the victim for
        /// `PlayerDamaged`/`PlayerDied`, the actor for
        /// `PlayerDashed`/`PlayerSlideStarted`/`DashRicocheted`.
        ///
        /// ⚠ `PlayerDamaged` HAS A SECOND SLOT BYTE SINCE app-88jb Т8, and it
        /// is NOT this one: this field stays the VICTIM, `AttackerIndex` below
        /// is the shooter. Reading one for the other means shoving the wrong
        /// collector, which is why they are two fields and not one.
        public byte PlayerIndex;

        /// Decoded unit heading: the round's horizontal direction for
        /// `ProjectileSpawned`, the blow's impact direction for
        /// `PlayerDamaged`, the wall normal for `DashRicocheted`.
        public float2 Dir;

        /// Contact point of a `ProjectileRicocheted` (app-88jb Т30), in world
        /// meters. Zero for every other kind. The one payload in this catalog
        /// that carries a point of its own (plan deviation 3) — every other
        /// kind's position rides the RECORD HEADER instead
        /// (`SnapshotBlocks.EventRecord.Pos`, filled per connection by the
        /// assembler), which is where `DashRicocheted`'s own wall contact
        /// travels today.
        /// ⚠ THE PLAN'S OWN WORDING FOR THIS FIELD SAID SOMETHING ELSE —
        /// that `DashRicocheted` "restores its position from the actor's
        /// body" — and the code says otherwise: `ClientEventDecoder` assigns
        /// `e.Pos = record.Pos` for EVERY kind before its per-kind switch, and
        /// `PersistentPropsDirector.HandleRicocheted` plays its spark at that
        /// same `e.Pos`, never at the actor. The redundancy this leaves
        /// between the header's point and this one is recorded for the review
        /// round rather than decided here.
        public float2 Pos;

        /// `ProjectileSpawned` only: the round's HORIZONTAL speed. It is not
        /// the config's `ProjectileSpeed` for an aimed player shot —
        /// WeaponSystem normalizes a full 3D vector to `ProjectileSpeed`, so
        /// the horizontal component is `ProjectileSpeed * cos(elevation)` — and
        /// so it rides the wire explicitly instead of being assumed.
        public float HorizSpeed;

        /// `ProjectileSpawned` only: the round's vertical speed, signed.
        public float VelZ;

        /// Meters above ground: the muzzle height for `ProjectileSpawned`, the
        /// contact height for a `Blocked` `ProjectileEnded` and, since app-88jb
        /// Т8, the contact height of the blow for `PlayerDamaged` (0 otherwise).
        public float Height;

        /// Speed of the round that landed, in m/s (app-88jb Т8). Quantized against
        /// SpeedCapFor(attackerIndex) -- the OWNER's own scale, the precedent
        /// ProjectileSpawned already sets ("THE SPEED SCALE DEPENDS ON THE OWNER").
        public float ImpactSpeed;

        /// Who fired the round (app-88jb Т8) -- a player slot, or ProjectileIds.NoOwner
        /// for a mob's.
        ///
        /// ⚠ A SECOND FIELD, NOT PlayerIndex, and round 3 is why (finding A-C3):
        /// PlayerIndex is the VICTIM here. Its own doc says so ("the killer for
        /// MobDied, the VICTIM for PlayerDamaged"), ClientEventDecoder fills it as the
        /// victim, and T9's decoder filter keys "my own hit" off exactly that. Plan v2
        /// declared only ImpactSpeed and then asserted PlayerIndex == the attacker --
        /// two readings of one slot that cannot both hold: either the test is red on a
        /// correct implementation, or the victim is lost and the client shoves the
        /// wrong collector.
        public byte AttackerIndex;

        /// `PlayerDamaged`: the damage actually dealt. `StaminaDenied`: the
        /// stamina that was missing.
        public float Amount;

        /// The blow's vertical zone — `ProjectileEnded` (HitMob and, since
        /// Stage 2 Task 44a, HitPlayer), `MobDied`,
        /// `PlayerDamaged`, `PlayerDied`. `HitZone.None` where no blow applies.
        public HitZone Zone;

        /// `ProjectileEnded` only.
        public ProjectileEndKind EndKind;

        /// `MobSpawned` only.
        public MobType MobType;

        /// `WaveStarted`/`WaveCleared` only.
        ///
        /// ⚠ THE NUMBER IS THE RAID'S DIFFICULTY STEP from bd app-ggvz Т4 on,
        /// not a wave ordinal, and it belongs to ONE RING (the ring whose wave
        /// started or was cleared) rather than to the arena: the three rings
        /// run independent cadences, so several events with the SAME number
        /// are ordinary, and a number that repeats or skips across consecutive
        /// events is not a gap in delivery. The step is a pure function of the
        /// tick (Simulation.AI.WaveSystem.DifficultyStepFor); the wire shape,
        /// the width and the codec are unchanged.
        public ushort WaveIndex;
    }

    /// Stage 2 Task 28 (spec §3.7 Р28, §3.8, Р32/Р61/Р62): the wire-event
    /// catalog — payload layouts, the priority scale the event budget spends,
    /// and both sides of the payload codec. The snapshot assembler
    /// (`Ring.Networking.Server.SnapshotAssembler`) is the only producer; Task
    /// 32 is the consumer.
    ///
    /// WHERE A PAYLOAD SITS. An Events-block record is Task 27's 9-byte header
    /// — kind, seq, tickDelta, quantized POSITION, payload length — followed by
    /// the bytes this file writes. THE POSITION IS NOT IN THE PAYLOAD: it is
    /// per-CONNECTION (an event delivered exactly to one observer and coarsely
    /// to another differs only there), while everything below is the same bytes
    /// for every addressee, which is what lets the assembler build each
    /// payload once per tick and share it across connections.
    ///
    /// PAYLOAD LAYOUTS, byte for byte. Every multi-byte field is
    /// little-endian, same convention as the rest of the protocol. Quantization
    /// is always through `Quantize`, with the scale taken from `cfg` — no
    /// formula and no balance number is restated here (rule 2).
    ///
    ///   ProjectileSpawned  8 B  id u16 | ownerIndex u8 | dir u8 | horizSpeed u8
    ///                           | velZ u16 | height u8
    ///   ProjectileEnded    5 B  id u16 | endKind u8 | zone u8 | height u8
    ///   ShotHeard          1 B  ownerIndex u8
    ///   MobSpawned         3 B  id u16 | mobType u8
    ///   MobDied            4 B  id u16 | attackerIndex u8 | zone u8
    ///   PlayerDamaged      7 B  victimIndex u8 | zone u8 | amount u8 | hitDir u8
    ///                           | impactSpeed u8 | height u8 | attackerIndex u8
    ///   PlayerDied         2 B  victimIndex u8 | zone u8
    ///   PlayerDashed       1 B  actorIndex u8
    ///   PlayerSlideStarted 1 B  actorIndex u8
    ///   DashRicocheted     2 B  actorIndex u8 | normal u8
    ///   StaminaDenied      1 B  amount u8
    ///   WaveStarted        2 B  waveIndex u16
    ///   WaveCleared        2 B  waveIndex u16
    ///
    /// THE SPEED SCALE DEPENDS ON THE OWNER, AND THE READER MUST TAKE
    /// `ownerIndex` FIRST. A player's round is quantized against
    /// `cfg.Weapon.ProjectileSpeed`, a mob's against
    /// `cfg.Gunner.ProjectileSpeed`; decoding a mob's round on the player's
    /// scale would report it roughly twice as fast as it flies. Exactly the
    /// precedent Task 27 set for a mob's HP, which is quantized against its own
    /// archetype's `MaxHp` (task-27-brief §2.7).
    ///
    /// ⚠ TWO KINDS OBEY THAT RULE NOW, AND THE BYTE IS NOT IN THE SAME PLACE:
    /// `ProjectileSpawned` carries its owner in byte 2, while `PlayerDamaged`
    /// (app-88jb Т8) carries its SHOOTER in byte 6 — the LAST byte of its
    /// payload, which a reader must therefore take before byte 4. The two
    /// share one home for the rule itself, `SpeedCapFor`, so the branch is
    /// written once no matter where the byte sits.
    ///
    /// `velZ` GOES THROUGH `Quantize.Pos`, NOT `Unit`, and against the SPEED
    /// CAP rather than a new constant: a round's vertical velocity is one
    /// component of a vector whose length is at most `ProjectileSpeed`, so it
    /// lives in `[-speedCap, +speedCap]` by construction — which is precisely
    /// `Pos`'s symmetric domain. No second formula, no fourth codec.
    ///
    /// IDS AND WAVE INDICES ARE TRUNCATED TO u16, LOSSILY. Same honest wording
    /// as `SnapshotBlocks.MobRecord.Id`: the original is not recoverable, and
    /// two ids 65536 apart collide. For wave indices the collision needs 65536
    /// waves in one match — roughly fifteen days of play at the shipped pacing
    /// — which is a statement about practical reach, not a proof that it cannot
    /// happen.
    ///
    /// THE WRITE SIDE THROWS; THE READ SIDE NEVER DOES. Identical asymmetry to
    /// Tasks 26/27 and for the identical reason: a value outside its own domain
    /// handed to a writer is a bug in the assembler, while a hostile or
    /// corrupted byte arriving from the wire is ordinary traffic (Р82). The
    /// read side also refuses rather than passing content through — a slot
    /// index at or above `cfg.Arena.MaxPlayers`, an enumerator that does not
    /// exist — because Task 32 and Tasks 43-45 index per-slot view pools and
    /// prefab tables by exactly these values.
    ///
    /// ERRORS REUSE `SnapshotBlockError` RATHER THAN DECLARING A PARALLEL ENUM
    /// (task-28-brief §2.2 leaves the choice open, so it is recorded here). A
    /// payload IS the content of an Events-block record, delivered by
    /// `SnapshotBlocks.TryReadEventsBlock`, and its only two refusal shapes are
    /// the two that enum already names with the same meanings:
    /// `MalformedLength` (the byte count is not this kind's) and
    /// `MalformedContent` (the shape is legal, a value is not). A second enum
    /// with the same two members would be duplication, and would force every
    /// caller that walks a block and then decodes its records to reconcile two
    /// taxonomies for one parse.
    ///
    /// ZERO ALLOCATIONS: everything below is spans and structs.
    ///
    /// WHY ITS OWN LITTLE-ENDIAN PRIMITIVES, AGAIN. `SnapshotWriter`,
    /// `SnapshotReader` and `SnapshotBlocks` each carry private LE helpers, and
    /// SnapshotWriter's own doc names "a THIRD consumer" as the threshold for
    /// lifting them into a shared home. That threshold is crossed here — and
    /// the helpers are still private, because the only way to lift them would
    /// be to edit three files belonging to closed tasks, leaving four copies
    /// instead of three if it were done only halfway. Recorded as a known debt
    /// rather than a fresh judgement: the unification belongs to whichever task
    /// may edit Tasks 26/27's files.
    public static class SnapshotEvents
    {
        /// The largest payload any kind produces — `ProjectileSpawned`'s 8
        /// bytes. The assembler sizes its per-record payload slots by this, so
        /// a carried-over event never needs a variable-length pool.
        public const int MaxPayloadBytes = 8;

        /// Highest legal wire value of `HitZone` / `ProjectileEndKind`. Same
        /// tripwire role as `SnapshotBlocks.MaxMobTypeValue` and friends: the
        /// wire carries these as raw bytes, and `(HitZone)200` is a perfectly
        /// legal C# cast onto a value the enum never declared. `MobType`'s own
        /// bound is NOT restated here — `SnapshotBlocks.MaxMobTypeValue` is
        /// reused directly.
        public const byte MaxHitZoneValue = (byte)HitZone.Head;
        public const byte MaxProjectileEndKindValue = (byte)ProjectileEndKind.HitPlayer;

        /// Priority ranks, LOWER IS MORE IMPORTANT — the order the event budget
        /// (Р61) spends its room in, and the order it defers what does not fit.
        ///
        /// THIS IS NOT `DeliveryChannel`, AND THE TWO MUST NOT BE CONFLATED
        /// (Р136 warns about exactly this): `Audible` describes WHO an event
        /// reaches, not how much it matters. A death heard through a wall
        /// outranks a dash seen in the open.
        public const byte PriorityDeath = 0;
        public const byte PriorityImpact = 1;
        public const byte PriorityState = 2;
        public const byte PriorityCosmetic = 3;

        /// Rank of `kind` on the scale above (spec Р61: "deaths and hits above
        /// cosmetics"). Every kind is listed explicitly and an unhandled one
        /// throws rather than falling through a `default` — the same discipline
        /// as `EventRelevance.ChannelFor`, and for the same reason: a kind
        /// added to the enum without a rank must fail loudly instead of
        /// silently inheriting whichever rank happens to compile.
        public static byte PriorityOf(SnapshotEventKind kind)
        {
            switch (kind)
            {
                case SnapshotEventKind.PlayerDied:
                case SnapshotEventKind.MobDied:
                // Stage 3 Т29 (R-233): the Director falling is BOTH a death
                // and the raid's own turning point — it is what opens the
                // gate (spec §3.5). Nothing in a frame outranks it.
                case SnapshotEventKind.DirectorDied:
                    return PriorityDeath;

                case SnapshotEventKind.PlayerDamaged:
                case SnapshotEventKind.ProjectileEnded:
                    return PriorityImpact;

                case SnapshotEventKind.ProjectileSpawned:
                case SnapshotEventKind.MobSpawned:
                case SnapshotEventKind.StaminaDenied:
                case SnapshotEventKind.WaveStarted:
                case SnapshotEventKind.WaveCleared:
                // Stage 3 Т29 (R-233): both are turning points that destroy
                // nothing — exactly the kind of thing the two wave events beside
                // them. A deferred event is carried into the next frame, not
                // lost, so State rather than Death is the honest rank.
                case SnapshotEventKind.DirectorActivated:
                case SnapshotEventKind.PlayerExtracted:
                    return PriorityState;

                case SnapshotEventKind.ShotHeard:
                case SnapshotEventKind.PlayerDashed:
                case SnapshotEventKind.PlayerSlideStarted:
                case SnapshotEventKind.DashRicocheted:
                // Stage 3 Т29 (R-233): the STATE both of these announce
                // already rides every frame — the Pickups block stops
                // carrying a collected cell, the Containers block carries the
                // "already looted" flag. What the event adds is the MOMENT,
                // and a moment that arrives a frame late costs nothing.
                // ⚠ Rank is not channel (Р136): PickupTaken is cosmetic in
                // rank and PRIVATE in channel, and the two say nothing about
                // each other.
                case SnapshotEventKind.PickupTaken:
                case SnapshotEventKind.ContainerEmptied:
                // Stage 3 Т30: a spark on a wall, and it must never outrank a
                // death. The round it belongs to is ALREADY on this client's
                // screen — it received the spawn, or it would not be sent this
                // record at all — so a reflection that arrives a frame late
                // costs a moment of visual truth and nothing else, while a
                // death that is deferred to make room for it costs the frame
                // its most important fact (Р61: "deaths and hits above
                // cosmetics"). Ranking it any higher would let a firefight's
                // wall hits push kills out of a full frame's budget.
                case SnapshotEventKind.ProjectileRicocheted:
                    return PriorityCosmetic;

                default:
                    throw new System.ArgumentException(
                        $"SnapshotEvents.PriorityOf: unranked SnapshotEventKind {kind} — every kind must be "
                        + "explicitly ranked (Р61); a silently-returned default would let a future kind "
                        + "inherit a rank nobody chose.", nameof(kind));
            }
        }

        /// Payload size of `kind`, in bytes — the number the assembler budgets
        /// against BEFORE writing anything (the writer cannot un-write a frame
        /// that overflowed). Every kind is listed explicitly, same reasoning as
        /// `PriorityOf` above.
        public static int PayloadBytesFor(SnapshotEventKind kind)
        {
            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned: return 8;
                case SnapshotEventKind.ProjectileEnded: return 5;
                case SnapshotEventKind.MobDied: return 4;

                // app-88jb Т8: three bytes wider than the case it used to
                // share with MobDied -- impactSpeed, height and the shooter's
                // own slot ride along now (plan deviation 2).
                case SnapshotEventKind.PlayerDamaged: return 7;
                case SnapshotEventKind.MobSpawned: return 3;
                case SnapshotEventKind.PlayerDied:
                case SnapshotEventKind.DashRicocheted:
                case SnapshotEventKind.WaveStarted:
                case SnapshotEventKind.WaveCleared: return 2;
                case SnapshotEventKind.ShotHeard:
                case SnapshotEventKind.PlayerDashed:
                case SnapshotEventKind.PlayerSlideStarted:
                case SnapshotEventKind.StaminaDenied:
                // Stage 3 Т29: the slot that walked out.
                case SnapshotEventKind.PlayerExtracted: return 1;

                // Stage 3 Т29: the entity's own u16 code (Р278) — the same
                // width every long-lived entity rides on the wire.
                case SnapshotEventKind.PickupTaken:
                case SnapshotEventKind.ContainerEmptied: return 2;

                // Stage 3 Т29 (R-232): THE FIRST ZERO-LENGTH PAYLOADS IN THIS
                // CATALOG, and they are zero on purpose rather than for
                // want of a field. Both ride the All channel, which carries
                // no position by rule (Р28) — and the position they would
                // otherwise carry is exactly what Т21 refused to put on the
                // wire: the spot a collector walked into the core, and the
                // spot the corpse everyone is about to fight over lies on.
                // Nothing else about either is not already in the Match
                // block's own Director bit; what the event adds is the tick
                // it happened on, and the record header carries that.
                case SnapshotEventKind.DirectorActivated:
                case SnapshotEventKind.DirectorDied: return 0;

                // Stage 3 Т30: `id u16 | pos.x u16 | pos.y u16 | normal u8`.
                // THE WIDEST NON-SPAWN RECORD IN THE CATALOG, and four of the
                // seven are the contact point — the only payload here that
                // carries a position of its own (plan deviation 3; see
                // `SnapshotEventPayload.Pos` for the measured relationship
                // with the record header's own point, recorded for the owner
                // by coordinator Ruling 238 rather than acted on here).
                case SnapshotEventKind.ProjectileRicocheted: return 7;

                default:
                    throw new System.ArgumentException(
                        $"SnapshotEvents.PayloadBytesFor: unsized SnapshotEventKind {kind} — every kind must "
                        + "declare its payload length, or the assembler cannot budget a frame before writing "
                        + "it.", nameof(kind));
            }
        }

        // ---- Write side. Each method fills `dst` and returns the byte count
        // it wrote, which always equals PayloadBytesFor(kind). Arguments
        // outside their own domain throw — see the class doc.

        public static int WriteProjectileSpawned(System.Span<byte> dst, int id, byte ownerIndex,
            float2 dir, float horizSpeed, float velZ, float height, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.ProjectileSpawned);
            RequirePlayerSlotOrNoOwner(ownerIndex, in cfg, nameof(ownerIndex));

            float speedCap = SpeedCapFor(ownerIndex, in cfg);
            WriteU16(dst, 0, (ushort)(id & 0xFFFF));
            dst[2] = ownerIndex;
            dst[3] = Quantize.Dir(dir);
            dst[4] = Quantize.Unit(horizSpeed, speedCap);
            WriteU16(dst, 5, Quantize.Pos(velZ, speedCap));
            dst[7] = Quantize.Unit(height, cfg.Hero.MaxAimHeight);
            return SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ProjectileSpawned);
        }

        public static int WriteProjectileEnded(System.Span<byte> dst, int id, ProjectileEndKind endKind,
            HitZone zone, float height, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.ProjectileEnded);
            if ((byte)endKind == (byte)ProjectileEndKind.None || (byte)endKind > MaxProjectileEndKindValue)
                throw new System.ArgumentException(
                    $"SnapshotEvents.WriteProjectileEnded: endKind {(byte)endKind} is outside the written "
                    + $"domain [1, {MaxProjectileEndKindValue}] — None is never sent.", nameof(endKind));
            RequireZone(zone);

            WriteU16(dst, 0, (ushort)(id & 0xFFFF));
            dst[2] = (byte)endKind;
            dst[3] = (byte)zone;
            dst[4] = Quantize.Unit(height, cfg.Hero.MaxAimHeight);
            return PayloadBytesFor(SnapshotEventKind.ProjectileEnded);
        }

        public static int WriteShotHeard(System.Span<byte> dst, byte ownerIndex, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.ShotHeard);
            RequirePlayerSlotOrNoOwner(ownerIndex, in cfg, nameof(ownerIndex));
            dst[0] = ownerIndex;
            return PayloadBytesFor(SnapshotEventKind.ShotHeard);
        }

        public static int WriteMobSpawned(System.Span<byte> dst, int id, MobType type)
        {
            Reserve(dst, SnapshotEventKind.MobSpawned);
            if ((byte)type > SnapshotBlocks.MaxMobTypeValue)
                throw new System.ArgumentException(
                    $"SnapshotEvents.WriteMobSpawned: MobType {(byte)type} is outside the declared domain "
                    + $"(<= {SnapshotBlocks.MaxMobTypeValue}).", nameof(type));
            WriteU16(dst, 0, (ushort)(id & 0xFFFF));
            dst[2] = (byte)type;
            return PayloadBytesFor(SnapshotEventKind.MobSpawned);
        }

        public static int WriteMobDied(System.Span<byte> dst, int id, byte attackerIndex, HitZone zone,
            in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.MobDied);
            // NoOwner is legal here and NOT an oversight: a mob killed by
            // something with no player behind it (a mob's own round, a future
            // hazard) credits nobody, exactly as SimulationWorld.DamageMob's
            // own `ownerIndex` guard already allows.
            RequirePlayerSlotOrNoOwner(attackerIndex, in cfg, nameof(attackerIndex));
            RequireZone(zone);
            WriteU16(dst, 0, (ushort)(id & 0xFFFF));
            dst[2] = attackerIndex;
            dst[3] = (byte)zone;
            return PayloadBytesFor(SnapshotEventKind.MobDied);
        }

        public static int WritePlayerDamaged(System.Span<byte> dst, byte victimIndex, HitZone zone,
            float amount, float2 hitDir, float impactSpeed, float height, byte attackerIndex,
            in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.PlayerDamaged);
            RequirePlayerSlot(victimIndex, in cfg, nameof(victimIndex));
            // app-88jb Т8: the two slot bytes take DIFFERENT guards, and the
            // asymmetry is the point rather than an oversight -- a blow always
            // lands on a real seat, while the round that dealt it may be a
            // mob's and then belongs to nobody. Same pair MobDied carries.
            RequirePlayerSlotOrNoOwner(attackerIndex, in cfg, nameof(attackerIndex));
            RequireZone(zone);
            dst[0] = victimIndex;
            dst[1] = (byte)zone;
            dst[2] = Quantize.Unit(amount, cfg.Hero.MaxHp);
            dst[3] = Quantize.Dir(hitDir);
            // The speed rides the SHOOTER's own scale, through the one home
            // both sides share (`SpeedCapFor`, see the class doc); the height
            // rides `cfg.Hero.MaxAimHeight`, the scale WriteProjectileEnded
            // already quantizes a contact height against. No second home for
            // either rule.
            dst[4] = Quantize.Unit(impactSpeed, SpeedCapFor(attackerIndex, in cfg));
            dst[5] = Quantize.Unit(height, cfg.Hero.MaxAimHeight);
            dst[6] = attackerIndex;
            return PayloadBytesFor(SnapshotEventKind.PlayerDamaged);
        }

        public static int WritePlayerDied(System.Span<byte> dst, byte victimIndex, HitZone zone,
            in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.PlayerDied);
            RequirePlayerSlot(victimIndex, in cfg, nameof(victimIndex));
            RequireZone(zone);
            dst[0] = victimIndex;
            dst[1] = (byte)zone;
            return PayloadBytesFor(SnapshotEventKind.PlayerDied);
        }

        public static int WritePlayerDashed(System.Span<byte> dst, byte actorIndex, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.PlayerDashed);
            RequirePlayerSlot(actorIndex, in cfg, nameof(actorIndex));
            dst[0] = actorIndex;
            return PayloadBytesFor(SnapshotEventKind.PlayerDashed);
        }

        public static int WritePlayerSlideStarted(System.Span<byte> dst, byte actorIndex, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.PlayerSlideStarted);
            RequirePlayerSlot(actorIndex, in cfg, nameof(actorIndex));
            dst[0] = actorIndex;
            return PayloadBytesFor(SnapshotEventKind.PlayerSlideStarted);
        }

        public static int WriteDashRicocheted(System.Span<byte> dst, byte actorIndex, float2 normal,
            in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.DashRicocheted);
            RequirePlayerSlot(actorIndex, in cfg, nameof(actorIndex));
            dst[0] = actorIndex;
            dst[1] = Quantize.Dir(normal);
            return PayloadBytesFor(SnapshotEventKind.DashRicocheted);
        }

        public static int WriteStaminaDenied(System.Span<byte> dst, float amount, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.StaminaDenied);
            // No slot byte: this kind reaches its owner and nobody else
            // (channel Owner, Р28), so who it is about is already known.
            dst[0] = Quantize.Unit(amount, cfg.Hero.StaminaMax);
            return PayloadBytesFor(SnapshotEventKind.StaminaDenied);
        }

        public static int WriteWaveStarted(System.Span<byte> dst, int waveIndex)
        {
            Reserve(dst, SnapshotEventKind.WaveStarted);
            WriteU16(dst, 0, (ushort)(waveIndex & 0xFFFF));
            return PayloadBytesFor(SnapshotEventKind.WaveStarted);
        }

        public static int WriteWaveCleared(System.Span<byte> dst, int waveIndex)
        {
            Reserve(dst, SnapshotEventKind.WaveCleared);
            WriteU16(dst, 0, (ushort)(waveIndex & 0xFFFF));
            return PayloadBytesFor(SnapshotEventKind.WaveCleared);
        }

        /// Stage 3 Т29. WRITES NOTHING AND SAYS SO BY RETURNING 0 — see
        /// `PayloadBytesFor`'s own note on why these two carry no payload.
        /// `Reserve` is still called: it is the one place that would catch a
        /// caller handing in a span shorter than the kind needs, and a kind
        /// needing zero is exactly the case where forgetting to ask would
        /// never be noticed.
        public static int WriteDirectorActivated(System.Span<byte> dst)
        {
            Reserve(dst, SnapshotEventKind.DirectorActivated);
            return PayloadBytesFor(SnapshotEventKind.DirectorActivated);
        }

        public static int WriteDirectorDied(System.Span<byte> dst)
        {
            Reserve(dst, SnapshotEventKind.DirectorDied);
            return PayloadBytesFor(SnapshotEventKind.DirectorDied);
        }

        /// Stage 3 Т29: the slot that walked out. Validated like every other
        /// player slot this file writes — a wire byte naming a seat this
        /// match does not have is a CALLER bug, and the write side throws on
        /// caller bugs (Р82's other half).
        public static int WritePlayerExtracted(System.Span<byte> dst, byte playerIndex,
            in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.PlayerExtracted);
            RequirePlayerSlot(playerIndex, in cfg, nameof(playerIndex));
            dst[0] = playerIndex;
            return PayloadBytesFor(SnapshotEventKind.PlayerExtracted);
        }

        /// Stage 3 Т29: the collected cell's own id, truncated to the u16 code
        /// every long-lived entity rides (Р278) — the receiver maps the code
        /// back to a live entity within THIS frame and this epoch, never
        /// across frames.
        public static int WritePickupTaken(System.Span<byte> dst, int pickupId)
        {
            Reserve(dst, SnapshotEventKind.PickupTaken);
            WriteU16(dst, 0, (ushort)(pickupId & 0xFFFF));
            return PayloadBytesFor(SnapshotEventKind.PickupTaken);
        }

        /// Stage 3 Т29: the emptied container's own id, same u16 code and the
        /// same one-frame mapping contract as `WritePickupTaken` above.
        public static int WriteContainerEmptied(System.Span<byte> dst, int containerId)
        {
            Reserve(dst, SnapshotEventKind.ContainerEmptied);
            WriteU16(dst, 0, (ushort)(containerId & 0xFFFF));
            return PayloadBytesFor(SnapshotEventKind.ContainerEmptied);
        }

        /// Stage 3 Т30: the contact point and the surface normal of a round
        /// that mirrored off static geometry and FLEW ON — the catalog's only
        /// mid-flight record. Layout: `id u16 | pos.x u16 | pos.y u16 |
        /// normal u8`.
        ///
        /// THE POINT RIDES `Quantize.Pos` AGAINST `Arena.Radius`, which is the
        /// scale every position on the wire already uses (`SnapshotWriter`'s
        /// player, mob and pickup positions, and the Events-block record
        /// header itself). No new codec and no new constant: a contact point
        /// is a point in the arena, so it lives in `Pos`'s own symmetric
        /// domain by construction. The step is `2 * Radius / 65535`, which is
        /// the tolerance the round-trip test states rather than invents.
        ///
        /// NO SLOT GUARD, AND ITS ABSENCE IS THE STATEMENT: this payload
        /// carries no player index at all — a reflection names a ROUND, not a
        /// collector — so there is no seat for a caller to get wrong and
        /// nothing for `RequirePlayerSlot` to check. Every other writer in
        /// this file that omits the guard omits it for the same reason
        /// (`WriteWaveStarted`, `WritePickupTaken`).
        ///
        /// `normal` IS NOT VALIDATED FOR BEING A UNIT VECTOR, deliberately:
        /// `Quantize.Dir` takes an angle through `atan2`, which is
        /// scale-free, so a non-unit input encodes to exactly the code its
        /// direction deserves. A ZERO normal is the one degenerate input, and
        /// it is handled where it can be handled — `atan2(0, 0)` is 0 by
        /// contract, so it encodes as +X, and the presentation layer's own
        /// zero-normal guard (`PersistentPropsDirector.HandleRicocheted`) is
        /// what keeps a degenerate contact from writing a Unity error into
        /// the log. The emit site never produces one: a ricochet requires
        /// `dot(vel, normal) < 0`, which a zero vector cannot satisfy.
        public static int WriteProjectileRicocheted(System.Span<byte> dst, int id, float2 pos,
            float2 normal, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.ProjectileRicocheted);
            WriteU16(dst, 0, (ushort)(id & 0xFFFF));
            WriteU16(dst, 2, Quantize.Pos(pos.x, cfg.Arena.Radius));
            WriteU16(dst, 4, Quantize.Pos(pos.y, cfg.Arena.Radius));
            dst[6] = Quantize.Dir(normal);
            return PayloadBytesFor(SnapshotEventKind.ProjectileRicocheted);
        }

        // ---- Read side. One entry point, dispatching on the kind the record
        // header already carried. Never throws, on any byte sequence (Р82).

        /// Decodes one record's payload. `false` means refused — read `error`
        /// for which — and `value` is left `default`, never half-filled.
        ///
        /// A kind this catalog does not know is refused as `MalformedContent`
        /// rather than skipped: the SKIPPING already happened one level up, in
        /// Task 27's `TryReadEventsBlock`, which walks records by their declared
        /// lengths without ever consulting this file. Reaching here with an
        /// unknown kind means a caller decided to decode it anyway, which is a
        /// question this catalog can only answer with "no".
        public static bool TryReadPayload(SnapshotEventKind kind, System.ReadOnlySpan<byte> payload,
            in SimConfig cfg, out SnapshotEventPayload value, out SnapshotBlockError error)
        {
            value = default;

            if (!IsKnown(kind))
            {
                error = SnapshotBlockError.MalformedContent;
                return false;
            }
            if (payload.Length != PayloadBytesFor(kind))
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            // Content validation is its OWN pass, before a single field is
            // assigned (the discipline Task 27's fix-round I1 established), so
            // the "value is left default on refusal" contract above is
            // literally true rather than nearly true.
            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned:
                case SnapshotEventKind.ShotHeard:
                {
                    byte owner = kind == SnapshotEventKind.ProjectileSpawned ? payload[2] : payload[0];
                    if (!IsPlayerSlotOrNoOwner(owner, in cfg)) { error = SnapshotBlockError.MalformedContent; return false; }
                    break;
                }
                case SnapshotEventKind.ProjectileEnded:
                {
                    if (payload[2] == (byte)ProjectileEndKind.None || payload[2] > MaxProjectileEndKindValue
                        || payload[3] > MaxHitZoneValue)
                    { error = SnapshotBlockError.MalformedContent; return false; }
                    break;
                }
                case SnapshotEventKind.MobSpawned:
                {
                    if (payload[2] > SnapshotBlocks.MaxMobTypeValue) { error = SnapshotBlockError.MalformedContent; return false; }
                    break;
                }
                case SnapshotEventKind.MobDied:
                {
                    if (!IsPlayerSlotOrNoOwner(payload[2], in cfg) || payload[3] > MaxHitZoneValue)
                    { error = SnapshotBlockError.MalformedContent; return false; }
                    break;
                }
                case SnapshotEventKind.PlayerDamaged:
                {
                    // app-88jb Т8 split this off PlayerDied: since deviation 2
                    // the payload carries TWO slot bytes, and the halves are
                    // deliberately unequal. payload[0] is the VICTIM and must
                    // be a seat this match has -- a blow lands on a real
                    // collector or the bytes are not ours. payload[6] is the
                    // SHOOTER and may also be NoOwner, because a mob's round
                    // has no player behind it. Exactly the pair MobDied uses
                    // four lines up, for the same two reasons.
                    if (!IsPlayerSlot(payload[0], in cfg) || payload[1] > MaxHitZoneValue
                        || !IsPlayerSlotOrNoOwner(payload[6], in cfg))
                    { error = SnapshotBlockError.MalformedContent; return false; }
                    break;
                }
                case SnapshotEventKind.PlayerDied:
                {
                    if (!IsPlayerSlot(payload[0], in cfg) || payload[1] > MaxHitZoneValue)
                    { error = SnapshotBlockError.MalformedContent; return false; }
                    break;
                }
                case SnapshotEventKind.PlayerDashed:
                case SnapshotEventKind.PlayerSlideStarted:
                case SnapshotEventKind.DashRicocheted:
                // Stage 3 Т29: same bound, same reason — a seat this match
                // does not have is content this side refuses rather than
                // passes on.
                case SnapshotEventKind.PlayerExtracted:
                {
                    if (!IsPlayerSlot(payload[0], in cfg)) { error = SnapshotBlockError.MalformedContent; return false; }
                    break;
                }

                // Stage 3 Т29: the two id-carrying kinds have NO content
                // constraint, stated rather than left to be inferred from
                // their absence. Every u16 is a legal entity code — the
                // receiver's job is to map it to a live entity of THIS frame
                // and find nothing if it cannot (Р278), which is an ordinary
                // outcome and not malformed content. The same is true of the
                // two zero-length kinds, which have no byte to constrain.
                case SnapshotEventKind.PickupTaken:
                case SnapshotEventKind.ContainerEmptied:
                case SnapshotEventKind.DirectorActivated:
                case SnapshotEventKind.DirectorDied:
                // Stage 3 Т30: no constraint either, and for the same reason
                // spelled out rather than left to inference. Its three fields
                // are a u16 id (every code is legal, mapping it to a live
                // round is the receiver's job) and two quantized coordinates
                // plus an angle, and `Quantize`'s decoders are CLAMPING
                // rather than validating — every one of the 65536 codes
                // decodes to a point inside the arena by construction, so
                // there is no byte here a hostile sender could put out of
                // domain.
                case SnapshotEventKind.ProjectileRicocheted:
                    break;
            }

            value.Kind = kind;
            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned:
                {
                    // ownerIndex FIRST — it selects the speed scale the two
                    // following fields decode against (see the class doc).
                    byte owner = payload[2];
                    float speedCap = SpeedCapFor(owner, in cfg);
                    value.Id = ReadU16(payload, 0);
                    value.PlayerIndex = owner;
                    value.Dir = Quantize.DirBack(payload[3]);
                    value.HorizSpeed = Quantize.UnitBack(payload[4], speedCap);
                    value.VelZ = Quantize.PosBack(ReadU16(payload, 5), speedCap);
                    value.Height = Quantize.UnitBack(payload[7], cfg.Hero.MaxAimHeight);
                    break;
                }
                case SnapshotEventKind.ProjectileEnded:
                    value.Id = ReadU16(payload, 0);
                    value.EndKind = (ProjectileEndKind)payload[2];
                    value.Zone = (HitZone)payload[3];
                    value.Height = Quantize.UnitBack(payload[4], cfg.Hero.MaxAimHeight);
                    break;

                case SnapshotEventKind.ShotHeard:
                    value.PlayerIndex = payload[0];
                    break;

                case SnapshotEventKind.MobSpawned:
                    value.Id = ReadU16(payload, 0);
                    value.MobType = (MobType)payload[2];
                    break;

                case SnapshotEventKind.MobDied:
                    value.Id = ReadU16(payload, 0);
                    value.PlayerIndex = payload[2];
                    value.Zone = (HitZone)payload[3];
                    break;

                case SnapshotEventKind.PlayerDamaged:
                {
                    // app-88jb Т8: attackerIndex FIRST -- it selects the speed
                    // scale the line below decodes against, the same ordering
                    // rule ProjectileSpawned's ownerIndex follows above. Read
                    // it after the speed and a mob's round would come back on
                    // the collector's scale.
                    byte attacker = payload[6];
                    value.PlayerIndex = payload[0];
                    value.AttackerIndex = attacker;
                    value.Zone = (HitZone)payload[1];
                    value.Amount = Quantize.UnitBack(payload[2], cfg.Hero.MaxHp);
                    value.Dir = Quantize.DirBack(payload[3]);
                    value.ImpactSpeed = Quantize.UnitBack(payload[4], SpeedCapFor(attacker, in cfg));
                    value.Height = Quantize.UnitBack(payload[5], cfg.Hero.MaxAimHeight);
                    break;
                }

                case SnapshotEventKind.PlayerDied:
                    value.PlayerIndex = payload[0];
                    value.Zone = (HitZone)payload[1];
                    break;

                case SnapshotEventKind.PlayerDashed:
                case SnapshotEventKind.PlayerSlideStarted:
                    value.PlayerIndex = payload[0];
                    break;

                case SnapshotEventKind.DashRicocheted:
                    value.PlayerIndex = payload[0];
                    value.Dir = Quantize.DirBack(payload[1]);
                    break;

                case SnapshotEventKind.StaminaDenied:
                    value.Amount = Quantize.UnitBack(payload[0], cfg.Hero.StaminaMax);
                    break;

                case SnapshotEventKind.WaveStarted:
                case SnapshotEventKind.WaveCleared:
                    value.WaveIndex = ReadU16(payload, 0);
                    break;

                case SnapshotEventKind.PlayerExtracted:
                    value.PlayerIndex = payload[0];
                    break;

                case SnapshotEventKind.PickupTaken:
                case SnapshotEventKind.ContainerEmptied:
                    value.Id = ReadU16(payload, 0);
                    break;

                case SnapshotEventKind.ProjectileRicocheted:
                    // Against the SAME `Arena.Radius` the writer used, and the
                    // normal back through `DirBack` — the pair `Quantize`
                    // guarantees idempotent (Р34). `Dir` carries the surface
                    // normal here, exactly as it does for `DashRicocheted`
                    // one branch up; the two are the same fact about a
                    // contact, seen from the round's side and the actor's.
                    value.Id = ReadU16(payload, 0);
                    value.Pos = new float2(
                        Quantize.PosBack(ReadU16(payload, 2), cfg.Arena.Radius),
                        Quantize.PosBack(ReadU16(payload, 4), cfg.Arena.Radius));
                    value.Dir = Quantize.DirBack(payload[6]);
                    break;

                // DirectorActivated / DirectorDied assign nothing: their
                // payload is empty and `value.Kind` above is the whole
                // message. Listed by their absence deliberately — adding a
                // `case … : break;` here would read as a field somebody
                // forgot to fill.
            }

            error = SnapshotBlockError.None;
            return true;
        }

        /// The quantization scale a round's speeds ride: the player weapon's
        /// for a player-owned round, the Gunner archetype's for a mob's. ONE
        /// home for the rule, called by both sides — the same fix Task 27
        /// applied to `SnapshotBlocks.MaxHpFor` after that branch had been
        /// written out twice.
        public static float SpeedCapFor(byte ownerIndex, in SimConfig cfg)
            => ownerIndex == ProjectileIds.NoOwner ? cfg.Gunner.ProjectileSpeed : cfg.Weapon.ProjectileSpeed;

        /// ⚠ THE ONLY HOME IN THIS FILE THAT DOES NOT THROW ON AN
        /// UNACCOUNTED KIND, which is why it is the one a new kind is
        /// silently forgotten in: the bound is the LAST MEMBER of the enum,
        /// so a kind appended past it decodes as `MalformedContent` on the
        /// receiver and nowhere else. Stage 3 Т29 moved it from `WaveCleared`
        /// to `ContainerEmptied`, and Stage 3 Т30 from `ContainerEmptied` to
        /// `ProjectileRicocheted`; whoever appends the next kind moves it
        /// again, and `SnapshotCodecTests.EveryStage3Kind_RoundTripsItsOwnPayload`
        /// is the witness that says so for the Т29 five, with
        /// `ProjectileRicocheted_RoundTripsPointAndNormal` for Т30's own.
        static bool IsKnown(SnapshotEventKind kind)
            => kind != SnapshotEventKind.None && (byte)kind <= (byte)SnapshotEventKind.ProjectileRicocheted;

        static bool IsPlayerSlot(byte index, in SimConfig cfg) => index < cfg.Arena.MaxPlayers;

        static bool IsPlayerSlotOrNoOwner(byte index, in SimConfig cfg)
            => index == ProjectileIds.NoOwner || index < cfg.Arena.MaxPlayers;

        static void RequirePlayerSlot(byte index, in SimConfig cfg, string argument)
        {
            if (!IsPlayerSlot(index, in cfg))
                throw new System.ArgumentException(
                    $"SnapshotEvents: {argument} {index} is not a slot of this match "
                    + $"(Arena.MaxPlayers {cfg.Arena.MaxPlayers}).", argument);
        }

        static void RequirePlayerSlotOrNoOwner(byte index, in SimConfig cfg, string argument)
        {
            if (!IsPlayerSlotOrNoOwner(index, in cfg))
                throw new System.ArgumentException(
                    $"SnapshotEvents: {argument} {index} is neither a slot of this match "
                    + $"(Arena.MaxPlayers {cfg.Arena.MaxPlayers}) nor ProjectileIds.NoOwner.", argument);
        }

        static void RequireZone(HitZone zone)
        {
            if ((byte)zone > MaxHitZoneValue)
                throw new System.ArgumentException(
                    $"SnapshotEvents: HitZone {(byte)zone} is outside the declared domain "
                    + $"(<= {MaxHitZoneValue}).", nameof(zone));
        }

        static void Reserve(System.Span<byte> dst, SnapshotEventKind kind)
        {
            int need = PayloadBytesFor(kind);
            if (dst.Length < need)
                throw new System.ArgumentException(
                    $"SnapshotEvents: {kind} needs {need} bytes, the destination holds {dst.Length}. "
                    + "The caller owns the payload pool (task-28-brief §2.8).", nameof(dst));
        }

        static void WriteU16(System.Span<byte> dst, int offset, ushort value)
        {
            dst[offset] = (byte)(value & 0xFF);
            dst[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        static ushort ReadU16(System.ReadOnlySpan<byte> src, int offset)
            => (ushort)(src[offset] | (src[offset + 1] << 8));
    }
}
