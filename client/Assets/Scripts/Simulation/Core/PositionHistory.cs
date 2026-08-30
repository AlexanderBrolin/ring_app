using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Ring of per-body positions over the rewind window (app-88jb Т24,
    /// spec §3.6). Capacity is RewindCapTicks + 1 = 7 rows -- the six ticks
    /// a shot may be rewound by, plus the tick being rewound FROM.
    ///
    /// IT LIVES IN Core, NOT Combat (finding B-M4), because it is world
    /// state and not a combat routine: it survives a tick, and the address it
    /// hands out is carried by MobState/PlayerState and hashed with them.
    /// Combat only ASKS it questions.
    /// ⚠ THE ROWS THEMSELVES ARE IN NEITHER StateHash NOR WorldSave yet, and
    /// the ring does NOT ride SaveState/RestoreState -- SimulationWorld's
    /// `_history` doc states the same boundary from the other side. What a
    /// restore does rebuild is the OCCUPANCY (RederiveOccupancy below); the
    /// recorded positions of a rolled-back future stay standing. Inert while
    /// Write is a stub, and Т25 is the task that puts the rows into the hash
    /// and into the save.
    ///
    /// THERE IS NO PER-ROW POPULATION COUNT, AND THE SPEC ASKS FOR ONE.
    /// Spec §3.6 names a count per row; that sentence describes the scheme
    /// Р406 CANCELLED, in which a row was a run of live bodies packed back to
    /// back and its length therefore had to be recorded. With a PERMANENT
    /// SLOT a row has no live prefix at all: every body writes at its own
    /// address and is read at its own address. "A walk that trusted one count
    /// would read the tail of the previous tick" is a sentence about packing,
    /// and nothing here is packed or walked, so the count has nothing to
    /// count.
    /// ⚠ Recorded as a divergence between spec §3.6 and decision Р406, not as
    /// an omission in this class. Whether §3.6 is amended is the owner's call.
    ///
    /// WHAT ANSWERS STALENESS INSTEAD IS TWO MECHANISMS, AND NEITHER IS
    /// REDUNDANT:
    ///   * `_rowTick` -- a row answers only about the tick it was written
    ///     for. Without the stamp the opening ticks of a match, and any body
    ///     younger than the window, would be answered for anyway out of
    ///     whatever the ring happened to hold.
    ///   * `Record.FlagAlive` -- the body at that address was alive that tick.
    /// ⭐ AND FlagAlive IS ENOUGH ONLY BECAUSE ReturnSlot CLEARS. A slot back
    /// in circulation would otherwise carry the PREVIOUS tenant's FlagAlive
    /// under a stamp that is already current, and the new tenant would be
    /// found standing where the dead body stood. That is the whole reason
    /// ClearRowsOf exists: a structural necessity, not a tested behavior --
    /// see ReturnSlot's own note on why no test can observe it yet.
    ///
    /// ⚠ NOTHING IN THE ROW HALF OF THIS CLASS HAS A TEST WITNESS TODAY --
    /// not `Clear`, not `NoTick`, not `_rowTick`, not `ClearRowsOf`. RULING
    /// 131 exempted `Write` and `PosAt` from needing one; these four were
    /// never covered by that exemption and are simply unobserved, because the
    /// only thing that could observe a row is a reader, and the first reader
    /// is Т27/Т28. Said out loud so the next reader does not mistake the
    /// silence for coverage.
    ///
    /// PosAt NEVER THROWS: this is a combat path (Р378). Every question it
    /// cannot answer has a DEFINED ANSWER instead -- see its own table. A
    /// throw here would turn a body the server merely has no history for into
    /// a dropped tick for everyone in the match.
    internal sealed class PositionHistory
    {
        /// "This row belongs to no tick." Zero cannot serve as the sentinel:
        /// tick 0 is a legal tick of a match, and a fresh ring left at zero
        /// would claim to hold the truth about the opening tick of every
        /// raid. int.MinValue is unreachable for a tick counter that starts
        /// at 0 and only ever increments.
        const int NoTick = int.MinValue;

        // The ring's two dimensions, read by every index computation below:
        // row `t` occupies [(t % _capacityTicks) * _maxBodies + slot].
        readonly int _capacityTicks;
        readonly int _maxBodies;

        readonly Record[] _rows;
        // One stamp per ROW, not per record: a row is written as a whole (Т25
        // calls Write once at the end of a tick), so the tick it belongs to is
        // a property of the row and storing it per body would repeat one fact
        // _maxBodies times.
        readonly int[] _rowTick;

        // THE ALLOCATOR IS A SET, NOT A LIST, AND THAT IS A ROLLBACK
        // REQUIREMENT (coordinator RULING 133). A free LIST hands out the slot
        // released most recently, which makes the answer a function of the
        // HISTORY OF RETURNS -- and a history is state. A simulation that can
        // be rolled back may not keep state outside the save that decides
        // future state: RestoreState would rewind the bodies and not the
        // allocator, the next spawn would be handed a different slot than the
        // same spawn in a run that never rolled back, and HistorySlot is
        // hashed, so the two runs' digests would part company. (Worse, a body
        // that died after the save would come back holding a slot the list had
        // already handed on.)
        // Occupancy makes the answer a function of the CURRENT SET of live
        // bodies instead, and that set is derivable from _players and
        // _mobs[0.._mobCount) -- both of which the save carries. So the
        // divergence is not masked, it stops existing: see RederiveOccupancy.
        //
        // A bool per slot rather than a bitmask: 1353 bytes is nothing beside
        // the ring itself, and the scan below stays one comparison per slot
        // instead of a shift, a mask and a branch. The project has chosen the
        // readable linear form five times in writing.
        readonly bool[] _occupied;

        public PositionHistory(int capacityTicks, int maxBodies)
        {
            _capacityTicks = capacityTicks;
            _maxBodies = maxBodies;
            _rows = new Record[capacityTicks * maxBodies];
            _rowTick = new int[capacityTicks];
            _occupied = new bool[maxBodies];
            Clear();
        }

        /// One body's record. 12 bytes: Pos (8) + Flags (1) + padding.
        /// Flags: bit0 Alive, bit1 Sliding, bit2 Invulnerable.
        ///
        /// SLIDING AND INVULNERABLE ARE NOT DECORATION (finding C-I5). The
        /// height gate reads the target's SlideTimer -- ProjectileSystem's own
        /// AcceptCandidate does it at ProjectileSystem.cs:711 -- so a collector
        /// who was sliding five ticks ago and is standing now would be tested
        /// against a STANDING profile, and the round that visibly went over
        /// their head would land. The flag is what makes the rewound question
        /// ask about the body that was there, not the body that is.
        /// Invulnerability is the same argument at the other end of the
        /// window: HeroConfig.DashIframes is 0.2 s, which is EXACTLY 6 ticks
        /// (SimulationWorld.TicksFromSeconds(0.2f)) -- the rewind cap itself
        /// -- so a whole iframe window fits inside the deepest rewind, and
        /// reading invulnerability from the LIVE body would award a hit the
        /// victim had already dodged.
        ///
        /// NO CONSTRUCTOR ON PURPOSE (Т24 step 3): today the only value this
        /// struct can hold is `default`, because the only writer is Т25's
        /// Write. A constructor added now would be production code with no
        /// test able to kill it; Т25 brings both together.
        public readonly struct Record
        {
            public readonly float2 Pos;
            public readonly byte Flags;
        }

        public const byte FlagAlive = 1, FlagSliding = 2, FlagInvulnerable = 4;

        /// Writes the row for `tick` from the world's LIVE bodies. Called
        /// ONCE, at the END of TickAll (Т25) -- the row for tick T describes
        /// the world as T left it, which is what makes k == 0 mean "the live
        /// positions" in PosAt's table below.
        public void Write(int tick, SimulationWorld w)
        {
            // Т25 is the first writer (spec §3.6). The ring it will fill is
            // already allocated by the constructor above: the rows are the
            // storage half of this class, the occupancy set is the addressing
            // half, and only the second half has a test asking for it today.
        }

        /// | Case                            | Answer                          |
        /// | Record present, Alive           | the historical position/flags   |
        /// | Row's Tick does not match       | the CURRENT position -- degrades|
        /// |   (body did not live that tick, |   into "no rewind at all"       |
        /// |    first ticks of the match)    |                                 |
        /// | Record present, Alive cleared   | MISS: the target was dead then  |
        /// | k == 0                          | live positions (the row for T is|
        /// |                                 |   written at the END of TickAll)|
        public bool PosAt(int slot, int tick, float2 currentPos, out Record record)
        {
            // Т27/Т28 are the first readers. Until a writer exists every row
            // is `default` and every stamp is NoTick, so the honest answer to
            // every question is the one the table's second line already
            // describes -- "no rewind at all".
            record = default;
            return false;
        }

        /// Handed out at spawn: THE LOWEST FREE SLOT.
        ///
        /// WHAT RULING 133 ACTUALLY REQUIRES is only that the answer be a
        /// pure function of the occupancy SET -- see _occupied's own doc for
        /// why, and note that "the highest free slot" would satisfy that
        /// requirement exactly as well. Lowest is chosen on top of it, for
        /// two reasons of its own:
        ///   * the ring fills from the bottom instead of wandering, so the
        ///     slot a body holds is a small number a debugger or a fixture
        ///     can be read against;
        ///   * the collectors, who rent first and never give the slot back,
        ///     therefore own exactly 0..playerCount-1, and every mob is
        ///     numbered above them.
        /// Neither is a side effect: they are the reason this rule and not
        /// the other equally pure one.
        ///
        /// THROWS WHEN NOTHING IS FREE, and that is a backstop rather than a
        /// path this call takes -- the same shape and the same promise as
        /// SpawnContainer's own named refusal (R-99). The world sizes this
        /// ring to Arena.MaxMobs + Arena.MaxPlayers, and both populations are
        /// capped below that on the way in: SpawnMob refuses past
        /// _mobs.Length before it reaches the initializer, and _players is
        /// fixed at construction to a playerCount the constructor itself
        /// bounds by Arena.MaxPlayers. So a full ring means the ring and the
        /// world disagree about the arena's own caps, which is a broken
        /// invariant and not a spawn to swallow quietly.
        ///
        /// The scan is linear in maxBodies and is paid PER SPAWN, not per
        /// tick, which puts it three orders of magnitude below the separation
        /// passes the same tick already runs over every body.
        public int RentSlot()
        {
            for (int i = 0; i < _maxBodies; i++)
            {
                if (_occupied[i]) continue;
                _occupied[i] = true;
                return i;
            }
            throw new System.InvalidOperationException(
                "PositionHistory.RentSlot: every slot is occupied. Capacity is " +
                "Arena.MaxMobs + Arena.MaxPlayers and both populations are capped below it, " +
                "so this means the ring was built from different caps than the world.");
        }

        /// Handed back when a body LEAVES THE WORLD'S ARRAYS -- which is
        /// usually a death (DamageMob) but not only: ClearMobsForTest takes
        /// mobs off the arena without killing them and returns their slots
        /// here too, and its own test is named ..._WithoutKillingThem. A
        /// collector never reaches this method at all (PlayerState.HistorySlot's
        /// own doc says why).
        public void ReturnSlot(int s)
        {
            // THE PAST GOES BACK WITH THE SLOT, and this is the mechanism the
            // class header calls a structural necessity: FlagAlive alone
            // cannot tell a live tenant from its predecessor, because a row
            // the current tick did not rewrite still carries the old tenant's
            // Alive bit under a stamp that has already moved on. Clearing at
            // RELEASE rather than at rent keeps the invariant "a free slot
            // holds nothing" true at every instant instead of only just after
            // a rent, which is what makes the FlagAlive read safe.
            //
            // ⚠ NO TEST OBSERVES THIS, and no test can yet. Nothing writes a
            // row until Т25, so every row is already `default` and deleting
            // this call would turn nothing red. In particular
            // DeadBodysSlot_IsReused_ButNotItsPast is NOT the witness its own
            // name suggests: its only past-tense assertion reads MobState.Pos,
            // which SpawnMob assigns directly and which this call has no hand
            // in. Written now regardless, because the invariant above has to
            // hold before the first row is ever written, and a clear bolted on
            // afterwards would be a fix for a bug that had already shipped.
            ClearRowsOf(s);
            _occupied[s] = false;
        }

        /// Back to a world with no history and no body: every row blank,
        /// every stamp back to the sentinel, every slot free again. Shares one
        /// body with the constructor rather than repeating it (rule 2) -- the
        /// state a fresh ring is in and the state Clear produces are the same
        /// state by definition, and two spellings of it would be two things to
        /// keep in step.
        public void Clear()
        {
            System.Array.Clear(_rows, 0, _rows.Length);
            for (int t = 0; t < _capacityTicks; t++) _rowTick[t] = NoTick;
            System.Array.Clear(_occupied, 0, _occupied.Length);
        }

        /// Re-derives WHICH SLOTS ARE TAKEN from the world's live bodies.
        /// Called by SimulationWorld.RestoreState, and it is the second half
        /// of RULING 133: with a lowest-free rule the occupancy set is the
        /// WHOLE of the allocator's state, and this set is exactly the slots
        /// the restored bodies carry -- so a restore can rebuild it instead of
        /// having to have saved it.
        ///
        /// IT IS A DERIVATION, NOT A SYNCHRONIZATION. Nothing here reconciles
        /// two copies of the same fact: there is one copy, and it lives on the
        /// bodies (MobState.HistorySlot / PlayerState.HistorySlot, both in
        /// WorldSave and both in StateHash). _occupied is an index over that
        /// one copy, rebuilt from it -- which is why it needs no save entry of
        /// its own and cannot drift out of step with one.
        ///
        /// It takes the world for the same reason Write does: the bodies it
        /// must walk are the same bodies, and the walk is the same walk minus
        /// the positions. One call rather than a begin/mark/end protocol, so
        /// there is no half-executed rebuild to leave the set lying.
        ///
        /// ⚠ IT DOES NOT TOUCH THE ROWS. A slot freed by the restore may still
        /// hold rows written by the future that was just rolled back. That is
        /// the ring's own restore, and it arrives with Т25, the task that puts
        /// the rows into the save; today every row is `default`, because
        /// nothing writes one yet.
        /// The slots it reads are NOT range-checked. That is a contract this
        /// doc states here, not one borrowed from a neighbour: they are
        /// numbers this ring itself issued through RentSlot and the save
        /// carried back unchanged, never a value off a wire. A hand-built
        /// MobState/PlayerState pushed through a *ForTest seam breaks the
        /// contract -- it carries HistorySlot 0, or whatever a reflective
        /// sweep last wrote -- and that whole class of fixture is tracked as
        /// app-41wd rather than guarded here.
        public void RederiveOccupancy(SimulationWorld w)
        {
            System.Array.Clear(_occupied, 0, _occupied.Length);
            for (int i = 0; i < w.PlayerCount; i++) _occupied[w.PlayerAt(i).HistorySlot] = true;
            for (int i = 0; i < w.MobCount; i++) _occupied[w.Mobs[i].HistorySlot] = true;
        }

        void ClearRowsOf(int slot)
        {
            for (int t = 0; t < _capacityTicks; t++) _rows[t * _maxBodies + slot] = default;
        }
    }
}
