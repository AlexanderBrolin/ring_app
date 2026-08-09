using System;
using System.Diagnostics;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using Ring.Networking.Protocol;

namespace Ring.Networking.Server
{
    /// Pure decision core for the connection handshake (Stage 2 Task 39,
    /// spec §3.10 :642-651; brief §2.4). No FishNet types anywhere in this
    /// class — `MatchHandshake` below is the only thing that touches the
    /// network — same split as `PlayerNetworkController`/
    /// `PlayerPredictionCore` and `MatchServer`/`InputStarvation` (brief
    /// §2.4's own precedent list).
    public static class HandshakeDecision
    {
        /// Version and balance checks ONLY — roster/token decisions belong
        /// to `MatchRoster` (Task 38) and arrive through the join delegate
        /// (brief §2.2), not through this method.
        ///
        /// ORDER IS FIXED: `ProtocolVersionMismatch` before
        /// `SimConfigMismatch`. When the protocol itself has drifted, the
        /// rest of `hello`'s fields may not have been written/read with the
        /// same layout the two sides agree on, so comparing the hash at
        /// that point is not a meaningful check (brief §2.4).
        public static HandshakeRefusal Evaluate(in ClientHelloNet hello,
            byte expectedProtocolVersion, ulong expectedSimConfigHash)
        {
            if (hello.ProtocolVersion != expectedProtocolVersion)
                return HandshakeRefusal.ProtocolVersionMismatch;
            if (hello.SimConfigHash != expectedSimConfigHash)
                return HandshakeRefusal.SimConfigMismatch;
            return HandshakeRefusal.None;
        }

        /// TOTAL MAPPING — a `JoinRejection` with no case here is a bug, not
        /// a pass (brief §2.4/§2.7 test 5). `JoinRejection` itself lives in
        /// `Ring.Server`, which this assembly must not reference (brief
        /// §2.2's cycle ruling) — the caller (`MatchHandshake`'s join
        /// delegate, brief §2.2) hands the rejection in as the already-cast
        /// `byte` instead. Every case below names its own
        /// `JoinRejection` member in a comment for that reason: the literal
        /// itself cannot carry the enum's name across the assembly
        /// boundary.
        ///
        /// An unrecognized code throws rather than falling back to `None` —
        /// `None` would mean "accepted", and silently accepting an
        /// unrecognized rejection is exactly the failure mode brief §0
        /// scenario 2 warns about. This is also what makes a FUTURE
        /// `JoinRejection` member added without a matching case here a loud
        /// failure the next time this runs, instead of a silent pass-through
        /// (brief §2.7 test 5).
        public static HandshakeRefusal FromJoinRejection(byte joinRejection)
        {
            switch (joinRejection)
            {
                case 0: return HandshakeRefusal.None;                    // JoinRejection.None
                case 1: return HandshakeRefusal.MatchAlreadyStarted;     // JoinRejection.MatchAlreadyStarted
                case 2: return HandshakeRefusal.InvalidPlayerId;         // JoinRejection.InvalidPlayerId
                case 3: return HandshakeRefusal.UnknownPlayer;           // JoinRejection.UnknownPlayer
                case 4: return HandshakeRefusal.BadToken;                // JoinRejection.BadToken
                case 5: return HandshakeRefusal.DuplicatePlayer;         // JoinRejection.DuplicatePlayer
                case 6: return HandshakeRefusal.MatchFull;               // JoinRejection.MatchFull
                default:
                    throw new ArgumentOutOfRangeException(nameof(joinRejection), joinRejection,
                        "HandshakeDecision.FromJoinRejection: unrecognized JoinRejection code — "
                        + "the mapping is incomplete, not the caller's fault.");
            }
        }
    }

