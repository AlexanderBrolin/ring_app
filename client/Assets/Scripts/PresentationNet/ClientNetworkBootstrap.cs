using System;
using System.Globalization;
using FishNet;
using FishNet.Managing;
using Ring.Data;
using UnityEngine;

namespace Ring.Presentation.Net
{
    /// What this process was launched to do, read off the command line (Stage
    /// 2 Task 44e). A plain value with no `UnityEngine` in it, no clock, no
    /// logger and no side effect: `Parse` below IS the mode decision, and the
    /// component after it only acts on the answer and prints `Complaint` when
    /// there is one. Same split `ClientLinkState`/`ClientMatchLink` keep in
    /// the networking assembly, and for the same reason — the decision is
    /// worth reading on its own, the wiring is not.
    ///
    /// SOLO IS THE ABSENCE OF THE SWITCH, AND IT IS SILENT. `Main.unity` is
    /// the scene the owner plays, every Stage 1 and asset-phase milestone was
    /// taken on it, and it now carries a network stack it must be able to
    /// ignore completely. So a command line without `-ring-connect` produces
    /// `default(ClientLaunchOptions)` — `Networked` false, `Complaint` null —
    /// and the component returns before it touches `ClientManager`,
    /// `NetworkSimBackend` or the console.
    ///
    /// A MALFORMED VALUE REFUSES THE NETWORKED LAUNCH RATHER THAN GUESSING
    /// (Р82: a refusal is a value and a log, never a throw). The alternative
    /// — keep the switch, substitute a default for the part that did not
    /// parse — would connect somewhere other than where the operator typed,
    /// which is the one outcome nobody can diagnose from a screenshot of the
    /// game. Refusing leaves a solo session, exactly as if the switch had not
    /// been passed, plus one line naming the argument and what was wrong with
    /// it.
    ///
    /// WHY A COMMAND-LINE SWITCH AND NOT A CHECKBOX IN THE SCENE. Milestone
    /// В1 is "two clients locally": both come out of ONE build, and a scene
    /// field would have to be flipped, rebuilt and — worse — could reach a
    /// commit already switched on, turning the owner's solo scene into one
    /// that dials a server nobody is running.
    public readonly struct ClientLaunchOptions
    {
        /// The switch that makes a launch networked. Compared with an ordinal
        /// `==`, never case-insensitively — the same strictness
        /// `MatchConfigLoader` applies to `startMode`, and for the same
        /// reason: a launch script is machine-written, and quietly tolerating
        /// a typo in one place teaches the operator that the spelling does
        /// not matter in the other.
        ///
        /// Its value is optional and has the form `host` or `host:port`. Both
        /// halves have a default named below, so `-ring-connect` alone is the
        /// ordinary local case; `-ring-connect 10.0.0.5` reaches another
        /// machine on the default port; `-ring-connect 10.0.0.5:7778` names
        /// both.
        ///
        /// AN IPv6 LITERAL IS NOT ACCEPTED HERE, and that is a refusal rather
        /// than a silent misread: everything after the LAST colon is the
        /// port, so `::1` would offer `1` as a port and `:` as a host, and
        /// the empty host is refused. Use an IPv4 address or a hostname. The
        /// switch is a development knob for В1; a launcher that needs more
        /// than this belongs to Э5's meta, not to a parse function.
        public const string ConnectArgument = "-ring-connect";

        /// Who this client claims to be. The server compares it against
        /// `MatchConfig.Players` when that roster is NON-EMPTY and answers
        /// `UnknownPlayer` to a name that is not in it; with the dev roster —
        /// which is empty — `MatchRoster.TryJoin` skips the membership and
        /// token checks entirely and accepts any non-empty id up to
        /// `MaxPlayers`. An EMPTY id is refused on both sides: the roster
        /// answers `InvalidPlayerId`, and this parse refuses before the
        /// connection is opened at all, so the mistake is named here instead
        /// of arriving as a disconnect.
        public const string PlayerIdArgument = "-ring-player-id";

        /// The token that goes out in the hello. Compared by the server ONLY
        /// for a named roster; an empty string is a legal token there
        /// (`MatchConfigLoader` coalesces a missing `joinToken` to exactly
        /// that), which is why the default below is empty rather than absent.
        ///
        /// A token that STARTS WITH A DASH cannot be passed through this
        /// switch — the argument after a switch is taken as its value only
        /// when it does not itself look like a switch, which is what lets
        /// `-ring-connect` stand alone. Named rather than worked around: real
        /// tokens arrive from Э5's meta, not from a shell.
        public const string JoinTokenArgument = "-ring-join-token";

        /// The loopback address, spelled numerically rather than as
        /// `localhost`. The name can resolve to the IPv6 loopback first,
        /// while the server socket in this project's dev loop is opened over
        /// IPv4 — a mismatch that presents as a client that never connects
        /// and a server that never sees it. `Tugboat`'s own default client
        /// address is the NAME, which is one more reason this side states the
        /// address explicitly instead of leaving the transport's field alone.
        public const string DefaultHost = "127.0.0.1";

        /// The port the dev server binds. It is a second home of a number
        /// that already has one — `MatchConfigLoader`'s dev default, which
        /// the container runbook's `-p 7777:7777/udp` also copies — and the
        /// duplication is deliberate rather than overlooked: that constant is
        /// private to `Ring.Server`, an assembly the client must not
        /// reference, and widening it would be a change in a path this task
        /// may not touch. Changing the dev port therefore means changing it
        /// here too, and this sentence is the only warning there is.
        ///
        /// NOT `Tugboat`'s OWN DEFAULT, WHICH IS A DIFFERENT NUMBER. The
        /// transport ships `7770`. A client that let the transport keep its
        /// serialized port would quietly dial a port the dev server never
        /// binds, so the port is always passed explicitly to
        /// `StartConnection` and never authored into the scene.
        public const ushort DefaultPort = 7777;

        /// Whether the switch was present AND everything it carried parsed.
        /// False is the solo launch, whichever of the two reasons produced it
        /// — `Complaint` is what tells them apart.
        public readonly bool Networked;

        public readonly string Host;
        public readonly ushort Port;
        public readonly string PlayerId;
        public readonly string JoinToken;

        /// What was wrong with the command line, or `null` when there was
        /// nothing to say. Carried as a value instead of being logged from
        /// the parse so this type stays free of `UnityEngine`, and so the one
        /// place that prints is the one place that knows whether printing is
        /// wanted.
        public readonly string Complaint;

        ClientLaunchOptions(bool networked, string host, ushort port, string playerId,
            string joinToken, string complaint)
        {
            Networked = networked;
            Host = host;
            Port = port;
            PlayerId = playerId;
            JoinToken = joinToken;
            Complaint = complaint;
        }

        /// Reads the switches above out of `commandLine`, which is
        /// `Environment.GetCommandLineArgs()` in production — passed in
        /// rather than fetched so this function is a function of its
        /// arguments and nothing else.
        ///
        /// `fallbackPlayerId` is the id to use when the operator named none.
        /// It is a PARAMETER because a useful default is not a constant: two
        /// clients started from one build on one machine must arrive with
        /// DIFFERENT ids or the second is answered `DuplicatePlayer`, which
        /// is exactly milestone В1's shape. Inventing it here would put a
        /// non-deterministic value inside an otherwise pure function; the
        /// caller invents it and this function only falls back to it.
        public static ClientLaunchOptions Parse(string[] commandLine, string fallbackPlayerId)
        {
            if (commandLine == null) return default;

            bool networked = false;
            string endpoint = null;
            string playerId = null;
            string joinToken = null;

            for (int i = 0; i < commandLine.Length; i++)
            {
                string arg = commandLine[i];
                if (arg == ConnectArgument)
                {
                    networked = true;
                    endpoint = ValueAt(commandLine, i);
                }
                else if (arg == PlayerIdArgument)
                {
                    playerId = ValueAt(commandLine, i);
                }
                else if (arg == JoinTokenArgument)
                {
                    joinToken = ValueAt(commandLine, i);
                }
            }

            // The other two switches are meaningless without this one, and
            // saying so would mean printing a line on a launch that asked for
            // nothing. Silence is the contract of a solo start.
            if (!networked) return default;

            string host = DefaultHost;
            ushort port = DefaultPort;
            if (endpoint != null && !TryParseEndpoint(endpoint, ref host, ref port, out string endpointError))
                return Refuse(endpointError);

            if (playerId != null && playerId.Length == 0)
            {
                return Refuse($"{PlayerIdArgument} was given an empty value. The server refuses an "
                    + "empty player id outright (InvalidPlayerId), so there is nothing to connect "
                    + "with; pass a name, or drop the switch and take the generated one.");
            }

            return new ClientLaunchOptions(true, host, port, playerId ?? fallbackPlayerId,
                joinToken ?? string.Empty, complaint: null);
        }

        /// `host` or `host:port`. The port is validated to the same range the
        /// server's own config loader enforces — `[1, 65535]`, with `0`
        /// excluded because a zero port means "any free port" to a socket and
        /// nothing at all to a client dialing one.
        static bool TryParseEndpoint(string endpoint, ref string host, ref ushort port, out string error)
        {
            int colon = endpoint.LastIndexOf(':');
            string hostPart = colon < 0 ? endpoint : endpoint.Substring(0, colon);
            string portPart = colon < 0 ? null : endpoint.Substring(colon + 1);

            if (hostPart.Length == 0)
            {
                error = $"{ConnectArgument} was given \"{endpoint}\", which names no host. "
                    + $"The form is {ConnectArgument} [host[:port]]; an IPv6 literal does not fit "
                    + "it, because everything after the last colon is read as the port.";
                return false;
            }

            if (portPart != null)
            {
                // NumberStyles.None on purpose: it rejects a sign, a
                // thousands separator and surrounding whitespace, so
                // "+7777", "-1" and " 7777" are refusals rather than a port
                // the operator did not type. `ushort` closes the upper end
                // by construction.
                if (!ushort.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture,
                        out ushort parsed) || parsed == 0)
                {
                    error = $"{ConnectArgument} was given \"{endpoint}\", whose port \"{portPart}\" "
                        + "is not a number in [1, 65535].";
                    return false;
                }
                port = parsed;
            }

            host = hostPart;
            error = null;
            return true;
        }

