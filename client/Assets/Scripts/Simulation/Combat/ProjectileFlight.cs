using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// The ONE physical home of "advance one round by one tick against the
    /// arena's STATIC geometry" (app-88jb Т18, decisions Р354/Р384). PUBLIC on
    /// purpose: the client's tracer (Т32) has to crank the SAME function --
    /// with ricochet (Т19) there is no closed form left to reproduce, and a
    /// second flight model in the presentation layer has nothing to check
    /// itself against (NetworkSimBackend's own doc refuses exactly that).
    /// Ring.Networking already references Ring.Simulation in its asmdef; no
    /// InternalsVisibleTo is added.
    ///
    /// THREE THINGS THIS CLASS DELIBERATELY DOES NOT DO, and together they are
    /// the whole of its contract (coordinator Ruling 91):
    ///
    ///  - IT PICKS NO WINNER. The three static candidates travel back side by
    ///    side, each behind its own flag, and the single canonical min-scan
    ///    stays in ProjectileSystem, where the bodies are. Collapsing them
    ///    into one candidate here would break the packing order that scan's
    ///    tie-break IS: the floor is packed AFTER mobs and players, so a
    ///    barrier/mob/player tie at the same t outranks it, while the interior
    ///    barrier and the ring boundary are packed BEFORE them.
    ///
    ///  - IT REFUSES NOTHING. The interior barrier's height gate lives in
    ///    ProjectileSystem.AcceptCandidate, because a refusal sends the scan
    ///    back over the candidates that are LEFT -- a decision only the owner
    ///    of the scratch array can make. Handing back one candidate that stood
    ///    for a barrier and the ring boundary together would throw the
    ///    boundary away along with the obstacle a high round legitimately
    ///    cleared, and BarrierHeightTests.RejectedInteriorBarrier_LeavesThe
    ///    RingBoundaryStanding states that failure in its own words.
    ///
    ///  - IT MUTATES NOTHING. The round arrives by `in`, never `ref`: its own
    ///    advance -- Pos, PrevPos, PrevHeight, Height and the TTL -- stays in
    ///    ProjectileSystem.Update. That is not tidiness, it is arithmetic:
    ///    AcceptCandidate gates the interior barrier on the round's PRE-STEP
    ///    Height, and the advance happens at all only in the branch where
    ///    nothing was hit. It is also what every neighboring doc already says
    ///    about the tick (TtlDecay's "decrements at the top of the movement
    ///    step and tests the result at the BOTTOM", PickupSystem's two
    ///    idiom notes about that same loop).
    public static class ProjectileFlight
    {
        /// The static geometry one tick of flight meets, reported as THREE
        /// SEPARATE candidates because that is what the canonical packing
        /// order needs (see the type's own doc above). None of them is chosen
        /// and none is refused; a flag says whether each one is there at all,
        /// which is exactly the `bool` the two Geometry solvers below already
        /// answer with. There is no `Kind` field for the same reason: with no
        /// winner to name, one would only invite a second home for
        /// ProjectileSystem's own kind table.
        ///
        /// struct, never a class: allocations are forbidden on this path
        /// (AllocationTests.SaturatedTrio_TicksWithoutAllocations and
        /// Tick_DoesNotAllocateGC both tick live rounds through it).
        ///
        /// ONLY THE BARRIER CARRIES A NORMAL, and that is not an omission: it
        /// is the one of the three whose normal has a reader. The ring
        /// boundary's is re-derived from the contact point by
        /// Geometry.RingWallNormal at that same call site, and the floor has
        /// no modelled normal at all -- its event carries exactly zero. An
        /// `out` nobody reads is worse than none at all (Ruling 73's own
        /// wording, HitZones.cs).
        public readonly struct StepResult
        {
            /// Where the round's center stands at the end of the step. The
            /// caller lerps [start, Target] for every contact point it
            /// reports, sweeps the bodies along that same segment, and assigns
            /// it to Pos in the branch where nothing was hit.
            public readonly float2 Target;

            /// Nearest INTERIOR barrier -- an obstacle circle, a stadium wall
            /// or a zone-wall arc, all three through the one SweepArena call
            /// below and all three under the one Arena.BarrierTop.
            /// `BarrierT`/`BarrierNormal` mean nothing unless `HasBarrier`.
            public readonly bool HasBarrier;
            public readonly float BarrierT;
            public readonly float2 BarrierNormal;

            /// The arena's outer boundary, answered SEPARATELY from the
            /// interior barriers since Stage 2 Task 46: only the interior ones
            /// have a modelled top, so the two can never share a slot.
            /// `RingWallT` means nothing unless `HasRingWall`.
            public readonly bool HasRingWall;
            public readonly float RingWallT;

            /// The ground, crossed only by a descending round and only when
            /// the crossing falls inside THIS step. `FloorT` means nothing
            /// unless `HasFloor`.
            public readonly bool HasFloor;
            public readonly float FloorT;

            /// internal, so `Step` below is the one place a result is ever
            /// built: the tracer that reads this type from another assembly
            /// (Т32) has nothing to say about what the geometry answered, and
            /// Simulation/AssemblyInfo.cs's single InternalsVisibleTo keeps it
            /// reachable from the test assembly.
            internal StepResult(float2 target, bool hasBarrier, float barrierT,
                float2 barrierNormal, bool hasRingWall, float ringWallT,
                bool hasFloor, float floorT)
            {
                Target = target;
                HasBarrier = hasBarrier;
                BarrierT = barrierT;
                BarrierNormal = barrierNormal;
                HasRingWall = hasRingWall;
                RingWallT = ringWallT;
                HasFloor = hasFloor;
                FloorT = floorT;
            }
        }

        /// Advances one round by `dt` against the arena's STATIC geometry and
        /// reports what that step meets. The arena arrives ONCE, through
        /// `cfg.Arena` (finding D-M6: v1 passed it twice).
        ///
        /// `p` is `in`, not `ref` -- nothing here writes to the round; see the
        /// type's own doc above for why the advance cannot move in here.
        public static StepResult Step(in ProjectileState p, in SimConfig cfg, float dt)
        {
            float2 p0 = p.Pos;
            float2 target = p0 + p.Vel * dt;

            // The interior sweep is asked FIRST and the ring boundary SECOND,
            // which is the order ProjectileSystem packs them in, and the split
            // is arithmetically identical to the one call it replaced (Stage 2
            // Task 46): SweepArena consults the interior circles, walls and
            // arcs first and only then lets the ring boundary win with a
            // strict `tw < t` (its own doc), so asking the ring with the same
            // Geometry.SegmentRingWall call and the same arguments SweepArena
            // itself would have used reproduces both the same minimum and the
            // same "interior takes an exact tie" rule through the caller's
            // min-scan -- the `t` is the same number, not merely the same
            // rule. The `false` argument is `includeWall`, which is what
            // leaves the ring boundary out of the interior sweep.
            bool hasBarrier = Geometry.SweepArena(p0, target, p.Radius, in cfg.Arena, false,
                out float barrierT, out float2 barrierNormal);

            bool hasRingWall = Geometry.SegmentRingWall(p0, target, p.Radius, cfg.Arena.Radius,
                out float ringWallT);

            // Floor (Task 7): a descending shot (VelZ < 0) crosses the ground
            // when its center height reaches Radius (the sphere's underside at
            // z = 0). t_floor solves p.Height + t*VelZ*dt = Radius for t; it is
            // reported only when that crossing genuinely falls within THIS
            // step -- clipped to [0,1] the same way SegmentCircle and
            // SegmentRingWall reject an out-of-range root above, rather than
            // forcing a distant crossing to register early. A level or
            // climbing round has no ground contact to report at all, which is
            // the gate Trajectory's own three-answer doc reads alongside this
            // one (down to `-0.0f` passing the `>= 0f` here).
            //
            // `floorT` starts at 0f for definite assignment, and 0f means
            // exactly what it means for the `out t` of Geometry's own solvers:
            // nothing, until the flag beside it is true.
            bool hasFloor = false;
            float floorT = 0f;
            if (p.VelZ < 0f)
            {
                float tFloor = (p.Radius - p.Height) / (p.VelZ * dt);
                if (tFloor >= 0f && tFloor <= 1f)
                {
                    hasFloor = true;
                    floorT = tFloor;
                }
            }

            return new StepResult(target, hasBarrier, barrierT, barrierNormal,
                hasRingWall, ringWallT, hasFloor, floorT);
        }
    }
}
