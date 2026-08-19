using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Raw per-tick input from a client, before sanitization inside the world.
    public struct SimInput
    {
        public float2 MoveDir, AimPoint;
        public bool FireHeld, DashRequested;

        /// Aim-down-sights height controls (Task 8 Interfaces): AimHeld is a
        /// level (true while the player holds the aim button), AimHeight is the
        /// requested vertical aim offset it carries while held. The consumer
        /// that reads these to drive the raycast aim system arrives in Task 15
        /// — here they only travel through SimInputFrame.ForTick and Sanitize.
        public float AimHeight;
        public bool AimHeld, SlideRequested;

        /// Stage 3 Task 17 (spec §3.8 check 2, Р239, plan errata E-5): whether
        /// the looting window is open this tick. It lives HERE and not in
        /// PlayerState because it changes PREDICTED MOVEMENT — an open window
        /// slows the step to Hero.AimMoveSpeedFrac and takes the weapon away —
        /// so client and server must read the exact same value for the exact
        /// same tick, which is what the input path guarantees and a
        /// server-only state field could not.
        ///
        /// A LEVEL, not an edge: SimInputFrame.ForTick copies it unchanged to
        /// every sub-tick (like FireHeld/AimHeld, unlike the two *Requested
        /// latches), because "the window is open" is a state the player holds,
        /// not an event they fire.
        ///
        /// DECLARED, NOT YET CARRIED. This task is the server-side half only:
        /// LootOps.Validate reads it, and nothing else does. The wire bit
        /// (ReplicateData's own free flag bits, InputCodec), the movement
        /// slowdown, WeaponSystem.CanFire's `!InventoryOpen` term and the
        /// dash/slide sanitizer that forces it back down all belong to Т20 —
        /// see InputCodecTests.EveryFieldOfSimInput_IsCarriedOnTheWire and
        /// ReconcileCodecTests.SimInputFieldsCovered, the two sentinels that
        /// force that decision to be made rather than forgotten.
        public bool InventoryOpen;
    }

    /// Distributes one frame sample over N sub-ticks: held values copy to every
    /// tick, the dash and slide edge-latches fire on tick 0 only (spec §3.2).
    public static class SimInputFrame
    {
        public static SimInput ForTick(in SimInput frame, int tickIndex)
        {
            SimInput si = frame;
            si.DashRequested = frame.DashRequested && tickIndex == 0;
            si.SlideRequested = frame.SlideRequested && tickIndex == 0;
            return si;
        }
    }
}
