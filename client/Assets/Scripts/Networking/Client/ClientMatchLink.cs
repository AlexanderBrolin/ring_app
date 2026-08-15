using System;
using FishNet.Managing;
using FishNet.Transporting;
using Ring.Data;
using Ring.Networking.Protocol;

namespace Ring.Networking.Client
{
    /// Pure decision core for the CLIENT end of the connection handshake and
    /// the match life cycle (Stage 2 Task 44b, spec §3.10; the client halves
    /// of Task 39's and Task 40's server-side work). No FishNet types
    /// anywhere in this class — `ClientMatchLink` below is the only thing
    /// that touches the network — the same split as `HandshakeDecision`/
    /// `MatchHandshake`, `PlayerPredictionCore`/`PlayerNetworkController` and
    /// `InputStarvation`/`MatchServer`.
    ///
    /// (The four `Ring.Networking.Protocol` structs below are wire DATA, not
    /// FishNet machinery — `HandshakeDecision.Evaluate` already takes
    /// `in ClientHelloNet` for the same reason. What the split keeps out is
    /// `NetworkManager`/`ClientManager`/`Channel`, none of which appear here.)
    ///
    /// NOT ONE METHOD HERE THROWS, AND THAT IS NOT A STYLE CHOICE. Every one
    /// of them is called, directly, from inside a FishNet broadcast handler,
    /// and the CLIENT's parsing loop — `ClientManager.ParseReader`, which walks
    /// the messages BATCHED into one datagram and dispatches each in turn —
    /// answers an exception out of one of them two ways, neither of them a
    /// refusal. In the Editor and in a development build (that file opens by
    /// defining `DEVELOPMENT` as `UNITY_EDITOR || DEVELOPMENT_BUILD`) the loop
    /// has no try/catch at all and the exception escapes into the transport's
    /// receive callback. THIS PROJECT BUILDS BOTH KINDS OF PLAYER, so both
    /// branches are reachable in a shipped artifact: `BuildCommands` passes
    /// `BuildOptions.Development` from `BuildLinuxClientDev` and
    /// `BuildLinuxServerDev`, and passes it from nowhere else — `BuildLinuxClient`,
    /// `BuildWindowsClient` and `BuildLinuxServer` get no such define. In those
    /// three the same loop is wrapped in a try/catch compiled in exactly when
    /// `DEVELOPMENT` is NOT defined, and that catch does one thing: `LogError`
    /// naming the packet id. Which branch a given number was read under is not
    /// academic: milestone В1 is measured on the DEV pair (the overlay and the
    /// latency simulator only exist there), so a В1 reading runs under the FIRST
    /// branch, where nothing catches at all.
    ///
    /// WHAT IT COSTS IS A FRAME OF DATA, SILENTLY. Either way the throw leaves
    /// the loop, so every message batched BEHIND the one that threw is never
    /// read, and the `Objects.IterateObjectCache()` that closes the loop is
    /// skipped for that datagram — one error line in a log no player will see,
    /// and a hole in the stream nothing on this side reports.
    ///
    /// THE CLIENT IS NOT KICKED FOR IT, AND THE ASYMMETRY WITH THE SERVER RUNS
    /// ONE WAY ONLY (fix round 1, F-1 — this paragraph previously claimed the
    /// mirror consequence was "a match lost", which is the SERVER's behavior
    /// carried over untested). `KickReason.MalformedData` occurs exactly once
    /// in the whole of FishNet's `Runtime/Managing`, in `ServerManager`'s own
    /// catch, where `MatchHandshake`'s doc records it misblaming the client for
    /// a bug on the server. The client's catch contains no kick and no
    /// disconnect. So a refusal here is a VALUE — `LinkVerdict` — that the
    /// wiring turns into a log line, not because a throw would end the match,
    /// but because a throw would take the rest of the datagram down with it and
    /// leave this class's own decision unrecorded.
    ///
    /// THE PHASE IS THE WHOLE MEMORY, AND IT ONLY EVER MOVES FORWARD.
    /// `Connecting -> HelloSent -> Joined` on the ordinary path;
    /// `Joined <-> MatchEnded` across the life cycle of successive matches;
    /// `Refused` is terminal and reachable from either of the first two.
    /// Nothing returns to `Connecting`: RECONNECTION IS NOT IMPLEMENTED HERE
    /// and is not meant to be — spec §3.10 defers reconnect to milestone Э5,
    /// and a link that silently re-armed itself would send a second hello the
    /// server answers with `DuplicatePlayer` while the first seat stays
    /// claimed (`MatchRoster` has no slot-revoke path).
    public sealed class ClientLinkState
    {
        /// Where this client is in the exchange. Deliberately NOT a bool
        /// pair: "greeted", "admitted", "refused" and "the match is over" are
        /// four different facts about one connection, and every message below
        /// is answered differently in each of them.
        public enum LinkPhase : byte
        {
            /// Nothing has been sent yet — the transport may not even be up.
            Connecting,

