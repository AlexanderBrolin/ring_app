using System;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Ring.Server;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 38 (spec §3.10/§3.11's first three bullets, §4:1147-1149;
    /// plan Т38; task-38-brief.md). The last missing class of spec §4 — DoD
    /// Stage 2 item 2 reads 14/14 once this class exists and passes
    /// (task-38-brief §0).
    ///
    /// Tests 1-9 are the plan's own letter (brief §3, Step 1); 10-24 are the
    /// coordinator's additions (brief §2.7); the final section is fix-round 1
    /// (Opus review C-1/I-1/I-3/I-4/m5/m8 — task-38-report.md §10). Every
    /// negative test carries a positive witness beside it (brief §2.8), so a
    /// mutation that always refuses (or always accepts) cannot survive
    /// unnoticed — see task-38-report.md's mutation table for the full
    /// M-1..M-25 sweep this file was written against.
    ///
    /// ARENA CAP IS A FIXTURE, NEVER THE BARE LITERAL 3 (brief §4/§2.2):
    /// `ArenaCap` below reads `TestConfigs.Default().Arena.MaxPlayers` — the
    /// SAME number `SimConfigBuilder.cs:374` validates and `ArenaConfig.cs:84`
    /// ships as `[Range(1,3)]` — so a future rebalance of the arena's cap
    /// doesn't silently desync this file from the number `Ring.Server`
    /// actually receives at runtime. `ArenaCapIsHandedIn_NotHardcoded` is the
    /// one test exempt from this: its entire point is proving the cap is a
    /// PARAMETER, so it drives two explicit literal cap values (2 and 3) by
    /// design, not fixture indirection — using the fixture there would defeat
    /// the test.
    public class MatchConfigTests
    {
        static readonly int ArenaCap = TestConfigs.Default().Arena.MaxPlayers;

        // ------------------------------------------------------------------
        // JSON body builder — every parameter is the RAW JSON text for that
        // key (already quoted for strings); passing `null` OMITS the key
        // entirely (not "sets it to null"). This is how every "field X is
        // missing" fixture below is built without hand-duplicating the whole
        // object per test.
        // ------------------------------------------------------------------
        static string Json(string matchId = "\"m1\"", string seed = "7",
            string maxPlayers = "2", string port = "7000",
            string startMode = "\"waitForAll\"", string countdownSeconds = "5",
            string players = "[{\"playerId\":\"p1\",\"joinToken\":\"t1\"},{\"playerId\":\"p2\",\"joinToken\":\"t2\"}]")
        {
            var sb = new StringBuilder("{");
            void Field(string name, string rawValue)
            {
                if (rawValue == null) return;
                if (sb.Length > 1) sb.Append(',');
                sb.Append('"').Append(name).Append("\":").Append(rawValue);
            }
            Field("matchId", matchId);
            Field("seed", seed);
            Field("maxPlayers", maxPlayers);
            Field("port", port);
            Field("startMode", startMode);
            Field("countdownSeconds", countdownSeconds);
            Field("players", players);
            sb.Append('}');
            return sb.ToString();
        }

        /// Round-trips an already-built `MatchConfig` back into the JSON
        /// shape `Parse` accepts. Used only by test 2, to prove dev defaults
        /// pass the REAL validation path — not a second, hand-duplicated copy
        /// of its rules living inside this test file.
        static string ToJson(in MatchConfig c)
        {
            static string Q(string s) => "\"" + s + "\"";
            string startMode = c.StartMode == MatchStartMode.WaitForAll ? "waitForAll" : "countdown";
            string players = string.Join(",", c.Players.Select(p =>
                $"{{\"playerId\":{Q(p.PlayerId)},\"joinToken\":{Q(p.JoinToken)}}}"));
            return $"{{\"matchId\":{Q(c.MatchId)},\"seed\":{c.Seed},\"maxPlayers\":{c.MaxPlayers}," +
                $"\"port\":{c.Port},\"startMode\":{Q(startMode)},\"countdownSeconds\":{c.CountdownSeconds}," +
                $"\"players\":[{players}]}}";
        }

        static MatchConfigLoadResult Parse(string json) => MatchConfigLoader.Parse(json, ArenaCap);

        static void AssertAccepted(MatchConfigLoadResult r)
        {
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual(0, r.ExitCode);
            Assert.IsNull(r.Error);
        }

        static void AssertRefused(MatchConfigLoadResult r)
        {
            Assert.IsFalse(r.Ok);
            Assert.AreEqual(2, r.ExitCode);
            Assert.IsNotNull(r.Error);
        }

        static void AssertRefusedNaming(MatchConfigLoadResult r, string expectedFieldNameSubstring)
        {
            AssertRefused(r);
            StringAssert.Contains(expectedFieldNameSubstring, r.Error);
        }

        // ==================================================================
        // 1-9: plan's own letter (brief §3, Step 1).
        // ==================================================================

        [Test]
        public void Parses_WellFormedJson_AllFields()
        {
            string json = Json(matchId: "\"arena-7\"", seed: "-42", maxPlayers: "2",
                port: "7777", startMode: "\"countdown\"", countdownSeconds: "10",
                players: "[{\"playerId\":\"alice\",\"joinToken\":\"tok-a\"},{\"playerId\":\"bob\",\"joinToken\":\"tok-b\"}]");

            var result = Parse(json);

            AssertAccepted(result);
            var c = result.Config;
            Assert.AreEqual("arena-7", c.MatchId);
            Assert.AreEqual(-42L, c.Seed);
            Assert.AreEqual(2, c.MaxPlayers);
            Assert.AreEqual(7777, c.Port);
            Assert.AreEqual(MatchStartMode.Countdown, c.StartMode);
            Assert.AreEqual(10, c.CountdownSeconds);
            Assert.AreEqual(2, c.Players.Length);
            Assert.AreEqual("alice", c.Players[0].PlayerId);
            Assert.AreEqual("tok-a", c.Players[0].JoinToken);
            Assert.AreEqual("bob", c.Players[1].PlayerId);
            Assert.AreEqual("tok-b", c.Players[1].JoinToken);
        }

        [Test]
        public void EmptyEnvironment_YieldsDevDefaults()
        {
            var result = MatchConfigLoader.Load(ArenaCap, getEnv: _ => null, readFile: _ => null);
            AssertAccepted(result);

            var expected = MatchConfigLoader.DevDefaults(ArenaCap);
            Assert.AreEqual(expected.MatchId, result.Config.MatchId);
            Assert.AreEqual(expected.Seed, result.Config.Seed);
            Assert.AreEqual(expected.MaxPlayers, result.Config.MaxPlayers);
            Assert.AreEqual(expected.Port, result.Config.Port);
            Assert.AreEqual(expected.StartMode, result.Config.StartMode);
            Assert.AreEqual(expected.CountdownSeconds, result.Config.CountdownSeconds);
            Assert.AreEqual(0, expected.Players.Length);
            Assert.AreEqual(ArenaCap, expected.MaxPlayers, "arenaMaxPlayers must be honored, not hardcoded.");
            // R-CONTAINER's own runbook publishes -p 7777:7777/udp
            // (global-constraints.md) — not a coincidence, see MatchConfigLoader's doc.
            Assert.AreEqual(7777, expected.Port);

            // Not a tautology (brief §2.4): dev defaults must independently
            // survive the SAME validation any hand-authored config goes
            // through — this catches a future edit that makes the default
            // invalid without anyone noticing.
            var revalidated = MatchConfigLoader.Parse(ToJson(expected), ArenaCap);
            AssertAccepted(revalidated);

            // Witness: an explicitly-empty (not null-returning) environment
            // variable is ALSO "not set" (brief §2.4 — a container's
            // environment usually hands back "" rather than omitting the key).
            var resultEmptyStrings = MatchConfigLoader.Load(ArenaCap, getEnv: _ => "", readFile: _ => null);
            AssertAccepted(resultEmptyStrings);
            Assert.AreEqual(expected.MatchId, resultEmptyStrings.Config.MatchId);
        }

        [Test]
        public void MalformedJson_IsRefused_WithExitCodeTwo()
        {
            // Step 1a's four established inputs (brief §2.3, task-38-report.md
            // §2): "{" and "not json at all" throw System.ArgumentException
            // from JsonUtility.FromJsonOverwrite; ""/null are a silent no-op —
            // Parse's own empty-body guard (checked BEFORE JsonUtility ever
            // runs) catches those two before the two-sentinel machinery
            // starts.
            AssertRefused(Parse("{"));
            AssertRefused(Parse("not json at all"));
            AssertRefused(Parse(""));
            AssertRefused(Parse(null));
            // Witness (m3, fix-round 1): well-formed JSON is still accepted —
            // this test's four refusals are refusing the INPUT, not just
            // always refusing.
            AssertAccepted(Parse(Json()));
        }

        [Test]
        public void MissingSeed_IsRefused_NamingTheField()
        {
            AssertRefusedNaming(Parse(Json(seed: null)), "seed");
            // Witness: the same body with seed present parses.
            AssertAccepted(Parse(Json(seed: "7")));
        }

        [Test]
        public void ZeroSeed_IsAccepted()
        {
            // Р42: 0 is a legal seed — the world folds it via its existing Fold.
            var zero = Parse(Json(seed: "0"));
            AssertAccepted(zero);
            Assert.AreEqual(0L, zero.Config.Seed);

            // Witness: negative seeds are legal too (DeterminismTests.
            // NegativeSeed_IsDeterministicAndAlive — the world holds them).
            var negative = Parse(Json(seed: "-99"));
            AssertAccepted(negative);
            Assert.AreEqual(-99L, negative.Config.Seed);
        }

        [Test]
        public void MaxPlayers_ZeroAndNinetyNine_AreRefused()
        {
            // players: "[]" + countdown mode isolates the maxPlayers RANGE
            // check itself — a non-empty roster would also trip the separate
            // "players[] exceeds maxPlayers" check at maxPlayers=0, and that
            // refusal message ALSO contains the substring "maxPlayers",
            // letting a widened lower bound (M-4) hide behind it.
            AssertRefusedNaming(Parse(Json(maxPlayers: "0", players: "[]", startMode: "\"countdown\"")), "maxPlayers");
            AssertRefusedNaming(Parse(Json(maxPlayers: "99", players: "[]", startMode: "\"countdown\"")), "maxPlayers");
            // Witness: a value at the fixture's own cap is accepted.
            AssertAccepted(Parse(Json(maxPlayers: ArenaCap.ToString())));
        }

        [Test]
        public void JoinToken_IsCheckedOnlyWhenRosterIsNonEmpty()
        {
            // Non-empty roster: a mismatched joinToken for a known player is
            // rejected with BadToken (MatchRoster.TryJoin, brief §2.6).
            var namedConfig = new MatchConfig("m", 1, 2, 7000,
                new[] { new MatchPlayerEntry("alice", "secret") }, MatchStartMode.WaitForAll, 0);
            var namedRoster = new MatchRoster(in namedConfig);
            bool joined = namedRoster.TryJoin("alice", "WRONG", 0.0, out _, out var rejection);
            Assert.IsFalse(joined);
            Assert.AreEqual(JoinRejection.BadToken, rejection);
            // Witness: the correct token is accepted.
            bool joinedOk = namedRoster.TryJoin("alice", "secret", 0.0, out _, out var okRejection);
            Assert.IsTrue(joinedOk);
            Assert.AreEqual(JoinRejection.None, okRejection);

            // Empty roster (dev countdown, §2.6's last bullet): any non-empty
            // playerId is accepted — the token isn't compared against
            // anything at all, not even an arbitrary non-empty string.
            var emptyConfig = new MatchConfig("m", 1, 2, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            var emptyRoster = new MatchRoster(in emptyConfig);
            bool joinedEmptyRoster = emptyRoster.TryJoin("anyone", "any-arbitrary-token", 0.0,
                out _, out var emptyRejection);
            Assert.IsTrue(joinedEmptyRoster);
            Assert.AreEqual(JoinRejection.None, emptyRejection);
        }

        [Test]
        public void Roster_PlayerCountEqualsConnected()
        {
            // Р73: two of three configured players actually connect —
            // PlayerCount reads the CONNECTED count, not MaxPlayers.
            var config = new MatchConfig("m", 1, 3, 7000,
                new[] { new MatchPlayerEntry("a", ""), new MatchPlayerEntry("b", ""), new MatchPlayerEntry("c", "") },
                MatchStartMode.WaitForAll, 0);
            var roster = new MatchRoster(in config);
            roster.TryJoin("a", "", 0.0, out _, out _);
            roster.TryJoin("b", "", 0.0, out _, out _);
            roster.Start();
            Assert.AreEqual(2, roster.PlayerCount);
            Assert.AreNotEqual(config.MaxPlayers, roster.PlayerCount);
        }

        [Test]
        public void Roster_JoinAfterStart_Rejected()
        {
            var config = new MatchConfig("m", 1, 2, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            var roster = new MatchRoster(in config);
            roster.TryJoin("a", "", 0.0, out _, out _);
            roster.Start();
            bool joined = roster.TryJoin("late", "", 1.0, out int slot, out var rejection);
            Assert.IsFalse(joined);
            Assert.AreEqual(JoinRejection.MatchAlreadyStarted, rejection);
            Assert.AreEqual(-1, slot);
        }

        // ==================================================================
        // 10-24: coordinator's additions (brief §2.7).
        // ==================================================================

        [Test]
        public void EachRequiredField_WhenMissing_IsRefusedNamingIt()
        {
            AssertRefusedNaming(Parse(Json(matchId: null)), "matchId");
            AssertRefusedNaming(Parse(Json(maxPlayers: null)), "maxPlayers");
            AssertRefusedNaming(Parse(Json(port: null)), "port");
            AssertRefusedNaming(Parse(Json(startMode: null)), "startMode");
            // seed is pinned separately, by MissingSeed_IsRefused_NamingTheField.
            // Positive witness: the full body parses.
            AssertAccepted(Parse(Json()));
        }

        [Test]
        public void PathVariableWins_AndItsFailureIsFatal()
        {
            // Both variables set at once; the path is unreadable
            // (readFile -> null). Must refuse — must NOT fall through to
            // RING_MATCH_CONFIG_JSON, and must NOT fall back to dev defaults
            // (brief §2.4 scenario 2 — a typo'd path must not silently boot a
            // dev match on a production host).
            string bodyJson = Json();
            var result = MatchConfigLoader.Load(ArenaCap,
                getEnv: name => name == MatchConfigLoader.PathVariable ? "/bad/path.json"
                    : name == MatchConfigLoader.BodyVariable ? bodyJson : null,
                readFile: _ => null);
            AssertRefused(result);

            // Witness: a readable path wins over a simultaneously-set body.
            string pathJson = Json(matchId: "\"from-path\"");
            var winResult = MatchConfigLoader.Load(ArenaCap,
                getEnv: name => name == MatchConfigLoader.PathVariable ? "/good/path.json"
                    : name == MatchConfigLoader.BodyVariable ? bodyJson : null,
                readFile: path => path == "/good/path.json" ? pathJson : null);
            AssertAccepted(winResult);
            Assert.AreEqual("from-path", winResult.Config.MatchId);
        }

        [Test]
        public void BodyVariableAlone_IsUsed_WithNoPathVariableSet()
        {
            // Ф8 gate W-2: PathVariableWins_AndItsFailureIsFatal above always
            // sets BOTH variables; EmptyEnvironment_YieldsDevDefaults sets
            // NEITHER — no existing test in this file ever exercises
            // RING_MATCH_CONFIG_JSON alone. A mutant that deleted the whole
            // body branch (`Load`'s `if (!string.IsNullOrEmpty(body)) return
            // Parse(body, ...)`) would fall through to dev defaults instead —
            // and dev defaults ALSO answer `Ok == true`, so a test that only
            // asserted `AssertAccepted` here would still pass against that
            // mutant. The assertion on `MatchId` below is what actually
            // distinguishes "the body was read" from "dev defaults were used
            // instead" — a production deploy that hands the config over as a
            // body would otherwise silently boot a dev match.
            string bodyJson = Json(matchId: "\"from-body\"");
            var result = MatchConfigLoader.Load(ArenaCap,
                getEnv: name => name == MatchConfigLoader.BodyVariable ? bodyJson : null,
                // The path variable is unset, so PathVariable's branch must
                // never call readFile at all — throwing here turns a
                // regression that DID reach for the path into a loud
                // failure instead of a silent one.
                readFile: _ => throw new InvalidOperationException(
                    "readFile must not be called when only RING_MATCH_CONFIG_JSON is set"));
            AssertAccepted(result);
            Assert.AreEqual("from-body", result.Config.MatchId);

            // Witness (the discriminator named above, made explicit): dev
            // defaults alone do NOT carry this MatchId — proving the
            // assertion above is really about the body being read, not
            // merely about SOME accepted config coming back.
            var devResult = MatchConfigLoader.Load(ArenaCap, getEnv: _ => null, readFile: _ => null);
            AssertAccepted(devResult);
            Assert.AreNotEqual("from-body", devResult.Config.MatchId);
        }

        [Test]
        public void UnknownStartMode_IsRefused()
        {
            AssertRefusedNaming(Parse(Json(startMode: "\"WaitForAll\"")), "startMode");
            AssertRefusedNaming(Parse(Json(startMode: "\"waitforall\"")), "startMode");
            AssertRefusedNaming(Parse(Json(startMode: "\"\"")), "startMode");
            // Witness: both valid strings, exactly as spelled — strict
            // ordinal, case-sensitive comparison (brief §2.5).
            AssertAccepted(Parse(Json(startMode: "\"waitForAll\"")));
            AssertAccepted(Parse(Json(startMode: "\"countdown\"")));
        }

        [Test]
        public void CountdownSeconds_RequiredOnlyInCountdownMode()
        {
            var missing = Parse(Json(startMode: "\"countdown\"", countdownSeconds: null));
            AssertRefusedNaming(missing, "countdownSeconds");
            // The presence check's own message, distinct from the "must be >=
            // 0" wording a negative value produces — otherwise a missing
            // presence check (M-8) hides behind the sentinel (int.MinValue)
            // also being negative, and the range check alone refuses it for
            // the WRONG reason, masking the mutation.
            StringAssert.Contains("missing", missing.Error);
            // Witness: absent in waitForAll mode is legal — it's simply ignored.
            AssertAccepted(Parse(Json(startMode: "\"waitForAll\"", countdownSeconds: null)));
            // Negative is refused in countdown mode.
            AssertRefusedNaming(Parse(Json(startMode: "\"countdown\"", countdownSeconds: "-1")), "countdownSeconds");
            // Zero is legal (immediate start, dev mode) — no upper bound (brief §2.5).
            var zero = Parse(Json(startMode: "\"countdown\"", countdownSeconds: "0"));
            AssertAccepted(zero);
            Assert.AreEqual(0, zero.Config.CountdownSeconds);
        }

        [Test]
        public void WaitForAllWithEmptyRoster_IsRefused()
        {
            // The degenerate-handle guard (brief §2.5, lesson 125): waiting
            // for nobody must never reach MatchServer.StartMatch with a
            // zero-length connections array.
            var refused = Parse(Json(startMode: "\"waitForAll\"", players: "[]"));
            AssertRefused(refused);
            // m4 (fix-round 1) / N-1 (fix-round 2): pin the FIELD in the
            // message, not just the flag — a mutation that always refuses
            // would still pass a flag-only assertion. "waitForAll" alone
            // does NOT discriminate: it also appears in the UNRELATED
            // "startMode must be exactly ..." refusal (MatchConfigLoader.cs,
            // the startMode-validity check) — an off-target refusal from
            // that OTHER check would pass a "waitForAll"-only assertion too.
            // "non-empty players[]" appears in exactly one Refuse(...) call
            // in the whole file (grepped, fix-round 2 N-1) — the degenerate-
            // handle guard this test actually targets.
            StringAssert.Contains("non-empty players[]", refused.Error);
            // Witness: countdown with an empty roster is fine — it's the dev
            // default's own shape.
            AssertAccepted(Parse(Json(startMode: "\"countdown\"", players: "[]")));
        }

        [Test]
        public void Port_OutOfRange_IsRefused()
        {
            AssertRefusedNaming(Parse(Json(port: "0")), "port");
            AssertRefusedNaming(Parse(Json(port: "65536")), "port");
            // Witness.
            AssertAccepted(Parse(Json(port: "7777")));
        }

        [Test]
        public void DuplicatePlayerId_IsRefused()
        {
            string json = Json(maxPlayers: "2",
                players: "[{\"playerId\":\"a\",\"joinToken\":\"1\"},{\"playerId\":\"a\",\"joinToken\":\"2\"}]");
            var refused = Parse(json);
            AssertRefused(refused);
            // m4, fix-round 1: pin the field/reason in the message.
            StringAssert.Contains("duplicate", refused.Error);
            // Witness: distinct ids are fine.
            string ok = Json(maxPlayers: "2",
                players: "[{\"playerId\":\"a\",\"joinToken\":\"1\"},{\"playerId\":\"b\",\"joinToken\":\"2\"}]");
            AssertAccepted(Parse(ok));
        }

        [Test]
        public void RosterLargerThanMaxPlayers_IsRefused()
        {
            string json = Json(maxPlayers: "1",
                players: "[{\"playerId\":\"a\",\"joinToken\":\"1\"},{\"playerId\":\"b\",\"joinToken\":\"2\"}]");
            var refused = Parse(json);
            AssertRefused(refused);
            // m4, fix-round 1: pin the field/reason in the message.
            StringAssert.Contains("exceeding maxPlayers", refused.Error);
            // Witness: a roster within the cap passes.
            string ok = Json(maxPlayers: "2",
                players: "[{\"playerId\":\"a\",\"joinToken\":\"1\"},{\"playerId\":\"b\",\"joinToken\":\"2\"}]");
            AssertAccepted(Parse(ok));
        }

        [Test]
        public void PlayersArray_NullElement_IsRefusedNamingItsIndex()
        {
            // Ф8 gate, fixwave W-7: what this fixture actually pins is the
            // EMPTY-`playerId` refusal, and the phase gate's re-review is why
            // that is said plainly here. An earlier version of this comment
            // claimed the null-conditional in `Parse`'s per-entry loop guards
            // a literal `null` array element, which UnityEngine.JsonUtility
            // supposedly represents as a null C# reference — MEASURED FALSE by
            // this wave's own mutation run: the serializer substitutes a
            // default-initialised instance whose `playerId` is null, so the
            // refusal below comes from the emptiness check, not from `?.`.
            // The null-conditional stays as one token of insurance against a
            // future serializer that behaves otherwise; it is not the live
            // path, and no test can reach it today.
            string json = Json(maxPlayers: "2",
                players: "[null,{\"playerId\":\"p2\",\"joinToken\":\"t2\"}]");
            var refused = Parse(json);
            AssertRefused(refused);
            StringAssert.Contains("players[0]", refused.Error);

            // Witness: the identical body with the null element replaced by
            // a well-formed entry parses.
            string ok = Json(maxPlayers: "2",
                players: "[{\"playerId\":\"p1\",\"joinToken\":\"t1\"},{\"playerId\":\"p2\",\"joinToken\":\"t2\"}]");
            AssertAccepted(Parse(ok));
        }

        [Test]
        public void PlayersArray_MissingJoinToken_DefaultsToEmptyString()
        {
            // Ф8 gate W-7 (2/2): `joinToken` is read as `playersJson[i].
            // joinToken ?? string.Empty` — the OMITTED-key case (not merely
            // an explicit `""`) never had a test naming the RESULTING value,
            // and the brief calls this branch non-harmless: without the
            // substitution, an honest client that sent an empty token would
            // be compared against a `null` MatchPlayerEntry.JoinToken and
            // refused by MatchRoster.TryJoin's BadToken check instead of
            // accepted.
            string json = Json(maxPlayers: "1",
                players: "[{\"playerId\":\"p1\"}]"); // joinToken key entirely absent
            var accepted = Parse(json);
            AssertAccepted(accepted);
            Assert.AreEqual(1, accepted.Config.Players.Length);
            Assert.AreEqual("p1", accepted.Config.Players[0].PlayerId);
            Assert.AreEqual(string.Empty, accepted.Config.Players[0].JoinToken,
                "an omitted joinToken must default to the empty string, not null — MatchRoster."
                + "TryJoin compares it with ==, and a null default would refuse an honest client "
                + "that sends an empty token to match");
        }

        [Test]
        public void ArenaCapIsHandedIn_NotHardcoded()
        {
            // The one test allowed to use bare literal caps (brief §4) — its
            // entire point is proving Ring.Server takes the cap as a
            // PARAMETER (brief §2.2), which the ArenaCap fixture used
            // everywhere else in this file would hide.
            string json = Json(maxPlayers: "3", players: "[]", startMode: "\"countdown\"");
            Assert.IsFalse(MatchConfigLoader.Parse(json, 2).Ok,
                "maxPlayers=3 must be refused when the caller's cap is 2.");
            Assert.IsTrue(MatchConfigLoader.Parse(json, 3).Ok,
                "maxPlayers=3 must be accepted when the caller's cap is 3.");
        }

        [Test]
        public void UnknownJsonFields_AreIgnored()
        {
            // Documented UnityEngine.JsonUtility behavior, pinned here as a
            // forward compatibility contract with Э5's meta-generated configs
            // (brief §2.7 test 18).
            string json = "{\"matchId\":\"m1\",\"seed\":7,\"maxPlayers\":2,\"port\":7000," +
                "\"startMode\":\"waitForAll\",\"players\":[{\"playerId\":\"p1\",\"joinToken\":\"t1\"}]," +
                "\"futureMetaField\":\"unused-by-this-stage\",\"anotherOne\":123}";
            var result = Parse(json);
            AssertAccepted(result);
            Assert.AreEqual("m1", result.Config.MatchId);
        }

        [Test]
        public void Loader_NeverThrows_OnHostileInput()
        {
            // Lesson 115: a refusal is a value, never an exception.
            Assert.DoesNotThrow(() => AssertRefused(Parse(null)));
            Assert.DoesNotThrow(() => AssertRefused(Parse("")));
            Assert.DoesNotThrow(() => AssertRefused(Parse("{\"matchId\":")));   // truncated mid-object
            Assert.DoesNotThrow(() => AssertRefused(Parse("[1,2,3]")));        // array, not an object
        }

        [Test]
        public void Roster_CountdownStartsFromFirstJoin_NotFromConstruction()
        {
            var config = new MatchConfig("m", 1, 2, 7000, Array.Empty<MatchPlayerEntry>(),
                MatchStartMode.Countdown, 10);
            var roster = new MatchRoster(in config);

            // Constructed "at t=0" (implicitly — no clock is read in the
            // constructor at all), but nobody has joined yet. At t=50,
            // deliberately > 10s past construction, with no joins the
            // countdown must not have been running against construction
            // time.
            Assert.IsFalse(roster.ShouldStart(50.0));

            roster.TryJoin("a", "", 100.0, out _, out _);
            // 9s after the first join: not yet.
            Assert.IsFalse(roster.ShouldStart(109.0));
            // Witness: exactly 10s after the first join, it starts.
            Assert.IsTrue(roster.ShouldStart(110.0));
        }

        [Test]
        public void Roster_WaitForAll_StartsOnlyWhenEveryRosterPlayerJoined()
        {
            var config = new MatchConfig("m", 1, 3, 7000,
                new[] { new MatchPlayerEntry("a", ""), new MatchPlayerEntry("b", ""), new MatchPlayerEntry("c", "") },
                MatchStartMode.WaitForAll, 0);
            var roster = new MatchRoster(in config);
            roster.TryJoin("a", "", 0.0, out _, out _);
            roster.TryJoin("b", "", 0.0, out _, out _);
            Assert.IsFalse(roster.ShouldStart(0.0));

            // Witness: the third joins, now it starts.
            roster.TryJoin("c", "", 0.0, out _, out _);
            Assert.IsTrue(roster.ShouldStart(0.0));
        }

        [Test]
        public void Roster_SlotsAreAssignedInAcceptanceOrder_AndStayStable()
        {
            // players[] is authored a/b/c, but "c" connects first — slots
            // follow ACCEPTANCE order, not players[] array order (M-17).
            var config = new MatchConfig("m", 1, 3, 7000,
                new[] { new MatchPlayerEntry("a", ""), new MatchPlayerEntry("b", ""), new MatchPlayerEntry("c", "") },
                MatchStartMode.WaitForAll, 0);
            var roster = new MatchRoster(in config);

            roster.TryJoin("c", "", 0.0, out int slotC, out _);
            roster.TryJoin("a", "", 0.0, out int slotA, out _);
            roster.TryJoin("b", "", 0.0, out int slotB, out _);

            Assert.AreEqual(0, slotC);
            Assert.AreEqual(1, slotA);
            Assert.AreEqual(2, slotB);
            Assert.AreEqual("c", roster.PlayerIdAt(0));
            Assert.AreEqual("a", roster.PlayerIdAt(1));
            Assert.AreEqual("b", roster.PlayerIdAt(2));

            roster.Start();
            // Slots stay stable after Start() — re-reading gives the same mapping.
            Assert.AreEqual("c", roster.PlayerIdAt(0));
            Assert.AreEqual("a", roster.PlayerIdAt(1));
            Assert.AreEqual("b", roster.PlayerIdAt(2));
        }

        [Test]
        public void Roster_UnknownPlayerAndBadToken_AreRejectedWithTheirOwnReasons()
        {
            var config = new MatchConfig("m", 1, 3, 7000,
                new[] { new MatchPlayerEntry("a", "secret-a") }, MatchStartMode.WaitForAll, 0);
            var roster = new MatchRoster(in config);

            bool unknownJoined = roster.TryJoin("stranger", "whatever", 0.0, out _, out var unknownRejection);
            Assert.IsFalse(unknownJoined);
            Assert.AreEqual(JoinRejection.UnknownPlayer, unknownRejection);

            bool badTokenJoined = roster.TryJoin("a", "WRONG", 0.0, out _, out var badTokenRejection);
            Assert.IsFalse(badTokenJoined);
            Assert.AreEqual(JoinRejection.BadToken, badTokenRejection);

            // Witness: the correct player + token succeeds.
            bool okJoined = roster.TryJoin("a", "secret-a", 0.0, out _, out var okRejection);
            Assert.IsTrue(okJoined);
            Assert.AreEqual(JoinRejection.None, okRejection);
        }

        [Test]
        public void Roster_FullMatch_RejectsExtraJoin()
        {
            var config = new MatchConfig("m", 1, 2, 7000, Array.Empty<MatchPlayerEntry>(),
                MatchStartMode.Countdown, 5);
            var roster = new MatchRoster(in config);
            roster.TryJoin("a", "", 0.0, out _, out _);
            roster.TryJoin("b", "", 0.0, out _, out _);

            bool joined = roster.TryJoin("c", "", 0.0, out int slot, out var rejection);
            Assert.IsFalse(joined);
            Assert.AreEqual(JoinRejection.MatchFull, rejection);
            Assert.AreEqual(-1, slot);
        }

        // ==================================================================
        // 25-30: fix-round 1 additions (Opus review C-1/I-1/I-3/I-4/m5/m8).
        // ==================================================================

        [Test]
        public void TwoSentinelMechanism_DistinguishesPresenceFromSentinelValuedFields()
        {
            // I-4: a naive single-sentinel presence check (compare one parsed
            // instance against a fixed constant) passes every OTHER test in
            // this file, because none of them feeds a value that happens to
            // equal an internal sentinel. This test closes that gap directly
            // — mutation M-20 in task-38-report.md is exactly that naive
            // single-sentinel form, and it fails HERE.
            var seedAtSentinel = Parse(Json(seed: long.MinValue.ToString()));
            AssertAccepted(seedAtSentinel);
            Assert.AreEqual(long.MinValue, seedAtSentinel.Config.Seed);

            var maxPlayersAtSentinel = Parse(Json(
                maxPlayers: int.MinValue.ToString(), players: "[]", startMode: "\"countdown\""));
            AssertRefused(maxPlayersAtSentinel);
            // Refused by the RANGE check (a real value, just out of range),
            // never by "missing" — proves the field was correctly read as PRESENT.
            StringAssert.Contains("must be in", maxPlayersAtSentinel.Error);

            var portAtSentinel = Parse(Json(port: int.MaxValue.ToString()));
            AssertRefused(portAtSentinel);
            StringAssert.Contains("must be in", portAtSentinel.Error);

            // A matchId equal to the loader's own internal sentinel text is
            // still accepted — the mechanism never depends on user data
            // avoiding it (see MatchConfigLoader's own doc on the sentinel
            // constants, fix-round 1 C-1).
            var matchIdAtSentinel = Parse(Json(matchId: "\"__ring_sentinel_lo__\""));
            AssertAccepted(matchIdAtSentinel);
            Assert.AreEqual("__ring_sentinel_lo__", matchIdAtSentinel.Config.MatchId);
        }

        [Test]
        public void MatchId_Empty_IsRefused()
        {
            // m8, coordinator ruling: matchId feeds Task 41's startup log
            // line and the meta's own records, where "" is indistinguishable
            // from a generator bug.
            AssertRefusedNaming(Parse(Json(matchId: "\"\"")), "matchId");
            // Witness.
            AssertAccepted(Parse(Json(matchId: "\"m1\"")));
        }

        [Test]
        public void RosterConstructor_RejectsNullPlayers()
        {
            // I-1: default(MatchConfig) — what a refused MatchConfigLoadResult.
            // Config actually is — has Players == null. MatchRoster must
            // refuse it loudly, not NRE on first use two calls later.
            var badConfig = default(MatchConfig);
            Assert.Throws<ArgumentException>(() => new MatchRoster(in badConfig));
        }

        [Test]
        public void RosterConstructor_RejectsNonPositiveMaxPlayers()
        {
            // I-1: without this guard, `new string[MaxPlayers]` throws a bare,
            // unexplained OverflowException on a negative value.
            var badConfig = new MatchConfig("m", 1, -5, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            Assert.Throws<ArgumentOutOfRangeException>(() => new MatchRoster(in badConfig));
            var zeroConfig = new MatchConfig("m", 1, 0, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            Assert.Throws<ArgumentOutOfRangeException>(() => new MatchRoster(in zeroConfig));
            // Witness: MaxPlayers == 1 (the minimum legal value) constructs fine.
            var okConfig = new MatchConfig("m", 1, 1, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            Assert.DoesNotThrow(() => new MatchRoster(in okConfig));
        }

        [Test]
        public void RosterConstructor_RejectsWaitForAllWithEmptyRoster()
        {
            // Ф8 gate W-6: the SAME degenerate-handle rule
            // WaitForAllWithEmptyRoster_IsRefused pins at the LOADER — moved
            // into the core, which is where the decision actually belongs
            // (AGENT.md rule 4). A caller that builds a MatchConfig directly,
            // past the loader (every fixture in this file, and any future
            // production path that skips it), had no such gate before this:
            // ShouldStart's WaitForAll arm reads `_connectedCount >=
            // _config.Players.Length`, which an empty Players[] already
            // satisfies at `_connectedCount == 0`, so the FIRST connection
            // would start the match before ANY of the (zero) required
            // players had actually joined.
            var badConfig = new MatchConfig("m", 1, 2, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.WaitForAll, 0);
            Assert.Throws<ArgumentException>(() => new MatchRoster(in badConfig));

            // Witness: the identical shape with a non-empty roster constructs
            // fine — the guard is about the EMPTY roster, not about
            // WaitForAll itself (a mutant that refused every WaitForAll
            // config outright would still pass the negative assert above).
            var okConfig = new MatchConfig("m", 1, 2, 7000,
                new[] { new MatchPlayerEntry("a", "") }, MatchStartMode.WaitForAll, 0);
            Assert.DoesNotThrow(() => new MatchRoster(in okConfig));
        }

        [Test]
        public void Roster_InvalidPlayerId_IsRejectedBeforeDuplicateCheck()
        {
            // I-3: a null/empty playerId (Task 39's handshake handing in a
            // raw wire value) must not be silently accepted, nor
            // misdiagnosed as DuplicatePlayer on a second such attempt.
            var config = new MatchConfig("m", 1, 2, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            var roster = new MatchRoster(in config);

            bool joinedNull = roster.TryJoin(null, "", 0.0, out int slotNull, out var rejectionNull);
            Assert.IsFalse(joinedNull);
            Assert.AreEqual(JoinRejection.InvalidPlayerId, rejectionNull);
            Assert.AreEqual(-1, slotNull);

            // A second null attempt must ALSO read InvalidPlayerId, not
            // DuplicatePlayer — proves the guard runs before the duplicate
            // scan, not after a null slipped into it.
            bool joinedNullAgain = roster.TryJoin(null, "", 0.0, out _, out var rejectionAgain);
            Assert.IsFalse(joinedNullAgain);
            Assert.AreEqual(JoinRejection.InvalidPlayerId, rejectionAgain);

            bool joinedEmpty = roster.TryJoin("", "", 0.0, out _, out var rejectionEmpty);
            Assert.IsFalse(joinedEmpty);
            Assert.AreEqual(JoinRejection.InvalidPlayerId, rejectionEmpty);

            // Witness: a well-formed id is accepted.
            bool joinedOk = roster.TryJoin("a", "", 0.0, out _, out var rejectionOk);
            Assert.IsTrue(joinedOk);
            Assert.AreEqual(JoinRejection.None, rejectionOk);
        }

        [Test]
        public void Roster_Start_RequiresAtLeastOneConnection()
        {
            // m5: a zero-connection Start() must not silently freeze
            // PlayerCount at 0 — MatchServer.StartMatch throws two layers
            // down on an empty connections array; this class is where
            // "nobody ever joined" is still diagnosable as its own problem.
            var config = new MatchConfig("m", 1, 2, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            var roster = new MatchRoster(in config);
            Assert.Throws<InvalidOperationException>(() => roster.Start());

            // Witness: one connection is enough.
            var config2 = new MatchConfig("m", 1, 2, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            var roster2 = new MatchRoster(in config2);
            roster2.TryJoin("a", "", 0.0, out _, out _);
            Assert.DoesNotThrow(() => roster2.Start());
            Assert.AreEqual(1, roster2.PlayerCount);
        }

        // ==================================================================
        // Ф8 gate W-4: three class-doc-declared guards of the roster's own
        // core, none of them previously pinned by a test.
        // ==================================================================

        [Test]
        public void Roster_PlayerCount_ThrowsBeforeStart()
        {
            // W-4 (1/3): PlayerCount's own doc says it throws before Start()
            // rather than answering the frozen field's un-set sentinel (-1) —
            // without the guard, ServerBootstrap.StartMatch would build a
            // NEGATIVE-length connections/controllers array from that -1.
            var config = new MatchConfig("m", 1, 2, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            var roster = new MatchRoster(in config);

            Assert.Throws<InvalidOperationException>(() => { _ = roster.PlayerCount; });

            // Witness: once frozen by Start(), the same property answers.
            roster.TryJoin("a", "", 0.0, out _, out _);
            roster.Start();
            Assert.DoesNotThrow(() => { _ = roster.PlayerCount; });
            Assert.AreEqual(1, roster.PlayerCount);
        }

        [Test]
        public void Roster_PlayerIdAt_RejectsBothSidesOfTheRange()
        {
            // W-4 (2/3): PlayerIdAt's legal range is [0, ConnectedCount) —
            // both ends need their own assertion, since a guard closing only
            // one side (e.g. `slot < 0` alone, with no upper check) would
            // still pass a test that only tried the other side.
            var config = new MatchConfig("m", 1, 3, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 5);
            var roster = new MatchRoster(in config);
            roster.TryJoin("a", "", 0.0, out _, out _);
            roster.TryJoin("b", "", 0.0, out _, out _);

            Assert.Throws<ArgumentOutOfRangeException>(() => roster.PlayerIdAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => roster.PlayerIdAt(2));

            // Witness: both ends of the legal range answer correctly.
            Assert.AreEqual("a", roster.PlayerIdAt(0));
            Assert.AreEqual("b", roster.PlayerIdAt(1));
        }

        [Test]
        public void Roster_ShouldStart_ReturnsFalseAfterStart()
        {
            // W-4 (3/3): ShouldStart's own guard is `_started ||
            // _connectedCount == 0`. Roster_JoinAfterStart_Rejected already
            // pins TryJoin's OWN post-start refusal, but nothing in the suite
            // called ShouldStart itself after Start() — so the `_started`
            // half of this OR specifically could be deleted and every
            // existing test would still pass (CountdownSeconds == 0 below
            // means the countdown half of the OR, on its own, would still
            // read "start" at nowSeconds == 0.0, which is exactly what makes
            // this fixture discriminate the `_started` term rather than
            // merely restating the countdown boundary).
            var config = new MatchConfig("m", 1, 2, 7000,
                Array.Empty<MatchPlayerEntry>(), MatchStartMode.Countdown, 0);
            var roster = new MatchRoster(in config);
            roster.TryJoin("a", "", 0.0, out _, out _);
            Assert.IsTrue(roster.ShouldStart(0.0),
                "fixture premise: the match must be startable before Start() is ever called");

            roster.Start();
            Assert.IsFalse(roster.ShouldStart(0.0),
                "an already-started roster must answer ShouldStart false — the _started half of the OR");
        }
    }
}
