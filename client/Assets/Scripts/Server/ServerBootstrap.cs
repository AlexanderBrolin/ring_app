using System;
using System.Globalization;
using System.Text;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using Ring.Data;
using Ring.Networking;
using Ring.Networking.Protocol;
using Ring.Networking.Server;
using Ring.Simulation.Core;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Ring.Server
{
    /// The headless server's entry point (Stage 2 Task 41, spec §3.10/§3.11):
    /// the object `Assets/Scenes/Server.unity` carries next to its
    /// `NetworkManager`. It reads the match config, checks the cross-config
    /// invariants, builds the match lifecycle objects, opens the port, waits
    /// for players, spawns them, runs the match and turns its outcome into the
    /// process's exit code.
    ///
    /// WHY SERIALIZED REFERENCES AND NOT `AssetDatabase`. `AssetDatabase` is
    /// Editor-only. `LongRunHarness` loads the balance assets through it and is
    /// an Editor tool for that reason; a built headless player has no such API,
    /// so the ONLY way the numbers reach this process is a reference serialized
    /// into the scene. `StageTwoSceneBootstrap` is what fills every field below
    /// — exactly the way `StageOneSceneBootstrap` feeds `SimulationRunner` in
    /// `Main.unity`. Nothing here resolves an asset by name at run time, and
    /// nothing should: a typo'd path would be a null the boot sequence could
    /// only discover after the port is already open.
    ///
    /// DO NOT CALL `PlayerNetworkController.Configure` FROM HERE.
    /// `MatchServer.StartMatch` calls it on every controller it is handed —
    /// that is stated in its own doc, and a match that method returns from is
    /// therefore never silently inert. A second call from the bootstrap would
    /// be a second source of the same fact; the method is `internal` to
    /// `Ring.Networking` precisely so this assembly cannot make that mistake by
    /// accident.
    ///
    /// The seven `SimConfigBuilder.Build` assets are the same seven
    /// `SimulationRunner` carries, in the same order, under the same field
    /// names — one balance source for the client and the server both (Critical
    /// Rule 6). `NetConfig` is separate on purpose (Р52): it never enters
    /// `SimConfig` or the balance-parity hash, and it is read directly, for the
    /// port timers, the process watchdog and `NetInvariants`.
    ///
    /// THE BOOT ORDER IS FIXED, AND EVERY STEP OF IT IS LOAD-BEARING
    /// (§6k Р161-Р167, task-40-report §6, task-41b-report §7.3):
    ///   1. `SimConfigBuilder.Build` from the serialized references;
    ///   2. `MatchConfigLoader.Load(simConfig.Arena.MaxPlayers)` — a refusal is
    ///      a VALUE, logged, exit code 2, and `Config` is NOT read on that path
    ///      (it is `default`, `Players` `null` — `MatchConfig`'s own doc);
    ///   3. `NetInvariants.Validate` against the transport's RAW MTU — every
    ///      violated line logged, exit code 2;
    ///   4. `new MatchServer(...)` BEFORE anything spawns, because that
    ///      constructor's `OnPostTick` subscription is what puts it ahead of
    ///      every `PlayerNetworkController` in FishNet's FIFO chain
    ///      (`MatchServer`'s own I1);
    ///   5. `MatchEpochCounter.Mint()` — before the port opens (Р163в);
    ///   6. `MatchRoster` then `MatchHandshake`, the latter fed the SAME epoch
    ///      value `StartMatch` will later be given (Р163б: one number, two
    ///      recipients, never a second `Mint` for the same match);
    ///   7. `ServerManager.SetFrameRate` — see `ChooseFrameRate`;
    ///   8. open the port;
    ///   9. subscribe the pre-match poll to `TimeManager.OnPostTick` (Р161);
    ///  10. log the EFFECTIVE `Application.targetFrameRate` once the server has
    ///      actually started — see `LogEffectiveFrameRateOnce` for why that
    ///      cannot happen inline at step 8.
    /// Steps 5 and 6 sit before step 8 because `MatchHandshake`'s constructor
    /// both freezes its epoch and registers the one `ClientHelloNet` handler
    /// this process has: a port opened first could hand the first client to a
    /// handler nobody registered yet.
    ///
    /// ONE CLOCK, HANDED OUT AS A DELEGATE (Р162). `_clock` is the process's
    /// only wall-clock axis: `NowSeconds` feeds `MatchHandshake`'s constructor
    /// AND every direct `MatchRoster.ShouldStart` call AND the join deadline
    /// AND the abandon watchdog AND the end-of-match linger. A second
    /// stopwatch started at a different instant would read a different elapsed
    /// time for the same wall-clock moment, and a countdown measured against
    /// one and checked against the other is wrong in either direction
    /// (`MatchHandshake`'s own "TIME ORIGIN IS INJECTED" paragraph spells the
    /// two failure shapes out). The axis's zero is this component's `Start`,
    /// not process spawn: engine startup is not part of the join window, and
    /// the number the deadline is compared against — `JoinTimeoutSeconds` —
    /// is about how long the server waits for players, not about how long
    /// Unity took to load a scene.
    ///
    /// ONE `OnPostTick` SUBSCRIBER FOR THE WHOLE PROCESS, NEVER RE-SUBSCRIBED.
    /// The same handler drives all four phases, because all four need a tick:
    /// the pre-match poll (Р161), the `MatchServer.Outcome` poll (`Outcome` is
    /// polled, not evented — `MatchServer`'s own doc), the abandon watchdog and
    /// the post-match linger. Unsubscribing after the match starts — the shape
    /// the brief left open — would take the outcome poll with it, and
    /// re-subscribing later would move this handler BEHIND the player
    /// controllers in FishNet's FIFO chain for no gain. `MatchServer`
    /// subscribes in its constructor, i.e. at boot step 4, so it is always
    /// ahead of this one: a match that ends on tick N is seen by this handler
    /// on tick N, not N+1.
    ///
    /// A RESTART IS NOT TRIGGERED BY ANYTHING IN PHASE Ф8, AND THAT IS THE
    /// DESIGN, NOT A GAP (Р164). Task 40 shipped the mechanism —
    /// `MatchServer.RestartMatch(seed, simConfig, newEpoch, connections,
    /// freshControllers)` — and the handle that pulls it is Phase Ф9's
    /// host-mode "play again" control, the same shape of deferred wiring the
    /// client half of the handshake already carries. When that handle lands it
    /// calls THIS class, and the four things it will have to do are already
    /// determined: (1) `_epochCounter.Mint()` again — the second mint of the
    /// process, and the first time `MatchRoster`/`MatchHandshake` are NOT
    /// rebuilt with it; (2) despawn the finished match's controllers and spawn
    /// NEW objects on the same slots, because `PlayerPredictionCore` has no
    /// reset and `NotifyOwnDeath` is a one-way latch; (3) hand
    /// `_slotConnections` back in as the connections argument — this class
    /// owns that table and `MatchServer` releases its own copy in `StopMatch`;
    /// (4) reuse `_config.Seed`, unchanged, because a restart is a rerun of
    /// the match this process was given rather than a new session (owner's
    /// decision, Р164) — `MatchConfig` is read from the environment exactly
    /// once, in `Boot`, and never again. The whole block has to run inside ONE
    /// tick callback or after the match has already ended, never split across
    /// frames, and `RestartMatch`'s throw has to be CAUGHT rather than waited
    /// out through `Outcome` — the same hole `StartMatch` has and for the same
    /// reason.
    ///
    /// THE HEADLESS AUTO-START IS TURNED OFF IN `Awake`, AND IT HAS TO BE.
    /// `NetworkManager` carries `[DefaultExecutionOrder(short.MinValue)]`, so
    /// its own `Start` runs before this one's, and that `Start` is
    /// `ServerManager.StartForHeadless()` — which, under
    /// `UNITY_SERVER && !UNITY_EDITOR` (exactly the build `BuildLinuxServer`
    /// produces) and with `_startOnHeadless` defaulted to `true`, calls
    /// `StartConnection()` with no port argument, i.e. opens `Tugboat`'s
    /// SCENE port, before this class has read the match config, registered the
    /// handshake or minted an epoch. Authoring the flag off in the scene is
    /// not available: `ServerManager` is not a scene component at all —
    /// `NetworkManager.Awake` adds it with `GetOrCreateComponent`, so it
    /// always arrives with the package's own serialized defaults. `Awake` is
    /// the only window that closes strictly before every `Start`.
    public sealed class ServerBootstrap : MonoBehaviour
    {
        [SerializeField] HeroConfig _hero;
        [SerializeField] WeaponConfig _weapon;
        [SerializeField] MobConfig _chaser;
        [SerializeField] MobConfig _gunner;
        [SerializeField] WaveConfig _wave;
        [SerializeField] ArenaConfig _arena;
        [SerializeField] VisibilityConfig _visibility;

        /// Network tuning, deliberately NOT a `SimConfigBuilder.Build`
        /// parameter — see the class doc.
        [SerializeField] NetConfig _net;

        /// The prefab spawned once per player slot (Р164: a restart spawns NEW
        /// objects, never the same ones again). Its root carries
        /// `NetworkObject` + `PlayerNetworkController`; the smoothing lives on
        /// its `Visual` child. Held as a `GameObject` by the Task 41 brief's
        /// own choice, NOT because the spawn call demands one: `ServerManager.
        /// Spawn` is overloaded for both, and the `GameObject` form is a thin
        /// `GetComponent&lt;NetworkObject&gt;` wrapper over the `NetworkObject`
        /// form.
        [SerializeField] GameObject _playerPrefab;

        /// Exit codes, spec §3.11. `0` and `4` are not listed here because
        /// they are not this class's to name: both come out of
        /// `MatchEndPolicy.ExitCodeFor`, the one place that maps an outcome to
        /// a code (`AllPlayersDead -> 0`, `MaxDurationReached -> 4`). The three
        /// below are the ones NO `MatchEndReason` describes — a match that
        /// never started, or one nobody stayed for — which is exactly why
        /// `StopMatch` leaves `Outcome` alone and §3.11's remaining codes land
        /// on the bootstrap (task-40-report §6.2, hole 4).
        ///
        /// `143` (SIGTERM) IS NOT IN THIS LIST, AND THE MEASUREMENT SAYS IT
        /// NEVER ARRIVES EITHER. Spec §3.11 lists "143 — interrupted by
        /// SIGTERM", which is what `128 + 15` would be if the signal reached
        /// its default disposition. It does not: the Unity Linux player
        /// installs its own SIGTERM handler, shuts the engine down cleanly and
        /// exits **0** — measured twice on this very build (`kill -TERM` on the
        /// running `RingServer`: the process tears down and the shell reports
        /// `0`, with no `Application.Quit` of ours ever called). So a container
        /// stop is indistinguishable, by exit code alone, from a match that
        /// played out. Nothing in this class can change that — the engine owns
        /// the handler — and nothing here should try: a code chosen after
        /// SIGTERM would be a code chosen by a process that ignored it. The
        /// honest statement is that the exit code says "clean shutdown" and
        /// the LOG says which kind: a code this class chose is always preceded
        /// by its own "exiting with code N" line, and a SIGTERM shutdown has
        /// no such line at all.
        const int ExitAbandoned = 0;
        const int ExitBadConfig = 2;
        const int ExitNoPlayers = 3;

        /// Where the process is in its own life. `Booting` covers everything
        /// before the first tick; `Exiting` is terminal and makes the tick
        /// handler inert, because `Application.Quit` does NOT stop the current
        /// frame — the engine finishes the frame and shuts down afterwards, so
        /// without this the same tick could keep polling a match that is on its
        /// way out.
        enum Phase
        {
            Booting,
            WaitingForPlayers,
            Running,
            Ending,
            Exiting,
        }

        readonly Stopwatch _clock = new Stopwatch();
        readonly MatchEpochCounter _epochCounter = new MatchEpochCounter();

        NetworkManager _nm;
        MatchConfig _config;
        SimConfig _simConfig;
        MatchServer _matchServer;
        MatchRoster _roster;
        ushort _epoch;

        /// Slot -> connection, built from `MatchHandshake`'s `onAccepted`.
        /// THIS TABLE BELONGS TO THE BOOTSTRAP (§6k Р164): the handshake is
        /// the only place that ever holds an accepted slot and its
        /// `NetworkConnection` in one stack frame, and `MatchServer` keeps no
        /// copy past `StopMatch`. Sized `MaxPlayers` and filled densely in
        /// acceptance order, which is the same numbering `MatchRoster` hands
        /// out and the same one `MatchWelcomeNet.PlayerIndex` already promised
        /// each client — so it is never compacted (Р165: compacting would
        /// rename players).
        NetworkConnection[] _slotConnections;
        int _slotCount;

        Phase _phase = Phase.Booting;
        bool _tickSubscribed;
        bool _frameRateLogged;
        ushort _requestedFrameRate;

        /// Abandon watchdog state (spec §3.10's "all clients disconnected ->
        /// grace -> exit"). `_anyConnectionEverLive` is what makes the grace
        /// count from the moment the server EMPTIED rather than from the
        /// moment it started: an empty server that nobody ever reached is the
        /// join-timeout's case (exit 3), not this one, and a grace armed at
        /// boot would beat that deadline to it on every idle container.
        /// `_abandonedSinceSeconds` is `-1` while the watchdog is unarmed.
        bool _anyConnectionEverLive;
        double _abandonedSinceSeconds = -1.0;

        double _matchStartedSeconds;
        double _matchEndedSeconds;
        int _pendingExitCode;

        void Awake()
        {
            // See the class doc's last paragraph. `InstanceFinder` reads
            // `NetworkManager.Instances`, which that class appends itself to
            // only AFTER `InitializeComponents()` — i.e. after `ServerManager`
            // has been created and initialized — so a non-null answer here is
            // also the proof that the manager this line reaches for exists. A
            // null answer is reported by `Start`, which is where every other
            // wiring failure is reported too.
            _nm = InstanceFinder.NetworkManager;
            if (_nm == null) return;

            _nm.ServerManager.SetStartOnHeadless(false);
        }

        void Start()
        {
            // Р162: the axis starts before anything can ask what time it is.
            _clock.Start();
            Boot();
        }

        void OnDestroy()
        {
            // Hygiene, not part of any contract: `TimeManager` lives on the
            // `NetworkManager` object, which FishNet marks
            // `DontDestroyOnLoad`, so it can outlive this component and would
            // then invoke a handler bound to a destroyed target. This is NOT
            // the "re-subscribe" the class doc rules out — the subscription is
            // released once, when the object dies, and never re-taken.
            if (_tickSubscribed && _nm != null)
            {
                _nm.TimeManager.OnPostTick -= OnPostTick;
                _tickSubscribed = false;
            }
        }

        void Boot()
        {
            if (_nm == null)
            {
                Fail(ExitBadConfig, "no NetworkManager in the loaded scenes — " +
                    "Server.unity must carry one beside this component.");
                return;
            }

            if (!TryBuildSimConfig(out _simConfig)) return;

            // Step 2. A refusal is a VALUE (`MatchConfigLoader` never throws,
            // never writes to the console and never quits — turning its answer
            // into a stdout line and an exit code is this class's job). `Ok ==
            // false` means `Config` is `default`, `Players` included, so it is
            // not read on this path.
            MatchConfigLoadResult loaded = MatchConfigLoader.Load(_simConfig.Arena.MaxPlayers);
            if (!loaded.Ok)
            {
                Fail(loaded.ExitCode, $"match config rejected — {loaded.Error}");
                return;
            }
            _config = loaded.Config;

            if (!TryValidateInvariants()) return;

            // Step 4. Before anything spawns: this constructor subscribes to
            // `OnPostTick`, and FishNet's tick events are strictly FIFO by
            // subscription time, so being first here is what keeps
            // `SetAuthoritativeState` ahead of every controller's own
            // `CreateReconcile` (`MatchServer`'s I1).
            //
            // `MatchEndPolicy` takes a finished TICK COUNT, not seconds: the
            // seconds-to-ticks conversion belongs to whoever reads the asset,
            // and that is this class. Both operands come from the same
            // `NetConfig` instance the `MatchServer` itself is handed.
            //
            // `SpectatePolicy` (Stage 2 Task 42a) takes the same kind of
            // finished tick count, built the same way and for the same
            // reason (`SpectatePolicy`'s own doc). The rounding is UP
            // (`Math.Ceiling`), never a bare `(int)` truncation: truncating
            // could enforce a cooldown SHORTER than the asset names, which
            // weakens the exact limiter Р70 exists to enforce.
            //
            // `decimal`, NOT `double` (Task 42a fix-round 1, I-2 — this was
            // the round's own defect, since corrected; the earlier comment
            // here claimed "0.35 * 30 = 10.5", which is a statement about the
            // MATHEMATICAL real numbers, not about what this line actually
            // computes). `(double)SpectatorSwitchCooldownSeconds * TickRate`
            // is exactly the trap spec Р141 names: widening a `float` to
            // `double` preserves the float's OWN rounding error instead of
            // fixing it, so a value the Inspector shows as a clean decimal
            // can land fractionally ABOVE its intended product — measured
            // directly (not assumed): `(double)0.1f * 30` is
            // `3.0000000447034836`, whose `Ceiling` is `4`, one tick more
            // than the `3` the number `0.1` actually means; the same defect
            // reproduces at `0.2` (`Ceiling` `7`, not `6`) and `0.4`
            // (`Ceiling` `13`, not `12`), silently lengthening the cooldown
            // by exactly one tick on those and roughly a third of the legal
            // `[Range(0.05f, 2f)]` × `{20, 30, 60}` combinations checked
            // while fixing this. The shipped default `0.35` happens to be
            // unaffected purely by chance (`10.499999821186066` still ceils
            // to `11`, the same answer the exact `10.5` would) — which is
            // exactly why R-TEST alone, run only against the shipped
            // default, could never have caught this. `(decimal)float`
            // widens through the float's OWN ~7-significant-digit precision
            // instead of `double`'s ~15-17, which is enough to recover the
            // clean decimal a human actually typed into the Inspector for
            // every value checked above — verified against the
            // mathematically exact product computed straight from the
            // decimal LITERAL (`decimal.Parse` on the same text, never
            // touching `float` at all), not merely against `double`'s own
            // answer.
            try
            {
                int spectateCooldownTicks = (int)Math.Ceiling(
                    (decimal)_net.SpectatorSwitchCooldownSeconds * _net.TickRate);
                _matchServer = new MatchServer(_nm, _net,
                    new MatchEndPolicy(_net.MatchMaxDurationSeconds * _net.TickRate),
                    new SpectatePolicy(spectateCooldownTicks));
            }
            catch (Exception ex)
            {
                Fail(ExitBadConfig, "match lifecycle could not be built — " + ex.Message);
                return;
            }

            // Steps 5-6. ONE mint for this match, handed to BOTH recipients.
            _epoch = _epochCounter.Mint();

            _slotConnections = new NetworkConnection[_config.MaxPlayers];
            _slotCount = 0;

            ulong simConfigHash = SimConfigHash.Compute(in _simConfig);
            try
            {
                _roster = new MatchRoster(in _config);

                // THE HANDSHAKE IS DELIBERATELY NOT HELD IN A FIELD. Its
                // constructor registers the process's one `ClientHelloNet`
                // handler, and that registration is itself the reference that
                // keeps the instance alive: `ServerManager` stores a delegate
                // whose target IS this object, for as long as the
                // `NetworkManager` lives. Nothing here ever needs to reach it
                // again — `Unregister()` is explicitly NOT a step of a restart
                // (Р164: the roster and the handshake are objects of the JOIN
                // PHASE and survive an epoch untouched), and this process
                // never builds a second one, which is the only situation that
                // method exists for.
                _ = new MatchHandshake(_nm, ProtocolVersion.Current, simConfigHash,
                    _config.MaxPlayers, _epoch, _config.Seed, TryJoin, NowSeconds, OnAccepted);
            }
            catch (Exception ex)
            {
                Fail(ExitBadConfig, "join phase could not be built — " + ex.Message);
                return;
            }

            // Step 7.
            _requestedFrameRate = ChooseFrameRate();
            _nm.ServerManager.SetFrameRate(_requestedFrameRate);

            // Step 8. `StartConnection(ushort)` is used INSTEAD of
            // `SetPort` + `StartConnection()`, not alongside it: the overload
            // already does `Transport.SetPort(port)` itself, so calling both
            // would state the port twice and leave two places to change it.
            // The port is a per-match number from `MatchConfig`, never the
            // scene's `Tugboat._port` — that field is left at the package
            // default precisely so it cannot become a second source (Task 41b
            // §6.2).
            if (!_nm.ServerManager.StartConnection((ushort)_config.Port))
            {
                Fail(ExitBadConfig, $"transport refused to open port {_config.Port}.");
                return;
            }

            // Step 9.
            _nm.TimeManager.OnPostTick += OnPostTick;
            _tickSubscribed = true;
            _phase = Phase.WaitingForPlayers;

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "ServerBootstrap: listening matchId={0} seed={1} maxPlayers={2} port={3} " +
                "startMode={4} countdownSeconds={5} rosterEntries={6} epoch={7} " +
                "protocolVersion={8} simConfigHash=0x{9:X16} joinTimeoutSeconds={10}",
                _config.MatchId, _config.Seed, _config.MaxPlayers, _config.Port,
                _config.StartMode, _config.CountdownSeconds, _config.Players.Length, _epoch,
                ProtocolVersion.Current, simConfigHash, _net.JoinTimeoutSeconds));
        }

        /// Every serialized reference is checked BEFORE `SimConfigBuilder.
        /// Build` touches one, because the alternative diagnosis for a field
        /// the scene failed to fill is a bare `NullReferenceException` from
        /// inside a sixty-field copy. `Build` itself throws
        /// `ArgumentException` when the balance numbers do not validate; that
        /// is a bad configuration exactly like a bad `MatchConfig`, so it
        /// takes the same exit code.
        bool TryBuildSimConfig(out SimConfig simConfig)
        {
            simConfig = default;

            var missing = new StringBuilder();
            AppendIfMissing(missing, _hero, nameof(_hero));
            AppendIfMissing(missing, _weapon, nameof(_weapon));
            AppendIfMissing(missing, _chaser, nameof(_chaser));
            AppendIfMissing(missing, _gunner, nameof(_gunner));
            AppendIfMissing(missing, _wave, nameof(_wave));
            AppendIfMissing(missing, _arena, nameof(_arena));
            AppendIfMissing(missing, _visibility, nameof(_visibility));
            AppendIfMissing(missing, _net, nameof(_net));
            AppendIfMissing(missing, _playerPrefab, nameof(_playerPrefab));
            if (missing.Length > 0)
            {
                Fail(ExitBadConfig, "unfilled serialized references on ServerBootstrap: " +
                    missing + " — re-run StageTwoSceneBootstrap.Apply.");
                return false;
            }

            try
            {
                simConfig = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave,
                    _arena, _visibility);
            }
            catch (Exception ex)
            {
                Fail(ExitBadConfig, "balance configuration rejected — " + ex.Message);
                return false;
            }
            return true;
        }

        static void AppendIfMissing(StringBuilder sb, UnityEngine.Object reference, string field)
        {
            if (reference != null) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(field);
        }

        /// Step 3. The transport's RAW MTU is what goes in — NOT
        /// `TransportManager.GetLowestMTU`: `NetInvariants.
        /// SnapshotWireOverheadBytes` already contains FishNet's own 2-byte MTU
        /// reserve, so the already-reduced number would make the check two
        /// bytes stricter than it should be while the raw one keeps it exact
        /// (task-41a-report §6.2). The transport is already filled by the time
        /// anything here reads it — `TransportManager.InitializeOnce_Internal`
        /// runs inside `NetworkManager.Awake`, which `[DefaultExecutionOrder(
        /// short.MinValue)]` puts ahead of this component's own `Awake`, let
        /// alone its `Start`. Reading it in `Awake` would therefore have been
        /// safe too; this sits in the boot sequence because that is where the
        /// step belongs, not because `Awake` would have raced.
        bool TryValidateInvariants()
        {
            string[] violations = NetInvariants.Validate(_net, in _simConfig,
                _nm.TransportManager.Transport.GetMTU(0));
            if (violations.Length == 0) return true;

            // Every line, not the first: an operator raising a server whose
            // config has two numbers out of step has to learn about both from
            // one run.
            for (int i = 0; i < violations.Length; i++)
                Debug.LogError("ServerBootstrap: network invariant violated — " + violations[i]);

            Fail(ExitBadConfig, $"{violations.Length} network invariant(s) violated (above).");
            return false;
        }

        /// The frame cap spec §3.11 (Р63/Р102) requires, expressed through the
        /// ONE public lever FishNet offers.
        ///
        /// `ServerManager.SetFrameRate(ushort)`, NOT `ServerManager.FrameRate`
        /// and NOT `Application.targetFrameRate`. The plan (:1837) and the spec
        /// (§3.11 :694) both name `ServerManager.FrameRate` as the lever; in
        /// 4.7.2 that member is `internal ushort FrameRate => _changeFrameRate
        /// ? _frameRate : (ushort)0;` and cannot be assigned from here at all,
        /// while `public void SetFrameRate(ushort value)` sets the same backing
        /// field AND raises `_changeFrameRate` (task-41b-report §7.3 point 2 filed
        /// the same correction). Assigning `Application.targetFrameRate`
        /// directly is the other trap: `NetworkManager.UpdateFramerate()`
        /// writes that property itself on every server/client state change and
        /// would overwrite whatever this class set.
        ///
        /// THE VALUE IS `TickRate + 15`, WHICH IS FISHNET'S OWN SERVER FLOOR.
        /// Under `UNITY_SERVER && !UNITY_EDITOR` `UpdateFramerate` replaces the
        /// requested rate with `TimeManager.TickRate + 15` when it equals the
        /// package maximum (500) or falls below the tick rate; choosing that
        /// same number means the clamp has nothing to correct, so the headless
        /// build and an Editor run report the SAME effective rate and the
        /// milestone В2 measurement is taken at a known frequency either way.
        /// The point of the cap is that a player loop left uncapped spins at
        /// thousands of frames a second and a `--cpus=1` measurement then
        /// reports the ceiling instead of the cost of a tick.
        ushort ChooseFrameRate() => (ushort)(_net.TickRate + 15);

        /// Р166: `MatchRoster.TryJoin` answers with a `JoinRejection`,
        /// `MatchHandshake.TryJoinDelegate` asks for a `byte`. The two live in
        /// assemblies that must not reference each other (`Ring.Networking`
        /// cannot see `Ring.Server` — that would close a compile cycle), and
        /// this bootstrap is the one node that sees both. The cast is total
        /// because `JoinRejection : byte`; the numeric agreement of the two
        /// enums is pinned BY NAME in `HandshakeTests.
        /// FromJoinRejection_MapsEveryJoinRejectionValue`, so a reordered
        /// member fails a test rather than silently renaming refusal reasons.
        bool TryJoin(string playerId, string joinToken, double nowSeconds,
            out int slot, out byte rejectionCode)
        {
            bool accepted = _roster.TryJoin(playerId, joinToken, nowSeconds, out slot,
                out JoinRejection rejection);
            rejectionCode = (byte)rejection;
            return accepted;
        }

        /// The slot-to-connection pairing, recorded the only moment it exists
        /// (`MatchHandshake`'s I-3). Called from inside the handshake's own
        /// broadcast handler, before its welcome send, and never for a refused
        /// candidate.
        void OnAccepted(int slot, NetworkConnection connection)
        {
            _slotConnections[slot] = connection;
            if (slot >= _slotCount) _slotCount = slot + 1;

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "ServerBootstrap: player accepted slot={0} clientId={1} connected={2}/{3} " +
                "atSeconds={4:F3}",
                slot, connection.ClientId, _roster.ConnectedCount, _config.MaxPlayers,
                NowSeconds()));
        }

        double NowSeconds() => _clock.Elapsed.TotalSeconds;

        void OnPostTick()
        {
            if (_phase == Phase.Exiting) return;

            LogEffectiveFrameRateOnce();

            double now = NowSeconds();
            switch (_phase)
            {
                case Phase.WaitingForPlayers:
                    PollJoinPhase(now);
                    break;
                case Phase.Running:
                    PollRunningMatch(now);
                    break;
                case Phase.Ending:
                    PollLinger(now);
                    break;
            }
        }

        /// Spec §3.11 asks for the EFFECTIVE frame rate to be logged AFTER the
        /// start, and that word is doing real work: the effective value is not
        /// readable at boot step 8. `SetFrameRate` calls `UpdateFramerate`
        /// immediately, but that method only assigns `Application.
        /// targetFrameRate` when a server or client is already STARTED, and at
        /// step 7 neither is. `StartConnection` does not fix it either:
        /// `Tugboat`'s server socket ENQUEUES `LocalConnectionState.Started`
        /// and the queue is drained by `IterateIncoming`, which the tick loop
        /// calls — so `ServerManager.Started` first reads true, and
        /// `UpdateFramerate` first actually assigns, during the tick cycle,
        /// ahead of `OnPostTick` in the same tick. Logging here, once, on the
        /// first tick that sees a started server, is therefore the earliest
        /// honest reading. (The socket itself binds synchronously inside
        /// `StartConnection` — the port is listening before this line ever
        /// runs; it is the package's own bookkeeping that lags by one tick.)
        void LogEffectiveFrameRateOnce()
        {
            if (_frameRateLogged || !_nm.ServerManager.Started) return;
            _frameRateLogged = true;

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "ServerBootstrap: server started — requestedFrameRate={0} " +
                "effectiveTargetFrameRate={1} tickRate={2}",
                _requestedFrameRate, Application.targetFrameRate, _nm.TimeManager.TickRate));
        }

        /// The four questions of the pre-match tick, in the order §6k fixes
        /// them (Р161/Р165, spec §3.10/§3.11).
        void PollJoinPhase(double now)
        {
            // 1. Р165 — a match with a dead seat does not start, ever. Slots
            // are never revoked (`MatchRoster` has no rollback and a returning
            // client would be refused `DuplicatePlayer`), so this is not a
            // wait for the seat to recover: it is a permanent veto, and the
            // match then runs out its join deadline below and the process
            // exits 3. Compacting the array instead would rename every player
            // above the hole, whose `PlayerIndex` the handshake already
            // promised.
            bool everySeatLive = true;
            for (int i = 0; i < _slotCount; i++)
            {
                if (_slotConnections[i].IsActive) continue;
                everySeatLive = false;
                break;
            }

            // 2. Start, if the roster says so.
            if (everySeatLive && _roster.ShouldStart(now))
            {
                StartMatch(now);
                return;
            }

            // 3. The join deadline.
            if (now >= _net.JoinTimeoutSeconds)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture,
                    "ServerBootstrap: no match after {0}s — connected={1}/{2}, everySeatLive={3}.",
                    _net.JoinTimeoutSeconds, _roster.ConnectedCount, _config.MaxPlayers,
                    everySeatLive));
                Exit(ExitNoPlayers);
                return;
            }

            // 4. The abandon watchdog. It is checked AFTER the deadline, so a
            // tick on which both would fire exits 3 — but the two can only
            // collide when the grace is long enough to outlast the deadline,
            // and on the shipped numbers (30 against 120) an emptied server
            // always exits 0 long before the deadline is reached.
            PollAbandonWatchdog(now);
        }

        /// Spec §3.10: "all clients disconnected -> grace -> exit", exit code
        /// 0 by §3.11 ("played out OR everyone left"). The grace starts when
        /// the live count reaches zero HAVING BEEN ABOVE IT, never at boot —
        /// see `_anyConnectionEverLive`'s own note. `NetworkConnection.
        /// IsActive` is the package's own "not disconnected, not
        /// disconnecting" predicate, the same one `MatchServer` uses to decide
        /// who gets a frame.
        ///
        /// IT LIVES IN THE SHARED TICK HANDLER, SO IT IS ARMED IN THE JOIN
        /// PHASE AND DURING THE MATCH BOTH — and the honest half of that: in
        /// a RUNNING match it is structurally unreachable today. `MatchServer`
        /// kills the player behind every dead connection on the same tick the
        /// disconnect is discovered (its step 2a) and asks `MatchEndPolicy` on
        /// that same tick (2b), so a match whose last client leaves ends as
        /// `AllPlayersDead` immediately — and `MatchServer` is ahead of this
        /// handler in the FIFO chain, so `Outcome` is already non-`None` by
        /// the time `PollRunningMatch` looks. The watchdog is still evaluated
        /// there rather than confined to the join phase, because that
        /// shadowing is a property of `MatchServer`'s current end policy, not
        /// of this class: a policy that ever stops ending matches on an empty
        /// arena must not silently leave the process running forever.
        void PollAbandonWatchdog(double now)
        {
            int live = 0;
            for (int i = 0; i < _slotCount; i++)
                if (_slotConnections[i].IsActive) live++;

            if (live > 0)
            {
                _anyConnectionEverLive = true;
                _abandonedSinceSeconds = -1.0;
                return;
            }

            if (!_anyConnectionEverLive) return;

            if (_abandonedSinceSeconds < 0.0)
            {
                _abandonedSinceSeconds = now;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "ServerBootstrap: every client disconnected at {0:F3}s — " +
                    "exiting in {1}s unless someone returns.",
                    now, _net.MatchAbandonGraceSeconds));
                return;
            }

            if (now - _abandonedSinceSeconds >= _net.MatchAbandonGraceSeconds)
            {
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "ServerBootstrap: abandon grace of {0}s elapsed — exiting.",
                    _net.MatchAbandonGraceSeconds));
                Exit(ExitAbandoned);
            }
        }

        /// Р167(б): freeze the roster, spawn one owned object per slot, hand
        /// both arrays to `MatchServer`. The world and the snapshot assembler
        /// are built INSIDE `StartMatch` (Р151/Р155г) — this class never
        /// constructs a `SimulationWorld`.
        void StartMatch(double now)
        {
            int playerCount;
            try
            {
                _roster.Start();
                playerCount = _roster.PlayerCount;
            }
            catch (Exception ex)
            {
                Fail(ExitBadConfig, "roster refused to start — " + ex.Message);
                return;
            }

            var connections = new NetworkConnection[playerCount];
            Array.Copy(_slotConnections, connections, playerCount);

            var controllers = new PlayerNetworkController[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                // OWNERSHIP IS NOT OPTIONAL. `IsOwner` is what decides both
                // that a client builds a local prediction replica for this
                // object and which branch `PlayerNetworkController`'s
                // replicate takes; an unowned spawn would produce a body
                // nobody drives. `Configure` is deliberately NOT called here
                // — `StartMatch` does it for every controller it is handed.
                GameObject instance = Instantiate(_playerPrefab);
                _nm.ServerManager.Spawn(instance, connections[i]);

                if (!instance.TryGetComponent(out PlayerNetworkController controller))
                {
                    Fail(ExitBadConfig, "the player prefab has no PlayerNetworkController on its " +
                        "root — the spawned object cannot be driven by the match.");
                    return;
                }
                if (!controller.IsSpawned)
                {
                    // `ServerManager.Spawn` reports a refusal as a warning and
                    // returns; without this check the match would start with a
                    // controller that is not on the network at all.
                    Fail(ExitBadConfig, $"slot {i}'s player object failed to spawn over the " +
                        "network (see the warning above).");
                    return;
                }
                controllers[i] = controller;
            }

            try
            {
                _matchServer.StartMatch(_config.Seed, in _simConfig, _epoch, connections,
                    controllers);
            }
            catch (Exception ex)
            {
                // task-40-report §6.2, hole 3: `StartMatch` stops the previous
                // match BEFORE it builds the new world, so a throw past that
                // point leaves "no match running AND no outcome" — a state a
                // bootstrap that only POLLED `Outcome` would wait in forever.
                // The exception is caught here for exactly that reason, and it
                // is exit code 2 because every guard that can throw out of
                // that method is a statement about the configuration.
                Fail(ExitBadConfig, "match failed to start — " + ex.Message);
                return;
            }

            _matchStartedSeconds = now;
            _phase = Phase.Running;

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "ServerBootstrap: match-start matchId={0} seed={1} epoch={2} playerCount={3} " +
                "startMode={4} atSeconds={5:F3}",
                _config.MatchId, _config.Seed, _epoch, playerCount, _config.StartMode, now));
        }

        void PollRunningMatch(double now)
        {
            MatchEndReason outcome = _matchServer.Outcome;
            if (outcome == MatchEndReason.None)
            {
                PollAbandonWatchdog(now);
                return;
            }

            // The order is fixed: ask `Outcome` first, `ExitCodeFor` second —
            // that method throws on `None` rather than inventing a code for a
            // match that has not ended.
            _pendingExitCode = MatchEndPolicy.ExitCodeFor(outcome);
            _matchEndedSeconds = now;
            _phase = Phase.Ending;

            LogMatchSummary(now);
        }

        /// The linger of spec §3.10 (`MatchEndLingerSeconds`), measured on this
        /// class's own axis because there are no world ticks left to measure
        /// it in: `MatchServer` ends a match instantly — broadcast, `StopMatch`,
        /// outcome — and this wait is what absorbs the race between the
        /// `Reliable` `MatchEndedNet` and the `Unreliable` final snapshot it
        /// belongs to (`MatchServer`'s own "PRICE OF THE LATE EXECUTION").
        void PollLinger(double now)
        {
            if (now - _matchEndedSeconds < _net.MatchEndLingerSeconds) return;
            Exit(_pendingExitCode);
        }

        /// Spec §3.11's structured per-match line, built from ONE
        /// `MatchSummary` and nothing else. Every number the summary carries is
        /// captured by `MatchServer` at the one moment it is all still
        /// readable — after `StopMatch` the world and the assembler are gone
        /// and `StatsFor` throws — so re-reading any of it here is impossible
        /// as well as wrong (Р151: one number, one home). What this class adds
        /// is only what this class owns: `matchId`, the seed, the wall-clock
        /// duration off its own axis, and the exit code.
        ///
        /// ONE LOG EVENT, NOT ONE PER PLAYER: a per-match line that arrives in
        /// N interleaved pieces is not a line a log collector can key on.
        void LogMatchSummary(double now)
        {
            MatchSummary summary = _matchServer.LastSummary;
            TickTimeAccumulator tickTime = _matchServer.TickTime;

            var sb = new StringBuilder(512);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "ServerBootstrap: match-end matchId={0} seed={1} epoch={2} playerCount={3} " +
                "reason={4} exitCode={5} finalTick={6} durationSeconds={7:F3} " +
                "wavesCleared={8} mobSpawnsSkipped={9} projectileSpawnsSkipped={10} " +
                "worldDroppedEvents={11} tickSamples={12} tickAvgMs={13:F3} tickMaxMs={14:F3} " +
                "lingerSeconds={15}",
                _config.MatchId, _config.Seed, summary.Epoch, summary.PlayerStats.Length,
                summary.Reason, _pendingExitCode, summary.FinalTick, now - _matchStartedSeconds,
                summary.World.WavesCleared, summary.World.MobSpawnsSkipped,
                summary.World.ProjectileSpawnsSkipped, summary.DroppedEvents,
                tickTime.Count, tickTime.AverageMs, tickTime.MaxMs,
                _net.MatchEndLingerSeconds);

            for (int i = 0; i < summary.PlayerStats.Length; i++)
            {
                MatchStats p = summary.PlayerStats[i];
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "\n  player[{0}] kills={1} headshotKills={2} shotsFired={3} shotsHit={4} " +
                    "dashesUsed={5} slidesUsed={6} deathTick={7} damageTaken={8:F1}",
                    i, p.Kills, p.HeadshotKills, p.ShotsFired, p.ShotsHit, p.DashesUsed,
                    p.SlidesUsed, p.DeathTick, p.DamageTaken);
            }

            // The snapshot's own losses (spec §3.11's "dropped entities and
            // snapshot events") live per connection, in the assembler's
            // `NetStats` — which is why `MatchSummary` carries them at all:
            // by the time this method runs the assembler that owned them has
            // already been released (task-40-report §6.3, I-6).
            for (int i = 0; i < summary.ConnectionStats.Length; i++)
            {
                NetStats c = summary.ConnectionStats[i];
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "\n  conn[{0}] droppedEntities={1} droppedEvents={2} staleSnapshots={3} " +
                    "duplicateSnapshots={4} inputStarved={5} inputOverwritten={6} " +
                    "unconfirmedGhosts={7} edgeRequestsRejected={8} bytesDown={9} bytesUp={10}",
                    i, c.DroppedEntities, c.DroppedEvents, c.StaleSnapshots,
                    c.DuplicateSnapshots, c.InputStarved, c.InputOverwritten,
                    c.UnconfirmedGhosts, c.EdgeRequestsRejected, c.BytesDown, c.BytesUp);
            }

            Debug.Log(sb.ToString());
        }

        /// A boot-time failure: one error line, then the exit code. Separate
        /// from `Exit` only so the two halves of "say why, then leave" cannot
        /// drift apart at the fifteen call sites above.
        void Fail(int exitCode, string message)
        {
            Debug.LogError("ServerBootstrap: " + message);
            Exit(exitCode);
        }

        /// The one place the process's exit code is set.
        ///
        /// `Application.Quit(int)` DOES NOT END THE FRAME, and Unity documents
        /// it as ignored in the Editor — so a dev run started from the Editor
        /// never demonstrates an exit code, and the codes of §3.11 are only
        /// observable on a built player (which is what the Task 41c evidence
        /// exercises). What the Editor DOES leave behind is this method's own
        /// `exiting with code N` line above; whether the engine adds one of its
        /// own was never observed here, so nothing is claimed about it. `_phase` going terminal is what keeps the rest
        /// of this frame, and any tick still queued behind it, from acting on
        /// a process that is already leaving.
        void Exit(int exitCode)
        {
            if (_phase == Phase.Exiting) return;
            _phase = Phase.Exiting;

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "ServerBootstrap: exiting with code {0} at {1:F3}s.", exitCode, NowSeconds()));
            Application.Quit(exitCode);
        }
    }
}