            /// The hello is out and the server has not answered.
            HelloSent,

            /// A welcome (or a restart) admitted this client to a match.
            Joined,

            /// A refusal arrived. Terminal: nothing is applied afterwards.
            Refused,

            /// The match this client was in has ended. A restart, if the
            /// server sends one, moves back to `Joined`.
            MatchEnded,
        }

        /// What was done with one message. `Applied` is the only member that
        /// changed anything; every other one is a refusal the wiring logs.
        public enum LinkVerdict : byte
        {
            /// Accepted, and the state below reflects it.
            Applied,

            /// This message cannot arrive in the current phase at all — a
            /// welcome to a client that never greeted anyone, or a life-cycle
            /// message about a match this client was never admitted to.
            Unexpected,

            /// A second welcome, or a refusal arriving after one. See
            /// `OnWelcome`/`OnRefused` for why neither may overwrite a match
            /// in progress.
            AlreadyJoined,

            /// A second end for the match that has already ended.
            AlreadyEnded,

            /// A life-cycle message naming an epoch this client is not in.
            ForeignEpoch,

            /// A restart naming the epoch already tracked — a repeat of a
            /// message that was already acted on.
            DuplicateEpoch,

            /// An epoch of 0, which is reserved for "there is no epoch"
            /// (`MatchEpochCounter` mints 1 first and never returns 0).
            ReservedEpoch,

            /// The link was closed by a refusal; nothing is applied after it.
            LinkRefused,

            /// The welcome named a seat outside this match's roster (Stage 2
            /// Task 44c fix-round 1, F-5). See `OnWelcome` for why the byte is
            /// refused here rather than clamped or repaired downstream.
            SlotOutOfRange,
        }

        /// One message's answer: the verdict, plus the ONE side effect this
        /// core cannot perform itself.
        ///
        /// `ResetSeams` IS AN INSTRUCTION, NOT AN OBSERVATION, AND IT IS WHY
        /// THIS TYPE EXISTS. Clearing the six per-match seams needs
        /// `ClientMatchReset`, which owns objects this core must not see (it
        /// would drag `RenderSnapshot` ring buffers into a decision class).
        /// Returning the instruction instead keeps the decision — "does THIS
        /// message carry the right to reset" — inside the tested core, where
        /// `MatchEndedNet`'s "no" and `MatchRestartedNet`'s "yes" are pinned
        /// by tests, rather than inside the untestable wiring where an `if`
        /// on the message type would live.
        public readonly struct LinkAction
        {
            public readonly LinkVerdict Verdict;

            /// True exactly when the caller must call
            /// `ClientMatchReset.ResetForEpoch(Epoch)` before anything else.
            public readonly bool ResetSeams;

            /// The epoch the seams are to be reset for. Meaningful only when
            /// `ResetSeams` is true.
            public readonly ushort Epoch;

            public LinkAction(LinkVerdict verdict, bool resetSeams, ushort epoch)
            {
                Verdict = verdict;
                ResetSeams = resetSeams;
                Epoch = epoch;
            }
        }

        /// Where this connection stands. See `LinkPhase`.
        public LinkPhase Phase { get; private set; } = LinkPhase.Connecting;

        /// Whether the one hello this connection gets has already gone out.
        public bool HelloSent { get; private set; }

        /// The epoch of the match this client is in, 0 before it is in one.
        public ushort MatchEpoch { get; private set; }

        /// The authoritative seed of that match — the same value the server
        /// handed its own `SimulationWorld`. Carried by both the welcome and
        /// the restart, and replaced by the restart's own.
        public long Seed { get; private set; }

        /// This client's slot, assigned once by the welcome. A restart does
        /// NOT reassign it: §6k Р164 keeps the roster untouched across an
        /// epoch, which is why `MatchRestartedNet` carries no `PlayerIndex`
        /// to re-read in the first place.
        ///
        /// WITHIN THE ROSTER THE WELCOME WAS CHECKED AGAINST, or zero because
        /// no welcome was ever applied — `OnWelcome` refuses a seat outside it
        /// (`LinkVerdict.SlotOutOfRange`) instead of storing it, so a reader
        /// that indexes an array of that roster's size by this byte cannot be
        /// handed a number the roster has no room for. Zero before the welcome
        /// is a LEGAL seat and not a sentinel: "has a seat been assigned" is
        /// `Phase`, never this.
        public byte PlayerIndex { get; private set; }

