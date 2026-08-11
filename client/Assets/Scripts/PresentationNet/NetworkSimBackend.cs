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
    /// the frame the facade already sampled, `NotifyOwnDeath` off the decoded
    /// `PlayerDied` that names this client's own seat.
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

        /// How many render ticks a stale entity takes to fade out once it is
        /// eligible (`StalePolicy`'s `fadeTicks`). `NetConfig` carries no field
        /// for it — `StalePolicy`'s own doc records that as an open end for
        /// this task — and it is still a constant here rather than an asset
        /// field because the number STILL has no consumer that draws it.
        /// The earlier wording named the dolls of Task 45 as the consumer that
        /// would arrive; Task 45 shipped whole (a, b and c) and reads neither
        /// `StaleState` nor `FadeProgress`. The real address is Task 47b and bd
        /// `app-wcy` — the fade of a stranger's doll whose records stopped
        /// coming — and until that reader exists a balance-asset field would be
        /// a number nobody could see the effect of tuning (CR 6 is about
        /// numbers the game plays by).
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

            SyncMatchEpoch();

            // RAISED HERE AND NOWHERE ELSE, THOUGH THE EPOCH CHANGE IS USUALLY
            // NOTICED IN THE BROADCAST HANDLER. Behind this event sit ten
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

            // THE FRAME THE FACADE ALREADY SAMPLED, HANDED STRAIGHT TO
            // PREDICTION (Р35, spec §3.8). `SetPendingInput` is the seam
            // `PlayerNetworkController`'s own doc names for this, and the
            // direction is what keeps `Ring.Networking` from ever referencing
            // a Presentation assembly back. Nothing is re-sampled here: a
            // second `InputSampler.SampleFrame` would consume the dash edge the
            // facade has already latched, and the facade clears that latch only
            // after a flush.
            //
            // AS EARLY IN THE FRAME AS THIS CLASS IS REACHED — AND STILL ONE
            // FRAME BEHIND THE TICK THAT READS IT (Stage 2 Task 44e fix-round
            // 1, G-2, correcting what these lines used to claim). The
            // controller's own tick — `TimeManager_OnTick` -> `BuildReplicate`
            // -> `_core.PendingInput` — is what turns this frame into a
            // replicate, and on any frame that ticks, that tick has already
            // run by the time this line does. The earlier text said
            // `NetworkManager` sat at the default order of 0 and that the
            // scene therefore placed this call ahead of FishNet's tick; both
            // halves were wrong. `NetworkManager`, and the reader loop
            // FishNet's own `TimeManager` adds beside itself to drive the tick,
            // are each declared `[DefaultExecutionOrder(short.MinValue)]` — the
            // floor of the scale — while `SimulationRunner`, whose `Update`
            // reaches this method, is pinned at -50. FishNet's tick loop
            // therefore runs BEFORE the facade every frame, and no scene
            // arranges otherwise: the floor cannot be undercut, and the
            // per-script order table lives in `ProjectSettings`.
            //
            // WHAT IT COSTS, AND WHERE IT IS FIXED. `BuildReplicate` reads what
            // this line wrote on the PREVIOUS render frame, so the local
            // player's own input reaches the server one render frame late —
            // about 16 ms at 60 fps, up to a whole tick once the frame rate
            // falls to the tick rate — on top of the network's own delay, and
            // under the 80 ms RTT of Critical Rule 7 the two read the same on a
            // playtest. The cure is not an ordering but a feed point: task
            // `app-b3z` moves this call to FishNet's `TimeManager.OnPreTick`,
            // raised inside the tick loop immediately before `OnTick`, after
            // which the execution-order table stops meaning anything here at
            // all. Until that task lands the line below stands, and so does the
            // frame of lag.
            if (_controller != null) _controller.SetPendingInput(in frame);

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

            _clock.Advance(unscaledDeltaTime, in _timings);
            int renderTick = _clock.RenderTick;

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
            }

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
            _stale = new StalePolicy(cfg.Arena.MaxPlayers, _net.InterpMaxStaleTicks, EntityFadeTicks);
            _reset = new ClientMatchReset(_dedup, _snapshots, _clock, _ghosts, _stale, _events);
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
            _nm.ClientManager.RegisterBroadcast<SnapshotBroadcast>(OnSnapshotBroadcast);
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

            if (applyState)
                _stale.OnFrameApplied(tick, !complete || (flags & SnapshotHeaderFlags.Truncated) != 0);
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
            if (!SnapshotBlocks.TryReadLivenessBlock(payload, out byte aliveMask,
                    out SnapshotBlockError error))
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

        /// The wave director's public face. `PendingChasers`/`PendingGunners`/
        /// `PhaseTimer` are not on the wire — they are the director's own
        /// bookkeeping and no client draws them — so they stay at zero rather
        /// than being guessed from the counts that are.
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
                    if (p.PlayerIndex == LocalPlayerIndex) _ghosts.Confirm(p.Id, 0u);
                    break;
                case SnapshotEventKind.ProjectileEnded:
                    _ghosts.TryTranslateEnd(p.Id, out int _);
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
            ApplyOwnPlayer(_prev, in _ownPrev);
            ApplyOwnPlayer(_curr, in _ownCurr);
            return true;
        }

        /// Takes the local player's own predicted copy, ONCE PER PREDICTION
        /// TICK, and keeps the last two (Stage 2 Task 44d).
        ///
        /// WHY TWO AND NOT ONE. The snapshot leaves this client's own slot out
        /// by the assembler's own rule, so the slot is filled from prediction —
        /// and the pair's two halves are then blended by `Alpha` like every
        /// other entity's. Writing ONE predicted state into both halves makes
        /// that blend degenerate (`Lerp(P, P, a) == P`), so the local player
        /// alone would step at the tick rate while every mob and every other
        /// doll slid between ticks. Two consecutive predicted poses restore the
        /// same in-between motion the rest of the picture has.
        ///
        /// THE SAMPLE IS TAKEN AGAINST THE PREDICTION TICK, NOT THE RENDER
        /// FRAME, and that distinction is the whole of the method. Prediction
        /// advances in `TimeManager.LocalTick`; a "previous" latched per render
        /// frame would be microseconds old at a high frame rate and the blend
        /// would jitter across a distance the player never travelled. Two poses
        /// exactly one prediction tick apart, blended by a coefficient that
        /// sweeps 0..1 once per world tick, move at the right speed — the two
        /// clocks share a rate and differ in phase, so what the offset costs is
        /// a fraction of a tick of latency on one's own doll and nothing else.
        /// Mixing them further than that would be mixing domains outright, so
        /// nothing here subtracts one tick number from the other.
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

        /// Puts this client's own player back into the picture for the frames
        /// that leave it out, one half of the render pair at a time.
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
