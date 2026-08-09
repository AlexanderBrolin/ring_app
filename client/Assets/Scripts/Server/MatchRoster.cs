using System;

namespace Ring.Server
{
    /// Why `TryJoin` refused a candidate — spec §3.10, brief §2.6. `None`
    /// (`0`) never appears as the OUT rejection when `TryJoin` returns
    /// `true`; it is the value written on the accepted path. `InvalidPlayerId`
    /// added fix-round 1 (I-3): the wire hands `playerId` in from Task 39's
    /// handshake, and a `null`/empty value reaching `TryJoin` unchecked used
    /// to slot in as a literal `null` string and get misdiagnosed as
    /// `DuplicatePlayer` on the SECOND such attempt — the wire was weaker
    /// than the config loader, which has required non-empty `playerId` for
    /// every roster entry since Task 38 (`MatchConfigLoader.cs`).
    public enum JoinRejection : byte
    {
        None = 0,
        MatchAlreadyStarted,
        InvalidPlayerId,
        UnknownPlayer,
        BadToken,
        DuplicatePlayer,
        MatchFull,
    }

    /// Stage 2 Task 38 (spec §3.10, Р73; brief §2.6). Decides who is in a
    /// match — a pure state machine, no `UnityEngine`, no clock of its own:
    /// every method that needs "now" takes it as a parameter, the same
    /// discipline `TickTimeAccumulator` uses for `Stopwatch` milliseconds
    /// from its caller.
    ///
    /// Р73 — `PlayerCount` IS THE CONNECTED COUNT, NOT `MaxPlayers`. A match
    /// starts with however many of `MatchConfig.Players` actually connected
    /// by the time `Start()` is called; the world must never carry empty
    /// slots for players who never showed up (`WorldStats` would count them,
    /// mobs would eat their unowned bodies). `PlayerCount` freezes at
    /// `Start()` and throws before it — the same "silent 0 is indistinguishable
    /// from nobody home" reasoning as `MatchServer.StatsFor`
    /// (`MatchServer.StatsFor`).
    ///
    /// SLOTS ARE ASSIGNED IN ACCEPTANCE ORDER, NOT `Players[]` ORDER, AND
    /// NEVER CHANGE ONCE GIVEN. This is the SAME numbering Task 39 hands to
    /// `MatchServer.StartMatch`'s `connections[i]`/`controllers[i]` — a
    /// second, independently-ordered numbering would let the two arrays
    /// disagree about which index is which player (`MatchServer`'s class doc,
    /// paragraph "WHAT Ф8 MUST HAND IN",
    /// own "assumed to be the SAME player, by index" contract).
    ///
    /// `TryJoin`'S CHECK ORDER IS FIXED (brief §2.6, extended fix-round 1
    /// I-3): `MatchAlreadyStarted` -> `InvalidPlayerId` -> `DuplicatePlayer`
    /// -> (only when `MatchConfig.Players` is non-empty) `UnknownPlayer` ->
    /// `BadToken` -> `MatchFull`. `InvalidPlayerId` sits right after the
    /// started-gate and BEFORE the duplicate scan deliberately — comparing a
    /// `null`/empty candidate against already-accepted ids is a meaningless
    /// comparison to make at all, not a "no match found" one. Duplicate is
    /// checked before unknown deliberately — a repeat of an id already
    /// accepted is "you again", not "who are you". An EMPTY `Players[]` (the
    /// dev `countdown` shape) skips the roster-membership/token checks
    /// entirely: any non-empty `playerId` is accepted up to `MaxPlayers`, and
    /// `joinToken` is never compared against anything.
    ///
    /// `ShouldStart` — `waitForAll` needs every entry of `Players[]` to have
    /// joined; `countdown` needs at least one connection AND `nowSeconds`
    /// past `CountdownSeconds` measured from the FIRST accepted join, never
    /// from construction (a countdown ticking from construction could expire
    /// before anyone ever connects at all, starting a match with zero players).
    ///
    /// `Start()` REQUIRES AT LEAST ONE CONNECTION (fix-round 1, m5). A
    /// zero-connection `Start()` would freeze `PlayerCount` at `0` and hand
    /// that silently down two more layers to `MatchServer.StartMatch`, which
    /// throws on an empty `connections` array (`MatchServer.ValidateRoster`) —
    /// this class is where that "nobody ever joined" state is still
    /// diagnosable as ITS OWN problem, not a stack trace from an unrelated
    /// class two calls later.
    ///
    /// THIS INSTANCE IS SINGLE-USE, AND A RESTART DOES NOT NEED A SECOND
    /// ONE (fix-round 1 m6, CORRECTED at the Ф8 phase gate against the later
    /// §6k Р164 — the original wording said a restart builds a NEW
    /// `MatchRoster`, and that is exactly what Р164 ruled out). `_started` is
    /// a one-way flag with no `Reset`, and none is planned, because a restart
    /// never asks this class anything again: spec §3.10 refuses joining a
    /// match in progress, so a restart HAS no join phase, and this instance
    /// simply keeps answering `MatchAlreadyStarted` to anything that arrives.
    /// The roster and the handshake are objects of the JOIN PHASE; they
    /// survive an epoch untouched, and `MatchConfig` is never re-read from
    /// the environment either (owner's decision, Р164: a restart is a rerun
    /// of the match this process was given, not a new session).
    ///
    /// THE CALLER MUST HAND IN A `MatchConfig` FROM AN `Ok == true` RESULT
    /// (fix-round 1, I-1). The constructor below guards the two ways a
    /// caller could otherwise reach a broken instance: a `null` `Players`
    /// (only possible via `default(MatchConfig)`, e.g. blindly wrapping a
    /// refused `MatchConfigLoadResult.Config` — see `MatchConfig`'s own doc)
    /// and a non-positive `MaxPlayers`. THE TWO NON-POSITIVE CASES FAIL
    /// DIFFERENTLY WITHOUT THIS GUARD (fix-round 2, N-2 — an earlier draft
    /// of this doc wrongly claimed BOTH throw): a NEGATIVE value makes
    /// `new string[MaxPlayers]` below throw a bare, unexplained
    /// `OverflowException`; `MaxPlayers == 0` throws NOTHING — `new
    /// string[0]` is a perfectly legal empty allocation — and instead
    /// produces a silently inert roster where every `TryJoin` reads
    /// `_connectedCount (0) >= _config.MaxPlayers (0)` and returns
    /// `MatchFull` forever, no player ever able to join. The guard below
    /// closes both failure shapes with the same loud, named exception.
    public sealed class MatchRoster
    {
        readonly MatchConfig _config;
        readonly string[] _slotPlayerIds;

