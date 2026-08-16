using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// The client's own copy of the rounds the SERVER has in flight, rebuilt
    /// from `ProjectileSpawned` and retired by `ProjectileEnded` (bd `app-s0u`,
    /// owner decision variant "б"). Without it a networked client sees a muzzle
    /// flash, hears the shot and watches the hit, but never sees the bullet:
    /// the snapshot carries no projectile block at all (`SnapshotBlockKind` has
    /// five kinds and none of them is Projectiles), while `RenderSnapshot` has
    /// the field and `ViewRegistry.SyncProjectiles` honestly draws whatever is
    /// in it.
    ///
    /// NOT A SECOND FLIGHT MODEL, AND THE DISTINCTION IS THE WHOLE DESIGN.
    /// `NetworkSimBackend`'s own doc warned that reconstructing tracers here
    /// would put a second copy of `ProjectileSystem`'s physics in the layer
    /// least able to check it. What makes that warning survivable is that the
    /// flight being reproduced is a STRAIGHT LINE and nothing else:
    /// `SimulationWorld.SpawnProjectile` sets `Vel`/`VelZ` once and no system
    /// ever changes them again — `ProjectileSystem.Update` advances
    /// `Pos += Vel * dt` and `Height += VelZ * dt` (:303-305), and there is no
    /// gravity, no drag and no ricochet anywhere in the project.
    /// `TracerProjectilesTests` pins that against a REAL round fired through
    /// `ProjectileSystem` rather than against algebra restated here.
    ///
    /// POSITION IS A FUNCTION OF THE RENDER TICK, NOT AN ACCUMULATOR, and that
    /// is the one design decision worth arguing. A stepped integrator has to be
    /// driven exactly once per tick forever: a frame that skips the call leaves
    /// every round permanently short, a frame that makes it twice leaves them
    /// permanently long, and neither error can ever be recovered because the
    /// only record of where the round should be is the very number that drifted.
    /// Here the answer at tick T is `spawn + vel * dt * (T - spawnTick)` — it
    /// depends on nothing but the event and the clock, so a dropped frame, a
    /// hitstop freeze, a catch-up flush and a clock snap all produce the right
    /// picture on the next frame with no reconciliation of any kind. Straight
    /// flight is what makes the closed form available at all.
    ///
    /// EVERYTHING IS IN THE RENDER CLOCK'S TIME, NOT ARRIVAL TIME. The events
    /// that spawn and end these rounds are shown from `ClientEventQueue` when
    /// the render clock reaches their tick — `InterpBufferTicks` behind the
    /// newest frame received — so a tracer keyed to ARRIVAL would appear
    /// several ticks before the muzzle flash that fired it and vanish before
    /// the hit that ended it. `spawnTick`/`endTick` are the event ticks
    /// themselves, which is what keeps the bullet, the flash and the impact one
    /// picture.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO: decide any outcome (CR 3). It never
    /// tests a hit, never consults a barrier and never ends a round on its own
    /// `Ttl` — a client that retired its own tracer on a locally computed
    /// lifetime would be deciding where a bullet stopped. Every ending arrives
    /// from the server as `ProjectileEnded`.
    ///
    /// FIXED TABLE, NO ALLOCATION, REFUSALS RATHER THAN THROWS. The WRITE half
    /// (`TrySpawn`/`Retire`) runs from the snapshot receive path, inside
    /// FishNet's batched parsing loop, where an exception abandons every
    /// message behind it in the same datagram (Р82/195); the read half
    /// (`WriteInto`/`Prune`) runs from the render frame. So a full table, an
    /// unknown id and an undersized destination are all VALUES. The table is scanned linearly on
    /// purpose — `MaxProjectiles` is 384 for the whole arena, of which one
    /// client sees only what `SightRadius` admits, and a dictionary would buy
    /// nothing but garbage on a path that runs every frame.
    public sealed class TracerProjectiles
    {
        /// One reconstructed round: what the wire said, plus the two ticks that
        /// bound its life. Nothing here changes after `TrySpawn` except
        /// `EndTick`, which is why the position can be a pure function of the
        /// clock.
        struct Track
        {
            public int Id;
            public int SpawnTick;
            public int EndTick;
            public float2 SpawnPos;
            public float SpawnHeight;
            public float2 Vel;
            public float VelZ;
            public float Radius;
            public float Ttl;
            public ProjectileOwner Owner;
            public byte OwnerIndex;
        }

        /// `EndTick` while the server has not ended the round. `int.MaxValue`
        /// rather than a `bool` beside it: the write path compares one number
        /// either way, and two fields that must agree are two fields that can
        /// disagree.
        const int NoEnd = int.MaxValue;

        /// How long past its own lifetime a round is kept when no
        /// `ProjectileEnded` ever arrives for it. NOT AN OUTCOME AND NOT A
        /// PREDICTION — the round is already over by every account, this is
        /// what stops a LOST end from burning a table slot for the rest of the
        /// match. The same defense `GhostProjectiles` was given for the same
        /// reason (`maxTrackTicks`), and it is needed here for three measured
        /// reasons: an end's redundancy is only `NetConfig.EventRedundancyTicks`
        /// frames, the assembler closes a spawn's subscription even when the
        /// end record did not fit the frame's budget, and a reorder can land an
        /// end before the spawn it belongs to. A stuck round would otherwise
        /// fly through walls forever and, worse, hold a `u16` code that a later
        /// round needs.
        const int LostEndSlackTicks = 8;

        readonly Track[] _live;
        int _count;

        public TracerProjectiles(int capacity)
        {
            if (capacity < 0) capacity = 0;
            _live = new Track[capacity];
        }

        /// How many rounds are tracked — including any whose spawn tick the
        /// render clock has not reached yet, which is why this is not the same
        /// number `WriteInto` returns.
        public int Count => _count;

        /// How many can be tracked at once.
        public int Capacity => _live.Length;

        /// A round the server says exists (`ProjectileSpawned` at `spawnTick`).
        /// `pos`/`height` come from the event envelope, `dir`/`horizSpeed`/
        /// `velZ` from the event's own three fields, and `radius`/`ttl` from the
        /// CONFIG — the wire carries neither, and both depend on WHO fired (a
        /// hero's `Weapon` against a Gunner mob's own numbers), which is the
        /// caller's business and not this class's.
        ///
        /// Refuses a duplicate id rather than tracking a round twice. The
        /// authority's own ids are unique for the whole match
        /// (`SimulationWorld.SpawnProjectile` mints them off a counter), but
        /// the WIRE truncates them to `u16` (`SnapshotEventPayload.Id`'s own
        /// doc), so two rounds exactly 65536 apart arrive under one code — and
        /// the receive path's dedup is about RECORDS, not about ids. The
        /// refusal keeps the older round drawn correctly instead of teleporting
        /// it onto the newer one's line.
        public bool TrySpawn(int serverId, int spawnTick, float2 pos, float height, float2 dir,
            float horizSpeed, float velZ, float radius, float ttl,
            ProjectileOwner owner = ProjectileOwner.Player,
            byte ownerIndex = ProjectileIds.NoOwner)
        {
            if (_count >= _live.Length) return false;
            if (IndexOf(serverId) >= 0) return false;

            _live[_count] = new Track
            {
                Id = serverId,
                Owner = owner,
                OwnerIndex = ownerIndex,
                SpawnTick = spawnTick,
                EndTick = NoEnd,
                SpawnPos = pos,
                SpawnHeight = height,
                Vel = dir * horizSpeed,
                VelZ = velZ,
                Radius = radius,
                Ttl = ttl
            };
            _count++;
            return true;
        }

        /// The server ended this round (`ProjectileEnded`, any `EndKind`) on
        /// `endTick`. The round keeps flying until the render clock REACHES that
        /// tick, so the tracer disappears together with the impact that ended
        /// it rather than the moment the datagram arrived.
        ///
        /// Answers whether the round was tracked at all, and an unknown id is
        /// ordinary traffic rather than an error. It is NOT, however, the case
        /// that ends arrive for rounds this client never saw fired — the
        /// assembler sends an end only to the connections subscribed by the
        /// spawn (`SnapshotAssembler`'s own subscription rule). The reachable
        /// sources are this class's own refusals (a full table, a truncated id
        /// already in use) and a reorder that lands the end first.
        public bool Retire(int serverId, int endTick)
        {
            int index = IndexOf(serverId);
            if (index < 0) return false;

            // An end already recorded stands: the first one is the server's own
            // answer, and a repeat cannot move it earlier or later.
            if (_live[index].EndTick == NoEnd) _live[index].EndTick = endTick;
            return true;
        }

        /// Fills `destination` with every round visible at `renderTick` and
        /// answers how many were written; rounds whose end the clock has passed
        /// are dropped from the table on the way through, which is the only
        /// thing that ever removes one.
        ///
        /// A destination shorter than the table is filled to its own length:
        /// the caller's array is `RenderSnapshot.Projectiles`, sized by
        /// `ArenaConfig.MaxProjectiles`, and writing past it would be the one
        /// failure this class could not report as a value.
        public int WriteInto(ProjectileState[] destination, int renderTick)
        {
            if (destination == null) return 0;

            int written = 0;
            for (int i = 0; i < _count && written < destination.Length; i++)
            {
                ref Track t = ref _live[i];
                if (renderTick < t.SpawnTick) continue;    // not born yet on screen
                if (renderTick >= t.EndTick) continue;     // the impact has been shown

                destination[written++] = StateAt(in t, renderTick);
            }
            return written;
        }

        /// Drops rounds the render clock has carried past their ending. Split
        /// from `WriteInto` on purpose: the caller writes BOTH halves of the
        /// render pair (`renderTick` and `renderTick + 1`), and a write that
        /// also pruned would make the result depend on which half was asked
        /// for first — a round ending on the newer half would vanish from the
        /// older one too, one frame early, depending only on call order.
        /// `WriteInto` therefore never mutates, and this runs once per frame
        /// with the OLDER tick.
        public void Prune(int renderTick)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                if (renderTick < _live[i].EndTick && !OutlivedItsEnd(in _live[i], renderTick))
                    continue;

                _count--;
                // Swap-remove: order carries no meaning (the renderer keys off
                // `Id`), and removal stays O(1) on a path that runs per frame.
                _live[i] = _live[_count];
                _live[_count] = default;
            }
        }

        /// A new match (`ClientMatchReset.ResetForEpoch`): rounds of the match
        /// that ended must not outlive it, and the new match mints their ids
        /// again from scratch.
        public void Reset()
        {
            for (int i = 0; i < _count; i++) _live[i] = default;
            _count = 0;
        }

        /// The closed form, in the authority's own terms. `PrevPos`/`PrevHeight`
        /// are the SAME function one tick earlier — never a stored previous
        /// value — so they stay correct across a skipped frame, and they are
        /// clamped at the spawn tick so a round's first frame interpolates from
        /// its muzzle rather than from behind it.
        static ProjectileState StateAt(in Track t, int renderTick)
        {
            float dt = SimulationWorld.TickDt;
            int age = renderTick - t.SpawnTick;
            int prevAge = age > 0 ? age - 1 : 0;

            return new ProjectileState
            {
                Id = t.Id,
                Owner = t.Owner,
                OwnerIndex = t.OwnerIndex,
                Pos = t.SpawnPos + t.Vel * (dt * age),
                PrevPos = t.SpawnPos + t.Vel * (dt * prevAge),
                Vel = t.Vel,
                Height = t.SpawnHeight + t.VelZ * (dt * age),
                PrevHeight = t.SpawnHeight + t.VelZ * (dt * prevAge),
                VelZ = t.VelZ,
                Radius = t.Radius,
                Ttl = t.Ttl - dt * age
            };
        }

        /// Whether a round with no end from the server has outlived every
        /// account of itself (see `LostEndSlackTicks`).
        static bool OutlivedItsEnd(in Track t, int renderTick)
        {
            if (t.EndTick != NoEnd) return false;

            int lifetimeTicks = (int)math.ceil(t.Ttl / SimulationWorld.TickDt);
            return renderTick - t.SpawnTick > lifetimeTicks + LostEndSlackTicks;
        }

        int IndexOf(int serverId)
        {
            for (int i = 0; i < _count; i++)
                if (_live[i].Id == serverId) return i;
            return -1;
        }
    }
}
