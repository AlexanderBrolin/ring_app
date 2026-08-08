using System;

namespace Ring.Server
{
    /// Why `TryJoin` refused a candidate — spec §3.10, brief §2.6. `None`
    /// (`0`) never appears as the OUT rejection when `TryJoin` returns
    /// `true`; it is the value written on the accepted path.
    public enum JoinRejection : byte
    {
        None = 0,
        MatchAlreadyStarted,
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
    /// (`MatchServer.cs:240-245`).
    ///
    /// SLOTS ARE ASSIGNED IN ACCEPTANCE ORDER, NOT `Players[]` ORDER, AND
    /// NEVER CHANGE ONCE GIVEN. This is the SAME numbering Task 39 hands to
    /// `MatchServer.StartMatch`'s `connections[i]`/`controllers[i]` — a
    /// second, independently-ordered numbering would let the two arrays
    /// disagree about which index is which player (`MatchServer.cs:129-146`'s
    /// own "assumed to be the SAME player, by index" contract).
    ///
    /// `TryJoin`'S CHECK ORDER IS FIXED (brief §2.6): `MatchAlreadyStarted` ->
    /// `DuplicatePlayer` -> (only when `MatchConfig.Players` is non-empty)
    /// `UnknownPlayer` -> `BadToken` -> `MatchFull`. Duplicate is checked
    /// before unknown deliberately — a repeat of an id already accepted is
    /// "you again", not "who are you". An EMPTY `Players[]` (the dev
    /// `countdown` shape) skips the roster-membership/token checks entirely:
    /// any non-empty `playerId` is accepted up to `MaxPlayers`, and
    /// `joinToken` is never compared against anything.
    ///
    /// `ShouldStart` — `waitForAll` needs every entry of `Players[]` to have
    /// joined; `countdown` needs at least one connection AND `nowSeconds`
    /// past `CountdownSeconds` measured from the FIRST accepted join, never
    /// from construction (a countdown ticking from construction could expire
    /// before anyone ever connects at all, starting a match with zero players).
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
