using System;
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

        /// TOTAL MAPPING — every `JoinRejection` byte value gets an answer,
        /// never a silent `None` (`None` would read as "accepted").
        /// `JoinRejection` itself lives in `Ring.Server`, which this
        /// assembly must not reference (brief §2.2's cycle ruling) — the
        /// caller (`MatchHandshake`'s join delegate, brief §2.2) hands the
        /// rejection in as the already-cast `byte` instead. Every named
        /// case below comments its own `JoinRejection` member for that
        /// reason: the literal itself cannot carry the enum's name across
        /// the assembly boundary.
        ///
        /// FIX-ROUND 1 (N-2/N-3): AN UNRECOGNIZED CODE NO LONGER THROWS.
        /// The original design threw `ArgumentOutOfRangeException` here,
        /// which — called from inside a FishNet broadcast handler — escapes
        /// through the package's own dispatch and into the transport's read
        /// loop; the connection that triggered it gets neither a refusal
        /// nor a disconnect, just silence, which is a worse failure than
        /// the one this method exists to prevent. An unrecognized code
        /// instead maps to the dedicated `HandshakeRefusal.
        /// UnrecognizedRejection` member — still loud
        /// (`MatchHandshake.Refuse` logs an error for it specifically) but
        /// never fatal to the handler. The mapping is still TOTAL in the
        /// sense that matters: a `JoinRejection` member added later without
        /// a matching case here lands on `UnrecognizedRejection`, not on
        /// the silent, wrong `None` — `HandshakeTests.
        /// FromJoinRejection_MapsEveryJoinRejectionValue`'s name-equality
        /// assert (fix-round 1, I-2) still catches the drift, just as a
        /// failed assertion instead of an uncaught exception.
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
                default: return HandshakeRefusal.UnrecognizedRejection;
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
    /// BEFORE `MatchServer.StartMatch` — which by its own class doc
    /// receives already-spawned connections/controllers and is a per-match
    /// tick loop, not a pre-match join point (`MatchServer`'s own doc names
    /// roster/join handling "entirely outside this task's scope").
    /// `MatchServer` also only receives `SimConfig` as a `StartMatch`
    /// parameter — later than the handshake needs it — so folding this in
    /// would mean carrying a second, independently-settable copy of the
    /// same config.
    ///
    /// `Ring.Server` IS NOT REFERENCED (brief §2.2, coordinator ruling).
    /// `Server.asmdef` already references `Ring.Networking` — a reference
    /// back from this assembly would close a cycle Unity cannot compile.
    /// Everything `MatchRoster`/`MatchConfig` would otherwise hand in
    /// arrives instead as plain values (`expectedProtocolVersion`/
    /// `expectedSimConfigHash`/`epoch`/`seed`) and delegates
    /// (`TryJoinDelegate`, `Func&lt;double&gt;`, `Action&lt;int,
    /// NetworkConnection&gt;`) — the bootstrap that constructs this class
    /// (Task 41) is the one place allowed to know about both assemblies.
    ///
    /// REGISTERS IN THE CONSTRUCTOR (brief §2.6). This guarantees only that
    /// the handler is subscribed before the constructor RETURNS — nothing
    /// in this class can guarantee anything about when a `NetworkConnection`
    /// first becomes able to reach the server at all (fix-round 1, N-9: an
    /// earlier draft of this doc overclaimed that). The actual requirement
    /// falls on Task 41's bootstrap ordering: construct this instance
    /// BEFORE opening the port to real connections, the same way it must
    /// already construct `MatchServer` before any `PlayerNetworkController`
    /// spawns (`MatchServer`'s own "WHAT Ф8 MUST HAND IN" doc).
    ///
    /// TIME ORIGIN IS INJECTED, NOT OWNED (fix-round 1, I-1). The
    /// constructor takes `Func&lt;double&gt; nowSeconds` rather than keeping
    /// a clock of its own. `MatchRoster.TryJoin`/`ShouldStart` (Task 38)
    /// both already commit to "no clock of its own: every method that
    /// needs 'now' takes it as a parameter" (`MatchRoster`'s own class
    /// doc) — an earlier draft of this class broke that discipline with a
    /// private `Stopwatch` started in ITS OWN constructor, independent of
    /// whatever clock Task 40/41's bootstrap uses to call `MatchRoster.
    /// ShouldStart` directly. Two independently-started stopwatches read
    /// different elapsed times for the same wall-clock instant, off by
    /// however long separates their two `StartNew()` calls — a countdown
    /// measured against one and checked against the other is wrong in
    /// EITHER direction (this instance's clock started EARLY relative to
    /// the other: the match looks older than it is and can start on the
    /// very first join if the gap alone exceeds `CountdownSeconds`; started
    /// LATE: every countdown is delayed by the gap, doubled again if that
    /// delay is itself computed lazily). A settable accessor was
    /// considered and rejected in favor of the delegate: an accessor would
    /// still leave TWO owners of "now" and only make one of them readable,
    /// where the delegate makes the origin STRUCTURALLY single — whoever
    /// owns the axis (Task 41's bootstrap, which is also the thing that
    /// calls `MatchRoster.ShouldStart`) is the only place a clock is ever
    /// started. Contract for Task 40/41: pass the SAME `Func&lt;double&gt;`
    /// (or an equivalent reading of the same underlying clock) here and to
    /// every `MatchRoster.ShouldStart`/`TryJoin` call.
    ///
    /// THE `onAccepted` CALLBACK CLOSES THE SLOT-TO-CONNECTION GAP
    /// (fix-round 1, I-3 — blocks Task 41 without it). `MatchServer.
    /// StartMatch` requires `connections[i]`/`controllers[i]` to be the
    /// SAME player by index; `OnClientHello` below is the only place that
    /// ever holds both the accepted `slot` and the `NetworkConnection`
    /// together in one stack frame, and previously discarded that pairing
    /// the moment the method returned. `onAccepted` is optional (`null` is
    /// a valid choice for anything that does not need the pairing, e.g. a
    /// throwaway harness) and is invoked with the RAW `int slot` — not the
    /// wire-narrowed `byte` — so Task 41 is never limited to what fits on
    /// the wire. Called BEFORE the welcome broadcast (see `OnClientHello`'s
    /// own comment on the call site for why): by the time `TryJoin`
    /// returns `true` the slot is already committed in the ROSTER
    /// (`MatchRoster` has no rollback, Task 38's own "one-way flag, no
    /// Reset" contract), so the record this callback lets Task 41 build
    /// must match what the roster decided, not whether the wire send that
    /// follows happens to succeed.
    ///
    /// THIS IS A DIAGNOSTIC, NOT AN ANTI-CHEAT CHECK (spec §3.10 :643-645,
    /// brief §2.5 — see `HandshakeRefusal`'s own doc in HandshakeNet.cs for
    /// the full statement). A modified client can send any
    /// `SimConfigHash`/`ProtocolVersion` it likes; this class only ever
    /// protects an HONEST client from silently mismatched balance data.
    ///
    /// EXPLICIT REFUSAL MESSAGE BEFORE THE DISCONNECT, ALWAYS (brief §2.3).
    /// FishNet's own `KickReason` (`ServerManager.Kick`) has no member for
    /// "balance mismatch" or "wrong protocol version" — its vocabulary is
    /// the package's own exploit/abuse categories, and reusing it would
    /// misreport an ordinary balance drift as an exploit attempt. So every
    /// refusal path here is `Broadcast(MatchRefusedNet)` THEN (except
    /// `DuplicatePlayer`, see below) `Disconnect(false)` — never a bare
    /// `Disconnect`/`Kick`.
    ///
    /// `DISCONNECT(FALSE)`, NOT `DISCONNECT(TRUE)` (brief §0a — verified
    /// against `NetworkConnection.Disconnect(bool immediately)`).
    /// `immediately: true` forces the transport connection closed on the
    /// spot, with no guarantee the `MatchRefusedNet` this method just
    /// queued has actually been flushed to the wire yet. `immediately:
    /// false` instead marks the connection dirty so the transport's normal
    /// outbound flush carries the queued refusal out before this
    /// connection is torn down — the method's own doc comment says this in
    /// as many words: "False to send any pending data first."
    /// `ServerManager.Kick`, by contrast, always disconnects immediately
    /// internally — which is exactly why this class does not use `Kick` at
    /// all.
    ///
    /// A SECOND `ClientHelloNet` FROM AN ALREADY-ACCEPTED CONNECTION IS
    /// REFUSED WITH `DuplicatePlayer`, AND THE CONNECTION IS KEPT ALIVE
    /// (fix-round 1, I-4 — RULING, reversing the original "refuse-and-
    /// disconnect-everything" doc). No special code is needed to detect the
    /// repeat: the join delegate is `MatchRoster.TryJoin` (Task 38), which
    /// already refuses a repeat `playerId` with `JoinRejection.
    /// DuplicatePlayer` (`MatchRoster`'s own "SLOTS ARE ASSIGNED IN
    /// ACCEPTANCE ORDER ... AND NEVER CHANGE ONCE GIVEN" doc). What
    /// changed is what `Refuse` does with that ONE specific reason:
    /// `DuplicatePlayer` is the only refusal an already-seated, LEGITIMATE
    /// connection can trigger (a client retry, a UI double-submit) —
    /// `MatchRoster` has no slot-revoke path (Task 38, "one-way flag, no
    /// Reset"), so disconnecting on this path would permanently burn a
    /// real player's seat over a harmless repeat message. Every OTHER
    /// refusal reason means this connection was never seated at all, so
    /// tearing it down is safe. An IMPOSTOR sending someone else's
    /// `playerId` a second time also lands on `DuplicatePlayer` and simply
    /// stays connected without ever entering `connections[]` or receiving
    /// a snapshot — strictly better than the alternative of kicking the
    /// real player it would otherwise be indistinguishable from at this
    /// layer. Re-issuing a NEW slot on a second hello (instead of refusing
    /// it) remains the bug brief §0 scenario 3 warns about and is not what
    /// happens here either way.
    ///
    /// A CONNECTION THAT DROPS BEFORE THE MATCH STARTS IS OUT OF SCOPE HERE
    /// (brief §2.6/§1's scope boundary) — `MatchRoster` has no slot-revoke
    /// path, so a slot handed out here stays claimed even if that
    /// connection later disconnects before `Start()`. Task 40 owns what a
    /// pre-start dropout means for the match; this class does not guess at
    /// it — including the now-related question of what happens to a
    /// `DuplicatePlayer`-refused-but-still-connected socket that never
    /// sends anything else (contract note for Task 40, not decided here).
    ///
    /// NO PER-REASON REFUSAL COUNTER (brief §2.6, AGENT.md rule 3 — no
    /// feature without a consumer). One log line per refusal is the
    /// observability this task ships; a counter has no reader yet.
    ///
    /// `Unregister()` MUST BE CALLED BEFORE THIS INSTANCE IS DISCARDED
    /// (fix-round 1, I-6 — see that method's own doc for the mechanism).
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
        readonly Func<double> _nowSeconds;
        readonly Action<int, NetworkConnection> _onAccepted;

        public MatchHandshake(NetworkManager networkManager, byte protocolVersion, ulong simConfigHash,
            ushort epoch, long seed, TryJoinDelegate tryJoin, Func<double> nowSeconds,
            Action<int, NetworkConnection> onAccepted = null)
        {
            _nm = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _tryJoin = tryJoin ?? throw new ArgumentNullException(nameof(tryJoin));
            _nowSeconds = nowSeconds ?? throw new ArgumentNullException(nameof(nowSeconds));
            _protocolVersion = protocolVersion;
            _simConfigHash = simConfigHash;
            _epoch = epoch;
            _seed = seed;
            _onAccepted = onAccepted;

            _nm.ServerManager.RegisterBroadcast<ClientHelloNet>(OnClientHello);
        }

        /// Unregisters this instance's handler (fix-round 1, I-6).
        /// FishNet's broadcast registration is ADDITIVE, not idempotent
        /// per-instance: `ServerManager.RegisterBroadcast&lt;T&gt;` appends
        /// to a handler list keyed only by `T`'s wire type, with no
        /// identity check against the delegate TARGET, so constructing a
        /// second `MatchHandshake` on the same `NetworkManager` without
        /// unregistering the first would leave BOTH `OnClientHello`
        /// methods subscribed — the next `ClientHelloNet` would run
        /// through both, and the stale instance would answer with its OWN
        /// (possibly now-wrong) epoch/roster, including sending a second
        /// `MatchWelcomeNet` carrying a stale `MatchEpoch`. `Unregister`
        /// must be called before any `MatchHandshake` is discarded (a
        /// restart, Task 40) — this class does not call it itself on any
        /// internal event, matching `MatchServer`'s own choice to never
        /// self-unsubscribe from `OnPostTick`.
        public void Unregister()
        {
            _nm.ServerManager.UnregisterBroadcast<ClientHelloNet>(OnClientHello);
        }

        void OnClientHello(NetworkConnection connection, ClientHelloNet hello, Channel channel)
        {
            HandshakeRefusal refusal = HandshakeDecision.Evaluate(in hello, _protocolVersion, _simConfigHash);

            if (refusal == HandshakeRefusal.None)
            {
                bool accepted = _tryJoin(hello.PlayerId, hello.JoinToken, _nowSeconds(),
                    out int slot, out byte rejectionCode);

                if (accepted)
                {
                    if (slot < 0 || slot > byte.MaxValue)
                    {
                        // Fix-round 1, N-1: MatchRoster caps `slot` at
                        // MatchConfig.MaxPlayers, not at byte.MaxValue —
                        // nothing structurally prevents a MatchConfig built
                        // outside MatchConfigLoader from carrying a
                        // MaxPlayers value the loader would have refused.
                        // PlayerIndex on the wire (HandshakeNet.cs) is a
                        // byte; a silent narrowing cast here would alias
                        // two different slots onto the same PlayerIndex.
                        // Refuse loudly instead of casting.
                        Refuse(connection, HandshakeRefusal.UnrecognizedRejection);
                        return;
                    }

                    // Fix-round 1, I-3: called BEFORE the welcome send —
                    // see the class doc's own paragraph on this callback
                    // for why the ordering matters (the slot is already
                    // committed in the roster by this point regardless of
                    // whether the send below succeeds).
                    _onAccepted?.Invoke(slot, connection);

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
                if (refusal == HandshakeRefusal.None)
                {
                    // Fix-round 1, N-2/N-3: the delegate returned `false`
                    // (a refusal) but supplied rejectionCode 0
                    // (JoinRejection.None, which legitimately means "no
                    // rejection") — a delegate contract violation. There
                    // is no honest reason to report here; Refuse's own
                    // UnrecognizedRejection branch logs the error.
                    refusal = HandshakeRefusal.UnrecognizedRejection;
                }
            }

            Refuse(connection, refusal);
        }

        void Refuse(NetworkConnection connection, HandshakeRefusal reason)
        {
            if (reason == HandshakeRefusal.UnrecognizedRejection)
            {
                _nm.LogError($"MatchHandshake: refusing connection {connection.ClientId} with "
                    + "UnrecognizedRejection — the join delegate or HandshakeDecision."
                    + "FromJoinRejection produced a code this class does not recognize; this "
                    + "is an internal contract violation, not a balance/version mismatch "
                    + "(fix-round 1, N-2/N-3).");
            }
            else
            {
                // Diagnostic wording only (brief §2.5) — never "exploit"/
                // "illegitimate"/"security": an unmodified client can
                // produce every one of these reasons just by running an
                // out-of-sync build.
                _nm.Log($"MatchHandshake: refusing connection {connection.ClientId} — {reason} "
                    + "(balance/version parity diagnostic, not an anti-cheat check; a modified "
                    + "client can report any hash or version).");
            }

            var refused = new MatchRefusedNet { Reason = (byte)reason };
            _nm.ServerManager.Broadcast(connection, refused, channel: Channel.Reliable);

            if (reason == HandshakeRefusal.DuplicatePlayer)
            {
                // Fix-round 1, I-4: the ONE reason an already-accepted,
                // legitimate connection can trigger — see the class doc's
                // own paragraph on this. Disconnecting here would
                // permanently burn a real player's seat (MatchRoster has
                // no slot-revoke path) over what may just be a harmless
                // retry.
                return;
            }

            // false: let the refusal broadcast above actually reach the
            // wire before the connection tears down (class doc's
            // "DISCONNECT(FALSE)" paragraph, brief §0a).
            connection.Disconnect(false);
        }
    }
}
