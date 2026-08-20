using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using Ring.Data;
using Ring.Networking;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Presentation.Net
{
    /// The `ISimBackend` WITH NO WORLD (Stage 2 Task 44c, spec §3.12): the one
    /// that receives snapshots instead of ticking a `SimulationWorld`. Task 43
    /// split producing state from showing it precisely so this class could
    /// exist; `LocalSimBackend` is its twin on the other side of that seam, and
    /// every member below answers the same question its counterpart there does,
    /// from a frame off the wire rather than from a world in memory.
    ///
    /// IT LIVES IN AN ASSEMBLY OF ITS OWN, ABOVE BOTH (Stage 2 Task 44d, the
    /// owner's decision of 2026-08-10). `Ring.Presentation.Net` references
    /// `Ring.Presentation`, `Ring.Networking` and `FishNet.Runtime`; the
    /// alternatives were both worse. Leaving the class in `Ring.Presentation`
    /// meant leaving that assembly's reference to FishNet in place, and
    /// `Presentation/` is the client track's own folder — its agents read
    /// `client/CLAUDE.md`, which has no network stack in it, so the reference
    /// made that document false for everybody working there. Moving the class
    /// into `Ring.Networking` instead was impossible rather than merely
    /// unattractive: `ISimBackend` lives in `Ring.Presentation`, and a
    /// `Ring.Networking` -> `Ring.Presentation` reference closes the assembly
    /// cycle Р35 exists to prevent. A third assembly ON TOP of both closes
    /// neither door. The folder is a new root, `Assets/Scripts/PresentationNet/`,
    /// rather than a subfolder of `Presentation/`, because ownership in
    /// `.github/CODEOWNERS` is handed out BY PATH: a subfolder of a
    /// colleague-owned path stays colleague-owned however its assembly is
    /// drawn.
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
    /// all six on an epoch change and `ClientMatchLink` says when that is. A
    /// tenth was written for the job in fix-round 1 rather than found:
    /// `MobTypeMemory`, because the death event arrives without the archetype
    /// the simulation put in it and the Mobs block is the only place that
    /// survives on this side.
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
    /// THE THREE SEAMS TASK 44c COULD NOT REACH ARE REACHED NOW (Task 44d).
    /// `PlayerNetworkController.Configure`, `SetPendingInput` and
    /// `NotifyOwnDeath` are still `internal` to `Ring.Networking` — that is
    /// what keeps Р34 structural — but `Networking/AssemblyInfo.cs` now names
    /// this assembly beside the test one, which is what the owner chose over
    /// widening the three to `public`. All three are called below, and each
    /// says at its call site why it is called from there:
    /// `Configure` the moment the controller is found, `SetPendingInput` with
    /// the input the facade hands over ON REQUEST from `TimeManager_OnPreTick`
    /// (Stage 2 app-b3z — the facade no longer samples ahead of this class and
    /// this class no longer collects a ready frame), `NotifyOwnDeath` off the
    /// decoded `PlayerDied` that names this client's own seat.
    ///
    /// NOTHING INSTALLS THIS BACKEND YET, AND THAT IS A RECORDED DEBT RATHER
    /// THAN AN OVERSIGHT. `SimulationRunner.TryUseBackend` is the seam and it
    /// exists as of Task 44d; the caller — the scene wiring, the mode choice
    /// and `ClientManager.StartConnection` — is Task 44e, the task
    /// immediately after this one, by the owner's decision. Contract before
    /// consumer is the order this phase has already used three times (Task
    /// 44a's protocol before 44b's link, 44b's link before 44c's backend).
    /// Whoever writes that caller inherits two obligations named elsewhere in
    /// this file: construct and `Restart` this backend BEFORE starting the
    /// client connection (see `Restart`), and `Unregister` any instance it
    /// discards (see `Unregister`).
    ///
    /// WHAT IS DELIBERATELY NOT HERE. `ObservedIndex` — which seat this client
    /// is LOOKING FROM — belongs to `SimulationRunner` and not to a backend: it
    /// is a property of this client rather than of the world, it changes
    /// between ticks, and putting it in the frame would make it something the
    /// hitstop freeze had to carry. What Task 47b did put here is the half a
    /// backend owns: the request that asks the server to move the viewpoint
    /// (`TryRequestSpectate`) and the window an answer to it may arrive in.
    /// Also not here: the player dolls and their pool (Task 45), the dev
    /// overlay's network section (Task 48) and the walls (Task 46). Two
    /// further gaps are this task's own findings rather than its neighbors'
    /// scope, and both are recorded where they bite: the projectile picture
    /// (see `Curr`) and the match statistics (see `HasMatchStats`).
    public sealed class NetworkSimBackend : ISimBackend
    {
        /// How often the dev diagnostics line is written, in seconds of facade
        /// frame time (`LogDiagnosticsTick`). A STRUCTURAL CONSTANT, not
        /// balance: it tunes how coarse a log a playtest leaves behind, not
        /// anything the game plays with (CR 6 is about the numbers a match is
        /// decided by). Why one second rather than one line per frame is
        /// argued once, in `LogDiagnosticsTick`'s own doc ("ONE LINE PER
        /// SECOND, NOT PER FRAME") — not repeated here.
        const float DiagnosticsLogIntervalSeconds = 1f;

        /// The block kinds this receiver understands. A kind absent from this
        /// list is walked past and counted by `SnapshotReader.
        /// SkippedBlockCount` (Р29), which would misreport a
        /// forward-compatibility statistic for a block this client does in fact
        /// know. All five are decoded as of Stage 2 Task 47a — `Liveness` was
        /// the one that was listed here and read by nobody, for want of a field
        /// to write it into (see `ReadLiveness`).
        static readonly byte[] KnownBlockKinds =
        {
            (byte)SnapshotBlockKind.Players,
            (byte)SnapshotBlockKind.Liveness,
            (byte)SnapshotBlockKind.Mobs,
            (byte)SnapshotBlockKind.Wave,
            (byte)SnapshotBlockKind.Events,
        };

        /// The same five kinds as a bit per kind — the set a COMPLETE frame
        /// carries, which `ReadFrame` tests the walk against (fix-round 1,
        /// F-1). Every one of them is required because the SENDER sends every
        /// one of them: `SnapshotAssembler` writes all five on every frame,
        /// empty or not, and says why in its own comment — a receiver cannot
        /// tell an absent block from an empty one, because a datagram cut on a
        /// block boundary parses as a shorter, valid snapshot. So a kind
        /// missing here is not "the server had nothing to say"; it is the tail
        /// of the frame missing, and everything the walk did not re-decode is
        /// still `BeginSlot`'s zeros.
        ///
        /// A BIT PER KIND RATHER THAN A COUNT: two copies of one kind and one
        /// each of two kinds are different frames, and a count cannot tell
        /// them apart. The shift is safe because `TryReadBlock` only ever
        /// delivers a kind out of `KnownBlockKinds` above.
        const int RequiredBlockKinds =
            (1 << (byte)SnapshotBlockKind.Players)
            | (1 << (byte)SnapshotBlockKind.Liveness)
            | (1 << (byte)SnapshotBlockKind.Mobs)
            | (1 << (byte)SnapshotBlockKind.Wave)
            | (1 << (byte)SnapshotBlockKind.Events);

        /// How many seats the Liveness mask can speak about, derived from the
        /// protocol's own payload size rather than restated as an 8 (Stage 2
        /// Task 47a): the block is one byte and a bit per seat, so widening it
        /// on the wire moves this number with it instead of leaving a literal
        /// here to go stale. See `ReadLiveness` for what happens to a seat past
        /// the ceiling.
        const int LivenessMaskSeats = SnapshotBlocks.LivenessBlockPayloadBytes * 8;

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

        /// How long the incoming byte RATE is averaged over, in seconds
        /// (Stage 2 Task 48). One second, and the reason is the reader rather
        /// than the arithmetic: the number is printed as "per second", so a
        /// window of exactly that is the one that needs no explaining, and it
        /// spans about 30 snapshot frames at the shipped tick rate — enough
        /// that one large datagram cannot dominate the figure, short enough
        /// that the panel reacts within a second of the traffic changing. A
        /// STRUCTURAL CONSTANT, not balance (CR 6 is about the numbers a match
        /// is decided by), so it lives here and not in an `.asset`.
        const float BytesRateWindowSeconds = 1f;

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

        // The seven per-match seams, plus the two objects that own their reset
        // and their epoch. All built by the first `Restart`, because the
        // facade does not hand `SimConfig` over until then and four of the
        // eight cannot be built without it: `SnapshotQueue` (the arena sizes
        // its ring of `RenderSnapshot`s), `EventDedup`, `GhostProjectiles`
        // (the projectile cap) and `StalePolicy` (the player cap). Of the
        // other four, `ClientEventQueue` is sized from `NetTimings`/
        // `NetConfig`, `ClientMatchLink` needs the config's HASH and its
        // roster size rather than its dimensions, and `RenderClock` and
        // `ClientMatchReset` are sized from nothing at all — they are built
        // here because the objects they are handed are, not because a number
        // of the config reaches them.
        SnapshotQueue _snapshots;
        RenderClock _clock;
        EventDedup _dedup;
        ClientEventQueue _events;
        GhostProjectiles _ghosts;
        StalePolicy _stale;
        ClientMatchReset _reset;
        ClientMatchLink _link;

        /// Which archetype each recently-seen mob id was — NOT a seventh seam
        /// (Stage 2 Task 44d fix-round 1). It is this class's own memory of a
        /// block this class decodes, `ClientMatchReset` is a closed task whose
        /// own doc argues for one call site rather than six, and the epoch
        /// change is already observed here (`SyncMatchEpoch`) for two other
        /// things that have to go with a match.
        MobTypeMemory _mobTypes;

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

        /// bd `app-s0u`: this client's copy of the rounds in flight, rebuilt
        /// from `ProjectileSpawned`/`ProjectileEnded`. The snapshot carries no
        /// projectile block, so without this the render pair's `Projectiles`
        /// stays at `BeginSlot`'s zeros and no bullet is ever drawn.
        TracerProjectiles _tracers;
        float _alpha;
        bool _ready;
        int _lastRenderTick;

        // An epoch change has been observed and `MatchRestarted` has not been
        // raised for it yet. See `Advance` for why the raise waits.
        bool _matchRestartedPending;

        // The local player's own predicted copy, as of the last two PREDICTION
        // ticks. See `SampleOwnPlayer`.
        PlayerState _ownPrev, _ownCurr;
        uint _ownTick;
        bool _hasOwnSample;

        // Seconds left of the window the last `SpectateRequestNet` may still be
        // answered in — see `TryRequestSpectate`. Zero means "nothing is
        // waiting, and the next request may go".
        float _spectateRequestWindow;

        // Decode scratch, sized once from the arena caps.
        //
        // NOTHING ON THE RECEIVE PATH ALLOCATES AFTER `Restart` RETURNS,
        // EXCEPT A REFUSAL LOG — the exception named rather than left to be
        // measured (fix-round 1, F-8). Every `_nm.Log` call below builds its
        // interpolated string BEFORE the logger's own level filter ever sees
        // it, so a refusal costs garbage whether or not the line is printed.
        // What keeps that off the ordinary path is where the calls sit: one
        // per refused FRAME or per refused BLOCK, never one per record — and
        // the one refusal a healthy connection produces in bulk, the Р29
        // forward-compatibility skip of an event kind this build has never
        // heard of, is not logged at all (see `ReadEvents`).
        SnapshotBlocks.PlayerRecord[] _playerScratch;
        SnapshotBlocks.MobRecord[] _mobScratch;
        SnapshotBlocks.EventRecord[] _eventScratch;

        // This flush's event buffer — the `EventCount`/`GetEvent` window the
        // facade opens with `Advance` and closes with `EndFrame`.
        SimEvent[] _frameEvents;
        int _frameEventCount;

        // The epoch everything above is keyed to, as last observed on the link.
        // See `SyncMatchEpoch`.
        ushort _matchEpoch;

        // Stage 2 Task 48 — the dev overlay's network section.
        //
        // Frames that arrived with entities missing: either the SENDER dropped
        // some for room (the header's `Truncated` bit) or the frame turned up
        // without all five blocks it must carry. `ReadFrame` already computes
        // that exact test for `StalePolicy`; this counts it. It is the honest
        // client-side neighbor of the server's `NetStats.DroppedEntities`,
        // which lives in the other process and which nothing here can read.
        int _framesMissingEntities;

        // The byte-rate derivative. `NetStats.BytesDown` is a running total
        // and the panel wants a rate, so the division is done HERE, by the one
        // object that is handed the frame time — `OnGUI` runs several times
        // per rendered frame and has no interval it could honestly divide by.
        long _bytesDownAtWindowStart;
        float _bytesRateWindowSeconds;

        /// Seconds of facade frame time since the last diagnostics line
        /// (`LogDiagnosticsTick`, bd `app-0h0`). Counted in the same clock the
        /// bytes-rate window uses, so a paused client logs nothing.
        float _diagLogSeconds;
        float _bytesDownPerSecond;

        // The NEXT frame's length is not an interval this client spent
        // receiving, so it must not become part of a window (fix-round 1,
        // F-1). Raised by `NotifyEngineIdle` and spent by the next
        // `UpdateBytesRate` whatever that frame looks like — the same
        // one-frame shape, and for the same reason, as
        // `FixedStepAccumulator.IgnoreNextFrameGap`.
        bool _bytesRateIgnoreNextFrame;

        /// This client's own player object once FishNet has spawned it.
        /// Found rather than injected: the object is spawned by the server
        /// mid-match, and the one authority on which object is ours is
        /// FishNet's own ownership table (`NetworkConnection.Objects`,
        /// maintained by `NetworkObject.InitializeEarly` on both ends). A
        /// bootstrap that passed the reference in would have to discover it
        /// the same way and would only add a second place for the answer to
        /// live.
        PlayerNetworkController _controller;

        /// The facade's offer of this render frame's input (Stage 2 app-b3z).
        /// Injected rather than reached for: this assembly is the only one in
        /// Presentation with FishNet on its references, and the boundary holds
        /// precisely because it runs one way — `Ring.Presentation` must not
        /// learn what a `TimeManager` is, so the facade cannot subscribe to the
        /// tick itself, and a reference to the facade held here would open all
        /// of it for the sake of one question. See `TimeManager_OnPreTick`.
        readonly FrameInputRequest _requestFrameInput;

        /// `netConfig` is required in every build even though only some of its
        /// numbers are read here — the same guard `ClientMatchLink`'s own
        /// constructor keeps, for the same reason: a bootstrap that forgot it
        /// is a wiring bug wherever it happens. `requestFrameInput` is guarded
        /// the same way and for a sharper reason: without it this backend has
        /// no input path AT ALL after app-b3z (`Advance` no longer reads the
        /// frame the facade hands it), so a caller that omitted it would get a
        /// client that connects, renders and never moves.
        public NetworkSimBackend(NetworkManager networkManager, NetConfig netConfig,
            string playerId, string joinToken, FrameInputRequest requestFrameInput)
        {
            _nm = networkManager ?? throw new System.ArgumentNullException(nameof(networkManager));
            _net = netConfig != null
                ? netConfig
                : throw new System.ArgumentNullException(nameof(netConfig));
            _requestFrameInput = requestFrameInput
                ?? throw new System.ArgumentNullException(nameof(requestFrameInput));
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
        ///
        /// AND IT GOES BACK TO FALSE ON AN EPOCH CHANGE (fix-round 1, F-6).
        /// A new match empties the ring, stops the clock and leaves the render
        /// pair holding the LAST PICTURE OF THE MATCH THAT ENDED — see
        /// `SyncMatchEpoch`, which is where the change is observed. Answering
        /// true across that gap would not merely be early; it would be a
        /// backend vouching for a picture it knows is of another match, with
        /// `CurrentTick` naming that match's tick beside it.
        public bool Ready => _ready;

        /// The balance numbers the facade built from its own ScriptableObjects
        /// and handed over by value. `default` before the first `Restart`,
        /// which is what makes the interface's "answers at any time" clause
        /// true here — the facade's `RenderMuzzleHeight` reads it with no
        /// guard of its own.
        ///
        /// THE NUMBERS OF THE FIRST `Restart`, FOR THE LIFE OF THIS BACKEND
        /// (fix-round 1, F-4). A later `Restart` does not replace them, and
        /// that is what keeps the sentence below literally true: everything
        /// this class sized from the config — the ring's snapshots, the three
        /// decode scratches, the dedup, the ghosts, the stale policy, the
        /// render pair — was sized from THIS struct, and so was the hash the
        /// hello carried. A backend that recorded a second config while
        /// keeping the first one's arrays would be indexing one arena's
        /// buffers with another arena's caps.
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
        /// (Р38), latched rather than derived live exactly as the interface
        /// asks, so a paused facade keeps showing the phase it stopped at.
        ///
        /// LATCHED WITH THE PAIR, NOT MERELY EVERY `Advance` (fix-round 1,
        /// F-2). A phase belongs to the two halves it blends; when the ring
        /// holds neither half of `RenderTick`'s pair, this class leaves the
        /// pair alone and therefore leaves the phase alone with it. See
        /// `Advance` for what advancing one without the other looks like on
        /// screen.
        ///
        /// IT NO LONGER GOVERNS THIS CLIENT'S OWN SLOT, AND NOBODY HAS TO KNOW
        /// THAT (app-5fh). Both halves of the pair THIS class publishes carry
        /// the same local pose, so blending them by this number is an identity
        /// whatever the number is — which is how that slot got out of this
        /// clock's domain without a second phase on the seam. `BlendOwnPlayer`
        /// has the argument.
        ///
        /// THE IDENTITY IS ABOUT THIS PAIR, NOT ABOUT EVERY PAIR A CONSUMER MAY
        /// HOLD. Views read `SimulationRunner.RenderPrev`/`RenderCurr`, and
        /// during the hitstop catch-up window that facade pairs a FROZEN buffer
        /// with the live one and eases its own coefficient across them
        /// (`SimulationRunner.cs:270-272`). The two halves it hands out are then
        /// genuinely different, including for the local seat, and the identity
        /// above says nothing about that window. It is the behavior hitstop
        /// already had and this fix neither improves nor worsens it.
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

        /// False, and permanently — the same answer as `HasStateHash` and for
        /// a closely related reason (Stage 2 Task 44d, the owner's decision of
        /// 2026-08-10). `RenderSnapshot.WorldStats` and `PlayerStats` are
        /// counted by the world, and the frame has no block for either of them:
        /// Players, Liveness, Mobs, Wave and Events are the whole protocol,
        /// and a sixth block is deliberately not being added — these are not
        /// per-frame quantities and the per-frame budget (Р146) is spent on
        /// the ones that are. `BeginSlot` clears both before decoding, so
        /// without this member every consumer would read a permanent zero as a
        /// measurement: no kills, no waves cleared, and — worse, because the
        /// dev overlay colors them red above zero — no skipped spawns.
        ///
        /// THE NUMBERS DO ARRIVE, ONCE, AT THE END. `MatchEndedNet` carries
        /// eleven of them and `ClientLinkState.EndedNet` holds the message
        /// whole. What is missing is a consumer: the end-of-match screen reads
        /// the render pair today, and pointing it at the link's summary is not
        /// this task's.
        public bool HasMatchStats => false;

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
        /// not a missing measurement — but the dev overlay prints this number
        /// through the same `DroppedTime: 0.000` counter line it prints for a
        /// local backend, red only above zero, which is worth knowing before a
        /// permanent zero there is read as evidence of a healthy clock.
        public float DroppedTime => 0f;

        /// False (CR 3). A networked client putting a mob into an
        /// authoritative world would be a client deciding a game outcome, so
        /// the dev overlay asks this before it draws the buttons at all.
        public bool CanDevSpawnMob => false;

        /// Refused, in the one way a refusal can still be seen. `CanDevSpawnMob`
        /// above is the real gate and the overlay honors it; this is the
        /// second line of defense for any future caller that does not, and it
        /// logs rather than throws because a dev convenience must not be able
        /// to take a match down.
        public void DevSpawnMob(MobType type, float2 pos)
        {
            _nm.Log("NetworkSimBackend: DevSpawnMob refused — a networked client does not put "
                + "entities into the server's world (CR 3). Ask `CanDevSpawnMob` first; it is "
                + "false on this backend and the dev overlay hides the buttons because of it.");
        }

        /// False, and permanently (Stage 2 Task 47b, the owner's decision 4b).
        /// A match on this backend begins and ends on the server's say-so, and
        /// `Restart` below refuses every call after the first — so a restart
        /// button wired to the facade could only ever do nothing at all. The
        /// death screen asks this and hides the button rather than offering a
        /// choice that is not there.
        public bool CanRestartMatch => false;

        /// True: there is a server to ask, and `RequestSpectate` below is how.
        /// Not gated on being dead, on the link's phase or on the seat — those
        /// are the SERVER's decision (`SpectatePolicy.Evaluate` refuses a live
        /// requester first of all), and a client that pre-judged them would be
        /// a second, quietly diverging copy of a rule that already has one home.
        public bool CanRequestSpectate => true;

        /// Sends one `SpectateRequestNet` through `ClientMatchLink` — the one
        /// place in this project that speaks to the server — and opens the
        /// window `SpectateRequestInFlight` reports on.
        ///
        /// THE WINDOW IS `NetConfig.SpectatorSwitchCooldownSeconds`, NOT A
        /// NUMBER OF THIS CLASS'S OWN. It is the same field `ServerBootstrap`
        /// converts into `SpectatePolicy`'s tick cooldown, so the client asks no
        /// faster than the server can accept, and a request the server refuses
        /// stops being waited for after exactly the interval that permits the
        /// next one.
        ///
        /// IN SECONDS, DELIBERATELY, AND MEASURED IN RENDER TIME. The asset
        /// states seconds; the two tick counters this process holds are FishNet's
        /// `LocalTick` and the world tick of the frame on screen, and neither is
        /// this client's own clock — the second one even rewinds to zero when the
        /// server restarts the match. Counting the interval down in the same
        /// `unscaledDeltaTime` the facade already hands `Advance` needs no
        /// conversion, no tick rate and no epoch arithmetic.
        public bool TryRequestSpectate(int targetIndex)
        {
            if (_link == null) return false;
            if (_spectateRequestWindow > 0f) return false;
            // The wire field is a byte, the same width as `MatchWelcomeNet.
            // PlayerIndex`; a seat that cannot be named is a seat that cannot
            // be asked for.
            if (targetIndex < 0 || targetIndex > byte.MaxValue) return false;

            _link.RequestSpectate((byte)targetIndex);
            _spectateRequestWindow = _net.SpectatorSwitchCooldownSeconds;
            return true;
        }

        public bool SpectateRequestInFlight => _spectateRequestWindow > 0f;

        /// `StalePolicy.FadeProgress` for the player slot, and nothing else
        /// (Stage 2 Task 47c, bd `app-wcy`). The decision is the policy's — how
        /// long a slot may go unheard before it freezes, when the fade may start
        /// at all, and when it must hold still because the whole connection is
        /// quiet — and this member is the wire out of it, which is the one thing
        /// the policy has lacked since Task 37 wrote it. Zero before the first
        /// `Restart` has built one: there is no picture then either.
        public float PlayerFadeProgress(int slot)
            => _stale != null ? _stale.FadeProgress(slot) : 0f;

        /// True while the policy still has something to show for the slot.
        /// `Gone` is its terminal reading — permanent until a fresh sighting,
        /// and also what a slot the policy was never told about reads — so it is
        /// the one answer that means "let the doll go", and this member is that
        /// test and no other. A doll therefore cannot be stranded by a seat
        /// nothing is tracking, and before the first `Restart` there is no
        /// policy at all and the answer is the same `false`.
        ///
        /// IT STAYS TRUE THROUGH A CONNECTION STALL, DELIBERATELY. While the
        /// policy reports global starvation it hands out no fade progress and
        /// reaches no terminal state, so a stranger's doll is held, frozen and
        /// at whatever brightness it had reached, for as long as the silence
        /// lasts. That is the intended reading of Р39/Р77 and not a leak: the
        /// dolls held this way are bounded by the roster, they stay in
        /// `_activePlayers` where `ViewRegistry.Clear` reaches them, and a
        /// connection that is genuinely down is what the connection indicator is
        /// for — killing the picture on top of it would say "everyone left"
        /// where the truth is "nobody is being heard".
        public bool ShouldKeepPlayerDoll(int slot)
            => _stale != null && _stale.StateOf(slot) != StalePolicy.StaleState.Gone;

        /// The dev overlay's whole network section, in one snapshot (Stage 2
        /// Task 48). `false` before the first `Restart` has built the seams —
        /// there is no ring, no clock and no queue to describe then, and a
        /// section drawn out of a `default` struct would be a page of zeros
        /// that look like measurements.
        ///
        /// EVERY FIELD IS READ HERE, IN ONE PLACE, ON ONE FRAME. That is the
        /// point of the member: `NetDiagnostics`' own doc explains why
        /// twenty-two interface calls from `OnGUI` would be twenty-two different
        /// moments, and this is where the single moment is taken.
        ///
        /// WHAT IS DELIBERATELY NOT IN IT, and this is a finding of this task
        /// rather than a shortcut:
        ///   * BYTES UP. `NetStats.BytesUp` exists and NOTHING ON THIS SIDE
        ///     EVER WRITES IT — the one client-side increment in the project
        ///     is `BytesDown` in `OnSnapshotBroadcast`, and the upstream
        ///     traffic that would dominate the figure is FishNet's own
        ///     replicate data, which never passes through this class at all.
        ///     FishNet does measure both directions, in
        ///     `NetworkTrafficStatistics`, and it is unreachable from code:
        ///     `StatisticsManager.TryGetNetworkTrafficStatistics`
        ///     (FishNet 4.7.2, `Runtime/Managing/Statistic/
        ///     StatisticsManager.cs:34`) hands the object out only when
        ///     `IsEnabled()` says so, and that method's first test is
        ///     `_enableMode == EnabledMode.Disabled`
        ///     (`NetworkTrafficStatistics.cs:267-269`). `_enableMode` is a
        ///     `[SerializeField]` whose only accessor is the getter
        ///     `EnableMode` — no setter, unlike its neighbors `_updateClient`/
        ///     `_updateServer`, which have `SetUpdateClient`/`SetUpdateServer`
        ///     (same file, :54-57 and :74). So turning it on is a scene
        ///     edit. Counting only this class's own sends and calling the
        ///     result "up" would be an instrument that lies. The panel prints
        ///     a dash and says why. THIS NARROWS spec §3.12 and plan Т48,
        ///     which both ask for bytes/s in BOTH directions: the outgoing
        ///     half is not deferred work, it is a limit of the pinned package,
        ///     and it is recorded as a decision here and in `NetDiagnostics`'
        ///     own doc rather than left as a silent gap.
        ///   * `InputStarved`, `InputOverwritten`, `DroppedEntities`,
        ///     `EdgeRequestsRejected`. All four are the SERVER's
        ///     per-connection counters (`MatchServer`, `SnapshotAssembler`);
        ///     this process holds a `NetStats` of its own on which they are
        ///     permanently zero. `FramesMissingEntities` is what this side can
        ///     honestly say about the third of them.
        public bool TryGetNetDiagnostics(out NetDiagnostics diagnostics)
        {
            if (_snapshots == null)
            {
                diagnostics = default;
                return false;
            }

            PlayerPredictionCore core = _controller != null ? _controller.Core : null;

            diagnostics = new NetDiagnostics
            {
                // FishNet's own figure, and its own caveat with it: the
                // package documents this as INCLUDING the latency of the tick
                // rate (TimeManager.cs:104), so it is not a pure wire ping and
                // the panel's label says so.
                RoundTripMs = (int)_nm.TimeManager.RoundTripTime,
                RenderTick = _clock.RenderTick,
                // The clock's own "am I running" flag, public since before
                // this task and left out of the first snapshot by oversight
                // (fix-round 1, F-2). Without it the panel printed `render 0`
                // and a `behind` the size of the server's tick number for as
                // long as the clock was still waiting for its second distinct
                // tick.
                HasRenderTick = _clock.Started,
                // Clamped into the `int` the panel prints rather than cast
                // blind: the queue stores the wire's `uint`, and an
                // out-of-range cast in C# produces an unspecified number
                // instead of an error (the same Р82 reasoning
                // `RenderClock.OnSnapshot` refuses such a tick for).
                NewestServerTick = (int)math.min(_snapshots.NewestTick, (uint)int.MaxValue),
                HasNewestServerTick = _snapshots.HasNewestTick,
                BytesDownPerSecond = _bytesDownPerSecond,
                // Zero corrections is what a client with no player object yet
                // has genuinely seen, so a missing controller and a fresh one
                // answer the same thing — and the panel prints a dash off the
                // count either way.
                CorrectionCount = core != null ? core.CorrectionCount : 0,
                CorrectionMedianMeters = core != null ? core.CorrectionMedianMeters : 0f,
                StaleSnapshots = _stats.StaleSnapshots,
                DuplicateSnapshots = _stats.DuplicateSnapshots,
                DroppedSnapshots = _snapshots.OverflowDroppedSnapshots,
                FramesMissingEntities = _framesMissingEntities,
                UnconfirmedGhosts = _stats.UnconfirmedGhosts,
                SnapshotQueueCount = _snapshots.Count,
                SnapshotQueueDepth = _snapshots.Depth,
                EventQueueCount = _events.Count,
                EventQueueCapacity = _events.Capacity,
                ClockSlewSign = _clock.SlewSign,
                ClockSnaps = _clock.Snaps,
            };

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // The APPLIED simulator facts, and they live on the LINK rather
            // than on this class's `_stats`: the simulator is one instance for
            // the whole transport, so what it reports describes this PROCESS,
            // and `ClientMatchLink` is the object that applies it (on the
            // connection's `Started`) and therefore the object that owns the
            // answer. Guarded because `DevStats` itself is — the whole
            // simulator path is compiled only into development builds, and
            // `NetDiagnostics` keeps one shape across both configurations by
            // leaving these at their C# defaults in a release build, exactly
            // as `NetStats` does.
            if (_link != null)
            {
                NetStats devStats = _link.DevStats;
                diagnostics.LatencySimActive = devStats.LatencySimActive;
                diagnostics.LatencySimRttMs = devStats.LatencySimRttMs;
                diagnostics.LatencySimLossPercent = devStats.LatencySimLossPercent;
            }
#endif

            return true;
        }

        /// There is no `DroppedTime` here to excuse — this side owns no
        /// `FixedStepAccumulator` and answers a permanent zero for one — and
        /// the render clock takes care of a long frame by itself, refusing a
        /// delta that is not a positive finite number and snapping forward
        /// onto its target past the configured threshold; neither behavior is
        /// something a caller may switch off.
        ///
        /// BUT THIS CLASS DID ACQUIRE A CLOCK OF ITS OWN IN TASK 48, AND IT IS
        /// EXACTLY WHAT AN ENGINE-SIDE GAP RUINS (fix-round 1, F-1 — this
        /// paragraph used to say there was nothing here at all, which stopped
        /// being true in the commit that wrote it). The byte-rate window
        /// counts FRAME time, and the frame that follows this callback carries
        /// the whole idle stretch as one delta (the owner measured 79 seconds
        /// of it): the window it closed would spread the traffic of an instant
        /// over a minute and a half and print a rate near zero over a
        /// connection receiving normally.
        ///
        /// EXCUSING THE FRAME, NOT MERELY RESTARTING THE WINDOW, is what that
        /// takes. A restart alone leaves the idle delta itself inside the new
        /// window, which is the lie above; the flag keeps the one frame that
        /// measures nothing out of the arithmetic entirely, and the window
        /// starts from the frame after it. Until it closes, the panel keeps
        /// showing the last whole second the game ran — see
        /// `RestartBytesRateWindow`.
        public void NotifyEngineIdle()
        {
            _bytesRateIgnoreNextFrame = true;
        }

        /// One rendered frame of the incoming byte rate. Latched once per
        /// whole window rather than smoothed per frame, so the printed figure
        /// is a real average over a stated interval instead of a filter whose
        /// time constant a reader would have to know to interpret it.
        void UpdateBytesRate(float unscaledDeltaTime)
        {
            // A frame length that is not a positive finite number is not a
            // frame — the same refusal `RenderClock.Advance` makes, and for
            // the same reason: a NaN here would poison the window forever.
            if (!math.isfinite(unscaledDeltaTime) || unscaledDeltaTime <= 0f) return;

            // The one frame the engine itself was not running through (see
            // `NotifyEngineIdle`). Its length is idle wall time, not receiving
            // time, so the window does not count it and starts from here
            // instead. Spent on ANY frame, exactly like the accumulator's own
            // excuse: a short resume frame consumes it too, which costs one
            // window and cannot hide a real change in the traffic.
            if (_bytesRateIgnoreNextFrame)
            {
                _bytesRateIgnoreNextFrame = false;
                RestartBytesRateWindow();
                return;
            }

            _bytesRateWindowSeconds += unscaledDeltaTime;
            if (_bytesRateWindowSeconds < BytesRateWindowSeconds) return;

            _bytesDownPerSecond = (_stats.BytesDown - _bytesDownAtWindowStart) / _bytesRateWindowSeconds;
            RestartBytesRateWindow();
        }

        /// The averaging window starts again from NOW, because the interval it
        /// had been counting is no longer an interval this client spent
        /// receiving (Stage 2 Task 48 fix-round 1, F-1).
        ///
        /// WHAT WOULD OTHERWISE BE PRINTED. The dividend of this rate is
        /// `NetStats.BytesDown`, written from FishNet's broadcast handler,
        /// which knows nothing of the facade's pause gate. The divisor is
        /// frame time, which the facade stops handing over the moment that
        /// gate closes — `SimulationRunner.Update` returns before it reaches
        /// `Advance`. A pause therefore freezes the divisor while the dividend
        /// keeps climbing at the snapshot rate, and the first window to close
        /// after resuming would divide a whole pause's worth of bytes by about
        /// one second: a spike that never crossed the wire, printed at exactly
        /// the moment a reader is most likely to be looking, since reading a
        /// dev panel begins with pausing. `NotifyEngineIdle` above is the same
        /// defect from the other end.
        ///
        /// STARTING OVER COSTS ONE WINDOW AND CANNOT INVENT ANYTHING. Until
        /// the new window closes the panel keeps showing the last whole second
        /// the game actually ran — a figure that was measured, and that no
        /// pause can push above the real rate. Zeroing the rate instead would
        /// print "nothing is arriving" over a connection receiving normally,
        /// which is the same instrument lying in the other direction.
        ///
        /// THREE CALLERS, ONE MEANING — "the window that was running describes
        /// nothing any more". `UpdateBytesRate` calls it having just latched a
        /// finished window; `OnPausedChanged` on either edge, though only
        /// leaving pause carries the fix (entering reaches the state leaving
        /// would produce anyway, since the window cannot advance while no
        /// frame time arrives, so the caller states the fact once instead of
        /// naming a direction the rule does not have); and the excused frame
        /// of `NotifyEngineIdle`, which is the one case where the window must
        /// also NOT count the delta that ended it.
        void RestartBytesRateWindow()
        {
            _bytesDownAtWindowStart = _stats.BytesDown;
            _bytesRateWindowSeconds = 0f;
        }

        /// THIS CLIENT'S OWN INPUT, TAKEN AND HANDED TO PREDICTION INSIDE THE
        /// TICK THAT REPLICATES IT (Stage 2 app-b3z, Р35, spec §3.8).
        ///
        /// FishNet raises `OnPreTick` from inside `TimeManager.IncreaseTick`'s
        /// tick loop, and the rest of that same pass is what turns the answer
        /// into a datagram: `OnTick` a few statements later is
        /// `PlayerNetworkController.TimeManager_OnTick` ->
        /// `BuildReplicate` -> `PlayerPredictionCore.PendingInput`, and
        /// `PredictionManager.SendStateUpdate` closes the pass. So the input a
        /// player gives on a frame leaves the process on that frame.
        ///
        /// WHAT THIS REPLACED, AND WHY IT COULD NOT BE FIXED WHERE IT STOOD.
        /// The call used to sit in `Advance`, which the facade reaches from its
        /// own `Update`. `NetworkManager` and the reader loop that drives the
        /// tick are each `[DefaultExecutionOrder(short.MinValue)]` while
        /// `SimulationRunner` is pinned at -50, so on every frame that ticked,
        /// the tick had already read `PendingInput` before the facade wrote it
        /// — the replicate carried the PREVIOUS frame's input, about 16 ms at
        /// 60 fps and up to a whole tick once the frame rate falls to the tick
        /// rate, on top of the network's own delay. No number in the execution
        /// order table could have closed that: `short.MinValue` is the floor.
        /// Moving the SAMPLE was the only cure, and moving only the delivery
        /// would have been the worse outcome of the two — the facade's
        /// `LastFrameInput` holds the sample of frame N-1 at this moment, so
        /// reading it here would have sent the very same bytes and looked
        /// repaired.
        ///
        /// IT SAMPLES NOTHING ITSELF, and asks a facade that is free to say no.
        /// `SimulationRunner.TrySampleFrameInput` owns both halves of Р35 — one
        /// sample per render frame whoever asks first, and no sample at all
        /// while the pause gate is closed — and its doc carries the reasons,
        /// including the muzzle flash a pause-blind sample would draw over the
        /// menu. A refusal leaves `PendingInput` alone, so prediction keeps
        /// replicating the last input it was given, which is what a paused
        /// client did before this method existed.
        ///
        /// `_controller` IS NOT LOOKED UP HERE. `EnsureController` runs once
        /// per frame in `Advance` and is the single owner of that search (its
        /// own doc says so); this method uses whatever that left behind, and
        /// the alternative would be a second home for a per-frame search on the
        /// hottest path this class has.
        ///
        /// AND IT COSTS NOTHING, WHICH IS WORTH STATING BECAUSE IT LOOKS LIKE
        /// IT SHOULD (fix-round 1, Ф-2, correcting a cost this doc invented).
        /// Before the first find this method does return early — but the tick
        /// it returns on was building no replicate either: `EnsureController`
        /// calls `Configure` at the moment it finds the object, and until that
        /// call `PlayerNetworkController.TimeManager_OnTick` refuses on
        /// `!_configured` before it ever reaches `BuildReplicate`. So no tick
        /// is skipped and none is emptied; the first replicate a match produces
        /// is built on the first tick after the find, and it carries the input
        /// of THAT frame — fresher than the pre-app-b3z arrangement, where it
        /// would have carried the input of the frame the find happened on.
        void TimeManager_OnPreTick()
        {
            if (_controller == null) return;
            if (!_requestFrameInput(out SimInput frame)) return;

            _controller.SetPendingInput(in frame);
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
        ///
        /// `frame` IS NEVER READ EITHER, AND THAT IS A DECISION RATHER THAN AN
        /// OVERSIGHT (Stage 2 app-b3z). It used to feed exactly one line —
        /// `_controller.SetPendingInput` — and that line moved to
        /// `TimeManager_OnPreTick` above, where the tick that replicates the
        /// input is the one that samples it. This implementation therefore
        /// takes nothing from the facade's render frame; it asks the facade for
        /// the input itself, in the tick domain, and the facade answers with
        /// the same single sample its own `Update` will use. The parameter
        /// stays in the signature because the INTERFACE needs it: on
        /// `LocalSimBackend` this argument is not a message, it is the
        /// simulation — `_world.Tick(SimInputFrame.ForTick(frame, i))` — and a
        /// seam shaped around the one implementation that has no world would
        /// be the wrong seam.
        public int Advance(in SimInput frame, float unscaledDeltaTime,
            System.Action<int, ulong> onTick)
        {
            if (_snapshots == null) return 0;

            SyncMatchEpoch();

            // RAISED HERE AND NOWHERE ELSE, THOUGH THE EPOCH CHANGE IS USUALLY
            // NOTICED IN THE BROADCAST HANDLER. Behind this event sit nine
            // ordinary Presentation components, and a throw out of any of them
            // inside FishNet's batched parsing loop abandons every message
            // behind this one in the same datagram. Deferring the raise to the
            // facade's own call stack costs at most one render frame and
            // removes that whole class of failure — the same "a refusal is a
            // value, never an exception" rule the receive path keeps, applied
            // to a notification.
            if (_matchRestartedPending)
            {
                _matchRestartedPending = false;
                MatchRestarted?.Invoke();
            }

            // ONE LOOKUP PER FRAME, SHARED BY EVERYTHING BELOW THAT NEEDS THE
            // CONTROLLER. It used to run twice — once per half of the render
            // pair — with the same answer both times.
            EnsureController();

            // The predicted pair for the local slot, sampled once per
            // PREDICTION tick — see `SampleOwnPlayer`.
            SampleOwnPlayer();

            // The one clock behind `SpectateRequestInFlight`, counted down in
            // the frame time the facade already hands over. It stops while the
            // facade is paused, which is correct rather than incidental: a
            // paused client is not receiving the picture that would confirm a
            // request either.
            if (_spectateRequestWindow > 0f)
                _spectateRequestWindow = math.max(0f, _spectateRequestWindow - unscaledDeltaTime);

            UpdateBytesRate(unscaledDeltaTime);

            _clock.Advance(unscaledDeltaTime, in _timings);
            int renderTick = _clock.RenderTick;

            LogDiagnosticsTick(unscaledDeltaTime, renderTick);

            // `RenderTick - 1`, which is the argument `SnapshotQueue`'s own doc
            // names: the pair being shown is `RenderTick` and the tick after
            // it, and the tick before is the headroom an ordinary reordering
            // lands in. Floored at zero because the opening ticks of a match
            // name a moment before it began.
            _snapshots.DiscardBelow((uint)math.max(0, renderTick - 1));

            // THE PHASE MOVES ONLY WITH THE PAIR IT BLENDS (fix-round 1, F-2).
            // Latching `RenderClock.Phase` unconditionally was the one way this
            // class could make a freeze visibly WORSE than a stall: with the
            // ring starved, `ResolveRenderPair` leaves `_prev`/`_curr` exactly
            // as they were — two poses one tick apart — while `Phase` keeps
            // sawing 0->1 every world tick off local time, and every consumer
            // blends the two frozen halves by it. The picture would not hold
            // still; it would oscillate across one tick of motion, at the
            // render rate, for as long as the hole lasts. Holding the phase
            // instead holds the pose actually on screen: the frame the pair
            // resolves again is the one that moves the picture, which is what
            // the interpolation buffer exists to make ordinary.
            if (ResolveRenderPair(renderTick))
            {
                _alpha = _clock.Phase;
                _ready = true;

                // bd `app-s0u`. BOTH halves, and each with its OWN tick: the
                // pair is `renderTick` and `renderTick + 1`, and the renderer
                // blends them by `_alpha`, so writing one state into both would
                // freeze every tracer between ticks while the rest of the world
                // slid. `ResolveRenderPair` has just overwritten these arrays
                // wholesale (`CopyFrom`), which is why this runs after it and
                // not before.
                _prev.ProjectileCount = _tracers.WriteInto(_prev.Projectiles, renderTick);
                _curr.ProjectileCount = _tracers.WriteInto(_curr.Projectiles, renderTick + 1);
                // Pruned by the OLDER tick, so a round ending on the newer half
                // is still drawn on the older one (see `Prune`'s own doc).
                _tracers.Prune(renderTick);
            }

            // AND THE LOCAL SEAT IS FILLED AFTER IT, EVERY FRAME, WHETHER OR
            // NOT THE PAIR ABOVE MOVED (app-5fh + app-0t6). Freezing the
            // picture that came off the wire is the paragraph above; freezing
            // this client's own PREDICTION with it is the opposite of what
            // prediction is for — it exists so that one's own movement does not
            // wait on somebody else's datagram (CR 3), and while the two writes
            // lived inside the branch above a hole in the ring stopped the local
            // doll dead and then jumped it forward when the hole closed. It runs
            // after the resolve rather than before it because `CopyFrom`
            // overwrites the slot it writes. What pose it writes, and why the
            // pair's two halves both get the same one, is `BlendOwnPlayer`.
            BlendOwnPlayer();

            _stale.Advance(renderTick);

            // Р67: ghosts age against the PREDICTED tick, never the render
            // tick — they are the client's own rounds, born in the prediction
            // domain. The expired ids the call hands back still have no consumer
            // even now that the tracer views exist (see `Curr`).
            _ghosts.Advance(_nm.TimeManager.LocalTick);

            DrainDueEvents(renderTick);

            int ticks = math.max(0, renderTick - _lastRenderTick);
            _lastRenderTick = renderTick;
            return ticks;
        }

        /// One line of network diagnostics per second, into the player log
        /// (bd `app-0h0`). Dev builds only, by the same `#if` every other dev
        /// surface in this project uses.
        ///
        /// WHY IT EXISTS. Task 48 built the dev overlay, and milestone В1's
        /// whole output is "the numbers from the overlay" — but the overlay
        /// draws to a SCREEN, and nothing on this side ever wrote those numbers
        /// anywhere they survive the session. The first playtest ran, produced
        /// symptoms nobody could quantify afterwards, and the player log had
        /// not one line about the wire in it. A milestone measured in numbers
        /// needs those numbers recorded, not photographed.
        ///
        /// ONE LINE PER SECOND, NOT PER FRAME. At 300 fps a per-frame line is
        /// twenty thousand lines a minute and a log nobody opens twice; at one
        /// per second a twenty-minute playtest is twelve hundred lines and a
        /// time series that can be read with `grep`. The cadence is counted in
        /// the frame time the facade already hands over, exactly as the
        /// bytes-per-second window is (see `UpdateBytesRate`), so a paused
        /// client writes nothing rather than filling the log with the same
        /// frozen row.
        ///
        /// THE FIELDS ARE THE PANEL'S OWN, THROUGH THE PANEL'S OWN SEAM —
        /// `TryGetNetDiagnostics`, not a second gathering of the same numbers
        /// (rule 2). What the panel shows and what the log records can
        /// therefore never disagree.
        void LogDiagnosticsTick(float unscaledDeltaTime, int renderTick)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _diagLogSeconds += unscaledDeltaTime;
            if (_diagLogSeconds < DiagnosticsLogIntervalSeconds) return;
            _diagLogSeconds = 0f;

            if (!TryGetNetDiagnostics(out NetDiagnostics d)) return;

            _nm.Log("NetDiag "
                + $"render={(d.HasRenderTick ? d.RenderTick.ToString() : "-")} "
                + $"newest={(d.HasNewestServerTick ? d.NewestServerTick.ToString() : "-")} "
                + $"behind={(d.HasRenderTick && d.HasNewestServerTick ? (d.NewestServerTick - d.RenderTick).ToString() : "-")} "
                + $"localTick={_nm.TimeManager.LocalTick} "
                + $"rttMs={d.RoundTripMs} "
                + $"slewSign={d.ClockSlewSign} snaps={d.ClockSnaps} "
                + $"queue={d.SnapshotQueueCount}/{d.SnapshotQueueDepth} "
                + $"dropped={d.DroppedSnapshots} stale={d.StaleSnapshots} dup={d.DuplicateSnapshots} "
                + $"corrections={d.CorrectionCount} medianM={d.CorrectionMedianMeters:F3} "
                + $"bytesDownPerSec={d.BytesDownPerSecond} "
                + $"latSim={(d.LatencySimActive ? $"{d.LatencySimRttMs}ms/{d.LatencySimLossPercent:F1}%" : "off")}");
#endif
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

        /// True: this backend takes the frame's input in FishNet's pre-tick
        /// (`TimeManager_OnPreTick`) and the facade clears the edge latches at
        /// that moment, inside the seam it hands them over through
        /// (bd `app-d1t`). `Advance`'s own `frame` parameter is not read here
        /// at all — see its doc.
        public bool ConsumesInputInTickDomain => true;

        /// The server started a new match. See `ISimBackend.MatchRestarted` for
        /// the whole contract; what is this class's own is WHEN it fires:
        /// `ClientMatchLink` moves the epoch it tracks on exactly two messages,
        /// the opening welcome and `MatchRestartedNet`, and only the second of
        /// them is a RESTART — the first begins the only match this connection
        /// has had, which the facade has already announced from its own
        /// `Restart`. `SyncMatchEpoch` therefore arms this for an epoch change
        /// away from a non-zero epoch and for nothing else.
        public event System.Action MatchRestarted;

        /// Whether `Restart` below has run to completion on THIS instance —
        /// which is to say whether the things it builds exist: the per-match
        /// seams, the registered snapshot channel, and the `ClientMatchLink`
        /// that sends the hello.
        ///
        /// IT IS HERE FOR THE BOOTSTRAP THAT OPENS THE CONNECTION (Stage 2 Task
        /// 44e fix-round 1, G-1). That component installs this backend from its
        /// `Awake` and dials the server from its `Start`, and between the two
        /// the facade's own `Awake` is supposed to have restarted this
        /// instance. "Supposed to" is the part worth asking about: the facade's
        /// `Awake` does not run at all on a deactivated object and does not
        /// finish if anything it builds first throws, and a connection opened
        /// with no link behind it sends no hello — after which the server waits
        /// out its join timeout and exits. This member is how that is checked
        /// instead of assumed.
        ///
        /// IT READS THE LINK, NOT THE CONFIG LATCH, though the same method sets
        /// that latch a few lines earlier. The link is built LAST, so its
        /// existence is the narrower answer and the one the caller is actually
        /// asking for: a `Restart` that threw partway through would leave the
        /// latch set and the link null.
        ///
        /// IT IS NOT A SEAT AT THE SERVER AND NOT A MATCH. On this backend a
        /// match begins on the server's welcome; true here says only that this
        /// side has been assembled and may now dial.
        public bool HasRestarted => _link != null;

        /// Records the match's balance numbers and builds everything this
        /// backend runs on — ON THE FIRST CALL, and on no other.
        ///
        /// THE FIRST CALL IS THE ONLY ONE THAT RECORDS ANYTHING EITHER
        /// (fix-round 1, F-4). The earlier shape wrote `_cfg` above the guard
        /// and returned, which read as harmless bookkeeping and was not: every
        /// buffer this class indexes is sized from the config of the FIRST
        /// call — `RenderSnapshot.Players`/`PlayerStats` in each of the ring's
        /// slots, the three decode scratches, the stale policy's capacity —
        /// while `BeginSlot` clears `_cfg.Arena.MaxPlayers` of them and
        /// `ReadPlayers` writes the record index the decoder validated against
        /// `_cfg`. A second `Restart` at a larger `Arena.MaxPlayers` (the dev
        /// overlay's forced-seed restart, the death overlay's R, the pause
        /// controller — the facade rebuilds `SimConfig` from its assets on
        /// every one of them) therefore put an index past the end of an array
        /// built for the old cap, inside the broadcast handler, on every frame
        /// that arrived afterwards. Keeping the numbers of the first call is
        /// not a workaround for that: it is the only reading under which
        /// `Config`'s own doc — "the hash of THIS very struct went out in the
        /// hello" — stays true.
        ///
        /// NOR MAY THE SEAMS BE REBUILT. `ClientLinkState` is the
        /// memory of this CONNECTION — one hello, one epoch, one seat, and
        /// reconnection deliberately unimplemented until Э5 — so rebuilding it
        /// because the facade restarted would send a second hello the server
        /// answers with `DuplicatePlayer` while the first seat stays claimed,
        /// and would re-register four broadcast handlers beside the four
        /// already subscribed.
        ///
        /// A LATER CALL DOES NOT RESTART ANYTHING, AND SAYS SO IN ITS ANSWER.
        /// On this backend a match begins and ends on the server's say-so
        /// (`MatchWelcomeNet`/`MatchRestartedNet`, the only two messages
        /// `ClientMatchReset` is ever called on); spec §3.12 lists
        /// `Restart`/`RestartNewSeed` as unavailable on a networked client for
        /// exactly that reason. Until Task 44d the facade nonetheless raised
        /// its own `WorldRestarted` on every one of these calls, so the dev
        /// overlay's forced-seed restart and the death overlay's R cleared
        /// every Presentation-side registry mid-match while the server kept
        /// sending the same match — and rebuilt the frozen hitstop pair from a
        /// config this backend had refused, leaving those buffers a different
        /// size from the pair they deep-copy. `false` is what stops both:
        /// the facade now does nothing at all on a refused restart. Hiding the
        /// dev controls themselves still belongs to whoever wires this backend
        /// into a scene.
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
        /// — see `HasMatchStats`, which is how a reader is told so.
        public bool Restart(long seed, in SimConfig cfg)
        {
            if (_hasConfig)
            {
                _nm.Log("NetworkSimBackend: Restart ignored — a networked client does not start "
                    + "matches. The server's own MatchRestartedNet is what begins the next one, and "
                    + "it is the only message that clears this client's per-match seams. The "
                    + "balance numbers of this call were NOT recorded either: every buffer on the "
                    + "receive path is sized from the config of the first Restart, and the hello's "
                    + "SimConfigHash was computed from it, so adopting a second set of numbers "
                    + "would index this arena's arrays with another arena's caps and would make "
                    + "the handshake's own agreement a statement about a struct the server never "
                    + "saw. Retune the assets and restart the match on both ends.");
                return false;
            }

            _cfg = cfg;
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
            // Both tick counts come off the asset, one line apart, because they
            // are the same policy's two halves — how long before a slot freezes
            // and how long it then takes to go out (Stage 2 Task 47c moved the
            // second one here from a constant of this class; `NetConfig.
            // EntityFadeTicks` has the why).
            _stale = new StalePolicy(cfg.Arena.MaxPlayers, _net.InterpMaxStaleTicks,
                _net.EntityFadeTicks);
            // bd `app-s0u`: the rounds this client draws are rebuilt from the
            // wire, so the table is sized by the same cap the arena mints ids
            // from — `MaxProjectiles` bounds what can be in flight at once,
            // and a client sees a subset of it (`SightRadius`, Р32).
            _tracers = new TracerProjectiles(cfg.Arena.MaxProjectiles);
            _reset = new ClientMatchReset(_dedup, _snapshots, _clock, _ghosts, _stale, _events,
                _tracers);
            // Sized from the same cap as `_mobScratch` below, which is what
            // makes "a frame can never carry more records than one generation
            // holds" true rather than hoped for.
            _mobTypes = new MobTypeMemory(cfg.Arena.MaxMobs);

            _prev = new RenderSnapshot(in cfg.Arena);
            _curr = new RenderSnapshot(in cfg.Arena);
            _alpha = 0f;
            _lastRenderTick = 0;

            _playerScratch = new SnapshotBlocks.PlayerRecord[math.max(1, cfg.Arena.MaxPlayers)];
            _mobScratch = new SnapshotBlocks.MobRecord[math.max(1, cfg.Arena.MaxMobs)];
            _eventScratch = new SnapshotBlocks.EventRecord[math.max(1, _net.SnapshotEventBudget)];

            _frameEvents = new SimEvent[_events.Capacity];

            // The snapshot channel is registered HERE and not in
            // `ClientMatchLink`: that class owns the match's IDENTITY — epoch,
            // seed, seat — and its own doc lists "decoding snapshot frames" as
            // this task's, explicitly. It is also the reason this class touches
            // `ClientManager` at all.
            //
            // THE PRE-TICK GOES UP BESIDE IT, UNDER THE SAME FLAG (Stage 2
            // app-b3z). Both are subscriptions this class makes on the
            // manager's own tables, both are dropped by `Unregister`, and one
            // flag rather than two is what keeps that promise checkable: a
            // second flag could be false while the first was true only if some
            // future line put the two subscriptions in different places, which
            // is the arrangement this file must not have. Registering from
            // `Restart` rather than from the constructor is also a promise
            // already made elsewhere — `SimulationRunner.TryUseBackend` and
            // `ClientNetworkBootstrap`'s refusal path both state that an
            // un-restarted instance holds no subscription, and both are right
            // only while this stays the single place.
            _nm.ClientManager.RegisterBroadcast<SnapshotBroadcast>(OnSnapshotBroadcast);
            _nm.TimeManager.OnPreTick += TimeManager_OnPreTick;
            _registered = true;

            // Registering the link LAST means every seam it may be told to
            // clear already exists. Its own constructor registers four
            // handlers and proves nothing about when the transport starts —
            // the obligation to construct this backend before starting the
            // client connection is the bootstrap's, exactly as it is for
            // `MatchHandshake` on the server.
            _link = new ClientMatchLink(_nm, _reset, _net, ProtocolVersion.Current,
                SimConfigHash.Compute(in cfg), cfg.Arena.MaxPlayers, _playerId, _joinToken);
            return true;
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

        /// The simulation needs nothing settled here: there is no accumulator
        /// on this side — the render clock integrates local time and corrects
        /// by pace — so a pause costs it no bookkeeping of the kind
        /// `LocalSimBackend` does.
        ///
        /// THE ONE MEASUREMENT THAT DOES NOT SURVIVE A PAUSE BY ITSELF is the
        /// byte rate, and this is where it is told (Stage 2 Task 48 fix-round
        /// 1, F-1): its window counts frame time, which stops with the gate,
        /// while the bytes it divides keep arriving. `RestartBytesRateWindow`
        /// has the whole argument.
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
            RestartBytesRateWindow();
        }

        /// Drops every subscription this backend made. Required before the
        /// instance is discarded, by the delegate-identity mechanism
        /// `ClientMatchLink.Unregister`'s own doc spells out: FishNet stores
        /// handlers per delegate identity, so a second backend on the same
        /// `NetworkManager` would leave both subscribed and the stale one
        /// would keep decoding into a ring nobody reads.
        ///
        /// THAT MECHANISM IS WHY THE PRE-TICK IS DROPPED HERE TOO (Stage 2
        /// app-b3z). `TimeManager.OnPreTick` is a plain `System.Action`, so it
        /// too keeps handlers by delegate identity and outlives this instance
        /// with the manager. A discarded backend left on it would go on asking
        /// a facade for input every tick and writing the answer into the
        /// prediction core of whichever controller it last cached — feeding
        /// the wire from an object nobody else still reads.
        ///
        /// THE CALLER EXISTS AS OF TASK 44e, AND IT IS THE ONLY ONE (Stage 2
        /// Task 44e fix-round 1: this paragraph used to say that nothing in the
        /// project constructed this class, which stopped being true the moment
        /// that task's bootstrap did). `ClientNetworkBootstrap` calls this on
        /// both of its exits — a backend the facade refused, and its own
        /// teardown — while `ISimBackend` still has no member for it, so the
        /// facade cannot call it even in principle. The install seam is written
        /// so the obligation cannot arise by accident:
        /// `SimulationRunner.TryUseBackend` refuses a second install, so the
        /// only backend a successful call replaces is a `LocalSimBackend`
        /// holding no subscription. What is left for any caller that discards
        /// an instance of THIS class — a scene teardown, a future reconnect —
        /// is the plain rule: unregister before dropping the reference, because
        /// FishNet keys handlers by delegate identity.
        public void Unregister()
        {
            if (_registered)
            {
                _nm.ClientManager.UnregisterBroadcast<SnapshotBroadcast>(OnSnapshotBroadcast);
                _nm.TimeManager.OnPreTick -= TimeManager_OnPreTick;
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

            SyncMatchEpoch();

            ReadFrame(new System.ReadOnlySpan<byte>(msg.Payload.Array, msg.Payload.Offset,
                msg.Payload.Count));
        }

        /// The frame, from its first byte to its last.
        ///
        /// ORDER IS THE WHOLE DESIGN HERE, and this list is the body's order
        /// rather than a plan for it (fix-round 1, F-7 — the earlier version
        /// named `EventDedup.TryAcceptState` LAST, which the code has never
        /// done and could not do). Each step is somebody's documented
        /// obligation:
        ///   1. the header, because `SnapshotReader` refuses a block read
        ///      before it and because the version check has to happen before
        ///      any byte whose meaning depends on the version is decoded;
        ///   2. `SnapshotQueue.Admit`, because Р150е makes THIS caller the gate
        ///      that keeps a frame from the absurd future out of `EventDedup` —
        ///      a `FutureRejected` frame's events are never offered to the
        ///      dedup at all, which is the exact path that would otherwise drag
        ///      its window forward and eat every real event behind it until the
        ///      next `Reset`;
        ///   3. `EventDedup.TryAcceptState`, because its answer is an ARGUMENT
        ///      of step 5: `ReadPlayers` takes it and gates `OnEntitySeen` on
        ///      it, so asking later would mean asking twice, and this call
        ///      RECORDS as well as answers. It is asked for the slotless
        ///      verdicts (`Stale`, `Duplicate`) too — nothing is decoded for
        ///      those, and the dedup refuses their ticks by its own gate
        ///      anyway, so the question costs nothing and the branch that would
        ///      skip it would be a second place to keep in step with the queue;
        ///   4. `BeginSlot`, clearing the recycled slot before a byte lands in
        ///      it, and only when `Admit` handed one back;
        ///   5. the block walk, decoding into that slot;
        ///   6. the completeness test — see the next paragraph;
        ///   7. `Commit`, only for a COMPLETE frame that has a slot, so `TryGet`
        ///      can never hand a consumer a half-decoded frame under a tick that
        ///      was never filled; and `RenderClock.OnSnapshot` beside it,
        ///      because the clock's target is a maximum over frames that really
        ///      landed and a frame that was not published did not land;
        ///   8. `StalePolicy.OnFrameApplied`, last, because it is the one call
        ///      whose argument depends on how the walk went.
        ///
        /// A FRAME IS COMPLETE ONLY IF THE WALK NEITHER FAILED, NOR CAME UP
        /// SHORT, NOR CARRIED A BLOCK ITS OWN DECODER REFUSED (fix-round 1,
        /// F-1; the third term is Task 44d's). All three are load-bearing, and
        /// the third is the one the first two cannot stand in for: a frame can
        /// arrive whole, parse cleanly and still have a Players payload the
        /// decoder rejects — a record index outside this match's roster, a
        /// length that is not a multiple of the record size, the untrusted-input
        /// path Р82 is about. `SnapshotReader` knows nothing of that by
        /// construction: it hands out a slice and never looks inside it. Without
        /// the third term such a frame committed a slot whose players were
        /// still `BeginSlot`'s zeros — nobody alive, everybody at the origin —
        /// and reported itself to `StalePolicy` as a CLEAN applied frame, which
        /// moves Р149's confirmation clock and opens `ConfirmedAbsent` for every
        /// id at once. That is the same price the two earlier terms were added
        /// to avoid, reached through a block that came rather than one that
        /// did not.
        ///
        /// THE EVENTS BLOCK IS DELIBERATELY NOT IN THE CONJUNCTION. Its
        /// contract is a different one: `TryReadEventsBlock` leaves the records
        /// it decoded BEFORE a refusal in the scratch and counts them (the
        /// `DestinationTooSmall` doc's own "Read `count` in both cases"), and
        /// this class walks them and accepts them either way. A partially
        /// decoded events block is therefore a frame that delivered some of its
        /// events, not a frame whose STATE is unknown — and `DestinationTooSmall`
        /// specifically is the ordinary shape of a server whose event budget
        /// exceeds this build's scratch, not damage. Folding it in would throw
        /// away a perfectly good picture over an event that did not fit.
        /// `SnapshotReader`'s own doc hands this receiver the obligation in as
        /// many words — a cut exactly on a block boundary parses as a shorter,
        /// perfectly valid snapshot with `Failed` and `Truncated` both false,
        /// so "no failure" is not "nothing missing" and the receiver has to
        /// check that the kinds it requires actually arrived. All five kinds
        /// are required, and that is the SENDER's fact rather than a choice
        /// made here: `SnapshotAssembler` writes Players, Liveness, Mobs, Wave
        /// and Events on every frame, empty or not, with its own comment giving
        /// this exact reason. An incomplete frame is not committed, is not
        /// shown to the clock, and is reported to `StalePolicy` as TRUNCATED —
        /// which is the honest word for it even though the header bit is clear:
        /// that bit means the SENDER dropped entities for room, while this
        /// means the frame arrived without the blocks that would have carried
        /// them, and the consequence is identical — absence proves nothing.
        /// Reporting it as a clean applied frame is what would move Р149's
        /// confirmation clock and start every doll fading at once.
        ///
        /// A FRAME WHOSE STATE IS REFUSED STILL DELIVERS ITS EVENTS, and that
        /// asymmetry is deliberate (spec §3.7's refinement of Р31): a packet
        /// that merely overtook another would otherwise swallow a death that
        /// was never shown. What `applyState` actually gates is narrower than
        /// "the state": the blocks are decoded into the slot and the slot is
        /// committed whatever it says, and the two calls that answer to it are
        /// `StalePolicy.OnEntitySeen` and `OnFrameApplied` — the liveness facts,
        /// which are monotonic maxima a reordered frame has nothing newer to
        /// say about. Only `ForeignEpoch` and `FutureRejected` refuse a frame
        /// whole.
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

            int kindsSeen = 0;
            bool stateDecoded = true;
            while (reader.TryReadBlock(KnownBlockKinds, out byte kind,
                       out System.ReadOnlySpan<byte> payload))
            {
                kindsSeen |= 1 << kind;
                switch ((SnapshotBlockKind)kind)
                {
                    case SnapshotBlockKind.Players:
                        stateDecoded &= ReadPlayers(slot, payload, tick, applyState);
                        break;
                    case SnapshotBlockKind.Liveness:
                        stateDecoded &= ReadLiveness(slot, payload);
                        break;
                    case SnapshotBlockKind.Mobs:
                        stateDecoded &= ReadMobs(slot, payload);
                        break;
                    case SnapshotBlockKind.Wave:
                        stateDecoded &= ReadWave(slot, payload);
                        break;
                    case SnapshotBlockKind.Events:
                        ReadEvents(epoch, tick, payload);
                        break;
                }
            }

            bool complete = !reader.Failed && stateDecoded
                && (kindsSeen & RequiredBlockKinds) == RequiredBlockKinds;
            if (!complete)
            {
                // Not counted, for the reason the header refusal above gives:
                // `NetStats`' composition is closed and has no field an
                // incomplete frame belongs in. One line per FRAME, never per
                // block — see the receive-path allocation note on the scratch
                // fields.
                _nm.Log($"NetworkSimBackend: snapshot {tick} incomplete — failed={reader.Failed} "
                    + $"truncated={reader.Truncated} stateDecoded={stateDecoded} "
                    + $"kinds=0x{kindsSeen:X2} of 0x{RequiredBlockKinds:X2}. The frame is not "
                    + "published and the render pair keeps the moment it was already showing; the "
                    + "events this frame did carry were accepted and stay accepted. Nothing on "
                    + "this side counts it.");
            }
            else if (slot != null)
            {
                _snapshots.Commit(tick);
                _clock.OnSnapshot(tick, epoch);
            }

            // ONE EXPRESSION, TWO READERS (Stage 2 Task 48): "this frame is
            // missing entities" is exactly what `StalePolicy` is told and
            // exactly what the dev overlay counts, so it is computed once. The
            // COUNT is taken whatever `applyState` says, because a reordered
            // frame that the dedup refuses for its state still arrived
            // truncated, and the panel is reporting what the connection is
            // delivering rather than what was applied out of it.
            bool missingEntities = !complete || (flags & SnapshotHeaderFlags.Truncated) != 0;
            if (missingEntities) _framesMissingEntities++;

            if (applyState)
                _stale.OnFrameApplied(tick, missingEntities);
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
        ///
        /// WHICH IS WHY BOTH FLAG ARRAYS ARE CLEARED HERE TOO (Stage 2 Task
        /// 47a). `PlayerKnown` is the field that says that zero apart from a
        /// real record of a real corpse, so it has to start this frame at "this
        /// frame has seen nothing" — a leftover `true` from the tick that used
        /// to live in this recycled slot would state, of a player behind the
        /// fog, that the frame carries their state. `PlayerAliveInMatch` is
        /// cleared for the same reason and refilled wholesale by
        /// `ReadLiveness`, the Liveness block riding on every frame.
        void BeginSlot(RenderSnapshot slot, uint tick)
        {
            slot.Tick = (int)tick;
            slot.LocalPlayerIndex = LocalPlayerIndex;
            slot.PlayerCount = _cfg.Arena.MaxPlayers;
            for (int i = 0; i < slot.PlayerCount; i++)
            {
                slot.Players[i] = default;
                slot.PlayerStats[i] = default;
                slot.PlayerKnown[i] = false;
                slot.PlayerAliveInMatch[i] = false;
            }
            slot.MobCount = 0;
            slot.ProjectileCount = 0;
            // Stage 3 Т6: the two fields `RenderSnapshot` grew with the
            // extraction economy. Nothing decodes them yet — the pickups
            // block and the match phase reach the wire in Т25 — and they are
            // cleared here anyway, for the reason this method exists: the
            // ring hands back a RECYCLED frame, so the moment a decoder does
            // start writing them, a tick that carried pickups would otherwise
            // leave them standing in the next tick that carries none. Adding
            // the clear together with the fields costs one line; discovering
            // it missing costs a ghost crate on the floor.
            slot.PickupCount = 0;
            slot.Match = default;
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
        bool ReadPlayers(RenderSnapshot slot, System.ReadOnlySpan<byte> payload, uint tick,
            bool applyState)
        {
            if (!SnapshotBlocks.TryReadPlayersBlock(payload, in _cfg,
                    new System.Span<SnapshotBlocks.PlayerRecord>(_playerScratch),
                    out int count, out SnapshotBlockError error))
            {
                LogBlockRefusal(SnapshotBlockKind.Players, error);
                return false;
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
                PlayerState state = PlayerFlags.ToSyntheticState(r.Flags, r.Pos, r.Dir, hp01,
                    in _cfg);
                // A BODY'S HEADING WOULD OTHERWISE DIE ON THIS LINE (Stage 2
                // Task 47a fix-round 1). The record carries a direction for
                // every seat, dead or alive — `SnapshotAssembler.PlayerRecordOf`
                // has no liveness branch and writes `normalizesafe(AimPoint -
                // Pos)`, and `SimulationWorld.TickMovement`'s own doc pins that
                // aim point at its value at death. The MAPPING drops it here
                // for a corpse and is right to: it places an aim point off the
                // `AimHeld` bit alone, and `KillPlayer` clears the settle timer
                // that raises that bit, so a body reports no pose at all. But
                // `ViewRegistry.EnsureCorpse` lays a body found after the fact
                // along exactly this heading, and with the field left at
                // `default` every such body on the arena would lie facing the
                // arena origin. So the border restores it, at the same distance
                // downrange the mapping's own constant states, and one field
                // over from the position it belongs to — reaching into
                // `Networking.Protocol` to widen the mapping instead would put
                // a Presentation question inside the wire contract, which is
                // the one thing that class's doc refuses to do.
                //
                // THE LOCAL BACKEND NEEDS NO SUCH LINE and gets the same
                // answer: a world in memory hands `CaptureSnapshot` the real
                // `AimPoint`, and `normalizesafe(AimPoint - Pos)` of it IS what
                // the assembler would have put on the wire. Both backends
                // therefore describe a body's facing with one field and one
                // expression, which is what lets `EnsureCorpse` carry no
                // branch on which backend it is drawing.
                //
                // THE CONSTANT HAS ONE HOME, THE EXPRESSION HAS TWO, AND THE
                // SECOND ONE IS THIS. `PlayerFlags.ToSyntheticState` places the
                // synthetic aim point the same way for a LIVING doll whose
                // `AimHeld` bit is set (`PlayerFlags.cs:125`); the line below is
                // the DEAD branch, which that mapping deliberately does not
                // cover — a cleared `Alive` bit there sets `Alive` false and
                // touches nothing else, by that method's own doc. So the repeat
                // is the price of keeping the corpse's facing out of the wire
                // contract, not an oversight; what is NOT repeated is the
                // distance itself, which both read from
                // `PlayerFlags.SyntheticAimMeters`.
                if (!state.Alive)
                    state.AimPoint = r.Pos + r.Dir * PlayerFlags.SyntheticAimMeters;
                slot.Players[r.Index] = state;
                // THE SAME LINE, ONE FIELD OVER (Stage 2 Task 47a): a state
                // written here is a state this frame KNOWS, and the flag says
                // so. It rides with the write rather than with `applyState`
                // above on purpose — the two answer different questions.
                // `OnEntitySeen` feeds a monotonic maximum ACROSS frames, which
                // a reordered frame has nothing newer to say about; this flag
                // describes THIS frame's own content, and the frame is
                // committed and drawn whatever the dedup thought of its tick
                // (see `ReadFrame`'s last paragraph).
                slot.PlayerKnown[r.Index] = true;
            }

            return true;
        }

        /// The match roster's liveness mask — who is alive ANYWHERE in the
        /// arena, as against the Players block above, which carries only who is
        /// visible to this client (Stage 2 Task 47a, bd `app-2rf`; Р70).
        ///
        /// THE BLOCK HAS RIDDEN ON EVERY FRAME SINCE TASK 27 AND WAS DECODED BY
        /// NOBODY UNTIL THIS TASK. `SnapshotBlocks.TryReadLivenessBlock` existed
        /// and had no production caller; what was missing was a field to write
        /// it into, and `RenderSnapshot.PlayerAliveInMatch` is that field. What
        /// its absence cost, in the words the branch that used to stand here
        /// carried: a slot alive but out of sight was indistinguishable from a
        /// dead one, both reading `Alive == false`.
        ///
        /// THE MASK STOPS AT THE ARRAY, NOT AT THE CONSUMER. `Presentation` is
        /// not told the wire's bit layout — the same border `PlayerFlags.
        /// ToSyntheticState` draws for the player record's own flag byte — so
        /// the spread happens here and everything above reads plain booleans.
        ///
        /// EIGHT SEATS IS THE MASK'S OWN CEILING, and the loop below refuses to
        /// invent the bits it does not have: one byte carries eight, the sender
        /// truncates its own scan the same way, and this match's roster is
        /// capped at three (`ArenaConfig.MaxPlayers`). A roster grown past eight
        /// would need a wider mask on the wire before it could be read here, and
        /// leaving the extra seats at `false` — rather than guessing — is what
        /// makes that a visible gap instead of a silent lie about who is alive.
        bool ReadLiveness(RenderSnapshot slot, System.ReadOnlySpan<byte> payload)
        {
            // THE SECOND MASK IS READ AND DELIBERATELY DROPPED HERE (Stage 3
            // Task 25, spec Р257). The block now carries `extractedMask`
            // beside the alive one, and this client has nothing to write it
            // into: `RenderSnapshot.PlayerAliveInMatch` exists because a
            // consumer asked for it, and the consumer of "who walked out" is
            // the results overlay of Т32 — adding a parallel array now would
            // be a field with no reader, the same reasoning SnapshotReader's
            // own doc gives for keeping its counters out of NetStats until
            // something folds them in. Discarding it costs nothing on the
            // wire: the byte rides for every recipient regardless, and the
            // decode is a single array read.
            if (!SnapshotBlocks.TryReadLivenessBlock(payload, out byte aliveMask,
                    out _, out SnapshotBlockError error))
            {
                LogBlockRefusal(SnapshotBlockKind.Liveness, error);
                return false;
            }

            if (slot == null) return true;
            int seats = math.min(slot.PlayerCount, LivenessMaskSeats);
            for (int i = 0; i < seats; i++)
                slot.PlayerAliveInMatch[i] = (aliveMask & (1 << i)) != 0;
            return true;
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
        bool ReadMobs(RenderSnapshot slot, System.ReadOnlySpan<byte> payload)
        {
            if (!SnapshotBlocks.TryReadMobsBlock(payload, in _cfg,
                    new System.Span<SnapshotBlocks.MobRecord>(_mobScratch),
                    out int count, out SnapshotBlockError error))
            {
                LogBlockRefusal(SnapshotBlockKind.Mobs, error);
                return false;
            }

            if (slot == null) return true;

            // THE ARCHETYPES GO INTO THE MEMORY BEFORE THE PICTURE GOES INTO
            // THE SLOT (fix-round 1, G-2). `MobDied` names a mob the wire has
            // already dropped from this very block, so the answer has to
            // outlive the frame — see `MobTypeMemory`, and `RestoreMobType`
            // for what asks it. Only frames the ring gave a slot to are fed:
            // a duplicate or an out-of-window frame carries nothing newer than
            // what is remembered already.
            _mobTypes.OnMobsDecoded(new System.ReadOnlySpan<SnapshotBlocks.MobRecord>(
                _mobScratch, 0, count));

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
            return true;
        }

        /// The wave director's public face. The nine zone/archetype
        /// `Pending*` debt fields (Stage 3 Task 11 — was two,
        /// PendingChasers/PendingGunners, before the zone budget split
        /// them) and `PhaseTimer` are not on the wire — they are the
        /// director's own bookkeeping and no client draws them — so they
        /// stay at zero rather than being guessed from the counts that are.
        bool ReadWave(RenderSnapshot slot, System.ReadOnlySpan<byte> payload)
        {
            if (!SnapshotBlocks.TryReadWaveBlock(payload, out WavePhase phase, out ushort waveIndex,
                    out byte aliveCount, out SnapshotBlockError error))
            {
                LogBlockRefusal(SnapshotBlockKind.Wave, error);
                return false;
            }

            if (slot == null) return true;
            slot.Wave = new WaveState
            {
                Phase = phase,
                WaveIndex = waveIndex,
                AliveCount = aliveCount,
            };
            return true;
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
        ///
        /// THE PER-RECORD REFUSALS ARE COUNTED AND REPORTED ONCE, FOR THE BLOCK
        /// (fix-round 1, F-8). A record whose payload does not decode is
        /// ordinary traffic on an untrusted path, and there can be up to
        /// `NetConfig.SnapshotEventBudget` of them in one block, thirty times a
        /// second; a line each would be a log flood and — because the line is
        /// built before the logger's level filter sees it — garbage on the
        /// receive path with it. One line per block says the same thing at
        /// 1/16th to 1/128th the cost.
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

            int refusedRecords = 0;
            SnapshotBlockError lastRefusal = SnapshotBlockError.None;
            for (int i = 0; i < count; i++)
            {
                SnapshotBlocks.EventRecord record = _eventScratch[i];
                if (!_dedup.TryAcceptEvent(epoch, frameTick, in record, out uint originTick))
                    continue;
                if (!ClientEventDecoder.TryDecode(originTick, in record, payload, in _cfg,
                        LocalPlayerIndex, out SimEvent decoded, out SnapshotEventPayload p,
                        out SnapshotBlockError recordError))
                {
                    if (recordError != SnapshotBlockError.None)
                    {
                        refusedRecords++;
                        lastRefusal = recordError;
                    }
                    continue;
                }

                // Filled here rather than by the decoder, and before anything
                // reads the event — see `RestoreMobType`.
                RestoreMobType(ref decoded);

                // THE ROUTING IS THE CALLER'S, NOT THE DECODER'S, and the split
                // is the point of moving the mapping out (Task 44d): turning
                // bytes into a `SimEvent` needs nothing but bytes, while both
                // calls below reach a live FishNet object. Keeping them here
                // is what leaves the decode a pure function a unit test can
                // reach.
                RouteToGhosts((SnapshotEventKind)record.Kind, in p);
                RouteToTracers(originTick, (SnapshotEventKind)record.Kind, in p, in decoded);
                RouteOwnDeath(in decoded);

                // The answer is deliberately not read: a refused event is one
                // the queue had no room for, and the queue counts that loss
                // itself in `OverflowDroppedEvents` — which is the number
                // `DroppedEvents` above reports. There is nothing this side
                // could do with a second copy of it.
                _events.Enqueue(in decoded);
            }

            if (refusedRecords > 0)
                _nm.Log($"NetworkSimBackend: {refusedRecords} of {count} event records refused — "
                    + $"last {lastRefusal}. The rest of the frame is still walked; a refusal here "
                    + "is ordinary traffic on an untrusted path (Р82), not a reason to abandon the "
                    + "datagram. Records of a kind this build has never heard of are NOT among "
                    + "these — that is Р29 forward compatibility and not a refusal at all.");
        }

        /// Puts back the one field the wire drops that this side can still
        /// answer for: the archetype of the mob a `MobDied` names (Stage 2
        /// Task 44d fix-round 1, G-2).
        ///
        /// NOT IN THE DECODER, BECAUSE THE DECODER CANNOT SEE IT. That class
        /// is handed one record and the bytes it points at; the archetype
        /// lives in the Mobs block of the frames around it, which is state
        /// this class owns. Moving the lookup there would mean handing a pure
        /// function a table it would then have to be kept in step with.
        ///
        /// `MobDied` AND NOT `ProjectileHit`, and the difference is not a
        /// choice. The wire's projectile-ending payload names the ROUND and
        /// never its victim, so a hit on a mob arrives with no mob in it at
        /// all — what is missing there is the identity, and no table can be
        /// asked about a mob nobody named. `MobDied` carries the id, so the
        /// type is a lookup away.
        ///
        /// A MISS LEAVES THE EVENT EXACTLY AS DECODED, zero included. The
        /// memory holds the last two frames' rosters (`MobTypeMemory`), and a
        /// mob absent from both — killed the instant it came into view, by
        /// somebody else — has no honest answer here. `ClientEventDecoder`'s
        /// own list of what the wire cannot give back names the residue.
        void RestoreMobType(ref SimEvent e)
        {
            if (e.Kind != SimEventKind.MobDied) return;
            if (_mobTypes.TryGetType(e.EntityId, out MobType type)) e.MobType = type;
        }

        /// Stops this client predicting its own corpse (Р41/Р59, Stage 2 Task
        /// 44d). `PlayerPredictionCore` needs BOTH triggers — the event and the
        /// authoritative state — because the event travels unreliably and the
        /// state arrives only with a reconcile; this is the event half, and
        /// until Task 44d nothing could deliver it.
        ///
        /// ON ARRIVAL, NOT ON DELIVERY. The decoded event still has to WAIT in
        /// the queue until the render clock reaches its tick, because the
        /// picture it belongs to is `InterpBufferTicks` behind — but prediction
        /// is not in that domain at all, it is ahead of it, and a corpse
        /// predicted for the length of the interpolation buffer rolls back by
        /// up to a dash's worth of distance when the truth catches up. So the
        /// latch is set the moment the frame is decoded and the doll keeps
        /// dying on screen at its own moment.
        ///
        /// THE VICTIM IS READ OUT OF `PlayerIndex`, WHICH IS THIS KIND'S
        /// CONVENTION. `SimEvent`'s own doc puts `PlayerDamaged`/`PlayerDied`
        /// under the VICTIM convention for both `EntityId` and `PlayerIndex`,
        /// and the server sends a player's own death to that player
        /// unconditionally (Р28's own-death carve-out in `EventRelevance`), so
        /// this arrives even when nobody could see it happen.
        ///
        /// A DEATH THAT ARRIVES BEFORE THE OBJECT DOES IS NOT LOST WORK. If
        /// FishNet has not spawned this client's player yet there is no core to
        /// latch, and there is also nothing to stop: a core created later
        /// starts with `Predicted` at `default`, whose `Alive` is false, and
        /// `ShouldPredict` reads the two triggers together — the missing latch
        /// only matters while the authoritative state still says alive, which
        /// is a window that cannot exist before the first reconcile.
        void RouteOwnDeath(in SimEvent e)
        {
            if (e.Kind != SimEventKind.PlayerDied || e.PlayerIndex != LocalPlayerIndex) return;

            EnsureController();
            if (_controller != null) _controller.NotifyOwnDeath();
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
        ///
        /// THE ZERO TICK HANDED TO `Confirm` IS A DOMAIN GAP, NOT A LAZY
        /// LITERAL (fix-round 1, M-6 — read the registry's body before reading
        /// the parameter's name). `Confirm` does not read that argument at all:
        /// it scans for a duplicate server id, then pairs the oldest
        /// unconfirmed ghost, and touches no tick. Nothing ages by it — the age
        /// of a record is measured from the BIRTH tick `TrySpawnFromPrediction`
        /// stored, against the PREDICTED tick `Advance` is fed (Р67). And that
        /// is exactly why the confirmation's own tick is not supplied from
        /// here: the only tick this path has is the frame's WORLD tick, from
        /// the wire, and the two counters have no fixed offset between them.
        /// The registry's doc invites a future consumer (telemetry, latency
        /// measurement) to start reading the parameter without a signature
        /// change — and such a consumer would subtract it from a prediction
        /// tick. A zero it can see is nothing; a world tick it cannot tell from
        /// a prediction tick is a plausible wrong number. Opening the
        /// prediction seams (Task 44d) did not change this: what is missing is
        /// not access to `TimeManager.LocalTick` — the render frame reads it
        /// already — but a ghost BORN in the prediction domain to measure
        /// against, and `TrySpawnFromPrediction` still has no caller (see the
        /// task's report). The tick to pass is the one that spawn records.
        void RouteToGhosts(SnapshotEventKind kind, in SnapshotEventPayload p)
        {
            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned:
                    if (p.PlayerIndex == LocalPlayerIndex)
                    {
                        _ghosts.Confirm(p.Id, 0u);
                    }
                    break;
                case SnapshotEventKind.ProjectileEnded:
                    _ghosts.TryTranslateEnd(p.Id, out int _);
                    break;
            }
        }

        /// bd `app-s0u` — the tracer half of the same two records, kept in its
        /// own method because it needs two things `RouteToGhosts` does not: the
        /// event's TICK (the tracer lives in render time, see
        /// `TracerProjectiles`) and the decoded envelope's position, which is
        /// where the round was born and is not part of the payload's own eight
        /// bytes.
        ///
        /// EVERY ROUND, NOT JUST THIS CLIENT'S. `ProjectileSpawned` is sent for
        /// every round the client can see (Р32 — relevance is judged on the
        /// whole trajectory), and a firefight in which only your own bullets
        /// are visible is exactly the picture this task exists to fix.
        ///
        /// `Radius`/`Ttl` COME FROM THE CONFIG, BY OWNER, AND THAT IS LOAD-
        /// BEARING. Neither rides the wire, and the two shooters do not share
        /// them: decoding a Gunner mob's round on the hero's `Weapon` numbers
        /// would draw a sphere of the wrong size. `PlayerIndex` carries
        /// `ProjectileIds.NoOwner` for a mob's round — the same sentinel the
        /// simulation uses — so the question is answered by the wire rather
        /// than guessed.
        void RouteToTracers(uint eventTick, SnapshotEventKind kind, in SnapshotEventPayload p,
            in SimEvent decoded)
        {
            switch (kind)
            {
                case SnapshotEventKind.ProjectileSpawned:
                {
                    bool byPlayer = p.PlayerIndex != ProjectileIds.NoOwner;
                    float radius = byPlayer
                        ? _cfg.Weapon.ProjectileRadius
                        : _cfg.Gunner.ProjectileRadius;
                    float ttl = byPlayer
                        ? _cfg.Weapon.ProjectileLifetime
                        : _cfg.Gunner.ProjectileLifetime;

                    // A refusal (full table, id already tracked) is a value on
                    // purpose: this runs inside FishNet's batched parse, where a
                    // throw would abandon every message behind it (Р82/195).
                    // What it costs is one bullet not drawn.
                    // The owner rides along because the wire carries it: the
                    // same `PlayerIndex` that picked the radius and the ttl
                    // above. Left on its defaults, every round -- a Gunner's
                    // included -- would sit in the render snapshot signed as a
                    // player's, and the first reader of that field would be
                    // wrong through no fault of its own.
                    _tracers.TrySpawn(p.Id, (int)eventTick, decoded.Pos, p.Height, p.Dir,
                        p.HorizSpeed, p.VelZ, radius, ttl,
                        byPlayer ? ProjectileOwner.Player : ProjectileOwner.Mob,
                        p.PlayerIndex);
                    break;
                }
                case SnapshotEventKind.ProjectileEnded:
                    _tracers.Retire(p.Id, (int)eventTick);
                    break;
            }
        }

        // ---- the pending window --------------------------------------------

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
        ///
        /// WHAT THE QUEUE HANDS BACK IS THE FINISHED EVENT (Task 44d). Until
        /// then it held the wire RECORD, whose payload fields point into a
        /// FishNet receive buffer that is gone by the time an event is due —
        /// so this class kept a pool of decoded events beside it and smuggled
        /// the pool slot through the record's `PayloadOffset`. The queue's own
        /// capacity IS that pool now, and the borrowed field, the free list
        /// and the epoch-time slot release went with it.
        void DrainDueEvents(int renderTick)
        {
            while (_frameEventCount < _frameEvents.Length
                   && _events.TryDequeue(renderTick, out SimEvent due))
            {
                _frameEvents[_frameEventCount++] = due;
            }
        }

        /// EVERYTHING THIS CLASS OWNS THAT A NEW MATCH INVALIDATES, cleared the
        /// moment the epoch it is keyed to changes. Three things qualify: the
        /// frame's own event window — which may hold events of the match that
        /// just ended, already drained out of the queue `ClientMatchReset`
        /// clears — this backend's own readiness, and the mob-archetype memory
        /// (fix-round 1, G-2), whose keys are entity ids a new match starts
        /// minting over.
        ///
        /// `Ready` HAS TO GO WITH IT (fix-round 1, F-6). `ClientMatchReset`
        /// empties the ring and stops the clock, so the next `Advance` resolves
        /// no pair at all — and `_prev`/`_curr` are then, by
        /// `ResolveRenderPair`'s own rule, left holding the last picture of the
        /// match that ENDED, with `CurrentTick` naming its tick. Readiness is
        /// this class's promise that what the pair holds is worth drawing, so
        /// the promise ends where the match does. It is re-armed by the first
        /// pair of the new epoch that actually resolves, which is the same
        /// event that armed it the first time. The opening welcome takes this
        /// branch too and costs nothing there: nothing has been drawn yet, so
        /// the clear is a no-op on a flag that is already false.
        ///
        /// IT IS OBSERVED RATHER THAN CALLED, and deliberately so.
        /// `ClientMatchReset` is the ONE handler that clears the per-match
        /// seams, its own doc argues at length for why there is one call site
        /// and not six, and it is reached from inside `ClientMatchLink` rather
        /// than from here. Adding a seventh seam to it would be a change to a
        /// closed task and would owe a test in `MatchLifecycleTests` besides.
        /// The epoch the link tracks moves on exactly the two messages that
        /// reset — the opening welcome and `MatchRestartedNet` — so watching it
        /// is watching the same fact, one step removed. Both entry points of
        /// this backend ask FIRST — the render frame and the broadcast handler
        /// — so nothing is DECODED INTO a state belonging to an epoch that has
        /// already been left. It says nothing about what is DRAWN, and cannot:
        /// `Ready`, `Prev`, `Curr` and `CurrentTick` are plain properties read
        /// from outside both entry points, and the reset itself runs inside
        /// `ClientMatchLink`'s own handler. Between that handler and the next
        /// `Advance` the pair still holds the previous frame's picture and
        /// `Ready` still answers for it — a window one frame wide, showing
        /// exactly what it showed a frame earlier, which is why the readiness
        /// clear below is where it is rather than in the handler.
        ///
        /// IT ALSO ARMS `MatchRestarted`, FOR A CHANGE AWAY FROM A REAL EPOCH
        /// ONLY (Stage 2 Task 44d). `_matchEpoch` starts at 0, which
        /// `ClientLinkState` reserves for "there is no epoch", so the first
        /// change — the opening welcome — is this connection's first match
        /// rather than a restart of one, and the facade has already told its
        /// subscribers about that from its own `Restart`.
        void SyncMatchEpoch()
        {
            if (_link == null) return;
            ushort epoch = _link.State.MatchEpoch;
            if (epoch == _matchEpoch) return;
            bool restarted = _matchEpoch != 0;
            _matchEpoch = epoch;
            _frameEventCount = 0;
            _ready = false;
            // Two more things that belong to the match that just ended (Stage 2
            // Task 47b). The predicted pose is of a body in the PREVIOUS
            // match's arena — `SampleOwnPlayer` no longer forgets it when
            // prediction stops, so a new match whose roster says this seat is
            // alive again would otherwise have it pasted into the opening
            // frames, before the new object's first reconcile. And a spectate
            // request cannot outlive the match it named a slot of.
            _hasOwnSample = false;
            _spectateRequestWindow = 0f;
            // The third thing that cannot survive a match: a new one mints its
            // entity ids from 1 again, so a remembered id would answer with
            // the archetype of a mob from the match before (fix-round 1, G-2).
            _mobTypes.Reset();
            if (restarted) _matchRestartedPending = true;
        }

        // ---- the render pair -----------------------------------------------

        /// Deep-copies the two halves of Р38's render pair out of the ring:
        /// the snapshot AT `renderTick` and the one after it, blended by
        /// `Phase`.
        ///
        /// A MISSING HALF IS NOT A REASON TO SHOW NOTHING. With one half
        /// resident both ends of the blend are that one, so the picture holds
        /// still instead of interpolating toward a moment nobody sent; with
        /// neither, the previous pair is left exactly as it was, and `false`
        /// says so — which is what keeps the PHASE still as well (fix-round 1,
        /// F-2: `Advance` latches it only on `true`, because a phase without
        /// its pair blends two frozen poses by a coefficient that keeps
        /// running, and a picture that oscillates across one tick of motion is
        /// worse than one that waits). That wait is the freeze `StalePolicy`
        /// then has an opinion about. A hole in the ring is ordinary at the 5%
        /// loss every playtest build must survive, and the buffer exists to
        /// absorb it.
        ///
        /// COLLAPSING THE PAIR INSTEAD — copying `_curr` over `_prev` so the
        /// blend degenerates — was the other way to make the doc true, and it
        /// is the worse one on the consumers' own terms: every one of them
        /// interpolates (the facade's own player position, the camera rig,
        /// the mob and projectile registries), so a collapse would jump the
        /// whole picture forward by the remainder of a tick on the first
        /// starved frame and hold it there, where holding the phase moves
        /// nothing at all. It would also make `_prev` claim `_curr`'s tick.
        ///
        /// THE LOCAL SEAT IS NO LONGER FILLED HERE (app-5fh + app-0t6). It used
        /// to be, one predicted sample per half, on the two lines that followed
        /// the copies below — which made this client's own doll wait on the
        /// arrival of somebody else's datagram and had it blended by a phase
        /// belonging to another clock. `Advance` calls `BlendOwnPlayer` after
        /// this method returns, every frame and whatever it returned; the whole
        /// argument for the move is there. The order matters and is that way
        /// round on purpose: `CopyFrom` overwrites every slot of both halves,
        /// the local one included, so anything written into that slot has to be
        /// written after this method has had its say.
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
            return true;
        }

        /// Takes the local player's own predicted copy, ONCE PER PREDICTION
        /// TICK, and keeps the last two (Stage 2 Task 44d).
        ///
        /// WHY TWO AND NOT ONE. The snapshot leaves this client's own slot out
        /// by the assembler's own rule, so the slot is filled from prediction —
        /// and a single predicted state is quantized to the tick it was taken
        /// on, so a doll drawn straight from it would STEP at the tick rate
        /// while every mob and every other doll slid between ticks. Two
        /// consecutive predicted poses are the two ends of a blend that puts
        /// that in-between motion back.
        ///
        /// WHAT THE BLEND IS NOT, ANY MORE, IS THE CONSUMERS' OWN (app-5fh).
        /// This paragraph used to go on to reject writing ONE predicted state
        /// into both halves, on the grounds that the consumers' blend would
        /// degenerate (`Lerp(P, P, a) == P`) and the local player alone would
        /// step at the tick rate. That objection is TRUE OF A RAW PREDICTED
        /// STATE and false of one recomputed every frame: `BlendOwnPlayer`
        /// collapses this pair into a single pose per FRAME, using the
        /// prediction domain's own phase, and writes that pose into both
        /// halves. Such a pose moves at the frame rate — smoother than anything
        /// the consumers' blend ever gave it, not coarser — and the degeneracy
        /// is then the point rather than the price: with both halves equal, no
        /// consumer of the local slot can be blending THIS pair by a coefficient
        /// that does not belong to it — the hitstop catch-up window, where the
        /// facade makes a pair of its own out of a frozen buffer and a live one,
        /// is the exception `BlendOwnPlayer` spells out. The pair is still
        /// sampled here, and still exactly one tick wide, because it is what
        /// that per-frame blend interpolates between.
        ///
        /// THE SAMPLE IS TAKEN AGAINST THE PREDICTION TICK, NOT THE RENDER
        /// FRAME, and that distinction is the whole of the method. Prediction
        /// advances in `TimeManager.LocalTick`; a "previous" latched per render
        /// frame would be microseconds old at a high frame rate and the blend
        /// would jitter across a distance the player never traveled. Two poses
        /// exactly one prediction tick apart are what a coefficient can honestly
        /// be swept across.
        ///
        /// AND THE COEFFICIENT HAS TO COME OFF THE SAME CLOCK — the sentence
        /// this paragraph used to end on said otherwise, and it was the defect
        /// itself (app-5fh). It claimed the two clocks "share a rate and differ
        /// in phase, so what the offset costs is a fraction of a tick of latency
        /// on one's own doll and nothing else". A constant phase offset buys a
        /// constant delay ONLY where the pair's index and the blend coefficient
        /// step on the same edge. These two never did: the halves move on
        /// FishNet's local tick while `Alpha` wraps on the render clock's, an
        /// origin with no fixed offset to the first (`CurrentTick` says so in as
        /// many words). What the offset actually cost was two discontinuities
        /// per tick, in opposite directions, each a whole tick of motion wide.
        /// The arithmetic is in `BlendOwnPlayer`, which is where the blend
        /// happens now and where the coefficient is read from
        /// `TimeManager.GetPreciseTick` — the phase of the very clock this
        /// method latches `_ownTick` against, normalized by the delta that
        /// clock actually spends. Nothing here subtracts one
        /// tick number from the other even so: mixing the two domains further
        /// than sampling would be mixing them outright.
        ///
        /// A FRAME LONGER THAN A TICK LEAVES THE HALVES MORE THAN ONE TICK
        /// APART, and that is accepted rather than corrected: the picture then
        /// interpolates across the whole gap instead of jumping it, and the
        /// next sample puts the pair back on one tick.
        ///
        /// THE LAST SAMPLE OUTLIVES PREDICTION, AND THAT IS THIS TASK'S OWN
        /// CORRECTION (Stage 2 Task 47b). This method used to FORGET the pair
        /// the moment `IsPredicting` went false, which is the instant the death
        /// event is decoded — while the render pair is still `InterpBufferTicks`
        /// behind, showing frames the server built when this player was alive
        /// and therefore left this seat out of. For that whole window nothing
        /// filled the seat: not the wire, which had no reason to, and not
        /// prediction, which had just been forgotten. The seat read
        /// `default(PlayerState)` — the arena ORIGIN — so the doll was retired
        /// and the camera set off for the middle of the arena a fifth of a
        /// second before the body arrived to explain itself. Keeping the last
        /// pose costs nothing and is the truth of those frames: it is where
        /// this player stood on the ticks they describe. `ApplyOwnPlayer`'s
        /// roster gate is what stops it reaching a single frame past the death,
        /// and `SyncMatchEpoch` drops it with the match it belongs to.
        ///
        /// NOT VERIFIABLE BY A UNIT TEST, and said so plainly: this assembly is
        /// outside the EditMode test assembly's references, and the quantity in
        /// question is what motion looks like. The milestone В1 playtest is what
        /// answers it.
        void SampleOwnPlayer()
        {
            if (_controller == null || !_controller.Core.IsPredicting) return;

            uint tick = _nm.TimeManager.LocalTick;
            PlayerState predicted = _controller.Core.Predicted;
            // The opening sample has no predecessor, so the pair starts
            // collapsed for exactly one tick — the same still picture the
            // render pair itself shows before its second half lands. Within
            // one tick the newer half is refreshed and the older one is left
            // alone: a reconcile can move the predicted copy several times
            // between ticks, and the pair must stay one tick wide.
            if (!_hasOwnSample) _ownPrev = predicted;
            else if (tick != _ownTick) _ownPrev = _ownCurr;
            _ownCurr = predicted;
            _ownTick = tick;
            _hasOwnSample = true;
        }

        /// THIS FRAME'S OWN DRAWN POSE: one state, blended out of the two
        /// predicted samples by the PREDICTION domain's own phase and written
        /// into BOTH halves of the render pair (app-5fh + app-0t6, the owner's
        /// decision of 2026-08-15).
        ///
        /// WHAT IT REPLACES WAS A DEFECT AND NOT A COST. The two samples used
        /// to go into the pair as they stood, one per half, and every consumer
        /// blended them by `Alpha` as it does any other entity's. But the halves
        /// step on FishNet's LOCAL tick and `Alpha` wraps on the RENDER clock's,
        /// and those two counters have no fixed offset between them (see
        /// `CurrentTick`). Equal rates with unequal phases do not buy a constant
        /// delay; they buy TWO DISCONTINUITIES PER TICK, IN OPPOSITE
        /// DIRECTIONS, each a whole tick of motion wide. Take the pair
        /// `(P[k-1], P[k])` and an offset `f`: the phase wraps to 0 while the
        /// pair still holds the old halves, so the doll snaps BACK by
        /// `P[k] - P[k-1]`; a fraction `f` of a tick later the pair steps under
        /// a phase already part-way through, and the doll snaps FORWARD by the
        /// same distance. The average speed stays right, which is why the player
        /// still arrives where they steered while the picture alternates between
        /// two positions a tick of travel apart — a quarter of a meter at this
        /// project's tick rate and top speed, thirty times a second. That is the
        /// "one's own doll splits in two while running" the owner reported: no
        /// single frame of it is wrong, so no screenshot holds it, and a solo
        /// match cannot show it at all because there both ends of the blend come
        /// off one accumulator. It is the same saw `Advance`'s F-2 paragraph
        /// refuses to ship for a STARVED pair — except that the local seat had
        /// it on a healthy connection, and had it always.
        ///
        /// SO THE BLEND MOVES HERE, WHERE COEFFICIENT AND PAIR SHARE A CLOCK.
        /// `TimeManager.GetPreciseTick(tick).PercentAsDouble` is how far the
        /// tick in progress has run — `_elapsedTickTime` over the delta the
        /// client's own loop spends, FishNet 4.7.2,
        /// `Runtime/Managing/Timing/TimeManager.cs:826-834`, read in the package
        /// rather than in its documentation as this project's rule for pinned
        /// packages requires — and that is the very tick `SampleOwnPlayer`
        /// latches its pair against. The two now change on the same edge by
        /// construction, which is the whole of the fix; what is left is a pose
        /// sliding at the FRAME rate along the segment between two predicted
        /// ticks.
        ///
        /// THE COEFFICIENT IS MEASURED FROM `_ownTick` RATHER THAN TAKEN RAW,
        /// AND THE CLAMP IS LOAD-BEARING. Whole ticks elapsed since the pair was
        /// sampled are added to the fraction before clamping. While prediction
        /// runs that term is zero on every frame — `SampleOwnPlayer` latched
        /// `_ownTick` off this same counter earlier in this same `Advance` — so
        /// the coefficient is the ruled one exactly. The term earns its keep the
        /// moment prediction STOPS, which is a state this class deliberately
        /// keeps samples through (Task 47b: the last pose outlives `IsPredicting`
        /// by the interpolation buffer, so the seat is not vacant while the
        /// corpse is in flight). A bare fraction would then be a free
        /// coefficient sawing across a FROZEN pair — precisely the oscillation
        /// `Advance`'s F-2 paragraph refuses for the remote picture, and it
        /// would have put a quarter-meter jitter on one's own body for the
        /// length of that window. With the term, the coefficient saturates at 1
        /// within a tick: the doll finishes the motion it was part-way through,
        /// holds at `_ownCurr`, and `ApplyOwnPlayer`'s roster gate ends the
        /// write when the body arrives. That is F-2's own answer for the local
        /// seat, reached without latching a second phase field.
        ///
        /// THE FRACTION COMES FROM `GetPreciseTick`, AND THE OBVIOUS-LOOKING
        /// `GetTickPercentAsDouble` IS THE WRONG CALL HERE. FishNet's tick loop
        /// drains `_elapsedTickTime` by the CLIENT-ADJUSTED delta
        /// (`TimeManager.cs:693`, `:771`), while that property divides the
        /// remainder by the NOMINAL one (`:792`). On a client the two differ
        /// whenever the server has asked it to speed up or slow down, so the
        /// fraction would reach `adjusted/nominal` at the tick boundary instead
        /// of 1 — the pair would then advance while the coefficient was still
        /// short of the end, which is a discontinuity of exactly the shape this
        /// method exists to remove, only smaller. `GetPreciseTick` divides by
        /// the delta the loop actually spent (`:830-832`) and hands back a
        /// `PreciseTick` whose constructor has already clamped the ratio to
        /// `[0, 1]` (`PreciseTick.cs:63`). No number is copied out of the
        /// package to get this: the ratio it holds privately is read through the
        /// accessor built for it.
        ///
        /// THE CLAMP IS STILL OWED, BUT ON THE SUM RATHER THAN ON THE FRACTION.
        /// `PreciseTick` hands the fraction over already inside `[0, 1]`; what
        /// is unbounded is the tick term beside it, which counts whole ticks
        /// the pair has fallen behind by and grows without limit once sampling
        /// stops. An unclamped coefficient would EXTRAPOLATE, placing the
        /// doll where neither predicted pose ever was, and this method
        /// interpolates on purpose. `math.saturate` is the same NaN-safe clamp
        /// `Quantize` puts on ratios of exactly this shape, and it also catches
        /// the one way the tick term can go negative: `LocalTick` is reset to
        /// zero on disconnect (`TimeManager.cs:459`), which lands on the lower
        /// rail — a legal pose, on a frame nobody is still drawing.
        ///
        /// ONLY `Pos` IS BLENDED, AND THAT IS A DECISION RATHER THAN AN
        /// OVERSIGHT. The defect is positional. Every other field of
        /// `PlayerState` is either discrete — `Alive`, the dash and slide
        /// latches — or already behaves exactly as it does today, and sweeping a
        /// coefficient across a timer or across `AimPoint` would invent
        /// intermediate values the simulation never produced and no consumer
        /// asked for. `Vel`, `AimPoint`, `DashDir` and `SlideTimer` are
        /// therefore taken from `_ownCurr` as they stand.
        ///
        /// ONE POSE IN BOTH HALVES IS WHAT KEEPS THE SEAM OUT OF THIS FIX.
        /// `Lerp(P, P, a) == P` for every `a`, so consumers go on reading the
        /// pair exactly as they always have: `ISimBackend`, `RenderSnapshot`,
        /// `SimulationRunner` and everything under `Presentation/` are untouched
        /// by it, and no reader can drift from another by having been missed —
        /// as long as the pair it reads is THIS pair. The hitstop catch-up
        /// window is the one place a consumer holds two different halves of its
        /// own making (`SimulationRunner.cs:270-272`, and the `Alpha` doc above
        /// says the same); that window is untouched here, for better and for
        /// worse.
        /// The alternative was a SECOND phase on the seam, for the local slot
        /// only, and the sweep of the two dozen places that read this pose
        /// priced it: the drawn aim ray would have moved with the doll while the
        /// point it lands on did not, and the muzzle flash while the shot's
        /// audio did not.
        ///
        /// WHAT IT DOES NOT BUY BACK IS THE SMOOTHING LAG, unchanged here rather
        /// than accepted anew. The drawn pose still trails the newest predicted
        /// one by `1 - phase` ticks, half a tick on average — exactly the
        /// average the old blend had, since it swept the same two poses at the
        /// same rate. Only the discontinuities go. Extrapolating past `_ownCurr`
        /// instead would trade that lag for an overshoot on every change of
        /// direction, which is a different decision and not this one.
        ///
        /// TWO CONSEQUENCES THAT ARE BEHAVIOR AND NOT ONLY PICTURE, named
        /// because they were found before the change rather than after it. The
        /// facade's muzzle position and its client-side line-of-fire gate both
        /// read the local seat out of `Curr`, so both now see an INTERPOLATED
        /// pose where they used to see a tick-quantized one. That is what makes
        /// them agree with the doll instead of trailing it, and it moves a
        /// client-side aiming input by a fraction of a tick. No game outcome is
        /// decided on this side (CR 3), so the change is legal — but it is a
        /// change, and this is where it is recorded.
        ///
        /// NOT VERIFIABLE BY A UNIT TEST, for the reason `SampleOwnPlayer` gives
        /// at length: this assembly sits outside the EditMode test assembly's
        /// references, and the quantity in question is what motion looks like.
        /// The playtest under 80 ms RTT and 5% loss is what answers it.
        void BlendOwnPlayer()
        {
            // How far the drawn pose has traveled from `_ownPrev` towards
            // `_ownCurr`, in ticks: whole ticks the pair has fallen behind by,
            // plus the fraction of the tick in progress. Subtracted as doubles
            // so that a `LocalTick` reset cannot wrap the difference of two
            // unsigned counters into a very large positive number.
            double ticksBehind = (double)_nm.TimeManager.LocalTick - _ownTick;
            // `GetPreciseTick`, NOT `GetTickPercentAsDouble`: only the former
            // normalizes by the delta the client's own tick loop spends. The
            // argument is passed through untouched into the returned struct and
            // does not enter the fraction; `_ownTick` is handed over because it
            // is the tick this fraction is measured from.
            double fraction = _nm.TimeManager.GetPreciseTick(_ownTick).PercentAsDouble;
            float phase = (float)math.saturate(ticksBehind + fraction);

            PlayerState pose = _ownCurr;
            pose.Pos = math.lerp(_ownPrev.Pos, _ownCurr.Pos, phase);

            // Both halves, through the one method that owns the guards: no
            // sample yet, a seat outside this frame's roster, and the roster's
            // own verdict on whether this seat is still standing (Task 47b).
            ApplyOwnPlayer(_prev, in pose);
            ApplyOwnPlayer(_curr, in pose);
        }

        /// Puts this client's own player back into the picture for the frames
        /// that leave it out, one half of the render pair at a time. Its two
        /// callers are the two halves, and `predicted` is the same value both
        /// times — the frame's blended pose, see `BlendOwnPlayer`. Everything
        /// below is about WHETHER the write may happen, which is a question of
        /// the frame in hand and not of the pose.
        ///
        /// FROM THE PREDICTED COPY, AND ONLY WHILE **THIS FRAME'S ROSTER** SAYS
        /// THE SEAT IS ALIVE (Stage 2 Task 47b). The assembler leaves a
        /// connection's own record out of its frame exactly while that
        /// connection is alive, and sends it once it is dead (the owner's
        /// decision 2a) — so prediction and the wire each own one half of this
        /// seat's life, and `PlayerAliveInMatch` is the line between them. It
        /// is the same fact the SENDER used when it decided whether to write
        /// the record (`SnapshotAssembler.WriteFrame`'s candidate phase and its
        /// liveness mask are two loops over one capture), which is why this is
        /// the gate rather than a second latch of "I died" on this side: a
        /// third opinion beside the mask and `IsPredicting` could only ever
        /// disagree with one of them.
        ///
        /// WITHOUT IT, THE DEFECT IS A BODY THAT STANDS UP. Prediction stops
        /// when the death EVENT arrives, the authoritative corpse arrives in a
        /// frame of the same tick, and the render pair reaches that tick
        /// `InterpBufferTicks` later — so there is a window in which this
        /// method would paste a living predicted pose over a corpse the server
        /// had already sent, and the doll would stand until the window closed
        /// and then snap down.
        ///
        /// IT IS ALSO THE ONLY PLACE THE LOCAL SEAT CAN BE MARKED KNOWN WHILE
        /// THAT PLAYER IS ALIVE (Stage 2 Task 47a). No record for a living seat
        /// ever rides the wire, so `ReadPlayers` never reaches it, and a frame
        /// left saying "nothing known about slot N" is a frame that retires this
        /// client's OWN doll. The flag therefore travels with the write, here as
        /// in `ReadPlayers`: the state came from prediction rather than from a
        /// datagram, but the frame carries it either way, which is the whole of
        /// what the flag claims.
        void ApplyOwnPlayer(RenderSnapshot snapshot, in PlayerState predicted)
        {
            if (!_hasOwnSample) return;

            int index = snapshot.LocalPlayerIndex;
            if (index < 0 || index >= snapshot.PlayerCount) return;
            // The frame's own roster, not this class's memory: a frame that
            // says the seat is down is a frame whose Players block carries the
            // body, and the body is the authoritative answer.
            if (!snapshot.PlayerAliveInMatch[index]) return;
            snapshot.Players[index] = predicted;
            snapshot.PlayerKnown[index] = true;
        }

        /// Finds this client's own player object once FishNet has spawned it.
        /// The search is over the local connection's OWN objects, which is the
        /// same table `NetworkObject.InitializeEarly` fills on both ends, so
        /// nothing here has to know how the object was spawned or by whom. A
        /// match restart spawns NEW objects on the same slots (Р164), so the
        /// cached reference is dropped as soon as it stops being spawned.
        ///
        /// FINDING ONE IS ALSO CONFIGURING IT (Stage 2 Task 44d). Until
        /// `Configure` has been called the component is inert by its own
        /// design — `TimeManager_OnTick` and `TimeManager_OnPostTick` both
        /// return on `!_configured`, because every codec mapping is a function
        /// of the config and a zeroed one would encode garbage. The moment this
        /// method decides an object is ours is the earliest moment the numbers
        /// can be handed over, and it is also the only moment: a new object
        /// after a restart comes through here again, and re-configuring the
        /// same instance is not possible because the reference is only replaced
        /// when the old one stops being spawned. The numbers are this backend's
        /// first `Restart`'s, which is the struct whose `SimConfigHash` the
        /// handshake agreed on.
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
                    _controller.Configure(in _cfg);
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
        ///
        /// IT IS WITHIN THE ROSTER, AND NOT BECAUSE THIS LINE CHECKS IT
        /// (fix-round 1, F-5). `ClientLinkState.OnWelcome` is handed this
        /// match's player cap and refuses a welcome naming a seat outside it,
        /// so the byte is stopped where it ENTERS the process rather than
        /// patched where it lands. That placement is the point: `BeginSlot`
        /// copies this number into `RenderSnapshot.LocalPlayerIndex`, and
        /// `RenderSnapshot.Player` indexes `Players` by it with no guard —
        /// so does everything the facade builds on that property, every frame,
        /// in `Update` rather than in a broadcast handler. A guard here would
        /// have to invent a substitute seat, which is a wrong picture in place
        /// of an exception; a refusal there means the client never joins a
        /// match whose seat it cannot occupy, and says why in the log.
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