        int _connectedCount;
        bool _started;
        int _frozenPlayerCount = -1;
        double _firstJoinSeconds;

        public MatchRoster(in MatchConfig config)
        {
            if (config.Players == null)
            {
                throw new ArgumentException(
                    "MatchRoster: MatchConfig.Players is null — only a MatchConfigLoadResult " +
                    "with Ok == true produces a valid MatchConfig (default(MatchConfig), e.g. " +
                    "from a refused result, is not one; see MatchConfig's own doc).",
                    nameof(config));
            }
            if (config.MaxPlayers < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(config), config.MaxPlayers,
                    "MatchRoster: MatchConfig.MaxPlayers must be >= 1.");
            }
            // Ф8 gate W-6: the same degenerate-roster rule `MatchConfigLoader.
            // Parse` already applies, RESTATED here rather than moved — the
            // loader's own refusal stays where it is, and its test with it.
            // Two homes, two contracts: the loader answers "is this
            // configuration well-formed" with a VALUE, this constructor answers
            // "may this object exist at all" with a throw, and a caller that
            // never goes through the loader reaches only the second. `ShouldStart`'s
            // WaitForAll arm reads `_connectedCount >= _config.Players.Length`,
            // which is true on the FIRST connection when `Players.Length == 0`
            // (`0 >= 0` is already true before anyone joins, and every count
            // afterward is too) — unreachable through the loader today, since
            // it refuses this combination first, but a caller that builds a
            // `MatchConfig` directly (every test in this file, and any future
            // production path that skips the loader) has no such gate. The
            // rule the loader enforces belongs here, not only in a parser one
            // layer up (AGENT.md rule 4: "the decision lives in the core").
            if (config.StartMode == MatchStartMode.WaitForAll && config.Players.Length == 0)
            {
                throw new ArgumentException(
                    "MatchRoster: MatchStartMode.WaitForAll requires a non-empty " +
                    "MatchConfig.Players roster — waiting for an empty roster starts on the very " +
                    "first connection (ShouldStart's own comparison against Players.Length).",
                    nameof(config));
            }

