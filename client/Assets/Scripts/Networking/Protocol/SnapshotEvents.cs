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
        public byte PlayerIndex;

        /// Decoded unit heading: the round's horizontal direction for
        /// `ProjectileSpawned`, the blow's impact direction for
        /// `PlayerDamaged`, the wall normal for `DashRicocheted`.
        public float2 Dir;

        /// `ProjectileSpawned` only: the round's HORIZONTAL speed. It is not
        /// the config's `ProjectileSpeed` for an aimed player shot —
        /// WeaponSystem normalizes a full 3D vector to `ProjectileSpeed`, so
        /// the horizontal component is `ProjectileSpeed * cos(elevation)` — and
        /// so it rides the wire explicitly instead of being assumed.
        public float HorizSpeed;

        /// `ProjectileSpawned` only: the round's vertical speed, signed.
        public float VelZ;

        /// Meters above ground: the muzzle height for `ProjectileSpawned`, the
        /// contact height for a `Blocked` `ProjectileEnded` (0 otherwise).
        public float Height;

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
    ///   PlayerDamaged      4 B  victimIndex u8 | zone u8 | amount u8 | hitDir u8
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
                case SnapshotEventKind.MobDied:
                case SnapshotEventKind.PlayerDamaged: return 4;
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
            float amount, float2 hitDir, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.PlayerDamaged);
            RequirePlayerSlot(victimIndex, in cfg, nameof(victimIndex));
            RequireZone(zone);
            dst[0] = victimIndex;
            dst[1] = (byte)zone;
            dst[2] = Quantize.Unit(amount, cfg.Hero.MaxHp);
            dst[3] = Quantize.Dir(hitDir);
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
                    value.PlayerIndex = payload[0];
                    value.Zone = (HitZone)payload[1];
                    value.Amount = Quantize.UnitBack(payload[2], cfg.Hero.MaxHp);
                    value.Dir = Quantize.DirBack(payload[3]);
                    break;

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
        /// to `ContainerEmptied`; whoever appends the next kind moves it
        /// again, and `SnapshotCodecTests.EveryStage3Kind_RoundTripsItsOwnPayload`
        /// is the witness that says so.
        static bool IsKnown(SnapshotEventKind kind)
            => kind != SnapshotEventKind.None && (byte)kind <= (byte)SnapshotEventKind.ContainerEmptied;

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