        /// Why this client is not playing, remembered BEFORE the disconnect
        /// that follows the refusal on the wire (`MatchHandshake.Refuse`
        /// broadcasts, then calls `Disconnect(false)`). Without this the
        /// player is shown "connection lost" where the truth was "your
        /// balance data disagrees with the server's".
        ///
        /// `None` here does NOT mean "not refused" — `Phase` is what says
        /// that. A refusal message carrying the byte 0 is decoded as
        /// `UnrecognizedRejection`, the same substitution
        /// `MatchHandshake.Refuse` makes on the sending side, and for the
        /// same reason: `None` would read to a human, and to a UI, as "not
        /// refused at all".
        public HandshakeRefusal RefusalReason { get; private set; }

        /// The refusal byte exactly as it rode the wire, kept beside the
        /// decoded form so a log line can name a code from a NEWER server
        /// build that this one has no member for.
        public byte RefusalReasonRaw { get; private set; }

        /// The summary of the match that ended, as it arrived. Held whole
        /// rather than picked apart: the consumer of these numbers is the
        /// end-of-match screen (Task 44c and Presentation beyond it), and
        /// copying eleven counters out into eleven properties here would only
        /// add a second place for a swapped pair to hide.
        public MatchEndedNet EndedNet { get; private set; }

        /// May the hello go out now? `true` EXACTLY ONCE per instance, and
        /// the caller must send it when told `true` — this method has already
        /// recorded that it did.
        ///
        /// Rule: ONE hello per connection. `MatchRoster.TryJoin` answers a
        /// repeat with `JoinRejection.DuplicatePlayer`, and by the Ф8 ruling
        /// (`MatchHandshake`, fix-round 1 I-4) the server KEEPS the
        /// connection rather than tearing it down — so a second hello costs
        /// nothing but a confusing pair of log lines on both ends and buys
        /// nothing at all, since the seat is already this client's.
        public bool TryBeginHello()
        {
            if (HelloSent) return false;

            HelloSent = true;
            Phase = LinkPhase.HelloSent;
            return true;
        }

        /// The server admitted this client. Applied ONCE.
        ///
        /// A SECOND WELCOME IS A REAL POSSIBILITY, NOT A HYPOTHETICAL: a
        /// `MatchHandshake` instance that was never `Unregister`ed stays
        /// subscribed beside its replacement, and its own doc spells out the
        /// consequence — "the stale instance answers with its OWN (possibly
        /// now-wrong) epoch/roster, including sending a second
        /// `MatchWelcomeNet` carrying a stale `MatchEpoch`". Letting that
        /// rewrite the tracked epoch would point every seam at a match
        /// nobody is sending frames for.
        ///
        /// `maxPlayers` IS THE ROSTER THIS SEAT HAS TO INDEX, AND IT IS A
        /// PARAMETER FOR THE REASON EVERY OTHER FACT HERE IS (Stage 2 Task 44c
        /// fix-round 1, F-5). This class is a decision core with no assets and
        /// no world of its own — the same discipline `MatchRoster` states as
        /// "no clock of its own: every method that needs 'now' takes it as a
        /// parameter" — so the one number this decision needs arrives with the
        /// message rather than being remembered from a constructor the tests
        /// call a dozen times.
        ///
        /// WHY THE SEAT IS REFUSED RATHER THAN CLAMPED. `PlayerIndex` is not a
        /// number this client displays; it is the INDEX the render pair is read
        /// through (`RenderSnapshot.Player` indexes `Players` by it with no
        /// guard, and neither do the facade properties built on it), so a byte
        /// past the roster is an exception every frame, in the layer that draws
        /// rather than the one that parses. Clamping would seat this client in
        /// somebody else's chair and show another player's health as its own,
        /// which is a wrong picture rather than a missing one. Refusing leaves
        /// the phase at `HelloSent`: nothing is adopted and the seams are not
        /// reset. The wiring around this core logs a line naming the seat and
        /// the roster — see `ClientMatchLink.OnWelcome`, which is where both
        /// numbers are in scope; the generic refusal line carries neither, and
        /// this refusal is the one they are needed for (Stage 2 Task 44d).
        public LinkAction OnWelcome(in MatchWelcomeNet welcome, int maxPlayers)
        {
            if (Phase == LinkPhase.Refused) return Refuse(LinkVerdict.LinkRefused);
            if (Phase == LinkPhase.Joined || Phase == LinkPhase.MatchEnded)
                return Refuse(LinkVerdict.AlreadyJoined);
            if (Phase != LinkPhase.HelloSent) return Refuse(LinkVerdict.Unexpected);
            if (welcome.MatchEpoch == 0) return Refuse(LinkVerdict.ReservedEpoch);
            if (welcome.PlayerIndex >= maxPlayers) return Refuse(LinkVerdict.SlotOutOfRange);

            MatchEpoch = welcome.MatchEpoch;
            Seed = welcome.Seed;
            PlayerIndex = welcome.PlayerIndex;
            Phase = LinkPhase.Joined;

            // The welcome that opens the first match is one of the two
            // messages `ClientMatchReset`'s own doc names as carrying the
            // right to reset — the seams start out tracking no epoch at all,
            // and until they are told this one they refuse every frame.
            return new LinkAction(LinkVerdict.Applied, resetSeams: true, MatchEpoch);
        }