        /// The argument after `index`, when there is one and it does not
        /// itself look like a switch. `null` means "the switch stood alone",
        /// which is a legal shape for `-ring-connect` and a mistake for the
        /// other two — and the difference is decided by the caller, not here.
        static string ValueAt(string[] commandLine, int index)
        {
            int next = index + 1;
            if (next >= commandLine.Length) return null;
            string value = commandLine[next];
            if (value.Length > 0 && value[0] == '-') return null;
            return value;
        }

        /// A refusal is a solo launch that has something to say. `PlayerId`
        /// and `JoinToken` are left null rather than defaulted: there is no
        /// connection to give them to, and a plausible-looking value on a
        /// refused launch is the kind of thing a later reader takes for a
        /// decision that was made.
        static ClientLaunchOptions Refuse(string complaint) =>
            new ClientLaunchOptions(false, DefaultHost, DefaultPort, null, null, complaint);
    }

    /// Puts the networked backend behind the Presentation facade and opens
    /// the connection (Stage 2 Task 44e) — the caller `NetworkSimBackend` and
    /// `SimulationRunner.TryUseBackend` were both written for and neither had
    /// until now. It lives in `Ring.Presentation.Net` because that is the one
    /// assembly with both `SimulationRunner` and FishNet in scope; the scene
    /// object it sits on is written by `StageTwoSceneBootstrap`, never by
    /// hand.
    ///
    /// FIVE STEPS, IN THIS ORDER, AND EACH ONE IS HELD BY SOMETHING:
    ///   1. decide whether this launch is networked at all
    ///      (`ClientLaunchOptions.Parse`) — and if it is not, do nothing
    ///      whatsoever;
    ///   2. build the backend;
    ///   3. hand it to the facade and READ THE ANSWER;
    ///   4. only then open the connection;
    ///   5. drop every subscription when this object goes away.
    ///
    /// STEPS 2-3 RUN IN `Awake`, AND THIS COMPONENT IS PINNED BELOW THE
    /// FACADE'S OWN ORDER TO GET THERE FIRST. `SimulationRunner` is
    /// `[DefaultExecutionOrder(-50)]` and its `Awake` starts a match outright
    /// on whatever backend it holds; a backend installed after that is
    /// refused by `TryUseBackend`, which is why this class carries `-100`.
    /// The attribute is the mechanism, and it is the only one available: the
    /// project's other two candidates do not reach here at all — a
    /// `RuntimeInitializeOnLoadMethod` runs either before the scene exists
    /// (nothing to find) or after every `Awake` in it (too late), and the
    /// per-script order table lives in `ProjectSettings`, which the phase's
    /// rollback gate keeps clean. What makes the arrangement trustworthy is
    /// not the number alone but the pairing: if the order is ever wrong, the
    /// install is refused with a red line rather than accepted quietly, and
    /// this class checks that answer instead of assuming it.
    ///
    /// STEP 4 RUNS IN `Start`, AND THAT IS NOT A SECOND ORDER NUMBER. Unity
    /// calls every `Awake` of a scene before it calls any `Start` in it, so
    /// `Start` is after the facade's `Awake` no matter what the execution
    /// order says — including after `_backend.Restart(...)`, which is what
    /// builds `ClientMatchLink` and registers the snapshot channel. That
    /// matters because the link sends the hello from FishNet's own
    /// connection-state event, and `ClientManager.Broadcast` drops anything
    /// sent before the transport reports `Started`: a connection opened
    /// before the link existed would leave this client silent until the
    /// server's `JoinTimeoutSeconds` expired. The obligation is stated in
    /// `NetworkSimBackend.Restart` and in `ClientMatchLink`'s class doc; here
    /// it is also CHECKED, see `Start`.
    ///
    /// IT IS INERT IN SOLO, AND THAT IS THE POINT. Without `-ring-connect`
    /// this component reads the command line, finds nothing and returns —
    /// no `NetworkSimBackend`, no `ClientManager` call, no console line. The
    /// facade keeps the `LocalSimBackend` its own field initializer gave it,
    /// which is the behavior `Main.unity` had before a network stack was ever
    /// put on it.
    [DefaultExecutionOrder(-100)]
    public sealed class ClientNetworkBootstrap : MonoBehaviour
    {
        /// The facade this installs into. Serialized rather than found at run
        /// time, the same choice `ServerBootstrap` makes and for the reason
        /// stated there: a bootstrap writes the reference, so a missing one
        /// is a scene defect visible in the Inspector rather than a name
        /// lookup that can silently pick the wrong object.
        [SerializeField] SimulationRunner _runner;

        /// Network tuning — the one asset the backend cannot be built
        /// without. Deliberately not a `SimConfigBuilder.Build` parameter
        /// (Р52), which is why it is a field here and not something the
        /// facade could hand over.
        [SerializeField] NetConfig _net;

        NetworkManager _nm;
        NetworkSimBackend _backend;
        ClientLaunchOptions _options;
        bool _connectionOpen;
        bool _tornDown;

        void Awake()
        {
            // Step 1. A fresh id per PROCESS, not per build: milestone В1
            // runs two clients out of one build on one machine, and a
            // constant default would have the second one answered
            // `DuplicatePlayer` by a roster that already holds the first. A
            // GUID rather than the process id because it needs no
            // `System.Diagnostics` and cannot collide; it is printed with the
            // connection line below, so the match log still names who this
            // client is.
            _options = ClientLaunchOptions.Parse(Environment.GetCommandLineArgs(),
                "dev-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            if (!_options.Networked)
            {
                if (_options.Complaint != null)
                {
                    Debug.LogWarning($"ClientNetworkBootstrap: {_options.Complaint} Starting solo "
                        + "on the local backend, exactly as an unflagged launch would.");
                }
                return;
            }

            _nm = InstanceFinder.NetworkManager;
            if (_nm == null)
            {
                Debug.LogError("ClientNetworkBootstrap: no NetworkManager in the loaded scenes — "
                    + "Main.unity must carry one beside this component (run "
                    + "Ring/Bootstrap/Stage 2 Client Networking). Starting solo instead.");
                return;
            }

            if (_runner == null || _net == null)
            {
                Debug.LogError("ClientNetworkBootstrap: the scene left "
                    + $"{(_runner == null ? "_runner" : "_net")} unassigned, and the networked "
                    + "backend cannot be built without it. Re-run Ring/Bootstrap/Stage 2 Client "
                    + "Networking. Starting solo instead.");
                return;
            }

            // Step 2.
            var backend = new NetworkSimBackend(_nm, _net, _options.PlayerId, _options.JoinToken);

            // Step 3. The answer is read, never assumed: a refusal means the
            // order this class exists to keep was broken, and the facade has
            // already said which of the three mistakes it was.
            if (!_runner.TryUseBackend(backend))
            {
                // The instance is ours to drop, and dropping one means
                // unregistering it (`NetworkSimBackend.Unregister`). Nothing
                // is subscribed yet — this one has not been restarted, and
                // that class registers from `Restart` rather than from its
                // constructor — so the call is a no-op today. It is made
                // anyway: it costs a branch, and the day the order above
                // changes it is the difference between a dropped backend and
                // a dropped backend still decoding frames into a ring nobody
                // reads.
                backend.Unregister();
                Debug.LogError("ClientNetworkBootstrap: the facade refused the networked backend "
                    + "(see the line above), so this launch runs on the local one. It will show a "
                    + "match of its own and will never talk to the server.");
                return;
            }

            _backend = backend;
        }

        void Start()
        {
            if (_backend == null) return; // solo, or the install was refused

            // Step 4, with the ordering obligation checked rather than
            // narrated. `SimulationRunner.Restart` records `Seed` only after
            // the backend accepted the restart, and `RestartNewSeed` seeds it
            // from the wall clock, so a non-zero `Seed` is this side's
            // evidence that `NetworkSimBackend.Restart` has run — which is
            // what built `ClientMatchLink` and registered the snapshot
            // channel. A zero here means the facade's `Awake` never happened
            // (a deactivated object is the way that occurs), and opening the
            // connection then would send a hello nobody assembled, leaving
            // the server to wait out `JoinTimeoutSeconds` and exit.
            if (_runner.Seed == 0)
            {
                Debug.LogError("ClientNetworkBootstrap: the facade has not started a match yet, so "
                    + "the link that sends the hello does not exist. Not opening the connection — "
                    + "a connection without it would be silence the server can only answer with a "
                    + "join timeout. Check that the Simulation object is active in the scene.");
                return;
            }

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "ClientNetworkBootstrap: connecting to {0}:{1} as playerId=\"{2}\" ({3}).",
                _options.Host, _options.Port, _options.PlayerId,
                _options.JoinToken.Length == 0 ? "no join token" : "join token supplied"));

            // The address-and-port overload rather than `SetClientAddress` +
            // `SetPort` + `StartConnection()`: it does both writes itself, so
            // the pair of calls would state the endpoint twice. The scene's
            // `Tugboat` keeps the package's own address and port for the same
            // reason `Server.unity` does — a value authored there would be a
            // second source, and the effective one would be whichever ran
            // last.
            if (!_nm.ClientManager.StartConnection(_options.Host, _options.Port))
            {
                Debug.LogError($"ClientNetworkBootstrap: the transport refused to dial "
                    + $"{_options.Host}:{_options.Port}. Nothing was connected; this client stays "
                    + "on a match of its own.");
                return;
            }

            _connectionOpen = true;
        }

        /// Step 5, on the two events that mean "this object is going away".
        ///
        /// BOTH, BECAUSE NEITHER ALONE COVERS THE TWO EXITS. Quitting the
        /// player raises `OnApplicationQuit` first and every object is still
        /// alive at that moment, which is when a disconnect can still be sent
        /// and the `NetworkManager` — marked `DontDestroyOnLoad` by FishNet
        /// itself — is certainly still there to unsubscribe from. Ending a
        /// scene, or stopping play in the Editor, arrives as `OnDestroy`
        /// instead. The work is idempotent so the ordinary case, which fires
        /// both, does it once.
        void OnApplicationQuit() => Teardown();

        void OnDestroy() => Teardown();

        /// Unregisters BEFORE stopping the connection, so the shutdown
        /// transitions reach no handler of ours at all. FishNet keys
        /// broadcast handlers by delegate identity and the manager outlives
        /// this component, so a backend left subscribed would keep decoding
        /// frames into a ring nobody reads — the scenario
        /// `NetworkSimBackend.Unregister` is written for.
        ///
        /// The connection is stopped rather than left to the process exit
        /// because a client that disappears without a word is the server's
        /// abandon watchdog case: it waits out its grace period before
        /// concluding what a disconnect would have told it immediately.
        void Teardown()
        {
            if (_tornDown) return;
            _tornDown = true;

            if (_backend == null) return;
            _backend.Unregister();

            if (!_connectionOpen) return;
            _connectionOpen = false;
            // Unity's fake-null: the manager can already be gone on a scene
            // teardown that destroyed it first, and asking a destroyed object
            // to stop a connection is a NullReferenceException rather than a
            // no-op.
            if (_nm != null) _nm.ClientManager.StopConnection();
        }
    }
}
