using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using Ring.Data;
using Ring.Networking;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Presentation
{
    /// The `ISimBackend` WITH NO WORLD (Stage 2 Task 44c, spec §3.12): the one
    /// that receives snapshots instead of ticking a `SimulationWorld`. Task 43
    /// split producing state from showing it precisely so this class could
    /// exist; `LocalSimBackend` is its twin on the other side of that seam, and
    /// every member below answers the same question its counterpart there does,
    /// from a frame off the wire rather than from a world in memory.
    ///
    /// A plain class, not a `MonoBehaviour`, for the same reason its twin is:
    /// it holds no scene reference and no `ScriptableObject` the facade could
    /// not hand it. `NetConfig` is the one exception and it is not an
    /// exception in spirit — it is the NETWORK tuning asset (Р52: it never
    /// enters `SimConfig` or the balance-parity hash), so the facade has no
    /// business building it and no way to pass it by value.
    ///
    /// NOTHING HERE DECIDES A GAME OUTCOME (CR 3). Every number that reaches
    /// the screen through this class was computed by the server and arrived on
    /// the wire, except the local player's own predicted copy — which is
    /// movement and dash only, is corrected by every reconcile, and is
    /// `PlayerPredictionCore`'s to own, not this class's.
    ///
    /// WHAT IT ASSEMBLES, AND FROM WHOM. Nine objects already existed for this
    /// task to put together, and the point of the class is that it writes none
    /// of their logic again: `SnapshotReader`/`SnapshotBlocks`/`SnapshotEvents`
    /// decode the frame, `SnapshotQueue` decides which frames get a slot,
    /// `RenderClock` decides which moment is on screen, `EventDedup` decides
    /// which events are new, `ClientEventQueue` holds them until their tick
    /// arrives, `GhostProjectiles` tracks the client's own predicted rounds,
    /// `StalePolicy` decides what a silence means, `ClientMatchReset` clears
    /// all six on an epoch change and `ClientMatchLink` says when that is.
    ///
    /// TWO TICK DOMAINS, AND THE MEMBERS ARE SPLIT ALONG THEM. `Advance` runs
    /// in the RENDER domain — `RenderClock.RenderTick`, continuous local time,
    /// `InterpBufferTicks` behind the newest frame buffered — and everything it
    /// drives (the ring's discharge, the render pair, the event drain, the
    /// stale policy) is measured there. The broadcast handler runs in the WIRE
    /// domain, on whatever tick the frame in hand names. The two are the same
    /// unit and a different origin, exactly as `StalePolicy`'s own doc
    /// describes; nothing below mixes them except by subtraction.
    ///
    /// THREE SEAMS THIS CLASS CANNOT REACH, AND THEY ARE NOT OVERSIGHTS —
    /// see the report of this task for the one-line fix each of them wants.
    ///   * `PlayerNetworkController.Configure`, `SetPendingInput` and
    ///     `NotifyOwnDeath` are `internal` to `Ring.Networking`, whose
    ///     `AssemblyInfo.cs` opens its internals to `Ring.Simulation.Tests`
    ///     and to nothing else. This assembly therefore cannot push a
    ///     `SimInput` into prediction, cannot hand the controller its
    ///     `SimConfig`, and cannot report the local player's own death — even
    ///     though that controller's own class doc names `Ring.Presentation` as
    ///     where its input comes FROM. Until that changes the client's
    ///     prediction path is inert: `Core.Predicted` never leaves
    ///     `default(PlayerState)`, `IsPredicting` reads false, and the branch
    ///     below that would show the local player from the predicted copy
    ///     never runs.
    ///   * `SimulationRunner` has no way to be given a backend at all — its
    ///     `_backend` field is private, initialized in place with a
    ///     `LocalSimBackend`, and written nowhere else. This class compiles,
    ///     registers and runs, and no code path installs it.
    ///   * `SimulationRunner.WorldRestarted` is raised only from the facade's
    ///     own `Restart`. A backend told by `MatchRestartedNet` that a new
    ///     match has begun has no seam to say so, so the ten Presentation
    ///     subscribers of that event are not reached on a network restart.
    ///     The seams `ClientMatchReset` clears ARE cleared (that path runs
    ///     inside `ClientMatchLink`); it is only the facade-side event that
    ///     has no route.
    ///
    /// WHAT IS DELIBERATELY NOT HERE. `ObservedIndex` and spectating (Task
    /// 47), the player dolls and their pool (Task 45), the dev overlay's
    /// network section (Task 48) and the walls (Task 46). Two further gaps are
    /// this task's own findings rather than its neighbours' scope, and both
    /// are recorded where they bite: the projectile picture (see `Curr`) and
    /// the match statistics (see `Restart`).
    public sealed class NetworkSimBackend : ISimBackend
    {
        /// The block kinds this receiver understands. A kind absent from this
        /// list is walked past and counted by `SnapshotReader.
        /// SkippedBlockCount` (Р29) — which is why `Liveness` appears here
        /// even though nothing below reads it: this client DOES know that
        /// block, it simply has nowhere to put it yet (see `ReadFrame`), and
        /// letting it count as an unknown kind would misreport a
        /// forward-compatibility statistic.
        static readonly byte[] KnownBlockKinds =
        {
            (byte)SnapshotBlockKind.Players,
            (byte)SnapshotBlockKind.Liveness,
            (byte)SnapshotBlockKind.Mobs,
            (byte)SnapshotBlockKind.Wave,
            (byte)SnapshotBlockKind.Events,
        };

        /// How long a ghost that was confirmed but never ended may stay in the
        /// registry, expressed as `GhostProjectiles`' own `maxTrackTicks`.
        /// That class's doc leaves the number to this task and names the
        /// starting point — "ceil(ProjectileLifetime / TickDt) + margin, since
        /// a confirmed ghost has no legitimate reason to outlive its own
        /// projectile's flight by much" — and this is that expression with the
        /// margin below. A ceiling, not a lifetime: it exists so a lost
        /// `ProjectileEndedNet` cannot burn a registry slot for the rest of
        /// the match, and being generous costs one slot for a few extra ticks
        /// while being stingy retires a round that is still flying.
        const int GhostTrackMarginTicks = 15;

        /// How many render ticks a stale entity takes to fade out once it is
        /// eligible (`StalePolicy`'s `fadeTicks`). `NetConfig` carries no field
        /// for it — `StalePolicy`'s own doc records that as an open end for
        /// this task — and it stays a constant here rather than becoming an
        /// asset field because the number has no visible consumer yet: nothing
        /// reads `StaleState`/`FadeProgress` until the dolls of Task 45 do.
        /// Half a second at 30 Hz: long enough to read as a fade rather than a
        /// blink, short enough that a genuinely departed entity does not
        /// linger past the moment the player stops believing in it.
        const int EntityFadeTicks = 15;

        readonly NetworkManager _nm;
        readonly NetConfig _net;
        readonly string _playerId;
        readonly string _joinToken;

        /// This connection's counters. One instance for the life of this
        /// backend, never per match: `NetStats`' own doc calls it a
        /// per-connection health record, and `SnapshotQueue`/`ClientEventQueue`
        /// both refuse to clear their overflow counters on a restart for the
        /// same reason.
        readonly NetStats _stats = new NetStats();

        // The six per-match seams, plus the two objects that own their reset
        // and their epoch. All built by the first `Restart`, because every one
        // of them is sized from `SimConfig` and the facade does not hand that
        // over until then.
        SnapshotQueue _snapshots;
        RenderClock _clock;
        EventDedup _dedup;
        ClientEventQueue _events;
        GhostProjectiles _ghosts;
        StalePolicy _stale;
        ClientMatchReset _reset;
        ClientMatchLink _link;

        NetTimings _timings;
        SimConfig _cfg;
        bool _hasConfig;
        bool _registered;

        // The render pair this class owns, deep-copied out of the ring every
        // frame. NOT a pair of references INTO the ring: a frame arriving
        // between this backend's `Advance` and the views' own `LateUpdate`
        // can `Admit` into the very slot a reference would be pointing at, and
        // the views would then read a half-decoded frame of another tick under
        // the identity of this one.
        RenderSnapshot _prev, _curr;
        float _alpha;
        bool _ready;
        int _lastRenderTick;

        // Decode scratch, sized once from the arena caps. Nothing on the
        // receive path allocates after `Restart` returns.
        SnapshotBlocks.PlayerRecord[] _playerScratch;
        SnapshotBlocks.MobRecord[] _mobScratch;
        SnapshotBlocks.EventRecord[] _eventScratch;

        // This flush's event buffer — the `EventCount`/`GetEvent` window the
        // facade opens with `Advance` and closes with `EndFrame`.
        SimEvent[] _frameEvents;
        int _frameEventCount;

        // The decoded events waiting for their tick, and the pool the queue
        // itself cannot hold. See `EnqueueEvent`.
        SimEvent[] _pendingPool;
        int[] _freeSlots;
        int _freeCount;
        ushort _poolEpoch;

        /// This client's own player object once FishNet has spawned it.
        /// Found rather than injected: the object is spawned by the server
        /// mid-match, and the one authority on which object is ours is
        /// FishNet's own ownership table (`NetworkConnection.Objects`,
        /// maintained by `NetworkObject.InitializeEarly` on both ends). A
        /// bootstrap that passed the reference in would have to discover it
        /// the same way and would only add a second place for the answer to
        /// live.
        PlayerNetworkController _controller;

        /// `netConfig` is required in every build even though only some of its
        /// numbers are read here — the same guard `ClientMatchLink`'s own
        /// constructor keeps, for the same reason: a bootstrap that forgot it
        /// is a wiring bug wherever it happens.
        public NetworkSimBackend(NetworkManager networkManager, NetConfig netConfig,
            string playerId, string joinToken)
        {
            _nm = networkManager ?? throw new System.ArgumentNullException(nameof(networkManager));
            _net = netConfig != null
                ? netConfig
                : throw new System.ArgumentNullException(nameof(netConfig));
            _playerId = playerId;
            _joinToken = joinToken;
        }

        /// Whether there is state to show (Р66). True from the first frame
        /// this class actually PUT on the render pair — not from the first
        /// frame that arrived, and emphatically not from the connection or the
        /// welcome.
        ///
        /// The stricter reading is the load-bearing one. `RenderClock` needs
        /// two DISTINCT ticks before it starts and does not place render time
        /// until the frame after that, so between the first snapshot and the
        /// first resolvable render pair there are frames on which the ring
        /// holds data and `Curr` still holds nothing. The seven views whose
        /// `World == null` test this member replaced would read that empty
        /// pair as a world at the origin.
        public bool Ready => _ready;

        /// The balance numbers the facade built from its own ScriptableObjects
        /// and handed over by value. `default` before the first `Restart`,
        /// which is what makes the interface's "answers at any time" clause
        /// true here — the facade's `RenderMuzzleHeight` reads it with no
        /// guard of its own.
        ///
        /// This client does not take the server's word for these numbers and
        /// never did: `ClientMatchLink` sends `SimConfigHash.Compute` of this
        /// very struct in the hello, and a server whose own numbers differ
        /// refuses the connection with `HandshakeRefusal.SimConfigMismatch`.
        public SimConfig Config => _cfg;

        /// The world tick of the moment ON SCREEN — `Curr`'s own tick, which
        /// is the newest half of the render pair and therefore
        /// `InterpBufferTicks` behind the newest frame received.
        ///
        /// NOT `TimeManager.LocalTick` (Р159, lesson 126). FishNet's local tick
        /// and the world's tick are two counters with no fixed offset between
        /// them — the server's world starts at zero when the MATCH starts,
        /// FishNet's tick has been running since the transport came up — so
        /// printing one where the other is meant is a number that looks right
        /// and is wrong by however long the process waited for players.
        public int CurrentTick => _curr.Tick;

        public RenderSnapshot Prev => _prev;

        /// The newer half of the render pair.
        ///
        /// `Projectiles` IS EMPTY, AND THAT IS THIS TASK'S OWN FINDING RATHER
        /// THAN A SHORTCUT. Spec §3.12 says the networked snapshot takes its
        /// rounds from the local ghosts, and the ghosts are here — but
        /// `GhostProjectiles` stores no geometry at all (its own class doc:
        /// "NO FLIGHT MATH LIVES HERE, ON PURPOSE ... geometry ... is Ф9's
        /// job"), the wire frame carries no projectile block (the five kinds
        /// are Players, Liveness, Mobs, Wave, Events), and `WeaponSystem`
        /// exposes no public seam that reproduces a predicted round's launch
        /// vector — `CanFire` and `WouldFireThisTick` are the whole of its
        /// public surface, and the spawn itself is `internal` and takes a
        /// `SimulationWorld`. So a tracer's position cannot be computed in
        /// this assembly today, from either end. Writing an integrator here
        /// off the `ProjectileSpawned` event's dir/speed/height would be a
        /// second flight model beside `ProjectileSystem`'s, in the layer least
        /// able to keep it honest. The picture this costs — own and remote
        /// tracers — is named in the task report as the decision it is.
        public RenderSnapshot Curr => _curr;

        /// `RenderClock.Phase`: the blend between the render pair's two halves
        /// (Р38), latched here every `Advance` exactly as the interface asks,
        /// so a paused facade keeps showing the phase it stopped at.
        public float Alpha => _alpha;

        public int EventCount => _frameEventCount;

        public SimEvent GetEvent(int index) => _frameEvents[index];

        /// False, and permanently: the state hash is a property of the world,
        /// the world is on the server, and this process has no way to compute
        /// it from a filtered, quantized frame. The dev overlay prints a dash
        /// rather than a plausible-looking wrong number.
        public bool HasStateHash => false;

        /// Zero, and MUST NOT BE READ while `HasStateHash` is false — it is
        /// not a hash of anything. Zero rather than a sentinel because there
        /// is no value a hash cannot legitimately take.
        public ulong StateHash => 0UL;

        /// Events this CLIENT lost, which is a different number from the
        /// server-side `NetStats.DroppedEvents` the brief for this task names.
        /// That counter belongs to the assembler's per-connection statistics
        /// and lives in the server process; this process never sees it and
        /// nothing on this side ever increments the field of the same name on
        /// the local `NetStats`, so returning it would report a permanent
        /// zero. What this side can actually lose is an accepted event with no
        /// room left to wait in, and `ClientEventQueue` counts exactly that —
        /// permanently, since the dedup has already marked the key seen by
        /// then and no resend can bring it back.
        public int DroppedEvents => _events != null ? _events.OverflowDroppedEvents : 0;

        /// Zero: there is no fixed-step accumulator on this side to throw real
        /// time away. `LocalSimBackend` reports what its clock had to discard
        /// on a long frame; the render clock corrects by changing PACE
        /// (`RenderClock`'s slew) and discards nothing, so there is no
        /// quantity here to report. A zero is therefore the true answer and
        /// not a missing measurement — but the dev overlay's line reads
        /// "0.000 s dropped" either way, which is worth knowing before it is
        /// read as evidence of a healthy clock.
        public float DroppedTime => 0f;

        /// False (CR 3). A networked client putting a mob into an
        /// authoritative world would be a client deciding a game outcome, so
        /// the dev overlay asks this before it draws the buttons at all.
        public bool CanDevSpawnMob => false;

        /// Refused, in the one way a refusal can still be seen. `CanDevSpawnMob`
        /// above is the real gate and the overlay honours it; this is the
        /// second line of defence for any future caller that does not, and it
        /// logs rather than throws because a dev convenience must not be able
        /// to take a match down.
        public void DevSpawnMob(MobType type, float2 pos)
        {
            _nm.Log("NetworkSimBackend: DevSpawnMob refused — a networked client does not put "
                + "entities into the server's world (CR 3). Ask `CanDevSpawnMob` first; it is "
                + "false on this backend and the dev overlay hides the buttons because of it.");
        }

        /// One render frame. Everything that has to happen every frame happens
        /// here, INCLUDING the two discharges — and that placement is the
        /// point, not an implementation detail.
        ///
        /// THE RING AND THE EVENT QUEUE DISCHARGE IN `Advance`, NOT IN
        /// `EndFrame` (Task 43 review, finding F-5). `SnapshotQueue`'s own doc
        /// requires `DiscardBelow` every render frame "including during
        /// `FreezeRender`", because a chain of hitstops otherwise fills the
        /// ring in a few hundred milliseconds and starts dropping snapshots
        /// along with their events. The facade calls `EndFrame` only on frames
        /// that produced a tick, so a discharge living there would stall
        /// exactly when it matters most. It calls `Advance` on every frame that
        /// is not paused, and a hitstop is not a pause — `FreezeRender` moves
        /// the render PAIR, `_paused` is a separate flag — so `Advance` is the
        /// hook that covers the case completely, with no change to the facade.
        ///
        /// `onTick` IS NEVER INVOKED. Its subscriber is the dev tick-to-hash
        /// log, and this backend has no hash (`HasStateHash`); calling it with
        /// a zero would put an invented number into the one log a determinism
        /// divergence would be found in.
        public int Advance(in SimInput frame, float unscaledDeltaTime,
            System.Action<int, ulong> onTick)
        {
            if (_snapshots == null) return 0;

            // WHERE THE INPUT WOULD GO, AND WHY IT DOES NOT.
            // `PlayerNetworkController.SetPendingInput(in frame)` is the seam
            // the frame the facade already sampled belongs in — the direction
            // chosen so `Ring.Networking` never references `Ring.Presentation`
            // back (Р35) — and it is `internal` to an assembly that does not
            // open its internals to this one. Until it does, `frame` reaches
            // nothing: prediction sends no replicate, the server sees no input,
            // and the local player has no predicted copy to draw. The frame is
            // NOT re-sampled here in any case — a second
            // `InputSampler.SampleFrame` would consume the dash edge the facade
            // has already latched (spec §3.8) — and it is not cached either,
            // because a cache with no reader is a field that only looks like a
            // solution.

            SyncPendingPoolEpoch();

            _clock.Advance(unscaledDeltaTime, in _timings);
            int renderTick = _clock.RenderTick;
            _alpha = _clock.Phase;

            // `RenderTick - 1`, which is the argument `SnapshotQueue`'s own doc
            // names: the pair being shown is `RenderTick` and the tick after
            // it, and the tick before is the headroom an ordinary reordering
            // lands in. Floored at zero because the opening ticks of a match
            // name a moment before it began.
            _snapshots.DiscardBelow((uint)math.max(0, renderTick - 1));

            if (ResolveRenderPair(renderTick)) _ready = true;

            _stale.Advance(renderTick);

            // Р67: ghosts age against the PREDICTED tick, never the render
            // tick — they are the client's own rounds, born in the prediction
            // domain. The expired ids the call hands back have no consumer
            // until the tracer views exist (see `Curr`).
            _ghosts.Advance(_nm.TimeManager.LocalTick);

            DrainDueEvents(renderTick);

            int ticks = math.max(0, renderTick - _lastRenderTick);
            _lastRenderTick = renderTick;
            return ticks;
        }

        /// Closes the window `Advance` opened, AFTER the facade has raised
        /// `TicksFlushed` — the same contract `LocalSimBackend.EndFrame`
        /// keeps, and the same consequence for inverting it: the fan-out
        /// behind that event is what reads these events, and this call is what
        /// drops them.
        ///
        /// Events drained on a frame that produced no tick are NOT lost by
        /// this: they stay in the window until a frame that did produce one,
        /// because nothing clears the window except this method.
        public void EndFrame() => _frameEventCount = 0;

        /// Records the match's balance numbers and, on the FIRST call, builds
        /// everything this backend runs on.
        ///
        /// THE FIRST CALL IS THE ONLY ONE THAT BUILDS. `ClientLinkState` is the
        /// memory of this CONNECTION — one hello, one epoch, one seat, and
        /// reconnection deliberately unimplemented until Э5 — so rebuilding it
        /// because the facade restarted would send a second hello the server
        /// answers with `DuplicatePlayer` while the first seat stays claimed,
        /// and would re-register four broadcast handlers beside the four
        /// already subscribed.
        ///
        /// A LATER CALL DOES NOT RESTART ANYTHING, AND CANNOT. On this backend
        /// a match begins and ends on the server's say-so
        /// (`MatchWelcomeNet`/`MatchRestartedNet`, the only two messages
        /// `ClientMatchReset` is ever called on); spec §3.12 lists
        /// `Restart`/`RestartNewSeed` as unavailable on a networked client for
        /// exactly that reason. The facade nonetheless raises its own
        /// `WorldRestarted` on every one of these calls, and this class has no
        /// way to stop it — the dev overlay's forced-seed restart and the death
        /// overlay's R therefore clear every Presentation-side registry
        /// mid-match while the server keeps sending the same match. Disabling
        /// those controls belongs to whoever wires this backend into a scene;
        /// it is recorded here because here is where it is visible.
        ///
        /// `seed` IS IGNORED, AND NOT MERELY UNUSED. Nothing on this side is
        /// seeded from it: the authoritative seed is the server's, arrives in
        /// the welcome, and lives in `ClientLinkState.Seed`. The facade's own
        /// `Seed` property still reports the number IT invented
        /// (`RestartNewSeed` uses the wall clock), which is a facade fact this
        /// class cannot correct and the dev overlay should not be read as the
        /// match's seed on a networked client.
        ///
        /// THE MATCH STATISTICS ARE NOT BUILT HERE AND CANNOT BE FILLED AT ALL
        /// (this task's finding). Spec §3.12 and this task's brief both list
        /// `WorldStats` as coming "from the snapshot"; the frame has no block
        /// for it and none for `MatchStats` either — Players, Liveness, Mobs,
        /// Wave and Events are the whole protocol. So `RenderSnapshot.
        /// WorldStats` and `PlayerStats` read zeros on a networked client, and
        /// every consumer of them (the dev overlay's counters, the death
        /// overlay's summary, the HUD) shows zeros with them. The numbers DO
        /// exist on the wire, once, at the end: `MatchEndedNet` carries eleven
        /// of them and `ClientLinkState.EndedNet` holds the message whole.
        /// Routing them into the end-of-match screen needs a consumer that
        /// does not exist yet; filling the per-tick snapshot from them would
        /// need a protocol change.
        public void Restart(long seed, in SimConfig cfg)
        {
            _cfg = cfg;

            if (_hasConfig)
            {
                _nm.Log("NetworkSimBackend: Restart ignored — a networked client does not start "
                    + "matches. The server's own MatchRestartedNet is what begins the next one, and "
                    + "it is the only message that clears this client's per-match seams. The "
                    + "balance numbers of this call were recorded; nothing else happened.");
                return;
            }

            _hasConfig = true;
            _timings = new NetTimings
            {
                InterpBufferTicks = _net.InterpBufferTicks,
                InterpMaxStaleTicks = _net.InterpMaxStaleTicks,
                RenderClockSnapTicks = _net.RenderClockSnapTicks,
                // The fourth field, which `NetTimings`' own doc warns is the
                // one a caller forgets: left at its C# default it does not
                // mistune the render clock's correction, it disables it.
                SlewFraction = _net.SlewFraction,
            };

            _snapshots = new SnapshotQueue(in cfg.Arena, in _timings);
            _clock = new RenderClock();
            _dedup = new EventDedup(in cfg);
            _events = new ClientEventQueue(in _timings, _net.SnapshotEventBudget);
            _ghosts = new GhostProjectiles(cfg.Arena.MaxProjectiles, _net.GhostConfirmTicks,
                GhostTrackTicks(in cfg), _stats);
            // Sized for the PLAYER SLOT space and nothing else. `StalePolicy`
            // is keyed by a DENSE index into the client's own view registry,
            // and the only dense index this client has today is the player
            // slot — the same number `MatchWelcomeNet.PlayerIndex` promised,
            // the same one `RenderSnapshot.Players` is indexed by, and the
            // same one the doll pool of Task 45 will rent by. Mobs arrive as
            // sparse `u16` wire codes with no dense space to map into yet, so
            // no mob is registered here and `StateOf` answers `Gone` for
            // every id that is not a slot. Whoever introduces that mapping
            // must start calling `OnEntitySeen` for mobs in `ReadMobs` at the
            // same time, or the fade will never start for them.
            _stale = new StalePolicy(cfg.Arena.MaxPlayers, _net.InterpMaxStaleTicks, EntityFadeTicks);
            _reset = new ClientMatchReset(_dedup, _snapshots, _clock, _ghosts, _stale, _events);

            _prev = new RenderSnapshot(in cfg.Arena);
            _curr = new RenderSnapshot(in cfg.Arena);
            _alpha = 0f;
            _lastRenderTick = 0;

            _playerScratch = new SnapshotBlocks.PlayerRecord[math.max(1, cfg.Arena.MaxPlayers)];
            _mobScratch = new SnapshotBlocks.MobRecord[math.max(1, cfg.Arena.MaxMobs)];
            _eventScratch = new SnapshotBlocks.EventRecord[math.max(1, _net.SnapshotEventBudget)];

            _frameEvents = new SimEvent[_events.Capacity];
            _pendingPool = new SimEvent[_events.Capacity];
            _freeSlots = new int[_events.Capacity];
            ReleaseEveryPendingSlot();

            // The snapshot channel is registered HERE and not in
            // `ClientMatchLink`: that class owns the match's IDENTITY — epoch,
            // seed, seat — and its own doc lists "decoding snapshot frames" as
            // this task's, explicitly. It is also the reason this class touches
            // `ClientManager` at all.
            _nm.ClientManager.RegisterBroadcast<SnapshotBroadcast>(OnSnapshotBroadcast);
            _registered = true;

            // Registering the link LAST means every seam it may be told to
            // clear already exists. Its own constructor registers four
            // handlers and proves nothing about when the transport starts —
            // the obligation to construct this backend before starting the
            // client connection is the bootstrap's, exactly as it is for
            // `MatchHandshake` on the server.
            _link = new ClientMatchLink(_nm, _reset, _net, ProtocolVersion.Current,
                SimConfigHash.Compute(in cfg), _playerId, _joinToken);
        }

        /// Refused with a log. Hot-tweak is a dev workflow over a world this
        /// process does not own (spec §3.12: unavailable on a networked
        /// client, available in solo and host mode), so there is nothing here
        /// to migrate in place.
        ///
        /// A LOG RATHER THAN THE `ArgumentException` THE INTERFACE ALLOWS, and
        /// the difference is not cosmetic: the facade CATCHES that exception
        /// and answers it by restarting on the same seed. On this backend that
        /// would turn a hot-tweak into a facade-side `WorldRestarted` and a
        /// `Restart` the server never hears about — a strictly worse outcome
        /// than the tweak simply not applying. The refusal is not silent
        /// either, because the facade sets its own `ConfigTweaked` flag
        /// whichever way this call returns, and a dev overlay reading "config
        /// tweaked" over numbers that did not move is exactly the silent loss
        /// spec §3.7 forbids.
        public void ApplyConfig(in SimConfig next)
        {
            _nm.Log("NetworkSimBackend: ApplyConfig ignored — balance numbers on a networked client "
                + "are the server's, and this side's copy is what the handshake's SimConfigHash was "
                + "computed from. Retune the assets and restart the match on both ends. Note that "
                + "SimulationRunner.ConfigTweaked is set regardless of this refusal.");
        }

        /// Nothing to settle. There is no accumulator here — the render clock
        /// integrates local time and corrects by pace — so a pause needs no
        /// per-backend bookkeeping.
        ///
        /// THE PRICE OF A PAUSE IS REAL AND IS NOT THIS CLASS'S TO CHARGE.
        /// While the facade's pause gate is closed it calls neither `Advance`
        /// nor `EndFrame`, and both discharges live in `Advance` — so the
        /// snapshot ring stops being emptied and the event queue stops being
        /// drained while frames keep arriving at 30 Hz. Within a few tenths of
        /// a second the ring begins evicting its oldest resident
        /// (`SnapshotQueue.OverflowDroppedSnapshots`) and the event queue
        /// begins refusing newcomers (`ClientEventQueue.OverflowDroppedEvents`,
        /// permanent — the dedup has already marked those keys seen). For a dev
        /// pause that is an acceptable cost; for a match it is not, and the
        /// only fix is a facade that keeps calling something while paused,
        /// which is a change to `SimulationRunner` and therefore the owner's
        /// decision rather than this class's.
        public void OnPausedChanged(bool paused)
        {
        }

        /// Drops every subscription this backend made. Required before the
        /// instance is discarded, by the delegate-identity mechanism
        /// `ClientMatchLink.Unregister`'s own doc spells out: FishNet stores
        /// handlers per delegate identity, so a second backend on the same
        /// `NetworkManager` would leave both subscribed and the stale one
        /// would keep decoding into a ring nobody reads.
        public void Unregister()
        {
            if (_registered)
            {
                _nm.ClientManager.UnregisterBroadcast<SnapshotBroadcast>(OnSnapshotBroadcast);
                _registered = false;
            }
            _link?.Unregister();
        }

        // ---- receive -------------------------------------------------------

        /// One arriving frame. Everything it can be refused for is a VALUE
        /// somewhere below, never an exception: this runs inside FishNet's own
        /// batched parsing loop, and a throw out of it abandons every message
        /// batched behind this one in the same datagram (the mechanism
        /// `ClientLinkState`'s class doc records for the four lifecycle
        /// handlers).
        void OnSnapshotBroadcast(SnapshotBroadcast msg, Channel channel)
        {
            if (_snapshots == null) return;

            _stats.BytesDown += msg.Payload.Count;
            if (msg.Payload.Array == null) return;

            SyncPendingPoolEpoch();

            ReadFrame(new System.ReadOnlySpan<byte>(msg.Payload.Array, msg.Payload.Offset,
                msg.Payload.Count));
        }

        /// The frame, from its first byte to its last.
        ///
        /// ORDER IS THE WHOLE DESIGN HERE, and each step is somebody's
        /// documented obligation:
        ///   * the header first, because `SnapshotReader` refuses a block read
        ///     before it and because the version check has to happen before any
        ///     byte whose meaning depends on the version is decoded;
        ///   * `SnapshotQueue.Admit` second, because Р150е makes THIS caller
        ///     the gate that keeps a frame from the absurd future out of
        ///     `EventDedup` — a `FutureRejected` frame's events are never
        ///     offered to the dedup at all, which is the exact path that would
        ///     otherwise drag its window forward and eat every real event
        ///     behind it until the next `Reset`;
        ///   * the blocks third, decoded into the reserved slot;
        ///   * `Commit` only after decoding finished, so `TryGet` can never
        ///     hand a consumer a half-decoded frame under a tick that was
        ///     never filled;
        ///   * `RenderClock.OnSnapshot` after that, because the clock's target
        ///     is a maximum over frames that really landed;
        ///   * `EventDedup.TryAcceptState` last, because it RECORDS as well as
        ///     answers and must only be asked when the answer is going to be
        ///     acted on.
        ///
        /// A FRAME WHOSE STATE IS REFUSED STILL DELIVERS ITS EVENTS, and that
        /// asymmetry is deliberate (spec §3.7's refinement of Р31): a packet
        /// that merely overtook another would otherwise swallow a death that
        /// was never shown. Only `ForeignEpoch` and `FutureRejected` refuse a
        /// frame whole.
        void ReadFrame(System.ReadOnlySpan<byte> source)
        {
            var reader = new SnapshotReader(source);
            if (!reader.TryReadHeader(out ushort epoch, out uint tick, out byte flags))
            {
                // NOT COUNTED, AND THAT IS A GAP RATHER THAN A CHOICE:
                // `NetStats`' composition was closed by Task 23 and carries no
                // field for a frame that failed to parse. `StaleSnapshots` and
                // `DuplicateSnapshots` both mean something else, and folding a
                // malformed frame into either would hide an attack inside a
                // packet-loss statistic.
                _nm.Log($"NetworkSimBackend: snapshot header refused — versionMismatch="
                    + $"{reader.VersionMismatch} truncated={reader.Truncated}. The frame is dropped "
                    + "whole; nothing on this side counts it.");
                return;
            }

            SnapshotQueue.AdmitVerdict verdict = _snapshots.Admit(epoch, tick, out RenderSnapshot slot);
            switch (verdict)
            {
                case SnapshotQueue.AdmitVerdict.ForeignEpoch:
                case SnapshotQueue.AdmitVerdict.FutureRejected:
                    return;
                case SnapshotQueue.AdmitVerdict.Stale:
                    _stats.StaleSnapshots++;
                    break;
                case SnapshotQueue.AdmitVerdict.Duplicate:
                    _stats.DuplicateSnapshots++;
                    break;
            }

            bool applyState = _dedup.TryAcceptState(epoch, tick);
            if (slot != null) BeginSlot(slot, tick);

            while (reader.TryReadBlock(KnownBlockKinds, out byte kind,
                       out System.ReadOnlySpan<byte> payload))
            {
                switch ((SnapshotBlockKind)kind)
                {
                    case SnapshotBlockKind.Players:
                        ReadPlayers(slot, payload, tick, applyState);
                        break;
                    case SnapshotBlockKind.Liveness:
                        // KNOWN, DECODED BY NOBODY, AND SAID OUT LOUD. The mask
                        // is the roster of every slot in the match and its own
                        // consumer is the spectate candidate list of Task 47
                        // (Р70); `RenderSnapshot` has no field it could be
                        // written into, and inventing a member here with no
                        // reader would be a feature without a cause. What it
                        // costs meanwhile: a slot that is alive but out of
                        // sight is indistinguishable, on this side, from a
                        // slot that is dead — both read `Alive == false`,
                        // because the Players block only carries who is
                        // VISIBLE.
                        break;
                    case SnapshotBlockKind.Mobs:
                        ReadMobs(slot, payload);
                        break;
                    case SnapshotBlockKind.Wave:
                        ReadWave(slot, payload);
                        break;
                    case SnapshotBlockKind.Events:
                        ReadEvents(epoch, tick, payload);
                        break;
                }
            }

            if (slot != null)
            {
                _snapshots.Commit(tick);
                _clock.OnSnapshot(tick, epoch);
            }

            if (applyState)
                _stale.OnFrameApplied(tick, (flags & SnapshotHeaderFlags.Truncated) != 0);
        }

        /// Clears the reserved slot before a byte of this frame is decoded into
        /// it. The ring hands back a RECYCLED `RenderSnapshot` — whatever tick
        /// used to live in that slot is still in its arrays — and a frame under
        /// fog of war carries only what this client may see, so anything not
        /// re-decoded below would otherwise be read as this tick's truth.
        ///
        /// `PlayerCount` IS THE WHOLE ROSTER, NOT THE RECORD COUNT. The array
        /// index of a player IS the player's slot everywhere in this project —
        /// `MatchWelcomeNet.PlayerIndex`, `RenderSnapshot.LocalPlayerIndex`,
        /// the doll pool of Task 45 — so records are scattered by their own
        /// `Index` rather than packed, and the slots no record arrived for read
        /// `default(PlayerState)`: not alive, at the origin.
        void BeginSlot(RenderSnapshot slot, uint tick)
        {
            slot.Tick = (int)tick;
            slot.LocalPlayerIndex = LocalPlayerIndex;
            slot.PlayerCount = _cfg.Arena.MaxPlayers;
            for (int i = 0; i < slot.PlayerCount; i++)
            {
                slot.Players[i] = default;
                slot.PlayerStats[i] = default;
            }
            slot.MobCount = 0;
            slot.ProjectileCount = 0;
            slot.Wave = default;
            slot.WorldStats = default;
        }

        /// The other players this client may see, turned into whole
        /// `PlayerState`s by the one mapping that owns that table
        /// (`PlayerFlags.ToSyntheticState`, Task 44a) — never by a second copy
        /// of it here.
        ///
        /// THE LOCAL PLAYER IS NEVER IN THIS BLOCK, by the assembler's own
        /// rule ("Never oneself: one's own state comes back through
        /// reconciliation, not the snapshot"). That is why `ApplyOwnPlayer`
        /// exists and why the prediction seam being unreachable costs the
        /// local player's whole picture rather than merely its smoothness.
        ///
        /// `hp01` IS A DIVISION BACK, AND IT ROUND-TRIPS. `TryReadPlayersBlock`
        /// already scaled the wire byte to absolute HP against `Hero.MaxHp`,
        /// while `ToSyntheticState` takes a normalized value and scales it by
        /// the same number; dividing here re-normalizes the value the decoder
        /// just built rather than re-deriving it from the wire, so the two
        /// scalings cannot disagree about which `MaxHp` was meant.
        void ReadPlayers(RenderSnapshot slot, System.ReadOnlySpan<byte> payload, uint tick,
            bool applyState)
        {
            if (!SnapshotBlocks.TryReadPlayersBlock(payload, in _cfg,
                    new System.Span<SnapshotBlocks.PlayerRecord>(_playerScratch),
                    out int count, out SnapshotBlockError error))
            {
                LogBlockRefusal(SnapshotBlockKind.Players, error);
                return;
            }

            float maxHp = _cfg.Hero.MaxHp;
            for (int i = 0; i < count; i++)
            {
                SnapshotBlocks.PlayerRecord r = _playerScratch[i];

                // Only frames whose state was accepted may move a liveness
                // fact: `StalePolicy`'s numbers are monotonic maxima and a
                // reordered frame filling a hole in the ring has nothing newer
                // to say about who was seen when.
                if (applyState) _stale.OnEntitySeen(r.Index, tick);

                if (slot == null) continue;
                float hp01 = maxHp > 0f ? r.Hp / maxHp : 0f;
                slot.Players[r.Index] = PlayerFlags.ToSyntheticState(r.Flags, r.Pos, r.Dir, hp01,
                    in _cfg);
            }
        }

        /// The mobs this client may see, as a DENSE list keyed by id — which is
        /// what `ViewRegistry` diffs against to rent and retire its views, so
        /// the order and the count are the contract, not the array position.
        ///
        /// `MobState.Vel` IS LEFT AT ZERO, DELIBERATELY. The record carries a
        /// unit HEADING (`normalizesafe(Vel)` on the sending side), not a
        /// velocity, and writing the heading into a velocity field would state
        /// 1 m/s where the truth is unknown. Nothing in Presentation reads
        /// `MobState.Vel` (verified by grep over the whole layer), and
        /// `MobVisual` derives its locomotion from the transform's own frame
        /// delta, so the honest zero costs nothing today and would cost a
        /// wrong number tomorrow. `StateTimer`/`FireCooldown`/`StrafeSign` are
        /// simply not on the wire.
        void ReadMobs(RenderSnapshot slot, System.ReadOnlySpan<byte> payload)
        {
            if (!SnapshotBlocks.TryReadMobsBlock(payload, in _cfg,
                    new System.Span<SnapshotBlocks.MobRecord>(_mobScratch),
                    out int count, out SnapshotBlockError error))
            {
                LogBlockRefusal(SnapshotBlockKind.Mobs, error);
                return;
            }

            if (slot == null) return;
            for (int i = 0; i < count; i++)
            {
                SnapshotBlocks.MobRecord r = _mobScratch[i];
                slot.Mobs[i] = new MobState
                {
                    Id = r.Id,
                    Type = r.Type,
                    Ai = r.Ai,
                    Pos = r.Pos,
                    Hp = r.Hp,
                };
            }
            slot.MobCount = count;
        }

        /// The wave director's public face. `PendingChasers`/`PendingGunners`/
        /// `PhaseTimer` are not on the wire — they are the director's own
        /// bookkeeping and no client draws them — so they stay at zero rather
        /// than being guessed from the counts that are.
        void ReadWave(RenderSnapshot slot, System.ReadOnlySpan<byte> payload)
        {
            if (!SnapshotBlocks.TryReadWaveBlock(payload, out WavePhase phase, out ushort waveIndex,
                    out byte aliveCount, out SnapshotBlockError error))
            {
                LogBlockRefusal(SnapshotBlockKind.Wave, error);
                return;
            }

            if (slot == null) return;
            slot.Wave = new WaveState
            {
                Phase = phase,
                WaveIndex = waveIndex,
                AliveCount = aliveCount,
            };
        }

        /// This frame's events, each asked about exactly once.
        ///
        /// THE DEDUP OWNS THE KEY AND HANDS BACK THE TICK. `TryAcceptEvent`
        /// performs the one subtraction the whole scheme rests on — the
        /// frame's tick minus the record's delta — and returns the result
        /// through `out originTick` (its fix round 1, F-2). That number is
        /// passed straight to the queue below; deriving it a second time here
        /// would put two derivations behind one key, and the value has to match
        /// exactly or the event is shown on the wrong frame. It means something
        /// ONLY when the call returned true.
        void ReadEvents(ushort epoch, uint frameTick, System.ReadOnlySpan<byte> payload)
        {
            if (!SnapshotBlocks.TryReadEventsBlock(payload, in _cfg,
                    new System.Span<SnapshotBlocks.EventRecord>(_eventScratch),
                    out int count, out SnapshotBlockError error))
            {
                // Records decoded BEFORE the refusal are still in the scratch
                // and still counted (`SnapshotBlockError.DestinationTooSmall`'s
                // own doc: "Read `count` in both cases"), so the walk below
                // runs either way and the refusal is only logged.
                LogBlockRefusal(SnapshotBlockKind.Events, error);
            }

            for (int i = 0; i < count; i++)
            {
                SnapshotBlocks.EventRecord record = _eventScratch[i];
                if (!_dedup.TryAcceptEvent(epoch, frameTick, in record, out uint originTick))
                    continue;
                if (!TryDecodeEvent(originTick, in record, payload, out SimEvent decoded)) continue;
                EnqueueEvent(originTick, in record, in decoded);
            }
        }

        /// One wire event, turned into the `SimEvent` the whole Presentation
        /// fan-out already speaks. This is the INVERSE of the mapping
        /// `SnapshotAssembler` applies on the way out, and it is written here
        /// because there is no inverse anywhere else — the assembler's own
        /// switch is a one-way function over types this assembly cannot reach.
        ///
        /// THE TWO ENUMERATIONS DO NOT LINE UP, and the shape of the mismatch
        /// is the assembler's, not this method's: one `ProjectileFired`
        /// becomes `ProjectileSpawned` for whoever the round flies near and
        /// `ShotHeard` for whoever merely hears it, and four projectile endings
        /// collapse into one `ProjectileEnded` discriminated by
        /// `ProjectileEndKind`. Both halves of the first split map back to
        /// `ProjectileFired`, because a connection receives one or the other
        /// for a given shot and the audible variant's whole purpose is to be
        /// heard as a shot.
        ///
        /// WHAT THE WIRE CANNOT GIVE BACK, named rather than guessed:
        ///   * a `HitMob`/`HitPlayer` ending carries the ROUND's id and not the
        ///     victim's, so `EntityId` stays 0 — `SimEvent.SecondaryEntityId`'s
        ///     own doc establishes 0 as "none", and the round's id goes there,
        ///     which is exactly the convention the simulation itself uses for
        ///     these two kinds. The cost is that `GameFeelDirector`'s
        ///     per-mob hit flash has no view to look up on a networked client;
        ///     the round's own end still retires its tracer.
        ///   * `StaminaDenied` carries no slot at all (it reaches its owner and
        ///     nobody else), so the local slot is the only honest answer.
        ///   * `PlayerSlideStarted` carries no direction, and `DashRicocheted`
        ///     carries the surface normal that the simulation puts in `HitDir`.
        ///   * a `ShotHeard` carries no direction either, so the fire angle
        ///     `SimEvent.Amount` means for `ProjectileFired` reads zero. Its
        ///     position has already been coarsened by the server (Task 20), and
        ///     a client that draws a muzzle flash from it will draw one through
        ///     a wall — the flash's own branch is Task 45's, and this is where
        ///     it is written down.
        bool TryDecodeEvent(uint originTick, in SnapshotBlocks.EventRecord record,
            System.ReadOnlySpan<byte> blockPayload, out SimEvent e)
        {
            e = default;

            var kind = (SnapshotEventKind)record.Kind;
            System.ReadOnlySpan<byte> slice = blockPayload.Slice(record.PayloadOffset,
                record.PayloadLength);
            if (!SnapshotEvents.TryReadPayload(kind, slice, in _cfg,
                    out SnapshotEventPayload p, out SnapshotBlockError error))
            {
                // An unknown kind lands here too, and it is NOT an error (Р29):
                // Task 27's own walk already skipped past its bytes correctly,
                // and a receiver of an older build simply has nothing to show
                // for it.
                LogBlockRefusal(SnapshotBlockKind.Events, error);
                return false;
            }

            e.Tick = (int)originTick;
            e.Pos = record.Pos;
            e.PlayerIndex = ProjectileIds.NoOwner;

            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned:
                    e.Kind = SimEventKind.ProjectileFired;
                    e.EntityId = p.Id;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Owner = p.PlayerIndex == ProjectileIds.NoOwner
                        ? ProjectileOwner.Mob : ProjectileOwner.Player;
                    // `Amount` is the shot's sim-plane velocity angle for this
                    // kind (the field's own doc); the wire carries the unit
                    // direction the angle is of.
                    e.Amount = math.atan2(p.Dir.y, p.Dir.x);
                    break;

                case SnapshotEventKind.ShotHeard:
                    e.Kind = SimEventKind.ProjectileFired;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Owner = p.PlayerIndex == ProjectileIds.NoOwner
                        ? ProjectileOwner.Mob : ProjectileOwner.Player;
                    break;

                case SnapshotEventKind.ProjectileEnded:
                    switch (p.EndKind)
                    {
                        case ProjectileEndKind.Blocked:
                            e.Kind = SimEventKind.ProjectileBlocked;
                            e.EntityId = p.Id;
                            // `Amount` carries the contact HEIGHT for this kind
                            // — the same field the sending side read it out of.
                            e.Amount = p.Height;
                            break;
                        case ProjectileEndKind.Expired:
                            e.Kind = SimEventKind.ProjectileExpired;
                            e.EntityId = p.Id;
                            break;
                        case ProjectileEndKind.HitMob:
                            e.Kind = SimEventKind.ProjectileHit;
                            e.SecondaryEntityId = p.Id;
                            e.Zone = p.Zone;
                            break;
                        case ProjectileEndKind.HitPlayer:
                            e.Kind = SimEventKind.ProjectileHitPlayer;
                            e.SecondaryEntityId = p.Id;
                            e.Zone = p.Zone;
                            break;
                        default:
                            return false;
                    }
                    break;

                case SnapshotEventKind.MobSpawned:
                    e.Kind = SimEventKind.MobSpawned;
                    e.EntityId = p.Id;
                    e.MobType = p.MobType;
                    break;

                case SnapshotEventKind.MobDied:
                    e.Kind = SimEventKind.MobDied;
                    e.EntityId = p.Id;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Zone = p.Zone;
                    break;

                case SnapshotEventKind.PlayerDamaged:
                    e.Kind = SimEventKind.PlayerDamaged;
                    // VICTIM in both fields, which is this kind's own
                    // convention on the simulation side (`SimEvent.PlayerIndex`
                    // mirrors `EntityId` for the two player-victim kinds).
                    e.EntityId = p.PlayerIndex;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Zone = p.Zone;
                    e.Amount = p.Amount;
                    e.HitDir = p.Dir;
                    break;

                case SnapshotEventKind.PlayerDied:
                    e.Kind = SimEventKind.PlayerDied;
                    e.EntityId = p.PlayerIndex;
                    e.PlayerIndex = p.PlayerIndex;
                    e.Zone = p.Zone;
                    break;

                case SnapshotEventKind.PlayerDashed:
                    e.Kind = SimEventKind.PlayerDashed;
                    e.PlayerIndex = p.PlayerIndex;
                    break;

                case SnapshotEventKind.PlayerSlideStarted:
                    e.Kind = SimEventKind.PlayerSlideStarted;
                    e.PlayerIndex = p.PlayerIndex;
                    break;

                case SnapshotEventKind.DashRicocheted:
                    e.Kind = SimEventKind.DashRicocheted;
                    e.PlayerIndex = p.PlayerIndex;
                    e.HitDir = p.Dir;
                    break;

                case SnapshotEventKind.StaminaDenied:
                    e.Kind = SimEventKind.StaminaDenied;
                    e.PlayerIndex = LocalPlayerIndex;
                    e.Amount = p.Amount;
                    break;

                case SnapshotEventKind.WaveStarted:
                    e.Kind = SimEventKind.WaveStarted;
                    // `EntityId` is the wave index for these two kinds
                    // (`WaveSystem`'s own emit sites).
                    e.EntityId = p.WaveIndex;
                    break;

                case SnapshotEventKind.WaveCleared:
                    e.Kind = SimEventKind.WaveCleared;
                    e.EntityId = p.WaveIndex;
                    break;

                default:
                    return false;
            }

            RouteToGhosts(kind, in p);
            return true;
        }

        /// The two wire events the ghost registry answers to, and nothing else.
        ///
        /// `Confirm` IS GATED ON THE OWNER, AND THE GATE IS LOAD-BEARING.
        /// `ProjectileSpawned` is sent for every round this client can see, not
        /// only for its own, while `GhostProjectiles.Confirm` matches
        /// POSITIONALLY against the oldest unconfirmed ghost and has no
        /// identity to refuse a stranger by. An unfiltered call would therefore
        /// pair another player's round with this client's own tracer — the
        /// wrong-identity match that class's KNOWN LIMIT paragraph describes,
        /// caused here rather than merely tolerated. The end event needs no
        /// such gate: it is looked up by a server id this registry either holds
        /// or does not.
        void RouteToGhosts(SnapshotEventKind kind, in SnapshotEventPayload p)
        {
            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned:
                    if (p.PlayerIndex == LocalPlayerIndex) _ghosts.Confirm(p.Id, 0u);
                    break;
                case SnapshotEventKind.ProjectileEnded:
                    _ghosts.TryTranslateEnd(p.Id, out int _);
                    break;
            }
        }

        // ---- the pending window --------------------------------------------

        /// Files a decoded event under the absolute tick it happened on.
        ///
        /// WHY THE DECODED EVENT IS HELD HERE AND NOT IN THE QUEUE (this task's
        /// finding about `ClientEventQueue`). That class stores an
        /// `EventRecord` by value and calls it self-contained because the
        /// struct has no reference fields — but two of its fields,
        /// `PayloadOffset`/`PayloadLength`, ARE a reference: they index into
        /// the block payload span that was passed to `TryReadEventsBlock`, and
        /// that span is a slice of FishNet's receive buffer, valid only for the
        /// duration of the broadcast handler. By the time the render clock
        /// reaches the event's tick those bytes are long gone, so the queue
        /// alone can never deliver an event's payload. The fix that costs
        /// nothing on either side is to decode while the bytes are still there
        /// and carry the RESULT: this pool holds the finished `SimEvent`, and
        /// the record filed in the queue keeps the pool slot in
        /// `PayloadOffset`, which is the one field of it this class reads back.
        ///
        /// The pool is exactly the queue's own capacity, so a slot is available
        /// whenever the queue has room, and a refused enqueue hands its slot
        /// straight back.
        void EnqueueEvent(uint originTick, in SnapshotBlocks.EventRecord record, in SimEvent decoded)
        {
            if (_freeCount == 0) return;

            int slot = _freeSlots[--_freeCount];
            _pendingPool[slot] = decoded;

            SnapshotBlocks.EventRecord filed = record;
            filed.PayloadOffset = (ushort)slot;
            filed.PayloadLength = 0;

            if (!_events.Enqueue(originTick, in filed)) _freeSlots[_freeCount++] = slot;
        }

        /// Moves every event whose tick the render clock has reached into this
        /// frame's window. `ClientEventQueue.TryDequeue` answers only for
        /// records that are actually due, which is the whole reason the wait
        /// exists: the world on screen is `InterpBufferTicks` behind the newest
        /// frame buffered, and an event shown at arrival time would land on a
        /// world that has not got there.
        ///
        /// The window is bounded by the queue's own capacity, so an event that
        /// does not fit this frame is LEFT in the queue rather than dropped —
        /// it is due, and it will be first out on the next frame.
        void DrainDueEvents(int renderTick)
        {
            while (_frameEventCount < _frameEvents.Length
                   && _events.TryDequeue(renderTick, out ClientEventQueue.PendingEvent pending))
            {
                int slot = pending.Record.PayloadOffset;
                _frameEvents[_frameEventCount++] = _pendingPool[slot];
                _freeSlots[_freeCount++] = slot;
            }
        }

        /// The pool has to be emptied exactly when `ClientEventQueue.Reset`
        /// empties the queue, or its slots leak — every event still waiting
        /// when a match ends never comes back to hand its slot over.
        ///
        /// IT IS OBSERVED RATHER THAN CALLED, and deliberately so.
        /// `ClientMatchReset` is the ONE handler that clears the per-match
        /// seams, its own doc argues at length for why there is one call site
        /// and not six, and it is reached from inside `ClientMatchLink` rather
        /// than from here. Adding a seventh seam to it would be a change to a
        /// closed task and would owe a test in `MatchLifecycleTests` besides.
        /// The epoch the link tracks moves on exactly the two messages that
        /// reset — the opening welcome and `MatchRestartedNet` — so watching it
        /// is watching the same fact, one step removed.
        void SyncPendingPoolEpoch()
        {
            if (_link == null) return;
            ushort epoch = _link.State.MatchEpoch;
            if (epoch == _poolEpoch) return;
            _poolEpoch = epoch;
            ReleaseEveryPendingSlot();
            _frameEventCount = 0;
        }

        void ReleaseEveryPendingSlot()
        {
            _freeCount = _freeSlots.Length;
            for (int i = 0; i < _freeCount; i++) _freeSlots[i] = i;
        }

        // ---- the render pair -----------------------------------------------

        /// Deep-copies the two halves of Р38's render pair out of the ring:
        /// the snapshot AT `renderTick` and the one after it, blended by
        /// `Phase`.
        ///
        /// A MISSING HALF IS NOT A REASON TO SHOW NOTHING. With one half
        /// resident both ends of the blend are that one, so the picture holds
        /// still instead of interpolating toward a moment nobody sent; with
        /// neither, the previous pair is left exactly as it was, which is the
        /// freeze `StalePolicy` then has an opinion about. A hole in the ring
        /// is ordinary at the 5% loss every playtest build must survive, and
        /// the buffer exists to absorb it.
        bool ResolveRenderPair(int renderTick)
        {
            if (renderTick < 0) return false;

            bool hasOlder = _snapshots.TryGet((uint)renderTick, out RenderSnapshot older);
            bool hasNewer = _snapshots.TryGet((uint)renderTick + 1u, out RenderSnapshot newer);
            if (!hasOlder && !hasNewer) return false;
            if (!hasOlder) older = newer;
            if (!hasNewer) newer = older;

            _prev.CopyFrom(older);
            _curr.CopyFrom(newer);
            ApplyOwnPlayer(_prev);
            ApplyOwnPlayer(_curr);
            return true;
        }

        /// Puts this client's own player back into the picture the snapshot
        /// deliberately left it out of.
        ///
        /// FROM THE PREDICTED COPY, AND ONLY WHILE IT IS PREDICTING.
        /// `PlayerPredictionCore.IsPredicting` is exactly "this client may
        /// advance its own copy at all" — false before the first reconcile has
        /// described the player, and false for good once the player dies — and
        /// both of those cases are ones where the snapshot's own record is the
        /// better answer. The catch is that the record does not exist: the
        /// assembler never puts a connection's own slot in its own frame, so
        /// while prediction is not running this slot reads `default`.
        ///
        /// TODAY IT NEVER RUNS AT ALL. `Configure` and `SetPendingInput` are
        /// `internal` to `Ring.Networking` (see the class doc), so the
        /// controller stays inert, its predicted copy stays `default`,
        /// `IsPredicting` stays false, and the local player is absent from the
        /// picture. That is the single most visible consequence of the three
        /// unreachable seams, and it is why they are reported as a blocker
        /// rather than as a rough edge.
        void ApplyOwnPlayer(RenderSnapshot snapshot)
        {
            EnsureController();
            if (_controller == null || !_controller.Core.IsPredicting) return;

            int index = snapshot.LocalPlayerIndex;
            if (index < 0 || index >= snapshot.PlayerCount) return;
            snapshot.Players[index] = _controller.Core.Predicted;
        }

        /// Finds this client's own player object once FishNet has spawned it.
        /// The search is over the local connection's OWN objects, which is the
        /// same table `NetworkObject.InitializeEarly` fills on both ends, so
        /// nothing here has to know how the object was spawned or by whom. A
        /// match restart spawns NEW objects on the same slots (Р164), so the
        /// cached reference is dropped as soon as it stops being spawned.
        void EnsureController()
        {
            if (_controller != null && _controller.IsSpawned) return;
            _controller = null;

            if (_nm.ClientManager.Connection == null) return;

            foreach (NetworkObject nob in _nm.ClientManager.Connection.Objects)
            {
                if (nob == null) continue;
                if (nob.TryGetComponent(out PlayerNetworkController controller))
                {
                    _controller = controller;
                    return;
                }
            }
        }

        // ---- small helpers --------------------------------------------------

        /// This client's own slot, as the welcome assigned it. Zero before the
        /// welcome arrives — which is a legal slot rather than a sentinel, and
        /// harmless only because nothing reads the render pair before `Ready`,
        /// and `Ready` cannot be true before a frame of the tracked epoch has
        /// been decoded.
        byte LocalPlayerIndex => _link != null ? _link.State.PlayerIndex : (byte)0;

        /// `GhostProjectiles`' registry-hygiene ceiling, in ticks — see
        /// `GhostTrackMarginTicks`.
        static int GhostTrackTicks(in SimConfig cfg)
            => (int)math.ceil(cfg.Weapon.ProjectileLifetime / SimulationWorld.TickDt)
               + GhostTrackMarginTicks;

        void LogBlockRefusal(SnapshotBlockKind kind, SnapshotBlockError error)
        {
            _nm.Log($"NetworkSimBackend: {kind} block refused — {error}. The rest of the frame is "
                + "still walked; a refusal here is ordinary traffic on an untrusted path (Р82), not "
                + "a reason to abandon the datagram.");
        }
    }
}
