using System;
using System.IO;
using UnityEngine;

namespace Ring.Server
{
    /// A refusal is a VALUE, never an exception and never void (lesson 115).
    /// `Config` is meaningful ONLY when `Ok` is `true` — `default` otherwise
    /// (`Players` is `null` there, unlike every `MatchConfig` the loader
    /// actually hands out — see `MatchConfig`'s own doc and fix-round 1's
    /// I-1). When `Ok` is `false`, `Error` NAMES the first violated
    /// field/reason and `ExitCode` is `2`; when `Ok` is `true`, `Error` is
    /// `null` and `ExitCode` is `0`.
    public readonly struct MatchConfigLoadResult
    {
        public readonly bool Ok;
        public readonly MatchConfig Config;
        public readonly string Error;
        public readonly int ExitCode;

        public MatchConfigLoadResult(bool ok, in MatchConfig config, string error, int exitCode)
        {
            Ok = ok;
            Config = config;
            Error = error;
            ExitCode = exitCode;
        }
    }

    /// Stage 2 Task 38 (spec §3.10, brief §2.1-§2.5). Reads and validates the
    /// match's `MatchConfig` from the process environment or a raw JSON body
    /// — the ONLY consumer of `UnityEngine.JsonUtility` in `Ring.Server`
    /// (§2.1); everything downstream of parsing is plain C# over already-
    /// parsed values, no `UnityEngine` needed.
    ///
    /// NEVER THROWS, NEVER WRITES TO THE CONSOLE, NEVER CALLS
    /// `Application.Quit` (Files §1's scope boundary) — a refusal is
    /// `MatchConfigLoadResult { Ok = false, ExitCode = 2 }`; turning that into
    /// a process exit code and a stdout line is Task 41's job, not this
    /// class's.
    ///
    /// SOURCE PRIORITY IS "FIRST SET WINS", NOT "TRY EACH UNTIL ONE WORKS"
    /// (brief §2.4): `RING_MATCH_CONFIG` (a path) beats `RING_MATCH_CONFIG_
    /// JSON` (a body) beats dev defaults. If the path variable is set but
    /// reading/parsing it fails, THAT IS FATAL — the loader does not fall
    /// through to the body variable or to dev defaults. A "try everything"
    /// chain would turn a typo'd path into a silent dev-config boot on a
    /// production host (brief §0 scenario 2); this loader refuses instead. An
    /// empty string read from either variable is treated as "not set" — a
    /// container's environment commonly hands back "" rather than omitting
    /// the key entirely.
    ///
    /// PRESENCE VS. VALUE — THE TWO-SENTINEL MECHANISM (brief §2.3).
    /// `UnityEngine.JsonUtility` cannot report which JSON keys were actually
    /// present: `FromJson`/`FromJsonOverwrite` both leave an absent field at
    /// whatever it already held (Context7, `json-serialization`:
    /// "Missing fields will retain their default values" /
    /// "If the JSON lacks a value for a field, that field's value remains
    /// unchanged"). That is fatal for `seed`, because Р42 requires
    /// distinguishing "seed omitted" (refuse) from "seed explicitly 0"
    /// (accept) — and both leave a freshly-defaulted `long` field reading `0`,
    /// indistinguishable by value alone.
    ///
    /// The fix: `MatchConfigJson` — a mutable CLASS (not struct:
    /// `FromJsonOverwrite` mutates its argument in place, and a struct
    /// argument would be a throwaway copy) — is parsed TWICE, into two
    /// SEPARATE instances pre-filled with two DIFFERENT sentinel values per
    /// field (`NewSentinelLo`/`NewSentinelHi`). A field the JSON never
    /// mentions keeps ITS OWN instance's sentinel on both sides, so the two
    /// disagree; a field the JSON DOES set — to any value at all, including
    /// `0`, `long.MinValue` or `long.MaxValue` — lands at the SAME value on
    /// both sides, because both instances parse the identical JSON text. So
    /// "the two instances agree" is exactly "this field was present",
    /// independent of what value it holds. `players` is the one field left
    /// `null` on both instances rather than sentineled — it's a reference
    /// type, so "absent" is already unambiguous as `null`; sentineling it
    /// would just be a second, needless mechanism for a question the type
    /// system already answers (this is why brief §2.3 calls the asymmetry
    /// deliberate, not an oversight).
    ///
    /// STEP 1a — JsonUtility ON MALFORMED INPUT (established by a batchmode
    /// probe, not assumed; task-38-report.md §2 has the full transcript).
    /// `FromJsonOverwrite` on a non-empty string that isn't valid JSON
    /// ("{", "not json at all", a JSON array) THROWS `System.ArgumentException`
    /// with a parser message ("JSON parse error: ...", "JSON must represent
    /// an object type."); `Parse` below catches exactly that type, not a
    /// blind `catch (Exception)`. `FromJsonOverwrite` on `""`/`null` throws
    /// NOTHING — it is a silent no-op that leaves the target's fields
    /// completely untouched. `Parse` recognizes that case EXPLICITLY, with
    /// its own early `string.IsNullOrEmpty(json)` guard BEFORE either
    /// sentineled instance is even constructed, rather than relying on the
    /// coincidence that an empty/null body would otherwise fall through to a
    /// generic "matchId is missing" refusal by every field simply staying at
    /// its own sentinel.
    public static class MatchConfigLoader
    {
        public const string PathVariable = "RING_MATCH_CONFIG";
        public const string BodyVariable = "RING_MATCH_CONFIG_JSON";

