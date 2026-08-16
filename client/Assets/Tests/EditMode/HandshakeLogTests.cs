using System;
using System.Globalization;
using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Networking.Server;

namespace Ring.Simulation.Tests
{
    /// Stage 2 app-uxx + app-aor (task-uxx-red-brief.md §3.3): pins
    /// `HandshakeLog.RefusalLine`/`SanitizePlayerId` — the line a refused join
    /// leaves in a headless container's log.
    ///
    /// TEN OF THESE TESTS WERE WRITTEN BEFORE EITHER METHOD EXISTED, against a
    /// constant stub, and each of those carries a `RED WITNESS` note recording
    /// which of its own assertions that constant failed. The notes are kept in
    /// the past tense rather than deleted: a test that would have passed on a
    /// constant proves nothing about the implementation that replaced it
    /// (lesson 129), and the note is the evidence that this one would not have.
    ///
    /// THE TESTS WITHOUT SUCH A NOTE CAME LATER, from review: each closes a
    /// coverage hole found by MUTATING the finished implementation and watching
    /// the suite stay green (lesson 205). Their witness is that measurement,
    /// recorded in evidence-uxx.md, not a stub they never ran against.
    ///
    /// SEPARATE FIXTURE FROM `HandshakeTests` ON PURPOSE. That one covers the
    /// handshake's DECISION core (`Evaluate`/`FromJoinRejection`/
    /// `SlotsFitOnTheWire`); this one covers a different class in the same
    /// file. Keeping them apart is what made the RED run's verdict readable at
    /// a glance instead of a mixed fixture whose count has to be reasoned
    /// about.
    ///
    /// WHAT IS PINNED AND WHAT IS NOT. The field KEYS (`playerId=`,
    /// `clientId=`, `reason=`, `atSeconds=`) and the phrase `player refused`
    /// are pinned as literals, deliberately and by the same argument
    /// `HandshakeTests.HandshakeRefusal_ValuesAreStableOnTheWire` makes for the
    /// wire bytes: they are a contract with somebody outside this codebase — an
    /// operator's `grep`, and the control panel app-7ss will parse them — so a
    /// silent rename is exactly the change that must fail a test rather than a
    /// production search. The REASON NAMES are never spelled out; every one is
    /// derived from the enum member itself, so the tests cannot drift from
    /// `HandshakeRefusal` and a permuted mapping cannot hide behind a table
    /// this file also owns. The tails are pinned only by the short fragment
    /// that carries their meaning, not word for word.
    ///
    /// `None` IS EXERCISED TOO, though `MatchHandshake.Refuse` is never called
    /// with it: a formatter that is total over the enum today cannot grow a
    /// hole tomorrow, and the same totality argument is why
    /// `FromJoinRejection`'s own test walks every member.
    public class HandshakeLogTests
    {
        /// A realistic generated id — the exact shape a client launched without
        /// `-ring-player-id` produces (`ClientNetworkBootstrap`'s `dev-XXXXXXXX`)
        /// and therefore the id most likely to appear on a refused join.
        const string OrdinaryId = "dev-1a2b3c4d";

        /// Chosen so it collides with nothing else this fixture puts on a line:
        /// it is not a substring of the seconds below, and the second id used
        /// for the negative half of the clientId test is not a substring of it.
        const int OrdinaryClientId = 4242;

        /// A point on the server's own elapsed axis (zero is `ServerBootstrap`'s
        /// `Start`, not process spawn), with a fractional part that survives
        /// `F3` intact and turns into `12,500` under a comma-decimal culture —
        /// which is what the culture test looks for.
        const double OrdinarySeconds = 12.5;

        const char Placeholder = HandshakeLog.ControlCharPlaceholder;

        static string LineFor(HandshakeRefusal reason) =>
            HandshakeLog.RefusalLine(OrdinaryId, OrdinaryClientId, reason, OrdinarySeconds);

        /// One `key=value` field cannot contain whitespace without becoming two
        /// fields. Applied ONLY to values this class itself produces — the
        /// markers and the truncation marker — never to a sanitized id in
        /// general: a `playerId` containing a space is passed through unchanged
        /// by design (`SanitizePlayerId`'s own "what this does not do"
        /// paragraph), so a universal version of this check would be asserting
        /// something the sanitizer does not promise.
        static void AssertSingleField(string value, string what)
        {
            Assert.IsNotEmpty(value, $"{what} must be visible, not an empty gap in the line");
            for (int i = 0; i < value.Length; i++)
            {
                Assert.IsFalse(char.IsWhiteSpace(value[i]),
                    $"{what} lands inside one key=value field, so it cannot carry whitespace "
                    + $"(found at index {i} of \"{value}\")");
            }
        }

