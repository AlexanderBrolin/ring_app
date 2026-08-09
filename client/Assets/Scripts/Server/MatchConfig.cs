using System;

namespace Ring.Server
{
    /// Stage 2 Task 38 (spec §3.10, plan Т38): how a match starts — mirrors the
    /// two JSON string values `MatchConfigLoader` accepts for `startMode`
    /// ("waitForAll"/"countdown"), never any other casing (spec §2.5's strict
    /// ordinal comparison — a machine-generated config from Э5's meta should
    /// never silently tolerate a typo).
    public enum MatchStartMode : byte
    {
        WaitForAll = 0,
        Countdown = 1,
    }

    /// One named slot in `MatchConfig.Players` — the roster `MatchRoster` uses
    /// to decide who may join and which token they must present (Р73).
    /// `JoinToken` may legally be an empty string ("this player needs no
    /// token") — see `MatchConfigLoader`'s own doc for why that is not a
    /// validation error.
    public readonly struct MatchPlayerEntry
    {
        public readonly string PlayerId;
        public readonly string JoinToken;

        public MatchPlayerEntry(string playerId, string joinToken)
        {
            PlayerId = playerId;
            JoinToken = joinToken;
        }
    }

    /// Plain value — spec §3.10. Never mutated after `MatchConfigLoader.Load`/
    /// `Parse` hands it back (readonly struct, readonly fields). `Players` is
    /// NEVER null when built THROUGH THIS CONSTRUCTOR — an absent
    /// `"players"` key and `"players": []` both produce a zero-length array
    /// (same "arrays EMPTY, never null" precedent as
    /// `ArenaSimConfig.WallA/WallB`, `SimConfig.cs`), and a caller passing
    /// `null` directly gets the same coalesced empty array back.
    ///
    /// THAT GUARANTEE ONLY HOLDS FOR A `MatchConfigLoadResult` WHOSE `Ok` IS
    /// `true` (fix-round 1, I-1 — an earlier draft of this doc overclaimed
    /// "cannot be defeated by construction", which is false: `default
    /// (MatchConfig)` bypasses this constructor entirely — every struct has
    /// one — and `MatchConfigLoader.Refuse` hands out exactly that `default`
    /// inside every refused `MatchConfigLoadResult.Config`, `Players`
    /// included, as `null`). Do not read `.Config` off a result with
    /// `Ok == false`; do not construct a `MatchRoster` from one either —
    /// `MatchRoster`'s own constructor guards against exactly this by
    /// throwing on a `null` `Players` (see its class doc).
    public readonly struct MatchConfig
    {
        public readonly string MatchId;

        /// Any `long` is legal, including `0` and negative values (Р42) — the
        /// world folds them via its existing `Fold`; only the FIELD'S ABSENCE
        /// from the source JSON is a validation error. See
        /// `MatchConfigLoader`'s class doc for the presence-vs-value mechanism
        /// this relies on.
        public readonly long Seed;

        public readonly int MaxPlayers;
        public readonly int Port;
        public readonly MatchPlayerEntry[] Players;
        public readonly MatchStartMode StartMode;
        public readonly int CountdownSeconds;

        public MatchConfig(string matchId, long seed, int maxPlayers, int port,
            MatchPlayerEntry[] players, MatchStartMode startMode, int countdownSeconds)
        {
            MatchId = matchId;
            Seed = seed;
            MaxPlayers = maxPlayers;
            Port = port;
            Players = players ?? Array.Empty<MatchPlayerEntry>();
            StartMode = startMode;
            CountdownSeconds = countdownSeconds;
        }
    }
}
