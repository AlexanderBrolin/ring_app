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
        /// CARRIED IN FULL AS OF STAGE 3 TASK 20. Wire bit —
        /// InputCodec.InventoryOpenBit (byte 7, bit 4); movement slowdown —
        /// PlayerMovementSystem.SlowsMovement, read once inside
        /// RegularMoveVel (its three call sites pass the input, not the
        /// predicate); WeaponSystem.CanFire's fifth
        /// eligibility term; SimInputSanitizer.Sanitize forces the flag back
        /// down for a dead, extracted, dashing or sliding player (that
        /// method's own doc explains why gating there, not only in
        /// LootOps.Validate, matters).
        public bool InventoryOpen;

        /// app-88jb Т26 (spec §3.6/§3.7, owner decision Н4/Р355): how many
        /// ticks into the past the authoritative server must look at its
        /// targets before it judges this tick's shot, so that it judges it
        /// against the picture the shooter was actually looking at. The domain
        /// is SIMULATION TICKS, and the value is clamped to the arena cap
        /// (Arena.RewindCapTicks) inside SimInputSanitizer rather than inside
        /// InputCodec, because the server must clamp a depth that did NOT come
        /// from a client of ours just as hard as one that did.
        ///
        /// WHY IT TRAVELS IN THE INPUT RATHER THAN OFF THE SOCKET. The depth a
        /// shot needs depends on the connection that fired it, and reading it
        /// out of socket state would make the evolution of the world a
        /// function of the network — the one thing CRITICAL RULE 2 forbids.
        /// Carried inside the input record it is ordinary data, the tick
        /// consumes it like any other field, and the simulation stays the pure
        /// (state, input, tick) -> state function. That is the owner's
        /// standing decision, not a choice this task made; the server-side
        /// sanity check that compares the claimed depth against a
        /// round-trip-time estimate lives in Networking/Server and is a
        /// separate task.
        ///
        /// A LEVEL, not an edge — like FireHeld/AimHeld/InventoryOpen and
        /// unlike the two *Requested latches. SimInputFrame.ForTick spreads
        /// one frame sample across the sub-ticks of that frame and silences
        /// the two latches on every sub-tick but the zeroth; the depth must
        /// NOT be silenced with them, because it describes the state of the
        /// connection, which is one and the same on every sub-tick of a single
        /// frame. ForTick needs no line of its own for it: the level behavior
        /// falls out of the whole-struct copy that method already makes.
        ///
        /// A byte and not an int, deliberately: the wire budget for it is
        /// three bits (InputCodec's byte 7, bits 5-7), the cap it is clamped
        /// to is small (Arena.RewindCapTicks — 5 as shipped since app-gtj6,
        /// under a validation ceiling of 6), and an unsigned type makes a
        /// negative depth unrepresentable instead of merely wrong.
        ///
        /// CARRIED IN FULL AS OF Т26. Wire field — InputCodec's bits 5-7 of
        /// byte 7 (the writer saturates, and the eighth value reads back as
        /// the wire cap); arena clamp — SimInputSanitizer.Sanitize; the client
        /// measures it in NetworkSimBackend.MeasureRewindTicks, whose own doc
        /// carries the two-counter argument for the formula.
        public byte RewindTicks;
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
