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

        /// The short window (Stage 2 Task 45b fix-round 1): this backend
        /// advances the tick and flushes its events inside the facade's own
        /// `Update`, which is pinned ahead of every view, so a prediction made
        /// in one frame is confirmed in the next one. Taking the interface's
        /// patient default here would leave a prediction that will never be
        /// confirmed blocking the next round's prediction ten times longer than
        /// this backend can possibly need — and solo play is where every
        /// playtest before milestone В1 happens.
        public float ImmediatePredictionWindowSeconds
            => ImmediatePredictionLatch.SameFrameWindowSeconds;

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

        /// True wherever the world HAS a dev surface to spawn into: this world is
        /// nobody else's, so spawning into it decides no outcome for another
        /// player (CR 3).
        ///
        /// FALSE IN A PRODUCTION BUILD, AND THAT IS NOT A POLICY BUT AN
        /// ARITHMETIC (bd `app-9m4`). `SimulationWorld.DevSpawnMob` is the one
        /// method in the whole simulation compiled behind
        /// `UNITY_EDITOR || DEVELOPMENT_BUILD`, and its own doc calls itself
        /// "the sole public dev-surface method here, stripped from production
        /// builds". A seat that answered `true` where that method does not
        /// exist would be promising a call nobody can make.
        public bool CanDevSpawnMob =>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            true;
#else
            false;
#endif

        /// `SimulationWorld.DevSpawnMob` hands back the spawned mob's id; the
        /// dev overlay has never used it and a networked twin could not produce
        /// one synchronously anyway, so the contract drops the return value
        /// instead of promising something only this implementation can keep.
        ///
        /// THE BODY CARRIES THE SAME GUARD AS THE METHOD IT CALLS, AND WITHOUT
        /// IT NO PRODUCTION BUILD COMPILES AT ALL (bd `app-9m4`, found by the
        /// pre-milestone rebuild of Stage 2). From Task 43 until that find this
        /// call stood unguarded against a definition stripped from every
        /// non-development target, so `BuildLinuxServer`, `BuildLinuxClient` and
        /// `BuildWindowsClient` all failed on `CS1061` while the editor, the
        /// EditMode run and `BuildLinuxClientDev` — the three surfaces anyone
        /// looked at — kept compiling, because all three define the symbol.
        /// The seam member itself is NOT guarded (`ISimBackend`), and must not
        /// be: an interface that changed shape per build target would make the
        /// two backends disagree about their own contract. So the refusal lives
        /// where the missing method is — here — and `CanDevSpawnMob` above tells
        /// the truth about it beforehand.
        public void DevSpawnMob(MobType type, float2 pos)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _world.DevSpawnMob(type, pos);
#endif
        }

        /// Always true: `Restart` below accepts every call, because this world
        /// is nobody else's — so the death screen's restart button and its dev
        /// keys mean what they have always meant in solo.
        public bool CanRestartMatch => true;

        /// False: there is nobody to ask and nobody to watch. Solo has one
        /// player, so the candidate list is empty by construction and the two
        /// mouse buttons keep the only meanings they have ever had here.
        public bool CanRequestSpectate => false;

        /// Refused, and silently: the facade asks `CanRequestSpectate` first,
        /// and a second line of defence on a path that cannot reach a server
        /// has nothing to report and nowhere to report it. A VALUE, never a
        /// throw — the same discipline `Restart`'s own answer keeps.
        public bool TryRequestSpectate(int targetIndex) => false;

        /// Never: nothing was ever sent, so nothing is waiting to be answered.
        public bool SpectateRequestInFlight => false;

        /// Zero for every slot, always (Stage 2 Task 47c): this backend owns no
        /// stale policy and there is no fog between a world in memory and the
        /// frame it captures itself into, so no slot's records ever stop
        /// arriving and nothing has a fade to be part of the way through.
        public float PlayerFadeProgress(int slot) => 0f;

        /// False for every slot, always — and that is what makes solo
        /// UNCHANGED BY CONSTRUCTION rather than by inspection. A doll is kept
        /// past a frame that is silent about its slot only while a fade is
        /// running, and none ever is here; a doll therefore leaves the moment
        /// the frame stops carrying its slot, which is exactly what every solo
        /// playtest before this task saw.
        ///
        /// AND THE CALLER NEVER EVEN ASKS. `SimulationWorld.CaptureSnapshot`
        /// writes `PlayerKnown` true for every seat of its roster and
        /// `PlayerCount` is that same roster, so the branch in
        /// `ViewRegistry.SyncPlayers` that reaches this member is unreachable on
        /// this backend. The answer above is what it would be if that ever
        /// changed — the conservative one, not a placeholder.
        public bool ShouldKeepPlayerDoll(int slot) => false;

        /// False, and permanently: solo has no wire, no round trip, no
        /// snapshot ring and no reconciliation, so every field of
        /// `NetDiagnostics` would be a zero that looks exactly like a
        /// measurement. The dev overlay draws no network section at all here
        /// — the same discipline as `HasMatchStats`, one step further:
        /// there is nothing to print a dash FOR.
        public bool TryGetNetDiagnostics(out NetDiagnostics diagnostics)
        {
            diagnostics = default;
            return false;
        }

        /// The one backend this matters to (Stage 2 Task 48, bd `app-c3m`):
        /// this is the only implementation that owns a
        /// `FixedStepAccumulator`, and therefore the only one with a
        /// `DroppedTime` an engine-side gap could pollute. The decision about
        /// what an excused frame IS lives in the accumulator; this line is the
        /// whole of the wiring.
        public void NotifyEngineIdle() => _acc.IgnoreNextFrameGap();

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

        /// False: this backend consumes the frame's input inside `Advance` —
        /// it hands it straight to `SimulationWorld.Tick` — so the facade's
        /// own tick flush is exactly when the edges have been spent, and the
        /// facade keeps clearing them there, as it always has (bd `app-d1t`).
        public bool ConsumesInputInTickDomain => false;

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
            // AFTER the reset, never before it (Stage 2 Task 48, bd
            // `app-c3m`): `Reset` clears a pending excuse along with everything
            // else it clears, so the order is the whole of this line's
            // correctness. The frame that follows a restart carries scene
            // construction and shader compilation — the first of the two cases
            // the owner excused — and it is the accumulator, not this class,
            // that decides what excusing one means.
            _acc.IgnoreNextFrameGap();
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
