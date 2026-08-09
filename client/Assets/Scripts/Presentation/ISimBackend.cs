using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Presentation
{
    /// Where the state on screen COMES FROM (Stage 2 Task 43, spec §3.12).
    /// `SimulationRunner` used to be two things at once: the facade every view
    /// reads, and the owner of the `SimulationWorld` it read out of. This
    /// interface is the seam between those two jobs. Everything that PRODUCES
    /// state lives behind it — the world, the fixed-step accumulator, the
    /// `Prev`/`Curr` double buffer, tick advancement, the lifetime of the event
    /// buffer. Everything that SHOWS state stays on the facade — the freeze
    /// layer, input sampling, the render pair the views actually read.
    ///
    /// The split exists because the second implementation (Task 44) has no
    /// world at all: it receives snapshots. That is also why `Ready` below is a
    /// member rather than each view keeping its own `World == null` test — the
    /// old test is unanswerable once the world is on another machine.
    ///
    /// Balance ScriptableObjects stay facade-side on purpose: a backend is
    /// handed a finished `SimConfig` BY VALUE (`Restart`/`ApplyConfig`) and
    /// never sees a `ScriptableObject`, so nothing here depends on Unity
    /// serialization, on scene wiring, or on which side of the wire the numbers
    /// were built on.
    ///
    /// CALL CONTRACT — three groups, not two (Task 43 fix-round 1: the earlier
    /// one-line version said "every member other than `Ready`/`Config` may be
    /// called only while `Ready` is true", which is false on the very first call
    /// any backend receives — see the mutators below).
    ///
    /// ANSWERED AT ANY TIME, ready or not: `Ready`, `Config` (reads `default`
    /// before the first `Restart`), `HasStateHash`, `CanDevSpawnMob`,
    /// `DroppedTime`. The facade reads several of these on frames that draw
    /// nothing, so an implementation returns a value here rather than throwing.
    ///
    /// READ ONLY WHILE `Ready` IS TRUE — the observers of simulation state:
    /// `CurrentTick`, `Prev`, `Curr`, `Alpha`, `EventCount`, `GetEvent`,
    /// `StateHash`, `DroppedEvents`, `DevSpawnMob`. There is no state to answer
    /// with before that. What keeps them from being reached early is the
    /// facade's own members plus the views' `Ready` guards, not a runtime check
    /// inside each implementation.
    ///
    /// OUTSIDE THAT RULE — the lifecycle mutators, called on the facade's own
    /// schedule: `Restart`, `ApplyConfig`, `OnPausedChanged`, `Advance`,
    /// `EndFrame`. `Restart` is what MAKES an implementation ready and is
    /// therefore called while `Ready` is still false — `SimulationRunner.Awake`
    /// -> `RestartNewSeed` -> `Restart(seed)` -> `_backend.Restart(seed, cfg)`
    /// is the first call any backend sees, so an implementation that refuses it
    /// on `!Ready` refuses the start of the match. `OnPausedChanged` has no
    /// readiness guard on the facade either (the `Paused` setter forwards it
    /// unconditionally) and must stay answerable regardless; the local backend
    /// touches only its accumulator there, never its world. `ApplyConfig`,
    /// `Advance` and `EndFrame` are reached only from `SimulationRunner.Update`,
    /// which runs strictly after that `Awake`, so those three do see a ready
    /// backend — and `EndFrame` only after an `Advance` that returned a nonzero
    /// tick count (see its own doc for why the order matters).
    public interface ISimBackend
    {
        /// Whether there is state to show (Р66) — successor to the
        /// `World == null` test seven views used to make for themselves. A
        /// networked backend is not ready until the first snapshot lands, which
        /// is strictly later than the frame its views first run.
        bool Ready { get; }

        /// The one config source Presentation reads (Р87 — before Task 43 the
        /// muzzle-height helper took its numbers from the world while the
        /// fire-gate copy took them from a `WeaponConfig` asset directly).
        /// By value, mirroring `SimulationWorld.Config`'s own by-value property:
        /// callers get a copy and nobody caches one. Reads `default` while
        /// `!Ready`, so a cold-start reader gets zeros instead of throwing.
        SimConfig Config { get; }

        /// The simulation's own tick counter (dev overlay). Not the same number
        /// as `Curr.Tick` for a backend whose snapshots arrive late.
        int CurrentTick { get; }

        /// The tick double buffer every interpolating view reads through the
        /// facade's freeze layer. Recycled objects, not values — a backend swaps
        /// the pair and overwrites the older half on every tick, so anything
        /// that needs to HOLD a picture must deep-copy it (the facade's frozen
        /// buffers do exactly that).
        RenderSnapshot Prev { get; }

        RenderSnapshot Curr { get; }

        /// Interpolation phase between `Prev` and `Curr`. Latched during
        /// `Advance` rather than derived live, so a facade that stops calling
        /// `Advance` (pause) keeps showing the phase it stopped at instead of
        /// having its views slide back toward `Prev`.
        float Alpha { get; }

        /// This flush's event buffer. Valid between `Advance` returning a
        /// nonzero tick count and the matching `EndFrame` — the whole reason
        /// those are two calls and not one.
        int EventCount { get; }

        SimEvent GetEvent(int index);

        /// Diagnostics; `DevOverlay` is the only consumer. `HasStateHash` is
        /// false where the hash is a server-side quantity the client cannot
        /// compute — the overlay then prints a dash instead of a plausible
        /// looking wrong number (spec §3.7's "no silent loss", applied to a
        /// diagnostic rather than a counter).
        bool HasStateHash { get; }

        ulong StateHash { get; }

        int DroppedEvents { get; }

        /// Real time the clock had to throw away (long frames). Facade-visible
        /// as `SimulationRunner.AccumulatorDroppedTime`.
        float DroppedTime { get; }

        /// Whether the dev spawn buttons may be drawn at all (CR 3): a
        /// networked client must not put a mob into an authoritative world, so
        /// the overlay asks first instead of calling into a backend that could
        /// only refuse.
        bool CanDevSpawnMob { get; }

        void DevSpawnMob(MobType type, float2 pos);

        /// One render frame of simulation; returns how many ticks it produced
        /// (0 = the frame landed inside the current tick).
        ///
        /// `onTick` is the facade's `TickAdvanced` event handed over as a plain
        /// delegate, and it is null exactly when nothing is subscribed. An
        /// implementation MUST NOT compute `StateHash` in that case: the hash
        /// walks every live mob and projectile, and its only subscriber is a
        /// dev-log toggle that is off almost always. A delegate rather than a
        /// second event on the backend keeps that decision in one place and the
        /// subscription list on the facade, where the public event already is.
        int Advance(in SimInput frame, float unscaledDeltaTime,
            System.Action<int, ulong> onTick);

        /// Closes the frame `Advance` opened — AFTER the facade has raised
        /// `TicksFlushed`. The fan-out behind that event reads this flush's
        /// events out of the buffer, and this call is what drops them; the order
        /// between the two is the contract, not an implementation detail
        /// (invert it and every casing, corpse, flash and shot sound silently
        /// stops appearing, with nothing failing to compile).
        void EndFrame();

        /// Starts a fresh match. `cfg` is built facade-side from its serialized
        /// ScriptableObjects and passed by value; `seed` is the match seed a
        /// local backend seeds its RNG with and a networked one only records.
        void Restart(long seed, in SimConfig cfg);

        /// Hot-tweak of balance numbers in place (spec §3.9). May throw
        /// `System.ArgumentException` when the new config changes arena
        /// topology, which in-place migration is not allowed to handle — the
        /// facade catches that and restarts on the same seed instead.
        void ApplyConfig(in SimConfig next);

        /// The facade's pause gate flipped. Only the facade decides what pause
        /// MEANS for what reaches the screen (it simply stops calling `Advance`);
        /// this call exists so a backend can settle its own clock — the local one
        /// drops the fractional-tick backlog so unpausing does not burst-catch-up.
        void OnPausedChanged(bool paused);
    }
}
