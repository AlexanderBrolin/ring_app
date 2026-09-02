using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// One wire event, turned into the `SimEvent` the whole Presentation
    /// fan-out already speaks (Stage 2 Task 44c, moved here whole by Task 44d).
    /// This is the INVERSE of the mapping `SnapshotAssembler` applies on the
    /// way out, and it is written by hand because there is no inverse anywhere
    /// else — the assembler's own switch is a one-way function.
    ///
    /// IT LIVES HERE, BESIDE `ClientEventQueue`, SO THAT IT CAN BE TESTED
    /// (the owner's decision of 2026-08-10). Task 44c wrote it inside the
    /// network backend, in an assembly the EditMode test assembly does not
    /// reference and will not: every one of the branches below — including the
    /// ones where the two vocabularies genuinely disagree — was unreachable by
    /// a unit test there. Nothing about the decode needs a `NetworkManager`, a
    /// scene or a connection: it is a pure function of a record, the block
    /// bytes it points into, the match's config and this client's own seat, so
    /// the class that could not be tested was simply in the wrong place. The
    /// side effects that DO need the runtime — confirming a ghost, telling the
    /// prediction core the local player died — stay with the caller, which is
    /// why they are not here.
    ///
    /// THE TWO ENUMERATIONS DO NOT LINE UP, and the shape of the mismatch is
    /// the assembler's rather than this file's: one `ProjectileFired` becomes
    /// `ProjectileSpawned` for whoever the round flies near and `ShotHeard` for
    /// whoever merely hears it, and four projectile endings collapse into one
    /// `ProjectileEnded` discriminated by `ProjectileEndKind`. Both halves of
    /// the first split map back to `ProjectileFired`, because a connection
    /// receives one or the other for a given shot and the audible variant's
    /// whole purpose is to be heard as a shot.
    ///
    /// WHAT THE WIRE CANNOT GIVE BACK. This list was NOT complete when it was
    /// written and is complete as of Stage 2 Task 44d fix-round 1: it was
    /// rebuilt by reading every emit site of the kinds below in the simulation
    /// and asking, field by field, whether the payload carries what was put
    /// there. Four fields with three live consumers in `Presentation` had been
    /// missing from it. A field a consumer reads as a fact and this side fills
    /// with a zero belongs here whether or not anything can be done about it.
    ///   * a `HitMob` ending CARRIES ITS VICTIM SINCE app-88jb Т31, and this
    ///     entry used to say the opposite. The payload's own `Id` is still the
    ///     ROUND's — it addresses the subscription and retires the tracer, and
    ///     it goes to `SecondaryEntityId`, the convention the simulation
    ///     itself uses for this kind — while the victim rides its own
    ///     `VictimId` field and lands in `EntityId`. So the per-mob hit flash
    ///     and the tilt axis DO find a view on a networked client now, which
    ///     is what the widening was for. What is still missing is the
    ///     VICTIM'S ARCHETYPE: the simulation puts `mob.Type` in this event
    ///     too, the wire has no room for it, and `MobType` therefore leaves
    ///     this class at the enum's zero — which is `Chaser`, a real
    ///     archetype rather than an absence. Nothing in `Presentation` reads
    ///     that field on this kind (all five readers take it on `MobDied`),
    ///     and the one consumer that needs the archetype —
    ///     `NetworkSimBackend`'s tilt integrator — asks `MobTypeMemory`
    ///     itself, in the moment the event comes due, precisely so that a
    ///     miss stays distinguishable from a chaser (Ruling 257). An earlier
    ///     wording of this entry also said the hit spark's height is "picked
    ///     from `MobType` against belts": that stopped being true at Т3, when
    ///     the zone-height table went (B2-I10) — the spark reads `e.Height`,
    ///     which Т31 put on the wire for this ending.
    ///   * a `HitPlayer` ending keeps the WORSE ZERO, and the trap is
    ///     unchanged: `SimEventKind.ProjectileHitPlayer`'s `EntityId` is the
    ///     victim's PLAYER SLOT, and slot 0 is a real seat. So `EntityId` on
    ///     this kind MUST NOT BE FILLED from the payload's `VictimId`: there
    ///     is no value a seat could ride under that would mean "nobody", so
    ///     the victim is simply not on the wire and this side leaves the
    ///     field unclaimed. `HitDir` and the contact height DO arrive since
    ///     Т31 — one call on the sending side serves both bodies — so what is
    ///     left missing here is `Amount` (the damage) and the victim itself.
    ///     `PlayerIndex` (the shooter) is not on the wire either, and it is
    ///     the one of them the receiver can still answer for: this class
    ///     leaves it at `ProjectileIds.NoOwner` and
    ///     `NetworkSimBackend.RestoreShooter` puts the round's owner back
    ///     from the tracer table, for this kind and for `HitMob` alike
    ///     (Ruling 256) — the same shape `RestoreMobType` has for `MobDied`.
    ///   * a `Blocked` ending carries no SURFACE NORMAL, and the zero it
    ///     leaves behind is read as a fact today. The simulation puts the
    ///     arena's normal in `HitDir` for a wall or an obstacle and exactly
    ///     `float2.zero` for the floor — the same payload again, and no field
    ///     in it for a direction. The persistent-props director tells the two
    ///     contacts apart by testing that field against exact zero (its own
    ///     comment calls it the simulation's gate rather than an epsilon
    ///     check), so on a networked client EVERY wall hit draws as a floor
    ///     hit: a decal flat on the ground at the foot of the wall and a spark
    ///     firing straight up. Nothing here can close it — the normal is not
    ///     on the wire at all — and putting it there is a protocol change, so
    ///     it is recorded rather than worked around.
    ///   * `MobDied` carries the mob, the killer and the zone, and drops
    ///     everything else the simulation put in it: `MobType`, `Amount` (the
    ///     killing blow's damage) and `HitDir` (the blow's direction). Of the
    ///     three, `MobType` is answerable from elsewhere on this side and IS
    ///     answered — not here: `NetworkSimBackend.RestoreMobType` fills it
    ///     from the Mobs block through `MobTypeMemory`, because that is state
    ///     the decoder does not see and must not be handed. The field
    ///     therefore leaves this class at the enum's zero by design; the
    ///     residue, a mob absent from both remembered frames, keeps it.
    ///     `HitDir` is the head gib's impulse on a headshot kill and stays
    ///     zero, so that gib drops instead of flying; `Amount` has no consumer
    ///     in `Presentation` at all today.
    ///   * `PlayerDied` carries the victim and the zone; the blow's `HitDir`
    ///     is not on the wire. No consumer reads it on this kind today, which
    ///     is the only reason it costs nothing.
    ///   * `StaminaDenied` carries no slot at all (it reaches its owner and
    ///     nobody else), so `localPlayerIndex` is the only honest answer, and
    ///     that is why the seat is a PARAMETER of this call.
    ///   * `PlayerSlideStarted` carries no direction, and `DashRicocheted`
    ///     carries the surface normal that the simulation puts in `HitDir`.
    ///   * a `ShotHeard` carries no direction either, so the fire angle
    ///     `SimEvent.Amount` means for `ProjectileFired` reads zero, and its
    ///     position has already been coarsened by the server.
    ///
    /// THAT COARSENED POSITION WAS DRAWN, ONCE — CLOSED BY bd app-p7t, IN
    /// `MuzzleFlashView`, NOT HERE. The muzzle-flash view and the
    /// persistent-props director are both subscribed to the event fan-out, and
    /// a `ShotHeard` reaches both as an ordinary `ProjectileFired` carrying
    /// `EntityId == 0` — the one fact this kind's payload can leave behind,
    /// because it has no room for a round id at all (the wire table above).
    /// `MuzzleFlashView.HandleEvent` now reads exactly that zero to withhold
    /// its burst: a mob's `ShotHeard` used to draw at `e.Pos`, the position
    /// the server coarsened precisely so it would not give the shooter away,
    /// and a visible player's `ShotHeard` used to burst from that player's own
    /// doll with an invented angle (the wire carries no direction for this
    /// kind either) — neither happens any more. The persistent-props
    /// director's casing was never actually exposed the way this paragraph
    /// used to say: `SpawnCasing` has required the shooter's own doll since
    /// Stage 2 Task 45b, so a `ShotHeard` from a shooter this client cannot
    /// see has dropped no shell since then — only a VISIBLE shooter's own
    /// `ShotHeard` ever produced one, at the honest position of their own
    /// ejection port, and that is unchanged by app-p7t. The sound this event
    /// exists for is still played by the audio director on `Kind` plus
    /// `Owner == Player`, with no condition on visibility or a doll — though
    /// `AudioDirector`'s own predicted-shot latch and its
    /// `MinSfxInterval`/`VoicesPerSfx` gates can still drop a given attempt
    /// for reasons that have nothing to do with this kind — at `e.Pos`, which
    /// is exactly what a SOUND is allowed to give away, and the whole reason
    /// this wire kind is sent at all. No consumer reads `EntityId` on this
    /// kind for anything but that zero.
    public static class ClientEventDecoder
    {
        /// Whether `TryDecode` has a `SimEvent` for this wire kind — the Р29
        /// frontier of THIS receiver, and the one question that tells an
        /// ordinary forward-compatibility skip from a refusal worth a log line.
        ///
        /// IT IS THE SWITCH'S OWN CASE LIST, ASKED WITHOUT A PAYLOAD, and it is
        /// written out rather than derived because both cheaper forms are
        /// wrong. `SnapshotEvents`' equivalent is private to that class and
        /// answers a DIFFERENT question — "can the catalog decode it" — which
        /// happens to coincide today and need not tomorrow. Testing the byte
        /// against the enum's last member would be that same question copied,
        /// and it would start calling a kind this project adds to the catalog
        /// but not to the switch a refusal, which is exactly the Р29 case. The
        /// two lists are kept in step by the switch's `default`, which refuses
        /// loudly: a kind admitted here and unmapped there is a defect of this
        /// file, and a defect should be loud.
        public static bool IsMapped(SnapshotEventKind kind)
        {
            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned:
                case SnapshotEventKind.ShotHeard:
                case SnapshotEventKind.ProjectileEnded:
                case SnapshotEventKind.MobSpawned:
                case SnapshotEventKind.MobDied:
                case SnapshotEventKind.PlayerDamaged:
                case SnapshotEventKind.PlayerDied:
                case SnapshotEventKind.PlayerDashed:
                case SnapshotEventKind.PlayerSlideStarted:
                case SnapshotEventKind.DashRicocheted:
                case SnapshotEventKind.StaminaDenied:
                case SnapshotEventKind.WaveStarted:
                case SnapshotEventKind.WaveCleared:
                // Stage 3 Т32 (bd app-gggs): the raid's own five. They have
                // ridden the wire since Т29 and this predicate did not know
                // them, so every one of them was walked past as a Р29 forward
                // -compatibility skip — a client that had been told the
                // Director woke up, that a raider walked out, or that the box
                // it is standing over is now empty, and did nothing with any
                // of it.
                case SnapshotEventKind.DirectorActivated:
                case SnapshotEventKind.DirectorDied:
                case SnapshotEventKind.PlayerExtracted:
                case SnapshotEventKind.PickupTaken:
                case SnapshotEventKind.ContainerEmptied:
                // Stage 3 Т30: the reflection. Without this line the record
                // would be walked past as an ordinary Р29 forward-compatibility
                // skip — the exact silence that lost the raid's own FIVE kinds
                // listed just above for two stages. (An earlier wording here
                // said THREE: that is the count in the ASSEMBLER's own
                // `default` arm, which measures a different set — the three
                // kinds that never reached the wire at all — while this
                // predicate's own doc and its Т32 comment above both say five.)
                // And what the client would lose is the CONTACT — and, since
                // app-88jb Т32 (Р420), the TURN as well. An earlier wording
                // here said "not a turning tracer: `TracerProjectiles` is a
                // closed form that flies straight on through the wall either
                // way"; that was true of Т30's tracer and stopped being true in
                // this very epic. Today the tracer cranks `ProjectileFlight`
                // against a cache, STANDS in the geometry it meets, and it is
                // this record that releases it onto the reflected line
                // (`TracerProjectiles.OnRicochet`, Ruling 290). Walking past
                // the record would therefore park the round at the wall until
                // its ending arrives, not merely mute a spark.
                case SnapshotEventKind.ProjectileRicocheted:
                    return true;
                default:
                    return false;
            }
        }

        /// Decodes one accepted record into the event Presentation shows.
        ///
        /// `originTick` IS THE ABSOLUTE TICK THE DEDUP HANDED BACK, never the
        /// frame's own and never re-derived here: `EventDedup.TryAcceptEvent`
        /// performs the one subtraction the whole scheme rests on and returns
        /// the result, and the value has to match exactly or the event surfaces
        /// on the wrong frame. `record.Pos` is the event's position as THIS
        /// connection was told it — per-connection, because the server
        /// coarsens what an observer may not see precisely.
        ///
        /// `false` MEANS NO EVENT, AND `refusal` SAYS WHETHER ANYBODY SHOULD
        /// CARE. `SnapshotBlockError.None` beside a `false` is the Р29 skip —
        /// a kind this build has never heard of, which is ordinary traffic from
        /// a newer server and must not be logged as hostile. Anything else is a
        /// real refusal. That distinction cannot be left to
        /// `SnapshotEvents.TryReadPayload`, which folds "unknown kind" into the
        /// same `MalformedContent` it gives a known kind with wrong bytes,
        /// which is why `IsMapped` is asked before the payload is looked at.
        ///
        /// NOTHING THROWS, ON ANY BYTE SEQUENCE (Р82). That includes the
        /// offset/length pair: `TryReadEventsBlock` validates it against the
        /// block it produced the record from, but this method is public and its
        /// records need not have come from there — the same precondition
        /// `SnapshotBlocks` enforces rather than assumes for its own u16 offset
        /// limit, and for the same reason. The caller of record runs inside
        /// FishNet's batched parsing loop, where a throw abandons every message
        /// batched behind it in the same datagram.
        ///
        /// `payload` IS HANDED BACK FOR THE CALLER'S OWN ROUTING, not as a
        /// second copy of the event: the ghost registry is keyed by the ROUND's
        /// id, and which field of the `SimEvent` that id landed in depends on
        /// the ending. Reading it out of the decoded payload keeps the caller
        /// from having to know.
        public static bool TryDecode(uint originTick, in SnapshotBlocks.EventRecord record,
            System.ReadOnlySpan<byte> blockPayload, in SimConfig cfg, byte localPlayerIndex,
            out SimEvent e, out SnapshotEventPayload payload, out SnapshotBlockError refusal)
        {
            e = default;
            payload = default;
            refusal = SnapshotBlockError.None;

            var kind = (SnapshotEventKind)record.Kind;
            if (!IsMapped(kind)) return false;

            if (record.PayloadOffset + record.PayloadLength > blockPayload.Length)
            {
                refusal = SnapshotBlockError.MalformedLength;
                return false;
            }

            System.ReadOnlySpan<byte> slice = blockPayload.Slice(record.PayloadOffset,
                record.PayloadLength);
            if (!SnapshotEvents.TryReadPayload(kind, slice, in cfg,
                    out SnapshotEventPayload p, out SnapshotBlockError error))
            {
                refusal = error;
                return false;
            }

            e.Tick = (int)originTick;
            e.Pos = record.Pos;
            e.PlayerIndex = ProjectileIds.NoOwner;
            // app-88jb Т8: the same pre-fill for the same reason as the line
            // above it. `default(byte)` is 0 and slot 0 is a real seat, so a
            // kind that names no shooter has to SAY so — `SimEvent`'s own doc
            // promises `ProjectileIds.NoOwner` here for every kind but
            // PlayerDamaged, and on this side nothing else can keep that
            // promise. Only the PlayerDamaged branch overwrites it.
            e.AttackerIndex = ProjectileIds.NoOwner;

            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned:
                    e.Kind = SimEventKind.ProjectileFired;
                    e.EntityId = p.Id;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Owner = p.PlayerIndex == ProjectileIds.NoOwner
                        ? ProjectileOwner.Mob : ProjectileOwner.Player;
                    // `Amount` is the shot's sim-plane velocity angle for this
                    // kind (the field's own doc); the wire carries the unit
                    // direction the angle is of.
                    e.Amount = math.atan2(p.Dir.y, p.Dir.x);
                    // `BirthSteps` IS DELIBERATELY NOT COPIED, and the silence
                    // is what needed fixing rather than the code (app-88jb Т32
                    // fix-round). The record carries the count, but the only
                    // consumer is the tracer table, and it is fed from the
                    // PAYLOAD directly — `NetworkSimBackend.RouteToTracers`
                    // hands `p.BirthSteps` to `TracerProjectiles.TrySpawn` off
                    // the same record, on the same line. Copying it here as
                    // well would be a second home for one number with no reader
                    // (rule 2). What it does mean is that a client-side
                    // `SimEvent.ProjectileFired` always carries zero, i.e.
                    // "nothing is known about the birth tick" — see that
                    // field's own doc, which now says so from the other side.
                    break;

                case SnapshotEventKind.ShotHeard:
                    e.Kind = SimEventKind.ProjectileFired;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Owner = p.PlayerIndex == ProjectileIds.NoOwner
                        ? ProjectileOwner.Mob : ProjectileOwner.Player;
                    break;

                case SnapshotEventKind.ProjectileEnded:
                    switch (p.EndKind)
                    {
                        case ProjectileEndKind.Blocked:
                            e.Kind = SimEventKind.ProjectileBlocked;
                            e.EntityId = p.Id;
                            // `Height` carries the contact height for this
                            // kind (app-88jb Т3) — the same field the
                            // sending side wrote it into (SnapshotAssembler's
                            // own ProjectileBlocked branch).
                            e.Height = p.Height;
                            break;
                        case ProjectileEndKind.Expired:
                            e.Kind = SimEventKind.ProjectileExpired;
                            e.EntityId = p.Id;
                            break;
                        case ProjectileEndKind.HitMob:
                            e.Kind = SimEventKind.ProjectileHit;
                            e.SecondaryEntityId = p.Id;
                            e.Zone = p.Zone;
                            // app-88jb Т31. The VICTIM, in the field this sim
                            // kind puts it in — `SimEventKind.ProjectileHit`'s
                            // own convention, the same one the simulation
                            // fills offline. `ViewRegistry.HandleEvent` finds
                            // the struck mob's view by it (and hands the
                            // visual its tilt axis), `GameFeelDirector` finds
                            // the same view for the hit flash, and
                            // `NetworkSimBackend`'s tilt integrator asks the
                            // archetype memory about it.
                            e.EntityId = p.VictimId;
                            // The axis half of that tilt: a signed scalar has
                            // no direction of its own (`MobState.Tilt`).
                            e.HitDir = p.Dir;
                            // And where the round entered the body —
                            // `PersistentPropsDirector.SpawnHitSpark` places
                            // the spark at exactly this height.
                            e.Height = p.Height;
                            break;
                        case ProjectileEndKind.HitPlayer:
                            e.Kind = SimEventKind.ProjectileHitPlayer;
                            e.SecondaryEntityId = p.Id;
                            e.Zone = p.Zone;
                            // app-88jb Т31: the same two facts as the mob
                            // ending above, from the same one call on the
                            // sending side — the blow's direction and the
                            // height it landed at, which
                            // `PersistentPropsDirector.SpawnPlayerHitSpark`
                            // reads.
                            e.HitDir = p.Dir;
                            e.Height = p.Height;
                            // ⚠ AND `EntityId` IS DELIBERATELY NOT TOUCHED,
                            // unlike one case up. It is the victim's player
                            // SLOT for this kind, seat 0 is a real seat, and
                            // the payload therefore carries no victim at all
                            // (Ruling 243) — `p.VictimId` is a mob's id or a
                            // zero meaning "no mob", and assigning it here
                            // would name seat 0 as the victim of every hit on
                            // a collector.
                            break;
                        default:
                            // Unreachable through `TryReadPayload`, which
                            // refuses `None` and anything past `HitPlayer`
                            // before this method ever sees `p` — so reaching
                            // it means the catalog and this switch disagree
                            // about the ending vocabulary, which is a defect
                            // of this file and IS worth the line.
                            refusal = SnapshotBlockError.MalformedContent;
                            return false;
                    }
                    break;

                case SnapshotEventKind.MobSpawned:
                    e.Kind = SimEventKind.MobSpawned;
                    e.EntityId = p.Id;
                    e.MobType = p.MobType;
                    break;

                case SnapshotEventKind.MobDied:
                    e.Kind = SimEventKind.MobDied;
                    e.EntityId = p.Id;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Zone = p.Zone;
                    break;

                case SnapshotEventKind.PlayerDamaged:
                    e.Kind = SimEventKind.PlayerDamaged;
                    // VICTIM in both fields, which is this kind's own
                    // convention on the simulation side (`SimEvent.PlayerIndex`
                    // mirrors `EntityId` for the two player-victim kinds).
                    e.EntityId = p.PlayerIndex;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Zone = p.Zone;
                    e.Amount = p.Amount;
                    e.HitDir = p.Dir;
                    // app-88jb Т8. COPIED, not left to the struct's defaults,
                    // and for the same reason the two fields above are both
                    // filled: `default(byte)` is 0 and slot 0 is a real seat,
                    // so an unset `AttackerIndex` would tell every client that
                    // the collector in seat 0 fired the blow. The speed and the
                    // height have no such trap, but they have the same source
                    // and belong in the same place — the client cannot derive
                    // either one, which is why deviation 2 put them on the wire.
                    e.AttackerIndex = p.AttackerIndex;
                    e.ImpactSpeed = p.ImpactSpeed;
                    e.Height = p.Height;
                    break;

                case SnapshotEventKind.PlayerDied:
                    e.Kind = SimEventKind.PlayerDied;
                    e.EntityId = p.PlayerIndex;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Zone = p.Zone;
                    break;

                case SnapshotEventKind.PlayerDashed:
                    e.Kind = SimEventKind.PlayerDashed;
                    e.PlayerIndex = p.PlayerIndex;
                    break;

                case SnapshotEventKind.PlayerSlideStarted:
                    e.Kind = SimEventKind.PlayerSlideStarted;
                    e.PlayerIndex = p.PlayerIndex;
                    break;

                case SnapshotEventKind.DashRicocheted:
                    e.Kind = SimEventKind.DashRicocheted;
                    e.PlayerIndex = p.PlayerIndex;
                    e.HitDir = p.Dir;
                    break;

                case SnapshotEventKind.StaminaDenied:
                    e.Kind = SimEventKind.StaminaDenied;
                    e.PlayerIndex = localPlayerIndex;
                    e.Amount = p.Amount;
                    break;

                case SnapshotEventKind.WaveStarted:
                    e.Kind = SimEventKind.WaveStarted;
                    // `EntityId` is the wave index for these two kinds
                    // (`WaveSystem`'s own emit sites).
                    e.EntityId = p.WaveIndex;
                    break;

                case SnapshotEventKind.WaveCleared:
                    e.Kind = SimEventKind.WaveCleared;
                    e.EntityId = p.WaveIndex;
                    break;

                // Stage 3 Т32 (bd app-gggs). Field conventions are the
                // SENDER's, taken from `SimEventKind`'s own per-kind PAYLOAD
                // paragraphs — not invented here.
                case SnapshotEventKind.DirectorActivated:
                    // Nothing but the kind: this one rides the All channel,
                    // which carries no position (Р28), and the moment IS the
                    // message.
                    e.Kind = SimEventKind.DirectorActivated;
                    break;

                case SnapshotEventKind.DirectorDied:
                    e.Kind = SimEventKind.DirectorDied;
                    break;

                case SnapshotEventKind.PlayerExtracted:
                    e.Kind = SimEventKind.PlayerExtracted;
                    // VICTIM in both fields — the third kind to take that
                    // convention (`SimEvent.PlayerIndex`'s master list), which
                    // is what lets `EventRelevance.VisibleSubjectId` resolve
                    // all three through one `ForPlayer(ev.PlayerIndex)`.
                    e.EntityId = p.PlayerIndex;
                    e.PlayerIndex = p.PlayerIndex;
                    break;

                case SnapshotEventKind.PickupTaken:
                    e.Kind = SimEventKind.PickupTaken;
                    e.EntityId = p.Id;
                    // THE COLLECTOR IS WHOEVER RECEIVED THIS, and the wire
                    // deliberately does not repeat it: the kind rides the
                    // Owner channel, so the server sent this record to exactly
                    // one connection — the collector's. Same inference
                    // `StaminaDenied` above already makes, and the same reason.
                    e.PlayerIndex = localPlayerIndex;
                    break;

                case SnapshotEventKind.ContainerEmptied:
                    e.Kind = SimEventKind.ContainerEmptied;
                    e.EntityId = p.Id;
                    // No player slot: this one is delivered by VISIBILITY
                    // (R-236 — the assembler decides it against
                    // `ContainersCurrent`), so the receiver is a witness, not
                    // a subject, and filling their slot in would be a fiction
                    // a consumer could act on.
                    break;

                case SnapshotEventKind.ProjectileRicocheted:
                    e.Kind = SimEventKind.ProjectileRicocheted;
                    // The ROUND's own id, the convention of the Blocked and
                    // Expired endings this record sits between in a flight's
                    // life — a reflection has no victim to spend `EntityId` on.
                    e.EntityId = p.Id;
                    // The surface normal, which is what `DashRicocheted` above
                    // puts in the same field and what
                    // `PersistentPropsDirector.HandleRicocheted` reads to aim
                    // the spark.
                    e.HitDir = p.Dir;
                    // The CONTACT HEIGHT (app-5o2q), which
                    // `PersistentPropsDirector.HandleRicocheted` lifts the
                    // spark by — left unassigned it is zero, and a mirrored
                    // round then sparks on the floor while an absorbed one
                    // sparks at the hit, on the same wall.
                    e.Height = p.Height;
                    // `Pos` COMES FROM THE RECORD HEADER, which is now the
                    // only place it exists: `e.Pos = record.Pos` above has
                    // already filled it for every kind, and the assembler put
                    // this very contact there. The payload used to carry a
                    // second copy this branch deliberately did not read — the
                    // owner's answer to that redundancy was to take the four
                    // bytes off the wire (spec §6k), so the argument about one
                    // number having two sources is now history rather than a
                    // live rule, and the header is the single home.
                    break;

                default:
                    // `IsMapped` above admitted a kind this switch does not
                    // map: the two are one list seen twice, and this is where
                    // they are caught disagreeing. Р29 skips are not here —
                    // they never got past the predicate.
                    refusal = SnapshotBlockError.MalformedContent;
                    return false;
            }

            payload = p;
            return true;
        }
    }
}
