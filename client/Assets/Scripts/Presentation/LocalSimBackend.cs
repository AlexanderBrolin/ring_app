using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Presentation
{
    /// The solo (and, from Task 44 on, listen-server-less) `ISimBackend`: it
    /// owns a `SimulationWorld` and drives it off wall-clock time through a
    /// `FixedStepAccumulator`. That is precisely what `SimulationRunner` itself
    /// did before Task 43 split producing state from showing it, and every line
    /// below was lifted from that class rather than rewritten — the tick loop,
    /// the snapshot swap, the guarded hash call, the accumulator's
    /// pause/restart handling — so the solo picture the milestone playtests
    /// settled keeps behaving the same way.
    ///
    /// A plain class, not a `MonoBehaviour`: it holds no scene reference and no
    /// `ScriptableObject` (the facade builds `SimConfig` and hands it over by
    /// value), so a component would add a serialized object to the scene in
    /// exchange for nothing.
    public sealed class LocalSimBackend : ISimBackend
    {
        readonly FixedStepAccumulator _acc = new FixedStepAccumulator();

        SimulationWorld _world;
        RenderSnapshot _prev, _curr;
        float _alpha;

        public bool Ready => _world != null;

        /// Forwards `SimulationWorld.Config`, which is itself a by-value
        /// property — no cached copy to drift. The null branch is what makes the
        /// interface's "`Config` answers at any time" clause true here: the
        /// facade's `RenderMuzzleHeight` reads it without a guard of its own,
        /// and zeros on a frame that draws nothing beat a throw.
        public SimConfig Config => _world != null ? _world.Config : default;

        public int CurrentTick => _world.CurrentTick;

        public RenderSnapshot Prev => _prev;

        public RenderSnapshot Curr => _curr;

        public float Alpha => _alpha;

        public int EventCount => _world.EventCount;

        public SimEvent GetEvent(int index) => _world.GetEvent(index);

        /// A local world computes its own hash, so the overlay always has a real
        /// number to print.
        public bool HasStateHash => true;

        public ulong StateHash => _world.StateHash();

        /// True: `CaptureSnapshot` fills both halves of the statistics from the
        /// world it just ticked, so every counter on the pair is a real one.
        public bool HasMatchStats => true;

        public int DroppedEvents => _world.DroppedEvents;

        public float DroppedTime => _acc.DroppedTime;

        /// Always true: this world is nobody else's, so spawning into it decides
        /// no outcome for another player (CR 3).
        public bool CanDevSpawnMob => true;

        /// `SimulationWorld.DevSpawnMob` hands back the spawned mob's id; the
        /// dev overlay has never used it and a networked twin could not produce
        /// one synchronously anyway, so the contract drops the return value
        /// instead of promising something only this implementation can keep.
        public void DevSpawnMob(MobType type, float2 pos) => _world.DevSpawnMob(type, pos);

        public int Advance(in SimInput frame, float unscaledDeltaTime,
            System.Action<int, ulong> onTick)
        {
            int ticks = _acc.Advance(unscaledDeltaTime);
            for (int i = 0; i < ticks; i++)
            {
                // Edge latches (dash/slide) ride the first sub-tick only.
                _world.Tick(SimInputFrame.ForTick(frame, i));
                (_prev, _curr) = (_curr, _prev);
                _world.CaptureSnapshot(_curr);
                // Guarded — see `ISimBackend.Advance`'s doc: `StateHash()` is
                // only ever computed when something is actually subscribed.
                if (onTick != null) onTick(_world.CurrentTick, _world.StateHash());
            }
            _alpha = _acc.Alpha;
            return ticks;
        }

        public void EndFrame() => _world.ClearEvents();

        /// Always accepts, and therefore always answers `true`: this backend's
        /// world is nobody else's, so the facade asking for a fresh match is
        /// the whole of the decision. The networked twin is the one that has a
        /// second party to disagree with — see `ISimBackend.Restart`.
        public bool Restart(long seed, in SimConfig cfg)
        {
            _world = new SimulationWorld(seed, cfg);
            _prev = new RenderSnapshot(cfg.Arena);
            _curr = new RenderSnapshot(cfg.Arena);
            _world.CaptureSnapshot(_prev);
            _world.CaptureSnapshot(_curr);
            _acc.Reset();
            _alpha = 0f;
            return true;
        }

        /// Never raised here — see `ISimBackend.MatchRestarted` for why a local
        /// match's restart is announced by the facade instead. The member exists
        /// because the interface has it; a subscriber is accepted and simply
        /// never called, which is the same thing a backend that could raise it
        /// but never does would do.
        ///
        /// The suppression below is CS0067 ("the event is never used"), which
        /// is exactly the true statement this doc makes, measured by the
        /// compiler. It is scoped to this one member rather than to the file,
        /// and an empty `add`/`remove` pair was the alternative — rejected
        /// because a member that silently swallows subscriptions reads as a
        /// defect, while a suppressed warning with a reason beside it reads as
        /// the decision it is.
#pragma warning disable 0067
        public event System.Action MatchRestarted;
#pragma warning restore 0067

        public void ApplyConfig(in SimConfig next) => _world.ApplyConfig(next);

        /// Entering pause zeroes only the accumulator's phase
        /// (`ResetAccumulatorOnly`, not `Reset`): a plain reset would also erase
        /// `DroppedTime`, wiping the diagnostic the dev overlay surfaces every
        /// time the owner pauses — exactly the silent loss spec §3.7 forbids.
        /// Leaving pause needs nothing: the facade simply resumes calling
        /// `Advance`, and a phase of zero is where the next frame starts from.
        public void OnPausedChanged(bool paused)
        {
            if (paused) _acc.ResetAccumulatorOnly();
        }
    }
}