        /// The server refused this client. Terminal, and the reason outlives
        /// the disconnect that follows it on the wire.
        ///
        /// A REFUSAL ARRIVING AFTER A WELCOME IS IGNORED, DELIBERATELY. The
        /// one refusal an already-seated, entirely legitimate connection can
        /// trigger is `DuplicatePlayer`, and `MatchHandshake.Refuse` goes out
        /// of its way NOT to disconnect on it (fix-round 1, I-4) precisely so
        /// a real player's seat is not burned by a harmless repeat. Treating
        /// it as terminal on this end would throw the running match away for
        /// exactly that harmless repeat, from the other direction.
        public LinkAction OnRefused(in MatchRefusedNet refused)
        {
            if (Phase == LinkPhase.Joined || Phase == LinkPhase.MatchEnded)
                return Refuse(LinkVerdict.AlreadyJoined);
            if (Phase == LinkPhase.Refused) return Refuse(LinkVerdict.LinkRefused);

            RefusalReasonRaw = refused.Reason;
            RefusalReason = DecodeRefusal(refused.Reason);
            Phase = LinkPhase.Refused;

            return new LinkAction(LinkVerdict.Applied, resetSeams: false, MatchEpoch);
        }

        /// The match this client is in is over. NOTHING IS RESET HERE, and
        /// that is the point: the epoch has not changed, the interpolation
        /// buffer is still feeding the last seconds of the match onto the
        /// screen behind the end-of-match summary, and clearing the seams
        /// would throw exactly those frames away. If a next match comes, it
        /// comes as `MatchRestartedNet`, with its own epoch, and THAT is what
        /// resets.
        public LinkAction OnEnded(in MatchEndedNet ended)
        {
            if (Phase == LinkPhase.Refused) return Refuse(LinkVerdict.LinkRefused);
            if (Phase != LinkPhase.Joined && Phase != LinkPhase.MatchEnded)
                return Refuse(LinkVerdict.Unexpected);
            if (ended.MatchEpoch != MatchEpoch) return Refuse(LinkVerdict.ForeignEpoch);
            if (Phase == LinkPhase.MatchEnded) return Refuse(LinkVerdict.AlreadyEnded);

            EndedNet = ended;
            Phase = LinkPhase.MatchEnded;

            return new LinkAction(LinkVerdict.Applied, resetSeams: false, MatchEpoch);
        }

        /// A new match is starting under a new epoch. THE ONE MESSAGE BESIDE
        /// THE OPENING WELCOME THAT CARRIES THE RIGHT TO RESET — never a
        /// snapshot: `ClientMatchReset`'s own doc refuses that outright, and
        /// the seams it clears refuse a snapshot of an unknown epoch anyway,
        /// so a client switched over by a snapshot would be one that never
        /// switched at all.
        ///
        /// IT COVERS BOTH RESTART PATHS, matching `MatchServer.RestartMatch`:
        /// a match still running (the host cuts it short) and one that has
        /// already ended ("play again"). Hence `MatchEnded` is a legal
        /// starting phase here, not only `Joined`.
        ///
        /// DEDUPLICATING THE REPEAT IS THIS CLASS'S JOB, AND IT WAS HANDED
        /// HERE ON PURPOSE. `SnapshotQueue.Reset` and `ClientMatchReset` both
        /// state that a `Reset` is a FULL reset even at the same epoch and
        /// that telling a repeated life-cycle message from a deliberate
        /// same-epoch restart belongs to "the caller that receives it". This
        /// is that caller: a restart naming the epoch already tracked is
        /// refused as `DuplicateEpoch` rather than allowed to clear the ring
        /// of a match already in progress.
        ///
        /// THE LIMIT THAT COMES WITH THAT, NAMED RATHER THAN LEFT TO BE FOUND
        /// (fix round 1, M-2): A DELIBERATE RESTART ON THE EPOCH ALREADY
        /// TRACKED IS NOT SUPPORTED BY THIS CLIENT. It is indistinguishable
        /// from a repeat on this end and is refused as one, so a server that
        /// sent it would get a client holding every seam pointed at the
        /// finished match, silent for the whole next one. Nothing produces that
        /// message today — `MatchEpochCounter.Mint` advances the counter on
        /// every call and never returns the same number twice running, and
        /// `MatchServer.RestartMatch` takes the epoch from its caller rather
        /// than reusing its own — so the obligation sits with whoever mints an
        /// epoch for a restart, and it is written down here because here is
        /// where breaking it shows up. The test cannot be relaxed into an
        /// ordering to cover the case either: Р163 forbids ORDERING epochs
        /// because the counter wraps `65535 -> 1`, which is why this is
        /// equality and not `<=`.
        public LinkAction OnRestarted(in MatchRestartedNet restarted)
        {
            if (Phase == LinkPhase.Refused) return Refuse(LinkVerdict.LinkRefused);
            if (Phase != LinkPhase.Joined && Phase != LinkPhase.MatchEnded)
                return Refuse(LinkVerdict.Unexpected);
            if (restarted.MatchEpoch == 0) return Refuse(LinkVerdict.ReservedEpoch);
            if (restarted.MatchEpoch == MatchEpoch) return Refuse(LinkVerdict.DuplicateEpoch);

            MatchEpoch = restarted.MatchEpoch;
            Seed = restarted.Seed;
            Phase = LinkPhase.Joined;

            return new LinkAction(LinkVerdict.Applied, resetSeams: true, MatchEpoch);
        }

