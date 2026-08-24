using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// One exit's ring on the floor, lit while that exit will actually take a
    /// collector — the emission-by-state spec §3.11 asks these rings for
    /// (Stage 3 Т33, bd `app-j4oj`).
    ///
    /// THE RULE IS NOT RESTATED HERE. Which exits are open is
    /// `Core.ExitRules.IsOpen`, the simulation's own single home for it — Т33
    /// MOVED that rule out of `ExtractionSystem` rather than copying it, for
    /// exactly this reader. A second copy would be a picture able to disagree
    /// with the world about the one thing the picture exists to report, and it
    /// would disagree SILENTLY: a ring drawn bright over an exit that refuses
    /// you is worse than no ring at all, because it is a promise.
    ///
    /// DIM, NOT HIDDEN. A closed exit is still a place on the map worth
    /// remembering — the early portals are where the raid came in, and the gate
    /// is where it hopes to leave — so the ring stays drawn at a fraction of
    /// its authored brightness. What the player reads is a state change, not an
    /// appearance, and a thing that appears from nothing reads as "new" rather
    /// than as "now".
    ///
    /// BUILT AT RUNTIME, SO IT IS CONFIGURED RATHER THAN SERIALIZED.
    /// `GreyboxBuilder` mints these markers from `ArenaConfig` every time it
    /// builds, so there is no prefab for a bootstrap to wire and `Configure` is
    /// how the two references arrive — the same shape `ViewRegistry`'s pooled
    /// views take their per-instance facts in.
    public sealed class ExtractionRingView : MonoBehaviour
    {
        /// How much of its authored brightness a shut exit keeps. UI paint
        /// rather than a number a match is decided by — the same category
        /// `InventoryWindowController`'s slot colors are in, and the same
        /// reason they are literals: CR 6 is about damage, cooldowns and loot.
        const float ClosedBrightness = 0.18f;

        SimulationRunner _runner;
        ExitKind _kind;
        readonly EmissiveFade _fade = new EmissiveFade();
        /// What was last written, so a ring that has not changed state does not
        /// repaint every frame. `-1` is neither of the two brightnesses.
        float _applied = -1f;

        /// Tells this marker whose state to read and which exit it is.
        public void Configure(SimulationRunner runner, ExitKind kind)
        {
            _runner = runner;
            _kind = kind;
            _fade.Capture(gameObject);
            _applied = -1f;
        }

        void Update()
        {
            if (_runner == null || !_runner.Ready) return;

            MatchState match = _runner.Curr.Match;
            float target = ExitRules.IsOpen(in match, (byte)_kind)
                ? 1f
                : ClosedBrightness;
            if (target == _applied) return;

            _applied = target;
            _fade.Apply(target);
        }
    }
}
