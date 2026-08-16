using System;
using System.Globalization;
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
        /// called from inside a FishNet broadcast handler
        /// (`ServerManager.ParseReceived`'s dispatch to `InvokeHandlers`).
        /// TWO DIFFERENT BUILD CONFIGURATIONS SEND THAT EXCEPTION TWO
        /// DIFFERENT WAYS, NEITHER ACCEPTABLE (fix-round 2, NF-1 — the
        /// original one-branch "just silence" justification was verified
        /// wrong for the build that matters most and is corrected here):
        /// - In a DEVELOPMENT build (Unity Editor or `BuildOptions.
        ///   Development`) `ParseReceived`'s parsing loop has NO try/catch
        ///   around it at all — the exception escapes uncaught, and this
        ///   handshake's own code never gets a chance to send
        ///   `MatchRefusedNet` or call `Disconnect`. That is the "silence"
        ///   this doc originally described.
        /// - In a PRODUCTION build — specifically `BuildCommands.
        ///   BuildLinuxServer`, which never sets `BuildOptions.Development`
        ///   (verified by grep: `BuildOptions` does not appear in
        ///   `BuildCommands.cs` at all) — `ParseReceived` wraps the same
        ///   loop in a `try/catch` that is compiled in exactly when
        ///   `DEVELOPMENT` is NOT defined, and ANY exception, including
        ///   this one, is caught and turned into an IMMEDIATE
        ///   `Kick(..., KickReason.MalformedData, ...)`. That is NOT
        ///   silence — it is a prompt, deterministic disconnect — but it is
        ///   still the wrong answer: `KickReason.MalformedData` blames the
        ///   CLIENT for corrupt data over a bug entirely on the SERVER
        ///   side, in the one build configuration the arena actually ships.
        /// Neither branch is silence for every build, and neither is
        /// acceptable on its own terms — one hangs the connection with no
        /// diagnosis, the other misattributes a server bug to the client.
        /// An unrecognized code instead maps to the dedicated
        /// `HandshakeRefusal.UnrecognizedRejection` member — still loud in
        /// EVERY build (`MatchHandshake.Refuse` logs an error for it
        /// specifically, from code this class actually controls) but never
        /// fatal to the handler and never mislabeled as the client's fault.
        /// The mapping is still TOTAL in the sense that matters: a
        /// `JoinRejection` member added later without a matching case here
        /// lands on `UnrecognizedRejection`, not on the silent, wrong
        /// `None` — `HandshakeTests.
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

        /// Precondition for the `(byte)slot` narrowing cast in
        /// `MatchHandshake.OnClientHello` — checked ONCE, at `MatchHandshake`
        /// construction (fix-round 2, K-1/N-1 ruling), replacing fix-round
        /// 1's per-connection runtime guard entirely.
        ///
        /// `MatchRoster.TryJoin` (Task 38) assigns slots DENSELY over
        /// `[0, maxPlayers)`: `slot = _connectedCount` read BEFORE the
        /// increment, and no more joins are accepted once `_connectedCount
        /// >= MaxPlayers` (`JoinRejection.MatchFull` — `MatchRoster`'s own
        /// "SLOTS ARE ASSIGNED IN ACCEPTANCE ORDER ... AND NEVER CHANGE
        /// ONCE GIVEN" doc). The highest slot `TryJoin` can EVER produce is
        /// therefore exactly `maxPlayers - 1` — so if THAT one value fits
        /// in a `byte`, every slot fits, by construction; there is nothing
        /// left for a per-join check to catch. `MatchWelcomeNet.
        /// PlayerIndex` (HandshakeNet.cs) is a `byte`, hence the `<= 256`
        /// bound (`maxPlayers = 256` -> highest slot `255` -> `byte.
        /// MaxValue`, the last value that still fits).
        ///
        /// `maxPlayers &lt; 1` is refused too — a match with zero or fewer
        /// seats is not a match `MatchHandshake` should ever be constructed
        /// for; `MatchRoster`'s own constructor already refuses the same
        /// range for the same reason (`MatchConfig.MaxPlayers < 1` throws)
        /// — this function cannot see that guard across the assembly
        /// boundary (brief §2.2), so it re-states the same bound
        /// independently rather than trusting a caller who might not go
        /// through `MatchRoster` at all.
        public static bool SlotsFitOnTheWire(int maxPlayers) => maxPlayers >= 1 && maxPlayers <= 256;
    }

    /// THE REFUSAL LOG LINE, FORMATTED (Stage 2 app-uxx + app-aor,
    /// task-uxx-red-brief.md §3.1/§3.2) — the pure half of what
    /// `MatchHandshake.Refuse` prints. This class BUILDS the sentence; the
    /// caller is the only thing that logs it.
    ///
    /// NO FISHNET TYPES HERE EITHER — `HandshakeDecision`'s rule above, plus
    /// a stronger one this class needs on its own: EVERY METHOD IS A FUNCTION
    /// OF ITS ARGUMENTS AND NOTHING ELSE. `playerId`, `clientId`, `reason`
    /// and `atSeconds` all arrive as parameters; nothing is read from a
    /// `NetworkConnection`, from a clock or from the environment, and nothing
    /// is written to a logger. `DevLatencyOptions.Parse` states the same rule
    /// for the same reason ("passed in rather than fetched, so this is a
    /// function of its argument and nothing else"): a formatter that fetched
    /// its own inputs could not be covered by an EditMode test at all, and one
    /// that logged would drag `UnityEngine` into a file that has never
    /// imported it.
    ///
    /// WHY THE LINE WAS REBUILT AT ALL. The refusal this one replaced named
    /// only `connection.ClientId` — a number the transport hands out afresh on
    /// every run — so a container log gave no way to tell WHICH configured
    /// player was turned away. That is precisely the question a refused join
    /// raises: a client launched without `-ring-player-id` generates its own
    /// `dev-XXXXXXXX` and is then refused by a named roster, and the line as it
    /// stood could not say so. The accepted side already answers the same
    /// question — `ServerBootstrap`'s "player accepted slot=... playerId=...
    /// clientId=... connected=.../... atSeconds=..." — and this line is
    /// deliberately its mirror rather than a second style.
    ///
    /// THE FORMAT:
    ///
    ///     MatchHandshake: player refused playerId={0} clientId={1} reason={2} atSeconds={3:F3} (tail)
    ///
    /// - `player refused` MIRRORS `player accepted` so that both halves of the
    ///   join decision come out of ONE `grep -E "player accepted|player
    ///   refused"`. The operator who asks "did anybody get in?" and the one who
    ///   asks "why did nobody get in?" are the same operator minutes apart, and
    ///   two unrelated spellings would make that two searches.
    /// - `MatchHandshake:` AND NOT `HandshakeLog:` — the prefix names the
    ///   subsystem an operator can look up, which is the class that decides and
    ///   prints, not the one that formats a string for it. It is also what the
    ///   line already says today, so no existing filter is invalidated.
    /// - `key=value`, space separated, `CultureInfo.InvariantCulture`, and
    ///   `atSeconds` at `F3` — every one of those is the accepted line's
    ///   choice, restated rather than reinvented. The culture is not a
    ///   formality: on a comma-decimal machine (the owner's own workstation)
    ///   the default format writes `12,500`, which reads as a second field to
    ///   anything parsing the line and as a different number to a human.
    /// - `reason` IS PRINTED BY NAME, NOT BY NUMBER. The byte is what rides the
    ///   wire (`MatchRefusedNet.Reason`); a log read by a person and by the
    ///   future control panel (app-7ss) should not require a second lookup
    ///   table to say `SimConfigMismatch`.
    /// - `atSeconds` IS THE SERVER'S OWN ELAPSED AXIS, NOT A WALL CLOCK, and it
    ///   is here to pay a known price. It is whatever the injected
    ///   `Func&lt;double&gt;` answers — in production `ServerBootstrap`'s clock,
    ///   whose zero is that component's `Start` and NOT process spawn
    ///   (`ServerBootstrap.cs`'s "ONE CLOCK, HANDED OUT AS A DELEGATE"
    ///   paragraph says so in as many words: engine startup is deliberately
    ///   outside the join window). Naming it "since process start" here would
    ///   contradict that file. Which axis it is remains this class's caller's
    ///   business either way — see "TIME ORIGIN IS INJECTED" below.
    ///   The refusal no longer goes out through
    ///   FishNet's `NetworkManager.Log` — whose `LevelLoggingConfiguration.
    ///   AddSettingsToLog` prepends `[yyyy.MM.dd HH:mm:ss]` outside the editor
    ///   — but through `UnityEngine.Debug.*`, which prepends nothing. The same
    ///   axis the accepted line already prints is the compensation, and it has
    ///   the advantage of being directly comparable with it: two lines from one
    ///   run can be subtracted, which two wall-clock stamps at one-second
    ///   resolution cannot.
    ///
    /// THE TAIL IS CHOSEN BY REASON, AND BOTH TAILS ARE KEPT (brief §3.1). The
    /// balance/version wording below went through an earlier phase-gate review and
    /// deliberately avoids "exploit"/"illegitimate"/"security": every reason it
    /// covers is one an HONEST client of an out-of-sync build produces by
    /// itself, and `HandshakeRefusal`'s own doc in HandshakeNet.cs states at
    /// length that this whole handshake is a parity diagnostic and not an
    /// anti-cheat check. `UnrecognizedRejection` gets the OTHER tail because
    /// for that one member the first sentence would be false — it is an
    /// internal contract violation between the join delegate and this
    /// assembly, a server-side bug, with no balance or version drift involved
    /// at all.
    ///
    /// SEVERITY IS NOT DECIDED HERE. `UnrecognizedRejection` goes out as an
    /// error and every other reason as a warning; that is a choice about which
    /// console channel to use, which only the caller — the one holding the
    /// logger — can act on. This class hands back the same kind of value in
    /// both cases: a string. `MatchHandshake.Refuse` splits on the SAME member
    /// this class splits the tail on, deliberately: one condition decides both
    /// halves of "how loud and in whose words", so the two can never come
    /// apart into an internal-bug sentence printed at a routine level.
    ///
    /// ONE MORE FIELD IS DELIBERATELY ABSENT: `JoinToken`. It is a secret the
    /// client sends (`ClientHelloNet.JoinToken`), and a log line is exactly
    /// where a secret must not appear. That is also why the sanitizer below is
    /// named for `playerId` specifically rather than as a general
    /// "SanitizeForLog": a general name is an invitation to hand it the token
    /// next.
    public static class HandshakeLog
    {
        /// The longest `playerId` that reaches the log unabridged. ONE NUMBER,
        /// ONE HOME (project rule 2): no caller passes a limit and no second
        /// constant restates it, so raising the bound is a one-line change with
        /// nothing left behind to disagree with it.
        ///
        /// 64 because it is comfortably above every id this project actually
        /// produces — `dev-XXXXXXXX` is twelve characters, and the ids in a
        /// match config are written by a human — while still being far below
        /// the length at which one refused join can push everything else out of
        /// a scrollback. THIS IS NOT A VALIDATION BOUND (brief §4): nothing
        /// anywhere limits how long a `playerId` may be, `MatchRoster` and
        /// `MatchConfigLoader` check only that it is non-empty, and this
        /// constant must never grow into a reason to refuse a join. It governs
        /// one thing: how much of an untrusted string is copied into a log
        /// line.
        public const int MaxPlayerIdLength = 64;

        /// Appended when — and only when — something was cut, so that a
        /// truncated id is never mistaken for a real one. Without it an
        /// operator reading a 64-character id has no way to know whether that
        /// was the whole id, which turns a diagnostic line into a misleading
        /// one. No spaces in it, like everything else that lands inside a
        /// `key=value` field.
        public const string TruncationMarker = "...";

        /// What every `char.IsControl` character becomes. A SUBSTITUTION, NOT A
        /// DELETION: dropping the character would render `a\nb` as `ab`, which
        /// is a perfectly ordinary id and says nothing about what was really
        /// received, while the placeholder keeps the length and shows the shape
        /// of what arrived. A single ASCII character rather than an escape
        /// sequence (`\n`), so the substitution is exactly length-preserving
        /// and truncation and neutralization cannot interact.
        public const char ControlCharPlaceholder = '?';

        /// `playerId` was `null` on the wire — the client never wrote the field.
        public const string NullPlayerIdMarker = "[null]";

        /// `playerId` was present and empty — the client wrote "" on purpose.
        ///
        /// TWO MARKERS, NOT ONE, and the reason is that nothing else can tell
        /// them apart: `MatchRoster.TryJoin` answers `string.IsNullOrEmpty`
        /// with a single `JoinRejection.InvalidPlayerId`, so the refusal reason
        /// this line prints is identical in both cases and the log line is the
        /// ONLY place the difference can survive. It is a real difference to
        /// whoever debugs the client: a `null` means the field was never
        /// assigned, an empty string means it was assigned something empty.
        ///
        /// Square brackets rather than angle brackets because the Unity console
        /// treats `&lt;...&gt;` as rich-text markup. A client that sends the
        /// literal text `[null]` renders identically — an ambiguity this class
        /// accepts rather than escapes, on the same grounds the whole handshake
        /// is documented with: it is a diagnostic, not an anti-cheat check, and
        /// that client is refused either way.
        public const string EmptyPlayerIdMarker = "[empty]";

        /// The tail every reason but `UnrecognizedRejection` carries, kept
        /// verbatim from the text `MatchHandshake.Refuse` used to format in
        /// place. The wording went through an earlier phase-gate review and is
        /// not restated in this class's own words on purpose: it names the check
        /// for what it is and deliberately avoids "exploit"/"illegitimate"/
        /// "security", because an HONEST client of an out-of-sync build produces
        /// every reason it covers by itself.
        ///
        /// THIS CONSTANT IS THE SENTENCE'S ONLY HOME (project rule 2). `Refuse`
        /// now prints what `RefusalLine` builds and formats nothing of its own,
        /// so there is no second copy of this wording anywhere to drift from it
        /// — which also means the tests that pin the fragment pin what the
        /// server actually says, not a parallel string that merely resembles it.
        const string ParityTail = "(balance/version parity diagnostic, not an anti-cheat check; "
            + "a modified client can report any hash or version).";

        /// The tail `UnrecognizedRejection` carries INSTEAD, because for that one
        /// member the sentence above would be false: no version or balance drift
        /// is involved at all. The join delegate — or `HandshakeDecision.
        /// FromJoinRejection` — produced a code this assembly does not recognize,
        /// which is a bug on the server's own side of the contract and says
        /// nothing about the client that happened to be turned away by it.
        /// Same substance as the error branch `MatchHandshake.Refuse` used to
        /// format in place, minus the reason's name: `reason=` already carries
        /// that. The BRANCH still exists in the caller — it is what picks the
        /// error channel over the warning one — but the WORDING lives here and
        /// nowhere else.
        const string InternalBugTail = "(the join delegate or HandshakeDecision.FromJoinRejection "
            + "produced a code the handshake does not recognize; an internal contract violation on "
            + "the server side, not a balance/version mismatch).";

        /// The whole line, ready to print — see the class doc for the format
        /// and for why each field is in it.
        ///
        /// `playerId` IS THE ONLY UNTRUSTED ARGUMENT and is passed through
        /// `SanitizePlayerId` before it reaches the line; `clientId` is the
        /// transport's own number, `reason` is an enum member and `atSeconds`
        /// comes from the process's own clock.
        ///
        /// `CultureInfo.InvariantCulture` IS PASSED, NOT ASSUMED — the same call
        /// shape `ServerBootstrap` uses for the accepted line, and for the same
        /// reason: string interpolation would pick up `CurrentCulture` and write
        /// `atSeconds=12,500` on the owner's own comma-decimal workstation.
        /// `reason` is formatted by the same call, which prints an enum member as
        /// its NAME.
        public static string RefusalLine(string playerId, int clientId, HandshakeRefusal reason,
            double atSeconds)
        {
            string tail = reason == HandshakeRefusal.UnrecognizedRejection
                ? InternalBugTail
                : ParityTail;

            return string.Format(CultureInfo.InvariantCulture,
                "MatchHandshake: player refused playerId={0} clientId={1} reason={2} "
                + "atSeconds={3:F3} {4}",
                SanitizePlayerId(playerId), clientId, reason, atSeconds, tail);
        }

        /// An arbitrary string from the wire, made safe to put inside one
        /// `key=value` field of one log line. Three things happen, in this
        /// order:
        ///
        /// 1. `null` and `""` become their markers above, so an absent id is
        ///    visibly absent instead of being an empty gap that reads as a
        ///    formatting bug.
        /// 2. Every `char.IsControl` character becomes
        ///    `ControlCharPlaceholder`. THE POINT IS `\n` AND `\r`: without
        ///    this, one string chosen by whoever is connecting turns one log
        ///    line into two, and everything that reads the log a line at a time
        ///    — `grep`, a human, the future control panel (app-7ss) — is handed
        ///    a fabricated record. `\0` and the rest of the C0/C1 range come
        ///    along because they are the same class of input and cost the same
        ///    predicate.
        /// 3. Anything longer than `MaxPlayerIdLength` is cut to it and given
        ///    `TruncationMarker`.
        ///
        /// A CUT NEVER SPLITS A SURROGATE PAIR: if the character at the bound is
        /// the leading half of a pair, it is dropped with its partner rather
        /// than left behind alone. A lone surrogate is not a control character
        /// and would survive step 2 untouched, then reach the log encoder as an
        /// unpaired UTF-16 unit that no encoder can represent — a defect
        /// manufactured by the truncation itself, which is why it is the
        /// truncation's job to avoid it.
        ///
        /// WHAT THIS DOES NOT DO, NAMED SO IT IS NOT MISTAKEN FOR AN OVERSIGHT:
        /// it does not neutralize the SPACE or the `=` character, so an id
        /// containing either still reads as more than one `key=value` pair to
        /// anything parsing the line. That is a different defect from the one
        /// above — it forges a field inside one line rather than forging a
        /// whole line — and closing it would mean altering ids that are
        /// perfectly legitimate (nothing forbids a space in a configured
        /// `playerId` today). Whether to quote the field instead is a decision
        /// for whoever builds the panel that parses it.
        public static string SanitizePlayerId(string playerId)
        {
            if (playerId == null)
                return NullPlayerIdMarker;
            if (playerId.Length == 0)
                return EmptyPlayerIdMarker;

            int kept = KeptLength(playerId);
            bool cut = kept < playerId.Length;
            char[] neutralized = null;

            // STEPS 2 AND 3 ARE FUSED, NOT REORDERED. Because the substitution
            // is exactly one character for one character, "neutralize the whole
            // string, then cut it" and "cut it, then neutralize what is left"
            // produce the same answer character for character — which is the
            // whole point of a length-preserving placeholder, and why the cut
            // can be measured on the raw string above. Scanning only the kept
            // range is therefore the documented behavior for less work: no
            // placeholder is ever written into a character the cut discards.
            for (int i = 0; i < kept; i++)
            {
                if (!char.IsControl(playerId[i]))
                    continue;

                if (neutralized == null)
                {
                    // Allocated on the FIRST control character and never for an
                    // ordinary id — the overwhelmingly common case, and the one
                    // that must stay a plain reference return.
                    neutralized = new char[kept];
                    playerId.CopyTo(0, neutralized, 0, kept);
                }

                neutralized[i] = ControlCharPlaceholder;
            }

            string body;
            if (neutralized != null)
                body = new string(neutralized);
            else if (cut)
                body = playerId.Substring(0, kept);
            else
                body = playerId;

            return cut ? body + TruncationMarker : body;
        }

        /// How many of `playerId`'s characters reach the line: all of them when
        /// it fits, `MaxPlayerIdLength` when it does not, and one less than that
        /// when a cut at the bound would strand a character.
        ///
        /// The character a cut can strand is a LEADING SURROGATE sitting last in
        /// the kept range: its partner is the first one dropped, so keeping it
        /// would end the id with half of a code point — not a control character,
        /// so the neutralization pass leaves it alone, and not representable by
        /// any encoder the log line passes through afterwards. Whether the next
        /// character really is the trailing half is deliberately not consulted:
        /// the answer is the same either way, since a leading surrogate with
        /// nothing after it is unpaired however it got that way, and the property
        /// this function owes the caller is simply that the cut never produces
        /// one. `MaxPlayerIdLength` is 64, so the `- 1` below is always a valid
        /// index.
        static int KeptLength(string playerId)
        {
            if (playerId.Length <= MaxPlayerIdLength)
                return playerId.Length;

            return char.IsHighSurrogate(playerId[MaxPlayerIdLength - 1])
                ? MaxPlayerIdLength - 1
                : MaxPlayerIdLength;
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
    /// started. A SECOND READER JOINED SINCE (app-uxx): `Refuse` stamps its
    /// log line with the same delegate, so a refusal and `ServerBootstrap`'s
    /// accepted line report one axis and can simply be subtracted — which is
    /// precisely what two clocks would break. Contract for Task 40/41: pass
    /// the SAME `Func&lt;double&gt;` (or an equivalent reading of the same
    /// underlying clock) here and to every `MatchRoster.ShouldStart`/`TryJoin`
    /// call.
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

        /// `maxPlayers` is checked ONCE here via `HandshakeDecision.
        /// SlotsFitOnTheWire` (fix-round 2, K-1/N-1 ruling — see that
        /// method's own doc) instead of on every accepted join; a failing
        /// precondition throws `ArgumentOutOfRangeException` — this is a
        /// bootstrap-time configuration error (Task 41 handed in a
        /// `MatchConfig`/`SimConfig.Arena.MaxPlayers` that could never
        /// produce a wire-safe `PlayerIndex`), not a per-connection runtime
        /// condition, so it fails loudly before the server ever opens the
        /// port rather than on whichever client happens to trigger it.
        public MatchHandshake(NetworkManager networkManager, byte protocolVersion, ulong simConfigHash,
            int maxPlayers, ushort epoch, long seed, TryJoinDelegate tryJoin, Func<double> nowSeconds,
            Action<int, NetworkConnection> onAccepted = null)
        {
            _nm = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _tryJoin = tryJoin ?? throw new ArgumentNullException(nameof(tryJoin));
            _nowSeconds = nowSeconds ?? throw new ArgumentNullException(nameof(nowSeconds));
            if (!HandshakeDecision.SlotsFitOnTheWire(maxPlayers))
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlayers), maxPlayers,
                    "MatchHandshake: maxPlayers must fit MatchWelcomeNet.PlayerIndex (a byte) — "
                    + "every slot MatchRoster could ever hand out, [0, maxPlayers), must fit in [0, 255].");
            }
            _protocolVersion = protocolVersion;
            _simConfigHash = simConfigHash;
            _epoch = epoch;
            _seed = seed;
            _onAccepted = onAccepted;

            _nm.ServerManager.RegisterBroadcast<ClientHelloNet>(OnClientHello);
        }

        /// Unregisters this instance's handler (fix-round 1, I-6).
        /// FishNet's broadcast registration is ADDITIVE but NOT blind to
        /// identity (fix-round 2, NF-2 — corrects the mechanism this doc
        /// originally claimed): `RegisterBroadcast&lt;T&gt;` stores handlers
        /// in a `List&lt;Action&lt;...&gt;&gt;` via `AddUnique`, which is
        /// `if (!list.Contains(item)) list.Add(item)` — and `List&lt;T&gt;.
        /// Contains` on a delegate compares with `Delegate.Equals`, which
        /// checks BOTH the method AND the invocation TARGET (the `this` the
        /// method is bound to). Two `MatchHandshake` INSTANCES both convert
        /// their own `OnClientHello` to the same METHOD but with two
        /// DIFFERENT targets, so `Delegate.Equals` correctly says they
        /// differ and BOTH get added — not because identity goes
        /// unchecked, but because it is checked and the two instances are,
        /// correctly, not the same delegate. The practical consequence is
        /// unchanged from the original claim: constructing a second
        /// `MatchHandshake` on the same `NetworkManager` without
        /// unregistering the first leaves BOTH `OnClientHello` methods
        /// subscribed — the next `ClientHelloNet` runs through both, and
        /// the stale instance answers with its OWN (possibly now-wrong)
        /// epoch/roster, including sending a second `MatchWelcomeNet`
        /// carrying a stale `MatchEpoch`. The SAME mechanism also means
        /// registering the SAME instance's handler twice is a no-op
        /// (`Delegate.Equals` says they match, `AddUnique` skips it), and
        /// `UnregisterHandler`'s own `IndexOf`-then-`RemoveAt` returns
        /// early on an unknown handler — so calling `Unregister()` more
        /// than once on the same instance is safe and idempotent, it is
        /// only the FIRST call that does anything.
        ///
        /// A RESTART IS NOT A REASON TO CALL IT (CORRECTED at the Ф8 phase
        /// gate — the original wording named "a restart, Task 40" as the
        /// case this method exists for, and the later §6k Р164 ruled the
        /// opposite: `Unregister()` is explicitly NOT a step of a restart,
        /// because this instance and its roster survive an epoch untouched).
        /// What it is for: stopping the process, and the hypothetical second
        /// instance the paragraph above describes. This class never calls it
        /// itself on any internal event, matching `MatchServer`'s own choice
        /// to never self-unsubscribe from `OnPostTick`.
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
                    // No slot-range check here (fix-round 2, K-1/N-1 ruling
                    // — fix-round 1 had one). `(byte)slot` below is safe
                    // PROVIDED the constructor was handed the same
                    // maxPlayers the join delegate's roster was actually
                    // built with (contract §7 of task-39-report.md;
                    // fix-round 3, NNF-2 — corrects an earlier "PROVABLY
                    // safe" overclaim here). That is CONTRACTUAL, not
                    // something this method — or the class — can verify:
                    // `maxPlayers` is not even kept as a field (see the
                    // constructor), so there is no way to cross-check it
                    // against whatever roster `_tryJoin` actually consults.
                    // `SlotsFitOnTheWire`'s own doc says the same thing
                    // from the other side ("re-states the same bound
                    // independently rather than trusting a caller who
                    // might not go through MatchRoster at all") — a
                    // mismatched maxPlayers (Task 41 passing one number
                    // here and building the roster with another) would
                    // silently alias slots 256+ right back, unnoticed.

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

            // `hello.PlayerId` is handed on rather than looked up later
            // (app-uxx): this stack frame is the only place the id that
            // arrived in THIS hello still exists. A refused candidate never
            // enters the roster, so after this call returns there is nothing
            // left anywhere that could answer "which player was turned away".
            Refuse(connection, hello.PlayerId, refusal);
        }

        /// The single refusal path: say it on the console, say it on the wire,
        /// then (except for `DuplicatePlayer`) close the connection — the
        /// ordering and the exception both have their own paragraphs in the
        /// class doc above.
        ///
        /// `playerId` ARRIVES AS A PARAMETER (app-uxx), not as a field and not
        /// read back out of `connection`. See the call site's own comment for
        /// why it can only come from there.
        ///
        /// THE SENTENCE IS `HandshakeLog.RefusalLine`'S, THE CHANNEL IS THIS
        /// METHOD'S (`HandshakeLog`'s "SEVERITY IS NOT DECIDED HERE"
        /// paragraph). Warning for an ordinary refusal and error for
        /// `UnrecognizedRejection`, split on the same member the formatter
        /// splits its tail on. A REFUSED JOIN IS A ROUTINE EVENT, NOT A FAULT
        /// — app-uxx's own direction, "Warning, not Error, because a refusal is
        /// routine": a wrong `-ring-player-id`, an out-of-sync balance asset or
        /// a full match are all things an operator causes and fixes himself,
        /// and calling every one of them an error would leave nothing louder to
        /// say when the server really does break. `UnrecognizedRejection` is
        /// the one that really is broken — the join delegate and this assembly
        /// disagreeing about a code, on the server's own side of the contract.
        ///
        /// `UnityEngine.Debug`, NOT `_nm.Log*` (app-aor, owner's decision (a)).
        /// The dedicated-server build defines `UNITY_SERVER`, and FishNet's
        /// `LevelLoggingConfiguration.InitializeOnce` answers that define with
        /// `_headlessLogging`, whose default is `LoggingType.Error` — so both
        /// `Common` and `Warning` are dropped before reaching stdout, and a
        /// refusal logged through the `NetworkManager` would be invisible in
        /// exactly the process this line exists for. THE NAME IS SPELLED OUT IN
        /// FULL although nothing in this file makes a bare `Debug` ambiguous
        /// (unlike `MatchServer.cs`, which imports `System.Diagnostics` for
        /// `Stopwatch` and would bind the short name to the wrong type
        /// outright). Which logger a server-side line uses is the thing this
        /// zone has now got wrong twice — the two `MatchServer` lines app-aor
        /// found silenced, and this refusal (app-aor's third `MatchServer` line
        /// was already an `Error` and audible; it moved for uniformity, not to
        /// be rescued) — so it belongs in the reader's way at the call site
        /// rather than in a `using` at the top of the file.
        void Refuse(NetworkConnection connection, string playerId, HandshakeRefusal reason)
        {
            string line = HandshakeLog.RefusalLine(playerId, connection.ClientId, reason,
                _nowSeconds());
            if (reason == HandshakeRefusal.UnrecognizedRejection)
                UnityEngine.Debug.LogError(line);
            else
                UnityEngine.Debug.LogWarning(line);

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