        // ==================================================================
        // RefusalLine — the line itself.
        // ==================================================================

        [Test]
        public void RefusalLine_CarriesEveryFieldTheRefusalHas()
        {
            // RED WITNESS: failed at its first assertion — the constant carried
            // no grep phrase, let alone the fields.
            //
            // `player refused` mirrors `ServerBootstrap`'s own "player
            // accepted" so that one `grep -E "player accepted|player refused"`
            // returns both halves of every join decision. Pinning it here is
            // the point of the task, not incidental: without the pair, the
            // accepted side is searchable and the refused side is not.
            string line = LineFor(HandshakeRefusal.SimConfigMismatch);

            StringAssert.Contains("player refused", line,
                "the refusal must be greppable beside ServerBootstrap's \"player accepted\"");
            StringAssert.Contains("playerId=" + OrdinaryId, line,
                "the playerId is the whole reason this line is being rebuilt — clientId alone is a "
                + "per-run transport number and cannot be mapped back to a configured player");
            StringAssert.Contains("clientId=" + OrdinaryClientId, line,
                "the clientId is what ties this line to the rest of the network log");
            StringAssert.Contains("reason=" + HandshakeRefusal.SimConfigMismatch, line,
                "the reason is named, not numbered — the byte is for the wire, not for a human");
            StringAssert.Contains("atSeconds=12.500", line,
                "the server's own elapsed axis, F3 — the one the accepted line already prints");
            StringAssert.Contains("MatchHandshake:", line,
                "the prefix names the subsystem that decides and prints, as today's line does");
        }

        [Test]
        public void RefusalLine_NamesTheReasonItWasGiven_ForEveryMember()
        {
            // RED WITNESS: failed on the first member — one constant cannot
            // carry ten different names.
            //
            // THE ASSERT KILLS A PERMUTATION, WHICH IS THE MUTATION THAT
            // MATTERS HERE (lesson 204). "The line for reason A differs from
            // the line for reason B" would survive any bijection — every
            // refusal would still get a unique-looking line while reporting the
            // wrong cause, which is worse than no reason at all because it
            // reads as an answer. Anchoring on `reason=` fixes each name to the
            // one field that is supposed to hold it: a formatter that printed
            // B's name for A fails immediately.
            //
            // THE SURROUNDING SPACES ARE LOAD-BEARING TOO. Without the trailing
            // one, a member whose name is a PREFIX of another (none today, but
            // nothing stops one being added) would let the shorter name's
            // assertion pass on the longer name's line. With them, the match is
            // the whole field.
            foreach (HandshakeRefusal reason in Enum.GetValues(typeof(HandshakeRefusal)))
            {
                string line = LineFor(reason);
                StringAssert.Contains(" reason=" + reason + " ", line,
                    $"HandshakeRefusal.{reason} must be named in its own line's reason field, "
                    + "not merely produce a line that differs from the others");
            }
        }