        /// A refusal carries the verdict and nothing else — never the right
        /// to reset, and never an epoch worth reading.
        static LinkAction Refuse(LinkVerdict verdict) => new LinkAction(verdict, resetSeams: false, 0);

        /// The wire byte to the ONE refusal vocabulary (`HandshakeRefusal`,
        /// HandshakeNet.cs). Written as an explicit switch rather than a cast
        /// or `Enum.IsDefined`: a raw byte off the wire cast straight to the
        /// enum produces a value with no member behind it, which then prints
        /// as a bare number in the one log line the player's report will
        /// contain. A code this build has no member for — a newer server, or
        /// corruption — becomes `UnrecognizedRejection`, the member
        /// `HandshakeRefusal`'s own doc introduces for exactly the "a byte it
        /// does not recognize" case; the raw byte is kept beside it.
        static HandshakeRefusal DecodeRefusal(byte reason)
        {
            switch (reason)
            {
                case (byte)HandshakeRefusal.ProtocolVersionMismatch:
                    return HandshakeRefusal.ProtocolVersionMismatch;
                case (byte)HandshakeRefusal.SimConfigMismatch:
                    return HandshakeRefusal.SimConfigMismatch;
                case (byte)HandshakeRefusal.UnknownPlayer:
                    return HandshakeRefusal.UnknownPlayer;
                case (byte)HandshakeRefusal.BadToken:
                    return HandshakeRefusal.BadToken;
                case (byte)HandshakeRefusal.DuplicatePlayer:
                    return HandshakeRefusal.DuplicatePlayer;
                case (byte)HandshakeRefusal.MatchFull:
                    return HandshakeRefusal.MatchFull;
                case (byte)HandshakeRefusal.MatchAlreadyStarted:
                    return HandshakeRefusal.MatchAlreadyStarted;
                case (byte)HandshakeRefusal.InvalidPlayerId:
                    return HandshakeRefusal.InvalidPlayerId;
                // `None` (0) falls through with every unknown code: a refusal
                // message that says "no refusal" is the same internal
                // contract violation `MatchHandshake.Refuse` already maps to
                // `UnrecognizedRejection` on the sending side.
                default:
                    return HandshakeRefusal.UnrecognizedRejection;
            }
        }
    }