    /// The FishNet wiring around `HandshakeDecision` (Stage 2 Task 39, spec
    /// §3.10 :642-651; brief §2.1/§2.6). Registers the one
    /// `ClientHelloNet` handler this process has and answers it with
    /// `MatchWelcomeNet` or `MatchRefusedNet` — nothing here decides
    /// anything; every decision is `HandshakeDecision`'s or the join
    /// delegate's.
    ///
    /// LIVES IN ITS OWN CLASS, NOT INSIDE `MatchServer` (brief §2.1,
    /// coordinator ruling, AGENT.md rule 2 — reuse over duplication, no
    /// second home for match lifecycle). The handshake decides who plays
    /// BEFORE `MatchServer.StartMatch` — which by its own class doc receives
    /// already-spawned connections/controllers (MatchServer.cs:129-146) and
    /// is a per-match tick loop, not a pre-match join point
    /// (MatchServer.cs's own doc names roster/join handling "entirely
    /// outside this task's scope"). `MatchServer` also only receives
    /// `SimConfig` as a `StartMatch` parameter — later than the handshake
    /// needs it — so folding this in would mean carrying a second,
    /// independently-settable copy of the same config.
    ///
    /// `Ring.Server` IS NOT REFERENCED (brief §2.2, coordinator ruling).
    /// `Server.asmdef` already references `Ring.Networking`
    /// (client/Assets/Scripts/Server/Server.asmdef) — a reference back from
    /// this assembly would close a cycle Unity cannot compile. Everything
    /// `MatchRoster`/`MatchConfig` would otherwise hand in arrives instead
    /// as plain values (`expectedProtocolVersion`/`expectedSimConfigHash`/
    /// `epoch`/`seed`) and one delegate (`TryJoinDelegate`) — the bootstrap
    /// that constructs this class (Task 41) is the one place allowed to
    /// know about both assemblies.
    ///
    /// REGISTERS IN THE CONSTRUCTOR (brief §2.6) — same precedent as
    /// `MatchServer`'s own `OnPostTick` subscription
    /// (MatchServer.cs's "SUBSCRIPTION TIMING" doc paragraph): registering
    /// here guarantees the handler exists before Task 41's bootstrap can
    /// possibly let a `NetworkConnection` reach the server at all.
    ///
    /// THIS IS A DIAGNOSTIC, NOT AN ANTI-CHEAT CHECK (spec §3.10 :643-645,
    /// brief §2.5 — see HandshakeRefusal's own doc in HandshakeNet.cs for
    /// the full statement). A modified client can send any
    /// `SimConfigHash`/`ProtocolVersion` it likes; this class only ever
    /// protects an HONEST client from silently mismatched balance data.
    ///
    /// EXPLICIT REFUSAL MESSAGE BEFORE THE DISCONNECT, ALWAYS (brief §2.3).
    /// FishNet's own `KickReason` (ServerManager.QOL.cs:181) has no member
    /// for "balance mismatch" or "wrong protocol version" — its vocabulary
    /// is the package's own exploit/abuse categories, and reusing it would
    /// misreport an ordinary balance drift as an exploit attempt. So every
    /// refusal path here is `Broadcast(MatchRefusedNet)` THEN
    /// `Disconnect(false)` — never a bare `Disconnect`/`Kick`.
    ///
    /// `DISCONNECT(FALSE)`, NOT `DISCONNECT(TRUE)` (brief §0a — verified
    /// against `NetworkConnection.cs:321-338`). `immediately: true` forces
    /// the transport connection closed on the spot
    /// (`Transport.StopConnection(ClientId, true)`), with no guarantee the
    /// `MatchRefusedNet` this method just queued has actually been flushed
    /// to the wire yet. `immediately: false` instead calls `ServerDirty()` —
    /// the SAME "there is data to send" signal `NetworkConnection.Buffer.cs`
    /// raises after every `SendToClient` write — which lets the transport's
    /// normal outbound flush carry the queued refusal out before this
    /// connection is torn down. FishNet's own doc comment on `Disconnect`
    /// says this in as many words: "False to send any pending data first."
    /// `ServerManager.Kick` (`ServerManager.QOL.cs:181`), by contrast,
    /// always calls `conn.Disconnect(true)` internally — which is exactly
    /// why this class does not use `Kick` at all.
    ///
    /// A SECOND `ClientHelloNet` FROM AN ALREADY-ACCEPTED CONNECTION IS
    /// REFUSED, NOT RE-ANSWERED (brief §2.6). This needs no special code
    /// here: the join delegate is `MatchRoster.TryJoin` (Task 38), which
    /// already refuses a repeat `playerId` with `JoinRejection.
    /// DuplicatePlayer` (MatchRoster.cs's own "SLOTS ARE ASSIGNED IN
    /// ACCEPTANCE ORDER ... AND NEVER CHANGE ONCE GIVEN" doc) — re-running
    /// the SAME evaluate-then-join pipeline on a second hello from the same
    /// connection produces the correct answer for free. Re-issuing a NEW
    /// slot on a second hello is exactly the bug brief §0 scenario 3 warns
    /// about (`connections[i]`/`controllers[i]` disagreeing about which
    /// index is which player), so this path is deliberately NOT special-
    /// cased into "just resend the same welcome".
    ///
    /// A CONNECTION THAT DROPS BEFORE THE MATCH STARTS IS OUT OF SCOPE HERE
    /// (brief §2.6/§1's scope boundary) — `MatchRoster` has no slot-revoke
    /// path (Task 38's own "one-way flag, no Reset" contract), so a slot
    /// handed out here stays claimed even if that connection later
    /// disconnects before `Start()`. Task 40 owns what a pre-start dropout
    /// means for the match; this class does not guess at it.
    ///
    /// NO PER-REASON REFUSAL COUNTER (brief §2.6, AGENT.md rule 3 — no
    /// feature without a consumer). One log line per refusal is the
    /// observability this task ships; a counter has no reader yet.
    public sealed class MatchHandshake
    {
        /// Everything `MatchRoster.TryJoin` (Task 38) needs, minus the
        /// `Ring.Server` type name itself (brief §2.2) — `rejectionCode` is
        /// the `byte` form of `JoinRejection`, mapped back to
        /// `HandshakeRefusal` by `HandshakeDecision.FromJoinRejection`.
        public delegate bool TryJoinDelegate(string playerId, string joinToken, double nowSeconds,
            out int slot, out byte rejectionCode);