        // Two sentinel strings for the presence-detection mechanism above.
        // `matchId` and `startMode` are the only STRING fields on
        // `MatchConfigJson` that need one — `players` is a reference type
        // (see the class doc's last paragraph on why it gets no sentinel at
        // all). THE ONLY REQUIREMENT IS THAT THE TWO DIFFER FROM EACH OTHER —
        // presence is `lo.X == hi.X`, comparing the two instances to EACH
        // OTHER after both parse the IDENTICAL JSON text, never a comparison
        // against a single fixed constant. So a present field matches at ANY
        // value the JSON supplies, including a value that happens to equal
        // `StringSentinelLo`/`Hi` itself — proven by
        // `TwoSentinelMechanism_DistinguishesPresenceFromSentinelValuedFields`
        // (`MatchConfigTests.cs`), which feeds exactly that value in and
        // still gets a correct "present" reading. There is therefore no need
        // for these two strings to avoid resembling plausible user data —
        // that would be a defense against a threat this mechanism does not
        // have.
        const string StringSentinelLo = "__ring_sentinel_lo__";
        const string StringSentinelHi = "__ring_sentinel_hi__";

        const string StartModeWaitForAll = "waitForAll";
        const string StartModeCountdown = "countdown";

        // Dev defaults (brief §2.4) — constants of the CODE, never numbers
        // pulled from a `.asset` (spec §0): this path exists specifically for
        // running without any balance data loaded at all.
        const string DevMatchId = "dev-match";
        const long DevSeed = 1; // arbitrary, fixed, nonzero — only Р42 legality matters here, not the specific value.
        // THIS NUMBER IS A CONVENTION WITH FOUR HOMES, and a change here has
        // to be carried to all of them: `client/docker/Dockerfile` declares
        // `EXPOSE 7777/udp`, R-CONTAINER's runbook and `docker-compose.yml`
        // publish `-p 7777:7777/udp`, and `client/docker/match.json` carries
        // the `"port"` the server actually binds in every container run.
        //
        // THE CONTAINER DOES NOT READ THIS CONSTANT. It gets its port from the
        // match config, and this value only stands in for a dev run started
        // without one -- which is exactly why the four can drift apart
        // silently, and why the Dockerfile names this constant as the origin
        // of its own number.
        const int DevPort = 7777;
        const int DevCountdownSeconds = 5;

        /// Production wiring (Task 41): binds `Environment.GetEnvironmentVariable`
        /// and a file reader that turns any I/O failure into `null` (see
        /// `ReadFileOrNull`'s own doc for why that catch is broad, unlike
        /// `Parse`'s deliberately narrow one).
        public static MatchConfigLoadResult Load(int arenaMaxPlayers) =>
            Load(arenaMaxPlayers, Environment.GetEnvironmentVariable, ReadFileOrNull);

        /// The seam every test uses — no process-global state is touched.
        /// `readFile` returns the file's content, or `null` on ANY failure
        /// (missing file, no permission, a directory, ...) — the caller never
        /// needs to model which failure occurred, because brief §2.4 treats
        /// them all identically: fatal, no fallback.
        public static MatchConfigLoadResult Load(int arenaMaxPlayers,
            Func<string, string> getEnv, Func<string, string> readFile)
        {
            string path = getEnv(PathVariable);
            if (!string.IsNullOrEmpty(path))
            {
                string content = readFile(path);
                if (content == null)
                    return Refuse($"failed to read {PathVariable} ('{path}').");
                return Parse(content, arenaMaxPlayers);
            }

            string body = getEnv(BodyVariable);
            if (!string.IsNullOrEmpty(body))
                return Parse(body, arenaMaxPlayers);

            return Accept(DevDefaults(arenaMaxPlayers));
        }