    /// The FishNet wiring around `ClientLinkState` (Stage 2 Task 44b) — the
    /// client's half of the wire whose server halves are `MatchHandshake`
    /// (Task 39) and `MatchServer`'s life-cycle sends (Task 40). Registers
    /// the four broadcasts this process can receive, sends the one it sends,
    /// and performs the two side effects the core cannot: clearing the
    /// per-match seams and applying the dev latency simulator.
    ///
    /// NOTHING HERE DECIDES ANYTHING. Every branch below is a question to
    /// `ClientLinkState` (`TryBeginHello`, `LinkAction.ResetSeams`,
    /// `LinkAction.Verdict`) or the event filter FishNet itself hands in
    /// (`LocalConnectionState`). That is the same discipline `MatchHandshake`
    /// keeps against `HandshakeDecision`, and the reason `ClientLinkTests`
    /// can pin the whole protocol without ever raising a `NetworkManager`.
    ///
    /// THE HELLO WAITS FOR THE CONNECTION, AND THE WAIT IS NOT OPTIONAL.
    /// `ClientManager.Broadcast<T>` opens with `if (!Started) { LogWarning
    /// ("Cannot send broadcast to server because client is not active."); return; }`
    /// — a hello sent before the transport reports `LocalConnectionState.
    /// Started` is dropped on this side, never reaches the server, and the
    /// only visible consequence is this client sitting silent until the
    /// server's own `JoinTimeoutSeconds` expires. So the hello rides
    /// `ClientManager.OnClientConnectionState`, whose own summary is "called
    /// after the local client connection state changes", filtered to
    /// `LocalConnectionState.Started` ("connection is established").
    ///
    /// REGISTERS IN THE CONSTRUCTOR, AND THAT PROVES ONLY WHAT IT PROVES —
    /// the same limit `MatchHandshake`'s own doc records (fix-round 1, N-9):
    /// the handlers are subscribed before the constructor returns, and
    /// nothing in this class can say anything about when the transport first
    /// starts. The ordering obligation is the bootstrap's: construct this
    /// before starting the client connection. It also needs
    /// `networkManager.ClientManager` to already exist, i.e. after the
    /// `NetworkManager` has initialized.
    ///
    /// `Unregister()` MUST BE CALLED BEFORE THIS INSTANCE IS DISCARDED, for
    /// the mechanism `MatchHandshake.Unregister`'s doc spells out: FishNet
    /// stores handlers per delegate identity, so a second instance on the
    /// same `NetworkManager` leaves BOTH subscribed and the stale one answers
    /// with its own, now-wrong state.
    ///
    /// WHAT IS DELIBERATELY NOT HERE (Task 44c's, spec §3.9/§3.10): decoding
    /// snapshot frames, feeding `EventDedup`/`SnapshotQueue`/`RenderClock`,
    /// draining `ClientEventQueue`, `RenderSnapshot.LocalPlayerIndex`, and
    /// sending input. This class brings the match's identity — epoch, seed,
    /// slot — to a known-good state and keeps it there; who reads it is not
    /// its business.
    public sealed class ClientMatchLink
    {
        readonly NetworkManager _nm;
        readonly ClientMatchReset _reset;
        readonly ClientLinkState _state = new ClientLinkState();

        readonly byte _protocolVersion;
        readonly ulong _simConfigHash;
        readonly int _maxPlayers;
        readonly string _playerId;
        readonly string _joinToken;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        readonly NetConfig _netConfig;

        // Stage 2 Task 33 carry-forward, the CLIENT half: a dedicated sink
        // for DevLatencySetup.Apply's applied-facts output, mirroring
        // MatchServer's own `_devStats` and for the same reason — the
        // simulator is ONE instance for the whole transport, so what it
        // reports describes this PROCESS, not any connection.
        readonly NetStats _devStats = new NetStats();

        /// The applied dev latency-simulator facts for THIS process.
        public NetStats DevStats => _devStats;
#endif

        /// The decision core. Exposed read-only so Task 44c can read the
        /// match's identity (epoch, seed, slot) and the end-of-match summary
        /// without this class growing a forwarding property per field.
        public ClientLinkState State => _state;

