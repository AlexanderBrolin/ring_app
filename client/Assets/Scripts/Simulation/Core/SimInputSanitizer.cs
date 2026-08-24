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

            // Stage 3 Task 20 (spec §3.8/§3.11, coordinator D-3): the loot
            // window closes the instant the player is dead, extracted,
            // dashing or sliding — regardless of what a client claims. Gated
            // HERE, not only in LootOps.Validate's own checks (1 and the
            // Use-only dash/slide gate), because WeaponSystem.CanFire and
            // PlayerMovementSystem's movement slowdown both read
            // SimInput.InventoryOpen directly and never go through Validate
            // at all — a modified client could otherwise keep claiming the
            // window open to those two consumers even while a real
            // Take/Drop/Use would be refused. `reference` is the player's
            // state as of the END of the PREVIOUS tick (same one-tick lag
            // every other line of this method already reads under, e.g. the
            // AimPoint fallback) — a dash/slide that STARTS this very tick
            // has not yet raised DashTimer/SlideTimer when this runs, so the
            // very first tick of a fresh dash/slide is not covered here;
            // that gap is closed by LootOps.Validate's own tick-exact checks
            // for the loot operations themselves (movement/CanFire are
            // unaffected on that one tick because the active dash/slide
            // branches bypass RegularMoveVel and CanFireWhileDash/Slide
            // already gate the shot independently).
            bool windowMustClose = !reference.Alive || reference.Extracted
                || reference.DashTimer > 0f || reference.SlideTimer > 0f;
            if (windowMustClose) s.InventoryOpen = false;

            return s;
        }
    }
}