        /// Body only, no environment at all — the parse+validate core both
        /// `Load` overloads route through once they have a JSON string in hand.
        public static MatchConfigLoadResult Parse(string json, int arenaMaxPlayers)
        {
            // Step 1a: FromJsonOverwrite is a silent no-op on ""/null (no
            // exception to catch) — recognized explicitly, before either
            // sentineled instance is built, rather than left to an
            // accidental "matchId is missing" outcome.
            if (string.IsNullOrEmpty(json))
                return Refuse("match config JSON body is empty or missing.");

            MatchConfigJson lo = NewSentinelLo();
            MatchConfigJson hi = NewSentinelHi();
            try
            {
                JsonUtility.FromJsonOverwrite(json, lo);
                JsonUtility.FromJsonOverwrite(json, hi);
            }
            catch (ArgumentException ex)
            {
                // Step 1a's confirmed exception type for malformed non-empty
                // JSON — caught by its specific type, not a blind catch(Exception).
                return Refuse($"match config JSON is malformed: {ex.Message}");
            }

            bool matchIdPresent = lo.matchId == hi.matchId;
            bool seedPresent = lo.seed == hi.seed;
            bool maxPlayersPresent = lo.maxPlayers == hi.maxPlayers;
            bool portPresent = lo.port == hi.port;
            bool startModePresent = lo.startMode == hi.startMode;
            bool countdownSecondsPresent = lo.countdownSeconds == hi.countdownSeconds;
            bool matchesToPlayPresent = lo.matchesToPlay == hi.matchesToPlay;

            // Required fields, checked in a fixed order — the FIRST violation
            // wins (brief §2.5: one message, not a batch like SimConfigBuilder's
            // ~60-field report; the audience here is a deploy operator who
            // needs the first blocker, not a balance designer auditing a whole sheet).
            if (!matchIdPresent) return Refuse("matchId is missing.");
            // Coordinator ruling, fix-round 1 (m8): matchId feeds Task 41's
            // startup log line and the meta's own records, where an empty
            // string is indistinguishable from a generator bug — unlike
            // every other required string field here, "present but empty"
            // is not a value worth accepting.
            if (string.IsNullOrEmpty(lo.matchId)) return Refuse("matchId must not be empty.");
            if (!seedPresent) return Refuse("seed is missing.");

            if (!maxPlayersPresent) return Refuse("maxPlayers is missing.");
            if (lo.maxPlayers < 1 || lo.maxPlayers > arenaMaxPlayers)
            {
                return Refuse($"maxPlayers must be in [1, {arenaMaxPlayers}] " +
                    $"(got {lo.maxPlayers}).");
            }

            if (!portPresent) return Refuse("port is missing.");
            if (lo.port < 1 || lo.port > 65535)
                return Refuse($"port must be in [1, 65535] (got {lo.port}).");

            if (!startModePresent) return Refuse("startMode is missing.");
            MatchStartMode startMode;
            if (lo.startMode == StartModeWaitForAll) startMode = MatchStartMode.WaitForAll;
            else if (lo.startMode == StartModeCountdown) startMode = MatchStartMode.Countdown;
            else
            {
                return Refuse("startMode must be exactly \"waitForAll\" or \"countdown\" " +
                    $"(got \"{lo.startMode}\").");
            }

            // Conditionally required: only in countdown mode (brief §2.5).
            // In waitForAll mode an absent value is legal and a present value
            // is simply ignored — never read into the result below.
            int countdownSeconds = 0;
            if (startMode == MatchStartMode.Countdown)
            {
                if (!countdownSecondsPresent)
                {
                    return Refuse("countdownSeconds is missing " +
                        "(required when startMode is \"countdown\").");
                }
                countdownSeconds = lo.countdownSeconds;
                if (countdownSeconds < 0)
                    return Refuse($"countdownSeconds must be >= 0 (got {countdownSeconds}).");
                // NO UPPER BOUND HERE, DELIBERATELY (brief §2.5). How long a
                // countdown may run is not this field's concern — the actual
                // ceiling on "how long we wait for players" is
                // `NetConfig.JoinTimeoutSeconds` (Task 40's exit code 3, a
                // different failure mode than a bad config). A second cap
                // here would be a second home for that one rule (AGENT.md
                // rule 2).
            }

            // players[] — optional; absence and "[]" both mean zero entries.
            MatchPlayerEntryJson[] playersJson = lo.players ?? Array.Empty<MatchPlayerEntryJson>();
            var players = new MatchPlayerEntry[playersJson.Length];
            for (int i = 0; i < playersJson.Length; i++)
            {
                string playerId = playersJson[i]?.playerId;
                if (string.IsNullOrEmpty(playerId))
                    return Refuse($"players[{i}].playerId is empty.");

                for (int j = 0; j < i; j++)
                {
                    if (players[j].PlayerId == playerId)
                        return Refuse($"players[] contains duplicate playerId \"{playerId}\".");
                }

                // joinToken may legally be an empty string — "no token
                // required for this player" (brief §2.5), not a config error.
                players[i] = new MatchPlayerEntry(playerId, playersJson[i].joinToken ?? string.Empty);
            }

            if (players.Length > lo.maxPlayers)
            {
                return Refuse($"players[] has {players.Length} entries, " +
                    $"exceeding maxPlayers ({lo.maxPlayers}).");
            }

            // The degenerate-handle guard (brief §2.5, lesson 125): waiting
            // for nobody must never reach MatchServer.StartMatch with a
            // zero-length connections array.
            if (startMode == MatchStartMode.WaitForAll && players.Length == 0)
                return Refuse("startMode \"waitForAll\" requires a non-empty players[] roster.");

            // bd `app-qrew`: OPTIONAL, and 1 when absent. Optional is the
            // contract rather than a convenience — all three `match*.json`
            // already deployed to the host predate this field, and a required
            // one would refuse every one of them on the next image. A
            // PRESENT value is validated the way `countdownSeconds` above is:
            // an operator asking for zero matches is asking for something
            // impossible, and this loader refuses rather than reinterprets.
            int matchesToPlay = MatchRerunPolicy.DefaultMatchesToPlay;
            if (matchesToPlayPresent)
            {
                matchesToPlay = lo.matchesToPlay;
                if (matchesToPlay < 1)
                    return Refuse($"matchesToPlay must be >= 1 (got {matchesToPlay}).");
            }

            return Accept(new MatchConfig(lo.matchId, lo.seed, lo.maxPlayers, lo.port,
                players, startMode, countdownSeconds, matchesToPlay));
        }