        public ClientMatchLink(NetworkManager networkManager, ClientMatchReset reset, NetConfig netConfig,
            byte protocolVersion, ulong simConfigHash, int maxPlayers, string playerId, string joinToken)
        {
            _nm = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _reset = reset ?? throw new ArgumentNullException(nameof(reset));

            // Guarded in EVERY build even though the field it feeds only
            // exists in development ones: a bootstrap that forgot the config
            // is a wiring bug wherever it happens, and finding it only in the
            // build that has no simulator would be finding it in the wrong
            // one.
            if (netConfig == null) throw new ArgumentNullException(nameof(netConfig));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _netConfig = netConfig;
#endif

            _protocolVersion = protocolVersion;
            _simConfigHash = simConfigHash;
            _maxPlayers = maxPlayers;
            _playerId = playerId;
            _joinToken = joinToken;

            _nm.ClientManager.RegisterBroadcast<MatchWelcomeNet>(OnWelcome);
            _nm.ClientManager.RegisterBroadcast<MatchRefusedNet>(OnRefused);
            _nm.ClientManager.RegisterBroadcast<MatchEndedNet>(OnEnded);
            _nm.ClientManager.RegisterBroadcast<MatchRestartedNet>(OnRestarted);
            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        /// Drops every subscription this instance made. Idempotent, by the
        /// same delegate-identity mechanism `MatchHandshake.Unregister`
        /// documents.
        public void Unregister()
        {
            _nm.ClientManager.UnregisterBroadcast<MatchWelcomeNet>(OnWelcome);
            _nm.ClientManager.UnregisterBroadcast<MatchRefusedNet>(OnRefused);
            _nm.ClientManager.UnregisterBroadcast<MatchEndedNet>(OnEnded);
            _nm.ClientManager.UnregisterBroadcast<MatchRestartedNet>(OnRestarted);
            _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;
        }

        // The client-side broadcast handler shape is `(T, Channel)` — no
        // NetworkConnection, because a client has exactly one connection and
        // it is the server's (ClientManager.Broadcast.cs's own signature).
        /// The one handler with a line of its own, and the reason is
        /// diagnostic rather than decorative (Stage 2 Task 44d). `Apply` below
        /// prints the verdict, the phase and the tracked epoch, which is
        /// everything a `LinkAction` carries — and a `SlotOutOfRange` is
        /// exactly the refusal those three numbers cannot explain. The two
        /// readings are far apart — a corrupted byte on the wire, or a client
        /// and a server built against different `Arena.MaxPlayers` — and only
        /// the pair "seat, roster" tells them apart; both numbers are in scope
        /// here and nowhere downstream.
        ///
        /// THE REFUSAL IS NOT TERMINAL, AND THE FIRST VERSION OF THIS LINE
        /// SAID IT WAS (fix-round 1, G-5). `ClientLinkState.Refuse` builds a
        /// verdict and touches no field, so the phase stays at `HelloSent` —
        /// which is precisely the phase a welcome is ACCEPTED from. A next
        /// welcome naming a seat inside the roster is therefore applied
        /// normally, and a second welcome is a real possibility rather than a
        /// hypothetical (`ClientLinkState.OnWelcome`'s own doc names the stale
        /// `MatchHandshake` that sends one). So a damaged byte costs one
        /// message and recovers; it is SILENCE after this line, or a repeat
        /// naming the same seat, that points at the configurations. The line
        /// says which of the two the next message would settle instead of
        /// declaring the handshake over.
        void OnWelcome(MatchWelcomeNet welcome, Channel channel)
        {
            ClientLinkState.LinkAction action = _state.OnWelcome(in welcome, _maxPlayers);
            if (action.Verdict == ClientLinkState.LinkVerdict.SlotOutOfRange)
            {
                _nm.Log($"ClientMatchLink: the welcome named seat {welcome.PlayerIndex} in a roster "
                    + $"of {_maxPlayers}. Nothing was adopted and the link stays at HelloSent, so a "
                    + "later welcome naming a seat inside the roster is still accepted — a damaged "
                    + "byte costs this one message. If no welcome follows, or the next one names "
                    + "the same seat, the reading is the other one: this build and the server's "
                    + "disagree about Arena.MaxPlayers, and the two configurations are what to "
                    + "compare.");
            }

            Apply(action, nameof(MatchWelcomeNet));
        }

        void OnRefused(MatchRefusedNet refused, Channel channel)
            => Apply(_state.OnRefused(in refused), nameof(MatchRefusedNet));

        void OnEnded(MatchEndedNet ended, Channel channel)
            => Apply(_state.OnEnded(in ended), nameof(MatchEndedNet));

        void OnRestarted(MatchRestartedNet restarted, Channel channel)
            => Apply(_state.OnRestarted(in restarted), nameof(MatchRestartedNet));

        /// The one place a decision of the core becomes an effect. Two
        /// statements, no judgement: do what the action says, then say what
        /// happened if it was not `Applied`.
        void Apply(in ClientLinkState.LinkAction action, string message)
        {
            if (action.ResetSeams) _reset.ResetForEpoch(action.Epoch);

            if (action.Verdict != ClientLinkState.LinkVerdict.Applied)
            {
                _nm.Log($"ClientMatchLink: {message} not applied — {action.Verdict} "
                    + $"(link phase {_state.Phase}, tracked epoch {_state.MatchEpoch}). "
                    + "Refusing by value: a broadcast handler that threw would abandon every "
                    + "further message batched into the same datagram, which is never the right "
                    + "answer to a message that merely arrived at the wrong moment.");
            }
        }

        void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            // Everything except an established connection is somebody else's
            // business: Starting has no transport to send on yet, and
            // Stopping/Stopped is reconnect territory, deferred to Э5 (see
            // ClientLinkState's own doc).
            if (args.ConnectionState != LocalConnectionState.Started) return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Critical Rule 7 needs BOTH ends of the link simulating: the
            // simulator delays only the OUTGOING side of whichever process
            // calls this (DevLatencySetup's own doc), and the server already
            // calls it on its own TransportManager in MatchServer.StartMatch.
            // Without this half a playtest measures 40 ms one-way as if it
            // were the required 80 ms RTT, and 5% loss in one direction only.
            //
            // Stage 2 app-ck7: the numbers come from `-ring-latency` when the
            // launch line carries it (DevLatencyLaunch reads it once per
            // process; this client and the server each obey their own copy,
            // which is why an operator on a real link has to pass it twice).
            // Both lines below are load-bearing rather than decorative
            // (lesson 195): without the applied one, "the switch worked" and
            // "the switch was ignored" look the same on this console; without
            // the complaint, "the switch never arrived" and "the switch was
            // rejected" do. They go through UnityEngine.Debug rather than the
            // NetworkManager's logger every other line of this class uses —
            // the paragraph below the call has the reason, and it is not the
            // server's reason repeated.
            DevLatencyOptions latency = DevLatencyLaunch.Options;
            DevLatencySetup.Apply(_nm.TransportManager.LatencySimulator, _netConfig, _devStats,
                latency);
            // BOTH LINES GO THROUGH `UnityEngine.Debug`, NOT `_nm.Log`, AND THE
            // CLIENT NEEDS THAT AS MUCH AS THE SERVER DOES. FishNet caps its
            // own logging by a define, not by role: `LevelLoggingConfiguration.
            // InitializeOnce` reads `_headlessLogging` (default `Error`)
            // whenever `UNITY_SERVER` is defined, and `BuildCommands.Build`
            // leaves the EDITOR on the Dedicated Server platform after any
            // server build — so a Play-mode session that followed one would
            // drop both of these, the complaint included, and say nothing about
            // having dropped them. That is lesson 195 in a new place, and the
            // one honest fix is not to route a measurement's own audit trail
            // through a filter that answers to something else.
            if (latency.Complaint != null)
            {
                UnityEngine.Debug.LogWarning($"ClientMatchLink: {latency.Complaint} Running on "
                    + "NetConfig's numbers instead, so Critical Rule 7's simulator stays ON — a "
                    + "value nobody could read must never be able to stand it down.");
            }
            UnityEngine.Debug.Log("ClientMatchLink: dev latency simulator — "
                + DevLatencySetup.Describe(latency, _devStats)
                + ". This is the client's own OUTGOING half of the link; the server applies and "
                + "prints the other.");
#endif

            if (!_state.TryBeginHello())
            {
                _nm.Log("ClientMatchLink: the connection reported Started again and the hello has "
                    + "already gone out — not repeating it. The server answers a second hello with "
                    + "DuplicatePlayer and keeps the seat this client already holds.");
                return;
            }

            var hello = new ClientHelloNet
            {
                ProtocolVersion = _protocolVersion,
                SimConfigHash = _simConfigHash,
                PlayerId = _playerId,
                JoinToken = _joinToken,
            };

            // Reliable, like every message of the "Lifecycle" class in spec
            // §3.7's table — and unlike the snapshot, the default of this
            // overload is already Reliable; it is passed explicitly so the
            // channel is readable at the call site.
            _nm.ClientManager.Broadcast(hello, Channel.Reliable);
        }