        [Test]
        public void RefusalLine_PrintsSecondsInTheInvariantCulture()
        {
            // The owner's own workstation is what this protects (the same
            // reason `DevLatencyOptionsTests` builds a comma-decimal culture by
            // hand rather than fetching one by name, so the test holds even
            // where the runtime ships no culture data): under a comma-decimal
            // culture a default format writes `atSeconds=12,500`, which reads
            // as two fields to anything parsing the line and as a different
            // number to a person.
            //
            // RED WITNESS: failed at the first assertion inside the try.
            CultureInfo previous = CultureInfo.CurrentCulture;
            var commaDecimal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaDecimal.NumberFormat.NumberDecimalSeparator = ",";
            commaDecimal.NumberFormat.NumberGroupSeparator = ".";

            try
            {
                CultureInfo.CurrentCulture = commaDecimal;

                string line = HandshakeLog.RefusalLine(OrdinaryId, OrdinaryClientId,
                    HandshakeRefusal.BadToken, OrdinarySeconds);

                StringAssert.Contains("atSeconds=12.500", line,
                    "the dot is the decimal point whatever the machine's locale says");
                StringAssert.DoesNotContain("12,5", line,
                    "a comma here is the machine's locale leaking into a log an operator and a "
                    + "parser both read");

                // A four-digit value catches the OTHER half of the same
                // mutation: the invariant culture has no group separator, so a
                // formatter that dropped `CultureInfo.InvariantCulture` would
                // write `1.234,568` here and `1,234.568` on an English machine —
                // both of them extra punctuation inside a numeric field.
                string wide = HandshakeLog.RefusalLine(OrdinaryId, OrdinaryClientId,
                    HandshakeRefusal.BadToken, 1234.56789);

                StringAssert.Contains("atSeconds=1234.568", wide,
                    "F3 rounds and the invariant culture groups nothing");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Test]
        public void RefusalLine_CarriesTheClientIdItWasGiven()
        {
            // RED WITNESS: failed at its first assertion against the stub.
            //
            // The two negative halves are what make this more than "a number
            // appears somewhere": they kill a formatter that hardcodes one
            // clientId, and one that prints some other argument in that field.
            string first = HandshakeLog.RefusalLine(OrdinaryId, OrdinaryClientId,
                HandshakeRefusal.MatchFull, OrdinarySeconds);
            string second = HandshakeLog.RefusalLine(OrdinaryId, 1717,
                HandshakeRefusal.MatchFull, OrdinarySeconds);

            StringAssert.Contains("clientId=" + OrdinaryClientId, first);
            StringAssert.Contains("clientId=1717", second);
            StringAssert.DoesNotContain("clientId=1717", first,
                "the field must follow the argument, not a constant");
            StringAssert.DoesNotContain("clientId=" + OrdinaryClientId, second,
                "the field must follow the argument, not a constant");

            // FishNet's own "no id yet" value (`NetworkConnection.ClientId` is
            // initialized to -1). A refusal on a connection that never got one
            // is exactly the case a log has to survive without inventing a
            // number.
            string unset = HandshakeLog.RefusalLine(OrdinaryId, -1,
                HandshakeRefusal.MatchFull, OrdinarySeconds);

            StringAssert.Contains("clientId=-1", unset);
        }

        [Test]
        public void RefusalLine_KeepsTheReviewedTail_AndBlamesTheRightSide()
        {
            // RED WITNESS: failed at its first assertion against the stub.
            //
            // The balance/version wording went through an earlier phase-gate review and is
            // preserved verbatim in substance (brief §3.1): every reason it
            // covers is one an HONEST client of an out-of-sync build produces
            // by itself, so the sentence deliberately avoids the vocabulary of
            // exploits. `UnrecognizedRejection` carries the OTHER tail because
            // for that one member the parity sentence would be false — it is a
            // server-side contract violation with no version drift in it at
            // all.
            string parity = LineFor(HandshakeRefusal.SimConfigMismatch);

            StringAssert.Contains("balance/version parity diagnostic", parity);
            StringAssert.Contains("not an anti-cheat check", parity);

            string internalBug = LineFor(HandshakeRefusal.UnrecognizedRejection);

            StringAssert.Contains("internal contract violation", internalBug);
            StringAssert.DoesNotContain("anti-cheat", internalBug,
                "the parity tail is false for an internal contract violation — one tail for every "
                + "reason would misdescribe this one");

            // THE TWO WITNESSES ABOVE ARE NOT ENOUGH, AND THIS WAS MEASURED
            // RATHER THAN REASONED (lesson 205). With only those, a formatter
            // that handed the internal-bug tail to `MatchFull` as well passed
            // the whole suite — 923/923 — because no assertion anywhere said
            // which tail the OTHER eight members get. That mutation is exactly
            // the defect the fixture claims to exclude: a refusal caused by an
            // ordinary full match would have been reported to the operator as a
            // bug on the server's own side, sending the search to the wrong
            // side of the wire.
            //
            // Stated as the rule itself — one member is the exception, every
            // other member is the norm — so a new `HandshakeRefusal` is covered
            // the day it is declared, without anyone remembering to come back.
            foreach (HandshakeRefusal reason in Enum.GetValues(typeof(HandshakeRefusal)))
            {
                string line = LineFor(reason);

                if (reason == HandshakeRefusal.UnrecognizedRejection)
                {
                    StringAssert.Contains("internal contract violation", line,
                        "the one member whose cause is on the server's own side");
                    StringAssert.DoesNotContain("balance/version parity", line,
                        "and it must not also claim the parity story");
                    continue;
                }

                StringAssert.Contains("balance/version parity diagnostic", line,
                    $"HandshakeRefusal.{reason} is something an honest client of an out-of-sync "
                    + "build produces, so it carries the reviewed parity wording");
                StringAssert.DoesNotContain("internal contract violation", line,
                    $"HandshakeRefusal.{reason} says nothing about a bug on the server side — "
                    + "blaming one would send the operator looking in the wrong process");
            }

            // The vocabulary rule `HandshakeRefusal`'s own doc states in as
            // many words ("do not describe this enum, or any refusal it
            // produces, with words like ... anti-tamper or security check"),
            // applied to every line this class can emit. It passed on the stub
            // by itself — it never was a RED assertion, and is here as a
            // standing guard for whoever edits the wording later.
            foreach (HandshakeRefusal reason in Enum.GetValues(typeof(HandshakeRefusal)))
            {
                string lower = LineFor(reason).ToLowerInvariant();
                StringAssert.DoesNotContain("exploit", lower,
                    $"HandshakeRefusal.{reason} is reachable from an unmodified client");
                StringAssert.DoesNotContain("illegitimate", lower,
                    $"HandshakeRefusal.{reason} is reachable from an unmodified client");
                StringAssert.DoesNotContain("security", lower,
                    $"HandshakeRefusal.{reason} is reachable from an unmodified client");
            }
        }

        [Test]
        public void RefusalLine_PrintsTheSanitizedPlayerId_NeverTheRawOne()
        {
            // The composition test: the sanitizer's own guarantees are useless
            // if the line does not actually go through it. Each case below is
            // the shape of a real defect, not a synthetic one.
            //
            // RED WITNESS: failed at its first assertion against the stub.

            // ONE FOREIGN STRING MUST NOT BECOME TWO LOG LINES. A client
            // chooses this string; without neutralization the second half of it
            // arrives in the log as a record of its own, and everything that
            // reads a log a line at a time is handed a fabricated entry.
            string forged = HandshakeLog.RefusalLine("evil\nFORGED", 7,
                HandshakeRefusal.UnknownPlayer, OrdinarySeconds);

            StringAssert.Contains("playerId=evil" + Placeholder + "FORGED", forged,
                "the break is neutralized in place — dropping it would render \"evil\\nFORGED\" as "
                + "the perfectly ordinary id \"evilFORGED\"");
            Assert.IsFalse(forged.Contains("\n"), "one refusal is one line");
            Assert.IsFalse(forged.Contains("\r"), "one refusal is one line");

            // AN ID LONG ENOUGH TO PUSH THE REST OF THE SCROLLBACK OUT.
            string longId = new string('z', HandshakeLog.MaxPlayerIdLength + 40);
            string truncated = HandshakeLog.RefusalLine(longId, 7,
                HandshakeRefusal.UnknownPlayer, OrdinarySeconds);

            StringAssert.DoesNotContain(longId, truncated,
                "the raw id must not reach the line");
            StringAssert.Contains(
                "playerId=" + new string('z', HandshakeLog.MaxPlayerIdLength)
                + HandshakeLog.TruncationMarker, truncated,
                "and what does reach it must be visibly incomplete");

            // A NULL ID IS THE ONE `InvalidPlayerId` IS MADE OF. The line has to
            // survive it without an exception and has to say what was there.
            string missing = HandshakeLog.RefusalLine(null, 7,
                HandshakeRefusal.InvalidPlayerId, OrdinarySeconds);

            StringAssert.Contains("playerId=" + HandshakeLog.NullPlayerIdMarker, missing,
                "an absent id must be visibly absent, not an empty gap that reads as a formatting "
                + "bug");
        }

        // ==================================================================
        // SanitizePlayerId — covered directly, not only through the line
        // (brief §3.2).
        // ==================================================================

        [Test]
        public void SanitizePlayerId_LeavesAnOrdinaryIdExactlyAsItIs()
        {
            // RED WITNESS: failed at its first assertion against the stub.
            //
            // The case the whole feature must not break: if the ordinary id
            // came out altered, every refusal line would misreport the one
            // field it exists to carry, and nothing in the log would say so.
            Assert.AreEqual(OrdinaryId, HandshakeLog.SanitizePlayerId(OrdinaryId),
                "a generated dev id passes through untouched");
            Assert.AreEqual("p1", HandshakeLog.SanitizePlayerId("p1"),
                "so does a short configured one");
            // Written as escapes so the ASSERTION's expectation is unambiguous
            // at a glance and cannot be altered by an editor normalizing what
            // it cannot render (the file's own prose does carry non-ASCII —
            // em-dashes — so "the file is ASCII" would not be true).
            // The characters are an accented Latin letter and a CJK ideograph,
            // neither of which is a control character.
            Assert.AreEqual("id-\u00e9\u4e2d", HandshakeLog.SanitizePlayerId("id-\u00e9\u4e2d"),
                "a non-ASCII id is not a control character and is not this class's business");
        }

        [Test]
        public void SanitizePlayerId_NeutralizesEveryControlCharacter()
        {
            // RED WITNESS: failed at its first assertion against the stub.
            //
            // EXACT EQUALITY, NOT "CONTAINS NO NEWLINE". The weaker assertion
            // would be green on the stub, on a sanitizer that DELETES the
            // characters (which renders "a\nb" as the ordinary id "ab" and
            // hides that anything happened), and on one that escapes them into
            // two characters (which makes the length bound below depend on the
            // input's contents). Equality admits exactly one behavior:
            // substitution, one character for one character.
            Assert.AreEqual("a" + Placeholder + "b" + Placeholder + "c",
                HandshakeLog.SanitizePlayerId("a\nb\rc"),
                "the two characters that split a log line");

            Assert.AreEqual(new string(Placeholder, 7),
                HandshakeLog.SanitizePlayerId("\n\r\t\0\u0007\u001b\u007f"),
                "and the rest of the C0 range with them, DEL included");

            Assert.AreEqual("x" + Placeholder + "y",
                HandshakeLog.SanitizePlayerId("x\u0085y"),
                "the C1 range is `char.IsControl` too — NEL is a line break to some readers");

            // Positive witness: a sanitizer that replaced EVERYTHING would pass
            // all three above.
            Assert.AreEqual(OrdinaryId, HandshakeLog.SanitizePlayerId(OrdinaryId),
                "witness: an ordinary id has nothing to neutralize");
        }

        [Test]
        public void SanitizePlayerId_TruncatesVisiblyAndOnlyPastTheBound()
        {
            // RED WITNESS: failed at its first assertion against the stub.
            //
            // The bound is read off `HandshakeLog.MaxPlayerIdLength` rather
            // than written here as 64: the number has one home (brief §3.2),
            // and a test carrying its own copy would be the second one.
            int max = HandshakeLog.MaxPlayerIdLength;
            string atBound = new string('a', max);

            // The marker is a value this class produces, so the one-field
            // invariant applies to it as much as to the null/empty markers —
            // a marker carrying a space would split the `playerId=` field in
            // two for anything parsing the line.
            AssertSingleField(HandshakeLog.TruncationMarker, "the truncation marker");

            // The off-by-one, from both sides. Without the first of these,
            // "always truncate" passes; without the second, "never truncate"
            // does.
            Assert.AreEqual(atBound, HandshakeLog.SanitizePlayerId(atBound),
                "exactly at the bound is a whole id, not a cut one");
            Assert.AreEqual(atBound + HandshakeLog.TruncationMarker,
                HandshakeLog.SanitizePlayerId(new string('a', max + 1)),
                "one character past it is cut, and the cut is visible — an operator cannot "
                + "otherwise tell a truncated id from a real one");
            Assert.AreEqual(atBound + HandshakeLog.TruncationMarker,
                HandshakeLog.SanitizePlayerId(new string('a', max * 4)),
                "and far past it the result is the same length, not a longer one");

            // A CUT MUST NOT SPLIT A SURROGATE PAIR. The pair below straddles
            // the bound: cutting at `max` characters would leave its leading
            // half alone, which is not a control character, survives
            // neutralization untouched, and reaches the log encoder as an
            // unpaired UTF-16 unit no encoder can represent — a defect the
            // truncation would have manufactured itself.
            string pairAtCut = new string('a', max - 1) + "\U0001F600";
            Assert.AreEqual(new string('a', max - 1) + HandshakeLog.TruncationMarker,
                HandshakeLog.SanitizePlayerId(pairAtCut),
                "a pair that does not fit is dropped whole, never halved");

            // Witness for the case above: a pair that DOES fit is kept, so
            // "drop every surrogate" is not an answer either.
            string pairInside = new string('a', max - 2) + "\U0001F600";
            Assert.AreEqual(pairInside, HandshakeLog.SanitizePlayerId(pairInside),
                "a pair that fits is part of the id like anything else");
        }

        [Test]
        public void SanitizePlayerId_NeutralizesInsideTheKeptRangeOfATruncatedId()
        {
            // THE TWO RULES HAVE TO HOLD AT THE SAME TIME, AND UNTIL THIS TEST
            // NOTHING SAID SO. Every long input in the fixture was made of
            // ordinary characters and every control character sat in a short
            // one, so an implementation that neutralized only the strings it
            // did NOT have to cut passed the whole suite — measured, not
            // supposed (lesson 205; the same shape of hole as the tail one
            // above).
            //
            // What that mutation costs is the whole point of the class: an id
            // long enough to be truncated is exactly where an attacker-chosen
            // `\n` is cheapest to hide, and it would reach the log as a real
            // line break — one refused join rendered as two log records.
            int max = HandshakeLog.MaxPlayerIdLength;
            string longWithBreak = "a\nb" + new string('c', max);

            Assert.AreEqual(
                "a" + Placeholder + "b" + new string('c', max - 3)
                + HandshakeLog.TruncationMarker,
                HandshakeLog.SanitizePlayerId(longWithBreak),
                "a truncated id is neutralized inside the part that survives, not handed "
                + "through raw because it happened to also need cutting");

            // And the control character sitting at the very last kept position,
            // which a fencepost error in the neutralization loop would skip.
            string breakAtBound = new string('c', max - 1) + "\n" + new string('d', 20);

            Assert.AreEqual(new string('c', max - 1) + Placeholder + HandshakeLog.TruncationMarker,
                HandshakeLog.SanitizePlayerId(breakAtBound),
                "the last character the cut keeps is inside the neutralized range too");

            // ALL THREE RULES AT ONCE, AND THIS ONE IS NOT MERELY A WRONG
            // ANSWER IF IT BREAKS. The surrogate pair straddling the bound
            // makes the kept range one SHORTER than the bound, while the
            // control character inside forces the copy that the neutralization
            // works on. An implementation that sizes that copy by the bound
            // instead of by the kept length writes 64 characters into a
            // 63-character buffer — measured: it passes all 924 other tests and
            // throws `ArgumentException` on this input alone.
            //
            // WHERE THAT THROW LANDS IS WHY THIS ASSERTION EXISTS: the caller
            // is `Refuse`, inside a FishNet broadcast handler. In a release
            // server build the handler's exception is caught by FishNet and
            // turned into `Kick(..., MalformedData, ...)` — so a client whose
            // id happens to end in an emoji would be blamed for corrupt data
            // by the very code that exists to report honestly WHY it was
            // turned away. Refusal is a value here, never an exception.
            string pairAtBoundWithBreak = new string('a', max - 5) + "\n" + new string('a', 3)
                + "\U0001F600";

            Assert.AreEqual(
                new string('a', max - 5) + Placeholder + new string('a', 3)
                + HandshakeLog.TruncationMarker,
                HandshakeLog.SanitizePlayerId(pairAtBoundWithBreak),
                "a shortened kept range and a neutralized character have to hold together");
        }

        [Test]
        public void SanitizePlayerId_MarksNullAndEmptyApart()
        {
            // RED WITNESS: failed at its first assertion against the stub.
            //
            // `MatchRoster.TryJoin` answers `string.IsNullOrEmpty` with ONE
            // `JoinRejection.InvalidPlayerId`, so the reason field of the line
            // is identical for both and this rendering is the only place the
            // difference can survive. It is a real difference to whoever debugs
            // the client: a null field was never assigned, an empty one was
            // assigned something empty.
            Assert.AreEqual(HandshakeLog.NullPlayerIdMarker,
                HandshakeLog.SanitizePlayerId(null));
            Assert.AreEqual(HandshakeLog.EmptyPlayerIdMarker,
                HandshakeLog.SanitizePlayerId(string.Empty));

            // The mutation this kills is the tempting one: a single marker for
            // "no id". It is stated against the FUNCTION's two answers rather
            // than against the two constants, because comparing the constants
            // is a fact about this file and not about the code under test.
            Assert.AreNotEqual(HandshakeLog.SanitizePlayerId(null),
                HandshakeLog.SanitizePlayerId(string.Empty),
                "\"the field was never written\" and \"the field was written empty\" are two "
                + "observations, and the refusal reason cannot tell them apart");

            AssertSingleField(HandshakeLog.SanitizePlayerId(null), "the null marker");
            AssertSingleField(HandshakeLog.SanitizePlayerId(string.Empty), "the empty marker");

            // Witness: a real id is not rendered as either marker — a sanitizer
            // that answered "absent" for everything would pass the four
            // assertions above.
            Assert.AreNotEqual(HandshakeLog.NullPlayerIdMarker,
                HandshakeLog.SanitizePlayerId(OrdinaryId));
            Assert.AreNotEqual(HandshakeLog.EmptyPlayerIdMarker,
                HandshakeLog.SanitizePlayerId(OrdinaryId));
        }
    }
}
