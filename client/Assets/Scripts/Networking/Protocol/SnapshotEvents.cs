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
        /// surface normal, so the client can put a spark and a sound where the
        /// contact actually happened.
        ///
        /// ⚠ AND ONLY THAT, UNTIL Т32 — measured rather than assumed. A
        /// networked client's tracer is `TracerProjectiles`' closed form
        /// (`SpawnPos + Vel * (dt * age)`, with `ProjectileSpawned` and
        /// `ProjectileEnded` the only two kinds its router reads), so it does
        /// not turn on a reflection: it flies straight on THROUGH the wall
        /// until the round's real ending retires it. This record moves the
        /// spark and the sound to the right spot; moving the TRACER there is
        /// Р420, i.e. spec §3.8, i.e. Т32. (The tracer that visibly turns is
        /// the offline/host path, where `ViewRegistry.SyncProjectiles` draws
        /// the world's real bodies.)
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
        /// `PlayerDamaged`, the wall normal for `DashRicocheted` and (app-5o2q)
        /// for `ProjectileRicocheted` — the same fact about one contact, seen
        /// from the round's side rather than the actor's — and, since app-88jb
        /// Т31, the round's travel direction at contact for a `ProjectileEnded`
        /// that ended on a BODY (`HitMob`/`HitPlayer`).
        ///
        /// ⚠ THE TWO SURFACE ENDINGS CARRY NO HEADING, and the byte they
        /// still spend on one decodes to `(1, 0)` rather than to nothing:
        /// `Blocked` and `Expired` are written with the zero vector, which
        /// `Quantize.Dir` takes through `atan2(0, 0)` = 0, i.e. the middle
        /// code. A reader must therefore take the ENDING KIND first and read
        /// this field only for the two body endings — the same "take the
        /// discriminator first" rule `ownerIndex` already imposes on the two
        /// speed fields above.
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
        /// contact height for a `Blocked` `ProjectileEnded` and, since app-88jb
        /// Т8, the contact height of the blow for `PlayerDamaged` (0 otherwise).
        ///
        /// app-88jb Т31 ADDED THE TWO BODY ENDINGS to that list: a `HitMob`
        /// and a `HitPlayer` `ProjectileEnded` now carry the height the round
        /// entered the body at, on the same `cfg.Hero.MaxAimHeight` scale the
        /// wall contact rides. Until then the assembler wrote a zero for both
        /// — "a body is not a surface" — and every impact spark on a networked
        /// client drew on the FLOOR under the target instead of at the belt
        /// that was hit (`PersistentPropsDirector.SpawnHitSpark` and
        /// `SpawnPlayerHitSpark` both read this field).
        ///
        /// app-5o2q ADDED `ProjectileRicocheted` to that same list: a
        /// reflection happens ON a surface, so it has a contact height like
        /// any other, and `PersistentPropsDirector.HandleRicocheted` lifts its
        /// spark by exactly this field. The "(0 otherwise)" above is therefore
        /// one kind shorter than it was.
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

        /// The entity the round ended ON (app-88jb Т31): a MobState.Id for
        /// HitMob, 0 for every other ending. Without it a networked client
        /// knows WHAT was hit but not WHOM to tilt (finding D2-C2). NOT `Id`
        /// above — that field is the ROUND's own id, which the tracer and the
        /// ghost are retired by, and it stays so.
        ///
        /// THREE READERS ON THE CLIENT, AND `MobVisual` IS NONE OF THEM. An
        /// earlier wording of this doc named it, from the red phase, while the
        /// plan still rebuilt the tilt inside the view; the owner put that
        /// reconstruction in the network backend instead (Ruling 255), and
        /// this is the measured list. `ClientEventDecoder` lifts the field
        /// into `SimEvent.EntityId` on the HitMob ending — and only there —
        /// after which three consumers address a body by it:
        /// `GameFeelDirector.HandleProjectileHit` flashes the view it finds by
        /// that id, `ViewRegistry.HandleEvent` gives the struck body its tilt
        /// AXIS through `SetHitDir` on the view it finds by that id, and
        /// `NetworkSimBackend.ApplyMobHit` asks `MobTypeMemory` for the
        /// victim's archetype by it and keys `MobTiltIntegrator`'s slot on it.
        /// `MobVisual` never sees an id at all: the backend patches the
        /// finished `Tilt` into the published pair, and the component draws
        /// the same field it always drew.
        public int VictimId;

        /// `MobSpawned` only.
        public MobType MobType;

        /// `ProjectileSpawned` only: how many flight steps the round had
        /// already taken when the tick it was born in ended (app-88jb Т32,
        /// coordinator Ruling 291; review finding D2-C7, bd app-56kx). Zero
        /// for every other kind.
        ///
        /// WHY IT RIDES. The record's own header position is the MUZZLE — the
        /// pre-step point the shot happened at, which is what the shot sound
        /// and a mob's muzzle flash are placed by, and what this assembler
        /// measures the round's own relevance segment from — while every
        /// BODY in a snapshot is an end-of-tick state. On top of that app-88jb
        /// Т27 gives a fresh round `RewindSplit.InputTicks` catch-up steps for
        /// the shooter's own input lag, and the projectile phase steps it once
        /// more in the same tick. A receiver seeding a tracer at the header
        /// point therefore draws the round permanently short by exactly this
        /// many steps — 1.75 m each at the shipped speed, and up to four of
        /// them.
        ///
        /// IT IS A COUNT, NOT A DISTANCE, and it crosses the wire exactly
        /// rather than quantized: the receiver spends it as
        /// `Dir * HorizSpeed * TickDt * BirthSteps`, and both factors are
        /// fields of this same payload.
        public int BirthSteps;

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
    ///   ProjectileSpawned  9 B  id u16 | ownerIndex u8 | dir u8 | horizSpeed u8
    ///                           | velZ u16 | height u8 | birthSteps u8
    ///   ProjectileEnded    8 B  id u16 | endKind u8 | zone u8 | height u8
    ///                           | hitDir u8 | victimId u16
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
    /// exist, a step count larger than the arena could have produced — because
    /// Task 32 and Tasks 43-45 index per-slot view pools and prefab tables by
    /// exactly these values, and because a count spent on GEOMETRY draws the
    /// round where it never was (app-88jb Т32, coordinator Ruling 300: the
    /// third class of refusal here, and the first whose reason is not
    /// indexing). Every one of them is refused PER RECORD — the block walker
    /// above keeps parsing, so a hostile byte costs one round its tracer and
    /// never costs the frame.
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
        /// The largest payload any kind produces. ONE KIND STANDS AT IT again
        /// since app-88jb Т32 — `ProjectileSpawned`, at nine bytes — where the
        /// tie `ProjectileEnded` held with it since Т31 is broken: the round's
        /// birth-step count went onto the spawn and nothing went onto the
        /// ending, which stayed at eight. The assembler sizes its per-record
        /// payload slots by this, so a carried-over event never needs a
        /// variable-length pool.
        ///
        /// ⛔ THE CEILING HAS ALREADY BEEN LIFTED ONCE, AND THIS IS WHAT IT
        /// COST (app-88jb Т32, coordinator Ruling 292). The note that stood
        /// here promised that the next field on either tying kind would lift
        /// it; the field arrived, and the bill came to less than the handoff
        /// had estimated. All nineteen uses of this constant in
        /// `SnapshotAssembler` are of the shape `slot * MaxPayloadBytes` or a
        /// pool length, so every one of them scaled itself and not a single
        /// stride was rewritten — measured, not assumed. What was edited is
        /// this constant, the width in `PayloadBytesFor`, the writer, the
        /// reader and the named pins in `SnapshotCodecTests`. The FOUR pools
        /// sized by this constant — `_wirePayload`, and the per-connection
        /// `QueuePayload`, `HistoryPayload` and `EventPayloadScratch` — grew by
        /// an eighth, and all four grew by themselves.
        ///   `ProtocolVersion` did NOT move with it, and the reason is a
        /// precedent rather than a convenience: app-88jb Т8 widened
        /// `PlayerDamaged` from four bytes to seven without a bump. That
        /// file's rule asks for a bump when the MEANING of bytes already on
        /// the wire changes or a DOMAIN grows, and a kind that simply got
        /// wider is neither.
        ///   THE NEXT KIND TO GROW IS STILL THE ONE THAT PAYS: any field added
        /// to `ProjectileSpawned` lifts this constant again, while
        /// `ProjectileEnded` now has one byte of room under it and would cost
        /// nothing until it reaches nine.
        public const int MaxPayloadBytes = 9;

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
                // app-88jb Т32: one byte wider than it was — `birthSteps u8`
                // rides along now, so a networked client can seed its tracer
                // where the round already is at the end of its birth tick
                // rather than at the muzzle it was reported from (Ruling 291,
                // review finding D2-C7). It is the widest kind in the catalog
                // and the only one standing at `MaxPayloadBytes`.
                case SnapshotEventKind.ProjectileSpawned: return 9;

                // app-88jb Т31: three bytes wider than it was — `hitDir u8 |
                // victimId u16` ride along now, so a networked client can put
                // the spark at the contact, flash the body that was hit and
                // give it an axis to tilt about. That made it the second kind
                // at `MaxPayloadBytes` until Т32 lifted the ceiling past it;
                // it is now the RUNNER-UP, one byte under.
                case SnapshotEventKind.ProjectileEnded: return 8;
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

                // Stage 3 Т30, narrowed by app-5o2q: `id u16 | normal u8 |
                // height u8`. SEVEN BYTES BECAME FOUR when the contact point
                // stopped riding here: a position belongs to the RECORD
                // HEADER, which is where every other kind's travels and where
                // this one's had a second, unread copy. The byte that arrived
                // in their place is the contact HEIGHT, on the same
                // `Hero.MaxAimHeight` scale `ProjectileEnded`, `PlayerDamaged`
                // and `ProjectileSpawned` already spend a height byte on —
                // without it the spark of a mirrored round draws on the floor.
                case SnapshotEventKind.ProjectileRicocheted: return 4;

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

        /// A round's birth: `id u16 | ownerIndex u8 | dir u8 | horizSpeed u8 |
        /// velZ u16 | height u8 | birthSteps u8` (app-88jb Т32 added the last).
        ///
        /// `birthSteps` IS A MEASUREMENT THAT RIDES RAW, which is what
        /// separates it from every other byte here: the four physical
        /// quantities before it (dir, horizSpeed, velZ, height) are quantized
        /// against scales taken from `cfg`, and the three that are not (id,
        /// ownerIndex) NAME something rather than measure it. A count is small
        /// and exact, so quantizing it would only be a way to lose it. The
        /// receiver spends it by multiplying its own decoded
        /// `dir * horizSpeed * TickDt` by it. See
        /// `SnapshotEventPayload.BirthSteps` for what the number means and
        /// `Ring.Simulation.Core.SimEvent.BirthSteps` for where it comes from.
        ///
        /// THE COUNT IS GUARDED FOR FITTING ITS BYTE, AND FOR NOTHING ELSE
        /// (app-88jb Т32, coordinator Ruling 301). The parameter is an `int`
        /// and the slot is a byte, so the cast below is a lossy one — and it
        /// loses into the worst value there is: 256 would arrive as 0, which
        /// is exactly the code the receiver must read as "nothing is known
        /// about the birth tick" and seed at the muzzle. A silent loss of that
        /// shape is what `RequirePlayerSlot`, `RequireZone` and `Reserve`
        /// exist for, so this argument gets the same treatment.
        ///   ⛔ THE ARENA'S OWN DOMAIN — how many steps a round COULD have
        /// taken — is deliberately NOT checked here, and its absence is the
        /// decision rather than an oversight. The two sides ask different
        /// questions: a writer is asked whether the number fits the slot the
        /// format gave it, which is this file's own business, while whether a
        /// number is BELIEVABLE is a question about someone else's traffic and
        /// is answered on the read side against `cfg` (see `TryReadPayload`).
        /// Asking it here too would give the bound a second home, and the
        /// reader's whole argument for taking an UPPER bound rests on there
        /// being only one home for the split rule it comes from.
        public static int WriteProjectileSpawned(System.Span<byte> dst, int id, byte ownerIndex,
            float2 dir, float horizSpeed, float velZ, float height, int birthSteps,
            in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.ProjectileSpawned);
            RequirePlayerSlotOrNoOwner(ownerIndex, in cfg, nameof(ownerIndex));
            RequireByteRange(birthSteps, nameof(birthSteps));

            float speedCap = SpeedCapFor(ownerIndex, in cfg);
            WriteU16(dst, 0, (ushort)(id & 0xFFFF));
            dst[2] = ownerIndex;
            dst[3] = Quantize.Dir(dir);
            dst[4] = Quantize.Unit(horizSpeed, speedCap);
            WriteU16(dst, 5, Quantize.Pos(velZ, speedCap));
            dst[7] = Quantize.Unit(height, cfg.Hero.MaxAimHeight);
            dst[8] = (byte)birthSteps;
            return SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ProjectileSpawned);
        }

        /// A round's ending: `id u16 | endKind u8 | zone u8 | height u8 |
        /// hitDir u8 | victimId u16` (app-88jb Т31 added the last three).
        ///
        /// `hitDir` IS THE ROUND'S TRAVEL DIRECTION AT CONTACT, and it is
        /// written for BOTH BODY ENDINGS through one call rather than a
        /// special case per kind: `SimEvent.HitDir` is filled by the emit
        /// sites of `ProjectileHit` and `ProjectileHitPlayer` alike, and the
        /// receiving side needs it for both — it is the axis a struck mob
        /// tips about, and the same fact about a hit on a collector.
        /// `Blocked` and `Expired` are handed the ZERO VECTOR instead, which
        /// encodes as code 128 by `Quantize.Dir`'s `atan2(0, 0)` contract; the
        /// reader is told not to read the field for those two (see
        /// `SnapshotEventPayload.Dir`), so what the byte holds for them is a
        /// consequence of writing every byte rather than a claim about a
        /// direction. The wall NORMAL is a different quantity, is not on the
        /// wire at all, and stays the recorded limit `ClientEventDecoder`'s
        /// own class doc names — not this task's business.
        ///
        /// `victimId` IS THE `MobState.Id` OF A `HitMob` AND ZERO EVERYWHERE
        /// ELSE, `HitPlayer` INCLUDED — the asymmetry is the decision
        /// (coordinator Ruling 243). Entity ids are minted from a counter that
        /// starts at 1, so 0 is a safe "no mob" for the three other endings;
        /// a hit on a PLAYER has no such spare value, because the victim there
        /// is a SEAT and seat 0 is a real one. There is no sentinel a player
        /// slot could ride under, so the victim of a `HitPlayer` is simply not
        /// on the wire, and the field stays 0 rather than carrying a number
        /// the receiver would read as a mob's identity.
        ///
        /// ⚠ EIGHT BYTES IS ONE UNDER `MaxPayloadBytes` SINCE app-88jb Т32,
        /// where it used to be exactly the ceiling. This kind tied
        /// `ProjectileSpawned` there from Т31 until Т32 put a ninth byte on
        /// the spawn and lifted the ceiling past both; the tie is broken and
        /// this kind is now the runner-up, with one byte of room under the
        /// stride the assembler sizes its per-record payload slots by. So a
        /// tenth field HERE is free and a tenth field on the SPAWN is not —
        /// the opposite of the arrangement Т31 recorded.
        public static int WriteProjectileEnded(System.Span<byte> dst, int id, ProjectileEndKind endKind,
            HitZone zone, float height, float2 hitDir, int victimId, in SimConfig cfg)
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
            // EVERY BYTE IS WRITTEN, the two surface endings' neutral pair
            // included: `Reserve` only checks the destination's LENGTH and
            // never clears it, and the caller's pool is reused across records
            // (the assembler's per-slot span) or sentinel-filled (the codec
            // tests' own buffer). A byte left unwritten would decode as
            // whatever the previous record put there.
            dst[5] = Quantize.Dir(hitDir);
            // The same `& 0xFFFF` truncation `id` above takes, and the same
            // lossy contract with it (`SnapshotEventPayload.Id`): two ids
            // exactly 65536 apart share one code, and mapping a code back to
            // a live mob is the receiver's job.
            WriteU16(dst, 6, (ushort)(victimId & 0xFFFF));
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

        /// Stage 3 Т30, narrowed by app-5o2q: the surface normal and the
        /// CONTACT HEIGHT of a round that mirrored off static geometry and
        /// FLEW ON — the catalog's only mid-flight record. Layout:
        /// `id u16 | normal u8 | height u8`.
        ///
        /// THE CONTACT POINT IS NOT IN THIS PAYLOAD, and its absence is the
        /// owner's decision (spec §6k) rather than an omission: the point
        /// rides the RECORD HEADER (`SnapshotBlocks.EventRecord.Pos`, filled
        /// per connection by the assembler), which is where every other kind's
        /// position travels and where `DashRicocheted`'s own wall contact has
        /// always traveled. One number, one home — a second copy on the wire
        /// had no combat reader and no rule saying which copy wins the day the
        /// two disagree.
        ///
        /// THE HEIGHT RIDES `Quantize.Unit` AGAINST `Hero.MaxAimHeight`, the
        /// scale `ProjectileEnded`, `PlayerDamaged` and `ProjectileSpawned`
        /// already spend a height byte on. No new codec and no new constant
        /// (rule 2); the step is `MaxAimHeight / 255`, which is the tolerance
        /// the tests state rather than invent. Without it the spark of a
        /// mirrored round draws on the floor while the spark of an absorbed
        /// one draws at the contact — the same wall, two different places.
        ///
        /// EVERY BYTE IS WRITTEN, none left to whatever the buffer held:
        /// `Reserve` only checks the destination's LENGTH, and the caller's
        /// pool is reused across ticks, so a byte this method skipped would
        /// carry the previous tenant's value onto the wire. The same reasoning
        /// `WriteProjectileEnded` states for its own zero-valued fields.
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
        public static int WriteProjectileRicocheted(System.Span<byte> dst, int id, float2 normal,
            float height, in SimConfig cfg)
        {
            Reserve(dst, SnapshotEventKind.ProjectileRicocheted);
            WriteU16(dst, 0, (ushort)(id & 0xFFFF));
            dst[2] = Quantize.Dir(normal);
            dst[3] = Quantize.Unit(height, cfg.Hero.MaxAimHeight);
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
                    // ⛔ THESE TWO KINDS SHARE A CASE AND DO NOT SHARE A
                    // LENGTH — a `ShotHeard` payload is ONE byte — so every
                    // index past 0 in this block is gated on the kind. The
                    // owner byte is the exception that proves it: its OFFSET
                    // differs per kind but its BOUND is the same one, so it is
                    // selected once and checked once for both.
                    bool spawned = kind == SnapshotEventKind.ProjectileSpawned;
                    byte owner = spawned ? payload[2] : payload[0];
                    if (!IsPlayerSlotOrNoOwner(owner, in cfg)) { error = SnapshotBlockError.MalformedContent; return false; }

                    // app-88jb Т32 (coordinator Ruling 300): the birth-step
                    // count is the first byte of this catalog a hostile sender
                    // could spend on GEOMETRY rather than on identity, so it
                    // is the first one that needed a domain. The receiver
                    // multiplies it by a step of the round's own speed — at
                    // the shipped 52.5 m/s and a 1/30 s tick that is 1.75 m —
                    // so a byte of 255 moves the seeded tracer 446 m, on an
                    // arena of radius 173 m. It decides no outcome (Critical
                    // Rule 3 leaves every outcome to the server); it draws
                    // nonsense, and it was the only field here with no bound
                    // at all.
                    //
                    // THE BOUND IS AN UPPER ONE AND NOT THE EXACT SET, which
                    // is a decision rather than laziness. The exact set is
                    // `RewindSplit.InputTicks(k) + 1` over every claimed depth
                    // the sanitizer admits, and writing that here would give
                    // the split rule a SECOND home — the codec would be
                    // restating a balance rule it has no business knowing, and
                    // the alternative (widening the split's own surface for
                    // one validator) is worse. The precedent is Ruling 165,
                    // which holds `InputCodec` to its own three-bit wire
                    // ceiling and deliberately keeps the arena's cap out of
                    // it: each side checks what it can check by itself. The
                    // price is named — a hostile value between the true
                    // maximum and this bound buys a few extra steps, i.e.
                    // meters rather than hundreds of them, inside the same
                    // class of error the tracer already tolerates.
                    //
                    // ZERO IS LEGAL AND MEANS "NOTHING IS KNOWN ABOUT THE
                    // BIRTH TICK", not "no steps were taken": it is what a
                    // round spawned through the simulation's own test seam
                    // carries, and what the field degenerates to for any
                    // sender that does not fill it. A receiver reads it as
                    // "seed at the header point", which is the behavior that
                    // predates this byte.
                    if (spawned && payload[8] > cfg.Arena.RewindCapTicks + 1)
                    { error = SnapshotBlockError.MalformedContent; return false; }
                    break;
                }
                case SnapshotEventKind.ProjectileEnded:
                {
                    // app-88jb Т31: BYTES 5-7 ARE UNCONSTRAINED, stated here
                    // rather than left to be inferred from their absence
                    // (the shape Т30 used for the reflection one case down).
                    // Every one of the 256 direction codes decodes to a
                    // heading by `Quantize.DirBack`, and every u16 is a legal
                    // entity code — pairing one with a live mob of THIS frame
                    // is the receiver's job (Р278), and finding no mob is an
                    // ordinary outcome rather than malformed content. Only
                    // the two enumerators below have a domain to be outside
                    // of.
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
                // Stage 3 Т30, narrowed by app-5o2q: no constraint either,
                // and for the same reason spelled out rather than left to
                // inference. Its three fields are a u16 id (every code is
                // legal, mapping it to a live round is the receiver's job), an
                // angle and a height, and `Quantize`'s decoders are CLAMPING
                // rather than validating — every one of the 256 codes decodes
                // to a heading, and every one to a height inside
                // `[0, MaxAimHeight]`, by construction. There is no byte here
                // a hostile sender could put out of domain.
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
                    // app-88jb Т32: the one field here that is READ RATHER
                    // THAN DECODED — a count crosses as itself, so there is no
                    // scale to undo. Its domain was settled in the validation
                    // pass above, which is why nothing is clamped here.
                    value.BirthSteps = payload[8];
                    break;
                }
                case SnapshotEventKind.ProjectileEnded:
                    value.Id = ReadU16(payload, 0);
                    value.EndKind = (ProjectileEndKind)payload[2];
                    value.Zone = (HitZone)payload[3];
                    value.Height = Quantize.UnitBack(payload[4], cfg.Hero.MaxAimHeight);
                    // app-88jb Т31. Both are decoded for EVERY ending rather
                    // than under a branch on `EndKind`: the codec's job is to
                    // hand back what the bytes say, and which fields MEAN
                    // something for which ending is the per-kind contract the
                    // two fields' own docs carry (`Dir`, `VictimId`). A branch
                    // here would be that contract written a second time, in
                    // the one place that cannot see who is asking.
                    value.Dir = Quantize.DirBack(payload[5]);
                    value.VictimId = ReadU16(payload, 6);
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
                    // Both scales are the SAME ONES the writer used — the
                    // normal back through `DirBack`, the height back through
                    // `UnitBack` against `Hero.MaxAimHeight` — the pairs
                    // `Quantize` guarantees idempotent (Р34). `Dir` carries
                    // the surface normal here, exactly as it does for
                    // `DashRicocheted` in the same switch; the two are the
                    // same fact about a contact, seen from the round's side
                    // and the actor's. NO POSITION IS ASSIGNED, and its
                    // absence is the decision (app-5o2q, spec §6k): the
                    // contact point rides the record header, so the payload
                    // has no point to decode.
                    value.Id = ReadU16(payload, 0);
                    value.Dir = Quantize.DirBack(payload[2]);
                    value.Height = Quantize.UnitBack(payload[3], cfg.Hero.MaxAimHeight);
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
        /// `ProjectileRicocheted_RoundTripsTheNormalAndTheContactHeight` for
        /// Т30's own.
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

        /// app-88jb Т32 (coordinator Ruling 301): the guard for an `int`
        /// argument that rides the wire as a single raw byte. It is about the
        /// CAST, not about the quantity — a value past 255 would fold silently
        /// into a small one, and 256 in particular into 0, which several of
        /// this catalog's fields spend as a sentinel. Whether a value that
        /// fits is also plausible is a per-field question and belongs to
        /// whichever writer or reader owns that field.
        static void RequireByteRange(int value, string argument)
        {
            if (value < 0 || value > byte.MaxValue)
                throw new System.ArgumentException(
                    $"SnapshotEvents: {argument} {value} does not fit the single byte it rides "
                    + $"(domain [0, {byte.MaxValue}]) — the cast would lose it silently.", argument);
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