        /// Asks the server to watch player slot `targetIndex` (Stage 2 Task
        /// 47b, spec §3.10 :673-678, Р70) — the client half of a message whose
        /// server half has been complete since Task 42a
        /// (`MatchServer.OnSpectateRequest`, `SpectatePolicy`) and which
        /// nothing in this process had ever sent.
        ///
        /// IT LIVES HERE BECAUSE THE ONE OTHER `ClientManager.Broadcast` IN THE
        /// PROJECT DOES (AGENT.md rule 2). This class already owns the client's
        /// only outgoing message, its `Unregister`, and the match identity a
        /// request is about; a second class talking to the transport would be a
        /// second place to keep the channel, the guard and the teardown right.
        ///
        /// IT DECIDES NOTHING, like every other method of this class — WHETHER
        /// to ask is the caller's, and whether to ACT on the asking is the
        /// server's alone (`SpectatePolicy.Evaluate`, five refusal reasons).
        /// Deliberately no phase test either: this class's own rule is that
        /// nothing here decides anything, and both guards that matter already
        /// exist elsewhere. `ClientManager.Broadcast<T>` opens with `if
        /// (!Started) { LogWarning(...); return; }`, so a request sent before
        /// the transport is up costs a warning line and no packet; and
        /// `MatchServer.OnSpectateRequest` returns on `!_running` and on a
        /// connection that holds no seat, so a request that arrives between
        /// matches is ignored rather than acted on.
        ///
        /// THERE IS NO REPLY TO WAIT FOR — see `SpectateRequestNet`'s own doc.
        /// An accepted switch is inferable only from the picture that follows
        /// it, and a refusal is indistinguishable from a switch whose effects
        /// have not arrived yet; the caller owns that wait.
        ///
        /// Reliable, like every message of the "Lifecycle" class in spec §3.7's
        /// table Р27, and for the sharper reason this one has: a dropped
        /// request is a keypress that did nothing, with nothing on the wire to
        /// tell the player which of the two happened.
        public void RequestSpectate(byte targetIndex)
        {
            _nm.ClientManager.Broadcast(new SpectateRequestNet { TargetIndex = targetIndex },
                Channel.Reliable);
        }
    }
}
