using Ring.Networking.Protocol;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// A struck mob's TILT, rebuilt on a networked client from the hit event
    /// (app-88jb Т31, owner decision of 2026-09-02 variant "б", coordinator
    /// Rulings 255/258/260/261).
    ///
    /// WHY ANYTHING HAS TO REBUILD IT. `MobState.Tilt` never rides the wire:
    /// `SnapshotBlocks.MobRecord` is nine bytes and carries `Id/Type/Ai/Pos/
    /// Hp` (Р383), so `NetworkSimBackend.ReadMobs` builds every mob with the
    /// pair at zero. Offline the same field is AUTHORITATIVE — `RenderSnapshot
    /// .Mobs` copies `MobState` whole — and `MobVisual.Sync` draws it either
    /// way. So until this class existed, a networked client saw bodies that
    /// took a round without moving: no lean, and a `Downed` mob that went over
    /// with no visible fall.
    ///
    /// WHY THE BACKEND AND NOT THE VIEW, which is the one design decision here
    /// worth arguing (and the owner's, not this file's). `ProjectileHit`
    /// reaches `ViewRegistry.HandleEvent` on BOTH paths — offline out of the
    /// world's own event buffer, over the wire out of `DrainDueEvents` — so an
    /// integrator living in `MobVisual` and fed by that event would DOUBLE the
    /// offline tilt: the authoritative scalar plus a reconstructed one, in a
    /// component with no way to tell which path it is on. Synthesizing the
    /// field into the published pair instead leaves exactly one number on
    /// screen on both paths, and `MobVisual`/`ViewRegistry` keep reading the
    /// one field they already read. The precedents are this backend's own:
    /// `ApplyOwnPlayer` pastes the locally PREDICTED `PlayerState` into both
    /// halves of the pair, and `RestoreMobType`/`MobTypeMemory` put back a
    /// field the wire drops from state this side already holds. Presentation
    /// is also the wrong assembly for the arithmetic: the speed scale a blow
    /// is sized by lives in `SnapshotEvents.SpeedCapFor`, and
    /// `Presentation.asmdef` deliberately references no networking assembly at
    /// all.
    ///
    /// THE STEP IS A TICK, NEVER A FRAME (Ruling 251). `Impact.SpringStep` is
    /// semi-implicit Euler, and its discrete damping term `c*dt` is part of the
    /// answer rather than an artifact of it: `Impact.PeakTilt`'s own measured
    /// note puts the chaser headshot peak at 0.586 rad through the integrator
    /// at dt = 1/30 against 0.789 through the corrected closed form. Stepped
    /// at a frame's delta the same impulse would peak somewhere else entirely,
    /// and the client would show a different blow from the one the server
    /// resolved. So `StepTicks` is driven by the RENDER CLOCK'S OWN TICK
    /// ADVANCE — the number `NetworkSimBackend.Advance` already computes and
    /// returns — with `SimulationWorld.TickDt` as the step, and the witness is
    /// the authoritative world tick for tick (`MobTiltIntegratorTests`).
    ///
    /// TWO WAYS AN IMPULSE IS MISSED, AND BOTH LEAVE THE BODY UPRIGHT
    /// (Ruling 261). The archetype's numbers are what the moment is built
    /// from, and this side can only get them by asking `MobTypeMemory` for the
    /// victim's archetype — which answers from the last two frames' rosters.
    /// It answers false for a mob that DIED ON THIS BLOW (the frame reporting
    /// the hit is already the frame that no longer lists it, and the event is
    /// shown `NetConfig.InterpBufferTicks` later still), and for a mob that
    /// LEFT THIS CLIENT'S VIEW inside that same buffer — fog of war or a
    /// truncated frame — even though it is alive and tilting on the server.
    /// Neither gets an impulse, and the honest reason is the same one
    /// `SimulationWorld.DamageMob` states for not branching on death: "a body
    /// that dies on this blow shows its tilt to nobody". Guessing an archetype
    /// would be worse than missing one, because `MobType`'s zero is `Chaser`
    /// — a REAL archetype, whose mass and gain would produce a confident,
    /// wrong lean on a body that is actually an Elite.
    ///
    /// PREALLOCATED, NO DICTIONARY, REFUSALS RATHER THAN THROWS. Four parallel
    /// arrays of `Arena.MaxMobs` and a linear scan over the OCCUPIED prefix —
    /// the shape `MobTypeMemory` and `TracerProjectiles` already keep, for
    /// their reason: this is touched from the snapshot receive path and from
    /// the render frame, and a dictionary would allocate as it grew on paths
    /// that must not allocate (Р406, `AllocationTests`). A full table refuses
    /// by value (`Apply` returns false) rather than throwing, because the
    /// caller runs inside FishNet's batched parsing loop where a throw
    /// abandons every message behind it (Р82/195); the cost of a refusal is
    /// one body that does not rock. Nothing here allocates after the
    /// constructor.
    ///
    /// NOT A SEVENTH SEAM. `ClientMatchReset` owns the six per-match objects
    /// and its own doc argues for one call site rather than six; this table is
    /// the BACKEND's, keyed by entity ids a new match mints from 1 again, so
    /// it is cleared where that backend already observes the epoch change —
    /// beside `MobTypeMemory.Reset`, which it is keyed the same way as.
    public sealed class MobTiltIntegrator
    {
        readonly int[] _ids;
        readonly MobType[] _types;
        readonly float[] _tilt;
        readonly float[] _tiltVel;

        /// How far the render clock may jump forward before stepping the
        /// spring stops being worth it — three settle times, in ticks, over
        /// the SLOWEST of the four archetypes. It is the bound
        /// `Impact.PeakTilt` uses for its own loop, and it is a BOUND rather
        /// than a tuned quantity: every impulse this table can hold has
        /// snapped to rest through `Impact.RestEpsilon` well inside it (the
        /// chaser's does at roughly two thirds of the window), so a jump wider
        /// than this can only end with every slot at zero, and `Reset` is that
        /// answer arrived at in one step instead of hundreds.
        readonly int _settleWindowTicks;

        int _count;

        /// Capacity is `Arena.MaxMobs`, floored at 1 — the shape
        /// `MobTypeMemory`'s own constructor keeps, and for its reason: a
        /// zero-width memory remembers nothing, which is a worse answer than
        /// a small one. The window is floored at one tick for the same kind of
        /// reason: a config with no settle time at all would otherwise make
        /// every ordinary one-tick advance a full reset.
        public MobTiltIntegrator(in SimConfig cfg)
        {
            int capacity = math.max(1, cfg.Arena.MaxMobs);
            _ids = new int[capacity];
            _types = new MobType[capacity];
            _tilt = new float[capacity];
            _tiltVel = new float[capacity];

            float slowestSettle = math.max(
                math.max(cfg.Chaser.TiltSettleSeconds, cfg.Gunner.TiltSettleSeconds),
                math.max(cfg.Elite.TiltSettleSeconds, cfg.Director.TiltSettleSeconds));
            _settleWindowTicks = math.max(1,
                (int)math.ceil(3f * slowestSettle / SimulationWorld.TickDt));
        }

        /// How many mobs are tilting right now.
        public int Count => _count;

        /// Takes one blow's moment for `mobId`, SUMMING into whatever the slot
        /// already carries — which is what `SimulationWorld.DamageMob` does
        /// (`TiltVel +=`), so a body hit twice inside one tick rocks harder
        /// rather than rocking once. `false` means the table was full and this
        /// blow is not shown; see the class doc on why that is a value.
        ///
        /// The archetype of an OCCUPIED slot is not rewritten: one id is one
        /// body for the length of a match, and a new match resets the table
        /// before it can mint the id again.
        public bool Apply(int mobId, MobType type, float angularImpulse)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_ids[i] != mobId) continue;
                _tiltVel[i] += angularImpulse;
                return true;
            }

            if (_count >= _ids.Length) return false;

            _ids[_count] = mobId;
            _types[_count] = type;
            _tilt[_count] = 0f;
            _tiltVel[_count] = angularImpulse;
            _count++;
            return true;
        }

        /// Walks every tilting mob `ticks` steps of its own archetype's
        /// spring, and frees the slots that came to rest.
        ///
        /// `ticks <= 0` DOES NOTHING AT ALL — neither steps nor resets. The
        /// caller's number is `renderTick - _lastRenderTick`, floored at zero
        /// by `Advance` itself, so zero is the ordinary case of two frames
        /// inside one tick; a clock that went BACKWARDS has not made the
        /// bodies swing backwards either, and forgetting them would be a
        /// stronger claim than the clock made (Ruling 260).
        ///
        /// A JUMP PAST THE SETTLE WINDOW RESETS instead of stepping: see
        /// `_settleWindowTicks`. The loop is finite by construction in both
        /// branches, with no convergence test and no tail that can run away.
        public void StepTicks(int ticks, in SimConfig cfg)
        {
            if (ticks <= 0) return;
            if (ticks > _settleWindowTicks)
            {
                Reset();
                return;
            }

            float dt = SimulationWorld.TickDt;
            for (int step = 0; step < ticks; step++)
            {
                int i = 0;
                while (i < _count)
                {
                    // ONE HOME FOR THE ARCHETYPE SWITCH (Ruling 259), by
                    // reference rather than by value: `MobSimConfig` is a
                    // fifteen-field struct and this is the inner loop.
                    ref readonly MobSimConfig target = ref SimConfig.MobConfigFor(in cfg, _types[i]);
                    Impact.SpringStep(ref _tilt[i], ref _tiltVel[i],
                        target.TiltDampingRatio, target.TiltSettleSeconds, dt);

                    // THE SNAP IS WHAT FREES THE SLOT. `Impact.SpringStep`
                    // zeroes both numbers exactly once they are inside its own
                    // `RestEpsilon` — an exponential never reaches zero on its
                    // own — so this is a test for "the step said it is over",
                    // never a tolerance of this file's choosing. Without it
                    // the table fills with bodies that stopped moving a match
                    // ago and refuses the next real blow.
                    if (_tilt[i] == 0f && _tiltVel[i] == 0f)
                    {
                        RemoveAt(i);
                        // NO `i++` HERE: the swap-remove just moved the LAST
                        // slot into this index, and that body has not been
                        // stepped on this pass yet.
                        continue;
                    }

                    i++;
                }
            }
        }

        /// Patches `.Tilt`/`.TiltVel` of the mobs it knows into `mobs`,
        /// matched BY ID — never by index, because the published pair's order
        /// is the frame's and this table's is the order blows landed in.
        ///
        /// TWO NESTED SCANS, AND THE PRICE IS NAMED RATHER THAN HIDDEN:
        /// `Count * count` integer comparisons once per frame. The first
        /// factor is the number of bodies still rocking — a handful in a
        /// firefight, and bounded by how many rounds land inside one settle
        /// time — while the second is what this client can see. Ten tilting
        /// bodies against three hundred visible is three thousand comparisons
        /// a frame, on the same path that already walks every mob to write it
        /// into the pair.
        public void WriteInto(MobState[] mobs, int count)
        {
            if (mobs == null) return;
            if (count > mobs.Length) count = mobs.Length;

            for (int i = 0; i < _count; i++)
            {
                int id = _ids[i];
                for (int j = 0; j < count; j++)
                {
                    if (mobs[j].Id != id) continue;
                    mobs[j].Tilt = _tilt[i];
                    mobs[j].TiltVel = _tiltVel[i];
                    break;
                }
            }
        }

        /// The pair this mob is at, or `false` if it is not tilting.
        public bool TryGetTilt(int mobId, out float tilt, out float tiltVel)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_ids[i] != mobId) continue;
                tilt = _tilt[i];
                tiltVel = _tiltVel[i];
                return true;
            }

            tilt = 0f;
            tiltVel = 0f;
            return false;
        }

        /// A new match: nothing from the one before may keep swinging. The ids
        /// are the point — a new match mints them from 1 again, so a surviving
        /// slot would tilt a DIFFERENT body, which is a wrong answer rather
        /// than a missing one (the same argument `MobTypeMemory.Reset` makes
        /// about its own keys).
        public void Reset()
        {
            System.Array.Clear(_ids, 0, _ids.Length);
            System.Array.Clear(_types, 0, _types.Length);
            System.Array.Clear(_tilt, 0, _tilt.Length);
            System.Array.Clear(_tiltVel, 0, _tiltVel.Length);
            _count = 0;
        }

        /// The blow's moment for a mob, from the fields the wire and the
        /// tracer can give: the SAME pair of calls `SimulationWorld.DamageMob`
        /// makes, in the same order and with the same `damping: 1f` — a mob
        /// has no cocoon, which is the collector's divisor and belongs to the
        /// blow that lands on HIM. Written as two calls into `Impact` rather
        /// than as arithmetic here for that class's own stated reason: the
        /// signed arm is the half that silently flips.
        ///
        /// THE SPEED IS THE OWNER'S CONFIGURED CAP, NOT THE ROUND'S SPEED AT
        /// CONTACT, and that is a priced approximation rather than an
        /// oversight: the wire carries no speed on this ending (eight bytes is
        /// the catalog's ceiling), so the only speed available is the one
        /// `SnapshotEvents.SpeedCapFor` answers for the shooter's own rail.
        /// For a round that RICOCHETED on its way in, the server resolved the
        /// blow at a damped speed and this side sizes it at the full one — so
        /// the rebuilt lean is overstated by the reciprocal of the retention
        /// the round had spent, while the raw term stays under the archetype's
        /// `ImpactSpeedCap`. Accepted at plan cost C-M3: it costs a reflected
        /// hit some visual honesty and it decides no outcome (CR 3).
        ///
        /// `ownerIndex` IS WORTH DIGGING OUT OF THE TRACER, which is what
        /// `NetworkSimBackend.RestoreShooter` does before this is ever called:
        /// `Impact.ProjectileMassFor` and `SnapshotEvents.SpeedCapFor` BOTH
        /// fork on this byte, so a collector's own shot mistaken for a mob's
        /// rebuilds a blow several times weaker than the one that landed.
        public static float AngularImpulseFor(byte ownerIndex, in MobSimConfig target,
            float hitHeight, in SimConfig cfg)
        {
            float dv = Impact.VelocityDelta(
                Impact.ProjectileMassFor(ownerIndex, in cfg),
                SnapshotEvents.SpeedCapFor(ownerIndex, in cfg),
                target.Mass, target.ImpactSpeedCap, damping: 1f);
            return Impact.AngularImpulse(hitHeight, target.CenterOfMassHeight, dv,
                target.TiltGain);
        }

        /// Swap-remove, the shape `TracerProjectiles.Prune` uses: order
        /// carries no meaning here (every reader looks a body up by id), so
        /// removal stays O(1) on a path that runs every tick. The vacated tail
        /// is left as it lies — nothing reads past `_count`, and `Reset`
        /// clears the whole table anyway.
        void RemoveAt(int index)
        {
            _count--;
            _ids[index] = _ids[_count];
            _types[index] = _types[_count];
            _tilt[index] = _tilt[_count];
            _tiltVel[index] = _tiltVel[_count];
        }
    }
}
