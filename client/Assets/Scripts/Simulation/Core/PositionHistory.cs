using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Ring of per-body positions over the rewind window (app-88jb Т24,
    /// spec §3.6). Capacity is RewindCapTicks + 1 = 7 rows.
    /// ⚠ THE `+ 1` IS THE WIDTH OF THE CLOSED WINDOW, NOT A ROW FOR "THE TICK
    /// BEING REWOUND FROM" (review finding A-4). Т24 wrote the second reading
    /// and Т25 made it false: PosAt's own table says the row for tick T does
    /// not exist while T is still running, because Write closes the tick. What
    /// 7 counts is DISTINCT TICK NUMBERS -- `T-6 … T`, the six a shot may be
    /// rewound by plus the one it is fired on -- and that span is exactly what
    /// Fold walks once Write has closed T. Both statements cannot be true of a
    /// ROW; both are true of a tick NUMBER, which is what the ring is keyed by.
    ///
    /// IT LIVES IN Core, NOT Combat (finding B-M4), because it is world
    /// state and not a combat routine: it survives a tick, and the address it
    /// hands out is carried by MobState/PlayerState and hashed with them.
    /// Combat only ASKS it questions.
    /// THE ROWS ARE CANONICAL WORLD STATE: they are folded into StateHash
    /// (Fold below, at the canonical position between the container slots and
    /// the waves) and they ride SaveState/RestoreState by deep copy (SaveTo /
    /// RestoreFrom below). Т25 put them there, and the argument is spec
    /// §3.6.1's own: two worlds can agree bit for bit on the present and hold
    /// different pasts, and the first shot rewound by three ticks would then
    /// read different positions in runs whose digests had already agreed. The
    /// counterexample is not hypothetical -- RewindTests'
    /// TwoWorldsWithEqualPresentAndDifferentPast_DisagreeOnTheHash measured
    /// exactly that collision before this task closed it.
    /// A restore therefore brings back BOTH halves of the ring: the rows and
    /// their stamps come from the save, the OCCUPANCY is re-derived from the
    /// restored bodies (RederiveOccupancy below), and the two answers cannot
    /// drift apart because the second is an index over state the first
    /// carries.
    ///
    /// THERE IS NO PER-ROW POPULATION COUNT, AND THE SPEC ASKS FOR ONE.
    /// Spec §3.6 names a count per row; that sentence describes the scheme
    /// Р406 CANCELED, in which a row was a run of live bodies packed back to
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
    /// WHAT THE ROW HALF IS WITNESSED BY, AS OF THE Т25 FIX-ROUND. RULING 131's
    /// exemption is spent, and this list is an INVENTORY OF TODAY'S SUITE
    /// rather than a promise that anything not named is covered -- the closed
    /// form it used to have was itself a review finding (B-2), because a
    /// reader planning Т27's tests would have read the silence as coverage.
    ///   * the moment of the write -- HistoryRowOfTickT_HoldsThePositionAtTheEndOfTickT
    ///   * the degenerate and the miss branch -- the two PosAtATick... fixtures
    ///   * the negative-tick guard -- PosAtANegativeTick_DegradesInsteadOfThrowing
    ///   * the MARK on the degenerate answer (app-88jb Т28, coordinator RULING
    ///     204, the fourth witness RULING 145's "every branch has its own"
    ///     was missing) -- PosAtSaysWhetherItAnsweredFromARow
    ///   * the fold, and the flag byte inside it --
    ///     TwoWorldsWithEqualPresentAndDifferentPast_DisagreeOnTheHash and
    ///     TwoWorldsDifferingOnlyInARecordedFlag_DisagreeOnTheHash
    ///   * the flag axis itself (RULING 153) --
    ///     HistoryRow_CarriesTheSlideAndInvulnerabilityOfTheTickItRecords and
    ///     HistoryRowOfAMob_ReportsItAliveAtItsOwnSlot
    ///   * `Clear`'s STAMP LOOP and the choice of `NoTick` -- the degenerate
    ///     fixture asks about TICK 0, so a sentinel of zero would let a blank
    ///     row answer for it and the question would come back a miss.
    /// ⚠ STILL UNOBSERVED, AND NAMED RATHER THAN LEFT OUT: `ClearRowsOf`, and
    /// the row-blanking and occupancy halves of `Clear` (both duplicate the
    /// state a fresh array is already in, so nothing can tell them from the
    /// constructor). No test kills a body and then reads the row its slot is
    /// handed on to, which is the only question `ClearRowsOf` answers -- see
    /// ReturnSlot's own note.
    /// ⚠ AND THE PARAGRAPH THIS REPLACES CONTRADICTED ITSELF INSIDE FOUR LINES:
    /// it called `NoTick` witnessed while calling `Clear` unobserved, when the
    /// only operator that ever writes `NoTick` is Clear's own stamp loop.
    ///
    /// PosAt NEVER THROWS ON A TICK: this is a combat path (Р378). Every
    /// question about a tick that it cannot answer has a DEFINED ANSWER
    /// instead -- see its own table, and the guard RULING 147 put in front of
    /// the indexing. A throw here would turn a body the server merely has no
    /// history for into a dropped tick for everyone in the match.
    ///
    /// ⛔ THE OTHER HALF OF THE ADDRESS IS NOT PROMISED FOR, AND THE PROMISE
    /// USED TO READ AS IF IT WERE (coordinator RULING 154, review finding
    /// A-1). `slot` is NOT range-checked, and on a matched stamp -- that is,
    /// on the ordinary rewound question -- it goes straight into the ring.
    /// A slot outside the arena's population has TWO failure modes, and the
    /// quiet one is the worse:
    ///   * it lands inside `_rows` anyway (a slot `d` past the end reads row
    ///     `t+1`, slot `d`) and the call returns TRUE with ANOTHER BODY'S
    ///     RECORD -- a plausible position under a raised FlagAlive, with
    ///     nothing to tell the caller it was answered about the wrong body;
    ///   * or it lands outside and throws, against the sentence above.
    /// ⇒ This is a CONTRACT, not a guard, and the neighbor it copies is
    /// RederiveOccupancy, which states the same one: slots are numbers this
    /// ring itself issued through RentSlot and the save carried back
    /// unchanged, never a value off a wire. A hand-built MobState/PlayerState
    /// pushed through a *ForTest seam breaks it, and that whole class of
    /// fixture is tracked as `app-41wd` rather than guarded here. Т25 is the
    /// task that made the sentence above capable of being false -- until it,
    /// the body was `record = default; return false;` and could neither throw
    /// nor lie -- so it is the task that has to say so.
    /// ⚠ `app-41wd`'s own description covers only the THROW; the silent-wrong-
    /// record branch is new, and the task is extended by it.
    internal sealed class PositionHistory
    {
        /// "This row belongs to no tick." Zero cannot serve as the sentinel:
        /// tick 0 is a legal tick of a match, and a fresh ring left at zero
        /// would claim to hold the truth about the opening tick of every
        /// raid. int.MinValue is unreachable for a tick counter that starts
        /// at 0 and only ever increments.
        const int NoTick = int.MinValue;

        /// "No row corresponds to this tick." Companion to NoTick, and it says
        /// the same thing one level down: NoTick marks a ROW that belongs to no
        /// tick, NoRow marks a TICK that can have no row. Both readers below
        /// need the second, because a tick before the match began indexes
        /// nothing at all.
        const int NoRow = -1;

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
        /// height gate reads the target's slide -- ProjectileSystem's own
        /// AcceptCandidate does it in the `overlapTop = sliding` line of its
        /// HitPlayer branch, and since app-88jb Т28 that `sliding` is THIS BIT
        /// on a rewound step and the live `SlideTimer > 0f` otherwise
        /// (RewoundBody is where the two meet) -- so a collector who was
        /// sliding a few ticks ago and is standing now would be tested against
        /// a STANDING profile, and the round that visibly went over their head
        /// would land.
        /// ⚠ CITED BY THE OPERATOR AND NOT BY A LINE NUMBER (coordinator RULING
        /// 210, the same discipline AcceptCandidate's own SlideProfileTop note
        /// already follows under Ruling 196): the number this sentence used to
        /// carry had gone stale by two hundred lines before anybody read it
        /// again, and a fresh number would only go stale the same way.
        /// The flag is what makes the rewound question ask about the body that
        /// was there, not the body that is.
        /// Invulnerability is the same argument at the other end of the
        /// window: HeroConfig.DashIframes is 0.2 s, which is EXACTLY 6 ticks
        /// (SimulationWorld.TicksFromSeconds(0.2f)) -- the rewind cap itself
        /// -- so a whole iframe window fits inside the deepest rewind, and
        /// reading invulnerability from the LIVE body would award a hit the
        /// victim had already dodged.
        ///
        /// THE CONSTRUCTOR ARRIVED WITH ITS FIRST CALLER (Т25), which is what
        /// Т24 deliberately waited for: while the only value this struct could
        /// hold was `default`, a constructor would have been production code
        /// no test could kill. Both callers live in this class -- Write builds
        /// the historical record, PosAt builds the degenerate one -- and
        /// nothing outside it ever constructs a Record, which is why the two
        /// fields stay readonly and there is no setter of any kind.
        public readonly struct Record
        {
            public readonly float2 Pos;
            public readonly byte Flags;

            public Record(float2 pos, byte flags)
            {
                Pos = pos;
                Flags = flags;
            }
        }

        public const byte FlagAlive = 1, FlagSliding = 2, FlagInvulnerable = 4;

        /// Writes the row for `tick` from the world's LIVE bodies. Called
        /// ONCE, at the END of TickAll (Т25) -- the row for tick T describes
        /// the world as T left it, which is what makes k == 0 mean "the live
        /// positions" in PosAt's table below.
        ///
        /// ⭐⭐ THE FLAG PREDICATES ARE COPIED FROM THE LIVE COMBAT PATH WORD
        /// FOR WORD, and that is the whole point of the flags existing (spec
        /// §3.6, finding C-I5). A rewound question has to be asked the SAME
        /// WAY the live one is, or the rewind quietly answers a different
        /// question:
        ///   * SLIDING is `SlideTimer > 0f`, which is what the height gate in
        ///     ProjectileSystem.AcceptCandidate tests to cap the target's
        ///     overlap at Hero.SlideProfileTop. Every other reader in the
        ///     simulation spells it the same way (Spread, WeaponSystem's muzzle
        ///     height, BodySeparation), so there is one predicate here, not a
        ///     second opinion.
        ///   * INVULNERABLE is `IframeTimer > 0f`, the positive form of
        ///     SimulationWorld.DamagePlayer's own `if (p.IframeTimer > 0f)
        ///     return;` guard. ProjectileSystem's HitPlayer arm asks the
        ///     opposite question ("does the blow arrive") and therefore reads
        ///     the answer inverted, as `!invulnerableThen`; the two are one
        ///     predicate read from two sides, and this is the side the record
        ///     needs.
        ///     ⭐ AND SINCE app-88jb Т28 THAT ARM FEEDS THIS VERY BIT INTO IT.
        ///     It hands RewoundBody the live `IframeTimer > 0f` as the
        ///     fallback and gets back either that or this flag, so the rewound
        ///     and the live question are literally one expression with two
        ///     sources -- which is what "asked the same way" was always meant
        ///     to mean.
        /// ⚠ THEY ARE READ AT DIFFERENT STAGES OF THE LIVE PIPELINE, and that
        /// asymmetry is real rather than an oversight here: the slide is a
        /// GATHER-time question (it decides whether the round can touch the
        /// body at all) while the i-frames are a DAMAGE-time one (the gather
        /// phase filters on Alive alone). The row carries both because a
        /// rewound shot has to reproduce both stages, not just the first.
        ///
        /// MOBS GET FlagAlive AND NOTHING ELSE, and neither omission is a
        /// shrug. A mob has no slide and no dash, so MobState carries neither
        /// SlideTimer nor IframeTimer -- there is no field to read, and a
        /// constant `false` would only be a longer way of leaving the bit
        /// clear. FlagAlive is set unconditionally rather than from a
        /// predicate, because `_mobs[0.._mobCount)` holds LIVE mobs by
        /// construction: DamageMob swap-removes a dead one out of that range in
        /// the same breath it returns its slot. A `Hp > 0f` test here would be
        /// a branch no test could ever kill, which is the shape HotTweakTests'
        /// own "no clamp with no witness" note argues against one struct over.
        ///
        /// COLLECTORS ARE WALKED WHOLE, DEAD ONES INCLUDED, and that is the
        /// opposite discipline for the opposite reason: `_players` is never
        /// compacted, so a dead collector keeps his row and this method keeps
        /// recording him with FlagAlive clear. That clear bit is exactly what
        /// PosAt's miss branch reads.
        ///
        /// NO ROW IS CLEARED BEFORE IT IS REWRITTEN, and it does not have to
        /// be: the stale content a row could hold is the tick `capacity` ticks
        /// ago, and every slot in it is either still occupied (this method
        /// overwrites it) or was released in between -- and ReturnSlot clears
        /// every row of a released slot at the moment of release, which is the
        /// invariant its own doc states ("a free slot holds nothing" at every
        /// instant). So a blank record in a written row means the slot was free
        /// this tick, and that is the truth rather than a leftover.
        ///
        /// `tick` IS NEVER NEGATIVE HERE. TickAll increments the counter before
        /// it runs and calls this on its last line, so the smallest value that
        /// can arrive is 1. PosAt guards against a negative tick because it
        /// takes one from a caller; this method takes one from the loop that
        /// produced it, and a guard would be an unreachable branch.
        public void Write(int tick, SimulationWorld w)
        {
            int rowIndex = RowIndex(tick);
            int row = RowBase(rowIndex);
            _rowTick[rowIndex] = tick;
            int playerCount = w.PlayerCount;
            for (int i = 0; i < playerCount; i++)
            {
                PlayerState p = w.PlayerAt(i);
                byte flags = 0;
                if (p.Alive) flags |= FlagAlive;
                if (p.SlideTimer > 0f) flags |= FlagSliding;
                if (p.IframeTimer > 0f) flags |= FlagInvulnerable;
                _rows[row + p.HistorySlot] = new Record(p.Pos, flags);
            }
            int mobCount = w.MobCount;
            for (int i = 0; i < mobCount; i++)
                _rows[row + w.Mobs[i].HistorySlot] = new Record(w.Mobs[i].Pos, FlagAlive);
        }

        /// ⛔ THE NEGATIVE-TICK GUARD IS FIRST, BEFORE ANY INDEXING (coordinator
        /// RULING 147, and it exists because of a defect the Т25 executor found
        /// in the contract rather than in the code). Spec §3.6 gives the
        /// projectile step an explicit `int historyTick` with `-1` meaning "the
        /// present", and in C# `-1 % 7 == -1`: an index computed before the
        /// guard would reach into the ring at a negative offset and throw,
        /// which is precisely the promise this class's header makes in capitals.
        /// The caller (Т27/Т28) is contracted never to pass `-1` down here --
        /// it branches on the sentinel itself and reads the live body -- but
        /// Р378 is unconditional, so surviving it is not optional.
        /// ⚠ A NEGATIVE TICK IS ANSWERED, NOT REJECTED, and it lands in the
        /// degenerate branch rather than in a fourth case of its own: a tick
        /// before the match began is "no row exists for this question" in its
        /// purest form, which is the same sentence the second line of the table
        /// already answers.
        /// ⛔ AND NOT BY A "SMART" MODULUS. `((tick % c) + c) % c` would also
        /// stop the throw, and it would be wrong: it would map tick -1 onto the
        /// row of tick c-1 and answer a question about a tick that never
        /// happened with somebody's real recorded position. The guard states a
        /// fact about the domain; the modulus would state a fact about the sign
        /// semantics of an operator.
        ///
        /// THE DEGENERATE BRANCH CANNOT TELL THE TRUTH ABOUT THE OTHER TWO
        /// FLAGS, and says so instead of guessing. It hands back `currentPos`
        /// with FlagAlive alone, because the signature takes a position and not
        /// a body: whether the target is sliding or invulnerable RIGHT NOW is
        /// known to the caller, who holds the live struct, and is not knowable
        /// here. A caller that has fallen into this branch must read
        /// SlideTimer/IframeTimer off the live body exactly as the un-rewound
        /// path does -- silence here would be finding C-I5 in reverse, a
        /// rewound question answered against a wrong profile.
        /// ⭐ AND `fromHistory` IS HOW A CALLER KNOWS IT IS IN THAT BRANCH
        /// (app-88jb Т28, coordinator RULING 204). Until this task the sentence
        /// above asked for something no caller could do. The degenerate answer
        /// is `true` plus a record carrying FlagAlive alone, and that is BYTE
        /// FOR BYTE what a real row holds for a collector who was alive,
        /// standing and vulnerable -- so the branch that KNOWS NOTHING about
        /// the other two flags and the branch that knows them to be clear were
        /// indistinguishable. This out parameter is the missing half of that
        /// contract and nothing more: `true` means the answer came out of a
        /// written row, `false` means it is the caller's own `currentPos`
        /// handed straight back and the caller owes the live body a look.
        /// ⛔ IT IS NOT INFERABLE FROM THE VALUE, which is why it is a
        /// parameter rather than a note: `record.Pos == currentPos` holds for a
        /// genuine row too, whenever the body did not move.
        /// ⚠ IT IS A SEPARATE ANSWER FROM THE RETURN VALUE, whose own meaning
        /// is untouched -- "was the target alive at that moment". The two
        /// combine in three ways: `false` is a MISS, `true` with `fromHistory`
        /// is the rewound picture, and `true` without it is "no rewind is
        /// available here, ask the live body". Only the degenerate branch
        /// produces the third.
        ///
        /// | Case                            | Answer                          | fromHistory |
        /// | tick < 0                        | the CURRENT position (see above)| false       |
        /// | Record present, Alive           | the historical position/flags   | true        |
        /// | Row's Tick does not match       | the CURRENT position -- degrades| false       |
        /// |   (body did not live that tick, |   into "no rewind at all"       |             |
        /// |    first ticks of the match)    |                                 |             |
        /// | Record present, Alive cleared   | MISS: the target was dead then  | true        |
        /// | k == 0                          | live positions (the row for T is| false       |
        /// |                                 |   written at the END of TickAll)|             |
        ///
        /// ⚠ THE `k == 0` LINE IS EXECUTED BY THE DEGENERATE BRANCH, not by a
        /// branch of its own: the row for tick T is written on TickAll's last
        /// line, so a round fired in the weapon phase of tick T asks about a
        /// stamp the ring has not written yet, misses it, and is answered with
        /// the live positions -- which is what that line of the table promises.
        /// It is a `fromHistory == false` answer for the same reason, and a
        /// caller reading flags off that record would be reading invented ones.
        public bool PosAt(int slot, int tick, float2 currentPos, out Record record,
            out bool fromHistory)
        {
            int rowIndex = tick < 0 ? NoRow : RowIndex(tick);
            if (rowIndex == NoRow || _rowTick[rowIndex] != tick)
            {
                record = new Record(currentPos, FlagAlive);
                fromHistory = false;
                return true;
            }
            record = _rows[RowBase(rowIndex) + slot];
            fromHistory = true;
            return (record.Flags & FlagAlive) != 0;
        }

        /// Folds the whole window into the digest (coordinator RULING 143),
        /// called once by SimulationWorld.StateHash at the canonical position
        /// between the container slots and the waves -- the same position the
        /// rows hold in WorldSave, because that class's own doc promises the
        /// three orders match so a gap is visible by position.
        ///
        /// THE ARITHMETIC STAYS IN HERE (rule 2). The world hands over itself
        /// and gets a digest back; `row * _maxBodies + slot` has exactly one
        /// home, and neither StateHash nor SaveState ever indexes a row.
        ///
        /// WHAT IS FOLDED PER TICK IS `n`, THE NUMBER OF RECORDS THAT TICK
        /// CONTRIBUTES -- zero when the ring holds no row for it, PlayerCount +
        /// MobCount when it does -- and then, only if n > 0, the records
        /// themselves in the world's own canonical order.
        ///
        /// ⛔ THE STEP IS KEPT BECAUSE Р409 SPELLS THE FORM OUT, NOT BECAUSE IT
        /// DISCRIMINATES, and the honest version of that is worth more than the
        /// argument that stood here (review finding A-3). The argument claimed
        /// an unreachable configuration -- "the row for tick 5 is missing and
        /// the one for tick 6 is full". Rows are written EVERY tick from the
        /// first, and the stamp is compared for exact equality, so presence is
        /// MONOTONE across the window: a tick is present exactly when
        /// `1 <= t <= _tick` and `t > _tick - capacity`. That makes presence a
        /// pure function of `_tick` -- which StateHash folds as its FIRST step,
        /// before this one runs. By the Р114 test the ruling itself invokes to
        /// reject the stamp, the discriminating power of THE WHOLE `n` STEP is
        /// therefore zero, not merely the difference between folding an int and
        /// folding a bool.
        /// ⇒ Two consequences, both stated rather than left for the next
        /// reader. The step costs `capacity` folds per digest and buys nothing
        /// measurable; and a mutation that replaces it with anything of equal
        /// arity survives the suite by construction, so no test here can be
        /// pointed at as its witness. It stays because the plan's form is not
        /// this task's to rewrite, and StateHash's own "a length goes in first"
        /// convention reads consistently with it.
        /// ⚠ NEITHER THE STAMP NOR THE OCCUPANCY COUNT IS FOLDED, and the
        /// precedent is Р114/Т16, recorded in StateHash's own doc: the separate
        /// `statsCount` step was REMOVED because `_matchStats.Length` equals
        /// `_players.Length` by construction, so "hashing it a second time added
        /// a constant with no discriminating power".
        /// ⛔ THE STAMP MEETS THAT TEST BY COMPOSITION, NOT BY BEING CONSTANT,
        /// and the difference matters enough to spell out (coordinator ruling
        /// 152). It would be wrong to say two worlds on one tick carry the same
        /// stamps "because they are the last `capacity` tick numbers": on tick 3
        /// the rows for 1, 2 and 3 carry their own numbers while the other four
        /// still carry NoTick, and the ring only holds a full run of the last
        /// `capacity` numbers from tick `capacity` onward. What is true is that
        /// a stamp is a function of exactly two things already in the digest --
        /// `_tick`, which StateHash folds as its FIRST step, and whether the row
        /// was ever written, which is precisely what the zero in `n` carries.
        /// So the stamp adds nothing ON TOP OF what has been folded, which is
        /// the Р114 condition; it is not a constant, and the first `capacity`
        /// ticks of every match are the counterexample.
        /// Occupancy fails the same test for the plainer reason: it is derivable
        /// from bodies that are in the digest already.
        ///
        /// THE WALK IS FIXED WIDTH -- always `capacity` steps, never a function
        /// of the tick. On tick 3 the window runs from -3, and those four steps
        /// fold a zero each without touching the ring: a tick from before the
        /// match contributed no records, and that is the honest number.
        /// ⚠ WHAT THE FIXED WIDTH DOES AND DOES NOT BUY IN SAFETY (review
        /// finding A-2/B-6, which caught this claim promising more than it
        /// proves). It buys ONE case: rows are indexed only for ticks the ring
        /// actually wrote, so a world that has NEVER TICKED reaches no row at
        /// all, and the reflective sweeps that push a nonsense HistorySlot
        /// through a *ForTest seam and then hash are safe for that reason and
        /// no other. It does NOT buy the general case: a world that HAS ticked
        /// and then receives a body with a slot outside the population will
        /// index with it, exactly as PosAt would, and StateHash is called once
        /// per tick on the dev path. That is the same `app-41wd` exposure the
        /// class header states for PosAt, in the other reader, and it is a
        /// contract rather than a guard for the same reasons.
        ///
        /// COST IS BOUNDED BY POPULATION, NOT BY CAPACITY (RULING 144).
        /// Walking every slot of every row would be capacity * _maxBodies
        /// records per call whatever the arena held; walking the live bodies is
        /// capacity * (collectors + mobs), and StateHash is called once per tick on
        /// the battle path (LocalSimBackend hands it to onTick), so the
        /// difference is not academic.
        public ulong Fold(ulong h, SimulationWorld w)
        {
            int tick = w.CurrentTick;
            int playerCount = w.PlayerCount, mobCount = w.MobCount;
            for (int t = tick - (_capacityTicks - 1); t <= tick; t++)
            {
                int rowIndex = t < 0 ? NoRow : RowIndex(t);
                bool present = rowIndex != NoRow && _rowTick[rowIndex] == t;
                int n = present ? playerCount + mobCount : 0;
                h = StateHash64.Add(h, n);
                if (n == 0) continue;

                int row = RowBase(rowIndex);
                for (int i = 0; i < playerCount; i++)
                    h = FoldRecord(h, in _rows[row + w.PlayerAt(i).HistorySlot]);
                for (int i = 0; i < mobCount; i++)
                    h = FoldRecord(h, in _rows[row + w.Mobs[i].HistorySlot]);
            }
            return h;
        }

        /// Flags fold as `int` rather than as a byte: StateHash64 has no byte
        /// overload, and the container-slot walk in StateHash already widens
        /// its bytes the same way.
        static ulong FoldRecord(ulong h, in Record r)
        {
            h = StateHash64.Add(h, r.Pos);
            return StateHash64.Add(h, (int)r.Flags);
        }

        /// Deep-copies both halves of the ring into the save (coordinator
        /// RULING 146), at the canonical position between the container slots
        /// and the waves.
        ///
        /// BOTH HALVES, AND THAT IS THE WHOLE POINT. Rows without stamps -- or
        /// stamps without rows -- would be a ring that answers historical
        /// positions to questions about the wrong ticks. They travel together
        /// or the restore is worse than no restore at all.
        /// ⚠ THE OCCUPANCY SET DELIBERATELY DOES NOT TRAVEL. It is an index
        /// over HistorySlot, which the save already carries on the bodies, so
        /// RestoreState re-derives it (RederiveOccupancy) instead. Copying it
        /// here would put a second version of one fact into the save, which is
        /// exactly the drift RULING 133 removed.
        /// ⚠ AND THE COPY IS DEEP. Handing over `_rows` itself would give the
        /// save a REFERENCE into the live ring and every later tick would
        /// rewrite the snapshot underneath its holder -- the same defect
        /// SaveState's own Waves comment describes in as many words.
        ///
        /// The arrays are allocated HERE rather than in SaveState's initializer
        /// because their sizes are this class's own two dimensions, and handing
        /// those out would give the ring's shape a second home.
        public void SaveTo(WorldSave save)
        {
            save.HistoryRows = new Record[_rows.Length];
            save.HistoryRowTicks = new int[_rowTick.Length];
            System.Array.Copy(_rows, save.HistoryRows, _rows.Length);
            System.Array.Copy(_rowTick, save.HistoryRowTicks, _rowTick.Length);
        }

        /// The other half of SaveTo's no-aliasing contract: the LIVE arrays are
        /// filled from the save, never replaced by it, so the world keeps
        /// writing into its own ring after a restore instead of into the
        /// snapshot's.
        ///
        /// NO LENGTH CROSS-CHECK, on the settled precedent of every other
        /// entity array in RestoreState. Mobs, Projectiles, Pickups and
        /// Containers all restore by straight Array.Copy with no guard of their
        /// own, on the "same immutable-topology reasoning" their own comments
        /// give: both of this ring's dimensions come from Arena caps
        /// (RewindCapTicks, MaxMobs + MaxPlayers) that ArenaTopologyMatches
        /// refuses to hot-tweak. A guard here would be production code with no
        /// test able to turn it red.
        public void RestoreFrom(WorldSave save)
        {
            System.Array.Copy(save.HistoryRows, _rows, _rows.Length);
            System.Array.Copy(save.HistoryRowTicks, _rowTick, _rowTick.Length);
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
            // ⚠ NO TEST OBSERVES THIS, AND THE REASON IS NO LONGER THE ONE
            // Т24 GAVE. That note said every row was `default` because nothing
            // wrote one; from Т25 rows ARE written, and deleting this call
            // would leave a dead tenant's positions standing under stamps the
            // ring still considers current -- a live body would be found where
            // the corpse stood. What keeps it unobserved is narrower now: no
            // fixture in the suite kills a body, hands its slot to another and
            // then READS the row, and reading a row is the only way the
            // difference can surface. In particular
            // DeadBodysSlot_IsReused_ButNotItsPast is still NOT the witness its
            // own name suggests: its only past-tense assertion reads
            // MobState.Pos, which SpawnMob assigns directly and which this call
            // has no hand in.
            // ⭐ AND Write NOW DEPENDS ON THIS CLEAR. It rewrites a row without
            // blanking it first, which is only correct because a released slot
            // was emptied here at the moment of release. The two are one
            // mechanism read from two ends.
            //
            // ⚠ THE PRICE, NAMED RATHER THAN DISCOVERED LATER: this erases the
            // dead body's past RETROACTIVELY, so from the moment it dies no
            // rewind can reach it -- PosAt would find a blank record under a
            // stamp that matches and report a miss. That is correct rather
            // than merely tolerable, and the reason is upstream: the gather
            // phase offers only bodies that are on the arena NOW
            // (`_mobs[0.._mobCount)`, which a death swap-removes from), so
            // nothing ever asks this ring where a corpse used to be. A shot
            // cannot hit a body that no longer exists, whatever the rewind
            // would have said about it.
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
        /// ⚠ IT DOES NOT TOUCH THE ROWS, AND NO LONGER HAS TO. Т24's note here
        /// warned that a slot freed by the restore could still hold rows
        /// written by the future that was just rolled back; from Т25 that
        /// future is gone with the rest of it, because RestoreFrom copies both
        /// halves of the ring back out of the save before anything reads them.
        /// The division of labor is what stays: the rows come FROM the save,
        /// the occupancy is DERIVED from the bodies the save restored, and
        /// neither is a second copy of the other.
        /// The slots it reads are NOT range-checked. That is a contract this
        /// doc states here, not one borrowed from a neighbor: they are
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

        /// THE RING'S TWO INDEX FORMULAS, EACH WRITTEN ONCE (coordinator RULING
        /// 156, review finding A-7). Before the fix-round the layout arithmetic
        /// stood spelled out in three methods and the wrap was computed twice in
        /// a row inside PosAt. The layer was never wrong -- the review's own
        /// answer to "is there a second home" was no, and neither formula ever
        /// left this class -- but three spellings of one formula INSIDE one
        /// class is the same defect rulings 7, 16 and 121 closed BETWEEN
        /// classes, and it is cheaper to close than to keep in step.
        ///
        /// SPLIT IN TWO RATHER THAN ONE, and that is a deliberate refinement of
        /// the ruling's own wording. A single `RowBase(int tick)` cannot serve
        /// both readers without computing the wrap twice -- `_rowTick` is keyed
        /// by the row INDEX and `_rows` by the row BASE -- so removing the
        /// double modulo, which the same ruling asks for, needs the index to be
        /// nameable on its own. Each formula still has exactly one home.
        ///
        /// NEITHER TAKES A NEGATIVE TICK. Both readers resolve that case into
        /// NoRow before calling, at their own moment and for their own stated
        /// reason, so a third check in here would be a branch neither of them
        /// could reach. ClearRowsOf below walks rows by index directly and
        /// needs no wrap at all: it visits every row of one slot.
        int RowIndex(int tick) => tick % _capacityTicks;
        int RowBase(int rowIndex) => rowIndex * _maxBodies;

        void ClearRowsOf(int slot)
        {
            for (int t = 0; t < _capacityTicks; t++) _rows[RowBase(t) + slot] = default;
        }
    }
}
