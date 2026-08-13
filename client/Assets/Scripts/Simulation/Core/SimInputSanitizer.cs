using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Public sanitization seam (Stage 2 Task 6 Interfaces, spec §3.1): lets
    /// client-side prediction (Task 30) sanitize raw input identically to the
    /// authoritative world, instead of duplicating the formula. Verbatim
    /// transfer of the body that used to live in SimulationWorld.Sanitize
    /// (Stage 2 Task 4) — no coefficient, check order or condition changed;
    /// `reference` stands in for the sanitizing player's own state (AimPoint
    /// fallback, Pos-relative AimPoint clamp) and `cfg` for the match's
    /// balance config (Arena.Radius, Hero.MuzzleHeight/MaxAimHeight). Pure
    /// function: no allocation, no state.
    public static class SimInputSanitizer
    {
        public static SimInput Sanitize(in SimInput raw, in PlayerState reference, in SimConfig cfg)
        {
            SimInput s = raw;
            if (!math.all(math.isfinite(s.MoveDir))) s.MoveDir = float2.zero;
            float lsq = math.lengthsq(s.MoveDir);
            if (lsq > 1f) s.MoveDir /= math.sqrt(lsq);
            if (!math.all(math.isfinite(s.AimPoint))) s.AimPoint = reference.AimPoint;
            float2 rel = s.AimPoint - reference.Pos;
            float maxR = cfg.Arena.Radius * 2f;
            if (math.lengthsq(rel) > maxR * maxR)
                s.AimPoint = reference.Pos + math.normalizesafe(rel) * maxR;
            // Task 8: non-finite AimHeight maps to standing muzzle height, then
            // the result is clamped into the arena-wide aim-ray height cap —
            // sanitized unconditionally so the field stays finite regardless of
            // AimHeld (the consumer that gates on AimHeld arrives in Task 15).
            if (!math.isfinite(s.AimHeight)) s.AimHeight = cfg.Hero.MuzzleHeight;
            s.AimHeight = math.clamp(s.AimHeight, 0f, cfg.Hero.MaxAimHeight);
            return s;
        }
    }
}
