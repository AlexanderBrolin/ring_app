using Ring.Simulation.Combat;
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
    /// least able to check it. Until app-88jb Т32 what made that warning
    /// survivable was that the flight being reproduced was A STRAIGHT LINE and
    /// nothing else. Т19 ended that: a round now reflects off static geometry,
    /// and a straight line drawn through a wall is not a simplification but a
    /// wrong picture. ⇒ THE ANSWER IS NOT A SECOND MODEL BUT THE SAME ONE.
    /// This class cranks `ProjectileFlight` — the very members
    /// `ProjectileSystem` itself calls, in the one public home that exists so
    /// both sides can share it (that class's own doc names this caller three
    /// times) — and it is worth saying WHICH MEMBER FROM WHERE, because an
    /// earlier wording put all three inside `StepTo` and one of them is not
    /// there: `StepTo` calls `Step` and `BarrierStops` (the frame half, walking
    /// the cache); `OnRicochet` calls `TryRicochet` (the write half, answering
    /// the server's own word). The split is the design rather than an accident
    /// — the frame half never reflects anything, which is CR 3 in one line
    /// (Р420). Nothing about flight is restated here; what IS here is the
    /// bookkeeping of a CACHE (which tick it stands on, when it is thrown
    /// away, how many steps one frame may spend) and nothing else.
    /// `TracerProjectilesTests` still pins the result against a REAL round
    /// fired through `ProjectileSystem` rather than against algebra restated
    /// here, and `TracerFlightTests` pins the new half the same way.
    ///
    /// POSITION IS A FUNCTION OF THE CACHE AND THE CLOCK, NOT AN ACCUMULATOR,
    /// and the argument the old wording made is ANSWERED here rather than
    /// deleted — it was a real argument and the next reader would only make it
    /// again. It ran: a stepped integrator has to be driven exactly once per
    /// tick forever, a frame that skips the call leaves every round permanently
    /// short, a frame that makes it twice leaves them permanently long, and
    /// NEITHER ERROR CAN EVER BE RECOVERED, because the only record of where
    /// the round should be is the very number that drifted.
    /// Every clause of that is still true of a bare accumulator. What this
    /// class keeps is not one: the cache carries the TICK IT STANDS ON, so
    ///   * a skipped frame is not lost — `StepTo` walks the cache to whatever
    ///     tick it is asked for, however many ticks that is (bounded by the
    ///     budget below, which only DELAYS the round, never shortens it);
    ///   * a doubled call cannot advance anything twice — the second one finds
    ///     `CacheTick` already at the target and steps nothing;
    ///   * a TARGET TICK that moves BACKWARDS throws the cache away and
    ///     re-runs from the birth state, because an integrator that meets a
    ///     wall cannot be stepped backwards through the reflection it made
    ///     (what actually moves it back is named at `StepTo`'s step 1 — it is
    ///     NOT the render clock);
    ///   * and the drift the old argument feared has an authority to be
    ///     corrected against, which a bare accumulator did not have: every
    ///     `ProjectileRicocheted` re-seats the cache on the server's own
    ///     contact point and normal, and every `ProjectileEnded` retires it.
    /// The one property genuinely given up is the old one's headline — the
    /// answer at tick T no longer depends on nothing but the event and the
    /// clock — and `WriteInto` keeps as much of it as survives: it is still a
    /// CLOSED FORM, still mutates nothing, still answers any tick asked of it,
    /// only now measured from the cache rather than from the spawn (coordinator
    /// Ruling 287).
    ///
    /// EVERYTHING IS IN THE PREDICTED CLOCK'S TIME, NOT ARRIVAL TIME, and since
    /// Т32 not the render clock's either (coordinator Rulings 285/286). The
    /// events that spawn and end these rounds are shown from `ClientEventQueue`
    /// when the render clock reaches their tick — `InterpBufferTicks` behind
    /// the newest frame received — so a tracer keyed to ARRIVAL would appear
    /// several ticks before the muzzle flash that fired it and vanish before
    /// the hit that ended it. `spawnTick`/`endTick` are still the event ticks
    /// themselves; what moved is the tick the CALLER asks about, which is now
    /// `renderTick + the latched rewind depth` — the same clock this client's
    /// OWN BODY is predicted on, which is the whole point of the task (Р408):
    /// the bullet is drawn where it really is, not where it was 180 ms ago.
    /// ⚠ THE PRICE IS NAMED AND IT IS REAL. The bullet now runs AHEAD of the
    /// bodies it flies past, which are interpolated on the render clock, so the
    /// spark of a hit on a mob lags the bullet's passage by that same depth —
    /// about five ticks, 170 ms, at 80 ms RTT. The flash, the bullet and the
    /// impact are no longer one picture; they are one picture per clock. That
    /// is the mirror of the already-accepted edge "the bullet is ahead of the
    /// barrel" (spec §3.8), it was taken deliberately, and it belongs to the
    /// В3 expectations rather than to a future reader's list of bugs.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO: decide any outcome (CR 3). It never
    /// tests a hit and never ends a round on its own `Ttl` — a client that
    /// retired its own tracer on a locally computed lifetime would be deciding
    /// where a bullet stopped. Every ending arrives from the server as
    /// `ProjectileEnded`.
    /// ⚠ SINCE Т32 IT DOES CONSULT THE ARENA'S STATIC GEOMETRY, and that is
    /// not a breach of the rule but its enforcement (coordinator Ruling 289).
    /// It asks the geometry in order to STOP DRAWING, never in order to decide
    /// anything: seeing an interior barrier or the ring boundary inside the
    /// step it is about to take, the round stands still in the contact and
    /// waits for the server to say what happened. It does not reflect on its
    /// own (Р420: the direction error after a self-computed reflection reaches
    /// 14.1° against 0.703° in a straight line — drawing that is drawing a
    /// wrong line, while standing still draws an incomplete but true one), it
    /// does not resolve a hit, and it never asks about the FLOOR at all: a
    /// floor crossing is the END of a round's life, and endings are the
    /// server's alone.
    ///
    /// FIXED TABLE, NO ALLOCATION, REFUSALS RATHER THAN THROWS. The WRITE half
    /// (`TrySpawn`/`Retire`/`OnRicochet` — the three that are fed by events)
    /// runs from the snapshot receive path, inside FishNet's batched parsing
    /// loop, where an exception abandons every message behind it in the same
    /// datagram (Р82/195); the FRAME half (`StepTo`/`WriteInto`/`Prune`) runs
    /// from the render frame. So a full table, an unknown id and an undersized
    /// destination are all VALUES. The table is scanned linearly on purpose,
    /// and the argument is `_count` RATHER THAN THE CEILING — which is the
    /// correction rather than the point. An earlier wording said
    /// "`MaxProjectiles` is 384 for the whole arena"; the shipped number is
    /// `ArenaConfig.MaxProjectiles` = 4096 — the ceiling was raised with the
    /// mob cap and this line kept the figure of the stage before it (that
    /// field's own comment carries the arithmetic) — so an argument leaning on
    /// the ceiling would be an order of magnitude out. It does not have to lean
    /// on it: EVERY loop in this class — `IndexOf`, `StepTo`, `WriteInto`,
    /// `Prune` — runs to `_count`, the rounds actually tracked, and the array's
    /// length is only how many may be tracked at once. What bounds `_count` is
    /// what one client is SENT: relevance (Р32) and `SightRadius`, not the
    /// arena's slot ceiling. A dictionary would buy nothing but garbage on a
    /// path that runs every frame, and it would not shorten the walk the frame
    /// half has to make over the live rounds anyway.
    public sealed class TracerProjectiles
    {
        /// One reconstructed round: what the wire said, the two ticks that
        /// bound its life, and — since app-88jb Т32 — the CACHE, i.e. where the
        /// integrator has walked it to and which tick that is true at.
        ///
        /// THE TWO HALVES ARE NAMED APART ON PURPOSE. The `Spawn*` fields are
        /// what the wire said and never change after `TrySpawn`; the cache
        /// fields do change, and they are the ONLY ones that do (`EndTick`
        /// aside). The birth half has to survive intact because a TARGET TICK
        /// BEHIND THE CACHE re-runs the flight from it — an integrator that has
        /// already reflected cannot be stepped back through its own reflection.
        /// (What moves the target back is the latched rewind depth, never the
        /// render clock; `StepTo`'s step 1 carries the account.)
        ///
        /// THE CACHE IS FIELDS OF THIS STRUCT, NEVER A PARALLEL ARRAY
        /// (coordinator Ruling 288). `Prune` removes by swapping the last live
        /// entry into the freed slot (`_live[i] = _live[_count]`), so a cache
        /// living HERE moves with its round BY CONSTRUCTION and the mutation
        /// "forgot to swap the cache too" is not expressible. A side table
        /// would be the very thing `NoEnd`'s doc below refuses in the small:
        /// two fields that must agree are two fields that can disagree.
        struct Track
        {
            public int Id;
            public int SpawnTick;
            public int EndTick;
            public float2 SpawnPos;
            public float SpawnHeight;
            public float2 SpawnVel;
            public float SpawnVelZ;
            public float SpawnTtl;
            public float Radius;
            public ProjectileOwner Owner;
            public byte OwnerIndex;

            /// The tick the six fields below are true at. Starts at
            /// `SpawnTick`, which is what makes an untouched track answer
            /// exactly what it answered before this class had a cache at all.
            public int CacheTick;
            public float2 Pos;
            public float Height;
            public float2 Vel;
            public float VelZ;
            public float Ttl;
            public int Ricochets;

            /// STOPPED IN A CONTACT, WAITING FOR THE SERVER TO SAY WHAT
            /// HAPPENED (coordinator Ruling 289/304). SET FROM TWO PLACES, and
            /// the pair is worth naming because an auditor who trusts a "only
            /// `StepTo`" wording — which is what stood here — walks past the
            /// second one, exactly as this task's first round of witnesses did:
            ///   * `StepTo`, where the GEOMETRY stopped the round inside a step
            ///     it had not finished;
            ///   * `OnRicochet`, on the REFUSAL path, where the server named a
            ///     bounce this client's own gates would not grant — that
            ///     method's own doc calls marking it waiting the execution of
            ///     Ruling 290's sentence, and the quantized contact point is
            ///     why leaving the velocity pointing into the wall is not
            ///     enough.
            /// Cleared by an authoritative event that DOES move the round
            /// (a granted reflection) or by the cache being thrown away
            /// (`SeatOnBirth`). A waiting round IS drawn — in the contact, and in it for
            /// BOTH halves of the render pair, because an interpolator handed
            /// two different points would stretch the stopped round one frame
            /// further, i.e. straight through the wall the step refused to
            /// cross.
            public bool Waiting;

            /// THE STEP BUDGET RAN OUT BEFORE THE ASKED-FOR TICK (coordinator
            /// Rulings 295/304/305). DECIDED only by `StepTo` — it is the one
            /// member that can tell "behind the clock" from "arrived" — but,
            /// like `Waiting` above, it is CLEARED elsewhere as well:
            /// `SeatOnBirth` (a cache thrown away is not behind anything yet)
            /// and `OnRicochet` (the server has just said where the round is,
            /// so the gap the budget could not close is gone). Such a round is NOT
            /// DRAWN AT ALL — not drawn at its birth position, which for a
            /// round born 90 ticks ago is 42 m behind where it really is, and
            /// a bullet in the wrong place is worse than no bullet. It is a
            /// self-healing state: the next frame spends another budget on it,
            /// and a round that is behind catches up in about half a second.
            public bool NotCaughtUp;

            /// WHERE THE NEXT STEP WOULD BE STOPPED, looked up but not taken
            /// (bd `app-pj5t`).
            ///
            /// The caller writes BOTH halves of the render pair off ONE walk
            /// of the integrator — `predictedTick` and `predictedTick + 1` —
            /// and the closed form answers the newer half by extending the
            /// cache along its velocity. That extension does not ask the
            /// geometry, so on the frame whose contact lies INSIDE the step
            /// the round was drawn up to a whole step past the wall (1.75 m at
            /// the shipped speed) and jumped back into the contact on the next
            /// frame, trail and all. `Waiting` could not cover it: it is
            /// decided by a step that was TAKEN, and this contact belongs to a
            /// step that has not been.
            ///
            /// So `StepTo` looks one step ahead WITHOUT committing it, and
            /// what it finds lands here: the round still stands where it
            /// really is on its own tick, and the newer half of the pair is
            /// clamped to the contact instead of extrapolated through it.
            /// Nothing here decides anything about the round's life — that
            /// stays the server's (CR 3); it decides where a line is drawn on
            /// one frame.
            ///
            /// Cleared everywhere `Waiting` is, and for the same reasons: the
            /// look-ahead belongs to the cache's current tick, so a cache
            /// thrown away (`SeatOnBirth`) or moved by an authoritative event
            /// takes the look-ahead with it.
            public bool HasClamp;
            public float2 ClampPos;
            public float ClampHeight;
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
        /// hold a `u16` code that a later round needs.
        /// ⚠ WHAT IT NO LONGER HAS TO DEFEND AGAINST IS THE PICTURE (app-88jb
        /// Т32). Before this task a round with no ending flew THROUGH walls
        /// forever, and cutting it off after this many ticks was the only thing
        /// that stopped it; now the geometry stops it in the first contact it
        /// meets and it stands there instead. So the visible cost of a lost
        /// ending changed shape rather than size — a bullet parked against a
        /// wall for eight ticks, not a bullet flying through the arena — and
        /// the slot argument above is what still makes the constant necessary.
        const int LostEndSlackTicks = 8;

        readonly Track[] _live;

        /// The arena's geometry and the ricochet numbers, i.e. everything
        /// `ProjectileFlight` asks for besides the round itself (plan finding
        /// A2-I9b). Held by value like every other consumer of this struct:
        /// it is a `SimConfig`, not a reference to one, and a match's config
        /// does not change under a live client.
        readonly SimConfig _cfg;

        /// How many steps ONE `StepTo` may spend on ONE round
        /// (`NetConfig.TracerCatchUpBudget`, coordinator Rulings 295/305).
        /// PER ROUND AND PER CALL, not per frame: a per-frame budget would mean
        /// that a client which suddenly sees a hundred rounds draws one of them
        /// and never the other ninety-nine, which is not a smoothed spike but a
        /// switched-off feature. Per round it bounds the spike — a hundred cold
        /// rounds cost `100 × budget` steps instead of `100 × 90` — and every
        /// round behind the clock closes the gap by `budget − 1` ticks per
        /// frame, so the measure heals itself in about half a second.
        readonly int _catchUpBudget;

        int _count;

        /// `cfg` SECOND and the budget THIRD, and both are decisions rather
        /// than an order of convenience. The config is second because it is
        /// what `ProjectileFlight` takes second everywhere in this project.
        /// The budget is third and comes from `NetConfig` rather than from
        /// `SimConfig` because it is a CLIENT POLICY — how much work one frame
        /// of this client's picture may cost — and not a rule of the world;
        /// the precedent stands one line away from this constructor's only
        /// production call site, `new ClientEventQueue(in _timings,
        /// _net.SnapshotEventBudget)`.
        public TracerProjectiles(int capacity, in SimConfig cfg, int catchUpBudget)
        {
            if (capacity < 0) capacity = 0;
            _live = new Track[capacity];
            _cfg = cfg;
            // A budget of zero would mean no round ever reaches the tick it is
            // asked about, i.e. an empty picture forever; the floor is the same
            // refusal-as-a-value this whole class is built on, and it is here
            // rather than in the asset because a hand-edited YAML answers to no
            // [Range] (NetConfig's own type doc says so of every one of them).
            _catchUpBudget = catchUpBudget < 1 ? 1 : catchUpBudget;
        }

        /// How many rounds are tracked — including any whose spawn tick the
        /// clock has not reached yet and any that are behind their budget,
        /// which is why this is not the same number `WriteInto` returns.
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
        /// `birthSteps` (app-88jb Т32, coordinator Rulings 291/306, bd
        /// `app-56kx`) IS HOW MANY FLIGHT STEPS THE ROUND HAD ALREADY TAKEN
        /// WHEN THE TICK IT WAS BORN IN ENDED, and it moves the SEED POINT
        /// forward by exactly that many: `pos + dir * horizSpeed * TickDt *
        /// birthSteps`. Zero means "nothing is known about the birth tick" —
        /// which is what a round spawned through a test seam carries, what a
        /// refused wire byte degrades to, and what every caller written before
        /// this parameter existed keeps meaning.
        /// WHY THE SEED MOVES AND THE EVENT'S POINT DOES NOT: the envelope's
        /// `Pos` is the MUZZLE, the pre-step point where the shot happened,
        /// while the round itself was walked forward inside that same tick —
        /// once by `ProjectileSystem.Update`, and `RewindSplit.InputTicks` more
        /// by the catch-up that pays for the shooter's own input lag (Т27). The
        /// muzzle point has three readers that all want the muzzle (the shot
        /// sound, a mob's muzzle flash, and the assembler's whole relevance
        /// segment — see `SimEvent.BirthSteps`), so what crosses the wire is
        /// the COUNT and the seed is moved here, in the one place that already
        /// computes `dir * horizSpeed`.
        /// ⚠ `SpawnTick` IS NOT MOVED WITH IT, and that is the half worth
        /// stating: the round really was born on that tick, every age this
        /// class computes is measured from it, and moving it would shift the
        /// round's whole life to pay for a position that has just been paid for
        /// directly. Nor is the height: it already rides the wire as an
        /// end-of-tick capture and needs no correction at all.
        /// ⚠ AND NEITHER IS `ttl`, WHICH IS THE ONE HALF OF THE SEAM LEFT
        /// OPEN — stated rather than left for the next reader to find. The
        /// server's round has already spent `birthSteps × TickDt` of its life
        /// by the end of its birth tick (`ProjectileSystem` decrements at the
        /// top of every step it takes, catch-up included), and `SpawnTtl` here
        /// is the FULL lifetime, so this client's clock on the round runs up to
        /// three steps = 0.100 s generous at the shipped cap of 5 (app-gtj6;
        /// four steps and 0.133 s at the earlier cap of 6). Nothing today is
        /// wrong because of it and the error has ONE direction: the only reader
        /// is `ProjectileFlight.TryRicochet`'s first gate (`Ttl <= 0` refuses),
        /// and being generous there can only GRANT a reflection the server has
        /// already granted by sending the event; `Prune` counts from `SpawnTtl`
        /// and is generous by the same margin. It is written down because the
        /// next reader of `Ttl` — a round whose glow fades toward the end of
        /// its life, say — inherits it as a defect rather than a margin, and
        /// because a seam closed by halves reads as a seam closed.
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
            byte ownerIndex = ProjectileIds.NoOwner,
            int birthSteps = 0)
        {
            if (_count >= _live.Length) return false;
            if (IndexOf(serverId) >= 0) return false;

            float2 vel = dir * horizSpeed;
            // THE SEED, MOVED BY THE BIRTH TICK'S OWN STEPS (see this method's
            // doc above for why the count crosses the wire and the point does
            // not). It is `vel` that is multiplied rather than `dir *
            // horizSpeed` written out again: the product is already computed
            // one line up, and the whole reason Ruling 306 put this here
            // instead of in `RouteToTracers` is that this is the one place
            // that has it.
            float2 seed = pos + vel * (SimulationWorld.TickDt * birthSteps);

            // The birth half, and ONLY the birth half. Every cache field is
            // seated by `SeatOnBirth` below — one home for "the cache stands
            // where the round was born", shared with the backwards-target
            // reset in `StepTo`, because a second copy of this list is exactly the
            // "two fields that must agree" the `NoEnd` doc refuses. Its
            // completeness is also what makes `Reset`/`Prune`'s `= default`
            // sufficient: an initializer starts from the struct's zero, so a
            // cache field left out anywhere would silently be a zero rather
            // than the round's birth value.
            _live[_count] = new Track
            {
                Id = serverId,
                Owner = owner,
                OwnerIndex = ownerIndex,
                SpawnTick = spawnTick,
                EndTick = NoEnd,
                SpawnPos = seed,
                SpawnHeight = height,
                SpawnVel = vel,
                SpawnVelZ = velZ,
                SpawnTtl = ttl,
                Radius = radius
            };
            SeatOnBirth(ref _live[_count]);
            _count++;
            return true;
        }

        /// The server ended this round (`ProjectileEnded`, any `EndKind`) on
        /// `endTick`. The round keeps flying until the clock REACHES that tick,
        /// so the tracer disappears together with the impact that ended it
        /// rather than the moment the datagram arrived.
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

        /// THE SERVER SAYS THIS ROUND BOUNCED, at `tick`, off the surface whose
        /// outward normal is `normal`, touching it at `pos` and at height
        /// `contactHeight` (app-88jb Т32, coordinator Rulings 290/303; the
        /// event is Т30's `ProjectileRicocheted`, narrowed to four bytes by
        /// `app-5o2q`). The cache is seated on that tick and the reflection is
        /// asked of `ProjectileFlight.TryRicochet` — the same method
        /// `ProjectileSystem` calls, never a second copy of its arithmetic
        /// (Ruling 92: the reflection, the damping, the counter and the speed
        /// floor have ONE home, and this is its second caller).
        ///
        /// THE EVENT CARRIES ALL THREE NUMBERS THAT METHOD ASKS FOR, which is
        /// why this signature looks the way it does and why `contactHeight` is
        /// a parameter rather than something read off the cache. Substituting
        /// the cache's own pre-step height would reproduce exactly the error
        /// `TryRicochet`'s doc warns about — "leaving `Height` at its pre-step
        /// value would stall the round vertically for one tick per ricochet and
        /// drift a descending round upward over a chain" — and the wire carries
        /// the true one precisely because without it "the spark of a mirrored
        /// round draws on the floor" (`PayloadBytesFor`'s own words).
        ///
        /// ⚠ THE METHOD'S FOUR GATES MAY REFUSE WHERE THE SERVER AGREED, AND A
        /// REFUSAL IS NOT AN ERROR. The client's counter, `Ttl` and speed are
        /// its own reconstruction, and it does not know everything the server
        /// knows — a round that spent its last ricochet on a contact this
        /// client never saw will be refused here. What then happens is what
        /// happens on a refusal anyway: the round STANDS in the contact point
        /// the event named and goes no further until the next authoritative
        /// word about it. That is the honest picture, and it is deliberately
        /// not "healed": healing it would mean letting the client decide that a
        /// bounce happened, which is the one thing CR 3 forbids.
        ///
        /// WHAT THE TWO OUTCOMES LEAVE BEHIND, and they are NOT the same:
        ///   * REFLECTED — the round flies on from the contact along the
        ///     reflected velocity, so it is no longer waiting: waiting means
        ///     "the geometry stopped me and nobody has told me what happened",
        ///     and something just did;
        ///   * REFUSED — the round STANDS in the contact and goes no further
        ///     until the next authoritative word about it, which is Ruling
        ///     290's own sentence and is executed by marking it WAITING. It is
        ///     not enough to leave the velocity pointing into the wall and
        ///     trust the next step to meet the same geometry again: the point
        ///     the event names arrives QUANTIZED off the wire, so it can land a
        ///     hair INSIDE the surface it touched, and a sweep that starts
        ///     inside answers nothing at all. The flag says what the picture
        ///     means instead of hoping the arithmetic repeats.
        ///
        /// Answers whether the round was tracked at all, with the same contract
        /// and for the same reason as `Retire` above: this runs inside
        /// FishNet's batched parse, where a throw abandons every message behind
        /// it in the same datagram (Р82/195).
        public bool OnRicochet(int serverId, int tick, float2 pos, float2 normal,
            float contactHeight)
        {
            int index = IndexOf(serverId);
            if (index < 0) return false;

            ref Track t = ref _live[index];

            // SEATED ON THE EVENT'S OWN TICK, WHICH IS USUALLY BEHIND THE
            // CACHE rather than ahead of it — the frame half walks this table
            // to the PREDICTED tick, while an event is shown when the RENDER
            // clock reaches it (Rulings 285/286), so the cache is normally the
            // latched depth in front. Seating it back is the point: the server
            // has just said where this round really was, and everything the
            // integrator did past that word was extrapolation.
            // `Ttl` moves with the tick for the same reason every other cache
            // field does — it is a value AT `CacheTick`, and `StateAt` reports
            // it as such — so the difference is signed and the expression
            // covers both directions.
            t.Ttl -= SimulationWorld.TickDt * (tick - t.CacheTick);
            t.CacheTick = tick;
            t.Pos = pos;
            t.Height = contactHeight;
            t.NotCaughtUp = false;

            // THE POSITION IS TAKEN BEFORE THE GATES ARE ASKED, and that is
            // Ruling 290's own instruction rather than an ordering
            // convenience: a refusal must still leave the round standing where
            // the server said it touched, so the contact cannot be something
            // only the success path writes.
            ProjectileState p = StateAt(in t, tick);
            if (ProjectileFlight.TryRicochet(ref p, in _cfg, normal, pos, contactHeight))
            {
                // The four fields that method promises to have written, and no
                // others: `PrevHeight` is not a field of this cache at all
                // (`StateAt` derives the previous half of the render pair from
                // the tick, never from a stored value), and `Ttl` it does not
                // touch.
                t.Pos = p.Pos;
                t.Vel = p.Vel;
                t.VelZ = p.VelZ;
                t.Height = p.Height;
                t.Ricochets = p.Ricochets;
                t.Waiting = false;
                // The round has been moved and turned by the authority, so the
                // look-ahead taken before the reflection describes a flight
                // that no longer exists (bd `app-pj5t`). It is retaken by the
                // next `StepTo`, from where the server just put the round.
                t.HasClamp = false;
            }
            else
            {
                t.Waiting = true;
                t.HasClamp = false;
            }
            return true;
        }

        /// WHO FIRED THE ROUND `serverId`, out of the table the spawn record
        /// already filled (app-88jb Т31, coordinator Rulings 247/256).
        ///
        /// WHY THE QUESTION IS ASKED HERE AT ALL. A `ProjectileEnded` payload
        /// does not carry the shooter — its four bytes are spent on the round's
        /// id, the ending's kind and the victim, and there is no room for a
        /// fifth thing — so `ClientEventDecoder` leaves
        /// `SimEvent.PlayerIndex` at `ProjectileIds.NoOwner` for both body
        /// endings. And the blow a networked client rebuilds from that event
        /// is built out of exactly that byte twice over:
        /// `Impact.ProjectileMassFor` and `SnapshotEvents.SpeedCapFor` both
        /// fork on it, so a collector's own shot decoded as a mob's produces a
        /// moment several times weaker than the one the server resolved. This
        /// side still knows the answer — it was told when the round SPAWNED —
        /// which is the same shape `NetworkSimBackend.RestoreMobType` has for
        /// a `MobDied`: put back the one field the wire drops that this side
        /// can still answer for.
        /// ⚠ THE CEILING IS NOT THE ARGUMENT, AND SAYING SO IS app-88jb Т32's
        /// correction of this very paragraph. An earlier wording read "eight
        /// bytes is the catalog's ceiling and there is no room": the ceiling
        /// became NINE in Т32-А (`SnapshotEvents.MaxPayloadBytes`, Ruling 292)
        /// and the sentence turned into a false reason for a true fact. What is
        /// true is per-kind and stated above — `ProjectileEnded` spends its own
        /// width on other things.
        ///
        /// WHEN IT STILL ANSWERS. Until `Prune` carries the clock past the
        /// round's `EndTick` — so an ending's own arrival, which only records
        /// that tick, does not close the window: the restore therefore runs in
        /// the decode loop BEFORE `RouteToTracers` retires anything, and
        /// `TracerProjectilesTests` pins both halves of that ordering rather
        /// than leaving it a hope about the caller.
        ///
        /// A MISS IS A VALUE, NOT AN EXCEPTION (Р82/195). This is asked from
        /// inside FishNet's batched parsing loop, where a throw abandons every
        /// message behind it in the same datagram; a round this client never
        /// saw fired — the table was full, the truncated id was already in
        /// use, or a reorder landed the end first — leaves `NoOwner` behind,
        /// exactly as `RestoreMobType` leaves a zero archetype. The seat is
        /// the honest half of that answer: `ProjectileOwner`'s own zero is
        /// `Player`, a real rail rather than an absence.
        public bool TryGetOwner(int serverId, out ProjectileOwner owner, out byte ownerIndex)
        {
            // Through the same linear `IndexOf` `Retire` uses, for its reason
            // (see the class doc's note on the table's size).
            int index = IndexOf(serverId);
            if (index < 0)
            {
                owner = default;
                ownerIndex = ProjectileIds.NoOwner;
                return false;
            }

            owner = _live[index].Owner;
            ownerIndex = _live[index].OwnerIndex;
            return true;
        }

        /// WALKS EVERY ROUND'S CACHE TO `targetTick` (app-88jb Т32, coordinator
        /// Rulings 287/289/295/304/305). The one mutating member of the frame
        /// half, and the ONLY place the two states below are decided — so
        /// `WriteInto` stays a pure function of the cache and can be called
        /// twice per frame, which it is.
        ///
        /// THE NAME IS `StepTo` AND NOT `Advance` (Р421, plan finding B2-I3),
        /// because `Advance` is taken twice in this very namespace and means
        /// the OPPOSITE of this: `GhostProjectiles.Advance` and
        /// `EntityStaleTracker.Advance` both mean "age everything and let go of
        /// what has expired".
        ///
        /// WHAT ONE ROUND'S CALL DOES, in order:
        ///  1. A TARGET BEHIND THE CACHE THROWS THE CACHE AWAY. The cache is
        ///     reset to the birth state and re-run forward from there, because
        ///     an integrator that has bounced off a wall cannot be stepped back
        ///     through its own reflection; the birth half of `Track` exists to
        ///     make that possible.
        ///     ⛔ AND THE TRIGGER IS NOT THE RENDER CLOCK, however plausible
        ///     that reads. An earlier wording here (and at `SeatOnBirth`, and
        ///     in the class doc) said "the render clock genuinely does move
        ///     backwards — `RenderClockSnapTicks` is 10 and a snap is what that
        ///     number is for". It does not: `RenderClock.Advance` snaps ONLY
        ///     FORWARD and slews in both directions, and that class's own doc
        ///     states the invariant — "a clock that has run AHEAD of its target
        ///     is never snapped back … `renderTime` is monotonic inside an
        ///     epoch (spec §3.9)". So `renderTick` never walks back inside an
        ///     epoch, and a NEW epoch empties this table outright
        ///     (`ClientMatchReset.ResetForEpoch` calls `Reset`), leaving no
        ///     cache to rewind.
        ///     WHAT DOES MOVE THE TARGET BACK IS THE OTHER SUMMAND. The caller
        ///     asks about `renderTick + _rewindDepth`
        ///     (`NetworkSimBackend.Advance`), and the latched depth SHRINKS —
        ///     it is re-measured on the prediction tick, 30 Hz, while this is
        ///     asked on the render frame, 60+ Hz, so most frames carry a
        ///     `renderTick` that did not move and a depth that may have. One
        ///     tick less of depth on such a frame is a target one tick behind
        ///     the cache.
        ///     ⚠ AND THE RESET IS THE WHOLE TABLE, NOT ONE ROUND: the branch
        ///     stands inside the loop over `_count`, and the quantity that
        ///     shrank is the same for every track, so one shrinking frame
        ///     re-seats every round at once and hands the budget below every
        ///     round's whole flight to re-walk. That is what the budget is
        ///     bounding, and it is why the number is a client policy rather
        ///     than a rule of the world (`NetConfig.TracerCatchUpBudget`);
        ///  2. otherwise it takes single `ProjectileFlight.Step`s, at most
        ///     `_catchUpBudget` of them, until `CacheTick` reaches the target;
        ///  3. of the three candidates that step reports it looks at TWO — the
        ///     interior barrier (only when `ProjectileFlight.BarrierStops` says
        ///     this round does not clear its crown) and the ring boundary. The
        ///     nearer of the two by `t` stops the round: it stands at
        ///     `lerp(start, Target, t)` and is marked WAITING. With nothing in
        ///     the way the round simply takes the step.
        ///     ⚠ THE TIE-BREAK BETWEEN THE TWO DOES NOT MATTER HERE, and that
        ///     is worth writing down so the next reader does not go looking for
        ///     a copy of `ProjectileSystem`'s canonical packing order: both
        ///     candidates mean the same thing to a tracer — "stand still" — so
        ///     which one wins an exact tie cannot change the picture. The
        ///     canonical order matters where the two lead to different EVENTS,
        ///     and this class emits none.
        ///     ⚠ THE FLOOR IS NOT LOOKED AT AT ALL (Ruling 289). Crossing the
        ///     floor is the end of a round's life, and a life ends when the
        ///     server says it does (CR 3);
        ///  4. a round the budget could not walk all the way is marked NOT
        ///     CAUGHT UP and, until it is, is not drawn at all.
        public void StepTo(int targetTick)
        {
            float dt = SimulationWorld.TickDt;
            for (int i = 0; i < _count; i++)
            {
                ref Track t = ref _live[i];

                // 1. Behind the cache: throw it away and re-run from birth.
                // Asked BEFORE the waiting check below, because a waiting
                // round is exactly the one whose cache must not survive a
                // backwards target — it is standing at a contact its re-run may
                // never reach.
                if (targetTick < t.CacheTick) SeatOnBirth(ref t);

                // A round stopped in a contact has nothing to walk: it stands
                // there until an authoritative word arrives (`OnRicochet`,
                // `Retire`), and `NotCaughtUp` is deliberately left alone —
                // it is false whenever `Waiting` is true, because the step
                // that stopped this round is the step that reached it.
                if (t.Waiting) continue;

                for (int spent = 0; spent < _catchUpBudget && t.CacheTick < targetTick; spent++)
                {
                    // The cache AS A ROUND, through the one function that
                    // turns one into the other. At its own tick every age in
                    // there collapses to zero, so this is the cache itself and
                    // not an extrapolation of it.
                    ProjectileState p = StateAt(in t, t.CacheTick);
                    ProjectileFlight.StepResult step = ProjectileFlight.Step(in p, in _cfg, dt);

                    // 2. TWO CANDIDATES OF THE THREE. The interior barrier
                    // counts only if this round does not clear its crown --
                    // the same gate `ProjectileSystem.AcceptCandidate` asks,
                    // in the same one home, with the contact height computed
                    // by that method's own expression. Without it a high round
                    // would be stopped forever by a low wall it legitimately
                    // flew over, and no server event would ever come to
                    // release it. The ring boundary is never asked this: it is
                    // the one barrier with no modelled top (`BarrierStops`'
                    // own doc). The FLOOR is not looked at at all -- crossing
                    // it ends a round's life, and endings are the server's
                    // (CR 3).
                    bool barrier = step.HasBarrier && ProjectileFlight.BarrierStops(in p, in _cfg,
                        p.Height + p.VelZ * dt * step.BarrierT, dt);
                    bool ring = step.HasRingWall;

                    t.CacheTick++;
                    // The tick passed for this round whichever way the step
                    // ended, exactly as it does on the authority's side, where
                    // `Ttl` is decremented at the top of the movement step and
                    // read at the bottom.
                    t.Ttl -= dt;

                    if (barrier || ring)
                    {
                        // The nearer of the two stops it. THE TIE-BREAK DOES
                        // NOT MATTER and that is worth stating so the next
                        // reader does not go looking for a copy of
                        // `ProjectileSystem`'s canonical packing order: both
                        // candidates mean one thing to a tracer -- "stand
                        // still" -- so which of them wins an exact tie cannot
                        // change the picture. The order matters where the two
                        // lead to different EVENTS, and this class emits none.
                        float contactT = barrier && ring ? math.min(step.BarrierT, step.RingWallT)
                            : barrier ? step.BarrierT : step.RingWallT;
                        // `lerp` of the two endpoints and the SAME height
                        // expression the gate above was asked with, in the
                        // same grouping (`AcceptCandidate`'s own
                        // `proj.Height + proj.VelZ * TickDt * t`): the point
                        // this round is drawn at and the height its gate was
                        // decided on must be the one contact, not two floats
                        // that nearly agree.
                        t.Pos = math.lerp(p.Pos, step.Target, contactT);
                        t.Height = p.Height + p.VelZ * dt * contactT;
                        t.Waiting = true;
                        break;
                    }

                    // Nothing in the way: the step is simply taken, in the
                    // authority's own two statements (`ProjectileSystem`'s
                    // `default:` arm).
                    t.Pos = step.Target;
                    t.Height = p.Height + p.VelZ * dt;
                }

                // 4. Short of the target with the budget spent. A waiting
                // round is never this: it reached everything there was to
                // reach.
                t.NotCaughtUp = !t.Waiting && t.CacheTick < targetTick;

                // 5. THE STEP AFTER THE LAST ONE, LOOKED AT AND NOT TAKEN
                // (bd `app-pj5t`). The caller asks this table for
                // `targetTick + 1` as well — the newer half of the render
                // pair — and the closed form would answer it by extending the
                // cache through whatever is in the way. Asking the same two
                // candidates the loop above asks, one step further, is what
                // lets `StateAt` clamp that half to the contact instead.
                //
                // ⛔ NOTHING IS COMMITTED HERE: the cache still stands on its
                // own tick, `Ttl` is untouched, `Ricochets` is untouched, and
                // the round is NOT marked waiting — it has not reached the
                // contact yet, and saying it had would stop it a step early,
                // which is the mirror of the defect this fixes.
                //
                // A waiting round needs none of this (it is already standing
                // in a contact and both halves read the same point), and a
                // round that did not catch up is not drawn at all.
                t.HasClamp = false;
                if (!t.Waiting && !t.NotCaughtUp)
                {
                    ProjectileState ahead = StateAt(in t, t.CacheTick);
                    ProjectileFlight.StepResult peek =
                        ProjectileFlight.Step(in ahead, in _cfg, dt);
                    bool peekBarrier = peek.HasBarrier
                        && ProjectileFlight.BarrierStops(in ahead, in _cfg,
                            ahead.Height + ahead.VelZ * dt * peek.BarrierT, dt);
                    if (peekBarrier || peek.HasRingWall)
                    {
                        float peekT = peekBarrier && peek.HasRingWall
                            ? math.min(peek.BarrierT, peek.RingWallT)
                            : peekBarrier ? peek.BarrierT : peek.RingWallT;
                        t.ClampPos = math.lerp(ahead.Pos, peek.Target, peekT);
                        t.ClampHeight = ahead.Height + ahead.VelZ * dt * peekT;
                        t.HasClamp = true;
                    }
                }
            }
        }

        /// Fills `destination` with every round visible at `tick` and answers
        /// how many were written. MUTATES NOTHING — not the cache, not the
        /// table: `StepTo` moves the cache, `Prune` removes rounds, and this is
        /// called TWICE per frame, once for each half of the render pair.
        ///
        /// STILL A CLOSED FORM, ONLY MEASURED FROM THE CACHE (coordinator
        /// Ruling 287): the answer is `cache + velocity × dt × (tick −
        /// cacheTick)`, which is defined for any tick, before or after the one
        /// the cache stands on. That is what lets the caller ask for
        /// `predictedTick` and `predictedTick + 1` off ONE walk of the
        /// integrator, and what makes an untouched track — cache still on its
        /// spawn tick — answer bit for bit what this class answered before Т32.
        ///
        /// TWO ROUNDS ARE TREATED SPECIALLY, and both cases are read from the
        /// track rather than re-decided here (see `Track.Waiting` and
        /// `Track.NotCaughtUp` for the why of each): a WAITING round is written
        /// standing still, the same point in both halves of the pair; a round
        /// that has NOT CAUGHT UP is not written at all.
        ///
        /// A destination shorter than the table is filled to its own length:
        /// the caller's array is `RenderSnapshot.Projectiles`, sized by
        /// `ArenaConfig.MaxProjectiles`, and writing past it would be the one
        /// failure this class could not report as a value.
        public int WriteInto(ProjectileState[] destination, int tick)
        {
            if (destination == null) return 0;

            int written = 0;
            for (int i = 0; i < _count && written < destination.Length; i++)
            {
                ref Track t = ref _live[i];
                if (tick < t.SpawnTick) continue;      // not born yet on screen
                if (tick >= t.EndTick) continue;       // the impact has been shown
                if (t.NotCaughtUp) continue;           // behind the budget — drawn nowhere

                destination[written++] = StateAt(in t, tick);
            }
            return written;
        }

        /// Drops rounds the clock has carried past their ending. Split from
        /// `WriteInto` on purpose: the caller writes BOTH halves of the render
        /// pair (`tick` and `tick + 1`), and a write that also pruned would
        /// make the result depend on which half was asked for first — a round
        /// ending on the newer half would vanish from the older one too, one
        /// frame early, depending only on call order. `WriteInto` therefore
        /// never mutates, and this runs once per frame with the OLDER tick.
        public void Prune(int tick)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                if (tick < _live[i].EndTick && !OutlivedItsEnd(in _live[i], tick))
                    continue;

                _count--;
                // Swap-remove: order carries no meaning (the renderer keys off
                // `Id`), and removal stays O(1) on a path that runs per frame.
                // Since Т32 this one statement is also what moves a round's
                // CACHE with it, which is why the cache is fields of `Track`
                // and not a table beside it (that struct's own doc).
                _live[i] = _live[_count];
                _live[_count] = default;
            }
        }

        /// A new match (`ClientMatchReset.ResetForEpoch`): rounds of the match
        /// that ended must not outlive it, and the new match mints their ids
        /// again from scratch. `= default` covers the cache and both states
        /// along with everything else — see `SeatOnBirth`, which is what makes
        /// the struct's zero safe by naming every cache field explicitly at
        /// every spawn.
        public void Reset()
        {
            for (int i = 0; i < _count; i++) _live[i] = default;
            _count = 0;
        }

        /// THE CACHE, PUT BACK WHERE THE ROUND WAS BORN — the one home of the
        /// list "which fields are the cache", called from the two places that
        /// need it: `TrySpawn`, where a new tenant's cache starts at its birth
        /// state, and `StepTo`, where a TARGET TICK BEHIND THE CACHE throws the
        /// cache away and re-runs the flight forward from here.
        /// ⛔ RE-RUNNING IS THE ONLY CORRECT ANSWER TO A BACKWARDS TARGET, not
        /// a simplification: an integrator that has already bounced off a wall
        /// cannot be stepped back through its own reflection. That is also the
        /// whole reason the birth half of `Track` is kept intact and named
        /// apart (see that struct's doc).
        /// ⚠ AN EARLIER WORDING NAMED THE WRONG CAUSE — "`NetConfig.
        /// RenderClockSnapTicks` is 10 precisely because the clock really does
        /// snap". The clock's snap is FORWARD ONLY and `renderTime` is
        /// monotonic inside an epoch (`RenderClock`'s own doc), so it can never
        /// produce this call; the target moves back when the LATCHED REWIND
        /// DEPTH shrinks on a frame the render tick did not advance. `StepTo`'s
        /// step 1 carries the whole account, including the fact that such a
        /// frame re-seats EVERY track at once.
        /// ⚠ `Ricochets` GOES BACK TO ZERO WITH THE REST, and it must: the
        /// re-run starts before any of this round's bounces happened, so a
        /// counter left standing would spend a budget the re-run has not used
        /// yet and refuse a reflection the server already granted.
        static void SeatOnBirth(ref Track t)
        {
            t.CacheTick = t.SpawnTick;
            t.Pos = t.SpawnPos;
            t.Height = t.SpawnHeight;
            t.Vel = t.SpawnVel;
            t.VelZ = t.SpawnVelZ;
            t.Ttl = t.SpawnTtl;
            t.Ricochets = 0;
            t.Waiting = false;
            t.NotCaughtUp = false;
            // The look-ahead belongs to the tick the cache stood on, and that
            // tick is what this method throws away (bd `app-pj5t`).
            t.HasClamp = false;
        }

        /// The closed form, in the authority's own terms — measured from the
        /// CACHE since Т32 (see `WriteInto`). `PrevPos`/`PrevHeight` are the
        /// SAME function one tick earlier — never a stored previous value — so
        /// they stay correct across a skipped frame, and they are clamped at
        /// the spawn tick so a round's first frame interpolates from its muzzle
        /// rather than from behind it.
        ///
        /// A WAITING ROUND SPANS NO TIME AT ALL: both ages collapse to zero, so
        /// the point the step stopped in is written into both halves of the
        /// pair, and the round stands still on screen instead of being
        /// interpolated one frame further — through the wall the step refused
        /// to cross (coordinator Ruling 304). Its `Vel` is still reported as
        /// the velocity it carries: a stopped bullet still points the way it
        /// was flying, and zeroing it would be inventing a fact the server
        /// never stated.
        static ProjectileState StateAt(in Track t, int tick)
        {
            float dt = SimulationWorld.TickDt;
            // The clamp, stated as the max it is: never earlier than the spawn
            // tick. With the cache still ON the spawn tick this is exactly the
            // `age > 0 ? age - 1 : 0` this method carried before Т32, which is
            // what makes the fourteen closed-form fixtures answer bit for bit.
            int prevTick = math.max(tick - 1, t.SpawnTick);
            int age = t.Waiting ? 0 : tick - t.CacheTick;
            int prevAge = t.Waiting ? 0 : prevTick - t.CacheTick;

            // THE HALF THE INTEGRATOR HAS NOT WALKED IS CLAMPED TO WHAT IT
            // WOULD HIT (bd `app-pj5t`, see `Track.HasClamp`). Only ages PAST
            // the cache are affected — the round's own tick is where the
            // integrator really stood and is left exactly as it was, which is
            // what keeps the closed-form fixtures answering bit for bit.
            bool clamped = t.HasClamp && age > 0;
            bool prevClamped = t.HasClamp && prevAge > 0;

            return new ProjectileState
            {
                Id = t.Id,
                Owner = t.Owner,
                OwnerIndex = t.OwnerIndex,
                Pos = clamped ? t.ClampPos : t.Pos + t.Vel * (dt * age),
                PrevPos = prevClamped ? t.ClampPos : t.Pos + t.Vel * (dt * prevAge),
                Vel = t.Vel,
                Height = clamped ? t.ClampHeight : t.Height + t.VelZ * (dt * age),
                PrevHeight = prevClamped ? t.ClampHeight
                    : t.Height + t.VelZ * (dt * prevAge),
                VelZ = t.VelZ,
                Radius = t.Radius,
                Ttl = t.Ttl - dt * age,
                // NOT DECORATION AND NOT A RENDERER'S FIELD: this is what
                // makes the function complete enough for `OnRicochet` to hand
                // its result straight to `ProjectileFlight.TryRicochet`, whose
                // second gate reads exactly this counter. Reconstructing the
                // round anywhere else would be the "two fields that must
                // agree" this class refuses everywhere. It is zero for every
                // round that has not bounced, which is every round this file's
                // fourteen closed-form fixtures ever build.
                Ricochets = t.Ricochets
            };
        }

        /// Whether a round with no end from the server has outlived every
        /// account of itself (see `LostEndSlackTicks`).
        static bool OutlivedItsEnd(in Track t, int tick)
        {
            if (t.EndTick != NoEnd) return false;

            int lifetimeTicks = (int)math.ceil(t.SpawnTtl / SimulationWorld.TickDt);
            return tick - t.SpawnTick > lifetimeTicks + LostEndSlackTicks;
        }

        int IndexOf(int serverId)
        {
            for (int i = 0; i < _count; i++)
                if (_live[i].Id == serverId) return i;
            return -1;
        }
    }
}
