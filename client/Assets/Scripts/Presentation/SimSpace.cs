using Ring.Simulation.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Sole mapping between sim-space `float2 (x, y)` and Unity world-space
    /// `Vector3 (x, 0, z)` (spec §3.7/§3.11). Every other Presentation type that
    /// needs to convert between the two routes through here — no inline
    /// `new Vector3(p.x, 0f, p.y)` / `new float2(w.x, w.z)` elsewhere.
    public static class SimSpace
    {
        public static Vector3 ToWorld(float2 simPos) => new Vector3(simPos.x, 0f, simPos.y);

        public static float2 ToSim(Vector3 world) => new float2(world.x, world.z);

        /// The world rotation a body's tilt composes onto its facing —
        /// ONE HOME for both dolls (app-94sk T5a, rule 2). `MobVisual.Sync`
        /// and `PlayerVisual.Sync` were carrying five byte-identical lines
        /// and a dozen of identical explanation each; what differs between
        /// them is only what this result is composed WITH (`_facing` for a
        /// mob, the dash-lean `rotation` for a collector), and that stays at
        /// the call sites where it belongs.
        ///
        /// `tilt` IS THE SIM-SPACE LEAN: length is the angle in RADIANS,
        /// heading is the direction the body leans towards
        /// (`MobState.Tilt`'s own doc). `Quaternion.AngleAxis` wants degrees,
        /// hence the one `Mathf.Rad2Deg` in this layer.
        ///
        /// A BODY DOES NOT SPIN AROUND ITS LEAN, IT TIPS OVER IT — around the
        /// horizontal line perpendicular to the lean — and
        /// `Vector3.Cross(Vector3.up, worldLean)` is that perpendicular, with
        /// the sign that carries the model's top ALONG the lean rather than
        /// against it. It is unit length with no explicit `.normalized`: the
        /// lean is divided by its own length just below, `ToWorld` is a
        /// lossless axis swap, and `Vector3.up` is perpendicular to anything
        /// confined to the plane it maps into, so the cross is
        /// `1 * 1 * sin(90°)`.
        ///
        /// THE RESULT IS A WORLD ROTATION, which is why every caller composes
        /// it OUTERMOST: the axis is fixed in the world, not relative to
        /// whichever way the model currently faces.
        ///
        /// THE GUARD IS CONTENT, NOT DEFENSE (Ruling 49): an upright body has
        /// a lean of length zero, and a zero vector offers no direction to
        /// build an axis from. `Quaternion.AngleAxis`'s own documentation
        /// says nothing about a zero-length axis in either direction (checked
        /// against the API docs, not assumed), and this runs for every drawn
        /// body every frame. The threshold is `Impact.RestEpsilon` — the
        /// simulation's own "this body has come to rest", reused rather than
        /// restated (rule 2). ⚠ It is NOT a promise that the pair has been
        /// snapped: `Impact.SpringStep` zeroes both members only when the
        /// tilt AND the angular velocity are inside that epsilon, so a lean
        /// under it with a live velocity is drawn upright here while the
        /// simulation still carries it. The quantity discarded is at most
        /// 1e-4 rad — 0.0057°, under a thousandth of a pixel at any framing
        /// this game uses — and the alternative is handing `AngleAxis` an
        /// axis built by dividing by a number that small.
        public static Quaternion TiltRotation(float2 tilt)
        {
            float tiltMag = math.length(tilt);
            return tiltMag > Impact.RestEpsilon
                ? Quaternion.AngleAxis(tiltMag * Mathf.Rad2Deg,
                    Vector3.Cross(Vector3.up, ToWorld(tilt / tiltMag)))
                : Quaternion.identity;
        }
    }
}
