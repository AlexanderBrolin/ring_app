using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Protocol
{
    /// Stage 2 Task 44a (spec §3.12 Р68): the DECODE side of the Players
    /// record's `flags` byte — flags plus the quantized fields that ride with
    /// them, turned into a `PlayerState` complete enough for another player's
    /// doll to strike the right pose.
    ///
    /// THE BIT CONSTANTS DELIBERATELY DO NOT LIVE HERE. They already have one
    /// home — `SnapshotBlocks.PlayerWireFlags` (Task 27), which is what the
    /// assembler writes and what the codec round-trips — and a second copy of
    /// `Alive = 1 << 0, Dashing = 1 << 1, …` next to it would be two sources of
    /// truth for one wire byte, the kind that stays consistent right up until
    /// somebody edits one of them. This class is the MAPPING, not the catalog:
    /// it reads `PlayerWireFlags` and owns only what the catalog cannot say —
    /// which `PlayerState` field each bit drives.
    ///
    /// WHY A SYNTHETIC STATE AT ALL. The 8-byte Players record carries
    /// position, heading, hp and this flags byte, while `PlayerVisual` reads
    /// `DashTimer`, `DashDir`, `SlideTimer`, `AimSettleTimer` and `AimPoint`.
    /// Without one fixed mapping, the networked doll would need a second
    /// rendering path of its own, or would simply lose the slide and dash poses
    /// — which is exactly the divergence spec §3.12 pins this table against.
    ///
    /// THE TIMERS ARE NOT A DURATION, THEY ARE A "YES". A flag says the pose is
    /// active THIS frame and says nothing about how much of it is left, so the
    /// timers are set to one tick — the smallest value that still reads as
    /// "on". `PlayerState`'s timers are SECONDS (see the struct's own fields),
    /// so a literal `1` would claim a dash roughly thirty times longer than any
    /// dash lasts.
    public static class PlayerFlags
    {
        /// How far downrange the synthetic `AimPoint` is placed along the
        /// heading, in metres. A COSMETIC PLACEMENT DISTANCE, not a balance
        /// number: the flags byte says "aiming" and carries no aim point, while
        /// the doll only needs a point far enough away for the aim pose to face
        /// the right way. It is a constant here, and not a `SimConfig` field,
        /// precisely because no gameplay outcome reads it — nothing in
        /// `Simulation` ever sees this state.
        public const float SyntheticAimMeters = 10f;

        /// Builds the doll's `PlayerState` from one decoded Players record.
        /// `heading` is the record's decoded unit direction, `hp01` its
        /// normalized hp — scaled back to absolute through `cfg`, never through
        /// a literal, so the doll's health bar and the simulation's own agree
        /// on one MaxHp.
        ///
        /// A CLEARED `Alive` BIT DOES NOT CLEAR THE POSE. It sets `Alive` false
        /// and nothing else: the corpse is drawn by its own branch, and zeroing
        /// the pose here would silently decide a presentation question this
        /// mapping has no business deciding.
        public static PlayerState ToSyntheticState(byte flags, float2 pos, float2 heading,
            float hp01, in SimConfig cfg)
        {
            // One tick of seconds — see this class's own doc for why the
            // literal `1` would be wrong rather than merely imprecise.
            const float oneTick = SimulationWorld.TickDt;

            var state = new PlayerState
            {
                Pos = pos,
                Hp = hp01 * cfg.Hero.MaxHp,
                Alive = (flags & PlayerWireFlags.Alive) != 0,
            };

            if ((flags & PlayerWireFlags.Dashing) != 0)
            {
                state.DashTimer = oneTick;
                state.DashDir = heading;
            }

            if ((flags & PlayerWireFlags.Sliding) != 0)
            {
                state.SlideTimer = oneTick;
                state.SlideDir = heading;
            }

            if ((flags & PlayerWireFlags.AimHeld) != 0)
            {
                // SETTLED, not settling: the flag reports that the pose is on
                // now and carries no progress of its own, so anything less than
                // the full settle would make every remote player look as if it
                // had just raised its weapon, every frame.
                state.AimSettleTimer = cfg.Hero.AimSettleSeconds;
                state.AimPoint = pos + heading * SyntheticAimMeters;
            }

            if ((flags & PlayerWireFlags.LinkWindow) != 0) state.LinkWindowTimer = oneTick;

            return state;
        }
    }
}