        readonly NetworkManager _nm;
        readonly byte _protocolVersion;
        readonly ulong _simConfigHash;
        readonly ushort _epoch;
        readonly long _seed;
        readonly TryJoinDelegate _tryJoin;

        // Same idiom as MatchServer's own _stopwatch (MatchServer.cs:182):
        // MatchRoster.TryJoin/ShouldStart take "now" as a caller-supplied
        // parameter rather than reading a clock themselves (MatchRoster's
        // own class doc), and this is the caller. Started at construction —
        // before the FIRST ClientHelloNet this instance could possibly
        // receive, since registration also happens in the constructor.
        readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public MatchHandshake(NetworkManager networkManager, byte protocolVersion, ulong simConfigHash,
            ushort epoch, long seed, TryJoinDelegate tryJoin)
        {
            _nm = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _tryJoin = tryJoin ?? throw new ArgumentNullException(nameof(tryJoin));
            _protocolVersion = protocolVersion;
            _simConfigHash = simConfigHash;
            _epoch = epoch;
            _seed = seed;

            _nm.ServerManager.RegisterBroadcast<ClientHelloNet>(OnClientHello);
        }

        void OnClientHello(NetworkConnection connection, ClientHelloNet hello, Channel channel)
        {
            HandshakeRefusal refusal = HandshakeDecision.Evaluate(in hello, _protocolVersion, _simConfigHash);

            if (refusal == HandshakeRefusal.None)
            {
                bool accepted = _tryJoin(hello.PlayerId, hello.JoinToken, _stopwatch.Elapsed.TotalSeconds,
                    out int slot, out byte rejectionCode);

                if (accepted)
                {
                    var welcome = new MatchWelcomeNet
                    {
                        MatchEpoch = _epoch,
                        Seed = _seed,
                        PlayerIndex = (byte)slot,
                    };
                    _nm.ServerManager.Broadcast(connection, welcome, channel: Channel.Reliable);
                    return;
                }

                refusal = HandshakeDecision.FromJoinRejection(rejectionCode);
            }

            Refuse(connection, refusal);
        }

        void Refuse(NetworkConnection connection, HandshakeRefusal reason)
        {
            // Diagnostic wording only (brief §2.5) — never "exploit"/
            // "illegitimate"/"security": an unmodified client can produce
            // every one of these reasons just by running an out-of-sync
            // build.
            _nm.Log($"MatchHandshake: refusing connection {connection.ClientId} — {reason} "
                + "(balance/version parity diagnostic, not an anti-cheat check; a modified "
                + "client can report any hash or version).");

            var refused = new MatchRefusedNet { Reason = (byte)reason };
            _nm.ServerManager.Broadcast(connection, refused, channel: Channel.Reliable);
            // false: let the refusal above actually reach the wire before
            // the connection tears down (class doc's "DISCONNECT(FALSE)"
            // paragraph, brief §0a).
            connection.Disconnect(false);
        }
    }
}
