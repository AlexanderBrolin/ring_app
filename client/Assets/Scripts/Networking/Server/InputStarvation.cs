using Ring.Simulation.Core;

namespace Ring.Networking.Server
{
    /// History of received inputs -> effective input for this tick + counters (Р25,
    /// Stage 2 Task 36, spec §3.7). `MatchServer.OnPostTick` calls this once per
    /// player, BEFORE `world.TickAll` (Р22 step 1): the world must never see a raw
    /// "no input arrived" gap — it always gets a well-formed `SimInput`, and this
    /// function is the ONE place that decides what that stand-in looks like.
    ///
    /// PURE C#, NO UnityEngine, NO FishNet (Files §1) — `Unity.Mathematics` is the
    /// same standing exception `Ring.Simulation` itself relies on (Critical Rule 1),
    /// not a new one; `SimInput.MoveDir`/`AimPoint` are already `float2` and nothing
    /// here would gain from re-deriving that type.
    ///
    /// THREE REGIMES, KEYED ON `ticksSinceLast` (task-34-report §8.1 supplies the
    /// value: the gap between the tick the world is about to run and
    /// `PlayerPredictionCore.LastServerInput.Tick`, the tick of the last input that
    /// actually arrived — never a repeat, see that struct's own doc):
    ///   * 0 — a fresh input arrived for this exact tick. PASSTHROUGH, untouched,
    ///     edge flags included. Edge requests (`DashRequested`/`SlideRequested`)
    ///     are consumed exactly once, on the tick they are presented (carryover-t30
    ///     latch discipline) — starvation must not interfere with that on the one
    ///     tick a request is legitimately live.
    ///   * 1..starveTicks — the HOLD window. The last input is repeated so the
    ///     player does not visibly stop on a single dropped packet (Р24's own
    ///     redundancy is the first line of defense; this is the second, for when
    ///     redundancy itself runs out), but with the edge flags CLEARED: repeating
    ///     `last` verbatim would re-present the same dash/slide request on every
    ///     held tick, firing it repeatedly for one keypress. Movement and aim ride
    ///     along unmodified — a repeated walk is not a lie the way a repeated dash
    ///     would be.
    ///   * beyond starveTicks — STARVATION. A genuinely stale input is not
    ///     trustworthy enough to keep moving or shooting on: `MoveDir` and
    ///     `FireHeld` are zeroed (Р25 — without this, a lost connection would
    ///     carry the body into a wall or keep firing into whatever it last aimed
    ///     at). `AimHeld` (and the `AimPoint`/`AimHeight` that travel with it) are
    ///     the one exception, deliberately: the aim is cosmetic while stationary
    ///     and jerking it to a neutral value on every disconnect would be a worse
    ///     visual than leaving it be.
    ///
    /// Р82 HYGIENE, BOTH ENDS:
    ///   * a negative `ticksSinceLast` clamps to 0 (fresh) — the caller's tick
    ///     arithmetic is trusted for magnitude, not for sign; a caller bug that
    ///     hands this a negative gap must not read as "starved forever" (a large
    ///     unsigned wrap would have looked exactly like the worst possible
    ///     staleness had this function done the arithmetic itself instead of
    ///     trusting a caller-supplied `int`).
    ///   * `starveTicks <= 0` means there is no hold window: any
    ///     `ticksSinceLast >= 1` is starvation immediately. This is not a
    ///     special case in the code — it falls out of the ordinary
    ///     `0 < starveTicks && ticksSinceLast <= starveTicks` guard on the hold
    ///     branch — but it is a real, tested configuration (a match with
    ///     `NetConfig.InputStarveTicks` misconfigured to 0 must degrade to
    ///     "starve on the very first miss", not throw or loop forever).
    public static class InputStarvation
    {
        public static SimInput Effective(in SimInput last, int ticksSinceLast,
            int starveTicks, out bool starved)
        {
            if (ticksSinceLast < 0) ticksSinceLast = 0;

            if (ticksSinceLast == 0)
            {
                starved = false;
                return last;
            }

            if (starveTicks > 0 && ticksSinceLast <= starveTicks)
            {
                starved = false;
                SimInput held = last;
                held.DashRequested = false;
                held.SlideRequested = false;
                return held;
            }

            starved = true;
            SimInput starving = last;
            starving.MoveDir = default;
            starving.FireHeld = false;
            starving.DashRequested = false;
            starving.SlideRequested = false;
            // AimHeld/AimPoint/AimHeight are left exactly as `last` carried
            // them — see the class doc's third regime.
            return starving;
        }
    }
}
