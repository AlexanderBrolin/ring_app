using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// В1/В2 fix-wave 2 (app-n6g item 3a): the single zone→color lookup shared
    /// by `CrosshairView`'s marker and `AimRayView`'s ray — both tint by the
    /// exact same `HitZone` `AimProvider.CurrentAimZone` reports each frame,
    /// so this is one switch, not two copies of it (Reuse > duplication,
    /// AGENT.md §4). `HitZone` itself is `Ring.Simulation.Core`'s own enum
    /// (`SimEvent.Zone`'s type) — reused as-is, never redefined here.
    internal static class AimZoneColors
    {
        /// Legs/Body share the neutral `GameFeelConfig.AimZoneBodyColor`; Head
        /// gets its own `AimZoneHeadColor` accent so a headshot lineup reads
        /// as visibly different before the trigger is pulled. `HitZone.None`
        /// (no proxy hit this frame, or `!AimHeld` — `AimProvider`'s own
        /// class doc) falls back to the caller's own existing baked color —
        /// unchanged behavior whenever this feature has nothing to say.
        public static Color Resolve(HitZone zone, Color fallback, GameFeelConfig gameFeel)
        {
            switch (zone)
            {
                case HitZone.Head: return gameFeel.AimZoneHeadColor;
                case HitZone.Body:
                case HitZone.Legs: return gameFeel.AimZoneBodyColor;
                default: return fallback;
            }
        }
    }
}