            _config = config;
            _slotPlayerIds = new string[config.MaxPlayers];
        }

        public bool Started => _started;

        public int ConnectedCount => _connectedCount;

        /// Р73: frozen at `Start()` — the number of players who actually
        /// connected. Throws before `Start()`, matching `MatchServer.
        /// StatsFor`'s own "no silent 0" precedent.
        public int PlayerCount
        {
            get
            {
                if (!_started)
                    throw new InvalidOperationException("MatchRoster.PlayerCount: match has not started.");
                return _frozenPlayerCount;
            }
        }

        public string PlayerIdAt(int slot)
        {
            if (slot < 0 || slot >= _connectedCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slot),
                    $"MatchRoster.PlayerIdAt: slot {slot} is outside [0, {_connectedCount}).");
            }
            return _slotPlayerIds[slot];
        }

        public bool TryJoin(string playerId, string joinToken, double nowSeconds,
            out int slot, out JoinRejection rejection)
        {
            slot = -1;

            if (_started)
            {
                rejection = JoinRejection.MatchAlreadyStarted;
                return false;
            }

            // Fix-round 1, I-3: checked before the duplicate scan below —
            // comparing null/empty against already-accepted ids is not a
            // meaningful "found/not found" question.
            if (string.IsNullOrEmpty(playerId))
            {
                rejection = JoinRejection.InvalidPlayerId;
                return false;
            }

            for (int i = 0; i < _connectedCount; i++)
            {
                if (_slotPlayerIds[i] == playerId)
                {
                    rejection = JoinRejection.DuplicatePlayer;
                    return false;
                }
            }

            bool rosterIsNamed = _config.Players.Length > 0;
            if (rosterIsNamed)
            {
                int rosterIndex = FindInRoster(playerId);
                if (rosterIndex < 0)
                {
                    rejection = JoinRejection.UnknownPlayer;
                    return false;
                }
                if (_config.Players[rosterIndex].JoinToken != joinToken)
                {
                    rejection = JoinRejection.BadToken;
                    return false;
                }
            }

            if (_connectedCount >= _config.MaxPlayers)
            {
                rejection = JoinRejection.MatchFull;
                return false;
            }

            if (_connectedCount == 0)
                _firstJoinSeconds = nowSeconds;

            slot = _connectedCount;
            _slotPlayerIds[_connectedCount] = playerId;
            _connectedCount++;
            rejection = JoinRejection.None;
            return true;
        }

        public bool ShouldStart(double nowSeconds)
        {
            if (_started || _connectedCount == 0)
                return false;

            if (_config.StartMode == MatchStartMode.WaitForAll)
                return _connectedCount >= _config.Players.Length;

            // Countdown — measured from the FIRST accepted join, not from
            // construction (class doc's "never from construction" paragraph).
            return (nowSeconds - _firstJoinSeconds) >= _config.CountdownSeconds;
        }

        public void Start()
        {
            // Fix-round 1, m5: a zero-connection Start() would freeze
            // PlayerCount at 0 and let that reach MatchServer.StartMatch two
            // layers down, which throws on an empty connections array
            // (MatchServer.ValidateRoster) — diagnosed here instead, where the
            // "nobody ever joined" state is still this class's own problem.
            if (_connectedCount == 0)
            {
                throw new InvalidOperationException(
                    "MatchRoster.Start: cannot start with zero connected players.");
            }

            _frozenPlayerCount = _connectedCount;
            _started = true;
        }

        int FindInRoster(string playerId)
        {
            MatchPlayerEntry[] players = _config.Players;
            for (int i = 0; i < players.Length; i++)
            {
                if (players[i].PlayerId == playerId)
                    return i;
            }
            return -1;
        }
    }
}
