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
    ///   * a `HitMob` ending carries the ROUND's id and not the victim's, so
    ///     `EntityId` — the mob's id for `SimEventKind.ProjectileHit` — stays
    ///     0. 0 IS SAFE THERE and only there: entity ids are minted from a
    ///     counter that starts at 1, so no mob can ever be 0. The round's id
    ///     goes to `SecondaryEntityId`, which is the convention the simulation
    ///     itself uses for this kind. The cost is that a per-mob hit flash has
    ///     no view to look up on a networked client; the round's own end still
    ///     retires its tracer. THE VICTIM'S ARCHETYPE GOES WITH ITS IDENTITY:
    ///     the simulation puts `mob.Type` in this event too, so `MobType`
    ///     reads the enum's zero — which is `Chaser`, a real archetype — and
    ///     the hit spark's height is picked from it against belts that differ
    ///     by a factor of two between the two archetypes. It cannot be
    ///     restored the way `MobDied`'s is below: what is missing here is not
    ///     the type but the identity, and no table can be asked about a mob
    ///     nobody named.
    ///   * a `HitPlayer` ending is the SAME shortfall with a WORSE zero.
    ///     `SimEventKind.ProjectileHitPlayer`'s `EntityId` is the victim's
    ///     PLAYER SLOT, and slot 0 is a real seat. So `EntityId` on this kind
    ///     MUST NOT BE READ on a networked client: it is not "no victim", it
    ///     is "seat 0", and the victim is simply not on the wire. `Amount`
    ///     (the damage), `HitDir` (the round's travel direction) and
    ///     `PlayerIndex` (the shooter) are missing for the same reason — the
    ///     whole payload of a projectile ending is the round's id, the ending
    ///     kind, the zone and the contact height.
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
                            // `Amount` carries the contact HEIGHT for this kind
                            // — the same field the sending side read it out of.
                            e.Amount = p.Height;
                            break;
                        case ProjectileEndKind.Expired:
                            e.Kind = SimEventKind.ProjectileExpired;
                            e.EntityId = p.Id;
                            break;
                        case ProjectileEndKind.HitMob:
                            e.Kind = SimEventKind.ProjectileHit;
                            e.SecondaryEntityId = p.Id;
                            e.Zone = p.Zone;
                            break;
                        case ProjectileEndKind.HitPlayer:
                            e.Kind = SimEventKind.ProjectileHitPlayer;
                            e.SecondaryEntityId = p.Id;
                            e.Zone = p.Zone;
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