        /// Constants of the code (spec §0), never numbers pulled from a
        /// `.asset` — `maxPlayers`/`Port`/`StartMode` are chosen specifically
        /// so this result passes `Parse`'s own validation unmodified
        /// (`Countdown`, not `WaitForAll`: an empty `players[]` under
        /// `WaitForAll` is refused outright — see the guard above — so
        /// `WaitForAll` would make these defaults invalid by construction).
        public static MatchConfig DevDefaults(int arenaMaxPlayers) =>
            new MatchConfig(DevMatchId, DevSeed, arenaMaxPlayers, DevPort,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, DevCountdownSeconds);

        static MatchConfigLoadResult Refuse(string error) =>
            new MatchConfigLoadResult(false, default, error, 2);

        static MatchConfigLoadResult Accept(in MatchConfig config) =>
            new MatchConfigLoadResult(true, in config, null, 0);

        static MatchConfigJson NewSentinelLo() => new MatchConfigJson
        {
            matchId = StringSentinelLo,
            seed = long.MinValue,
            maxPlayers = int.MinValue,
            port = int.MinValue,
            players = null,
            startMode = StringSentinelLo,
            countdownSeconds = int.MinValue,
            matchesToPlay = int.MinValue,
        };

        static MatchConfigJson NewSentinelHi() => new MatchConfigJson
        {
            matchId = StringSentinelHi,
            seed = long.MaxValue,
            maxPlayers = int.MaxValue,
            port = int.MaxValue,
            players = null,
            startMode = StringSentinelHi,
            countdownSeconds = int.MaxValue,
            matchesToPlay = int.MaxValue,
        };

        /// Wraps `File.ReadAllText` for the production `Load(int)` overload.
        /// Deliberately a BROAD catch, unlike `Parse`'s narrow
        /// `catch (ArgumentException)` above — this one is about FILE I/O
        /// (missing file, permission denied, a directory given instead of a
        /// file, ...), a completely different failure family from "JSON text
        /// didn't parse", and brief §2.4 treats every one of those I/O
        /// failures identically: fatal, no fallback. The caller (`Load`)
        /// turns a `null` return into a refusal.
        static string ReadFileOrNull(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }

        // Parsed DTO for UnityEngine.JsonUtility — a mutable CLASS, not a
        // struct (FromJsonOverwrite mutates its argument in place; a struct
        // argument would be a throwaway copy and the mutation would be lost).
        // Never leaves this file.
        [Serializable]
        sealed class MatchConfigJson
        {
            public string matchId;
            public long seed;
            public int maxPlayers;
            public int port;
            public MatchPlayerEntryJson[] players;
            public string startMode;
            public int countdownSeconds;
            public int matchesToPlay;
        }

        [Serializable]
        sealed class MatchPlayerEntryJson
        {
            public string playerId;
            public string joinToken;
        }
    }
}
